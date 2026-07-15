using System.Text;

namespace Beam.Core.Abstractions;

/// <summary>
/// Normalização de texto para comparação entre plataformas. O Windows usa
/// <c>\r\n</c> e o Android <c>\n</c>; sem normalizar, o mesmo texto "parece
/// diferente" a cada ida-e-volta e o sync entra em loop infinito.
/// </summary>
public static class ClipboardTextUtil
{
    /// <summary>Converte todos os fins de linha para <c>\n</c>.</summary>
    public static string Normalize(string text) =>
        text.Replace("\r\n", "\n").Replace('\r', '\n');

    /// <summary>Converte fins de linha para o padrão do Windows (<c>\r\n</c>).</summary>
    public static string ToWindowsLineEndings(string text) =>
        Normalize(text).Replace("\n", "\r\n");

    /// <summary>
    /// Prepara texto para injeção por teclado. A forma NFC mantém letras com
    /// acento como um único caractere Unicode quando isso é possível.
    /// </summary>
    public static string NormalizeForTyping(string text) =>
        Normalize(text).Normalize(NormalizationForm.FormC);
}
