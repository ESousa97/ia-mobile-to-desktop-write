using Beam.Core.Net;
using Beam.Core.Security;
using Xunit;

namespace Beam.Core.Tests;

public class BlobChunkCodecTests
{
    [Fact]
    public void EncodeDecode_RoundTripsChunk()
    {
        using var cipher = new SessionCipher(new byte[SessionCipher.KeySizeBytes]);
        var blobId = BlobChunkCodec.CreateBlobIdBytes();
        var plaintext = "conteúdo do chunk"u8.ToArray();

        var frame = BlobChunkCodec.EncodeChunk(blobId, 2, plaintext, cipher);
        Assert.True(BlobChunkCodec.TryDecodeChunk(frame, cipher, out var decodedId, out var sequence, out var decoded));

        Assert.Equal(blobId, decodedId);
        Assert.Equal(2u, sequence);
        Assert.Equal(plaintext, decoded);
    }
}
