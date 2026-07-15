using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Beam.Core.Abstractions;

namespace Beam.Desktop.Services;

/// <summary>
/// Captura a tela em resolução física real (alta definição) via GDI <c>BitBlt</c>
/// e codifica em PNG sem perda. Com DPI awareness Per-Monitor v2 (ver app.manifest),
/// as dimensões correspondem aos pixels reais dos monitores.
/// </summary>
public sealed class WindowsScreenCaptureService : IScreenCaptureService
{
    private const int SmXVirtualScreen = 76;
    private const int SmYVirtualScreen = 77;
    private const int SmCxVirtualScreen = 78;
    private const int SmCyVirtualScreen = 79;
    private const int SmCMonitors = 80;

    private const uint SrcCopy = 0x00CC0020;
    private const uint CaptureBlt = 0x40000000; // inclui janelas em camadas (layered)

    public CaptureResult CaptureAllScreens()
    {
        var x = GetSystemMetrics(SmXVirtualScreen);
        var y = GetSystemMetrics(SmYVirtualScreen);
        var width = GetSystemMetrics(SmCxVirtualScreen);
        var height = GetSystemMetrics(SmCyVirtualScreen);
        var monitors = GetSystemMetrics(SmCMonitors);
        return Capture(x, y, width, height, monitors);
    }

    public CaptureResult CapturePrimaryScreen()
    {
        var width = (int)SystemParameters.PrimaryScreenWidth;
        var height = (int)SystemParameters.PrimaryScreenHeight;
        return Capture(0, 0, width, height, 1);
    }

    private static CaptureResult Capture(int x, int y, int width, int height, int monitors)
    {
        var screenDc = GetDC(IntPtr.Zero);
        var memDc = CreateCompatibleDC(screenDc);
        var bitmap = CreateCompatibleBitmap(screenDc, width, height);
        var previous = SelectObject(memDc, bitmap);

        try
        {
            BitBlt(memDc, 0, 0, width, height, screenDc, x, y, SrcCopy | CaptureBlt);

            var source = Imaging.CreateBitmapSourceFromHBitmap(
                bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(source));

            using var stream = new MemoryStream();
            encoder.Save(stream);
            return new CaptureResult(stream.ToArray(), width, height, monitors);
        }
        finally
        {
            SelectObject(memDc, previous);
            DeleteObject(bitmap);
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDc);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);

    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hDc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr hDc, int width, int height);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hDc, IntPtr hObject);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr hObject);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hDc);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(
        IntPtr hDest, int xDest, int yDest, int width, int height,
        IntPtr hSrc, int xSrc, int ySrc, uint rop);
}
