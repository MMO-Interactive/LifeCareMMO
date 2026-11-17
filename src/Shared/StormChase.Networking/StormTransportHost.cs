using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using StormChase.Protocol;

namespace StormChase.Networking;

public enum TransportEventKind { Connected = 1, Disconnected = 2, Message = 3, Fault = 4 }
public sealed record TransportEvent(TransportEventKind Kind, uint ConnectionId, ProtocolMessage? Message = null, string Reason = "");

/// <summary>Encrypted GameNetworkingSockets IP transport. A dedicated pump keeps networking independent of rendering.</summary>
/// <remarks>Admission, trusted server identity, server authority, and persistence are application responsibilities.</remarks>
public sealed unsafe class StormTransportHost : IDisposable
{
    public const int MaxPendingEvents = 2048;
    public const int MaxPendingSends = 512;
    private const long MaxQueuedBytes = 8 * 1024 * 1024;
    private readonly ConcurrentQueue<QueuedEvent> _events = new();
    private readonly ConcurrentQueue<Outgoing> _sends = new();
    private readonly ConcurrentDictionary<uint, byte> _connections = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly Thread _pump;
    private nint _native;
    private int _eventCount, _sendCount, _disposed;
    private long _queuedBytes, _eventBytes, _sequence;
    private Outgoing? _blockedSend;
    private readonly TransportSecurity? _security;
    public bool IsServer { get; }
    public ushort BoundPort { get; }
    public int ConnectionCount => _connections.Count;
    public string? LastError { get; private set; }

    private StormTransportHost(ushort port, int maxConnections, TransportSecurity? security)
    {
        if (maxConnections is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(maxConnections));
        IsServer = port != 0;
        BoundPort = port;
        _security = security;
        var error = new byte[512];
        var certificate = security?.LocalCertificateBase64 is { } local ? Convert.FromBase64String(local) : [];
        if (certificate.Length > 4096 || security?.AuthorityCertificateBase64.Length > 8192 || security?.ExpectedPeerIdentity?.Length > 96) throw new ArgumentException("Certificate configuration exceeds supported bounds.", nameof(security));
        fixed (byte* p = error) fixed (byte* c = certificate) lock (NativeMethods.Gate)
            _native = NativeMethods.sc_host_create(port, maxConnections, security?.AuthorityCertificateBase64 ?? "", c, certificate.Length, security?.ExpectedPeerIdentity ?? "", p, error.Length);
        if (_native == 0) { _wake.Dispose(); _shutdown.Dispose(); throw new InvalidOperationException($"GameNetworkingSockets startup failed: {DecodeText(error)}"); }
        _pump = new(Pump) { IsBackground = true, Name = $"StormChase network {port}" };
        _pump.Start();
    }

    public static StormTransportHost CreateServer(ushort port, int maxConnections = 32, TransportSecurity? security = null) => port == 0 ? throw new ArgumentOutOfRangeException(nameof(port)) : new(port, maxConnections, security);
    public static StormTransportHost CreateClient(TransportSecurity? security = null) => new(0, 1, security);

    public uint Connect(string host, ushort port)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (IsServer) throw new InvalidOperationException("A server accepts connections instead of connecting.");
        if (port == 0) throw new ArgumentOutOfRangeException(nameof(port));
        var address = IPAddress.TryParse(host, out var ip) ? ip : Dns.GetHostAddresses(host).FirstOrDefault(x => x.AddressFamily == AddressFamily.InterNetwork) ?? throw new SocketException((int)SocketError.HostNotFound);
        if (!IPAddress.IsLoopback(address) && (_security is null || string.IsNullOrWhiteSpace(_security.AuthorityCertificateBase64) || string.IsNullOrWhiteSpace(_security.ExpectedPeerIdentity)))
            throw new InvalidOperationException("Remote connections require a server authority certificate and identity obtained through trusted HTTPS.");
        var endpoint = address.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{address}]:{port}" : $"{address}:{port}";
        uint result;
        lock (NativeMethods.Gate) result = NativeMethods.sc_connect(_native, endpoint);
        if (result == 0) throw new InvalidOperationException("GameNetworkingSockets rejected the connection address.");
        _wake.Set();
        return result;
    }

    /// <summary>Queues a message without blocking the game thread. False means bounded backpressure; retry reliable actions later.</summary>
    public bool Send<T>(uint connectionId, MessageKind kind, T payload, bool reliable = true)
    {
        if (Volatile.Read(ref _disposed) != 0 || connectionId == 0) return false;
        var bytes = ProtocolCodec.Encode(kind, (ulong)Interlocked.Increment(ref _sequence), payload);
        if (Interlocked.Increment(ref _sendCount) > MaxPendingSends)
        {
            Interlocked.Decrement(ref _sendCount);
            return false;
        }
        if (Interlocked.Add(ref _queuedBytes, bytes.Length) > MaxQueuedBytes)
        {
            Interlocked.Add(ref _queuedBytes, -bytes.Length);
            Interlocked.Decrement(ref _sendCount);
            return false;
        }
        _sends.Enqueue(new(connectionId, bytes, reliable));
        _wake.Set();
        return true;
    }

    public IReadOnlyList<TransportEvent> Poll(int maxEvents = 256)
    {
        if (maxEvents is < 1 or > MaxPendingEvents) throw new ArgumentOutOfRangeException(nameof(maxEvents));
        var result = new List<TransportEvent>(Math.Min(maxEvents, Volatile.Read(ref _eventCount)));
        while (result.Count < maxEvents && _events.TryDequeue(out var item)) { Interlocked.Decrement(ref _eventCount); Interlocked.Add(ref _eventBytes, -item.Bytes); result.Add(item.Event); }
        return result;
    }

    public void Close(uint connectionId, string reason = "Disconnected")
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        lock (NativeMethods.Gate) NativeMethods.sc_close(_native, connectionId, reason.Length > 128 ? reason[..128] : reason);
        _connections.TryRemove(connectionId, out _);
    }

    public string GetConnectionStatus(uint connectionId)
    {
        if (Volatile.Read(ref _disposed) != 0) return "Disposed";
        var bytes = new byte[8192];
        fixed (byte* p = bytes) lock (NativeMethods.Gate) NativeMethods.sc_status(_native, connectionId, p, bytes.Length);
        return DecodeText(bytes);
    }

    /// <summary>Process-wide native network simulator for repeatable integration testing; reset to zero afterward.</summary>
    public static void ConfigureNetworkSimulation(float packetLossPercent = 0, int lagMilliseconds = 0)
    {
        if (!float.IsFinite(packetLossPercent) || packetLossPercent is < 0 or > 50 || lagMilliseconds is < 0 or > 1000) throw new ArgumentOutOfRangeException(nameof(packetLossPercent));
        lock (NativeMethods.Gate) NativeMethods.sc_simulate(packetLossPercent, lagMilliseconds);
    }

    private void Pump()
    {
        var payload = new byte[ProtocolCodec.HeaderBytes + ProtocolCodec.MaxPayloadBytes];
        var reason = new byte[512];
        try
        {
            while (!_shutdown.IsCancellationRequested)
            {
                for (var i = 0; i < 64; i++)
                {
                    var send = _blockedSend;
                    if (send is null && !_sends.TryDequeue(out send)) break;
                    int result;
                    fixed (byte* p = send.Bytes) lock (NativeMethods.Gate) result = NativeMethods.sc_send(_native, send.Connection, p, send.Bytes.Length, send.Reliable ? 1 : 0);
                    if (result == 25 && send.Reliable) { _blockedSend = send; break; } // EResultLimitExceeded: retain reliable order and retry after native congestion drains.
                    _blockedSend = null;
                    Interlocked.Decrement(ref _sendCount);
                    Interlocked.Add(ref _queuedBytes, -send.Bytes.Length);
                    if (result != 1 && send.Reliable) Publish(new(TransportEventKind.Fault, send.Connection, Reason: $"Native send rejected: {result}"));
                }
                for (var i = 0; i < 256; i++)
                {
                    int kind, length; uint connection;
                    fixed (byte* p = payload) fixed (byte* r = reason) lock (NativeMethods.Gate) kind = NativeMethods.sc_poll(_native, out connection, p, payload.Length, out length, r, reason.Length);
                    if (kind == 0) break;
                    switch ((TransportEventKind)kind)
                    {
                        case TransportEventKind.Connected:
                            _connections.TryAdd(connection, 0); Publish(new(TransportEventKind.Connected, connection)); break;
                        case TransportEventKind.Disconnected:
                            _connections.TryRemove(connection, out _); Publish(new(TransportEventKind.Disconnected, connection, Reason: DecodeText(reason))); break;
                        case TransportEventKind.Message:
                            if (length >= 0 && length <= payload.Length && ProtocolCodec.TryDecode(payload.AsSpan(0, length), out var message)) Publish(new(TransportEventKind.Message, connection, message), length);
                            else { Close(connection, "Invalid protocol message"); Publish(new(TransportEventKind.Fault, connection, Reason: "Invalid protocol message")); }
                            break;
                        default: Publish(new(TransportEventKind.Fault, connection, Reason: DecodeText(reason))); break;
                    }
                }
                _wake.WaitOne(5);
            }
        }
        catch (Exception ex) { LastError = ex.Message; Publish(new(TransportEventKind.Fault, 0, Reason: ex.Message)); }
    }

    private void Publish(TransportEvent item, int bytes = 128)
    {
        var count = Interlocked.Increment(ref _eventCount);
        if (Interlocked.Add(ref _eventBytes, bytes) <= MaxQueuedBytes && count <= MaxPendingEvents) { _events.Enqueue(new(item, bytes)); return; }
        Interlocked.Decrement(ref _eventCount); Interlocked.Add(ref _eventBytes, -bytes);
        LastError = "Incoming event queue exceeded its bounded capacity.";
        if (item.ConnectionId != 0) Close(item.ConnectionId, "Receive queue overflow");
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _shutdown.Cancel(); _wake.Set(); _pump.Join();
        lock (NativeMethods.Gate) { NativeMethods.sc_host_destroy(_native); _native = 0; }
        _connections.Clear(); _wake.Dispose(); _shutdown.Dispose();
    }

    private static string DecodeText(byte[] bytes) { var end = Array.IndexOf(bytes, (byte)0); return Encoding.UTF8.GetString(bytes, 0, end < 0 ? bytes.Length : end); }
    private sealed record Outgoing(uint Connection, byte[] Bytes, bool Reliable);
    private sealed record QueuedEvent(TransportEvent Event, int Bytes);
}
