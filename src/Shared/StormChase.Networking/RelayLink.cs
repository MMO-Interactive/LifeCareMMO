using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using StormChase.Protocol;

namespace StormChase.Networking;

/// <summary>
/// Internal Gateway ↔ district host link: one TCP stream per Gateway carries every player it fronts.
/// Frame = int32 length of the rest, byte type, uint32 connection id, payload. Player payloads are protocol frames, forwarded unchanged.
/// </summary>
public static class RelayFrames
{
    public const byte Hello = 0, Connected = 1, Disconnected = 2, Message = 3, Close = 4;
    public const int MaxFrameBytes = 5 + ProtocolCodec.HeaderBytes + ProtocolCodec.MaxPayloadBytes;

    public static byte[] Build(byte type, uint connection, ReadOnlySpan<byte> payload)
    {
        if (payload.Length > MaxFrameBytes - 5) throw new ArgumentOutOfRangeException(nameof(payload));
        var frame = new byte[4 + 5 + payload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, 5 + payload.Length);
        frame[4] = type;
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(5), connection);
        payload.CopyTo(frame.AsSpan(9));
        return frame;
    }

    /// <summary>Blocking read of one frame; false at a clean end of stream.</summary>
    public static bool TryRead(Stream stream, out byte type, out uint connection, out byte[] payload)
    {
        type = 0; connection = 0; payload = [];
        Span<byte> head = stackalloc byte[9];
        if (!Fill(stream, head)) return false;
        int length = BinaryPrimitives.ReadInt32LittleEndian(head);
        if (length is < 5 or > MaxFrameBytes) throw new InvalidDataException("Relay frame length is out of range.");
        type = head[4]; connection = BinaryPrimitives.ReadUInt32LittleEndian(head[5..]);
        if (type > Close) throw new InvalidDataException("Unknown relay frame type.");
        payload = new byte[length - 5];
        if (!Fill(stream, payload)) throw new EndOfStreamException("Relay frame was truncated.");
        return true;
    }

    private static bool Fill(Stream stream, Span<byte> buffer)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int n = stream.Read(buffer[read..]);
            if (n == 0) return read == 0 ? false : throw new EndOfStreamException("Relay stream ended mid-frame.");
            read += n;
        }
        return true;
    }

    internal static bool KeyMatches(string? expected, byte[] supplied) =>
        expected is null || CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), supplied);
}

/// <summary>One writer thread per TCP link so a slow peer never blocks the game tick. Unreliable frames are dropped first under backpressure.</summary>
internal sealed class RelayWriter : IDisposable
{
    private const int SoftLimit = 2048, HardLimit = 8192;
    private readonly BlockingCollection<byte[]> queue = new(HardLimit);
    private readonly Stream stream;
    private readonly Thread thread;
    private readonly Action<Exception> fault;

    public RelayWriter(Stream stream, string name, Action<Exception> fault)
    {
        this.stream = stream; this.fault = fault;
        thread = new(Run) { IsBackground = true, Name = name };
        thread.Start();
    }

    /// <summary>False when a reliable frame cannot be queued; the link should then be closed.</summary>
    public bool Post(byte[] frame, bool reliable)
    {
        if (queue.IsAddingCompleted) return false;
        if (!reliable && queue.Count >= SoftLimit) return true;
        try { return queue.TryAdd(frame); } catch (InvalidOperationException) { return false; }
    }

    private void Run()
    {
        try
        {
            foreach (var frame in queue.GetConsumingEnumerable())
            {
                stream.Write(frame);
                if (queue.Count == 0) stream.Flush();
            }
        }
        catch (Exception error) { fault(error); }
    }

    public void Dispose() { queue.CompleteAdding(); thread.Join(2000); queue.Dispose(); }
}

/// <summary>District host side: accepts Gateway links and presents their players as an ordinary game transport.</summary>
public sealed class RelayLinkServer : IGameTransport
{
    private readonly TcpListener listener;
    private readonly string? key;
    private readonly ConcurrentQueue<TransportEvent> events = new();
    private readonly ConcurrentDictionary<uint, (Link Link, uint Remote)> players = new();
    private readonly ConcurrentDictionary<Link, byte> links = new();
    private readonly Thread acceptor;
    private long sequence;
    private uint nextId;
    private int disposed;
    public int Port { get; }
    public int LinkCount => links.Count;
    public string? LastError { get; private set; }

    public RelayLinkServer(IPAddress address, int port, string? key)
    {
        if (!IPAddress.IsLoopback(address) && string.IsNullOrEmpty(key)) throw new ArgumentException("A relay link outside loopback requires a link key.");
        this.key = string.IsNullOrEmpty(key) ? null : key;
        listener = new(address, port); listener.Start();
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        acceptor = new(Accept) { IsBackground = true, Name = "Relay link accept " + Port };
        acceptor.Start();
    }

    private void Accept()
    {
        while (Volatile.Read(ref disposed) == 0)
        {
            TcpClient client;
            try { client = listener.AcceptTcpClient(); }
            catch (SocketException) { return; }
            catch (ObjectDisposedException) { return; }
            client.NoDelay = true;
            var link = new Link(this, client);
            new Thread(link.Read) { IsBackground = true, Name = "Relay link read" }.Start();
        }
    }

    public bool Send<T>(uint connectionId, MessageKind kind, T payload, bool reliable = true)
    {
        if (Volatile.Read(ref disposed) != 0 || !players.TryGetValue(connectionId, out var route)) return false;
        var bytes = ProtocolCodec.Encode(kind, (ulong)Interlocked.Increment(ref sequence) & ~ProtocolCodec.ReliableFlag | (reliable ? ProtocolCodec.ReliableFlag : 0), payload);
        return route.Link.Post(RelayFrames.Build(RelayFrames.Message, route.Remote, bytes), reliable);
    }

    public IReadOnlyList<TransportEvent> Poll(int maxEvents = 256)
    {
        var result = new List<TransportEvent>();
        while (result.Count < maxEvents && events.TryDequeue(out var item)) result.Add(item);
        return result;
    }

    public void Close(uint connectionId, string reason = "Disconnected")
    {
        if (!players.TryRemove(connectionId, out var route)) return;
        route.Link.Forget(route.Remote);
        route.Link.Post(RelayFrames.Build(RelayFrames.Close, route.Remote, Encoding.UTF8.GetBytes(reason.Length > 128 ? reason[..128] : reason)), true);
    }

    public string GetConnectionStatus(uint connectionId) => players.TryGetValue(connectionId, out var route) ? "Gateway relay link " + route.Link.Remote : "Unknown";

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        listener.Stop();
        foreach (var link in links.Keys) link.Shutdown();
    }

    private sealed class Link(RelayLinkServer owner, TcpClient client)
    {
        private readonly NetworkStream stream = client.GetStream();
        private readonly ConcurrentDictionary<uint, uint> local = new();
        private RelayWriter? writer;
        public string Remote { get; } = client.Client.RemoteEndPoint?.ToString() ?? "?";

        public void Read()
        {
            try
            {
                if (!RelayFrames.TryRead(stream, out var type, out _, out var hello) || type != RelayFrames.Hello || !RelayFrames.KeyMatches(owner.key, hello))
                    throw new InvalidDataException("Relay link was not authorised.");
                writer = new(stream, "Relay link write", Fail);
                owner.links[this] = 0;
                while (RelayFrames.TryRead(stream, out type, out var remote, out var payload))
                {
                    switch (type)
                    {
                        case RelayFrames.Connected:
                            uint id = Interlocked.Increment(ref owner.nextId);
                            local[remote] = id; owner.players[id] = (this, remote);
                            owner.events.Enqueue(new(TransportEventKind.Connected, id)); break;
                        case RelayFrames.Disconnected:
                            if (local.TryRemove(remote, out var gone) && owner.players.TryRemove(gone, out _))
                                owner.events.Enqueue(new(TransportEventKind.Disconnected, gone, Reason: Encoding.UTF8.GetString(payload)));
                            break;
                        case RelayFrames.Message:
                            if (!local.TryGetValue(remote, out var player)) break;
                            if (ProtocolCodec.TryDecode(payload, out var message)) owner.events.Enqueue(new(TransportEventKind.Message, player, message));
                            else { owner.Close(player, "Invalid protocol message"); owner.events.Enqueue(new(TransportEventKind.Fault, player, Reason: "Invalid protocol message")); }
                            break;
                    }
                }
                Fail(new EndOfStreamException("Gateway closed the relay link."));
            }
            catch (Exception error) { Fail(error); }
        }

        public bool Post(byte[] frame, bool reliable)
        {
            if (writer is { } w && w.Post(frame, reliable)) return true;
            if (reliable) Fail(new IOException("Relay link send queue is full."));
            return false;
        }

        public void Forget(uint remote) => local.TryRemove(remote, out _);

        private void Fail(Exception error)
        {
            if (!owner.links.TryRemove(this, out _) && writer is not null) return;
            owner.LastError = error.Message;
            foreach (var pair in local.ToArray())
                if (owner.players.TryRemove(pair.Value, out _)) owner.events.Enqueue(new(TransportEventKind.Disconnected, pair.Value, Reason: "Gateway link lost"));
            local.Clear();
            Shutdown();
        }

        public void Shutdown() { try { client.Close(); } catch { } writer?.Dispose(); }
    }
}

/// <summary>Gateway side of one link to a district host. Thread-safe; incoming frames are queued and <c>signal</c> is set.</summary>
public sealed class RelayLinkClient : IDisposable
{
    private readonly TcpClient client;
    private readonly NetworkStream stream;
    private readonly RelayWriter writer;
    private readonly ConcurrentQueue<(byte Type, uint Connection, byte[] Payload)> incoming = new();
    private readonly EventWaitHandle signal;
    private int faulted;
    public bool Faulted => Volatile.Read(ref faulted) != 0;
    public string? LastError { get; private set; }

    public RelayLinkClient(string host, int port, string? key, EventWaitHandle signal)
    {
        this.signal = signal;
        client = new() { NoDelay = true };
        client.Connect(host, port);
        stream = client.GetStream();
        writer = new(stream, "Gateway link write " + host + ":" + port, Fail);
        writer.Post(RelayFrames.Build(RelayFrames.Hello, 0, Encoding.UTF8.GetBytes(key ?? "")), true);
        new Thread(Read) { IsBackground = true, Name = "Gateway link read " + host + ":" + port }.Start();
    }

    public bool Open(uint connection) => writer.Post(RelayFrames.Build(RelayFrames.Connected, connection, []), true);
    public bool Forward(uint connection, byte[] frame, bool reliable) => writer.Post(RelayFrames.Build(RelayFrames.Message, connection, frame), reliable);
    public bool Leave(uint connection, string reason) => writer.Post(RelayFrames.Build(RelayFrames.Disconnected, connection, Encoding.UTF8.GetBytes(reason.Length > 128 ? reason[..128] : reason)), true);

    public bool TryTake(out (byte Type, uint Connection, byte[] Payload) frame) => incoming.TryDequeue(out frame);

    private void Read()
    {
        try
        {
            while (RelayFrames.TryRead(stream, out var type, out var connection, out var payload))
            { incoming.Enqueue((type, connection, payload)); signal.Set(); }
            Fail(new EndOfStreamException("District host closed the relay link."));
        }
        catch (Exception error) { Fail(error); }
    }

    private void Fail(Exception error)
    {
        if (Interlocked.Exchange(ref faulted, 1) != 0) return;
        LastError = error.Message; signal.Set();
        try { client.Close(); } catch { }
    }

    public void Dispose() { Fail(new ObjectDisposedException(nameof(RelayLinkClient))); writer.Dispose(); }
}
