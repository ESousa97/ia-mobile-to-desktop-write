using System.IO;
using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Beam.Desktop.Services;

/// <summary>
/// Impressão digital de imagem: SHA-256 dos pixels de uma miniatura 64×64 (BGR24).
/// Como o PNG é sem perdas e o redimensionamento do WPF é determinístico, a MESMA
/// imagem produz o MESMO hash mesmo após decodificar/recodificar — ao contrário dos
/// bytes do arquivo, que mudam a cada recodificação. É isso que permite detectar
/// "esta imagem já foi sincronizada" e cortar o loop infinito de eco, sem colidir
/// entre imagens diferentes.
/// </summary>
public static class ImageFingerprint
{
    private const int Size = 64;

    public static ulong Compute(byte[] pngBytes)
    {
        using var stream = new MemoryStream(pngBytes);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];

        var scaled = new TransformedBitmap(
            frame,
            new ScaleTransform((double)Size / frame.PixelWidth, (double)Size / frame.PixelHeight));
        // BGR24 descarta o canal alfa: o round-trip pelo clipboard (DIB) pode
        // alterá-lo, mas mantém RGB intacto.
        var canonical = new FormatConvertedBitmap(scaled, PixelFormats.Bgr24, null, 0);

        var width = canonical.PixelWidth;
        var height = canonical.PixelHeight;
        var stride = width * 3;
        var pixels = new byte[stride * height];
        canonical.CopyPixels(pixels, stride, 0);

        var digest = SHA256.HashData(pixels);
        return BitConverter.ToUInt64(digest, 0);
    }

    public static bool IsSameImage(ulong a, ulong b) => a == b;
}
