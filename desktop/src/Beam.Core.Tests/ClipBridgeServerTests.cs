using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using Beam.Core.Net;
using Beam.Core.Protocol;
using Beam.Core.Security;
using Xunit;

namespace Beam.Core.Tests;

/// <summary>
/// Testes de integração do <see cref="ClipBridgeServer"/>: valida o handshake
/// WebSocket sobre TCP cru, o fluxo de pareamento e o round-trip cifrado —
/// exatamente o caminho exercido pelo cliente Android (OkHttp).
/// </summary>
public sealed class ClipBridgeServerTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Completes_pairing_and_delivers_secure_text()
    {
        var port = GetFreePort();
        // discoveryPort 0 = porta UDP efêmera, para não colidir com uma instância
        // real do app (que usa a porta fixa de descoberta) durante os testes.
        await using var server = new ClipBridgeServer(port, discoveryPort: 0);
        server.Start();
        var invitation = server.BeginPairing();

        var received = new TaskCompletionSource<Envelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secureEstablished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        server.MessageReceived += (_, e) => received.TrySetResult(e);
        server.SecureSessionEstablished += (_, _) => secureEstablished.TrySetResult(true);

        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/"), CancellationToken.None);

        // pair.request (texto claro)
        using var keyAgreement = new X25519KeyAgreement();
        var nonce = RandomNumberGenerator.GetBytes(32);
        await SendAsync(ws, Envelope.Create(
            MessageType.PairRequest,
            new PairRequestPayload(
                Convert.ToBase64String(keyAgreement.PublicKey.Span),
                Convert.ToBase64String(nonce))));

        // pair.response → deriva a mesma chave de sessão
        var response = await ReceiveAsync(ws);
        Assert.Equal(MessageType.PairResponse, response.Type);
        var serverPublicKey = Convert.FromBase64String(response.PayloadAs<PairResponsePayload>()!.PubKey);
        var sessionKey = keyAgreement.DeriveSessionKey(serverPublicKey, nonce, PairingCoordinator.SessionKeyInfo);
        using var cipher = new SessionCipher(sessionKey);

        // pair.confirm com o código exibido pelo desktop
        await SendAsync(ws, Envelope.Create(
            MessageType.PairConfirm,
            new PairConfirmPayload(invitation.PairingCode)));

        var ack = await ReceiveAsync(ws);
        Assert.Equal(MessageType.Ack, ack.Type);
        Assert.True(await secureEstablished.Task.WaitAsync(Timeout));
        Assert.Equal(1, server.SecureClientCount);

        // clipboard.text cifrado → o servidor deve entregar o envelope decifrado
        var wire = SecureEnvelopeCodec.Encrypt(
            Envelope.Create(MessageType.ClipboardText, new ClipboardTextPayload("olá desktop")),
            cipher);
        await SendAsync(ws, wire);

        var delivered = await received.Task.WaitAsync(Timeout);
        Assert.Equal(MessageType.ClipboardText, delivered.Type);
        Assert.Equal("olá desktop", delivered.PayloadAs<ClipboardTextPayload>()!.Text);
    }

    [Fact]
    public async Task Rejects_data_before_pairing()
    {
        var port = GetFreePort();
        // discoveryPort 0 = porta UDP efêmera, para não colidir com uma instância
        // real do app (que usa a porta fixa de descoberta) durante os testes.
        await using var server = new ClipBridgeServer(port, discoveryPort: 0);
        server.Start();

        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/"), CancellationToken.None);

        await SendAsync(ws, Envelope.Create(MessageType.ClipboardText, new ClipboardTextPayload("antes do pareamento")));

        var reply = await ReceiveAsync(ws);
        Assert.Equal(MessageType.Error, reply.Type);
        Assert.Equal("auth.failed", reply.PayloadAs<ErrorPayload>()!.Code);
    }

    private static int GetFreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static Task SendAsync(WebSocket socket, Envelope envelope) =>
        socket.SendAsync(
            Encoding.UTF8.GetBytes(MessageSerializer.Serialize(envelope)),
            WebSocketMessageType.Text,
            true,
            CancellationToken.None);

    private static async Task<Envelope> ReceiveAsync(WebSocket socket)
    {
        using var cts = new CancellationTokenSource(Timeout);
        var buffer = new byte[64 * 1024];
        using var stream = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
            stream.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);

        var json = Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length);
        return MessageSerializer.Deserialize(json)
            ?? throw new InvalidOperationException("Envelope inválido recebido no teste.");
    }
}
