namespace Beam.Core.Abstractions;

/// <summary>Tipo de conteúdo da área de transferência suportado.</summary>
public enum ClipboardKind
{
    None,
    Text,
    Image,
}

/// <summary>
/// Representa um item da área de transferência de forma agnóstica de plataforma.
/// Para imagens, <see cref="ImageBytes"/> contém um PNG.
/// </summary>
public sealed record ClipboardContent
{
    public ClipboardKind Kind { get; init; } = ClipboardKind.None;
    public string? Text { get; init; }
    public byte[]? ImageBytes { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }

    public static ClipboardContent FromText(string text) =>
        new() { Kind = ClipboardKind.Text, Text = text };

    public static ClipboardContent FromImage(byte[] pngBytes, int width, int height) =>
        new() { Kind = ClipboardKind.Image, ImageBytes = pngBytes, Width = width, Height = height };

    public static readonly ClipboardContent Empty = new();
}
