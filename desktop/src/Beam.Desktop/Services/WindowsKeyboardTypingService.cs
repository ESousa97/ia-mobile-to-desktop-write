using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Beam.Core.Abstractions;

namespace Beam.Desktop.Services;

/// <summary>
/// Digita texto simulando o teclado físico via <c>SendInput</c>.
/// Útil para "colar" onde <c>Ctrl+V</c> é bloqueado (bancos, terminais, etc.).
/// </summary>
/// <remarks>
/// A injeção acontece em três níveis, do mais fiel ao hardware para o menos:
/// <list type="number">
/// <item>scancode do layout da janela em foco (<c>KEYEVENTF_SCANCODE</c>);</item>
/// <item>tecla morta + letra base, para acentos que o layout não tem em uma
/// única tecla (á, ê, õ… em layouts como o ABNT2);</item>
/// <item><c>KEYEVENTF_UNICODE</c>, para o que não existe no layout (emoji, por
/// exemplo).</item>
/// </list>
/// O nível 1 existe porque clientes de sessão remota — Citrix Workspace, RDP,
/// VMware Horizon — leem o teclado no nível bruto e enviam <em>scancode</em>
/// pelo canal do protocolo. Um evento <c>KEYEVENTF_UNICODE</c> não tem scancode
/// (chega como <c>VK_PACKET</c>), então esses clientes não têm o que transmitir
/// e o texto simplesmente não aparece na sessão remota.
/// </remarks>
public sealed class WindowsKeyboardTypingService : IKeyboardTypingService
{
    private const int InputKeyboard = 1;
    private const uint KeyEventExtendedKey = 0x0001;
    private const uint KeyEventKeyUp = 0x0002;
    private const uint KeyEventUnicode = 0x0004;
    private const uint KeyEventScanCode = 0x0008;

    private const ushort VkShift = 0x10;
    private const ushort VkControl = 0x11;
    private const ushort VkMenu = 0x12;
    private const ushort VkTab = 0x09;
    private const ushort VkReturn = 0x0D;

    private const uint MapVkToVscEx = 0x04;

    // Estado de modificadores devolvido no byte alto de VkKeyScanEx.
    private const int ShiftStateShift = 1;
    private const int ShiftStateControl = 2;
    private const int ShiftStateAlt = 4;
    private const int ShiftStateUnsupported = ~(ShiftStateShift | ShiftStateControl | ShiftStateAlt);

    /// <summary>Scancode do Alt direito (AltGr), sempre estendido.</summary>
    private const ushort RightAltScanCode = 0x38;

    /// <summary>
    /// Pausa entre eventos enviados ao Windows. Sem isso, lotes grandes de
    /// <c>SendInput</c> saturam a fila de entrada e o restante do texto vira
    /// lixo (muitos "." repetidos em campos de chat/navegador).
    /// </summary>
    private const int DelayBetweenElementsMs = 1;

    /// <summary>
    /// Acentos que a maioria dos layouts só produz por tecla morta. O valor é o
    /// caractere "espaçador" correspondente, que existe como tecla no layout.
    /// </summary>
    private static readonly Dictionary<char, char> DeadKeySpacingChars = new()
    {
        ['̀'] = '`', // crase
        ['́'] = '´', // agudo
        ['̂'] = '^', // circunflexo
        ['̃'] = '~', // til
        ['̈'] = '¨', // trema
    };

    public void TypeText(string text) => TypeTextWithReport(text);

    /// <summary>
    /// Digita o texto e informa por qual nível cada elemento passou. Um valor
    /// alto em <see cref="TypingReport.UnicodeFallbacks"/> indica texto que não
    /// chega em sessões remotas (Citrix/RDP), porque só existe como Unicode.
    /// </summary>
    public TypingReport TypeTextWithReport(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return default;
        }

        var preparedText = ClipboardTextUtil.NormalizeForTyping(text);
        var layout = ForegroundKeyboardLayout();
        var batch = new List<INPUT>(8);
        var textElements = StringInfo.GetTextElementEnumerator(preparedText);
        var typedElementCount = 0;
        var unicodeFallbackCount = 0;

        while (textElements.MoveNext())
        {
            try
            {
                var textElement = textElements.GetTextElement();
                batch.Clear();
                var usedUnicode = false;

                switch (textElement)
                {
                    case "\n":
                        AppendKeyStroke(batch, VkReturn, layout);
                        break;
                    case "\t":
                        AppendKeyStroke(batch, VkTab, layout);
                        break;
                    default:
                        if (!TryAppendScanCodeElement(batch, textElement, layout) &&
                            !TryAppendDeadKeyElement(batch, textElement, layout))
                        {
                            batch.Clear();
                            AppendUnicodeTextElement(batch, textElement);
                            usedUnicode = true;
                        }

                        break;
                }

                FlushInputs(batch, oneEventPerCall: usedUnicode);
                typedElementCount++;
                if (usedUnicode)
                {
                    unicodeFallbackCount++;
                }

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

        return new TypingReport(typedElementCount, unicodeFallbackCount);
    }

    /// <summary>
    /// Monta o caractere como tecla física do layout: modificadores pressionados,
    /// tecla, e modificadores liberados na ordem inversa.
    /// </summary>
    private static bool TryAppendScanCodeElement(List<INPUT> inputs, string textElement, IntPtr layout)
    {
        if (textElement.Length != 1)
        {
            return false;
        }

        return TryAppendScanCodeChar(inputs, textElement[0], layout);
    }

    private static bool TryAppendScanCodeChar(List<INPUT> inputs, char ch, IntPtr layout)
    {
        var mapped = VkKeyScanEx(ch, layout);
        if (mapped == -1)
        {
            return false;
        }

        var virtualKey = (ushort)(mapped & 0xFF);
        var shiftState = (mapped >> 8) & 0xFF;
        if ((shiftState & ShiftStateUnsupported) != 0)
        {
            return false;
        }

        if (!TryGetScanCode(virtualKey, layout, out var scanCode, out var extended))
        {
            return false;
        }

        AppendModifiers(inputs, shiftState, layout, keyUp: false);
        inputs.Add(KeyboardScanCode(scanCode, extended, keyUp: false));
        inputs.Add(KeyboardScanCode(scanCode, extended, keyUp: true));
        AppendModifiers(inputs, shiftState, layout, keyUp: true);
        return true;
    }

    /// <summary>
    /// Acentos que o layout só produz em duas etapas: a tecla morta (´, ^, ~, `,
    /// ¨) seguida da letra base. É como o usuário digitaria "á" no ABNT2.
    /// </summary>
    private static bool TryAppendDeadKeyElement(List<INPUT> inputs, string textElement, IntPtr layout)
    {
        var decomposed = textElement.Normalize(NormalizationForm.FormD);
        if (decomposed.Length != 2 ||
            !DeadKeySpacingChars.TryGetValue(decomposed[1], out var deadKeyChar))
        {
            return false;
        }

        var composed = new List<INPUT>(8);
        if (!TryAppendScanCodeChar(composed, deadKeyChar, layout) ||
            !TryAppendScanCodeChar(composed, decomposed[0], layout))
        {
            return false;
        }

        inputs.Clear();
        inputs.AddRange(composed);
        return true;
    }

    private static void AppendModifiers(List<INPUT> inputs, int shiftState, IntPtr layout, bool keyUp)
    {
        // Ctrl+Alt é, no teclado físico, a tecla AltGr — um único scancode
        // estendido. Emular os dois separadamente faz o destino enxergar um Ctrl
        // solto e interpretar o caractere como atalho.
        if ((shiftState & (ShiftStateControl | ShiftStateAlt)) == (ShiftStateControl | ShiftStateAlt))
        {
            inputs.Add(KeyboardScanCode(RightAltScanCode, extended: true, keyUp));
            return;
        }

        if (keyUp)
        {
            AppendModifier(inputs, VkMenu, ShiftStateAlt);
            AppendModifier(inputs, VkControl, ShiftStateControl);
            AppendModifier(inputs, VkShift, ShiftStateShift);
            return;
        }

        AppendModifier(inputs, VkShift, ShiftStateShift);
        AppendModifier(inputs, VkControl, ShiftStateControl);
        AppendModifier(inputs, VkMenu, ShiftStateAlt);

        void AppendModifier(List<INPUT> target, ushort virtualKey, int flag)
        {
            if ((shiftState & flag) == 0)
            {
                return;
            }

            if (TryGetScanCode(virtualKey, layout, out var scanCode, out var extended))
            {
                target.Add(KeyboardScanCode(scanCode, extended, keyUp));
            }
            else
            {
                target.Add(KeyboardVk(virtualKey, keyUp));
            }
        }
    }

    private static void AppendKeyStroke(List<INPUT> inputs, ushort virtualKey, IntPtr layout)
    {
        if (TryGetScanCode(virtualKey, layout, out var scanCode, out var extended))
        {
            inputs.Add(KeyboardScanCode(scanCode, extended, keyUp: false));
            inputs.Add(KeyboardScanCode(scanCode, extended, keyUp: true));
            return;
        }

        inputs.Add(KeyboardVk(virtualKey, keyUp: false));
        inputs.Add(KeyboardVk(virtualKey, keyUp: true));
    }

    private static void AppendUnicodeTextElement(List<INPUT> inputs, string textElement)
    {
        foreach (var codeUnit in textElement)
        {
            inputs.Add(KeyboardUnicode(codeUnit, keyUp: false));
            inputs.Add(KeyboardUnicode(codeUnit, keyUp: true));
        }
    }

    private static bool TryGetScanCode(ushort virtualKey, IntPtr layout, out ushort scanCode, out bool extended)
    {
        var mapped = MapVirtualKeyEx(virtualKey, MapVkToVscEx, layout);
        scanCode = (ushort)(mapped & 0xFF);
        var prefix = (mapped >> 8) & 0xFF;
        extended = prefix is 0xE0 or 0xE1;
        return scanCode != 0;
    }

    private static void FlushInputs(List<INPUT> inputs, bool oneEventPerCall)
    {
        if (inputs.Count == 0)
        {
            return;
        }

        if (!oneEventPerCall)
        {
            // Um toque de tecla inteiro em uma chamada: os modificadores precisam
            // continuar pressionados enquanto a tecla desce e sobe.
            SendInputs(inputs.ToArray());
            inputs.Clear();
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

    /// <summary>
    /// Layout de teclado da janela em foco — é ele que o destino usa para
    /// traduzir scancode em caractere, inclusive dentro do cliente Citrix.
    /// </summary>
    private static IntPtr ForegroundKeyboardLayout()
    {
        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero)
        {
            return GetKeyboardLayout(0);
        }

        var threadId = GetWindowThreadProcessId(foreground, out _);
        return GetKeyboardLayout(threadId);
    }

    private static INPUT KeyboardScanCode(ushort scanCode, bool extended, bool keyUp) => new()
    {
        type = InputKeyboard,
        u = new InputUnion
        {
            ki = new KEYBDINPUT
            {
                wVk = 0,
                wScan = scanCode,
                dwFlags = KeyEventScanCode
                    | (extended ? KeyEventExtendedKey : 0)
                    | (keyUp ? KeyEventKeyUp : 0),
                time = 0,
                dwExtraInfo = IntPtr.Zero,
            },
        },
    };

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

    [DllImport("user32.dll")]
    private static extern short VkKeyScanEx(char ch, IntPtr dwhkl);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKeyEx(uint uCode, uint uMapType, IntPtr dwhkl);

    [DllImport("user32.dll")]
    private static extern IntPtr GetKeyboardLayout(uint idThread);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

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

/// <summary>Resumo de uma digitação simulada.</summary>
/// <param name="Elements">Elementos de texto digitados.</param>
/// <param name="UnicodeFallbacks">
/// Elementos que só puderam ser enviados como Unicode — não chegam em sessões
/// remotas como Citrix e RDP.
/// </param>
public readonly record struct TypingReport(int Elements, int UnicodeFallbacks);
