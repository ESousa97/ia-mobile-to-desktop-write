package com.esousa.clipbridge.discovery

import android.content.Context
import android.net.nsd.NsdManager
import android.net.nsd.NsdServiceInfo
import com.esousa.clipbridge.protocol.Protocol
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow

/** Um desktop ClipBridge descoberto na rede local. */
data class DiscoveredDesktop(val name: String, val host: String, val port: Int)

/**
 * Descobre servidores ClipBridge na LAN via NSD (mDNS/DNS-SD), procurando o
 * serviço [Protocol.SERVICE_TYPE]. Esqueleto: registra o listener e publica os
 * serviços resolvidos; a resolução completa de host/porta é finalizada na etapa
 * de pareamento.
 */
class NsdDiscovery(context: Context) {

    private val nsdManager = context.getSystemService(Context.NSD_SERVICE) as NsdManager
    private var discoveryListener: NsdManager.DiscoveryListener? = null

    private val _desktops = MutableStateFlow<List<DiscoveredDesktop>>(emptyList())
    val desktops: StateFlow<List<DiscoveredDesktop>> = _desktops

    fun start() {
        if (discoveryListener != null) return

        val listener = object : NsdManager.DiscoveryListener {
            override fun onDiscoveryStarted(serviceType: String) {}

            override fun onServiceFound(serviceInfo: NsdServiceInfo) {
                nsdManager.resolveService(serviceInfo, resolveListener())
            }

            override fun onServiceLost(serviceInfo: NsdServiceInfo) {
                _desktops.value = _desktops.value.filterNot { it.name == serviceInfo.serviceName }
            }

            override fun onDiscoveryStopped(serviceType: String) {}
            override fun onStartDiscoveryFailed(serviceType: String, errorCode: Int) {}
            override fun onStopDiscoveryFailed(serviceType: String, errorCode: Int) {}
        }

        discoveryListener = listener
        nsdManager.discoverServices(
            "${Protocol.SERVICE_TYPE}.",
            NsdManager.PROTOCOL_DNS_SD,
            listener,
        )
    }

    fun stop() {
        discoveryListener?.let { nsdManager.stopServiceDiscovery(it) }
        discoveryListener = null
    }

    private fun resolveListener() = object : NsdManager.ResolveListener {
        override fun onResolveFailed(serviceInfo: NsdServiceInfo, errorCode: Int) {}

        @Suppress("DEPRECATION")
        override fun onServiceResolved(serviceInfo: NsdServiceInfo) {
            val host = serviceInfo.host?.hostAddress ?: return
            val desktop = DiscoveredDesktop(serviceInfo.serviceName, host, serviceInfo.port)
            _desktops.value = (_desktops.value.filterNot { it.name == desktop.name } + desktop)
        }
    }
}
