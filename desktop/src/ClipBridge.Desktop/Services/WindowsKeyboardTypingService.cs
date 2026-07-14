using System.Runtime.InteropServices;
using ClipBridge.Core.Abstractions;

namespace ClipBridge.Desktop.Services;

/// <summary>
/// Digita texto simulando o teclado físico via <c>SendInput</c> (modo Unicode).
/// Útil para "colar" onde <c>Ctrl+V</c> é bloqueado (bancos, terminais, etc.).
/// </summary>
public sealed class WindowsKeyboardTypingService : IKeyboardTypingService
{
    private const int InputKeyboard = 1;
    private const uint KeyEventUnicode = 0x0004;
    private const uint KeyEventKeyUp = 0x0002;
    private const ushort VkReturn = 0x0D;

    public void TypeText(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        var inputs = new List<INPUT>(text.Length * 2);

        foreach (var ch in text)
        {
            switch (ch)
            {
                case '\r':
                    continue; // ignora CR; o LF vira Enter
                case '\n':
                    inputs.Add(KeyboardVk(VkReturn, keyUp: false));
                    inputs.Add(KeyboardVk(VkReturn, keyUp: true));
                    break;
                default:
                    inputs.Add(KeyboardUnicode(ch, keyUp: false));
                    inputs.Add(KeyboardUnicode(ch, keyUp: true));
                    break;
            }
        }

        if (inputs.Count > 0)
        {
            SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<INPUT>());
        }
    }

    private static INPUT KeyboardUnicode(char ch, bool keyUp) => new()
    {
        type = InputKeyboard,
        u = new InputUnion
        {
            ki = new KEYBDINPUT
            {
                wVk = 0,
                wScan = ch,
                dwFlags = KeyEventUnicode | (keyUp ? KeyEventKeyUp : 0),
                time = 0,
                dwExtraInfo = IntPtr.Zero,
            },
        },
    };

    private static INPUT KeyboardVk(ushort vk, bool keyUp) => new()
    {
        type = InputKeyboard,
        u = new InputUnion
        {
            ki = new KEYBDINPUT
            {
                wVk = vk,
                wScan = 0,
                dwFlags = keyUp ? KeyEventKeyUp : 0,
                time = 0,
                dwExtraInfo = IntPtr.Zero,
            },
        },
    };

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion u;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }
}
