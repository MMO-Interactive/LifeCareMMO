using System.Collections.Concurrent;
using System.Runtime.Versioning;
using System.Threading.Channels;
using Engine.Audio.Assets;
using Engine.Audio.Backend;
using Engine.Audio.Configuration;
using Engine.Audio.Mixing;
using Engine.Audio.Runtime;
using Engine.Audio.Spatial;
using Engine.Core.Diagnostics;
using Engine.Core.Runtime;
using Engine.UniStorm;
using Engine.UniStorm.Content;
using Shared.Contracts.Rendering;
using Shared.Contracts.World;

/// <summary>Real Engine.Audio output; disk decoding and mixing never block the window thread.</summary>
[SupportedOSPlatform("windows")]
internal sealed class StormChaseAudio : IAsyncDisposable
{
    private readonly AudioEngine engine;
    private readonly IRuntimeLogger logger;
    private readonly Channel<AudioUpdate> updates = Channel.CreateBounded<AudioUpdate>(new BoundedChannelOptions(1)
    { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.DropOldest });
    private readonly ConcurrentQueue<ScheduledThunder> strikes = new();
    private readonly CancellationTokenSource shutdown = new();
    private readonly List<ScheduledThunder> pendingThunder = [];
    private readonly Task worker;
    private readonly AudioPlaybackHandle rain;
    private readonly AudioPlaybackHandle wind;
    private readonly AudioPlaybackHandle hail;
    private readonly StormChaseWarningAudio warnings;
    private Vector3f[] warningSites = [];
    private long previousStrikeSequence = long.MinValue;
    private int muted;
    private int disposed;
    private float rainGain;
    private float windGain;
    private float hailGain;
    private int thunderPlayed;

    private StormChaseAudio(AudioEngine engine, IRuntimeLogger logger, bool isMuted)
    {
        this.engine = engine;
        this.logger = logger;
        muted = isMuted ? 1 : 0;
        rain = Ambient("Sounds/Weather Effects/Heavy Rain 1.wav", .58f);
        wind = Ambient("Sounds/Wind/Stormy Wind 1.wav", .48f);
        hail = Ambient("Sounds/Weather Effects/Hail 1.mp3", .32f);
        warnings = new(engine);
        worker = Task.Run(WorkerAsync);
    }

    public string BackendName => engine.BackendInfo.BackendName;
    public bool Muted => Volatile.Read(ref muted) != 0;
    public int ThunderPlayedCount => Volatile.Read(ref thunderPlayed);
    public int RadioWarningsPlayed => warnings.RadioWarningsPlayed;
    public int SirenActivations => warnings.SirenActivations;
    public void ConfigureWarningSites(IEnumerable<Vector3f> positions) => warningSites = positions.ToArray();

    public static async Task<StormChaseAudio> CreateAsync(IRuntimeLogger logger, bool muted = false, string? contentRoot = null)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Storm chasing audio requires the native Windows output backend.");
        var clips = new InMemoryAudioClipCatalog();
        int count = UniStormAudioCatalog.Register(clips, contentRoot);
        count += StormChaseWarningAudio.Register(clips);
        if (count == 0) throw new InvalidOperationException("Imported UniStorm weather recordings were not found.");
        var backend = new FallbackAudioBackend(new NAudioMixerBackend(clips, null, logger), new NullAudioBackend(logger), logger);
        var engine = new AudioEngine(backend, clips, new AudioOptions
        {
            MasterVolume = muted ? 0 : .7f, MaxActiveVoices = 24, SpeedOfSoundMetersPerSecond = 343,
        }, logger);
        // Device initialization can enter the Windows audio driver; startup is awaited off-thread.
        try { await Task.Run(() => engine.InitializeAsync(CancellationToken.None)).ConfigureAwait(false); }
        catch { await engine.DisposeAsync().ConfigureAwait(false); throw; }
        logger.Info("StormChase", $"Weather audio: {engine.BackendInfo.BackendName}; {count} imported clips; mute={muted}.");
        return new(engine, logger, muted);
    }

    public void SetMuted(bool value)
    {
        Volatile.Write(ref muted, value ? 1 : 0);
        engine.SetBusGain(AudioBus.Master, value ? 0 : .7f);
    }

    public bool ToggleMuted()
    {
        bool value = !Muted;
        SetMuted(value);
        return value;
    }

    /// <summary>Call from the window thread after Effects.AddToFrame, passing Effects.LastStrike.</summary>
    public void Update(RenderFrame frame, float seconds, UniStormSnapshot? snapshot = null, UniStormLightningStrike? strike = null)
    {
        if (Volatile.Read(ref disposed) != 0 || !float.IsFinite(seconds)) return;
        var lightning = strike ?? snapshot?.LightningStrike;
        if (lightning is not null && lightning.Sequence != previousStrikeSequence)
        {
            previousStrikeSequence = lightning.Sequence;
            strikes.Enqueue(new(seconds + Math.Clamp(lightning.ThunderDelaySeconds, 0, 60), lightning));
        }
        updates.Writer.TryWrite(new(frame, seconds));
    }

    // Convenient for callers already using an asynchronous frame update. This never waits on output.
    public Task UpdateAsync(RenderFrame frame, float seconds, UniStormSnapshot? snapshot = null, UniStormLightningStrike? strike = null)
    { Update(frame, seconds, snapshot, strike); return Task.CompletedTask; }

    private AudioPlaybackHandle Ambient(string relativePath, float volume) => engine.PlayAmbient(
        AudioClipReference.FromAssetId(UniStormAssetIds.Audio(relativePath)), new()
        {
            Loop = true, Volume = volume, Priority = .8f, FadeInDuration = TimeSpan.FromSeconds(.8), Emitter = LoopEmitter(0),
        });

    private async Task WorkerAsync()
    {
        float previousSeconds = float.NaN;
        try
        {
            await foreach (var update in updates.Reader.ReadAllAsync(shutdown.Token).ConfigureAwait(false))
            {
                var frame = update.Frame;
                float seconds = update.Seconds;
                bool rewound = float.IsFinite(previousSeconds) && seconds < previousSeconds;
                float dt = float.IsFinite(previousSeconds) ? Math.Clamp(seconds - previousSeconds, 0, .25f) : 1f / 60f;
                previousSeconds = seconds;
                if (rewound)
                {
                    pendingThunder.Clear();
                    while (strikes.TryDequeue(out _)) { }
                }
                while (strikes.TryDequeue(out var strike))
                {
                    if (pendingThunder.Count < 24) pendingThunder.Add(strike);
                }
                engine.SetListener(new AudioListenerState
                {
                    Position = frame.Camera.Position, Forward = frame.Camera.Forward, Up = new(0, 1, 0),
                });
                engine.SetBusGain(AudioBus.Master, Muted ? 0 : .7f);
                float blend = 1 - MathF.Exp(-Math.Max(dt, 1f / 120f) * 4f);
                var weather = frame.Weather;
                var warned = weather?.ActiveStorms.Where(s => s.Tornadoes.Any(t => t.CondensationAmount >= .1f && (t.SmoothedWindSpeed ?? t.WindSpeed) >= 15))
                    .OrderBy(s => StormChaseCareer.HorizontalDistance(s.Position, frame.Camera.Position)).ToArray() ?? [];
                var sites = warningSites.Where(p => warned.Any(s => StormChaseCareer.HorizontalDistance(p, s.Position) < 8000))
                    .OrderBy(p => StormChaseCareer.HorizontalDistance(p, frame.Camera.Position)).ToArray();
                warnings.Update(warned.Length > 0, sites.FirstOrDefault(), seconds,
                    warned.FirstOrDefault()?.Tornadoes.FirstOrDefault()?.Id, sites.Length > 0);
                float rainTarget = Math.Clamp(weather?.RainParticleAlpha ?? 0, 0, 1);
                float windTarget = Math.Clamp(weather?.WindAudioAmount ?? 0, 0, 1);
                float hailTarget = Math.Clamp(StormChaseStormFields.Sample(frame.Camera.Position, weather?.ActiveStorms ?? []).Hail01 * .95f, 0, 1);
                rainGain += (rainTarget - rainGain) * blend;
                windGain += (windTarget - windGain) * blend;
                hailGain += (hailTarget - hailGain) * blend;
                engine.SetEmitter(rain, LoopEmitter(rainGain));
                engine.SetEmitter(wind, LoopEmitter(windGain));
                engine.SetEmitter(hail, LoopEmitter(hailGain));
                foreach (var thunder in pendingThunder.Where(t => seconds >= t.DueSeconds).ToArray())
                {
                    var eventStrike = thunder.Strike;
                    engine.PlaySoundEffect(AudioClipReference.FromAssetId(eventStrike.ThunderClipId), new()
                    {
                        Volume = .9f * Math.Clamp(eventStrike.Intensity, .3f, 1f), Priority = 1f,
                        Pitch = .93f + (eventStrike.Sequence % 5) * .025f,
                        Emitter = new AudioEmitterState
                        {
                            Position = eventStrike.End, MinDistance = 350, MaxDistance = 10000,
                            RolloffFactor = .35f, DistanceModel = AudioDistanceModel.InverseDistanceClamped, DopplerFactor = 0,
                        },
                    });
                    pendingThunder.Remove(thunder);
                    Interlocked.Increment(ref thunderPlayed);
                }
                try
                {
                    await engine.TickAsync(new FrameContext(frame.FrameNumber, TimeSpan.FromSeconds(dt),
                        TimeSpan.FromSeconds(seconds), frame.UtcTimestamp), shutdown.Token).ConfigureAwait(false);
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    logger.Warn("StormChaseAudio", $"Audio frame failed: {error.Message}");
                }
            }
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
        catch (Exception error) { logger.Warn("StormChaseAudio", $"Weather audio worker stopped: {error.Message}"); }
    }

    private static AudioEmitterState LoopEmitter(float gain) => new()
    {
        // Engine.Audio exposes emitter updates rather than mutable per-voice volume.
        // A centered listener-relative linear emitter gives continuous gain 0..1
        // without restarting the imported loops or changing the semantic ambient bus.
        ListenerRelative = true, Position = new(0, 0, 2 - Math.Clamp(gain, 0, 1)),
        MinDistance = 1, MaxDistance = 2, RolloffFactor = 1,
        DistanceModel = AudioDistanceModel.Linear, StereoPanAmount = 0, DopplerFactor = 0,
    };

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        updates.Writer.TryComplete();
        shutdown.Cancel();
        await worker.ConfigureAwait(false);
        await engine.DisposeAsync().ConfigureAwait(false);
        shutdown.Dispose();
    }

    private sealed record AudioUpdate(RenderFrame Frame, float Seconds);
    private sealed record ScheduledThunder(float DueSeconds, UniStormLightningStrike Strike);
}
