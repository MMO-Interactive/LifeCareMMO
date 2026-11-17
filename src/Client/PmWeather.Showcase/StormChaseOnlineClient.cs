using System.Collections.Concurrent;
using System.Diagnostics;
using StormChase.Networking;
using StormChase.Protocol;

internal sealed record OnlineChaseState(uint PlayerId, Guid CharacterId, CareerProgress Career,
    WorldSnapshot World, IReadOnlyList<DebrisBodyState> Debris, long BaselineRevision,
    bool BaselineReady, string Message, long ReceivedSnapshots)
{
    public PlayerState? Player => World.Players.FirstOrDefault(player => player.PlayerId == PlayerId);
    public VehicleState? Vehicle => World.Vehicles.FirstOrDefault(vehicle => vehicle.VehicleId == Player?.VehicleId);
}

/// <summary>Authenticated native transport and immutable server state; no client physics or weather simulation.</summary>
internal sealed class StormChaseOnlineClient : IDisposable
{
    private readonly StormTransportHost transport;
    private readonly AuthoritativeDebrisReplica debris = new();
    private readonly AuthoritativeAtmosphereReplica atmosphere = new(StormChaseOnlineAtmosphere.Reconstruct);
    private readonly ConcurrentQueue<GameplayAction> actions = new();
    private readonly ConcurrentQueue<ActionResult> receipts = new();
    private readonly ConcurrentQueue<DestructionDelta> flashes = new();
    private readonly ConcurrentQueue<ChatMessage> chatIn = new();
    private readonly ConcurrentQueue<ChatSend> chatOut = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly TaskCompletionSource<OnlineChaseState> admission = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread worker;
    private readonly uint connection;
    private readonly JoinRequest request;
    private readonly DateTimeOffset ticketExpires;
    private OnlineChaseState? current;
    private ClientInput requestedInput = new(0, default, 0, 0, PlayerMovementMode.OnFoot, 0, false);
    private Exception? failure;
    private long inputTick, requestId, snapshots, lastFlash;
    private int actionCount, disposed;
    private bool accepted;
    public OnlineChaseState? Current => Volatile.Read(ref current);
    public Task<OnlineChaseState> Admission => admission.Task;
    public string? Error => Volatile.Read(ref failure)?.Message;
    public IReadOnlyList<ActionResult> DrainReceipts()
    { var result = new List<ActionResult>(); while (receipts.TryDequeue(out var item)) result.Add(item); return result; }
    /// <summary>Chat lines the district delivered since the last call.</summary>
    public IReadOnlyList<ChatMessage> DrainChat()
    { var result = new List<ChatMessage>(); while (chatIn.TryDequeue(out var item)) result.Add(item); return result; }
    /// <summary>Queues a chat line for the district; false when it is empty, too long or too many are waiting.</summary>
    public bool SendChat(ChatChannel channel, string text)
    {
        text = text.Trim();
        if (text.Length is 0 or > ProtocolCodec.ChatLimit || chatOut.Count >= 8) return false;
        chatOut.Enqueue(new(channel, text)); return true;
    }
    public IReadOnlyList<DestructionDelta> DrainPowerFlashes()
    { var result = new List<DestructionDelta>(); while (flashes.TryDequeue(out var item)) result.Add(item); return result; }

    public StormChaseOnlineClient(ChaseServerInfo server, ChaseJoinTicket ticket, string contentFingerprint)
    {
        if (!server.Ready || string.IsNullOrWhiteSpace(server.TransportAuthority) || string.IsNullOrWhiteSpace(server.TransportIdentity))
            throw new InvalidOperationException("The selected server has no ready authenticated native transport.");
        if (!string.Equals(server.WorldContentFingerprint, contentFingerprint, StringComparison.Ordinal))
            throw new InvalidOperationException("World assets differ from the server. Install the same game version.");
        if (ticket.GamePort is < 1 or > 65535 || ticket.ExpiresUtc <= DateTimeOffset.UtcNow || ticket.ServerId != server.ServerId)
            throw new InvalidOperationException("The server admission ticket expired or belongs to another server.");
        request = new(ticket.Ticket, contentFingerprint); ticketExpires = ticket.ExpiresUtc;
        transport = StormTransportHost.CreateClient(new(server.TransportAuthority, ExpectedPeerIdentity: server.TransportIdentity));
        try { connection = transport.Connect(ticket.Host, checked((ushort)ticket.GamePort)); }
        catch { transport.Dispose(); throw; }
        worker = new(Pump) { IsBackground = true, Name = "Storm Chase authoritative client" }; worker.Start();
    }

    public void SetInput(float yaw, float pitch, float forward, float strafe, float turn, bool sprint, bool brake, bool jump)
    {
        var state = Current; var player = state?.Player;
        Volatile.Write(ref requestedInput, new(0, player?.Position ?? default, yaw, Math.Clamp(pitch, -1.48f, 1.48f),
            player?.Mode ?? PlayerMovementMode.OnFoot, player?.VehicleId ?? 0, sprint,
            Math.Clamp(forward, -1, 1), Math.Clamp(strafe, -1, 1), Math.Clamp(turn, -1, 1), brake, jump));
    }

    public long SendAction(GameplayActionKind kind, int target = 0, int slot = -1, float placementYaw = 0)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (Interlocked.Increment(ref actionCount) > 32) { Interlocked.Decrement(ref actionCount); return 0; }
        long id = Interlocked.Increment(ref requestId);
        actions.Enqueue(new(id, kind, target, Current?.Player?.Position ?? default,slot,placementYaw)); return id;
    }

    public void ThrowIfFaulted()
    { if (Volatile.Read(ref failure) is { } error) throw new InvalidOperationException("Native multiplayer disconnected: " + error.Message, error); }

    private void Pump()
    {
        var timer = Stopwatch.StartNew(); double nextInput = 0, nextAction = 0, lastReceive = 0;
        bool connected = false, joined = false; GameplayAction? pending = null;
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                bool changed=false;
                foreach (var packet in transport.Poll())
                {
                    if (packet.ConnectionId != 0 && packet.ConnectionId != connection) continue;
                    if (packet.Kind == TransportEventKind.Connected) { connected = true; lastReceive = timer.Elapsed.TotalSeconds; }
                    else if (packet.Kind is TransportEventKind.Disconnected or TransportEventKind.Fault)
                        throw new IOException(string.IsNullOrWhiteSpace(packet.Reason) ? "Server connection ended." : packet.Reason);
                    else if (packet.Kind == TransportEventKind.Message && packet.Message is { } message)
                    {
                        lastReceive = timer.Elapsed.TotalSeconds;
                        switch (message.Kind)
                        {
                            case MessageKind.JoinRejected: throw new InvalidOperationException(message.Get<JoinRejected>().Reason);
                            case MessageKind.JoinAccepted:
                                var welcome = message.Get<JoinAccepted>(); accepted = true;
                                if (welcome.Snapshot.Environment is null) throw new InvalidDataException("Server omitted its authoritative environment.");
                                Volatile.Write(ref current, new(welcome.PlayerId, welcome.CharacterId, welcome.Career,
                                    atmosphere.Retain(welcome.Snapshot), [], -1, false, "Connected; assembling the server world…", ++snapshots));
                                if (welcome.Snapshot.Debris is { } initial) ApplyDebris(initial);
                                changed=true; break;
                            case MessageKind.WorldSnapshot:
                                var world = message.Get<WorldSnapshot>();
                                if (world.Debris is { } embedded) ApplyDebris(embedded);
                                if (Current is { } state && world.ServerTick >= state.World.ServerTick)
                                {
                                    if (world.Environment is null) throw new InvalidDataException("Server omitted its authoritative environment.");
                                    foreach (var flash in world.Destruction.Where(item => item.PowerFlash && item.EventId > lastFlash))
                                    { lastFlash = Math.Max(lastFlash, flash.EventId); if (flashes.Count < 64) flashes.Enqueue(flash); }
                                    Volatile.Write(ref current, state with { World = atmosphere.Retain(world), Career = world.Career ?? state.Career, ReceivedSnapshots = ++snapshots });
                                }
                                changed=true; break;
                            case MessageKind.AtmosphereSnapshot:
                                if (atmosphere.Apply(message.Get<AtmosphereSnapshot>()) && Current is { } atmosphericState)
                                {
                                    Volatile.Write(ref current, atmosphericState with { World = atmosphere.AttachCurrent(atmosphericState.World) });
                                    changed=true;
                                }
                                break;
                            case MessageKind.DebrisSnapshot: ApplyDebris(message.Get<DebrisSnapshot>()); changed=true; break;
                            case MessageKind.DebrisEvent:
                                if (debris.Apply(message.Get<DebrisEvent>()) == DebrisApplyResult.Invalid)
                                    throw new InvalidDataException("Invalid authoritative debris release.");
                                changed=true; break;
                            case MessageKind.ChatMessage:
                                if (chatIn.Count < 256) chatIn.Enqueue(message.Get<ChatMessage>());
                                break;
                            case MessageKind.ActionResult:
                                var result = message.Get<ActionResult>(); if (receipts.Count < 128) receipts.Enqueue(result);
                                if (Current is { } prior) Volatile.Write(ref current, prior with { Career = result.Career ?? prior.Career, Message = result.Message });
                                break;
                        }
                    }
                }
                // Coalesce motion chunks into one immutable catalogue publication per network pump.
                if(changed)Publish();
                double seconds = timer.Elapsed.TotalSeconds;
                if (connected && !joined)
                {
                    if (DateTimeOffset.UtcNow >= ticketExpires) throw new TimeoutException("Admission expired. Return to the menu and join again.");
                    joined = transport.Send(connection, MessageKind.JoinRequest, request);
                }
                if (accepted && seconds >= nextInput)
                {
                    transport.Send(connection, MessageKind.ClientInput,
                        Volatile.Read(ref requestedInput) with { ClientTick = Interlocked.Increment(ref inputTick) }, false);
                    nextInput = seconds + .05;
                }
                if (accepted && chatOut.TryPeek(out var chat) && transport.Send(connection, MessageKind.ChatSend, chat)) chatOut.TryDequeue(out _);
                if (accepted && seconds >= nextAction)
                {
                    if (pending is null && actions.TryDequeue(out pending)) Interlocked.Decrement(ref actionCount);
                    if (pending is not null && transport.Send(connection, MessageKind.GameplayAction, pending))
                    { pending = null; nextAction = seconds + .55; }
                }
                if ((!accepted && seconds > 25) || (accepted && seconds - lastReceive > 12))
                    throw new TimeoutException("The authoritative server stopped responding.");
                lifetime.Token.WaitHandle.WaitOne(5);
            }
        }
        catch (Exception error) when (!lifetime.IsCancellationRequested)
        { Volatile.Write(ref failure, error); admission.TrySetException(error); }
    }

    private void ApplyDebris(DebrisSnapshot snapshot)
    { if (debris.Apply(snapshot) == DebrisApplyResult.Invalid) throw new InvalidDataException("Invalid authoritative debris baseline or motion."); }
    private void Publish()
    {
        if (Current is not { } state) return;
        var value = state with { Debris = debris.Bodies, BaselineRevision = debris.BaselineRevision,
            BaselineReady = debris.CompletedBaseline, Message = debris.CompletedBaseline && !state.BaselineReady ? "Joined the authoritative chase." : state.Message };
        Volatile.Write(ref current, value);
        if (value.BaselineReady) admission.TrySetResult(value);
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        lifetime.Cancel(); worker.Join(); transport.Close(connection, "Returned to main menu"); transport.Dispose();
        admission.TrySetCanceled(); lifetime.Dispose();
    }
}
