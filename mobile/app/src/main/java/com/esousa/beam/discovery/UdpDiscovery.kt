package com.esousa.beam.discovery

import com.esousa.beam.BuildConfig
import com.esousa.beam.protocol.Protocol
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.util.concurrent.ExecutorService
import java.util.concurrent.Executors

/** Um desktop ClipBridge descoberto na rede local. */
data class DiscoveredDesktop(val name: String, val host: String, val port: Int)

/**
 * Descobre servidores ClipBridge na LAN por broadcast UDP.
 * Em builds de debug, também consulta 10.0.2.2 (alias do host no emulador Android).
 */
class UdpDiscovery {

    private var executor: ExecutorService? = null

    private val _desktops = MutableStateFlow<List<DiscoveredDesktop>>(emptyList())
    val desktops: StateFlow<List<DiscoveredDesktop>> = _desktops

    fun start() {
        if (executor != null) return
        val discoveryExecutor = Executors.newSingleThreadExecutor()
        executor = discoveryExecutor
        discoveryExecutor.execute {
            try {
                discover()
            } finally {
                if (executor == discoveryExecutor) executor = null
                discoveryExecutor.shutdown()
            }
        }
    }

    fun stop() {
        executor?.shutdownNow()
        executor = null
    }

    private fun discover() {
        val request = "clipbridge.discover.v1".toByteArray()
        DatagramSocket().use { socket ->
            socket.broadcast = true
            socket.soTimeout = 3_000
            discoveryTargets().forEach { host ->
                socket.send(
                    DatagramPacket(request, request.size, InetAddress.getByName(host), Protocol.DISCOVERY_PORT),
                )
            }

            val response = DatagramPacket(ByteArray(128), 128)
            while (!Thread.currentThread().isInterrupted) {
                try {
                    socket.receive(response)
                    val value = String(response.data, 0, response.length)
                    val port = value.removePrefix("clipbridge.announce.v1:").toIntOrNull() ?: continue
                    val host = response.address?.hostAddress ?: continue
                    _desktops.value = listOf(DiscoveredDesktop("ClipBridge", host, port))
                    return
                } catch (_: java.net.SocketTimeoutException) {
                    return
                }
            }
        }
    }

    private fun discoveryTargets(): List<String> =
        buildList {
            add("255.255.255.255")
            if (BuildConfig.DEBUG) add("10.0.2.2")
        }
}
