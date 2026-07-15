package com.esousa.beam.net

import com.esousa.beam.blob.BlobChunkCodec
import com.esousa.beam.blob.BlobReceiver
import com.esousa.beam.blob.BlobSender
import com.esousa.beam.protocol.AckPayload
import com.esousa.beam.protocol.BlobBeginPayload
import com.esousa.beam.protocol.BlobEndPayload
import com.esousa.beam.protocol.Envelope
import com.esousa.beam.protocol.MessageType
import com.esousa.beam.protocol.PairConfirmPayload
import com.esousa.beam.protocol.PairResponsePayload
import com.esousa.beam.protocol.SecureEnvelope
import com.esousa.beam.security.PairingManager
import com.esousa.beam.security.SessionCipher
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
    private val blobReceiver = BlobReceiver()
    private val binaryAccumulator = ByteArrayOutputStream()

    private val _incoming = MutableSharedFlow<Envelope>(extraBufferCapacity = 64)
    val incoming: SharedFlow<Envelope> = _incoming

    private val _blobs = MutableSharedFlow<BlobTransferCompleted>(extraBufferCapacity = 8)
    val blobs: SharedFlow<BlobTransferCompleted> = _blobs

    private val _connectionState = MutableSharedFlow<ConnectionState>(replay = 1, extraBufferCapacity = 8)
    val connectionState: SharedFlow<ConnectionState> = _connectionState

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
                if (pairingRequest == null) {
                    _connectionState.tryEmit(ConnectionState.Connected(host, port))
                } else if (webSocket.send(json.encodeToString(Envelope.serializer(), pairingRequest))) {
                    _connectionState.tryEmit(ConnectionState.Pairing(host, port))
                } else {
                    _connectionState.tryEmit(ConnectionState.Error("Não foi possível iniciar o pareamento"))
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

                MessageType.ACK -> {
                    val acknowledgment = json.decodeFromJsonElement<AckPayload>(requireNotNull(envelope.payload))
                    if (acknowledgment.ackId == pendingConfirmationId) {
                        sessionCipher = pairingManager.complete()
                        pendingConfirmationId = null
                        _connectionState.tryEmit(ConnectionState.Secure(host, port))
                    }
                }

                MessageType.ERROR -> {
                    _connectionState.tryEmit(ConnectionState.Error(requireNotNull(envelope.payload).toString()))
                    pairingManager.cancel()
                    pendingConfirmationId = null
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
    }
}

data class BlobTransferCompleted(val blobId: String, val data: ByteArray)

sealed interface ConnectionState {
    data object Disconnected : ConnectionState
    data class Connected(val host: String, val port: Int) : ConnectionState
    data class Pairing(val host: String, val port: Int) : ConnectionState
    data class Secure(val host: String, val port: Int) : ConnectionState
    data class Error(val message: String) : ConnectionState
}
