using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Engine.Environment;
using StormChase.Protocol;

namespace StormChase.Networking;

/// <summary>The weather a district host needs for one tick. The 97-level atmosphere rides along only when its day changes.</summary>
public sealed record ClimateFrame(int Seed, double ElapsedSeconds, float Hour, int DayNumber, string ForecastName, float StartHour,
    EnvironmentSceneState Environment, AtmosphereSnapshot? Atmosphere = null);

/// <summary>Campaign weather as a game authority reads it: in-process, or a local copy of what the climate process pushed.</summary>
public interface IClimateFeed : IDisposable
{
    int Seed { get; }
    double ElapsedSeconds { get; }
    float Hour { get; }
    int DayNumber { get; }
    string ForecastName { get; }
    float StartHour { get; }
    EnvironmentSceneState Current { get; }
    AtmosphereSnapshot Atmosphere { get; }
    /// <summary>In-process: advances the model. Remote: takes the newest pushed frame; never waits.</summary>
    void Update(double elapsedWallSeconds);
}

internal static class ClimateWire
{
    public const int MaxFrameBytes = 8 * 1024 * 1024;
    public static byte[] Encode(ClimateFrame frame)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(frame, ProtocolCodec.JsonOptions);
        var data = new byte[4 + json.Length];
        BinaryPrimitives.WriteInt32LittleEndian(data, json.Length); json.CopyTo(data, 4);
        return data;
    }
    public static ClimateFrame? Read(Stream stream)
    {
        var head = new byte[4];
        if (!Fill(stream, head)) return null;
        int length = BinaryPrimitives.ReadInt32LittleEndian(head);
        if (length is <= 0 or > MaxFrameBytes) throw new InvalidDataException("Climate frame length is out of range.");
        var body = new byte[length];
        if (!Fill(stream, body)) throw new EndOfStreamException("Climate frame was truncated.");
        return JsonSerializer.Deserialize<ClimateFrame>(body, ProtocolCodec.JsonOptions) ?? throw new InvalidDataException("Empty climate frame.");
    }
    public static bool Fill(Stream stream, byte[] buffer)
    {
        int read = 0;
        while (read < buffer.Length) { int n = stream.Read(buffer, read, buffer.Length - read); if (n == 0) return read == 0 ? false : throw new EndOfStreamException(); read += n; }
        return true;
    }
}

/// <summary>Climate process side: pushes every frame to each subscribed district host. A slow subscriber skips to the newest frame.</summary>
public sealed class ClimateLinkServer : IDisposable
{
    private readonly TcpListener listener;
    private readonly string? key;
    private readonly List<Subscriber> subscribers = [];
    private readonly object gate = new();
    private int disposed;
    public int Port { get; }
    public int SubscriberCount { get { lock (gate) return subscribers.Count; } }

    public ClimateLinkServer(IPAddress address, int port, string? key)
    {
        if (!IPAddress.IsLoopback(address) && string.IsNullOrEmpty(key)) throw new ArgumentException("A climate link outside loopback requires a link key.");
        this.key = string.IsNullOrEmpty(key) ? null : key;
        listener = new(address, port); listener.Start(); Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        new Thread(Accept) { IsBackground = true, Name = "Climate link accept" }.Start();
    }

    public void Publish(ClimateFrame frame)
    {
        byte[]? plain = null, withAtmosphere = null;
        lock (gate)
            foreach (var s in subscribers)
            {
                bool needsDay = frame.Atmosphere is not null && s.AtmosphereDay != frame.Atmosphere.DayNumber;
                s.Offer(needsDay ? withAtmosphere ??= ClimateWire.Encode(frame) : plain ??= ClimateWire.Encode(frame with { Atmosphere = null }),
                    needsDay ? frame.Atmosphere!.DayNumber : null);
            }
    }

    private void Accept()
    {
        while (Volatile.Read(ref disposed) == 0)
        {
            TcpClient client;
            try { client = listener.AcceptTcpClient(); } catch (SocketException) { return; } catch (ObjectDisposedException) { return; }
            client.NoDelay = true;
            new Thread(() => Start(client)) { IsBackground = true, Name = "Climate subscriber" }.Start();
        }
    }

    private void Start(TcpClient client)
    {
        try
        {
            var stream = client.GetStream(); var head = new byte[4];
            if (!ClimateWire.Fill(stream, head)) { client.Close(); return; }
            int length = BinaryPrimitives.ReadInt32LittleEndian(head);
            if (length is < 0 or > 256) { client.Close(); return; }
            var supplied = new byte[length];
            if (!ClimateWire.Fill(stream, supplied) || !RelayFrames.KeyMatches(key, supplied)) { client.Close(); return; }
            var subscriber = new Subscriber(client, this);
            lock (gate) subscribers.Add(subscriber);
            subscriber.Run();
        }
        catch { client.Close(); }
    }

    private void Remove(Subscriber s) { lock (gate) subscribers.Remove(s); }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        listener.Stop();
        lock (gate) foreach (var s in subscribers.ToArray()) s.Stop();
    }

    private sealed class Subscriber(TcpClient client, ClimateLinkServer owner)
    {
        private readonly AutoResetEvent ready = new(false);
        private byte[]? latest;
        private int? latestDay;
        private volatile bool stopped;
        public int AtmosphereDay = -1;

        public void Offer(byte[] frame, int? atmosphereDay)
        {
            lock (this)
            {
                // Never let a plain frame overwrite one carrying a new day's atmosphere before it is sent.
                if (latestDay is not null && atmosphereDay is null) return;
                latest = frame; latestDay = atmosphereDay;
                if (atmosphereDay is not null) AtmosphereDay = atmosphereDay.Value;
            }
            ready.Set();
        }

        public void Run()
        {
            var stream = client.GetStream();
            try
            {
                while (!stopped)
                {
                    ready.WaitOne(1000);
                    byte[]? frame;
                    lock (this) { frame = latest; latest = null; latestDay = null; }
                    if (frame is not null) stream.Write(frame);
                }
            }
            catch { }
            finally { Stop(); }
        }

        public void Stop() { stopped = true; owner.Remove(this); ready.Set(); try { client.Close(); } catch { } }
    }
}

/// <summary>District host side: holds the newest pushed frame. The game tick reads it without waiting on the network.</summary>
public sealed class ClimateLinkClient : IClimateFeed
{
    private readonly TcpClient client;
    private ClimateFrame? received;
    private ClimateFrame current;
    private AtmosphereSnapshot atmosphere;
    private volatile string? failure;
    public string? Error => failure;
    public long FramesReceived => Interlocked.Read(ref frames);
    private long frames;

    /// <summary>Connects and waits for the first frame, so the host never starts without weather.</summary>
    public ClimateLinkClient(string host, int port, string? key, int expectedSeed, TimeSpan firstFrameTimeout)
    {
        client = new() { NoDelay = true }; client.Connect(host, port);
        var stream = client.GetStream(); var keyBytes = Encoding.UTF8.GetBytes(key ?? "");
        var hello = new byte[4 + keyBytes.Length]; BinaryPrimitives.WriteInt32LittleEndian(hello, keyBytes.Length); keyBytes.CopyTo(hello, 4);
        stream.Write(hello);
        var first = new TaskCompletionSource<ClimateFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        new Thread(() =>
        {
            try
            {
                while (ClimateWire.Read(stream) is { } frame)
                {
                    if (frame.Atmosphere is { } day) Volatile.Write(ref atmosphere!, day);
                    Volatile.Write(ref received, frame); Interlocked.Increment(ref frames); first.TrySetResult(frame);
                }
                failure = "Climate process closed the link.";
            }
            catch (Exception error) { failure = error.Message; first.TrySetException(error); }
        }) { IsBackground = true, Name = "Climate link read" }.Start();
        if (!first.Task.Wait(firstFrameTimeout)) { client.Close(); throw new TimeoutException("The climate process sent no weather."); }
        current = first.Task.Result;
        if (current.Seed != expectedSeed) { client.Close(); throw new InvalidOperationException($"Climate seed {current.Seed} differs from this world's seed {expectedSeed}."); }
        atmosphere = current.Atmosphere ?? throw new InvalidDataException("The first climate frame has no atmosphere.");
    }

    public int Seed => current.Seed;
    public double ElapsedSeconds => current.ElapsedSeconds;
    public float Hour => current.Hour;
    public int DayNumber => current.DayNumber;
    public string ForecastName => current.ForecastName;
    public float StartHour => current.StartHour;
    public EnvironmentSceneState Current => current.Environment;
    public AtmosphereSnapshot Atmosphere => Volatile.Read(ref atmosphere);
    public void Update(double elapsedWallSeconds) { if (Volatile.Read(ref received) is { } frame) current = frame; }
    public void Dispose() { try { client.Close(); } catch { } }
}
