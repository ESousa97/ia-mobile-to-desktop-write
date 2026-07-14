package com.esousa.clipbridge.ui

import android.app.Application
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import com.esousa.clipbridge.clipboard.ClipboardRepository
import com.esousa.clipbridge.discovery.DiscoveredDesktop
import com.esousa.clipbridge.discovery.NsdDiscovery
import com.esousa.clipbridge.net.ClipBridgeClient
import com.esousa.clipbridge.net.ConnectionState
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch

data class HomeUiState(
    val statusLabel: String = "Desconectado",
    val discovered: List<DiscoveredDesktop> = emptyList(),
    val lastActivity: String? = null,
)

/**
 * ViewModel da tela principal. Orquestra descoberta, conexão e área de
 * transferência, expondo um único [HomeUiState] para a UI (padrão UDF).
 */
class ClipBridgeViewModel(application: Application) : AndroidViewModel(application) {

    private val discovery = NsdDiscovery(application)
    private val client = ClipBridgeClient()
    private val clipboard = ClipboardRepository(application)

    private val _uiState = MutableStateFlow(HomeUiState())
    val uiState: StateFlow<HomeUiState> = _uiState.asStateFlow()

    init {
        observeDiscovery()
        observeConnection()
    }

    private fun observeDiscovery() = viewModelScope.launch {
        discovery.desktops.collect { list ->
            _uiState.value = _uiState.value.copy(discovered = list)
        }
    }

    private fun observeConnection() = viewModelScope.launch {
        client.connectionState.collect { state ->
            val label = when (state) {
                is ConnectionState.Connected -> "Conectado a ${state.host}:${state.port}"
                is ConnectionState.Error -> "Erro: ${state.message}"
                ConnectionState.Disconnected -> "Desconectado"
            }
            _uiState.value = _uiState.value.copy(statusLabel = label)
        }
    }

    fun startDiscovery() = discovery.start()

    fun connectTo(desktop: DiscoveredDesktop) = client.connect(desktop.host, desktop.port)

    override fun onCleared() {
        super.onCleared()
        discovery.stop()
        client.disconnect()
    }
}
