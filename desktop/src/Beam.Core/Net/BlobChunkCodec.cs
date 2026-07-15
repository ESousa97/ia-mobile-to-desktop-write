using System.Buffers.Binary;
using System.Security.Cryptography;
using Beam.Core.Security;

namespace Beam.Core.Net;

/// <summary>
/// Codifica/decodifica frames binários <c>blob.chunk</c> com cifra AES-GCM por chunk.
/// Layout: blobId(16) | seq(uint32 BE) | len(uint32 BE) | pacote GCM.
/// </summary>
public static class BlobChunkCodec
{
    public const int HeaderSize = 24;

    public static byte[] EncodeChunk(ReadOnlySpan<byte> blobId, uint sequence, ReadOnlySpan<byte> plaintext, SessionCipher cipher)
    {
        if (blobId.Length != 16)
        {
            throw new ArgumentException("O blobId deve ter 16 bytes.", nameof(blobId));
        }

        var encrypted = cipher.Encrypt(plaintext, AssociatedData(blobId, sequence));
        var frame = new byte[HeaderSize + encrypted.Length];
        blobId.CopyTo(frame);
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(16, 4), sequence);
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(20, 4), (uint)encrypted.Length);
        encrypted.CopyTo(frame.AsSpan(HeaderSize));
        return frame;
    }

    public static bool TryDecodeChunk(
        ReadOnlySpan<byte> frame,
        SessionCipher cipher,
        out byte[] blobId,
        out uint sequence,
        out byte[] plaintext)
    {
        blobId = [];
        sequence = 0;
        plaintext = [];

        if (frame.Length < HeaderSize)
        {
            return false;
        }

        blobId = frame[..16].ToArray();
        sequence = BinaryPrimitives.ReadUInt32BigEndian(frame.Slice(16, 4));
        var payloadLength = BinaryPrimitives.ReadUInt32BigEndian(frame.Slice(20, 4));
        if (payloadLength == 0 || frame.Length < HeaderSize + payloadLength)
        {
            return false;
        }

        try
        {
            plaintext = cipher.Decrypt(frame.Slice(HeaderSize, (int)payloadLength), AssociatedData(blobId, sequence));
            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    public static byte[] CreateBlobIdBytes() => Guid.NewGuid().ToByteArray();

    public static string ToBlobIdString(ReadOnlySpan<byte> blobIdBytes) =>
        Convert.ToHexStringLower(blobIdBytes);

    public static byte[] ParseBlobIdString(string blobId)
    {
        if (blobId.Length != 32)
        {
            throw new ArgumentException("O blobId deve ter 32 caracteres hex.", nameof(blobId));
        }

        return Convert.FromHexString(blobId);
    }

    public static string ComputeSha256Hex(ReadOnlySpan<byte> data) =>
        Convert.ToHexStringLower(SHA256.HashData(data));

    private static byte[] AssociatedData(ReadOnlySpan<byte> blobId, uint sequence)
    {
        var aad = new byte[blobId.Length + 4];
        blobId.CopyTo(aad);
        BinaryPrimitives.WriteUInt32BigEndian(aad.AsSpan(blobId.Length, 4), sequence);
        return aad;
    }
}
