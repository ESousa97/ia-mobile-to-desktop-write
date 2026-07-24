package com.esousa.beam.net

import com.esousa.beam.blob.BlobChunkCodec
import com.esousa.beam.blob.BlobReceiver
import com.esousa.beam.blob.BlobSender
import com.esousa.beam.protocol.AckPayload
import com.esousa.beam.protocol.BlobBeginPayload
import com.esousa.beam.protocol.BlobEndPayload
import com.esousa.beam.protocol.Envelope
import com.esousa.beam.protocol.MessageType
import com.esousa.beam.protocol.ErrorPayload
import com.esousa.beam.protocol.PairConfirmPayload
import com.esousa.beam.protocol.PairResponsePayload
import com.esousa.beam.protocol.SecureEnvelope
import com.esousa.beam.protocol.SessionResumeConfirmPayload
import com.esousa.beam.protocol.SessionResumePayload
import com.esousa.beam.protocol.SessionResumedPayload
import com.esousa.beam.security.PairingManager
import com.esousa.beam.security.ResumeHandshake
import com.esousa.beam.security.SessionCipher
import com.esousa.beam.security.TrustRecord
import com.esousa.beam.security.X25519KeyAgreement
import java.security.SecureRandom
import java.util.Base64
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.SharedFlow
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.decodeFromJsonElement
import kotlinx.serialization.json.encodeToJsonElement
import kotlinx.serialization.json.jsonObject
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.Response
import okhttp3.WebSocket
import okhttp3.WebSocketListener
import java.io.ByteArrayOutputStream
import java.util.concurrent.TimeUnit
import java.util.UUID

class ClipBridgeClient(
    private val json: Json = Json { ignoreUnknownKeys = true },
) {
    private val httpClient = OkHttpClient.Builder()
        .pingInterval(15, TimeUnit.SECONDS)
        .build()

    private var webSocket: WebSocket? = null
    private val pairingManager = PairingManager()
    private var sessionCipher: SessionCipher? = null
    private var pendingConfirmationId: String? = null
    private var pendingResume: PendingResume? = null
    private val blobReceiver = BlobReceiver()
    private val binaryAccumulator = ByteArrayOutputStream()

    /** Handshake de retomada em andamento, entre `session.resume` e o `ack`. */
    private class PendingResume(
        val trust: TrustRecord,
        val keyAgreement: X25519KeyAgreement,
        val clientNonce: ByteArray,
    ) {
        var cipher: SessionCipher? = null
    }

    private val _incoming = MutableSharedFlow<Envelope>(extraBufferCapacity = 64)
    val incoming: SharedFlow<Envelope> = _incoming

    private val _blobs = MutableSharedFlow<BlobTransferCompleted>(extraBufferCapacity = 8)
    val blobs: SharedFlow<BlobTransferCompleted> = _blobs

    private val _connectionState = MutableSharedFlow<ConnectionState>(replay = 1, extraBufferCapacity = 8)
    val connectionState: SharedFlow<ConnectionState> = _connectionState

    /** Material do vínculo, emitido a cada sessão segura — pareamento ou retomada. */
    private val _trust = MutableSharedFlow<SessionTrust>(replay = 0, extraBufferCapacity = 4)
    val trust: SharedFlow<SessionTrust> = _trust

    val isSecure: Boolean
        get() = sessionCipher != null

    fun connect(host: String, port: Int) = connect(host, port, null)

    fun pair(host: String, port: Int, code: String) {
        val request = runCatching { pairingManager.createRequest() }
            .getOrElse { error ->
                _connectionState.tryEmit(ConnectionState.Error(error.message ?: "Não foi possível iniciar o pareamento"))
                return
            }
        connect(host, port, envelope(MessageType.PAIR_REQUEST, request), code)
    }

    /**
     * Reconecta usando um vínculo anterior, sem código. A chave efêmera é nova a
     * cada tentativa: uma retomada gravada não é decifrável nem com a chave de
     * retomada em mãos depois.
     */
    fun resume(trust: TrustRecord, host: String = trust.host, port: Int = trust.port) {
        val attempt = PendingResume(
            trust = trust,
            keyAgreement = X25519KeyAgreement(),
            clientNonce = ByteArray(ResumeHandshake.NONCE_SIZE).also(SecureRandom()::nextBytes),
        )
        pendingResume = attempt
        connect(
            host,
            port,
            envelope(
                MessageType.SESSION_RESUME,
                SessionResumePayload(
                    deviceId = trust.deviceId,
                    pubKey = Base64.getEncoder().encodeToString(attempt.keyAgreement.publicKey),
                    nonce = Base64.getEncoder().encodeToString(attempt.clientNonce),
                ),
            ),
        )
    }

    fun send(envelope: Envelope): Boolean {
        val socket = webSocket ?: return false
        val wire = sessionCipher?.let { SecureEnvelope.encrypt(envelope, it, json) } ?: envelope
        return socket.send(json.encodeToString(Envelope.serializer(), wire))
    }

    fun sendBlob(data: ByteArray, metadataFactory: (String) -> Envelope): Boolean {
        val socket = webSocket ?: return false
        val cipher = sessionCipher ?: return false
        return runCatching {
            BlobSender.send(socket, cipher, json, data, metadataFactory)
            true
        }.getOrDefault(false)
    }

    fun disconnect() {
        webSocket?.close(NORMAL_CLOSURE, "bye")
        webSocket = null
        pairingManager.cancel()
        sessionCipher = null
        pendingConfirmationId = null
        pendingResume = null
        binaryAccumulator.reset()
        _connectionState.tryEmit(ConnectionState.Disconnected)
    }

    private fun connect(host: String, port: Int, pairingRequest: Envelope?, pairingCode: String? = null) {
        // Encerra qualquer conexão anterior antes de abrir outra. Sem isto, cada nova
        // tentativa de pareamento deixava um WebSocket órfão vivo — o desktop passava a
        // ver N sessões seguras e entregava cada cópia N vezes (loop/duplicação).
        webSocket?.cancel()
        webSocket = null
        sessionCipher = null

        val request = Request.Builder()
            .url("ws://$host:$port/")
            .build()

        webSocket = httpClient.newWebSocket(request, object : WebSocketListener() {
            override fun onOpen(webSocket: WebSocket, response: Response) {
                val resuming = pendingResume != null
                if (pairingRequest == null) {
                    _connectionState.tryEmit(ConnectionState.Connected(host, port))
                } else if (webSocket.send(json.encodeToString(Envelope.serializer(), pairingRequest))) {
                    _connectionState.tryEmit(
                        if (resuming) ConnectionState.Resuming(host, port) else ConnectionState.Pairing(host, port),
                    )
                } else {
                    _connectionState.tryEmit(
                        ConnectionState.Error(
                            if (resuming) "Não foi possível retomar a sessão" else "Não foi possível iniciar o pareamento",
                        ),
                    )
                }
            }

            override fun onMessage(webSocket: WebSocket, text: String) {
                if (webSocket !== this@ClipBridgeClient.webSocket) return
                runCatching { json.decodeFromString<Envelope>(text) }
                    .getOrNull()
                    ?.let { handleIncoming(it, host, port, pairingCode) }
            }

            override fun onMessage(webSocket: WebSocket, bytes: okio.ByteString) {
                if (webSocket !== this@ClipBridgeClient.webSocket) return
                handleBinary(bytes.toByteArray())
            }

            override fun onClosing(webSocket: WebSocket, code: Int, reason: String) {
                webSocket.close(NORMAL_CLOSURE, null)
                if (webSocket !== this@ClipBridgeClient.webSocket) return
                _connectionState.tryEmit(ConnectionState.Disconnected)
            }

            override fun onFailure(webSocket: WebSocket, t: Throwable, response: Response?) {
                if (webSocket !== this@ClipBridgeClient.webSocket) return
                _connectionState.tryEmit(ConnectionState.Error(t.message ?: "falha de conexão"))
            }
        })
    }

    private fun handleBinary(frame: ByteArray) {
        val cipher = sessionCipher ?: return
        val decoded = BlobChunkCodec.decodeChunk(frame, cipher) ?: return
        blobReceiver.addChunk(decoded.blobId, decoded.sequence, decoded.plaintext)
    }

    private fun handleIncoming(
        envelope: Envelope,
        host: String,
        port: Int,
        pairingCode: String?,
    ) {
        runCatching {
            when (envelope.type) {
                MessageType.PAIR_RESPONSE -> {
                    val response = json.decodeFromJsonElement<PairResponsePayload>(requireNotNull(envelope.payload))
                    val confirmation = envelope(
                        MessageType.PAIR_CONFIRM,
                        pairingManager.createConfirmation(response, requireNotNull(pairingCode)),
                    )
                    check(requireNotNull(webSocket).send(json.encodeToString(Envelope.serializer(), confirmation))) {
                        "Não foi possível confirmar o pareamento."
                    }
                    pendingConfirmationId = confirmation.id
                }

                MessageType.SESSION_RESUMED -> handleResumed(envelope)

                MessageType.ACK -> {
                    val acknowledgment = json.decodeFromJsonElement<AckPayload>(requireNotNull(envelope.payload))
                    if (acknowledgment.ackId != pendingConfirmationId) return@runCatching

                    val resumed = pendingResume
                    val trustMaterial = if (resumed != null) {
                        sessionCipher = requireNotNull(resumed.cipher)
                        pendingResume = null
                        SessionTrust(host, port, resumed.trust.deviceId, resumed.trust.resumeKey)
                    } else {
                        val result = pairingManager.complete()
                        sessionCipher = result.cipher
                        SessionTrust(host, port, result.deviceId, result.resumeKey)
                    }

                    pendingConfirmationId = null
                    _trust.tryEmit(trustMaterial)
                    _connectionState.tryEmit(ConnectionState.Secure(host, port))
                }

                MessageType.ERROR -> {
                    val error = runCatching {
                        json.decodeFromJsonElement<ErrorPayload>(requireNotNull(envelope.payload))
                    }.getOrNull()
                    pairingManager.cancel()
                    pendingConfirmationId = null
                    pendingResume = null
                    _connectionState.tryEmit(
                        // O desktop esqueceu o vínculo (expirou, foi revogado ou o
                        // perfil do Windows mudou): só um novo código resolve.
                        if (error?.code == RESUME_DENIED) {
                            ConnectionState.TrustRejected(error.message)
                        } else {
                            ConnectionState.Error(error?.message ?: requireNotNull(envelope.payload).toString())
                        },
                    )
                }

                else -> if (sessionCipher != null) {
                    val decrypted = SecureEnvelope.decrypt(envelope, sessionCipher!!, json)
                    if (decrypted.type == MessageType.PONG) return@runCatching
                    if (handleBlobControl(decrypted)) return@runCatching
                    _incoming.tryEmit(decrypted)
                }
            }
        }.onFailure { error ->
            pairingManager.cancel()
            pendingConfirmationId = null
            _connectionState.tryEmit(ConnectionState.Error(error.message ?: "Falha no pareamento"))
        }
    }

    /**
     * Valida a prova do desktop, deriva a chave de sessão e devolve a prova do
     * celular já cifrada com ela — decifrar do outro lado fecha a autenticação
     * mútua sem que a chave de retomada trafegue.
     */
    private fun handleResumed(envelope: Envelope) {
        val attempt = requireNotNull(pendingResume) { "Resposta de retomada sem pedido correspondente." }
        val payload = json.decodeFromJsonElement<SessionResumedPayload>(requireNotNull(envelope.payload))

        val serverNonce = Base64.getDecoder().decode(payload.nonce)
        val salt = ResumeHandshake.buildSalt(attempt.clientNonce, serverNonce)
        check(
            ResumeHandshake.matches(
                Base64.getDecoder().decode(payload.proof),
                ResumeHandshake.serverProof(attempt.trust.resumeKey, salt),
            ),
        ) { "O desktop não provou conhecer o vínculo — retomada abortada." }

        val ecdhKey = attempt.keyAgreement.deriveSessionKey(
            Base64.getDecoder().decode(payload.pubKey),
            salt,
            ResumeHandshake.ecdhInfo,
        )
        val cipher = try {
            SessionCipher(ResumeHandshake.deriveSessionKey(ecdhKey, attempt.trust.resumeKey, salt))
        } finally {
            ecdhKey.fill(0)
        }
        attempt.cipher = cipher

        val confirmation = envelope(
            MessageType.SESSION_RESUME_CONFIRM,
            SessionResumeConfirmPayload(
                Base64.getEncoder().encodeToString(
                    ResumeHandshake.clientProof(attempt.trust.resumeKey, salt),
                ),
            ),
        )
        val wire = SecureEnvelope.encrypt(confirmation, cipher, json)
        check(requireNotNull(webSocket).send(json.encodeToString(Envelope.serializer(), wire))) {
            "Não foi possível confirmar a retomada."
        }
        pendingConfirmationId = confirmation.id
    }

    private fun handleBlobControl(envelope: Envelope): Boolean = when (envelope.type) {
        MessageType.BLOB_BEGIN -> {
            val begin = json.decodeFromJsonElement(BlobBeginPayload.serializer(), requireNotNull(envelope.payload))
            blobReceiver.begin(begin)
            true
        }

        MessageType.BLOB_END -> {
            val end = json.decodeFromJsonElement(BlobEndPayload.serializer(), requireNotNull(envelope.payload))
            blobReceiver.complete(end)?.let { data ->
                _blobs.tryEmit(BlobTransferCompleted(end.blobId, data))
            }
            true
        }

        else -> false
    }

    private inline fun <reified T> envelope(type: String, payload: T): Envelope = Envelope(
        type = type,
        id = UUID.randomUUID().toString().replace("-", ""),
        timestamp = System.currentTimeMillis(),
        payload = json.encodeToJsonElement(payload).jsonObject,
    )

    companion object {
        private const val NORMAL_CLOSURE = 1000
        private const val RESUME_DENIED = "resume.denied"
    }
}

data class BlobTransferCompleted(val blobId: String, val data: ByteArray)

/** Vínculo em vigor após uma sessão segura — o que o app persiste para retomar depois. */
data class SessionTrust(
    val host: String,
    val port: Int,
    val deviceId: String,
    val resumeKey: ByteArray,
) {
    override fun equals(other: Any?): Boolean =
        this === other || (other is SessionTrust &&
            host == other.host && port == other.port && deviceId == other.deviceId &&
            resumeKey.contentEquals(other.resumeKey))

    override fun hashCode(): Int =
        ((host.hashCode() * 31 + port) * 31 + deviceId.hashCode()) * 31 + resumeKey.contentHashCode()
}

sealed interface ConnectionState {
    data object Disconnected : ConnectionState
    data class Connected(val host: String, val port: Int) : ConnectionState
    data class Pairing(val host: String, val port: Int) : ConnectionState

    /** Retomando um vínculo anterior, sem código. */
    data class Resuming(val host: String, val port: Int) : ConnectionState
    data class Secure(val host: String, val port: Int) : ConnectionState
    data class Error(val message: String) : ConnectionState

    /** O desktop recusou o vínculo: é preciso parear de novo com código. */
    data class TrustRejected(val message: String) : ConnectionState
}
