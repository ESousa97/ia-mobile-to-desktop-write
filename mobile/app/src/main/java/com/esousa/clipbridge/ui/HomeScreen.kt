package com.esousa.clipbridge.ui

import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.rounded.Computer
import androidx.compose.material.icons.rounded.ContentPaste
import androidx.compose.material.icons.rounded.Keyboard
import androidx.compose.material.icons.rounded.PhotoCamera
import androidx.compose.material.icons.rounded.Search
import androidx.compose.material.icons.rounded.Wifi
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.TextButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun HomeScreen(viewModel: ClipBridgeViewModel) {
    val state by viewModel.uiState.collectAsStateWithLifecycle()
    Scaffold(
        topBar = { TopAppBar(title = { Text("ClipBridge") }) },
    ) { innerPadding ->
        Column(
            modifier = Modifier
                .fillMaxSize()
                .padding(innerPadding)
                .padding(horizontal = 16.dp)
                .verticalScroll(rememberScrollState()),
            verticalArrangement = Arrangement.spacedBy(12.dp),
        ) {
            StatusCard(status = state.statusLabel)

            PairingCodeCard(
                desktopFound = state.discovered.isNotEmpty(),
                onConfirm = viewModel::confirmPairing,
            )

            FeatureCard(
                icon = Icons.Rounded.ContentPaste,
                title = "Área de transferência sincronizada",
                description = "Textos e imagens copiados no PC aparecem aqui — e vice-versa.",
            )
            FeatureCard(
                icon = Icons.Rounded.PhotoCamera,
                title = "Prints em alta definição",
                description = "Receba capturas de tela do PC em resolução real, com zoom e pan.",
            )
            FeatureCard(
                icon = Icons.Rounded.Keyboard,
                title = "Digitação remota (Ctrl+F1)",
                description = "O PC digita o texto copiado como se fosse o teclado físico.",
            )

            DevicesCard(
                devices = state.discovered.map { it.name },
                onSearch = viewModel::startDiscovery,
            )

        }
    }
}

@Composable
private fun PairingCodeCard(
    desktopFound: Boolean,
    onConfirm: (String) -> Unit,
) {
    var code by rememberSaveable { mutableStateOf("") }

    Card(modifier = Modifier.fillMaxWidth()) {
        Column(modifier = Modifier.fillMaxWidth().padding(16.dp)) {
            Text("Parear dispositivo", style = MaterialTheme.typography.titleMedium, fontWeight = FontWeight.SemiBold)
            Text(
                if (desktopFound) {
                    "Desktop encontrado. Digite o código de 6 dígitos exibido no ClipBridge Desktop."
                } else {
                    "Procurando um ClipBridge Desktop na rede local."
                },
                style = MaterialTheme.typography.bodyMedium,
                modifier = Modifier.padding(top = 4.dp),
            )
            OutlinedTextField(
                value = code,
                onValueChange = { value ->
                    if (value.length <= 6 && value.all(Char::isDigit)) code = value
                },
                label = { Text("Código de autenticação") },
                singleLine = true,
                keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Number),
                modifier = Modifier.fillMaxWidth().padding(top = 12.dp),
            )
            Row(modifier = Modifier.fillMaxWidth().padding(top = 8.dp), horizontalArrangement = Arrangement.End) {
                TextButton(onClick = { onConfirm(code) }, enabled = desktopFound && code.length == 6) {
                    Text("Autenticar")
                }
            }
        }
    }
}

@Composable
private fun StatusCard(status: String) {
    Card(modifier = Modifier.fillMaxWidth()) {
        Row(
            modifier = Modifier.fillMaxWidth().padding(16.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            Icon(Icons.Rounded.Wifi, contentDescription = null, modifier = Modifier.size(28.dp))
            Column(modifier = Modifier.padding(start = 12.dp)) {
                Text("Conexão", style = MaterialTheme.typography.labelMedium)
                Text(status, style = MaterialTheme.typography.titleMedium, fontWeight = FontWeight.SemiBold)
            }
        }
    }
}

@Composable
private fun FeatureCard(icon: ImageVector, title: String, description: String) {
    Card(modifier = Modifier.fillMaxWidth()) {
        Row(modifier = Modifier.fillMaxWidth().padding(16.dp)) {
            Icon(icon, contentDescription = null, modifier = Modifier.size(24.dp))
            Column(modifier = Modifier.padding(start = 12.dp)) {
                Text(title, style = MaterialTheme.typography.titleSmall, fontWeight = FontWeight.SemiBold)
                Text(
                    description,
                    style = MaterialTheme.typography.bodyMedium,
                    modifier = Modifier.padding(top = 4.dp),
                )
            }
        }
    }
}

@Composable
private fun DevicesCard(devices: List<String>, onSearch: () -> Unit) {
    Card(modifier = Modifier.fillMaxWidth()) {
        Column(modifier = Modifier.fillMaxWidth().padding(16.dp)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Icon(Icons.Rounded.Computer, contentDescription = null, modifier = Modifier.size(24.dp))
                Text(
                    "Desktops na rede",
                    style = MaterialTheme.typography.titleSmall,
                    fontWeight = FontWeight.SemiBold,
                    modifier = Modifier.padding(start = 12.dp),
                )
            }

            if (devices.isEmpty()) {
                Text(
                    "Nenhum desktop encontrado ainda.",
                    style = MaterialTheme.typography.bodyMedium,
                    modifier = Modifier.padding(top = 8.dp),
                )
            } else {
                devices.forEach { name ->
                    Text(
                        "• $name",
                        style = MaterialTheme.typography.bodyMedium,
                        modifier = Modifier.padding(top = 4.dp),
                    )
                }
            }

            Button(onClick = onSearch, modifier = Modifier.padding(top = 12.dp)) {
                Icon(Icons.Rounded.Search, contentDescription = null, modifier = Modifier.size(18.dp))
                Text("Procurar", modifier = Modifier.padding(start = 8.dp))
            }
        }
    }
}
