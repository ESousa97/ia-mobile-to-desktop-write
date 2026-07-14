using System.IO;
using System.Windows;
using System.Windows.Threading;
using ClipBridge.Core.Abstractions;
using ClipBridge.Core.Net;
using ClipBridge.Core.Security;
using ClipBridge.Desktop.Services;
using Wpf.Ui.Controls;

namespace ClipBridge.Desktop;

/// <summary>
/// Janela principal. Registra os atalhos globais e conecta os serviços de
/// captura de tela, digitação simulada e área de transferência.
/// </summary>
public partial class MainWindow : FluentWindow
{
    // Virtual-Key codes (Win32)
    private const uint VkF = 0x46;
    private const uint VkF1 = 0x70;

    private readonly Win32HotkeyService _hotkeys = new();
    private readonly WindowsScreenCaptureService _screenCapture = new();
    private readonly WindowsKeyboardTypingService _typing = new();
    private readonly WindowsClipboardService _clipboard = new();
    private readonly ClipBridgeServer _server = new();

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            // Ctrl + F  → captura de tela em segundo plano
            _hotkeys.Register(
                HotkeyModifiers.Control | HotkeyModifiers.NoRepeat, VkF, CaptureScreenshot);

            // Ctrl + F1 → digita o texto copiado
            _hotkeys.Register(
                HotkeyModifiers.Control | HotkeyModifiers.NoRepeat, VkF1, TypeClipboardText);

            Log("Atalhos registrados: Ctrl+F (captura), Ctrl+F1 (digitar texto).");
        }
        catch (Exception ex)
        {
            Log($"Falha ao registrar atalhos: {ex.Message}");
        }

        _clipboard.StartWatching();
        _clipboard.ClipboardChanged += OnClipboardChanged;
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
    }

    private void OnClipboardChanged(object? sender, ClipboardContent content)
    {
        var description = content.Kind switch
        {
            ClipboardKind.Text => $"texto ({content.Text?.Length ?? 0} caracteres)",
            ClipboardKind.Image => $"imagem ({content.Width}×{content.Height})",
            _ => "vazio",
        };
        Log($"Área de transferência mudou: {description}. (o envio ao celular entra no roadmap)");
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

    private void OnRefreshPairingClick(object sender, RoutedEventArgs e) => GeneratePairingInvitation();

    private void GeneratePairingInvitation()
    {
        var invitation = _server.BeginPairing();
        PairingCodeText.Text = invitation.PairingCode;
        Log("Novo código de pareamento gerado.");
    }

    private void CaptureScreenshot()
    {
        try
        {
            var result = _screenCapture.CaptureAllScreens();

            var folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "ClipBridge");
            Directory.CreateDirectory(folder);

            var file = Path.Combine(folder, $"screenshot-{DateTime.Now:yyyyMMdd-HHmmss}.png");
            File.WriteAllBytes(file, result.PngBytes);

            Log($"Captura salva: {result.Width}×{result.Height} · {result.MonitorCount} monitor(es) → {file}");
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
            _typing.TypeText(content.Text);
            Log($"Digitado: {content.Text.Length} caracteres.");
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

    private async void OnClosed(object? sender, EventArgs e)
    {
        _clipboard.ClipboardChanged -= OnClipboardChanged;
        _clipboard.Dispose();
        _hotkeys.Dispose();
        await _server.DisposeAsync();
    }
}
