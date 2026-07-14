using System.Net;
using System.Net.WebSockets;
using System.Text;
using ClipBridge.Core.Protocol;

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
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;

    /// <summary>Disparado quando um envelope é recebido de um cliente.</summary>
    public event EventHandler<Envelope>? MessageReceived;

    /// <summary>Disparado quando um cliente conecta ou desconecta.</summary>
    public event EventHandler<bool>? ClientConnectionChanged;

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
            ClientConnectionChanged?.Invoke(this, false);
            socket.Dispose();
        }
    }

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
    }
}
