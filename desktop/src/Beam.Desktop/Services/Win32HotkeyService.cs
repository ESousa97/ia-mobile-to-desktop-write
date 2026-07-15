using System.Runtime.InteropServices;
using System.Windows.Interop;
using Beam.Core.Abstractions;

namespace Beam.Desktop.Services;

/// <summary>
/// Registra atalhos globais via <c>RegisterHotKey</c> do Win32, funcionando em
/// segundo plano (mesmo sem a janela em foco). Usa uma janela "message-only"
/// para receber as mensagens <c>WM_HOTKEY</c>.
/// </summary>
public sealed class Win32HotkeyService : IHotkeyService
{
    private const int WmHotkey = 0x0312;
    private static readonly IntPtr HwndMessage = new(-3);

    private readonly HwndSource _source;
    private readonly Dictionary<int, Action> _callbacks = new();
    private int _nextId = 1;
    private bool _disposed;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    public Win32HotkeyService()
    {
        var parameters = new HwndSourceParameters("ClipBridgeHotkeyWindow")
        {
            Width = 0,
            Height = 0,
            ParentWindow = HwndMessage, // janela message-only
        };

        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);
    }

    public int Register(HotkeyModifiers modifiers, uint virtualKey, Action callback)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var id = _nextId++;
        if (!RegisterHotKey(_source.Handle, id, (uint)modifiers, virtualKey))
        {
            var error = Marshal.GetLastWin32Error();
            throw new InvalidOperationException(
                $"Não foi possível registrar o atalho (id={id}, erro Win32={error}). " +
                "Talvez já esteja em uso por outro aplicativo.");
        }

        _callbacks[id] = callback;
        return id;
    }

    public void Unregister(int id)
    {
        if (_callbacks.Remove(id))
        {
            UnregisterHotKey(_source.Handle, id);
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && _callbacks.TryGetValue(wParam.ToInt32(), out var callback))
        {
            callback();
            handled = true;
        }

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        foreach (var id in _callbacks.Keys)
        {
            UnregisterHotKey(_source.Handle, id);
        }

        _callbacks.Clear();
        _source.RemoveHook(WndProc);
        _source.Dispose();
        _disposed = true;
    }
}
