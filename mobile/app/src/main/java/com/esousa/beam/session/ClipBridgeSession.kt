package com.esousa.beam.session

import android.content.Context
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
import com.esousa.beam.protocol.ScreenshotPayload
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

    val discovery = UdpDiscovery()
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
        observeClipboard()
        discovery.start()
    }

    fun startDiscovery() = discovery.start()

    fun confirmPairing(code: String) {
        val desktop = _discovered.value.firstOrNull()
        if (desktop == null) {
            updateActivity(statusLabel = "Nenhum desktop encontrado na rede")
            return
        }
        lastDesktop = desktop
        client.pair(desktop.host, desktop.port, code)
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
                is ConnectionState.Secure -> "Conexão segura com ${state.host}:${state.port}" to true
                is ConnectionState.Error -> {
                    _events.tryEmit(state.message)
                    discovery.start()
                    "Erro: ${state.message}" to false
                }
                ConnectionState.Disconnected -> {
                    stopHeartbeat()
                    ClipBridgeForegroundService.stop(appContext)
                    discovery.start()
                    "Desconectado — procurando desktop…" to false
                }
            }
            updateActivity(statusLabel = label, isSecure = secure)
            if (secure) {
                ClipBridgeForegroundService.start(appContext, label)
                startHeartbeat()
            }
        }
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
    }
}

@kotlinx.serialization.Serializable
private data class EmptyPayload(val ok: Boolean = true)
