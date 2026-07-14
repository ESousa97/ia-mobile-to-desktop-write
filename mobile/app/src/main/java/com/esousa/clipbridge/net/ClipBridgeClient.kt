package com.esousa.clipbridge.net

import com.esousa.clipbridge.protocol.Envelope
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.SharedFlow
import kotlinx.serialization.json.Json
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.Response
import okhttp3.WebSocket
import okhttp3.WebSocketListener
import java.util.concurrent.TimeUnit

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

    private val _incoming = MutableSharedFlow<Envelope>(extraBufferCapacity = 64)
    val incoming: SharedFlow<Envelope> = _incoming

    private val _connectionState = MutableSharedFlow<ConnectionState>(replay = 1, extraBufferCapacity = 8)
    val connectionState: SharedFlow<ConnectionState> = _connectionState

    fun connect(host: String, port: Int) {
        val request = Request.Builder()
            .url("ws://$host:$port/")
            .build()

        webSocket = httpClient.newWebSocket(request, object : WebSocketListener() {
            override fun onOpen(webSocket: WebSocket, response: Response) {
                _connectionState.tryEmit(ConnectionState.Connected(host, port))
            }

            override fun onMessage(webSocket: WebSocket, text: String) {
                runCatching { json.decodeFromString<Envelope>(text) }
                    .getOrNull()
                    ?.let { _incoming.tryEmit(it) }
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
        _connectionState.tryEmit(ConnectionState.Disconnected)
    }

    companion object {
        private const val NORMAL_CLOSURE = 1000
    }
}

sealed interface ConnectionState {
    data object Disconnected : ConnectionState
    data class Connected(val host: String, val port: Int) : ConnectionState
    data class Error(val message: String) : ConnectionState
}
