package com.esousa.beam.session

import android.content.Context
import android.net.ConnectivityManager
import android.net.Network
import android.net.wifi.WifiManager
import com.esousa.beam.clipboard.ClipboardRepository
import com.esousa.beam.clipboard.ImageFingerprint
import com.esousa.beam.discovery.DiscoveredDesktop
import com.esousa.beam.discovery.UdpDiscovery
import com.esousa.beam.net.BlobTransferCompleted
import com.esousa.beam.net.ClipBridgeClient
import com.esousa.beam.net.ConnectionState
import com.esousa.beam.protocol.ClipboardImagePayload
import com.esousa.beam.protocol.ClipboardTextPayload
import com.esousa.beam.protocol.Envelope
import com.esousa.beam.protocol.MessageType
import com.esousa.beam.protocol.Protocol
import com.esousa.beam.protocol.ScreenshotPayload
import com.esousa.beam.security.TrustRecord
import com.esousa.beam.security.TrustStore
import com.esousa.beam.service.ClipBridgeForegroundService
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharedFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asSharedFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.decodeFromJsonElement
import kotlinx.serialization.json.encodeToJsonElement
import kotlinx.serialization.json.jsonObject
import java.util.UUID
import java.util.concurrent.ConcurrentHashMap

data class SessionActivity(
    val statusLabel: String = "Desconectado",
    val lastActivity: String? = null,
    val isSecure: Boolean = false,
    /** Há vínculo válido: o app reconecta sozinho, sem pedir código. */
    val isResuming: Boolean = false,
    /** Instante em que a confiança expira, se houver vínculo. */
    val trustExpiresAt: Long? = null,
)

data class ReceivedImage(
    val pngBytes: ByteArray,
    val width: Int,
    val height: Int,
    val label: String,
)

/**
 * Sessão de longa duração (escopo Application) — mantém rede e clipboard sync
 * enquanto o serviço em foreground estiver ativo.
 */
class ClipBridgeSession(context: Context) {

    private val appContext = context.applicationContext
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main.immediate)
    private val json = Json { ignoreUnknownKeys = true }

    val discovery = UdpDiscovery(appContext)
    val client = ClipBridgeClient(json)
    private val clipboard = ClipboardRepository(appContext)

    // Anti-eco por conteúdo: texto normalizado (fins de linha) e hash perceptual
    // de imagem (sobrevive à recodificação PNG). O conteúdo recebido é registrado
    // antes de ser escrito no clipboard, então seu evento local não volta ao PC.
    @Volatile private var lastSyncedText: String? = null
    @Volatile private var lastSyncedImageHash: Long? = null
    private val completedBlobs = ConcurrentHashMap<String, ByteArray>()
    private val pendingClipboardImages = ConcurrentHashMap<String, ClipboardImagePayload>()
    private val pendingScreenshots = ConcurrentHashMap<String, ScreenshotPayload>()

    private var heartbeatJob: Job? = null
    private var lastDesktop: DiscoveredDesktop? = null
    // De onde veio o endereço da última tentativa: sem isso, um "falha ao conectar
    // na porta X" não diz se X foi descoberto, digitado ou lido do vínculo salvo.
    private var lastTargetSource: String? = null
    private var wifiLock: WifiManager.WifiLock? = null

    private val trustStore = TrustStore(appContext)
    private var trust: TrustRecord? = null
    private var reconnectJob: Job? = null
    private var reconnectAttempt = 0

    private val _discovered = MutableStateFlow<List<DiscoveredDesktop>>(emptyList())
    val discovered: StateFlow<List<DiscoveredDesktop>> = _discovered.asStateFlow()

    private val _activity = MutableStateFlow(SessionActivity())
    val activity: StateFlow<SessionActivity> = _activity.asStateFlow()

    private val _latestImage = MutableStateFlow<ReceivedImage?>(null)
    val latestImage: StateFlow<ReceivedImage?> = _latestImage.asStateFlow()

    private val _events = MutableSharedFlow<String>(extraBufferCapacity = 16)
    val events: SharedFlow<String> = _events.asSharedFlow()

    init {
        scope.launch { discovery.desktops.collect { _discovered.value = it } }
        scope.launch { observeConnection() }
        scope.launch { observeIncoming() }
        scope.launch { observeBlobs() }
        scope.launch { observeTrust() }
        observeClipboard()

        trust = trustStore.load()
        _activity.value = _activity.value.copy(
            isResuming = trust != null,
            trustExpiresAt = trust?.expiresAt,
        )
        // A descoberta segue ligada mesmo com vínculo: o IP do desktop pode ter
        // mudado (DHCP) desde a última conexão.
        discovery.start()
        watchNetworkChanges()
        if (trust != null) {
            updateActivity(statusLabel = "Reconectando ao desktop…")
            reconnectNow()
        }
    }

    fun startDiscovery() = discovery.restart()

    /**
     * Inicia o pareamento. [manualAddress] permite informar `ip` ou `ip:porta`
     * do desktop quando a descoberta automática não passa — redes Wi-Fi com
     * isolamento de clientes ou broadcast filtrado bloqueiam apenas o UDP de
     * descoberta; a conexão WebSocket em si continua funcionando.
     */
    fun confirmPairing(code: String, manualAddress: String? = null) {
        // Pareamento explícito tem precedência sobre a retomada em andamento.
        cancelReconnect()
        val manual = parseManualAddress(manualAddress)
        val desktop = manual ?: _discovered.value.firstOrNull()
        if (desktop == null) {
            updateActivity(statusLabel = "Nenhum desktop encontrado — informe o IP mostrado no PC")
            return
        }
        lastDesktop = desktop
        lastTargetSource = if (manual != null) "endereço digitado" else "descoberto na rede"
        client.pair(desktop.host, desktop.port, code)
    }

    /**
     * Erro de rede com o alvo e sua procedência. "Falhou na porta 878" sozinho não
     * revela se o endereço veio da descoberta, foi digitado ou saiu do vínculo salvo.
     */
    private fun describeFailure(message: String): String {
        val target = lastDesktop ?: return "Erro: $message"
        val source = lastTargetSource?.let { " ($it)" } ?: ""
        val hint = if (target.port != Protocol.DEFAULT_PORT) {
            " — atenção: a porta padrão do Beam é ${Protocol.DEFAULT_PORT}."
        } else {
            ""
        }
        return "Falha ao conectar em ${target.host}:${target.port}$source: $message$hint"
    }

    /** Aceita `192.168.0.10` ou `192.168.0.10:8787`; devolve null se em branco ou inválido. */
    private fun parseManualAddress(address: String?): DiscoveredDesktop? {
        val trimmed = address?.trim().orEmpty()
        if (trimmed.isEmpty()) return null

        val host = trimmed.substringBefore(':').trim()
        if (host.isEmpty()) return null
        val port = trimmed.substringAfter(':', "").toIntOrNull() ?: Protocol.DEFAULT_PORT
        if (port !in 1..65535) return null
        return DiscoveredDesktop(host, host, port)
    }

    fun syncCopiedTextFromInputMethod() {
        scope.launch {
            if (!client.isSecure) return@launch

            val text = clipboard.readText()?.takeIf { it.isNotEmpty() } ?: return@launch
            val normalized = text.normalizeEol()
            if (normalized == lastSyncedText) return@launch

            if (client.send(envelope(MessageType.CLIPBOARD_TEXT, ClipboardTextPayload(text)))) {
                lastSyncedText = normalized
                updateActivity(lastActivity = "Enviado ao PC: ${text.preview()}")
            }
        }
    }

    private suspend fun observeConnection() {
        client.connectionState.collect { state ->
            val (label, secure) = when (state) {
                is ConnectionState.Connected -> "Conectado a ${state.host}:${state.port}" to false
                is ConnectionState.Pairing -> "Pareando com ${state.host}:${state.port}" to false
                is ConnectionState.Resuming -> "Retomando sessão em ${state.host}:${state.port}" to false
                is ConnectionState.Secure -> "Conexão segura com ${state.host}:${state.port}" to true
                is ConnectionState.TrustRejected -> {
                    releaseWifiLock()
                    forgetTrust()
                    _events.tryEmit(state.message)
                    "Vínculo expirado — pareie novamente com o código" to false
                }
                is ConnectionState.Error -> {
                    releaseWifiLock()
                    _events.tryEmit(describeFailure(state.message))
                    discovery.start()
                    scheduleReconnect()
                    (if (trust != null) "Reconectando…" else describeFailure(state.message)) to false
                }
                ConnectionState.Disconnected -> {
                    stopHeartbeat()
                    releaseWifiLock()
                    ClipBridgeForegroundService.stop(appContext)
                    discovery.start()
                    scheduleReconnect()
                    (if (trust != null) "Reconectando…" else "Desconectado — procurando desktop…") to false
                }
            }
            updateActivity(statusLabel = label, isSecure = secure)
            if (secure) {
                // Com a sessão de pé, as sondas de broadcast só gastariam bateria.
                discovery.stop()
                acquireWifiLock()
                cancelReconnect()
                ClipBridgeForegroundService.start(appContext, label)
                startHeartbeat()
            }
        }
    }

    /** Persiste o vínculo a cada sessão segura — é o que renova a janela de 72h. */
    private suspend fun observeTrust() {
        client.trust.collect { material ->
            trustStore.save(material.host, material.port, material.deviceId, material.resumeKey)
            trust = trustStore.load()
            _activity.value = _activity.value.copy(
                isResuming = true,
                trustExpiresAt = trust?.expiresAt,
            )
        }
    }

    /**
     * Retoma a sessão enquanto houver vínculo válido. Cada tentativa usa primeiro
     * o desktop recém-descoberto (o IP pode ter mudado) e cai para o endereço
     * salvo quando a descoberta ainda não achou ninguém.
     */
    private fun scheduleReconnect() {
        val record = trust ?: return
        if (!record.isValid) {
            forgetTrust()
            updateActivity(statusLabel = "Vínculo expirado — pareie novamente com o código")
            return
        }

        if (reconnectJob?.isActive == true) return
        reconnectJob = scope.launch {
            while (isActive && !client.isSecure) {
                val current = trust ?: return@launch
                if (!current.isValid) {
                    forgetTrust()
                    updateActivity(statusLabel = "Vínculo expirado — pareie novamente com o código")
                    return@launch
                }

                val target = _discovered.value.firstOrNull()
                if (target != null) {
                    trustStore.updateEndpoint(target.host, target.port)
                    lastDesktop = target
                    lastTargetSource = "descoberto na rede"
                    client.resume(current, target.host, target.port)
                } else {
                    lastDesktop = DiscoveredDesktop(current.host, current.host, current.port)
                    lastTargetSource = "vínculo salvo"
                    client.resume(current)
                }

                delay(backoffDelayMs(reconnectAttempt++))
            }
        }
    }

    /** Nova tentativa imediata, zerando o backoff (ex.: o Wi-Fi acabou de voltar). */
    private fun reconnectNow() {
        cancelReconnect()
        scheduleReconnect()
    }

    private fun cancelReconnect() {
        reconnectJob?.cancel()
        reconnectJob = null
        reconnectAttempt = 0
    }

    /** 2s, 4s, 8s, 16s e daí em diante a cada 30s. */
    private fun backoffDelayMs(attempt: Int): Long =
        minOf(FIRST_RETRY_MS shl attempt.coerceAtMost(4), MAX_RETRY_MS)

    private fun forgetTrust() {
        trust = null
        trustStore.clear()
        cancelReconnect()
        discovery.start()
        _activity.value = _activity.value.copy(isResuming = false, trustExpiresAt = null)
    }

    /**
     * O Android não avisa o WebSocket quando o Wi-Fi cai e volta; sem este gatilho
     * a retomada só aconteceria no próximo passo do backoff.
     */
    private fun watchNetworkChanges() {
        val manager = appContext.getSystemService(Context.CONNECTIVITY_SERVICE) as? ConnectivityManager ?: return
        runCatching {
            manager.registerDefaultNetworkCallback(object : ConnectivityManager.NetworkCallback() {
                override fun onAvailable(network: Network) {
                    scope.launch {
                        discovery.restart()
                        if (!client.isSecure && trust != null) {
                            updateActivity(statusLabel = "Rede disponível — reconectando…")
                            reconnectNow()
                        }
                    }
                }
            })
        }
    }

    /**
     * Sem o WifiLock o Android desliga o rádio Wi-Fi em suspensão e derruba o
     * WebSocket poucos minutos depois de a tela apagar.
     */
    private fun acquireWifiLock() {
        if (wifiLock?.isHeld == true) return
        val wifi = appContext.getSystemService(Context.WIFI_SERVICE) as? WifiManager ?: return
        wifiLock = runCatching {
            wifi.createWifiLock(WifiManager.WIFI_MODE_FULL_HIGH_PERF, "clipbridge-session").apply {
                setReferenceCounted(false)
                acquire()
            }
        }.getOrNull()
    }

    private fun releaseWifiLock() {
        wifiLock?.let { lock -> runCatching { if (lock.isHeld) lock.release() } }
        wifiLock = null
    }

    private suspend fun observeIncoming() {
        client.incoming.collect { envelope ->
            when (envelope.type) {
                MessageType.CLIPBOARD_TEXT -> handleIncomingText(envelope)
                MessageType.CLIPBOARD_IMAGE -> handleIncomingImageMetadata(envelope)
                MessageType.SCREENSHOT -> handleIncomingScreenshotMetadata(envelope)
            }
        }
    }

    private suspend fun observeBlobs() {
        client.blobs.collect { completed ->
            val key = completed.blobId.lowercase()
            val pendingImage = pendingClipboardImages.remove(key)
            val pendingShot = pendingScreenshots.remove(key)
            when {
                pendingImage != null ->
                    applyIncomingImage(completed.data, pendingImage.width, pendingImage.height, "Imagem do PC")
                pendingShot != null ->
                    applyIncomingImage(completed.data, pendingShot.width, pendingShot.height, "Screenshot ${pendingShot.width}×${pendingShot.height}")
                // Metadados ainda não chegaram: guarda o blob até o envelope correspondente.
                else -> completedBlobs[key] = completed.data
            }
        }
    }

    private fun handleIncomingText(envelope: Envelope) {
        val payload = runCatching {
            json.decodeFromJsonElement(ClipboardTextPayload.serializer(), requireNotNull(envelope.payload))
        }.getOrNull() ?: return

        lastSyncedText = payload.text.normalizeEol()
        clipboard.writeText(payload.text.normalizeEol())
        updateActivity(lastActivity = "Recebido do PC: ${payload.text.preview()}")
    }

    private fun handleIncomingImageMetadata(envelope: Envelope) {
        val payload = runCatching {
            json.decodeFromJsonElement(ClipboardImagePayload.serializer(), requireNotNull(envelope.payload))
        }.getOrNull() ?: return

        val blob = completedBlobs.remove(payload.blobId.lowercase())
        if (blob != null) {
            applyIncomingImage(blob, payload.width, payload.height, "Imagem do PC")
        } else {
            pendingClipboardImages[payload.blobId.lowercase()] = payload
        }
    }

    private fun handleIncomingScreenshotMetadata(envelope: Envelope) {
        val payload = runCatching {
            json.decodeFromJsonElement(ScreenshotPayload.serializer(), requireNotNull(envelope.payload))
        }.getOrNull() ?: return

        val blob = completedBlobs.remove(payload.blobId.lowercase())
        if (blob != null) {
            applyIncomingImage(blob, payload.width, payload.height, "Screenshot ${payload.width}×${payload.height}")
        } else {
            pendingScreenshots[payload.blobId.lowercase()] = payload
        }
    }

    private fun applyIncomingImage(pngBytes: ByteArray, width: Int, height: Int, label: String) {
        ImageFingerprint.compute(pngBytes)?.let { lastSyncedImageHash = it }
        clipboard.writeImage(pngBytes, label)
        _latestImage.value = ReceivedImage(pngBytes, width, height, label)
        updateActivity(lastActivity = "$label recebido")
    }

    private fun observeClipboard() {
        clipboard.setOnPrimaryClipChangedListener {
            if (!client.isSecure) return@setOnPrimaryClipChangedListener

            clipboard.readImagePng()?.let { image ->
                // Mesma imagem já sincronizada (hash perceptual — sobrevive
                // à recodificação do PNG). Nunca reenvia.
                val hash = ImageFingerprint.compute(image.pngBytes)
                val last = lastSyncedImageHash
                if (hash != null && last != null && ImageFingerprint.isSameImage(hash, last)) {
                    return@setOnPrimaryClipChangedListener
                }
                if (sendImage(image.pngBytes, image.width, image.height, MessageType.CLIPBOARD_IMAGE) { blobId, w, h ->
                    ClipboardImagePayload(blobId, "image/png", w, h)
                }) {
                    hash?.let { lastSyncedImageHash = it }
                }
                return@setOnPrimaryClipChangedListener
            }

            val text = clipboard.readText()?.takeIf { it.isNotEmpty() } ?: return@setOnPrimaryClipChangedListener
            // Mesmo texto já sincronizado (comparação com fins de linha
            // normalizados — \r\n do Windows vs \n do Android). Nunca reenvia.
            val normalized = text.normalizeEol()
            if (normalized == lastSyncedText) return@setOnPrimaryClipChangedListener
            if (client.send(envelope(MessageType.CLIPBOARD_TEXT, ClipboardTextPayload(text)))) {
                lastSyncedText = normalized
                updateActivity(lastActivity = "Enviado ao PC: ${text.preview()}")
            }
        }
    }

    private inline fun <reified T> sendImage(
        pngBytes: ByteArray,
        width: Int,
        height: Int,
        type: String,
        crossinline payloadFactory: (String, Int, Int) -> T,
    ): Boolean {
        val sent = client.sendBlob(pngBytes) { blobId -> envelope(type, payloadFactory(blobId, width, height)) }
        if (sent) {
            updateActivity(
                lastActivity = if (type == MessageType.SCREENSHOT) "Screenshot enviado ao PC" else "Imagem enviada ao PC",
            )
        }
        return sent
    }

    private fun startHeartbeat() {
        heartbeatJob?.cancel()
        heartbeatJob = scope.launch {
            while (isActive && client.isSecure) {
                delay(HEARTBEAT_INTERVAL_MS)
                if (client.isSecure) {
                    client.send(envelope(MessageType.PING, EmptyPayload()))
                }
            }
        }
    }

    private fun stopHeartbeat() {
        heartbeatJob?.cancel()
        heartbeatJob = null
    }

    private fun updateActivity(
        statusLabel: String? = null,
        lastActivity: String? = null,
        isSecure: Boolean? = null,
    ) {
        _activity.value = _activity.value.copy(
            statusLabel = statusLabel ?: _activity.value.statusLabel,
            lastActivity = lastActivity ?: _activity.value.lastActivity,
            isSecure = isSecure ?: _activity.value.isSecure,
        )
    }

    private inline fun <reified T> envelope(type: String, payload: T): Envelope = Envelope(
        type = type,
        id = UUID.randomUUID().toString().replace("-", ""),
        timestamp = System.currentTimeMillis(),
        payload = json.encodeToJsonElement(payload).jsonObject,
    )

    private fun String.preview(maxLength: Int = 48): String =
        if (length <= maxLength) this else take(maxLength) + "…"

    /** Normaliza fins de linha (\r\n e \r → \n) para comparação e escrita locais. */
    private fun String.normalizeEol(): String = replace("\r\n", "\n").replace('\r', '\n')

    companion object {
        private const val HEARTBEAT_INTERVAL_MS = 15_000L
        private const val FIRST_RETRY_MS = 2_000L
        private const val MAX_RETRY_MS = 30_000L
    }
}

@kotlinx.serialization.Serializable
private data class EmptyPayload(val ok: Boolean = true)
