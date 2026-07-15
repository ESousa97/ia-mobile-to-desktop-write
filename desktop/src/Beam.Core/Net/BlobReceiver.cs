using System.Security.Cryptography;
using Beam.Core.Protocol;

namespace Beam.Core.Net;

/// <summary>Reúne chunks de um blob até validar o SHA-256 no <c>blob.end</c>.</summary>
public sealed class BlobReceiver
{
    private readonly Dictionary<string, PendingBlob> _pending = new(StringComparer.OrdinalIgnoreCase);

    public bool TryBegin(BlobBeginPayload begin)
    {
        if (begin.TotalBytes <= 0 || begin.TotalBytes > ProtocolInfo.MaxBlobBytes)
        {
            return false;
        }

        _pending[begin.BlobId] = new PendingBlob(begin.TotalBytes, begin.ChunkSize, begin.Sha256);
        return true;
    }

    public bool TryAddChunk(string blobId, uint sequence, ReadOnlySpan<byte> chunk)
    {
        if (!_pending.TryGetValue(blobId, out var pending))
        {
            return false;
        }

        return pending.TryWrite(sequence, chunk);
    }

    public byte[]? TryComplete(BlobEndPayload end)
    {
        if (!_pending.Remove(end.BlobId, out var pending) || !pending.IsComplete)
        {
            return null;
        }

        var data = pending.ToArray();
        var digest = BlobChunkCodec.ComputeSha256Hex(data);
        return string.Equals(digest, pending.ExpectedSha256, StringComparison.OrdinalIgnoreCase) ? data : null;
    }

    private sealed class PendingBlob
    {
        private readonly byte[] _buffer;
        private readonly bool[] _receivedChunks;
        private readonly int _chunkSize;
        private int _receivedBytes;

        public PendingBlob(long totalBytes, int chunkSize, string expectedSha256)
        {
            _buffer = new byte[totalBytes];
            _chunkSize = chunkSize;
            ExpectedSha256 = expectedSha256;
            _receivedChunks = new bool[(int)((totalBytes + chunkSize - 1) / chunkSize)];
        }

        public string ExpectedSha256 { get; }

        public bool IsComplete => _receivedBytes == _buffer.Length;

        public bool TryWrite(uint sequence, ReadOnlySpan<byte> chunk)
        {
            if (sequence >= _receivedChunks.Length || _receivedChunks[sequence])
            {
                return false;
            }

            var offset = (int)(sequence * (uint)_chunkSize);
            if (offset + chunk.Length > _buffer.Length)
            {
                return false;
            }

            chunk.CopyTo(_buffer.AsSpan(offset));
            _receivedChunks[sequence] = true;
            _receivedBytes += chunk.Length;
            return true;
        }

        public byte[] ToArray() => _buffer;
    }
}
