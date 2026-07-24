using System.Net;
using System.Net.WebSockets;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Beam.Core.Protocol;
using Beam.Core.Security;

namespace Beam.Core.Net;

/// <summary>
/// Servidor WebSocket do Beam. Aceita conexões dos dispositivos pareados
/// na LAN e faz o roteamento de envelopes do protocolo.
/// </summary>
/// <remarks>
/// A escuta é feita sobre um <see cref="TcpListener"/> cru (com handshake
/// WebSocket manual via <see cref="WebSocket.CreateFromStream"/>), e não sobre
/// <c>HttpListener</c>. Isso evita a necessidade de reserva de URL no http.sys
/// (<c>netsh http add urlacl</c>) ou de elevação — o app roda como usuário comum.
/// </remarks>
public sealed class ClipBridgeServer : IAsyncDisposable
{
    private const string WebSocketGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
    private const int MaxHandshakeBytes = 16 * 1024;

    private readonly int _port;
    private readonly int _discoveryPort;
    private readonly ITrustedDeviceStore? _trustStore;
    private readonly object _pairingLock = new();
    private readonly object _clientsLock = new();
    private readonly List<ClientSession> _clients = [];
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;
    private Task? _discoveryLoop;
    private Task? _announceLoop;
    private PairingCoordinator? _pairing;
    private UdpClient? _discoverySocket;
    private UdpClient? _announceSocket;
    private volatile bool _running;

    /// <summary>Disparado quando um envelope é recebido de um cliente.</summary>
    public event EventHandler<Envelope>? MessageReceived;

    /// <summary>Disparado quando um cliente conecta ou desconecta.</summary>
    public event EventHandler<bool>? ClientConnectionChanged;

    /// <summary>Disparado depois que um cliente conclui o handshake de pareamento.</summary>
    public event EventHandler? SecureSessionEstablished;

    /// <summary>Disparado quando um blob é recebido e validado.</summary>
    public event EventHandler<BlobTransferCompleted>? BlobCompleted;

    public bool IsRunning => _running;

    /// <summary>Porta TCP em que o servidor WebSocket escuta.</summary>
    public int Port => _port;

    /// <summary>Porta UDP das sondas de descoberta enviadas pelo celular.</summary>
    public int DiscoveryPort => _discoveryPort;

    /// <summary>
    /// Endereços <c>ip:porta</c> pelos quais o celular pode conectar manualmente,
    /// quando a descoberta automática não passa (rede com isolamento de clientes,
    /// VLANs separadas ou broadcast filtrado pelo roteador).
    /// </summary>
    public IReadOnlyList<string> ConnectionEndpoints =>
        NetworkInfo.GetLanAddresses().Select(address => $"{address.Address}:{_port}").ToList();

    /// <summary>Número de clientes com sessão segura ativa.</summary>
    public int SecureClientCount
    {
        get
        {
            lock (_clientsLock)
            {
                return _clients.Count(static session => session.IsSecure);
            }
        }
    }

    /// <summary>Envia um envelope cifrado a todos os clientes em sessão segura.</summary>
    public async Task BroadcastSecureAsync(Envelope envelope, CancellationToken ct = default)
    {
        List<(ClientSession Session, Envelope Wire)> messages;
        lock (_clientsLock)
        {
            messages = _clients
                .Where(static session => session.IsSecure && session.Cipher is not null)
                .Select(session => (session, SecureEnvelopeCodec.Encrypt(envelope, session.Cipher!)))
                .ToList();
        }

        foreach (var (session, wire) in messages)
        {
            if (session.Socket.State != WebSocketState.Open)
            {
                continue;
            }

            try
            {
                await SendAsync(session.Socket, wire, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is WebSocketException or IOException or ObjectDisposedException)
            {
                // Sessão morta (ex.: socket órfão de repareamento): não pode impedir
                // a entrega aos demais clientes.
            }
        }
    }

    /// <summary>Envia um blob e, em seguida, o envelope de metadados cifrado.</summary>
    public async Task BroadcastBlobAsync(byte[] data, Func<string, Envelope> metadataFactory, CancellationToken ct = default)
    {
        List<ClientSession> targets;
        lock (_clientsLock)
        {
            targets = _clients.Where(static session => session.IsSecure && session.Cipher is not null).ToList();
        }

        foreach (var session in targets)
        {
            if (session.Socket.State != WebSocketState.Open || session.Cipher is null)
            {
                continue;
            }

            try
            {
                Envelope Encrypt(Envelope plaintext) => SecureEnvelopeCodec.Encrypt(plaintext, session.Cipher);
                var blobId = await BlobSender.SendAsync(session.Socket, session.Cipher, data, Encrypt, ct).ConfigureAwait(false);
                await SendAsync(session.Socket, Encrypt(metadataFactory(blobId)), ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is WebSocketException or IOException or ObjectDisposedException)
            {
                // Sessão morta: segue para o próximo cliente.
            }
        }
    }

    /// <param name="trustStore">
    /// Registro dos celulares já pareados. Sem ele, o desktop continua funcionando,
    /// mas toda reconexão exige um novo código de pareamento.
    /// </param>
    public ClipBridgeServer(
        int port = ProtocolInfo.DefaultPort,
        int discoveryPort = ProtocolInfo.DiscoveryPort,
        ITrustedDeviceStore? trustStore = null)
    {
        _port = port;
        _discoveryPort = discoveryPort;
        _trustStore = trustStore;
    }

    public void Start()
    {
        if (IsRunning)
        {
            return;
        }

        _cts = new CancellationTokenSource();
        // Escuta em todas as interfaces (LAN). Bind de porta TCP >= 1024 não exige
        // privilégios especiais no Windows, ao contrário do HttpListener.
        _listener = new TcpListener(IPAddress.Any, _port);
        _listener.Start();
        _running = true;
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));

        try
        {
            _discoverySocket = new UdpClient(_discoveryPort);
            _discoveryLoop = Task.Run(() => DiscoveryLoopAsync(_cts.Token));
        }
        catch (SocketException)
        {
            // Porta de descoberta em uso (ex.: outra instância): o servidor TCP
            // continua funcional; apenas a descoberta automática fica indisponível.
            _discoverySocket = null;
        }

        try
        {
            // Anúncio periódico em broadcast: permite ao celular achar o desktop
            // mesmo quando o firewall do Windows (perfil Público, comum em Wi-Fi)
            // bloqueia a sonda UDP de entrada na porta de descoberta.
            _announceSocket = new UdpClient { EnableBroadcast = true };
            _announceLoop = Task.Run(() => AnnounceLoopAsync(_cts.Token));
        }
        catch (SocketException)
        {
            _announceSocket = null;
        }
    }

    /// <summary>Quantidade de celulares que hoje podem reconectar sem código.</summary>
    public int TrustedDeviceCount => _trustStore?.Count ?? 0;

    /// <summary>
    /// Revoga a reconexão automática de todos os celulares e derruba as sessões
    /// em curso — a próxima retomada é recusada e volta a exigir código.
    /// </summary>
    public async Task RevokeTrustedDevicesAsync(CancellationToken ct = default)
    {
        _trustStore?.ForgetAll();

        List<ClientSession> sessions;
        lock (_clientsLock)
        {
            sessions = [.. _clients];
        }

        foreach (var session in sessions)
        {
            session.IsSecure = false;
            if (session.Socket.State != WebSocketState.Open)
            {
                continue;
            }

            try
            {
                await session.Socket.CloseAsync(
                    WebSocketCloseStatus.NormalClosure, "trust revoked", ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is WebSocketException or IOException or ObjectDisposedException)
            {
                // Sessão já morta: nada a encerrar.
            }
        }
    }

    /// <summary>Gera um código temporário exibido pelo desktop para o pareamento.</summary>
    public PairingInvitation BeginPairing(TimeSpan? lifetime = null)
    {
        var pairing = new PairingCoordinator(lifetime);
        lock (_pairingLock)
        {
            _pairing?.Dispose();
            _pairing = pairing;
        }

        return pairing.Invitation;
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        var listener = _listener ?? throw new InvalidOperationException("Servidor não inicializado.");
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (Exception) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (SocketException)
            {
                continue;
            }

            _ = Task.Run(() => HandleClientAsync(client, ct), ct);
        }
    }

    private async Task DiscoveryLoopAsync(CancellationToken ct)
    {
        var socket = _discoverySocket ?? throw new InvalidOperationException("Descoberta não inicializada.");
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var request = await socket.ReceiveAsync(ct).ConfigureAwait(false);
                if (Encoding.UTF8.GetString(request.Buffer) != "clipbridge.discover.v1")
                {
                    continue;
                }

                var response = Encoding.UTF8.GetBytes(ProtocolInfo.BuildAnnounce(_port));
                await socket.SendAsync(response, request.RemoteEndPoint, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException)
            {
                // Falha transitória de rede; continua ouvindo.
            }
        }
    }

    private async Task AnnounceLoopAsync(CancellationToken ct)
    {
        var socket = _announceSocket ?? throw new InvalidOperationException("Anúncio não inicializado.");
        var payload = Encoding.UTF8.GetBytes(ProtocolInfo.BuildAnnounce(_port));
        while (!ct.IsCancellationRequested)
        {
            foreach (var target in NetworkInfo.GetBroadcastAddresses())
            {
                try
                {
                    await socket.SendAsync(payload, new IPEndPoint(target, ProtocolInfo.AnnouncePort), ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                catch (SocketException)
                {
                    // Interface sem rota de broadcast; tenta as demais.
                }
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        WebSocket socket;
        NetworkStream stream;
        try
        {
            client.NoDelay = true;
            stream = client.GetStream();
            if (!await TryPerformHandshakeAsync(stream, ct).ConfigureAwait(false))
            {
                client.Dispose();
                return;
            }

            socket = WebSocket.CreateFromStream(
                stream,
                isServer: true,
                subProtocol: null,
                keepAliveInterval: TimeSpan.FromSeconds(30));
        }
        catch
        {
            client.Dispose();
            return;
        }

        ClientConnectionChanged?.Invoke(this, true);
        var buffer = new byte[ProtocolInfo.DefaultChunkSize];
        var session = new ClientSession(socket);
        lock (_clientsLock)
        {
            _clients.Add(session);
        }

        try
        {
            while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var result = await socket.ReceiveAsync(buffer, ct).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", ct).ConfigureAwait(false);
                    break;
                }

                if (result.MessageType == WebSocketMessageType.Binary)
                {
                    session.BinaryAccumulator.Write(buffer, 0, result.Count);
                    if (!result.EndOfMessage)
                    {
                        continue;
                    }

                    var frame = session.BinaryAccumulator.ToArray();
                    session.BinaryAccumulator.SetLength(0);
                    HandleBinaryChunk(session, frame);
                    continue;
                }

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    // Mensagens de texto podem exceder o buffer e chegar em vários
                    // frames; acumula até EndOfMessage antes de desserializar.
                    session.TextAccumulator.Write(buffer, 0, result.Count);
                    if (!result.EndOfMessage)
                    {
                        continue;
                    }

                    var json = Encoding.UTF8.GetString(session.TextAccumulator.GetBuffer(), 0, (int)session.TextAccumulator.Length);
                    session.TextAccumulator.SetLength(0);

                    Envelope? envelope;
                    try
                    {
                        envelope = MessageSerializer.Deserialize(json);
                    }
                    catch (System.Text.Json.JsonException)
                    {
                        continue; // Mensagem malformada: ignora sem derrubar a conexão.
                    }

                    if (envelope is not null)
                    {
                        await HandleEnvelopeAsync(session, socket, envelope, ct).ConfigureAwait(false);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Encerramento normal.
        }
        catch (WebSocketException)
        {
            // Conexão interrompida pelo cliente; encerra a sessão.
        }
        catch (IOException)
        {
            // Stream subjacente fechado; encerra a sessão.
        }
        finally
        {
            lock (_clientsLock)
            {
                _clients.Remove(session);
            }

            session.Dispose();
            ClientConnectionChanged?.Invoke(this, false);
            socket.Dispose();
            client.Dispose();
        }
    }

    private async Task HandleEnvelopeAsync(ClientSession session, WebSocket socket, Envelope envelope, CancellationToken ct)
    {
        if (envelope.Type == MessageType.PairRequest)
        {
            session.Cipher?.Dispose();
            session.Cipher = await HandlePairRequestAsync(session, socket, envelope, ct).ConfigureAwait(false);
            return;
        }

        if (envelope.Type == MessageType.PairConfirm)
        {
            session.IsSecure = await HandlePairConfirmationAsync(
                socket, envelope, session.Cipher, ct).ConfigureAwait(false);
            if (session.IsSecure)
            {
                RememberTrust(session);
                SecureSessionEstablished?.Invoke(this, EventArgs.Empty);
            }

            return;
        }

        if (envelope.Type == MessageType.SessionResume)
        {
            session.Cipher?.Dispose();
            session.Cipher = await HandleResumeRequestAsync(session, socket, envelope, ct).ConfigureAwait(false);
            return;
        }

        if (envelope.Type == MessageType.SessionResumeConfirm)
        {
            session.IsSecure = await HandleResumeConfirmationAsync(session, socket, envelope, ct).ConfigureAwait(false);
            if (session.IsSecure)
            {
                // Cada conexão bem-sucedida empurra o vencimento para frente:
                // a janela de 72h conta da última conexão, não do pareamento.
                if (session.TrustedDeviceId is { } deviceId)
                {
                    _trustStore?.Renew(deviceId);
                }

                SecureSessionEstablished?.Invoke(this, EventArgs.Empty);
            }

            return;
        }

        if (envelope.Type == MessageType.Hello && !session.IsSecure)
        {
            return;
        }

        if (!session.IsSecure)
        {
            await SendAsync(socket, Envelope.Create(
                MessageType.Error,
                new ErrorPayload("auth.failed", "O pareamento deve ser concluído antes de enviar dados.")), ct).ConfigureAwait(false);
            return;
        }

        if (session.Cipher is null)
        {
            await SendAsync(socket, Envelope.Create(
                MessageType.Error,
                new ErrorPayload("auth.failed", "Sessão segura indisponível.")), ct).ConfigureAwait(false);
            return;
        }

        if (!SecureEnvelopeCodec.TryDecrypt(envelope, session.Cipher, out var decrypted))
        {
            await SendAsync(socket, Envelope.Create(
                MessageType.Error,
                new ErrorPayload("auth.failed", "Payload inválido ou não cifrado.")), ct).ConfigureAwait(false);
            return;
        }

        if (TryHandleBlobControlMessage(session, decrypted))
        {
            return;
        }

        if (decrypted.Type == MessageType.Ping)
        {
            await SendAsync(
                socket,
                SecureEnvelopeCodec.Encrypt(Envelope.Create(MessageType.Pong, new PingPayload()), session.Cipher!),
                ct).ConfigureAwait(false);
            return;
        }

        MessageReceived?.Invoke(this, decrypted);
    }

    /// <summary>Lê o request HTTP de upgrade e responde 101 Switching Protocols.</summary>
    private static async Task<bool> TryPerformHandshakeAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[MaxHandshakeBytes];
        var total = 0;
        var headerEnd = -1;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), ct).ConfigureAwait(false);
            if (read == 0)
            {
                return false;
            }

            total += read;
            headerEnd = FindHeaderEnd(buffer, total);
            if (headerEnd >= 0)
            {
                break;
            }
        }

        if (headerEnd < 0)
        {
            return false;
        }

        var request = Encoding.ASCII.GetString(buffer, 0, headerEnd);
        var key = ExtractHeader(request, "Sec-WebSocket-Key");
        if (string.IsNullOrEmpty(key) ||
            !request.Contains("upgrade: websocket", StringComparison.OrdinalIgnoreCase))
        {
            var reject = "HTTP/1.1 400 Bad Request\r\nConnection: close\r\n\r\n"u8.ToArray();
            await stream.WriteAsync(reject, ct).ConfigureAwait(false);
            return false;
        }

        var accept = Convert.ToBase64String(
            SHA1.HashData(Encoding.ASCII.GetBytes(key + WebSocketGuid)));
        var response =
            "HTTP/1.1 101 Switching Protocols\r\n" +
            "Upgrade: websocket\r\n" +
            "Connection: Upgrade\r\n" +
            $"Sec-WebSocket-Accept: {accept}\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(response), ct).ConfigureAwait(false);
        return true;
    }

    private static int FindHeaderEnd(byte[] buffer, int length)
    {
        for (var i = 3; i < length; i++)
        {
            if (buffer[i - 3] == '\r' && buffer[i - 2] == '\n' && buffer[i - 1] == '\r' && buffer[i] == '\n')
            {
                return i + 1;
            }
        }

        return -1;
    }

    private static string? ExtractHeader(string request, string name)
    {
        foreach (var line in request.Split("\r\n"))
        {
            var separator = line.IndexOf(':');
            if (separator > 0 &&
                line.AsSpan(0, separator).Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return line[(separator + 1)..].Trim();
            }
        }

        return null;
    }

    private void HandleBinaryChunk(ClientSession session, byte[] frame)
    {
        if (!session.IsSecure || session.Cipher is null)
        {
            return;
        }

        if (!BlobChunkCodec.TryDecodeChunk(frame, session.Cipher, out var blobIdBytes, out var sequence, out var plaintext))
        {
            return;
        }

        var blobId = BlobChunkCodec.ToBlobIdString(blobIdBytes);
        session.BlobReceiver.TryAddChunk(blobId, sequence, plaintext);
    }

    private bool TryHandleBlobControlMessage(ClientSession session, Envelope envelope)
    {
        switch (envelope.Type)
        {
            case MessageType.BlobBegin:
                var begin = envelope.PayloadAs<BlobBeginPayload>();
                return begin is not null && session.BlobReceiver.TryBegin(begin);

            case MessageType.BlobEnd:
                var end = envelope.PayloadAs<BlobEndPayload>();
                if (end is null)
                {
                    return false;
                }

                var data = session.BlobReceiver.TryComplete(end);
                if (data is not null)
                {
                    BlobCompleted?.Invoke(this, new BlobTransferCompleted(end.BlobId, data));
                }

                return true;

            default:
                return false;
        }
    }

    /// <summary>
    /// Persiste o vínculo recém-criado: a partir daqui o celular reconecta sem
    /// código enquanto a confiança valer.
    /// </summary>
    private void RememberTrust(ClientSession session)
    {
        var resumeKey = session.PendingResumeKey;
        if (_trustStore is null || resumeKey is null)
        {
            return;
        }

        var deviceId = ResumeHandshake.ComputeDeviceId(resumeKey);
        _trustStore.Remember(deviceId, resumeKey);
        session.TrustedDeviceId = deviceId;
    }

    /// <summary>
    /// Retomada: valida o vínculo, mistura um ECDH efêmero novo com a chave de
    /// retomada e devolve a prova de que este desktop é mesmo o par confiado.
    /// </summary>
    private async Task<SessionCipher?> HandleResumeRequestAsync(
        ClientSession session,
        WebSocket socket,
        Envelope envelope,
        CancellationToken ct)
    {
        var request = envelope.PayloadAs<SessionResumePayload>();
        var trusted = request is null ? null : _trustStore?.Find(request.DeviceId);
        if (request is null || trusted is null)
        {
            // Vínculo desconhecido ou vencido: o celular deve refazer o pareamento.
            await SendAsync(socket, Envelope.Create(
                MessageType.Error,
                new ErrorPayload("resume.denied", "Vínculo desconhecido ou expirado — refaça o pareamento.")), ct).ConfigureAwait(false);
            return null;
        }

        try
        {
            var clientNonce = Convert.FromBase64String(request.Nonce);
            if (clientNonce.Length != ResumeHandshake.NonceSizeBytes)
            {
                throw new ArgumentException("Nonce de retomada com tamanho inválido.");
            }

            var serverNonce = RandomNumberGenerator.GetBytes(ResumeHandshake.NonceSizeBytes);
            var salt = ResumeHandshake.BuildSalt(clientNonce, serverNonce);

            using var keyAgreement = new X25519KeyAgreement();
            var ecdhKey = keyAgreement.DeriveSessionKey(
                Convert.FromBase64String(request.PubKey), salt, ResumeHandshake.EcdhSessionInfo);

            byte[] sessionKey;
            try
            {
                sessionKey = ResumeHandshake.DeriveSessionKey(ecdhKey, trusted.ResumeKey, salt);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(ecdhKey);
            }

            var cipher = new SessionCipher(sessionKey);
            CryptographicOperations.ZeroMemory(sessionKey);

            session.TrustedDeviceId = trusted.DeviceId;
            session.ExpectedClientProof = ResumeHandshake.ClientProof(trusted.ResumeKey, salt);

            await SendAsync(socket, Envelope.Create(
                MessageType.SessionResumed,
                new SessionResumedPayload(
                    Convert.ToBase64String(keyAgreement.PublicKey.Span),
                    Convert.ToBase64String(serverNonce),
                    Convert.ToBase64String(ResumeHandshake.ServerProof(trusted.ResumeKey, salt)))), ct).ConfigureAwait(false);
            return cipher;
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or CryptographicException)
        {
            await SendAsync(socket, Envelope.Create(
                MessageType.Error,
                new ErrorPayload("resume.denied", "Pedido de retomada inválido.")), ct).ConfigureAwait(false);
            return null;
        }
    }

    /// <summary>
    /// Fecha a retomada: a prova do celular chega cifrada com a chave recém-derivada,
    /// então decifrar já demonstra posse — o HMAC confirma em tempo constante.
    /// </summary>
    private async Task<bool> HandleResumeConfirmationAsync(
        ClientSession session,
        WebSocket socket,
        Envelope envelope,
        CancellationToken ct)
    {
        var expected = session.ExpectedClientProof;
        if (session.Cipher is null || expected is null)
        {
            await SendAsync(socket, Envelope.Create(
                MessageType.Error,
                new ErrorPayload("resume.denied", "Envie session.resume antes de confirmar.")), ct).ConfigureAwait(false);
            return false;
        }

        if (!SecureEnvelopeCodec.TryDecrypt(envelope, session.Cipher, out var decrypted) ||
            decrypted.PayloadAs<SessionResumeConfirmPayload>() is not { } confirmation ||
            !TryVerifyProof(confirmation.Proof, expected))
        {
            session.ExpectedClientProof = null;
            await SendAsync(socket, Envelope.Create(
                MessageType.Error,
                new ErrorPayload("resume.denied", "Prova de retomada inválida.")), ct).ConfigureAwait(false);
            return false;
        }

        session.ExpectedClientProof = null;
        await SendAsync(socket, Envelope.Create(MessageType.Ack, new AckPayload(envelope.Id)), ct).ConfigureAwait(false);
        return true;
    }

    private static bool TryVerifyProof(string suppliedBase64, byte[] expected)
    {
        try
        {
            return CryptographicOperations.FixedTimeEquals(Convert.FromBase64String(suppliedBase64), expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private async Task<SessionCipher?> HandlePairRequestAsync(
        ClientSession session,
        WebSocket socket,
        Envelope envelope,
        CancellationToken ct)
    {
        PairingCoordinator? pairing;
        lock (_pairingLock)
        {
            pairing = _pairing;
        }

        if (pairing is null)
        {
            await SendAsync(socket, Envelope.Create(
                MessageType.Error,
                new ErrorPayload("auth.failed", "Não há convite de pareamento ativo.")), ct).ConfigureAwait(false);
            return null;
        }

        try
        {
            var request = envelope.PayloadAs<PairRequestPayload>()
                ?? throw new ArgumentException("Pedido de pareamento sem payload.");
            var sessionCipher = new SessionCipher(pairing.DeriveSessionKey(request));
            // Guardada só na memória da conexão até o código ser aceito — um
            // pedido não confirmado nunca vira vínculo persistido.
            session.PendingResumeKey = pairing.DeriveResumeKey(request);
            await SendAsync(socket, Envelope.Create(MessageType.PairResponse, pairing.CreateResponse()), ct).ConfigureAwait(false);
            return sessionCipher;
        }
        catch (Exception)
        {
            await SendAsync(socket, Envelope.Create(
                MessageType.Error,
                new ErrorPayload("auth.failed", "Pedido de pareamento inválido.")), ct).ConfigureAwait(false);
            return null;
        }
    }

    private async Task<bool> HandlePairConfirmationAsync(
        WebSocket socket,
        Envelope envelope,
        SessionCipher? sessionCipher,
        CancellationToken ct)
    {
        if (sessionCipher is null)
        {
            await SendAsync(socket, Envelope.Create(
                MessageType.Error,
                new ErrorPayload("auth.failed", "Envie pair.request antes de pair.confirm.")), ct).ConfigureAwait(false);
            return false;
        }

        var confirmation = envelope.PayloadAs<PairConfirmPayload>();
        var accepted = confirmation is not null && TryConsumePairingCode(confirmation.Code);
        if (!accepted)
        {
            await SendAsync(socket, Envelope.Create(
                MessageType.Error,
                new ErrorPayload("auth.failed", "Código de pareamento inválido ou expirado.")), ct).ConfigureAwait(false);
            return false;
        }

        await SendAsync(socket, Envelope.Create(MessageType.Ack, new AckPayload(envelope.Id)), ct).ConfigureAwait(false);
        return true;
    }

    private bool TryConsumePairingCode(string code)
    {
        lock (_pairingLock)
        {
            return _pairing?.TryConfirmCode(code) ?? false;
        }
    }

    private static Task SendAsync(WebSocket socket, Envelope envelope, CancellationToken ct) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(MessageSerializer.Serialize(envelope)), WebSocketMessageType.Text, true, ct);

    public async Task StopAsync()
    {
        if (_cts is null)
        {
            return;
        }

        _running = false;
        await _cts.CancelAsync().ConfigureAwait(false);
        _discoverySocket?.Dispose();
        _discoverySocket = null;
        _announceSocket?.Dispose();
        _announceSocket = null;
        _listener?.Stop();
        _listener = null;

        if (_acceptLoop is not null)
        {
            try
            {
                await _acceptLoop.ConfigureAwait(false);
            }
            catch
            {
                // Ignorado no shutdown.
            }
        }

        if (_discoveryLoop is not null)
        {
            try
            {
                await _discoveryLoop.ConfigureAwait(false);
            }
            catch
            {
                // Ignorado no shutdown.
            }
        }

        if (_announceLoop is not null)
        {
            try
            {
                await _announceLoop.ConfigureAwait(false);
            }
            catch
            {
                // Ignorado no shutdown.
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _cts?.Dispose();
        lock (_pairingLock)
        {
            _pairing?.Dispose();
            _pairing = null;
        }
    }
}
