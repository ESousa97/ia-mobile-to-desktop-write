using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using Beam.Core.Abstractions;

namespace Beam.Desktop.Services;

/// <summary>
/// Digita texto simulando o teclado físico via <c>SendInput</c> (modo Unicode).
/// Útil para "colar" onde <c>Ctrl+V</c> é bloqueado (bancos, terminais, etc.).
/// </summary>
public sealed class WindowsKeyboardTypingService : IKeyboardTypingService
{
    private const int InputKeyboard = 1;
    private const uint KeyEventUnicode = 0x0004;
    private const uint KeyEventKeyUp = 0x0002;
    private const ushort VkTab = 0x09;
    private const ushort VkReturn = 0x0D;
    /// <summary>
    /// Pausa entre eventos enviados ao Windows. Sem isso, lotes grandes de
    /// <c>SendInput</c> saturam a fila de entrada e o restante do texto vira
    /// lixo (muitos "." repetidos em campos de chat/navegador).
    /// </summary>
    private const int DelayBetweenElementsMs = 1;

    public void TypeText(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        var preparedText = ClipboardTextUtil.NormalizeForTyping(text);
        var inputs = new List<INPUT>(4);
        var textElements = StringInfo.GetTextElementEnumerator(preparedText);
        var typedElementCount = 0;

        while (textElements.MoveNext())
        {
            try
            {
                var textElement = textElements.GetTextElement();
                switch (textElement)
                {
                    case "\n":
                        AppendKeyStroke(inputs, VkReturn);
                        break;
                    case "\t":
                        AppendKeyStroke(inputs, VkTab);
                        break;
                    default:
                        AppendUnicodeTextElement(inputs, textElement);
                        break;
                }

                typedElementCount++;
                FlushInputs(inputs);
                if (DelayBetweenElementsMs > 0)
                {
                    Thread.Sleep(DelayBetweenElementsMs);
                }
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
            {
                throw new InvalidOperationException(
                    $"O Windows interrompeu a digitação após {typedElementCount} elementos de texto.",
                    ex);
            }
        }
    }

    private static void AppendUnicodeTextElement(List<INPUT> inputs, string textElement)
    {
        foreach (var codeUnit in textElement)
        {
            inputs.Add(KeyboardUnicode(codeUnit, keyUp: false));
            inputs.Add(KeyboardUnicode(codeUnit, keyUp: true));
        }
    }

    private static void AppendKeyStroke(List<INPUT> inputs, ushort virtualKey)
    {
        inputs.Add(KeyboardVk(virtualKey, keyUp: false));
        inputs.Add(KeyboardVk(virtualKey, keyUp: true));
    }

    private static void FlushInputs(List<INPUT> inputs)
    {
        if (inputs.Count == 0)
        {
            return;
        }

        // Envia pressionar e soltar separadamente. Alguns destinos perdem o
        // modo Unicode quando recebem muitos eventos no mesmo lote.
        foreach (var input in inputs)
        {
            SendInputs(new[] { input });
            if (DelayBetweenElementsMs > 0)
            {
                Thread.Sleep(DelayBetweenElementsMs);
            }
        }

        inputs.Clear();
    }

    private static void SendInputs(INPUT[] inputs)
    {
        var sentInputCount = 0;
        while (sentInputCount < inputs.Length)
        {
            var remaining = sentInputCount == 0 ? inputs : inputs[sentInputCount..];
            var sent = SendInput((uint)remaining.Length, remaining, Marshal.SizeOf<INPUT>());
            if (sent > 0)
            {
                sentInputCount += checked((int)sent);
                continue;
            }

            var error = Marshal.GetLastWin32Error();
            var detail = error == 0
                ? "A entrada foi bloqueada; verifique se o aplicativo de destino não está elevado."
                : new Win32Exception(error).Message;
            throw new InvalidOperationException(
                $"SendInput aceitou {sentInputCount} de {inputs.Length} eventos. {detail}");
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
