using System.Net;
using System.Net.WebSockets;
using System.Text;
using ClipBridge.Core.Protocol;
using ClipBridge.Core.Security;

namespace ClipBridge.Core.Net;

/// <summary>
/// Servidor WebSocket do ClipBridge. Aceita conexões dos dispositivos pareados
/// na LAN e faz o roteamento de envelopes do protocolo.
/// </summary>
/// <remarks>
/// Esqueleto da fundação: a aceitação de conexões e o loop de leitura já são
/// funcionais; o handshake de pareamento, a sessão cifrada e o roteamento
/// completo são implementados nas próximas etapas do roadmap.
/// </remarks>
public sealed class ClipBridgeServer : IAsyncDisposable
{
    private readonly HttpListener _listener = new();
    private readonly int _port;
    private readonly object _pairingLock = new();
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;
    private PairingCoordinator? _pairing;

    /// <summary>Disparado quando um envelope é recebido de um cliente.</summary>
    public event EventHandler<Envelope>? MessageReceived;

    /// <summary>Disparado quando um cliente conecta ou desconecta.</summary>
    public event EventHandler<bool>? ClientConnectionChanged;

    /// <summary>Disparado depois que um cliente conclui o handshake de pareamento.</summary>
    public event EventHandler? SecureSessionEstablished;

    public bool IsRunning => _listener.IsListening;

    public ClipBridgeServer(int port = ProtocolInfo.DefaultPort)
    {
        _port = port;
        // Bind restrito à máquina local; o confinamento à sub-rede é reforçado por firewall.
        _listener.Prefixes.Add($"http://+:{_port}/");
    }

    public void Start()
    {
        if (IsRunning)
        {
            return;
        }

        _cts = new CancellationTokenSource();
        _listener.Start();
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    /// <summary>Cria um convite temporário para ser exibido como QR Code no desktop.</summary>
    public PairingInvitation BeginPairing(string host, TimeSpan? lifetime = null)
    {
        var pairing = new PairingCoordinator(host, _port, lifetime);
        lock (_pairingLock)
        {
            _pairing?.Dispose();
            _pairing = pairing;
        }

        return pairing.Invitation;
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception) when (ct.IsCancellationRequested)
            {
                break;
            }

            if (!context.Request.IsWebSocketRequest)
            {
                context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                context.Response.Close();
                continue;
            }

            _ = Task.Run(() => HandleClientAsync(context, ct), ct);
        }
    }

    private async Task HandleClientAsync(HttpListenerContext context, CancellationToken ct)
    {
        WebSocket socket;
        try
        {
            var wsContext = await context.AcceptWebSocketAsync(subProtocol: null).ConfigureAwait(false);
            socket = wsContext.WebSocket;
        }
        catch
        {
            context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
            context.Response.Close();
            return;
        }

        ClientConnectionChanged?.Invoke(this, true);
        var buffer = new byte[ProtocolInfo.DefaultChunkSize];
        SessionCipher? sessionCipher = null;
        var secure = false;

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

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    var envelope = MessageSerializer.Deserialize(json);
                    if (envelope is not null)
                    {
                        if (envelope.Type == MessageType.PairRequest)
                        {
                            sessionCipher?.Dispose();
                            sessionCipher = await HandlePairRequestAsync(socket, envelope, ct).ConfigureAwait(false);
                            continue;
                        }

                        if (envelope.Type == MessageType.PairConfirm)
                        {
                            secure = await HandlePairConfirmationAsync(socket, envelope, sessionCipher, ct).ConfigureAwait(false);
                            if (secure)
                            {
                                SecureSessionEstablished?.Invoke(this, EventArgs.Empty);
                            }

                            continue;
                        }

                        if (envelope.Type == MessageType.Hello && !secure)
                        {
                            continue;
                        }

                        if (!secure)
                        {
                            await SendAsync(socket, Envelope.Create(
                                MessageType.Error,
                                new ErrorPayload("auth.failed", "O pareamento deve ser concluído antes de enviar dados.")), ct).ConfigureAwait(false);
                            continue;
                        }

                        MessageReceived?.Invoke(this, envelope);
                    }
                }
                // TODO(roadmap): montar blobs binários (blob.chunk) e roteá-los.
            }
        }
        catch (OperationCanceledException)
        {
            // Encerramento normal.
        }
        finally
        {
            sessionCipher?.Dispose();
            ClientConnectionChanged?.Invoke(this, false);
            socket.Dispose();
        }
    }

    private async Task<SessionCipher?> HandlePairRequestAsync(WebSocket socket, Envelope envelope, CancellationToken ct)
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
        var accepted = confirmation is not null && TryConsumePairingToken(confirmation.Token);
        if (!accepted)
        {
            await SendAsync(socket, Envelope.Create(
                MessageType.Error,
                new ErrorPayload("auth.failed", "Token de pareamento inválido ou expirado.")), ct).ConfigureAwait(false);
            return false;
        }

        await SendAsync(socket, Envelope.Create(MessageType.Ack, new AckPayload(envelope.Id)), ct).ConfigureAwait(false);
        return true;
    }

    private bool TryConsumePairingToken(string token)
    {
        lock (_pairingLock)
        {
            return _pairing?.TryConfirmToken(token) ?? false;
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

        await _cts.CancelAsync().ConfigureAwait(false);
        if (_listener.IsListening)
        {
            _listener.Stop();
        }

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
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _listener.Close();
        _cts?.Dispose();
        lock (_pairingLock)
        {
            _pairing?.Dispose();
            _pairing = null;
        }
    }
}
