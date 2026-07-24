package com.esousa.beam.ui

import android.app.Application
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import com.esousa.beam.ClipBridgeApplication
import com.esousa.beam.discovery.DiscoveredDesktop
import com.esousa.beam.service.BeamInputMethodService
import com.esousa.beam.session.ReceivedImage
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharingStarted
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.combine
import kotlinx.coroutines.flow.stateIn

data class HomeUiState(
    val statusLabel: String = "Desconectado",
    val discovered: List<DiscoveredDesktop> = emptyList(),
    val lastActivity: String? = null,
    val isSecure: Boolean = false,
    val latestImage: ReceivedImage? = null,
    val isBeamKeyboardSelected: Boolean = false,
    /** Vínculo válido: a reconexão é automática, sem código. */
    val isResuming: Boolean = false,
    val trustExpiresAt: Long? = null,
)

class ClipBridgeViewModel(application: Application) : AndroidViewModel(application) {

    private val session = (application as ClipBridgeApplication).session
    private val isBeamKeyboardSelected = MutableStateFlow(
        BeamInputMethodService.isSelected(application),
    )

    val uiState: StateFlow<HomeUiState> = combine(
        session.discovered,
        session.activity,
        session.latestImage,
        isBeamKeyboardSelected,
    ) { discovered, activity, image, keyboardSelected ->
        HomeUiState(
            statusLabel = activity.statusLabel,
            discovered = discovered,
            lastActivity = activity.lastActivity,
            isSecure = activity.isSecure,
            latestImage = image,
            isBeamKeyboardSelected = keyboardSelected,
            isResuming = activity.isResuming,
            trustExpiresAt = activity.trustExpiresAt,
        )
    }.stateIn(viewModelScope, SharingStarted.WhileSubscribed(5_000), HomeUiState())

    fun startDiscovery() = session.startDiscovery()

    fun confirmPairing(code: String, manualAddress: String? = null) =
        session.confirmPairing(code, manualAddress)

    fun refreshInputMethodStatus() {
        isBeamKeyboardSelected.value = BeamInputMethodService.isSelected(getApplication())
    }
}
