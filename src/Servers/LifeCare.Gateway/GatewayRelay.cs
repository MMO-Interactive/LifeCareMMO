using System.Diagnostics;
using StormChase.Networking;
using StormChase.Protocol;

namespace LifeCare.Gateway;

/// <summary>
/// The public game port. Each player's frames are forwarded unchanged over one TCP link per district host, so a request
/// makes exactly one internal hop: client → Gateway → the host that owns the player.
/// </summary>
public sealed class GatewayRelay(GatewayOptions options, GatewayDirectory directory, ILogger<GatewayRelay> logger) : BackgroundService
{
    private readonly AutoResetEvent signal = new(false);
    private readonly Dictionary<uint, DateTimeOffset> pending = [];
    private readonly Dictionary<uint, Session> sessions = [];
    private readonly Dictionary<string, RelayLinkClient> links = new(StringComparer.Ordinal);
    private StormTransportHost? transport;
    private int playerCount, linkCount;
    public bool Ready { get; private set; }
    public int PlayerCount => Volatile.Read(ref playerCount);
    public int LinkCount => Volatile.Read(ref linkCount);

    protected override Task ExecuteAsync(CancellationToken token)
    {
        var identity = TransportIdentity.LoadOrCreate(Path.Combine(options.DataRoot, "gateway-transport-identity.json"));
        directory.TransportIdentity = identity.Identity; directory.TransportAuthority = identity.AuthorityCertificateBase64;
        transport = StormTransportHost.CreateRelayServer((ushort)options.GamePort, options.MaxPlayers, identity.ServerSecurity, signal);
        logger.LogInformation("Gateway game port {Port} ready", options.GamePort);
        Ready = true;
        return Task.Factory.StartNew(() => Run(token), token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    private void Run(CancellationToken token)
    {
        var housekeeping = Stopwatch.StartNew();
        try
        {
            while (!token.IsCancellationRequested)
            {
                signal.WaitOne(2);
                foreach (var e in transport!.Poll(1024)) FromClient(e);
                foreach (var (serverId, link) in links.ToArray()) FromHost(serverId, link);
                if (housekeeping.ElapsedMilliseconds > 1000) { housekeeping.Restart(); ExpirePending(); }
                Volatile.Write(ref playerCount, sessions.Count); Volatile.Write(ref linkCount, links.Count);
            }
        }
        finally
        {
            foreach (var link in links.Values) link.Dispose();
            transport?.Dispose(); Ready = false;
        }
    }

    private void FromClient(TransportEvent e)
    {
        switch (e.Kind)
        {
            case TransportEventKind.Connected: pending[e.ConnectionId] = DateTimeOffset.UtcNow; break;
            case TransportEventKind.Disconnected:
                pending.Remove(e.ConnectionId);
                if (sessions.Remove(e.ConnectionId, out var gone) && links.TryGetValue(gone.ServerId, out var link)) link.Leave(e.ConnectionId, e.Reason);
                break;
            case TransportEventKind.Message when e.Raw is { } frame:
                if (sessions.TryGetValue(e.ConnectionId, out var session))
                {
                    ProtocolCodec.TryReadHeader(frame, out _, out bool reliable);
                    if (links.TryGetValue(session.ServerId, out var open)) open.Forward(e.ConnectionId, frame, reliable);
                }
                else if (pending.ContainsKey(e.ConnectionId)) Admit(e.ConnectionId, frame);
                break;
        }
    }

    /// <summary>The first frame must be a JoinRequest whose ticket this Gateway issued; it picks the district host for the whole session.</summary>
    private void Admit(uint connection, byte[] frame)
    {
        if (!ProtocolCodec.TryDecode(frame, out var message) || message!.Kind != MessageKind.JoinRequest) { Reject(connection, "Send a join request first."); return; }
        string? serverId = directory.ServerForTicket(message.Get<JoinRequest>().Ticket);
        if (serverId is null || directory.Route(serverId) is not { } route) { Reject(connection, "Admission ticket is invalid, expired or already used."); return; }
        if (!links.TryGetValue(serverId, out var link) || link.Faulted)
        {
            link?.Dispose();
            try { link = new RelayLinkClient(route.Host, route.Port, options.LinkKey, signal); links[serverId] = link; }
            catch (Exception error) { logger.LogWarning("District host {Server} relay link failed: {Reason}", serverId, error.Message); Reject(connection, "The game server is not reachable. Try again shortly."); return; }
        }
        pending.Remove(connection);
        sessions[connection] = new(serverId);
        link.Open(connection); link.Forward(connection, frame, true);
    }

    private void FromHost(string serverId, RelayLinkClient link)
    {
        while (link.TryTake(out var item))
        {
            if (!sessions.ContainsKey(item.Connection)) continue;
            if (item.Type == RelayFrames.Message) transport!.SendRaw(item.Connection, item.Payload);
            else if (item.Type == RelayFrames.Close)
            { sessions.Remove(item.Connection); transport!.Close(item.Connection, System.Text.Encoding.UTF8.GetString(item.Payload)); }
        }
        if (!link.Faulted) return;
        logger.LogWarning("District host {Server} relay link closed: {Reason}", serverId, link.LastError);
        foreach (var connection in sessions.Where(s => s.Value.ServerId == serverId).Select(s => s.Key).ToArray())
        { sessions.Remove(connection); transport!.Close(connection, "The game server stopped."); }
        links.Remove(serverId); link.Dispose();
    }

    private void Reject(uint connection, string reason)
    {
        pending.Remove(connection);
        transport!.Send(connection, MessageKind.JoinRejected, new JoinRejected(reason));
        transport.Close(connection, reason);
    }

    private void ExpirePending()
    {
        foreach (var connection in pending.Where(p => DateTimeOffset.UtcNow - p.Value > TimeSpan.FromSeconds(10)).Select(p => p.Key).ToArray())
        { pending.Remove(connection); transport!.Close(connection, "Admission timeout"); }
    }

    private sealed record Session(string ServerId);
}
