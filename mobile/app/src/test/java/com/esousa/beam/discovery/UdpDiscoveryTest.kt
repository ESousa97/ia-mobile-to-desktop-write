package com.esousa.beam.discovery

import com.esousa.beam.protocol.Protocol
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotEquals
import org.junit.Assert.assertNull
import org.junit.Test
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress

class UdpDiscoveryTest {

    private val announce = "${Protocol.ANNOUNCE_PREFIX}8787${Protocol.ANNOUNCE_SUFFIX}"

    @Test
    fun parseAnnouncedPort_acceptsWellFormedAnnounce() {
        assertEquals(8787, UdpDiscovery.parseAnnouncedPort(announce))
        assertEquals(1, UdpDiscovery.parseAnnouncedPort("${Protocol.ANNOUNCE_PREFIX}1${Protocol.ANNOUNCE_SUFFIX}"))
    }

    @Test
    fun parseAnnouncedPort_rejectsJunk() {
        assertNull(UdpDiscovery.parseAnnouncedPort(""))
        assertNull(UdpDiscovery.parseAnnouncedPort("clipbridge.discover.v1"))
        assertNull(UdpDiscovery.parseAnnouncedPort(Protocol.ANNOUNCE_PREFIX))
        assertNull(UdpDiscovery.parseAnnouncedPort("${Protocol.ANNOUNCE_PREFIX}87a7${Protocol.ANNOUNCE_SUFFIX}"))
        assertNull(UdpDiscovery.parseAnnouncedPort("$announce extra"))
        // Fora da faixa válida de portas TCP.
        assertNull(UdpDiscovery.parseAnnouncedPort("${Protocol.ANNOUNCE_PREFIX}70000${Protocol.ANNOUNCE_SUFFIX}"))
        assertNull(UdpDiscovery.parseAnnouncedPort("${Protocol.ANNOUNCE_PREFIX}0${Protocol.ANNOUNCE_SUFFIX}"))
    }

    /**
     * O caso que quebrou em campo: o datagrama chegou cortado em um byte e
     * `…:8787;` virou `…:8787` — antes do terminador isso passava como porta 878
     * e o app tentava conectar onde ninguém escuta.
     */
    @Test
    fun parseAnnouncedPort_rejectsEveryTruncation() {
        for (cut in announce.length - 1 downTo 1) {
            assertNull("prefixo de $cut bytes não pode ser aceito", UdpDiscovery.parseAnnouncedPort(announce.take(cut)))
        }
        assertNotEquals(878, UdpDiscovery.parseAnnouncedPort("${Protocol.ANNOUNCE_PREFIX}878"))
    }

    /**
     * O broadcast dirigido é o único que roteadores Wi-Fi costumam entregar —
     * `255.255.255.255` é descartado. Errar esse cálculo deixa o app sem descobrir
     * nada na rede.
     */
    @Test
    fun broadcastAddress_computesDirectedBroadcast() {
        fun ip(value: String) = value.split('.').map { it.toInt().toByte() }.toByteArray()
        fun text(bytes: ByteArray?) = bytes?.joinToString(".") { (it.toInt() and 0xFF).toString() }

        assertEquals("192.168.15.255", text(UdpDiscovery.broadcastAddress(ip("192.168.15.17"), 24)))
        assertEquals("192.168.15.255", text(UdpDiscovery.broadcastAddress(ip("192.168.15.6"), 24)))
        assertEquals("10.0.255.255", text(UdpDiscovery.broadcastAddress(ip("10.0.1.2"), 16)))
        assertEquals("172.16.31.255", text(UdpDiscovery.broadcastAddress(ip("172.16.16.9"), 19)))
        // Sem broadcast útil ou entrada inválida.
        assertNull(UdpDiscovery.broadcastAddress(ip("192.168.15.17"), 31))
        assertNull(UdpDiscovery.broadcastAddress(ip("192.168.15.17"), 32))
        assertNull(UdpDiscovery.broadcastAddress(ByteArray(16), 24))
    }

    /** Restaurar o length antes de cada receive mantém o anúncio íntegro. */
    @Test
    fun resettingLengthBeforeEachReceive_preservesFullAnnounce() {
        withUdpPair { sender, receiver, target ->
            val short = "curto".toByteArray()
            val announceBytes = announce.toByteArray()
            val buffer = ByteArray(128)
            val packet = DatagramPacket(buffer, buffer.size)

            sender.send(DatagramPacket(short, short.size, target, receiver.localPort))
            packet.length = buffer.size
            receiver.receive(packet)

            sender.send(DatagramPacket(announceBytes, announceBytes.size, target, receiver.localPort))
            packet.length = buffer.size
            receiver.receive(packet)

            assertEquals(announce, String(packet.data, 0, packet.length))
            assertEquals(8787, UdpDiscovery.parseAnnouncedPort(String(packet.data, 0, packet.length)))
        }
    }

    /** Par de sockets em loopback com portas efêmeras — não colide com o app real. */
    private fun withUdpPair(block: (DatagramSocket, DatagramSocket, InetAddress) -> Unit) {
        val loopback = InetAddress.getByName("127.0.0.1")
        DatagramSocket(0, loopback).use { sender ->
            DatagramSocket(0, loopback).use { receiver ->
                receiver.soTimeout = 5_000
                block(sender, receiver, loopback)
            }
        }
    }
}
