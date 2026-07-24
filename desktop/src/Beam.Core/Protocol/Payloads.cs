namespace Beam.Core.Protocol;

/// <summary>Payloads tipados do protocolo. Ver <c>docs/PROTOCOL.md</c>.</summary>
public sealed record HelloPayload(string Device, string Platform, string AppVersion);

public sealed record PairRequestPayload(string PubKey, string Nonce);

public sealed record PairResponsePayload(string PubKey);

public sealed record PairConfirmPayload(string Code);

/// <summary>Pedido de retomada: vínculo, chave efêmera nova e nonce do celular.</summary>
public sealed record SessionResumePayload(string DeviceId, string PubKey, string Nonce);

/// <summary>Resposta do desktop: chave efêmera, nonce e prova de posse da chave de retomada.</summary>
public sealed record SessionResumedPayload(string PubKey, string Nonce, string Proof);

/// <summary>Prova do celular, cifrada com a chave de sessão recém-derivada.</summary>
public sealed record SessionResumeConfirmPayload(string Proof);

public sealed record ClipboardTextPayload(string Text, string Mime = "text/plain; charset=utf-8");

public sealed record ClipboardImagePayload(string BlobId, string Mime, int Width, int Height);

public sealed record ScreenshotPayload(string BlobId, string Mime, int Width, int Height, int Monitors);

public sealed record BlobBeginPayload(string BlobId, long TotalBytes, int ChunkSize, string Sha256);

public sealed record BlobEndPayload(string BlobId);

public sealed record TypeTextPayload(string Text);

public sealed record AckPayload(string AckId);

public sealed record ErrorPayload(string Code, string Message);

public sealed record PingPayload(bool Ok = true);

/// <summary>Payload cifrado no fio (AES-256-GCM, base64).</summary>
public sealed record EncryptedPayload(string Ct);
