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
    private readonly ClipBridgeServer _server = new();
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
        }
        catch (Exception ex)
        {
            Log($"Falha ao iniciar o servidor: {ex.Message}");
        }

        Hide();
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

    private void GeneratePairingInvitation()
    {
        var invitation = _server.BeginPairing();
        PairingCodeText.Text = invitation.PairingCode;
        _trayIcon?.SetPairingCode(invitation.PairingCode);
        Log("Novo código de pareamento gerado.");
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
