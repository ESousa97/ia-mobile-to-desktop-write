using Beam.Core.Abstractions;
using Xunit;

namespace Beam.Core.Tests;

public sealed class ClipboardTextUtilTests
{
    [Fact]
    public void Normalize_makes_windows_and_android_text_comparable()
    {
        var windows = "linha 1\r\nlinha 2\r\nlinha 3";
        var android = "linha 1\nlinha 2\nlinha 3";

        Assert.Equal(ClipboardTextUtil.Normalize(windows), ClipboardTextUtil.Normalize(android));
    }

    [Fact]
    public void Normalize_handles_lone_carriage_returns()
    {
        Assert.Equal("a\nb", ClipboardTextUtil.Normalize("a\rb"));
    }

    [Fact]
    public void ToWindowsLineEndings_round_trips_with_normalize()
    {
        var android = "um\ndois\ntrês";
        var windows = ClipboardTextUtil.ToWindowsLineEndings(android);

        Assert.Equal("um\r\ndois\r\ntrês", windows);
        Assert.Equal(android, ClipboardTextUtil.Normalize(windows));
    }

    [Fact]
    public void NormalizeForTyping_composes_accents_and_preserves_multiline_text()
    {
        var copiedText = "A imagem mostra uma captura.\r\n\r\nA\u0300 esquerda: Voce\u0302 ve\u0302 um vi\u0301deo e uma pec\u0327a.";

        var prepared = ClipboardTextUtil.NormalizeForTyping(copiedText);

        Assert.Equal("A imagem mostra uma captura.\n\n\u00C0 esquerda: Voc\u00EA v\u00EA um v\u00EDdeo e uma pe\u00E7a.", prepared);
    }
}
