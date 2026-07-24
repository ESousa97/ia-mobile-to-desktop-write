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
/// Retomada de sessão: depois de um pareamento por código, o celular reconecta
/// sem código enquanto a confiança valer — o caminho exercido pelo cliente
/// Android após uma queda de Wi-Fi ou ao reabrir o app.
/// </summary>
public sealed class SessionResumeTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Resumes_without_pairing_code_and_renews_trust()
    {
        var store = new InMemoryTrustStore();
        var port = GetFreePort();
        await using var server = new ClipBridgeServer(port, discoveryPort: 0, trustStore: store);
        server.Start();

        var resumeKey = await PairAsync(server, port);
        var deviceId = ResumeHandshake.ComputeDeviceId(resumeKey);
        Assert.NotNull(store.Find(deviceId));

        // A validade encolhe artificialmente para provar que a retomada a renova.
        store.Expire(deviceId, DateTimeOffset.UtcNow.AddMinutes(5));

        var secureAgain = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        server.SecureSessionEstablished += (_, _) => secureAgain.TrySetResult(true);

        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/"), CancellationToken.None);

        using var keyAgreement = new X25519KeyAgreement();
        var clientNonce = RandomNumberGenerator.GetBytes(ResumeHandshake.NonceSizeBytes);
        await SendAsync(ws, Envelope.Create(
            MessageType.SessionResume,
            new SessionResumePayload(
                deviceId,
                Convert.ToBase64String(keyAgreement.PublicKey.Span),
                Convert.ToBase64String(clientNonce))));

        var resumed = await ReceiveAsync(ws);
        Assert.Equal(MessageType.SessionResumed, resumed.Type);
        var payload = resumed.PayloadAs<SessionResumedPayload>()!;

        var salt = ResumeHandshake.BuildSalt(clientNonce, Convert.FromBase64String(payload.Nonce));
        Assert.Equal(
            Convert.ToBase64String(ResumeHandshake.ServerProof(resumeKey, salt)),
            payload.Proof);

        var ecdhKey = keyAgreement.DeriveSessionKey(
            Convert.FromBase64String(payload.PubKey), salt, ResumeHandshake.EcdhSessionInfo);
        using var cipher = new SessionCipher(ResumeHandshake.DeriveSessionKey(ecdhKey, resumeKey, salt));

        await SendAsync(ws, SecureEnvelopeCodec.Encrypt(
            Envelope.Create(
                MessageType.SessionResumeConfirm,
                new SessionResumeConfirmPayload(
                    Convert.ToBase64String(ResumeHandshake.ClientProof(resumeKey, salt)))),
            cipher));

        var ack = await ReceiveAsync(ws);
        Assert.Equal(MessageType.Ack, ack.Type);
        Assert.True(await secureAgain.Task.WaitAsync(Timeout));

        // A janela de 72h passa a contar desta conexão, não do pareamento.
        Assert.True(store.Find(deviceId)!.ExpiresAt > DateTimeOffset.UtcNow.AddHours(71));

        // E a sessão retomada transporta dados normalmente.
        var received = new TaskCompletionSource<Envelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        server.MessageReceived += (_, e) => received.TrySetResult(e);
        await SendAsync(ws, SecureEnvelopeCodec.Encrypt(
            Envelope.Create(MessageType.ClipboardText, new ClipboardTextPayload("depois da queda")), cipher));

        var delivered = await received.Task.WaitAsync(Timeout);
        Assert.Equal("depois da queda", delivered.PayloadAs<ClipboardTextPayload>()!.Text);
    }

    [Fact]
    public async Task Rejects_resume_of_unknown_device()
    {
        var port = GetFreePort();
        await using var server = new ClipBridgeServer(port, discoveryPort: 0, trustStore: new InMemoryTrustStore());
        server.Start();

        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/"), CancellationToken.None);

        using var keyAgreement = new X25519KeyAgreement();
        await SendAsync(ws, Envelope.Create(
            MessageType.SessionResume,
            new SessionResumePayload(
                "0011223344556677",
                Convert.ToBase64String(keyAgreement.PublicKey.Span),
                Convert.ToBase64String(RandomNumberGenerator.GetBytes(ResumeHandshake.NonceSizeBytes)))));

        var reply = await ReceiveAsync(ws);
        Assert.Equal(MessageType.Error, reply.Type);
        Assert.Equal("resume.denied", reply.PayloadAs<ErrorPayload>()!.Code);
    }

    /// <summary>
    /// Quem conhece o identificador do vínculo mas não a chave de retomada não
    /// consegue fechar o handshake — a prova do cliente é verificada.
    /// </summary>
    [Fact]
    public async Task Rejects_resume_with_wrong_resume_key()
    {
        var store = new InMemoryTrustStore();
        var port = GetFreePort();
        await using var server = new ClipBridgeServer(port, discoveryPort: 0, trustStore: store);
        server.Start();

        var resumeKey = await PairAsync(server, port);
        var deviceId = ResumeHandshake.ComputeDeviceId(resumeKey);
        var forgedKey = RandomNumberGenerator.GetBytes(SessionCipher.KeySizeBytes);

        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/"), CancellationToken.None);

        using var keyAgreement = new X25519KeyAgreement();
        var clientNonce = RandomNumberGenerator.GetBytes(ResumeHandshake.NonceSizeBytes);
        await SendAsync(ws, Envelope.Create(
            MessageType.SessionResume,
            new SessionResumePayload(
                deviceId,
                Convert.ToBase64String(keyAgreement.PublicKey.Span),
                Convert.ToBase64String(clientNonce))));

        var resumed = await ReceiveAsync(ws);
        var payload = resumed.PayloadAs<SessionResumedPayload>()!;
        var salt = ResumeHandshake.BuildSalt(clientNonce, Convert.FromBase64String(payload.Nonce));
        var ecdhKey = keyAgreement.DeriveSessionKey(
            Convert.FromBase64String(payload.PubKey), salt, ResumeHandshake.EcdhSessionInfo);
        using var wrongCipher = new SessionCipher(ResumeHandshake.DeriveSessionKey(ecdhKey, forgedKey, salt));

        await SendAsync(ws, SecureEnvelopeCodec.Encrypt(
            Envelope.Create(
                MessageType.SessionResumeConfirm,
                new SessionResumeConfirmPayload(
                    Convert.ToBase64String(ResumeHandshake.ClientProof(forgedKey, salt)))),
            wrongCipher));

        var reply = await ReceiveAsync(ws);
        Assert.Equal(MessageType.Error, reply.Type);
        Assert.Equal("resume.denied", reply.PayloadAs<ErrorPayload>()!.Code);
        Assert.Equal(0, server.SecureClientCount);
    }

    [Fact]
    public void Trust_expires_after_the_configured_window()
    {
        var store = new InMemoryTrustStore();
        var resumeKey = RandomNumberGenerator.GetBytes(SessionCipher.KeySizeBytes);
        var deviceId = ResumeHandshake.ComputeDeviceId(resumeKey);

        store.Remember(deviceId, resumeKey);
        Assert.NotNull(store.Find(deviceId));

        store.Expire(deviceId, DateTimeOffset.UtcNow.AddSeconds(-1));
        Assert.Null(store.Find(deviceId));
    }

    [Fact]
    public void Trust_window_is_72_hours()
    {
        Assert.Equal(TimeSpan.FromHours(72), ResumeHandshake.TrustLifetime);
    }

    /// <summary>Revogar deve barrar a retomada imediatamente, não só na expiração.</summary>
    [Fact]
    public async Task Revoking_trust_denies_further_resume()
    {
        var store = new InMemoryTrustStore();
        var port = GetFreePort();
        await using var server = new ClipBridgeServer(port, discoveryPort: 0, trustStore: store);
        server.Start();

        var resumeKey = await PairAsync(server, port);
        var deviceId = ResumeHandshake.ComputeDeviceId(resumeKey);
        Assert.Equal(1, server.TrustedDeviceCount);

        await server.RevokeTrustedDevicesAsync();
        Assert.Equal(0, server.TrustedDeviceCount);

        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/"), CancellationToken.None);

        using var keyAgreement = new X25519KeyAgreement();
        await SendAsync(ws, Envelope.Create(
            MessageType.SessionResume,
            new SessionResumePayload(
                deviceId,
                Convert.ToBase64String(keyAgreement.PublicKey.Span),
                Convert.ToBase64String(RandomNumberGenerator.GetBytes(ResumeHandshake.NonceSizeBytes)))));

        var reply = await ReceiveAsync(ws);
        Assert.Equal(MessageType.Error, reply.Type);
        Assert.Equal("resume.denied", reply.PayloadAs<ErrorPayload>()!.Code);
    }

    /// <summary>
    /// Persistência real em arquivo: o vínculo sobrevive ao fechamento do app,
    /// que é o caso "desligou o PC e voltou" da reconexão automática.
    /// </summary>
    [Fact]
    public void File_store_survives_restart_and_expiry_is_enforced()
    {
        var path = Path.Combine(Path.GetTempPath(), $"beam-trust-{Guid.NewGuid():N}.bin");
        var protector = new PassthroughProtector();
        var resumeKey = RandomNumberGenerator.GetBytes(SessionCipher.KeySizeBytes);
        var deviceId = ResumeHandshake.ComputeDeviceId(resumeKey);

        try
        {
            new FileTrustedDeviceStore(path, protector).Remember(deviceId, resumeKey);

            // Instância nova = processo novo: precisa reler do disco.
            var reopened = new FileTrustedDeviceStore(path, protector);
            var found = reopened.Find(deviceId);
            Assert.NotNull(found);
            Assert.Equal(resumeKey, found!.ResumeKey);
            Assert.True(found.ExpiresAt > DateTimeOffset.UtcNow.AddHours(71));

            reopened.Forget(deviceId);
            Assert.Null(new FileTrustedDeviceStore(path, protector).Find(deviceId));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>Protetor identidade: no app real é DPAPI, aqui só isola o teste do Windows.</summary>
    private sealed class PassthroughProtector : ISecretProtector
    {
        public byte[] Protect(byte[] plaintext) => plaintext;

        public byte[] Unprotect(byte[] ciphertext) => ciphertext;
    }

    /// <summary>Pareamento por código completo; devolve a chave de retomada derivada.</summary>
    private static async Task<byte[]> PairAsync(ClipBridgeServer server, int port)
    {
        var invitation = server.BeginPairing();
        var secure = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnSecure(object? sender, EventArgs e) => secure.TrySetResult(true);
        server.SecureSessionEstablished += OnSecure;

        try
        {
            using var ws = new ClientWebSocket();
            await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/"), CancellationToken.None);

            using var keyAgreement = new X25519KeyAgreement();
            var nonce = RandomNumberGenerator.GetBytes(32);
            await SendAsync(ws, Envelope.Create(
                MessageType.PairRequest,
                new PairRequestPayload(
                    Convert.ToBase64String(keyAgreement.PublicKey.Span),
                    Convert.ToBase64String(nonce))));

            var response = await ReceiveAsync(ws);
            var serverPublicKey = Convert.FromBase64String(response.PayloadAs<PairResponsePayload>()!.PubKey);
            var resumeKey = keyAgreement.DeriveSessionKey(serverPublicKey, nonce, ResumeHandshake.ResumeKeyInfo);

            await SendAsync(ws, Envelope.Create(
                MessageType.PairConfirm,
                new PairConfirmPayload(invitation.PairingCode)));

            var ack = await ReceiveAsync(ws);
            Assert.Equal(MessageType.Ack, ack.Type);
            Assert.True(await secure.Task.WaitAsync(Timeout));

            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
            return resumeKey;
        }
        finally
        {
            server.SecureSessionEstablished -= OnSecure;
        }
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

    private sealed class InMemoryTrustStore : ITrustedDeviceStore
    {
        private readonly Dictionary<string, TrustedDevice> _devices = new(StringComparer.OrdinalIgnoreCase);

        public TrustedDevice? Find(string deviceId) =>
            _devices.TryGetValue(deviceId, out var device) && device.ExpiresAt > DateTimeOffset.UtcNow
                ? device
                : null;

        public void Remember(string deviceId, byte[] resumeKey) =>
            _devices[deviceId] = new TrustedDevice(
                deviceId,
                resumeKey.ToArray(),
                DateTimeOffset.UtcNow.Add(ResumeHandshake.TrustLifetime));

        public void Renew(string deviceId)
        {
            if (_devices.TryGetValue(deviceId, out var device))
            {
                _devices[deviceId] = device with
                {
                    ExpiresAt = DateTimeOffset.UtcNow.Add(ResumeHandshake.TrustLifetime),
                };
            }
        }

        public void Forget(string deviceId) => _devices.Remove(deviceId);

        public void ForgetAll() => _devices.Clear();

        public int Count => _devices.Count(entry => entry.Value.ExpiresAt > DateTimeOffset.UtcNow);

        public void Expire(string deviceId, DateTimeOffset expiresAt) =>
            _devices[deviceId] = _devices[deviceId] with { ExpiresAt = expiresAt };
    }
}
