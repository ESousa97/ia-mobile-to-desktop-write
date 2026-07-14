namespace ClipBridge.Core.Protocol;

/// <summary>
/// Tipos de mensagem do protocolo (campo <c>type</c> do envelope).
/// Ver <c>docs/PROTOCOL.md</c> para a especificação completa.
/// </summary>
public static class MessageType
{
    public const string Hello = "hello";

    public const string PairRequest = "pair.request";
    public const string PairResponse = "pair.response";
    public const string PairConfirm = "pair.confirm";

    public const string ClipboardText = "clipboard.text";
    public const string ClipboardImage = "clipboard.image";

    public const string Screenshot = "screenshot";

    public const string BlobBegin = "blob.begin";
    public const string BlobChunk = "blob.chunk";
    public const string BlobEnd = "blob.end";

    public const string TypeText = "type.text";

    public const string Ack = "ack";
    public const string Error = "error";
    public const string Ping = "ping";
    public const string Pong = "pong";
}
