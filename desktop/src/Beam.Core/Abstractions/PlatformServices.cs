namespace Beam.Core.Abstractions;

/// <summary>Observa e escreve na área de transferência do sistema.</summary>
public interface IClipboardService
{
    /// <summary>Disparado quando o conteúdo local da área de transferência muda.</summary>
    event EventHandler<ClipboardContent>? ClipboardChanged;

    /// <summary>Lê o conteúdo atual da área de transferência.</summary>
    ClipboardContent GetCurrent();

    /// <summary>Escreve conteúdo na área de transferência (recebido do par).</summary>
    void SetContent(ClipboardContent content);

    void StartWatching();
    void StopWatching();
}

/// <summary>Captura a tela em alta resolução (sem perda).</summary>
public interface IScreenCaptureService
{
    /// <summary>Captura toda a área virtual (todos os monitores) como PNG full-res.</summary>
    CaptureResult CaptureAllScreens();

    /// <summary>Captura apenas o monitor principal como PNG full-res.</summary>
    CaptureResult CapturePrimaryScreen();
}

/// <summary>Resultado de uma captura de tela.</summary>
public sealed record CaptureResult(byte[] PngBytes, int Width, int Height, int MonitorCount);

/// <summary>Simula digitação física (SendInput) — para colar via teclado onde Ctrl+V é bloqueado.</summary>
public interface IKeyboardTypingService
{
    /// <summary>Digita o texto caractere a caractere na janela em foco.</summary>
    void TypeText(string text);
}

/// <summary>Registra atalhos globais do sistema (funcionam em segundo plano).</summary>
public interface IHotkeyService : IDisposable
{
    /// <summary>Registra um atalho e associa um callback. Retorna um id de registro.</summary>
    int Register(HotkeyModifiers modifiers, uint virtualKey, Action callback);

    void Unregister(int id);
}

[Flags]
public enum HotkeyModifiers : uint
{
    None = 0x0000,
    Alt = 0x0001,
    Control = 0x0002,
    Shift = 0x0004,
    Win = 0x0008,
    NoRepeat = 0x4000,
}
