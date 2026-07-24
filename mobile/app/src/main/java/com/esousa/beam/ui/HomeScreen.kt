package com.esousa.beam.ui

import android.content.Intent
import androidx.compose.foundation.Image
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
import androidx.compose.material.icons.rounded.CheckCircle
import androidx.compose.material.icons.rounded.Computer
import androidx.compose.material.icons.rounded.ContentPaste
import androidx.compose.material.icons.rounded.Keyboard
import androidx.compose.material.icons.rounded.PhotoCamera
import androidx.compose.material.icons.rounded.Search
import androidx.compose.material.icons.rounded.Settings
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
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.Alignment
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.unit.dp
import com.esousa.beam.session.ReceivedImage
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import android.graphics.BitmapFactory
import android.provider.Settings as AndroidSettings

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun HomeScreen(viewModel: ClipBridgeViewModel) {
    val state by viewModel.uiState.collectAsStateWithLifecycle()
    val context = LocalContext.current
    var showPairingForm by rememberSaveable { mutableStateOf(false) }
    Scaffold(
        topBar = { TopAppBar(title = { Text("Beam") }) },
    ) { innerPadding ->
        Column(
            modifier = Modifier
                .fillMaxSize()
                .padding(innerPadding)
                .padding(horizontal = 16.dp)
                .verticalScroll(rememberScrollState()),
            verticalArrangement = Arrangement.spacedBy(12.dp),
        ) {
            StatusCard(status = state.statusLabel, activity = state.lastActivity, isSecure = state.isSecure)
            InputMethodCard(
                isSelected = state.isBeamKeyboardSelected,
                onOpenSettings = {
                    context.startActivity(Intent(AndroidSettings.ACTION_INPUT_METHOD_SETTINGS))
                },
            )

            when {
                state.isSecure -> ConnectedCard(trustExpiresAt = state.trustExpiresAt)
                // Com vínculo válido a reconexão é automática; o card de código só
                // volta se o usuário pedir (ou se o desktop recusar o vínculo).
                state.isResuming && !showPairingForm -> ReconnectingCard(
                    trustExpiresAt = state.trustExpiresAt,
                    onPairWithCode = { showPairingForm = true },
                )
                else -> PairingCodeCard(
                    desktopFound = state.discovered.isNotEmpty(),
                    onConfirm = { code, manualAddress -> viewModel.confirmPairing(code, manualAddress) },
                )
            }

            state.latestImage?.let { image ->
                ImagePreviewCard(image = image)
            }

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
                title = "Digitação local (Ctrl+F1)",
                description = "No PC: digita o texto copiado como teclado físico. Atalho local por segurança — não é acionado pelo celular.",
            )

            DevicesCard(
                devices = state.discovered.map { "${it.host}:${it.port}" },
                onSearch = viewModel::startDiscovery,
            )

        }
    }
}

@Composable
private fun InputMethodCard(isSelected: Boolean, onOpenSettings: () -> Unit) {
    Card(modifier = Modifier.fillMaxWidth()) {
        Row(
            modifier = Modifier.fillMaxWidth().padding(16.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            Icon(
                Icons.Rounded.Keyboard,
                contentDescription = null,
                tint = if (isSelected) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.onSurfaceVariant,
                modifier = Modifier.size(28.dp),
            )
            Column(modifier = Modifier.weight(1f).padding(start = 12.dp)) {
                Text("Teclado Beam", style = MaterialTheme.typography.titleMedium, fontWeight = FontWeight.SemiBold)
                Text(
                    if (isSelected) "Ativo" else "Não selecionado",
                    style = MaterialTheme.typography.bodySmall,
                    modifier = Modifier.padding(top = 2.dp),
                )
            }
            Button(onClick = onOpenSettings) {
                Icon(Icons.Rounded.Settings, contentDescription = null, modifier = Modifier.size(18.dp))
                Text("Configurar", modifier = Modifier.padding(start = 8.dp))
            }
        }
    }
}

@Composable
private fun PairingCodeCard(
    desktopFound: Boolean,
    onConfirm: (String, String?) -> Unit,
) {
    var code by rememberSaveable { mutableStateOf("") }
    var manualAddress by rememberSaveable { mutableStateOf("") }
    var showManual by rememberSaveable { mutableStateOf(false) }

    Card(modifier = Modifier.fillMaxWidth()) {
        Column(modifier = Modifier.fillMaxWidth().padding(16.dp)) {
            Text("Parear dispositivo", style = MaterialTheme.typography.titleMedium, fontWeight = FontWeight.SemiBold)
            Text(
                if (desktopFound) {
                    "Desktop encontrado. Digite o código de 6 dígitos exibido no Beam Desktop."
                } else {
                    "Procurando um Beam Desktop na Wi-Fi. O PC precisa estar na mesma rede."
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

            // Escape para redes que filtram broadcast (isolamento de clientes,
            // Wi-Fi corporativa): o IP aparece na janela do Beam Desktop.
            if (showManual) {
                OutlinedTextField(
                    value = manualAddress,
                    onValueChange = { manualAddress = it },
                    label = { Text("IP do PC (ex.: 192.168.0.10)") },
                    singleLine = true,
                    modifier = Modifier.fillMaxWidth().padding(top = 8.dp),
                )
            }

            Row(
                modifier = Modifier.fillMaxWidth().padding(top = 8.dp),
                horizontalArrangement = Arrangement.SpaceBetween,
                verticalAlignment = Alignment.CenterVertically,
            ) {
                TextButton(onClick = { showManual = !showManual }) {
                    Text(if (showManual) "Usar busca automática" else "Informar IP manualmente")
                }
                TextButton(
                    onClick = { onConfirm(code, manualAddress.takeIf { showManual }) },
                    enabled = code.length == 6 &&
                        (desktopFound || (showManual && manualAddress.isNotBlank())),
                ) {
                    Text("Autenticar")
                }
            }
        }
    }
}

@Composable
private fun StatusCard(status: String, activity: String?, isSecure: Boolean) {
    val accent = if (isSecure) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.onSurfaceVariant
    Card(modifier = Modifier.fillMaxWidth()) {
        Row(
            modifier = Modifier.fillMaxWidth().padding(16.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            Icon(
                if (isSecure) Icons.Rounded.CheckCircle else Icons.Rounded.Wifi,
                contentDescription = null,
                tint = accent,
                modifier = Modifier.size(28.dp),
            )
            Column(modifier = Modifier.padding(start = 12.dp)) {
                Text("Conexão", style = MaterialTheme.typography.labelMedium)
                Text(status, style = MaterialTheme.typography.titleMedium, fontWeight = FontWeight.SemiBold, color = accent)
                if (activity != null) {
                    Text(
                        activity,
                        style = MaterialTheme.typography.bodySmall,
                        modifier = Modifier.padding(top = 4.dp),
                    )
                }
            }
        }
    }
}

@Composable
private fun ConnectedCard(trustExpiresAt: Long?) {
    Card(modifier = Modifier.fillMaxWidth()) {
        Row(
            modifier = Modifier.fillMaxWidth().padding(16.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            Icon(
                Icons.Rounded.CheckCircle,
                contentDescription = null,
                tint = MaterialTheme.colorScheme.primary,
                modifier = Modifier.size(28.dp),
            )
            Column(modifier = Modifier.padding(start = 12.dp)) {
                Text(
                    "Pareado com o desktop",
                    style = MaterialTheme.typography.titleMedium,
                    fontWeight = FontWeight.SemiBold,
                )
                Text(
                    "Sincronização de área de transferência ativa. Copie no PC ou no celular.",
                    style = MaterialTheme.typography.bodyMedium,
                    modifier = Modifier.padding(top = 4.dp),
                )
                trustExpiresAt?.let { expiry ->
                    Text(
                        "Reconexão automática por ${remainingTrust(expiry)}.",
                        style = MaterialTheme.typography.bodySmall,
                        modifier = Modifier.padding(top = 4.dp),
                    )
                }
            }
        }
    }
}

@Composable
private fun ReconnectingCard(trustExpiresAt: Long?, onPairWithCode: () -> Unit) {
    Card(modifier = Modifier.fillMaxWidth()) {
        Column(modifier = Modifier.fillMaxWidth().padding(16.dp)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Icon(Icons.Rounded.Wifi, contentDescription = null, modifier = Modifier.size(28.dp))
                Column(modifier = Modifier.padding(start = 12.dp)) {
                    Text(
                        "Reconectando automaticamente",
                        style = MaterialTheme.typography.titleMedium,
                        fontWeight = FontWeight.SemiBold,
                    )
                    Text(
                        trustExpiresAt?.let { "Vínculo válido por ${remainingTrust(it)} — sem precisar de código." }
                            ?: "Retomando a sessão anterior sem precisar de código.",
                        style = MaterialTheme.typography.bodyMedium,
                        modifier = Modifier.padding(top = 4.dp),
                    )
                }
            }
            Row(modifier = Modifier.fillMaxWidth().padding(top = 8.dp), horizontalArrangement = Arrangement.End) {
                TextButton(onClick = onPairWithCode) {
                    Text("Parear com código")
                }
            }
        }
    }
}

/** "12 h" ou "45 min" — o suficiente para o usuário saber quanto ainda dura. */
private fun remainingTrust(expiresAt: Long): String {
    val minutes = ((expiresAt - System.currentTimeMillis()) / 60_000).coerceAtLeast(0)
    return if (minutes >= 60) "${minutes / 60} h" else "$minutes min"
}

@Composable
private fun ImagePreviewCard(image: ReceivedImage) {
    val bitmap = remember(image.pngBytes) {
        BitmapFactory.decodeByteArray(image.pngBytes, 0, image.pngBytes.size)?.asImageBitmap()
    }

    Card(modifier = Modifier.fillMaxWidth()) {
        Column(modifier = Modifier.fillMaxWidth().padding(16.dp)) {
            Text(image.label, style = MaterialTheme.typography.titleSmall, fontWeight = FontWeight.SemiBold)
            Text(
                "${image.width}×${image.height}",
                style = MaterialTheme.typography.bodySmall,
                modifier = Modifier.padding(top = 2.dp),
            )
            if (bitmap != null) {
                ZoomableImage(
                    bitmap = bitmap,
                    contentDescription = image.label,
                    modifier = Modifier.padding(top = 8.dp),
                )
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
