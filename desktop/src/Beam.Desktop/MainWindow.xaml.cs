using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using Beam.Core.Abstractions;
using Beam.Core.Net;
using Beam.Core.Protocol;
using Beam.Core.Security;
using Beam.Desktop.Services;
using Wpf.Ui.Controls;

namespace Beam.Desktop;

/// <summary>
/// Janela principal. Registra os atalhos globais e conecta os serviços de
/// captura de tela, digitação simulada e área de transferência.
/// </summary>
public partial class MainWindow : FluentWindow
{
    // Virtual-Key codes (Win32)
    private const uint VkF = 0x46;
    private const uint VkF1 = 0x70;
    private const int VkLControl = 0xA2;
    private const int VkRControl = 0xA3;

    private readonly Win32HotkeyService _hotkeys = new();
    private readonly WindowsScreenCaptureService _screenCapture = new();
    private readonly WindowsKeyboardTypingService _typing = new();
    private readonly WindowsClipboardService _clipboard = new();
    // Vínculos de confiança em disco (DPAPI): o celular reconecta sozinho, sem
    // novo código, enquanto a janela de 72h desde a última conexão não vencer.
    private readonly ClipBridgeServer _server = new(
        trustStore: new FileTrustedDeviceStore(
            FileTrustedDeviceStore.DefaultPath,
            new DpapiSecretProtector()));
    private readonly Dictionary<string, byte[]> _receivedBlobs = new(StringComparer.OrdinalIgnoreCase);
    // Dedup por conteúdo (última barreira contra loop de sync): texto normalizado
    // (fins de linha) e hash perceptual de imagem. Conteúdo igual ao último
    // sincronizado — em qualquer direção — nunca é reenviado.
    private string? _lastSyncedText;
    private ulong? _lastSyncedImageHash;
    private TrayIconService? _trayIcon;
    private bool _isExiting;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Closing += OnClosing;
        Closed += OnClosed;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _trayIcon = new TrayIconService(this);
        _trayIcon.ExitRequested += OnExitRequested;
        Dispatcher.BeginInvoke(
            new Action(() => _trayIcon?.Show()),
            DispatcherPriority.ApplicationIdle);
        try
        {
            WindowsStartupService.EnsureRegistered();
        }
        catch (Exception ex)
        {
            Log($"Não foi possível registrar a inicialização com o Windows: {ex.Message}");
        }

        try
        {
            // Ctrl + F  → captura de tela em segundo plano
            _hotkeys.Register(
                HotkeyModifiers.Control | HotkeyModifiers.NoRepeat, VkF, CaptureScreenshot);

            // Ctrl + F1 → digita o texto copiado
            _hotkeys.Register(
                HotkeyModifiers.Control | HotkeyModifiers.NoRepeat, VkF1, QueueClipboardTyping);

            Log("Atalhos registrados: Ctrl+F (captura), Ctrl+F1 (digitar texto).");
        }
        catch (Exception ex)
        {
            Log($"Falha ao registrar atalhos: {ex.Message}");
        }

        _clipboard.StartWatching();
        _clipboard.ClipboardChanged += OnClipboardChanged;
        _server.MessageReceived += OnMessageReceived;
        _server.SecureSessionEstablished += OnSecureSessionEstablished;
        _server.ClientConnectionChanged += OnClientConnectionChanged;
        _server.BlobCompleted += OnBlobCompleted;
        Log("Observando a área de transferência local.");

        try
        {
            _server.Start();
            StatusIcon.Symbol = SymbolRegular.PlugConnected24;
            StatusText.Text = "Servidor pronto para pareamento";
            GeneratePairingInvitation();
            ShowConnectionEndpoints();
            ShowTrustedDevices();
            _ = RefreshFirewallStatusAsync();
            StartupLog.Write(
                $"Servidor iniciado na porta {_server.Port}; endereços: {string.Join(", ", _server.ConnectionEndpoints)}");
        }
        catch (Exception ex)
        {
            Log($"Falha ao iniciar o servidor: {ex.Message}");
            StartupLog.Write($"Falha ao iniciar o servidor: {ex}");
            StatusIcon.Symbol = SymbolRegular.ErrorCircle24;
            StatusText.Text = "Falha ao iniciar o servidor — veja a atividade abaixo";
            ShowWindow();
            return;
        }

        // Some para a bandeja apenas quando já há celular pareado. Sem nenhum
        // vínculo não há o que fazer em segundo plano, e esconder a janela deixaria
        // o usuário sem o código, sem o endereço e sem o aviso do firewall.
        if (_server.TrustedDeviceCount == 0)
        {
            ShowWindow();
        }
        else
        {
            Hide();
        }
    }

    private void ShowWindow()
    {
        Show();
        WindowState = WindowState.Normal;
        ShowInTaskbar = true;
        Activate();
    }

    /// <summary>Exibe os IPs da LAN — o celular usa um deles na conexão manual.</summary>
    private void ShowConnectionEndpoints()
    {
        var endpoints = _server.ConnectionEndpoints;
        if (endpoints.Count == 0)
        {
            AddressText.Text = "Sem rede — conecte o PC à mesma Wi-Fi do celular";
            Log("Nenhuma interface de rede ativa: o celular não tem como alcançar este PC.");
            return;
        }

        AddressText.Text = string.Join("\n", endpoints);
        Log($"Escutando em {string.Join(", ", endpoints)}.");
    }

    private async Task RefreshFirewallStatusAsync()
    {
        var (allowed, blocked) = await Task.Run(() => (
            FirewallService.AreRulesPresent(_server.Port, _server.DiscoveryPort),
            FirewallService.HasBlockingRule()));

        FirewallIcon.Symbol = allowed ? SymbolRegular.ShieldCheckmark24 : SymbolRegular.ShieldError24;
        FirewallText.Text = (allowed, blocked) switch
        {
            (true, _) => "O Firewall do Windows já permite a conexão do celular nesta rede.",
            // Bloqueio vence permissão: enquanto essa regra existir, nenhuma regra
            // de porta faz efeito — daí a mensagem separada.
            (false, true) => "O Firewall do Windows tem uma regra que BLOQUEIA o Beam — ela costuma ser criada " +
                             "ao recusar o aviso \"Permitir acesso?\" na primeira execução, e tem prioridade sobre " +
                             "qualquer permissão. Liberar remove esse bloqueio e cria as regras de entrada.",
            (false, false) => "O Firewall do Windows pode estar bloqueando a conexão do celular. " +
                              "Liberar cria uma regra de entrada para as portas do Beam (pede confirmação do Windows).",
        };
        FirewallButton.IsEnabled = !allowed;
    }

    private async void OnAllowFirewallClick(object sender, RoutedEventArgs e)
    {
        FirewallButton.IsEnabled = false;
        var created = await Task.Run(() => FirewallService.TryCreateRules(
            _server.Port, _server.DiscoveryPort, ProtocolInfo.AnnouncePort));

        if (created)
        {
            Log("Regras de firewall criadas: o celular já pode conectar por esta rede.");
        }
        else
        {
            Log("Não foi possível criar as regras (elevação recusada). " +
                $"Rode num terminal como administrador: {FirewallService.ManualCommand(_server.Port, _server.DiscoveryPort, ProtocolInfo.AnnouncePort)}");
        }

        await RefreshFirewallStatusAsync();
    }

    private void OnClipboardChanged(object? sender, ClipboardContent content)
    {
        if (_server.SecureClientCount == 0)
        {
            return;
        }

        switch (content.Kind)
        {
            case ClipboardKind.Text when !string.IsNullOrEmpty(content.Text):
                var normalized = ClipboardTextUtil.Normalize(content.Text);
                if (normalized == _lastSyncedText)
                {
                    return; // eco de conteúdo já sincronizado
                }

                _lastSyncedText = normalized;
                _ = SendClipboardToMobileAsync(content.Text);
                break;

            case ClipboardKind.Image when content.ImageBytes is not null:
                ulong hash;
                try
                {
                    hash = ImageFingerprint.Compute(content.ImageBytes);
                }
                catch (Exception)
                {
                    return; // imagem indecodificável: não sincroniza
                }

                if (_lastSyncedImageHash is ulong last && ImageFingerprint.IsSameImage(hash, last))
                {
                    return; // eco de imagem já sincronizada (sobrevive à recodificação)
                }

                _lastSyncedImageHash = hash;
                _ = SendImageToMobileAsync(
                    content.ImageBytes,
                    content.Width,
                    content.Height,
                    MessageType.ClipboardImage,
                    static (blobId, width, height) => Envelope.Create(
                        MessageType.ClipboardImage,
                        new ClipboardImagePayload(blobId, "image/png", width, height)));
                break;
        }
    }

    private async Task SendClipboardToMobileAsync(string text)
    {
        if (_server.SecureClientCount == 0)
        {
            return;
        }

        try
        {
            await _server.BroadcastSecureAsync(
                Envelope.Create(MessageType.ClipboardText, new ClipboardTextPayload(text)));
            Log($"Texto enviado ao celular ({text.Length} caracteres).");
        }
        catch (Exception ex)
        {
            Log($"Falha ao enviar texto ao celular: {ex.Message}");
        }
    }

    private async Task SendImageToMobileAsync(
        byte[] pngBytes,
        int width,
        int height,
        string messageType,
        Func<string, int, int, Envelope> metadataFactory)
    {
        if (_server.SecureClientCount == 0)
        {
            return;
        }

        try
        {
            await _server.BroadcastBlobAsync(
                pngBytes,
                blobId => metadataFactory(blobId, width, height));
            var label = messageType == MessageType.Screenshot ? "Screenshot" : "Imagem";
            Log($"{label} enviado ao celular ({width}×{height}, {pngBytes.Length} bytes).");
        }
        catch (Exception ex)
        {
            Log($"Falha ao enviar imagem ao celular: {ex.Message}");
        }
    }

    private void OnBlobCompleted(object? sender, BlobTransferCompleted completed)
    {
        lock (_receivedBlobs)
        {
            _receivedBlobs[completed.BlobId] = completed.Data;
        }
    }

    private void OnMessageReceived(object? sender, Envelope envelope)
    {
        switch (envelope.Type)
        {
            case MessageType.ClipboardText:
                HandleIncomingText(envelope);
                break;
            case MessageType.ClipboardImage:
                HandleIncomingImage(envelope);
                break;
        }
    }

    private void HandleIncomingText(Envelope envelope)
    {
        var payload = envelope.PayloadAs<ClipboardTextPayload>();
        if (payload?.Text is null)
        {
            return;
        }

        Dispatcher.Invoke(() =>
        {
            _lastSyncedText = ClipboardTextUtil.Normalize(payload.Text);
            _clipboard.SetContent(ClipboardContent.FromText(ClipboardTextUtil.ToWindowsLineEndings(payload.Text)));
            Log($"Texto recebido do celular ({payload.Text.Length} caracteres).");
        });
    }

    private void HandleIncomingImage(Envelope envelope)
    {
        var payload = envelope.PayloadAs<ClipboardImagePayload>();
        if (payload?.BlobId is null)
        {
            return;
        }

        byte[]? pngBytes;
        lock (_receivedBlobs)
        {
            // Consome o blob (metadados chegam sempre após blob.end, em ordem),
            // liberando a memória em vez de acumular imagens ao longo da sessão.
            _receivedBlobs.Remove(payload.BlobId, out pngBytes);
        }

        if (pngBytes is null)
        {
            return;
        }

        Dispatcher.Invoke(() =>
        {
            try
            {
                _lastSyncedImageHash = ImageFingerprint.Compute(pngBytes);
            }
            catch (Exception)
            {
                // Sem hash, o anti-eco por janela de tempo continua cobrindo.
            }

            _clipboard.SetContent(ClipboardContent.FromImage(pngBytes, payload.Width, payload.Height));
            Log($"Imagem recebida do celular ({payload.Width}×{payload.Height}).");
        });
    }

    private void OnSecureSessionEstablished(object? sender, EventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            StatusIcon.Symbol = SymbolRegular.CheckmarkCircle24;
            StatusText.Text = "✓ Dispositivo pareado — sincronização ativa";
            Log("Sessão segura estabelecida. Sync de área de transferência ativo.");
            ShowTrustedDevices();
        });
    }

    private void OnClientConnectionChanged(object? sender, bool connected)
    {
        Dispatcher.Invoke(() =>
        {
            if (connected)
            {
                StatusIcon.Symbol = SymbolRegular.PlugConnected24;
                StatusText.Text = "Dispositivo conectado — digite o código no celular";
                Log("Dispositivo conectou. Aguardando o código de pareamento.");
            }
            else if (_server.SecureClientCount == 0)
            {
                StatusIcon.Symbol = SymbolRegular.PlugDisconnected24;
                StatusText.Text = "Aguardando dispositivo — servidor pronto";
                Log("Dispositivo desconectou.");
            }
        });
    }

    private void OnCaptureClick(object sender, RoutedEventArgs e) => CaptureScreenshot();

    private void OnTypeClick(object sender, RoutedEventArgs e)
    {
        Log("Digitando em 3s — clique no campo de destino…");
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            TypeClipboardText();
        };
        timer.Start();
    }

    private void QueueClipboardTyping()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(25) };
        timer.Tick += (_, _) =>
        {
            if (IsControlPressed())
            {
                return;
            }

            timer.Stop();
            TypeClipboardText();
        };
        timer.Start();
    }

    private static bool IsControlPressed() =>
        (GetAsyncKeyState(VkLControl) & 0x8000) != 0 ||
        (GetAsyncKeyState(VkRControl) & 0x8000) != 0;

    private void OnRefreshPairingClick(object sender, RoutedEventArgs e) => GeneratePairingInvitation();

    private async void OnRevokeTrustClick(object sender, RoutedEventArgs e)
    {
        await _server.RevokeTrustedDevicesAsync();
        Log("Reconexão automática revogada: o celular precisará de um novo código.");
        GeneratePairingInvitation();
        ShowTrustedDevices();
    }

    private void GeneratePairingInvitation()
    {
        var invitation = _server.BeginPairing();
        PairingCodeText.Text = invitation.PairingCode;
        _trayIcon?.SetPairingCode(invitation.PairingCode);
        Log("Novo código de pareamento gerado.");
    }

    /// <summary>Quantos celulares hoje reconectam sozinhos, e por quanto tempo.</summary>
    private void ShowTrustedDevices()
    {
        var count = _server.TrustedDeviceCount;
        RevokeButton.IsEnabled = count > 0;
        TrustText.Text = count switch
        {
            0 => "Nenhum celular com reconexão automática.",
            1 => "1 celular reconecta sozinho por até 72h após a última conexão.",
            _ => $"{count} celulares reconectam sozinhos por até 72h após a última conexão.",
        };
    }

    private void CaptureScreenshot()
    {
        try
        {
            var result = _screenCapture.CaptureAllScreens();

            var folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Beam");
            Directory.CreateDirectory(folder);

            var file = Path.Combine(folder, $"screenshot-{DateTime.Now:yyyyMMdd-HHmmss}.png");
            File.WriteAllBytes(file, result.PngBytes);

            Log($"Captura salva: {result.Width}×{result.Height} · {result.MonitorCount} monitor(es) → {file}");
            try
            {
                // Registra o hash para nunca reenviar este screenshot se ele voltar
                // ao clipboard por qualquer caminho (eco, sync externo etc.).
                _lastSyncedImageHash = ImageFingerprint.Compute(result.PngBytes);
            }
            catch (Exception)
            {
                // Sem hash, a janela de tempo continua cobrindo.
            }

            var monitors = result.MonitorCount;
            _ = SendImageToMobileAsync(
                result.PngBytes,
                result.Width,
                result.Height,
                MessageType.Screenshot,
                (blobId, width, height) => Envelope.Create(
                    MessageType.Screenshot,
                    new ScreenshotPayload(blobId, "image/png", width, height, monitors)));
        }
        catch (Exception ex)
        {
            Log($"Falha na captura: {ex.Message}");
        }
    }

    private void TypeClipboardText()
    {
        var content = _clipboard.GetCurrent();
        if (content.Kind == ClipboardKind.Text && !string.IsNullOrEmpty(content.Text))
        {
            try
            {
                _typing.TypeText(content.Text);
                Log($"Digitado: {content.Text.Length} caracteres.");
            }
            catch (Exception ex)
            {
                Log($"Falha ao digitar o texto: {ex.Message}");
            }
        }
        else
        {
            Log("Nada de texto na área de transferência para digitar.");
        }
    }

    private void Log(string message)
    {
        Dispatcher.Invoke(() =>
        {
            LogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
            LogBox.ScrollToEnd();
        });
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_isExiting)
        {
            return;
        }

        e.Cancel = true;
        Hide();
    }

    private void OnExitRequested(object? sender, EventArgs e)
    {
        _isExiting = true;
        System.Windows.Application.Current.Shutdown();
    }

    private async void OnClosed(object? sender, EventArgs e)
    {
        if (_trayIcon is not null)
        {
            _trayIcon.ExitRequested -= OnExitRequested;
        }

        _trayIcon?.Dispose();
        _clipboard.ClipboardChanged -= OnClipboardChanged;
        _server.MessageReceived -= OnMessageReceived;
        _server.SecureSessionEstablished -= OnSecureSessionEstablished;
        _server.ClientConnectionChanged -= OnClientConnectionChanged;
        _server.BlobCompleted -= OnBlobCompleted;
        _clipboard.Dispose();
        _hotkeys.Dispose();
        await _server.DisposeAsync();
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);
}
