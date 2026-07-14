package com.esousa.clipbridge.net

import com.esousa.clipbridge.protocol.Envelope
import com.esousa.clipbridge.protocol.AckPayload
import com.esousa.clipbridge.protocol.MessageType
import com.esousa.clipbridge.protocol.PairConfirmPayload
import com.esousa.clipbridge.protocol.PairResponsePayload
import com.esousa.clipbridge.security.PairingInvitation
import com.esousa.clipbridge.security.PairingManager
import com.esousa.clipbridge.security.SessionCipher
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.SharedFlow
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.encodeToJsonElement
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.Response
import okhttp3.WebSocket
import okhttp3.WebSocketListener
import java.util.concurrent.TimeUnit
import java.util.UUID

/**
 * Cliente WebSocket que conversa com o servidor ClipBridge do desktop na LAN.
 *
 * Esqueleto da fundação: a conexão e o recebimento de envelopes já funcionam;
 * o handshake de pareamento e a sessão cifrada entram nas próximas etapas.
 */
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

    private val _incoming = MutableSharedFlow<Envelope>(extraBufferCapacity = 64)
    val incoming: SharedFlow<Envelope> = _incoming

    private val _connectionState = MutableSharedFlow<ConnectionState>(replay = 1, extraBufferCapacity = 8)
    val connectionState: SharedFlow<ConnectionState> = _connectionState

    fun connect(host: String, port: Int) = connect(host, port, null)

    fun pair(invitation: PairingInvitation) {
        val request = runCatching { pairingManager.createRequest(invitation) }
            .getOrElse { error ->
                _connectionState.tryEmit(ConnectionState.Error(error.message ?: "QR Code de pareamento inválido"))
                return
            }
        connect(invitation.host, invitation.port, envelope(MessageType.PAIR_REQUEST, request))
    }

    private fun connect(host: String, port: Int, pairingRequest: Envelope?) {
        val request = Request.Builder()
            .url("ws://$host:$port/")
            .build()

        webSocket = httpClient.newWebSocket(request, object : WebSocketListener() {
            override fun onOpen(webSocket: WebSocket, response: Response) {
                if (pairingRequest is null) {
                    _connectionState.tryEmit(ConnectionState.Connected(host, port))
                } else if (webSocket.send(json.encodeToString(Envelope.serializer(), pairingRequest))) {
                    _connectionState.tryEmit(ConnectionState.Pairing(host, port))
                } else {
                    _connectionState.tryEmit(ConnectionState.Error("Não foi possível iniciar o pareamento"))
                }
            }

            override fun onMessage(webSocket: WebSocket, text: String) {
                runCatching { json.decodeFromString<Envelope>(text) }
                    .getOrNull()
                    ?.let { handleIncoming(webSocket, it, host, port) }
            }

            override fun onClosing(webSocket: WebSocket, code: Int, reason: String) {
                webSocket.close(NORMAL_CLOSURE, null)
                _connectionState.tryEmit(ConnectionState.Disconnected)
            }

            override fun onFailure(webSocket: WebSocket, t: Throwable, response: Response?) {
                _connectionState.tryEmit(ConnectionState.Error(t.message ?: "falha de conexão"))
            }
        })
    }

    fun send(envelope: Envelope): Boolean {
        val socket = webSocket ?: return false
        return socket.send(json.encodeToString(Envelope.serializer(), envelope))
    }

    fun disconnect() {
        webSocket?.close(NORMAL_CLOSURE, "bye")
        webSocket = null
        pairingManager.cancel()
        sessionCipher = null
        pendingConfirmationId = null
        _connectionState.tryEmit(ConnectionState.Disconnected)
    }

    private fun handleIncoming(webSocket: WebSocket, envelope: Envelope, host: String, port: Int) {
        runCatching {
            when (envelope.type) {
                MessageType.PAIR_RESPONSE -> {
                    val response = json.decodeFromJsonElement<PairResponsePayload>(requireNotNull(envelope.payload))
                    val confirmation = envelope(MessageType.PAIR_CONFIRM, pairingManager.createConfirmation(response))
                    check(webSocket.send(json.encodeToString(Envelope.serializer(), confirmation))) {
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
                    val error = requireNotNull(envelope.payload)
                    _connectionState.tryEmit(ConnectionState.Error(error.toString()))
                    pairingManager.cancel()
                    pendingConfirmationId = null
                }

                else -> if (sessionCipher is not null) {
                    _incoming.tryEmit(envelope)
                }
            }
        }.onFailure { error ->
            pairingManager.cancel()
            pendingConfirmationId = null
            _connectionState.tryEmit(ConnectionState.Error(error.message ?: "Falha no pareamento"))
        }
    }

    private fun <T> envelope(type: String, payload: T): Envelope = Envelope(
        type = type,
        id = UUID.randomUUID().toString().replace("-", ""),
        timestamp = System.currentTimeMillis(),
        payload = json.encodeToJsonElement(payload),
    )

    companion object {
        private const val NORMAL_CLOSURE = 1000
    }
}

sealed interface ConnectionState {
    data object Disconnected : ConnectionState
    data class Connected(val host: String, val port: Int) : ConnectionState
    data class Pairing(val host: String, val port: Int) : ConnectionState
    data class Secure(val host: String, val port: Int) : ConnectionState
    data class Error(val message: String) : ConnectionState
}
