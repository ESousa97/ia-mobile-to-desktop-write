using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Beam.Core.Abstractions;
using Clipboard = System.Windows.Clipboard;

namespace Beam.Desktop.Services;

/// <summary>
/// Observa e manipula a área de transferência do Windows (texto e imagens).
/// Usa <c>AddClipboardFormatListener</c> para detectar mudanças em segundo plano.
/// </summary>
public sealed class WindowsClipboardService : IClipboardService, IDisposable
{
    private const int WmClipboardUpdate = 0x031D;
    private static readonly IntPtr HwndMessage = new(-3);

    private HwndSource? _source;
    private bool _watching;

    public event EventHandler<ClipboardContent>? ClipboardChanged;

    public ClipboardContent GetCurrent()
    {
        try
        {
            if (Clipboard.ContainsImage())
            {
                var image = Clipboard.GetImage();
                if (image is not null)
                {
                    var png = EncodePng(image);
                    return ClipboardContent.FromImage(png, image.PixelWidth, image.PixelHeight);
                }
            }

            if (Clipboard.ContainsText())
            {
                return ClipboardContent.FromText(Clipboard.GetText());
            }
        }
        catch (COMException)
        {
            // A área de transferência pode estar temporariamente bloqueada por outro processo.
        }

        return ClipboardContent.Empty;
    }

    public void SetContent(ClipboardContent content)
    {
        try
        {
            switch (content.Kind)
            {
                case ClipboardKind.Text when content.Text is not null:
                    Clipboard.SetText(content.Text);
                    break;
                case ClipboardKind.Image when content.ImageBytes is not null:
                    Clipboard.SetImage(DecodePng(content.ImageBytes));
                    break;
            }
        }
        catch (COMException)
        {
            // A área de transferência pode estar temporariamente bloqueada por outro processo.
        }
    }

    public void StartWatching()
    {
        if (_watching)
        {
            return;
        }

        var parameters = new HwndSourceParameters("ClipBridgeClipboardWindow")
        {
            Width = 0,
            Height = 0,
            ParentWindow = HwndMessage,
        };

        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);
        AddClipboardFormatListener(_source.Handle);
        _watching = true;
    }

    public void StopWatching()
    {
        if (!_watching || _source is null)
        {
            return;
        }

        RemoveClipboardFormatListener(_source.Handle);
        _source.RemoveHook(WndProc);
        _source.Dispose();
        _source = null;
        _watching = false;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmClipboardUpdate)
        {
            return IntPtr.Zero;
        }

        var content = GetCurrent();
        if (content.Kind != ClipboardKind.None)
        {
            ClipboardChanged?.Invoke(this, content);
        }

        return IntPtr.Zero;
    }

    private static byte[] EncodePng(BitmapSource source)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static BitmapSource DecodePng(byte[] pngBytes)
    {
        using var stream = new MemoryStream(pngBytes);
        var decoder = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        frame.Freeze();
        return frame;
    }

    public void Dispose() => StopWatching();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
}
