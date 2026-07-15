namespace Beam.Core.Net;

/// <summary>Blob recebido e validado com sucesso.</summary>
public sealed record BlobTransferCompleted(string BlobId, byte[] Data);
