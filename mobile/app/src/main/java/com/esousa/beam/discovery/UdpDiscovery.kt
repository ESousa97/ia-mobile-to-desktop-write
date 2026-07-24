package com.esousa.beam.discovery

import android.content.Context
import android.net.ConnectivityManager
import android.net.wifi.WifiManager
import java.net.Inet4Address
import com.esousa.beam.BuildConfig
import com.esousa.beam.protocol.Protocol
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.NetworkInterface
import java.net.SocketTimeoutException
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.ExecutorService
import java.util.concurrent.Executors

/** Um desktop ClipBridge descoberto na rede local. */
data class DiscoveredDesktop(val name: String, val host: String, val port: Int)

/**
 * Descobre servidores ClipBridge na Wi-Fi por duas vias complementares:
 *  - ativa: envia sondas UDP para o broadcast dirigido de cada interface
 *    (255.255.255.255 é descartado por muitos roteadores Wi-Fi);
 *  - passiva: escuta na porta de anúncio os broadcasts periódicos do desktop —
 *    funciona mesmo quando o firewall do Windows bloqueia a sonda de entrada.
 *
 * A busca é contínua até [stop]: numa Wi-Fi o desktop pode entrar na rede depois
 * do celular, e uma janela fixa deixaria o app "sem desktop" para sempre. O
 * intervalo entre sondas cresce depois do primeiro minuto para poupar bateria.
 * Em builds de debug, também consulta 10.0.2.2 (alias do host no emulador Android).
 */
class UdpDiscovery(context: Context? = null) {

    private val appContext = context?.applicationContext

    private var executor: ExecutorService? = null

    @Volatile
    private var openSockets = listOf<DatagramSocket>()

    @Volatile
    private var multicastLock: WifiManager.MulticastLock? = null

    private val found = ConcurrentHashMap<String, DiscoveredDesktop>()

    private val _desktops = MutableStateFlow<List<DiscoveredDesktop>>(emptyList())
    val desktops: StateFlow<List<DiscoveredDesktop>> = _desktops

    val isRunning: Boolean
        get() = executor != null

    fun start() {
        if (executor != null) return
        val discoveryExecutor = Executors.newFixedThreadPool(2)
        executor = discoveryExecutor
        openSockets = emptyList()
        multicastLock = acquireMulticastLock()
        // Lista fresca a cada varredura: numa Wi-Fi o desktop pode ter trocado de
        // IP (DHCP) desde a última, e um endereço obsoleto trava o pareamento.
        found.clear()
        _desktops.value = emptyList()

        discoveryExecutor.execute { activeProbe() }
        discoveryExecutor.execute { passiveListen() }
    }

    /** Reinicia a varredura mesmo se já estiver ativa (ação explícita do usuário). */
    fun restart() {
        stop()
        start()
    }

    fun stop() {
        openSockets.forEach { runCatching { it.close() } }
        openSockets = emptyList()
        multicastLock?.let { runCatching { it.release() } }
        multicastLock = null
        executor?.shutdownNow()
        executor = null
    }

    /** Envia sondas de descoberta e coleta as respostas unicast do desktop. */
    private fun activeProbe() {
        val request = Protocol.DISCOVERY_REQUEST.toByteArray()
        runCatching {
            DatagramSocket().use { socket ->
                socket.broadcast = true
                socket.soTimeout = SOCKET_POLL_MS
                openSockets = openSockets + socket

                val startedAt = System.currentTimeMillis()
                var nextProbeAt = 0L
                val buffer = ByteArray(RECEIVE_BUFFER_SIZE)
                val response = DatagramPacket(buffer, buffer.size)
                while (!Thread.currentThread().isInterrupted) {
                    if (System.currentTimeMillis() >= nextProbeAt) {
                        discoveryTargets().forEach { target ->
                            runCatching {
                                socket.send(DatagramPacket(request, request.size, target, Protocol.DISCOVERY_PORT))
                            }
                        }
                        val elapsed = System.currentTimeMillis() - startedAt
                        val interval = if (elapsed < FAST_PHASE_MS) FAST_PROBE_MS else SLOW_PROBE_MS
                        nextProbeAt = System.currentTimeMillis() + interval
                    }
                    try {
                        // Obrigatório antes de cada receive: o receive anterior deixou
                        // length = tamanho daquele datagrama, e o próximo seria cortado
                        // nesse limite (um anúncio truncado vira uma porta errada).
                        response.length = buffer.size
                        socket.receive(response)
                        handleAnnounce(response)
                    } catch (_: SocketTimeoutException) {
                        // Sem resposta ainda; retransmite na próxima iteração.
                    }
                }
            }
        }
    }

    /** Escuta os anúncios em broadcast que o desktop emite periodicamente. */
    private fun passiveListen() {
        runCatching {
            DatagramSocket(null).use { socket ->
                socket.reuseAddress = true
                socket.soTimeout = SOCKET_POLL_MS
                socket.bind(InetSocketAddress(Protocol.ANNOUNCE_PORT))
                openSockets = openSockets + socket

                val buffer = ByteArray(RECEIVE_BUFFER_SIZE)
                val response = DatagramPacket(buffer, buffer.size)
                while (!Thread.currentThread().isInterrupted) {
                    try {
                        // Ver activeProbe: sem restaurar o length, o datagrama seguinte
                        // é truncado no tamanho do anterior.
                        response.length = buffer.size
                        socket.receive(response)
                        handleAnnounce(response)
                    } catch (_: SocketTimeoutException) {
                        // Continua escutando enquanto a descoberta estiver ativa.
                    }
                }
            }
        }
    }

    private fun handleAnnounce(packet: DatagramPacket) {
        // Um datagrama que ocupa o buffer inteiro provavelmente veio cortado: aceitá-lo
        // significaria conectar numa porta errada (ex.: 8787 lido como 878).
        if (packet.length >= packet.data.size) return
        val port = parseAnnouncedPort(String(packet.data, 0, packet.length)) ?: return
        val host = packet.address?.hostAddress ?: return
        if (found.putIfAbsent("$host:$port", DiscoveredDesktop(host, host, port)) == null) {
            _desktops.value = found.values.toList()
        }
    }

    /**
     * Alvos das sondas, em ordem de eficácia: o broadcast dirigido da rede atual
     * (ex.: 192.168.15.255) e, por último, o global.
     *
     * Roteadores Wi-Fi tipicamente **descartam 255.255.255.255** e entregam apenas
     * o dirigido, então depender só do global deixa o app sem descobrir nada. O
     * dirigido vem do `ConnectivityManager` (IP + prefixo da rede ativa), porque
     * `NetworkInterface.getInterfaceAddresses().broadcast` costuma vir nulo no
     * Android moderno.
     */
    private fun discoveryTargets(): List<InetAddress> =
        buildList {
            addAll(activeNetworkBroadcasts())
            runCatching {
                NetworkInterface.getNetworkInterfaces()?.toList().orEmpty()
                    .filter { it.isUp && !it.isLoopback }
                    .flatMap { it.interfaceAddresses }
                    .mapNotNull { it.broadcast }
                    .forEach { add(it) }
            }
            add(InetAddress.getByName("255.255.255.255"))
            if (BuildConfig.DEBUG) add(InetAddress.getByName("10.0.2.2"))
        }.distinct()

    /** Broadcast dirigido de cada endereço IPv4 da rede ativa. */
    private fun activeNetworkBroadcasts(): List<InetAddress> {
        val manager = appContext?.getSystemService(Context.CONNECTIVITY_SERVICE) as? ConnectivityManager
            ?: return emptyList()

        return runCatching {
            val network = manager.activeNetwork ?: return emptyList()
            val properties = manager.getLinkProperties(network) ?: return emptyList()
            properties.linkAddresses.mapNotNull { link ->
                val address = link.address as? Inet4Address ?: return@mapNotNull null
                broadcastAddress(address.address, link.prefixLength)?.let(InetAddress::getByAddress)
            }
        }.getOrDefault(emptyList())
    }

    /**
     * Sem o MulticastLock, muitos aparelhos Android descartam pacotes de
     * broadcast recebidos pelo Wi-Fi para economizar bateria.
     */
    private fun acquireMulticastLock(): WifiManager.MulticastLock? {
        val wifi = appContext?.getSystemService(Context.WIFI_SERVICE) as? WifiManager ?: return null
        return runCatching {
            wifi.createMulticastLock("clipbridge-discovery").apply {
                setReferenceCounted(false)
                acquire()
            }
        }.getOrNull()
    }

    companion object {
        private const val FAST_PHASE_MS = 60_000L
        private const val FAST_PROBE_MS = 2_000L
        private const val SLOW_PROBE_MS = 10_000L
        private const val SOCKET_POLL_MS = 1_000
        private const val RECEIVE_BUFFER_SIZE = 128

        private val ANNOUNCE_PATTERN = Regex(
            "^" + Regex.escape(Protocol.ANNOUNCE_PREFIX) + "(\\d{1,5})" +
                Regex.escape(Protocol.ANNOUNCE_SUFFIX) + "$",
        )

        /**
         * Porta anunciada por um desktop, ou null se a mensagem não for exatamente
         * um anúncio válido. Exigir o terminador é o que faz um datagrama cortado
         * no meio dos dígitos ser rejeitado em vez de virar uma porta plausível
         * porém errada — foi assim que `…:8787` chegou como `…:878` e o app passou
         * a tentar a porta 878.
         */
        /**
         * Endereço de broadcast dirigido de `address/prefixLength`: bits de host
         * em 1 (192.168.15.17/24 → 192.168.15.255). Devolve null para entradas
         * sem broadcast útil (/31, /32 ou dados inválidos).
         */
        internal fun broadcastAddress(address: ByteArray, prefixLength: Int): ByteArray? {
            if (address.size != 4 || prefixLength !in 1..30) return null

            val hostBits = 32 - prefixLength
            val value = address.fold(0) { acc, byte -> (acc shl 8) or (byte.toInt() and 0xFF) }
            val broadcast = value or ((1 shl hostBits) - 1)
            return byteArrayOf(
                (broadcast ushr 24).toByte(),
                (broadcast ushr 16).toByte(),
                (broadcast ushr 8).toByte(),
                broadcast.toByte(),
            )
        }

        internal fun parseAnnouncedPort(message: String): Int? {
            val digits = ANNOUNCE_PATTERN.matchEntire(message.trim())?.groupValues?.get(1) ?: return null
            return digits.toIntOrNull()?.takeIf { it in 1..65535 }
        }
    }
}
