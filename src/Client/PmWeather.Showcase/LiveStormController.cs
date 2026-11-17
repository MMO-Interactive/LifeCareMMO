using System.Globalization;
using System.Collections.Concurrent;
using Engine.Environment;
using Shared.Contracts.World;

/// <summary>The showcase scheduler and playback controls; the engine owns storm simulation.</summary>
internal sealed class LiveStormController
{
    public static readonly StormPreset[] Presets =
    [
        new("Weak", 80, 45, 65), new("Moderate", 100, 75, 80),
        new("Strong", 125, 120, 90), new("Severe", 155, 230, 90),
        new("Extreme", 190, 420, 105), new("Violent", 230, 700, 120),
    ];
    private static readonly double[] Speeds = [1, 5, 10, 20, 40, 80];
    private static readonly ConcurrentDictionary<(int Preset, int Seed, bool Varied), double> PeakTimes = new();
    private int seed;
    private double clearSeconds;

    public LiveStormController(string[] args)
    {
        PresetIndex = int.Parse(Read(args, "--storm-intensity", "4"), CultureInfo.InvariantCulture) - 1;
        if (PresetIndex is < 0 or > 5) throw new ArgumentException("--storm-intensity must be 1..6.");
        seed = int.Parse(Read(args, "--storm-seed", "13"), CultureInfo.InvariantCulture);
        double speed = double.Parse(Read(args, "--storm-speed", "20"), CultureInfo.InvariantCulture);
        if (!double.IsFinite(speed) || speed is < 0.1 or > 200) throw new ArgumentException("--storm-speed must be 0.1..200.");
        PlaybackSpeed = speed;
        ChaseMotionEnabled = args.Contains("--chase-motion") ||
            (args.Contains("--demo") && !args.Contains("--legacy-demo"));
        VariedLifecycleEnabled = ChaseMotionEnabled && !args.Contains("--controlled-lifecycle");
        Simulation = new(OptionsForRun());
    }

    public int PresetIndex { get; private set; }
    public StormPreset Preset => Presets[PresetIndex];
    public PmWeatherSupercellSimulation Simulation { get; private set; }
    public bool Paused { get; private set; }
    public double PlaybackSpeed { get; private set; }
    public float VisualSeconds { get; private set; }
    public long HistoryVersion { get; private set; } = 10;
    public long WorldResetVersion { get; private set; } = 10;
    public bool ChaseMotionEnabled { get; }
    public bool VariedLifecycleEnabled { get; }
    public bool CampaignManaged { get; private set; }
    public bool CampaignStormActive { get; private set; }
    public float TrackSpeedMph
    {
        get
        {
            var velocity = CampaignManaged && !CampaignStormActive ? Vector3f.Zero
                : Simulation.Current.ActiveStorms.FirstOrDefault()?.Velocity ?? Vector3f.Zero;
            return MathF.Sqrt(velocity.X * velocity.X + velocity.Z * velocity.Z) * (float)PlaybackSpeed / .44704f;
        }
    }
    public string Phase => CampaignManaged && !CampaignStormActive ? "Clear" : GetPhase(Simulation);
    public string Status
    {
        get
        {
            var storm = Simulation.Current.ActiveStorms.FirstOrDefault();
            string episode = VariedLifecycleEnabled && Simulation.TornadoEpisodeNumber > 0
                ? $" | Funnel #{Simulation.TornadoEpisodeNumber} / {Simulation.TouchdownCount} touchdowns" : "";
            return $"{PresetIndex + 1}:{Preset.Name} | {Phase}{episode} | {storm?.WindSpeed ?? 0}/{Preset.PeakMph} mph | {storm?.SmoothedWidthMeters ?? storm?.WidthMeters ?? 0:0} m | {(Paused ? "PAUSED" : PlaybackSpeed.ToString("0.#", CultureInfo.InvariantCulture) + "x")}";
        }
    }

    public static PmWeatherSupercellOptions CreateOptions(int presetIndex, int seed)
    {
        var preset = Presets[presetIndex];
        return new() { Seed = seed, PeakWindSpeedMph = preset.PeakMph,
            MaximumWidthMeters = preset.WidthMeters, TouchdownWindSpeedMph = preset.TouchdownMph,
            Velocity = new(0.5f, 0, 0.12f) };
    }

    public static PmWeatherSupercellOptions CreateChaseOptions(int presetIndex, int seed, bool variedLifecycle = false)
    {
        var source = CreateOptions(presetIndex, seed) with
            { VariedLifecycle = variedLifecycle, Id = variedLifecycle ? $"chase-storm-{seed}" : "pmweather-supercell" };
        double peakSeconds = PeakTimes.GetOrAdd((presetIndex, seed, variedLifecycle), key =>
        {
            var clock = new PmWeatherSupercellSimulation(CreateOptions(key.Preset, key.Seed) with
                { Velocity = Vector3f.Zero, VariedLifecycle = key.Varied });
            if (key.Varied) return clock.FirstActivityTimeSeconds;
            for (int step = 0; step < 100_000; step++)
            {
                if (clock.Current.ActiveStorms.FirstOrDefault() is { Stage: 3, IsDying: true })
                    return clock.SimulationSeconds;
                clock.Advance(.05);
            }
            throw new InvalidOperationException("Storm chase intercept timing could not reach the native peak.");
        });
        var random = new Random(unchecked(seed * 7919 + 104729));
        // The lifecycle is accelerated for the demo. At the default 20x these
        // tracks translate 30–42 m/s, crossing several kilometers per chase.
        float heading = (-8 + (float)random.NextDouble() * 28) * MathF.PI / 180;
        float speed = 1.5f + (float)random.NextDouble() * .6f;
        var velocity = new Vector3f(MathF.Cos(heading) * speed, 0, MathF.Sin(heading) * speed);
        var intercept = seed == 13 ? new Vector3f(95, 0, 1040) : seed == 14 ? new Vector3f(-195, 0, 1040) :
            new Vector3f(-80 + (float)random.NextDouble() * 620, 0, 850 + (float)random.NextDouble() * 420);
        var funnelOffset = Vector3f.Zero;
        if (variedLifecycle)
        {
            // Route the actual first vortex over the intercept. Later episodes
            // retain their own offsets as the same parent moves downstream.
            var anchor = new PmWeatherSupercellSimulation(source with { Origin = Vector3f.Zero, Velocity = Vector3f.Zero });
            anchor.Advance(peakSeconds);
            funnelOffset = anchor.Current.ActiveStorms.FirstOrDefault()?.Tornadoes.FirstOrDefault()?.LocalOffset ?? Vector3f.Zero;
        }
        return source with { Origin = intercept - funnelOffset - velocity * (float)peakSeconds, Velocity = velocity };
    }

    private PmWeatherSupercellOptions OptionsForRun() => ChaseMotionEnabled
        ? CreateChaseOptions(PresetIndex, seed, VariedLifecycleEnabled) : CreateOptions(PresetIndex, seed);

    /// <summary>The day director owns opportunities; the old three-second restart is disabled.</summary>
    public void EnterCampaign()
    {
        CampaignManaged = true;
        CampaignStormActive = false;
        clearSeconds = 0;
    }

    public void SetCampaignPrimary(PmWeatherSupercellSimulation simulation, int presetIndex, int stormSeed, double speed)
    {
        ArgumentNullException.ThrowIfNull(simulation);
        if (presetIndex is < 0 or > 5) throw new ArgumentOutOfRangeException(nameof(presetIndex));
        if (!double.IsFinite(speed) || speed is < .1 or > 200) throw new ArgumentOutOfRangeException(nameof(speed));
        CampaignManaged = true;
        CampaignStormActive = !simulation.IsComplete;
        if (!ReferenceEquals(Simulation, simulation)) HistoryVersion++;
        Simulation = simulation;
        PresetIndex = presetIndex;
        seed = stormSeed;
        PlaybackSpeed = speed;
        clearSeconds = 0;
    }

    public void SetCampaignIdle()
    {
        CampaignManaged = true;
        if (CampaignStormActive) HistoryVersion++;
        CampaignStormActive = false;
    }

    public void AdvanceCampaignVisualClock(double elapsedWallSeconds)
    {
        if (!Paused && elapsedWallSeconds > 0) VisualSeconds += (float)elapsedWallSeconds;
    }

    public void StartCampaignStorm(int presetIndex, int stormSeed, PmWeatherSupercellOptions? source = null, double speed = 20)
        => SetCampaignPrimary(new(source ?? CreateChaseOptions(presetIndex, stormSeed, variedLifecycle: true)),
            presetIndex, stormSeed, speed);

    public void AdvanceCampaign(double elapsedWallSeconds, double? speed = null)
    {
        if (Paused || elapsedWallSeconds <= 0) return;
        AdvanceCampaignVisualClock(elapsedWallSeconds);
        if (speed is { } value) PlaybackSpeed = value;
        if (CampaignStormActive) Simulation.Advance(elapsedWallSeconds * PlaybackSpeed);
        if (Simulation.IsComplete) SetCampaignIdle();
    }

    public void FinishCampaignStorm()
    {
        if (Simulation.IsComplete) SetCampaignIdle();
        else Simulation.RequestDissipation();
    }

    public void Update(double elapsedWallSeconds)
    {
        if (CampaignManaged) { AdvanceCampaign(elapsedWallSeconds); return; }
        if (Paused || elapsedWallSeconds <= 0) return;
        VisualSeconds += (float)elapsedWallSeconds;
        if (Simulation.IsComplete)
        {
            clearSeconds += elapsedWallSeconds;
            if (clearSeconds >= 3) Restart(newSeed: true);
        }
        else Simulation.Advance(elapsedWallSeconds * PlaybackSpeed);
    }

    public void HandleKey(int key)
    {
        if (key is >= 0x31 and <= 0x36) { PresetIndex = key - 0x31; Restart(); }
        else if (key == 0x52) Restart(); // R
        else if (key == 0x50) Paused = !Paused; // P
        else if (key == 0x47) AdvanceToNextPhase(); // G
        else if (key is 0xDB or 0xDD)
        {
            PlaybackSpeed = key == 0xDD
                ? Speeds.FirstOrDefault(candidate => candidate > PlaybackSpeed, PlaybackSpeed)
                : Speeds.LastOrDefault(candidate => candidate < PlaybackSpeed, PlaybackSpeed);
        }
        Console.WriteLine($"Storm controls: {Status}; simulation={Simulation.SimulationSeconds:0.00}s; seed={seed}.");
    }

    public void Restart(bool newSeed = false)
    {
        if (newSeed || VariedLifecycleEnabled) seed = unchecked(seed + 1);
        if (!newSeed) WorldResetVersion++;
        Simulation = new(OptionsForRun());
        clearSeconds = 0;
        HistoryVersion++;
    }

    public void AdvanceToNextPhase()
    {
        if (Simulation.IsComplete) { Restart(newSeed: true); return; }
        string phase = Phase;
        for (int tick = 0; tick < 100_000 && !Simulation.IsComplete && Phase == phase; tick++)
            Simulation.Advance(0.05);
        HistoryVersion++;
    }

    public static string GetPhase(PmWeatherSupercellSimulation simulation)
    {
        if (simulation.IsComplete) return "Clear";
        if (simulation.IsVariedLifecycle) return simulation.CurrentPhase;
        var storm = simulation.Current.ActiveStorms.FirstOrDefault();
        if (storm is null) return "Cumulus";
        if (storm.IsDying && storm.Stage < 3) return "Dissipating";
        if (storm.Stage == 0) return "Cumulus";
        if (storm.Stage == 1) return "Growing";
        if (storm.Stage == 2) return "Wall cloud";
        if (!storm.IsDying) return "Forming";
        if (storm.TicksSinceDying <= 1200) return "Mature";
        return storm.WindSpeed < 40 ? "Rope" : "Weakening";
    }

    private static string Read(string[] args, string option, string fallback)
    {
        int index = Array.IndexOf(args, option);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : fallback;
    }

    internal sealed record StormPreset(string Name, int PeakMph, float WidthMeters, int TouchdownMph);
}
