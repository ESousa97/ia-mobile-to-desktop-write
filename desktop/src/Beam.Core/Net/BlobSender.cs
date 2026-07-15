using System.Net.WebSockets;
using Beam.Core.Protocol;
using Beam.Core.Security;

namespace Beam.Core.Net;

/// <summary>Envia um blob completo (begin → chunks → end) por WebSocket.</summary>
public static class BlobSender
{
    public static async Task<string> SendAsync(
        WebSocket socket,
        SessionCipher cipher,
        byte[] data,
        Func<Envelope, Envelope> encrypt,
        CancellationToken ct)
    {
        var blobIdBytes = BlobChunkCodec.CreateBlobIdBytes();
        var blobId = BlobChunkCodec.ToBlobIdString(blobIdBytes);
        var sha256 = BlobChunkCodec.ComputeSha256Hex(data);
        var chunkSize = ProtocolInfo.DefaultChunkSize;

        await SendEnvelopeAsync(
            socket,
            encrypt(Envelope.Create(MessageType.BlobBegin, new BlobBeginPayload(blobId, data.Length, chunkSize, sha256))),
            ct).ConfigureAwait(false);

        var sequence = 0u;
        for (var offset = 0; offset < data.Length; offset += chunkSize, sequence++)
        {
            var length = Math.Min(chunkSize, data.Length - offset);
            var frame = BlobChunkCodec.EncodeChunk(blobIdBytes, sequence, data.AsSpan(offset, length), cipher);
            await socket.SendAsync(frame, WebSocketMessageType.Binary, true, ct).ConfigureAwait(false);
        }

        await SendEnvelopeAsync(
            socket,
            encrypt(Envelope.Create(MessageType.BlobEnd, new BlobEndPayload(blobId))),
            ct).ConfigureAwait(false);

        return blobId;
    }

    private static Task SendEnvelopeAsync(WebSocket socket, Envelope envelope, CancellationToken ct) =>
        socket.SendAsync(
            System.Text.Encoding.UTF8.GetBytes(MessageSerializer.Serialize(envelope)),
            WebSocketMessageType.Text,
            true,
            ct);
}
