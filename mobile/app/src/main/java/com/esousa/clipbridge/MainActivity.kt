package com.esousa.clipbridge

import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.activity.viewModels
import com.esousa.clipbridge.ui.ClipBridgeViewModel
import com.esousa.clipbridge.ui.HomeScreen
import com.esousa.clipbridge.ui.theme.ClipBridgeTheme

class MainActivity : ComponentActivity() {

    private val viewModel: ClipBridgeViewModel by viewModels()

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        enableEdgeToEdge()
        setContent {
            ClipBridgeTheme {
                HomeScreen(viewModel = viewModel)
            }
        }
    }
}
