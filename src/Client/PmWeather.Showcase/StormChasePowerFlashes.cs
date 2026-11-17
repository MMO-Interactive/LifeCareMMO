using Engine.Rendering.Assets;
using Engine.UniStorm.Content;
using Shared.Contracts.Content;
using Shared.Contracts.Rendering;
using Shared.Contracts.World;

/// <summary>Brief, local electrical failures emitted only by native utility-pole break events.</summary>
internal sealed class StormChasePowerFlashes
{
    private const int MaximumEvents = 32;
    private const int MaximumVisibleEvents = 4;
    private const int SparksPerEvent = 32;
    private static readonly AssetHandle Quad = new("mesh", "engine/particle-quad");
    private static readonly AssetHandle ArcMaterial = new("material", "chase/power-arc");
    private static readonly AssetHandle GlowMaterial = new("material", "chase/power-glow");
    private static readonly AssetHandle SparkMaterial = new("material", "chase/power-spark");
    private readonly List<Flash> active = [];
    private readonly HashSet<string> observedIds = new(StringComparer.Ordinal);
    private readonly Queue<string> observedOrder = new();
    private float previousSeconds = float.NaN;

    public int ActiveEventCount => active.Count;
    public int AcceptedEventCount { get; private set; }
    public int SparkInstanceCount { get; private set; }
    public int ArcInstanceCount { get; private set; }
    public int AddedLightCount { get; private set; }
    public float FlashIntensity { get; private set; }
    public float LastEmissionSeconds { get; private set; } = float.NegativeInfinity;
    public Vector3f? LastPowerFlashPosition { get; private set; }
    public string? LastEventId { get; private set; }

    // StormChaseEffects.Register must run first: these use its imported native
    // UniStorm trail texture and HDR lightning shader, without another texture.
    public StormChasePowerFlashes(MutableRenderAssetCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        catalog.UpsertMaterial(Material(ArcMaterial, new(.63f, 1f, .96f, 1), 12));
        catalog.UpsertMaterial(Material(GlowMaterial, new(.10f, .80f, .65f, .45f), 4));
        catalog.UpsertMaterial(Material(SparkMaterial, new(.48f, 1f, .78f, 1), 8));
    }

    public void Reset()
    {
        active.Clear(); observedIds.Clear(); observedOrder.Clear();
        previousSeconds = float.NaN; AcceptedEventCount = 0;
        SparkInstanceCount = ArcInstanceCount = AddedLightCount = 0;
        FlashIntensity = 0; LastEmissionSeconds = float.NegativeInfinity;
        LastPowerFlashPosition = null; LastEventId = null;
    }

    public RenderFrame AddToFrame(RenderFrame frame, float seconds, IReadOnlyList<StormChaseBreakEvent> events)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(events);
        if (!float.IsFinite(seconds)) throw new ArgumentOutOfRangeException(nameof(seconds));
        if (float.IsFinite(previousSeconds) && seconds < previousSeconds) Reset();
        previousSeconds = seconds;
        active.RemoveAll(f => seconds - f.StartSeconds >= f.Duration);
        foreach (var broken in events)
        {
            if (!broken.IsUtilityPole || string.IsNullOrEmpty(broken.Id) || !Finite(broken.Position)
                || !observedIds.Add(broken.Id)) continue;
            observedOrder.Enqueue(broken.Id);
            // There are at most 256 native destructible parts. Bound duplicate
            // history too, so a malformed external stream cannot grow forever.
            if (observedOrder.Count > 512) observedIds.Remove(observedOrder.Dequeue());
            if (active.Count == MaximumEvents) active.RemoveAt(0);
            uint seed = Hash(broken.Id);
            active.Add(new(broken.Id, broken.Position, seconds, .48f + Unit(seed, 1) * .40f, seed));
            AcceptedEventCount++;
            LastEmissionSeconds = seconds; LastPowerFlashPosition = broken.Position; LastEventId = broken.Id;
        }
        SparkInstanceCount = ArcInstanceCount = AddedLightCount = 0;
        FlashIntensity = 0;
        if (active.Count == 0) return frame;

        // The brightest nearby failures share three native instanced draws.
        // More simultaneous breaks remain tracked, without increasing budgets.
        var visible = active.OrderByDescending(f => Pulse(f, seconds)
            / (1 + DistanceSquared(f.Position, frame.Camera.Position) * .00005f)).Take(MaximumVisibleEvents).ToArray();
        var cores = new List<RenderParticleInstance>(24);
        var glows = new List<RenderParticleInstance>(24);
        var sparks = new List<RenderParticleInstance>(MaximumVisibleEvents * SparksPerEvent);
        var lights = new List<RenderLight>(MaximumVisibleEvents + (frame.Lights?.Count ?? 0));
        foreach (var flash in visible)
        {
            float age = seconds - flash.StartSeconds;
            float pulse = Pulse(flash, seconds);
            FlashIntensity = Math.Max(FlashIntensity, pulse);
            AddArcs(flash, age, pulse, cores, glows);
            AddSparks(flash, age, sparks);
            // Native inverse-square local lighting reaches nearby terrain and
            // real PhysX debris; this never changes the global sky/weather.
            lights.Add(new("chase/power/" + flash.Id, RenderLightType.Point, flash.Position,
                new(0, -1, 0), new(.12f, 1f, .78f, 1), 6000 * pulse, 90, CastsShadow: false));
        }
        AddedLightCount = lights.Count;
        if (frame.Lights is { } existingLights) lights.AddRange(existingLights);
        SparkInstanceCount = sparks.Count; ArcInstanceCount = cores.Count + glows.Count;
        var batches = (frame.Particles?.Batches ?? []).ToList();
        if (cores.Count > 0) batches.Add(Batch("chase/power-arc", ArcMaterial, cores));
        if (glows.Count > 0) batches.Add(Batch("chase/power-glow", GlowMaterial, glows));
        if (sparks.Count > 0) batches.Add(Batch("chase/power-spark", SparkMaterial, sparks));
        return frame with
        {
            Lights = lights,
            Particles = new RenderParticleScene((frame.Particles?.ActiveEffectCount ?? 0) + visible.Length,
                batches.Sum(b => b.Instances.Count), frame.Particles?.Billboards ?? [], batches),
            PostProcessSettings = (frame.PostProcessSettings ?? RenderPostProcessSettings.Default) with
            {
                BloomIntensity = Math.Max(frame.PostProcessSettings?.BloomIntensity ?? 0, .16f + FlashIntensity * .30f)
            }
        };
    }

    private static void AddArcs(Flash flash, float age, float pulse,
        List<RenderParticleInstance> cores, List<RenderParticleInstance> glows)
    {
        if (age >= flash.Duration * .65f) return;
        // Discrete ionized branches redraw at 30Hz while staying anchored to
        // the failed energized pole top, instead of becoming continuous fire.
        int flicker = (int)(age * 30);
        for (int branch = 0; branch < 2; branch++)
        {
            var start = flash.Position + new Vector3f(branch == 0 ? -.35f : .35f, 0, 0);
            float angle = Unit(flash.Seed, branch * 11 + 3) * MathF.Tau;
            var reach = new Vector3f(MathF.Cos(angle) * (2.8f + branch), -1.2f - branch * .7f,
                MathF.Sin(angle) * (2.8f + branch));
            var previous = start;
            for (int point = 1; point <= 3; point++)
            {
                float t = point / 3f;
                float jitter = point == 3 ? 0 : .75f;
                var next = start + reach * t + new Vector3f(
                    Signed(flash.Seed, branch * 17 + point * 3 + flicker * 23) * jitter,
                    Signed(flash.Seed, branch * 19 + point * 7 + flicker * 29) * jitter,
                    Signed(flash.Seed, branch * 23 + point * 11 + flicker * 31) * jitter);
                var delta = next - previous;
                float length = MathF.Sqrt(DistanceSquared(next, previous));
                var core = new RenderParticleInstance((previous + next) * .5f,
                    new(.14f + branch * .025f, length * 1.08f, 1), Vector3f.Zero,
                    new(.70f, 1f, .96f, pulse), delta * (1 / Math.Max(length, .001f)), age / flash.Duration, 1);
                cores.Add(core);
                glows.Add(core with { Scale = new(.70f + branch * .10f, length * 1.18f, 1),
                    Color = new(.16f, .95f, .72f, pulse * .38f) });
                previous = next;
            }
        }
    }

    private static void AddSparks(Flash flash, float age, List<RenderParticleInstance> instances)
    {
        for (int i = 0; i < SparksPerEvent; i++)
        {
            float life = flash.Duration * (.60f + Unit(flash.Seed, i * 11 + 7) * .40f);
            if (age >= life) continue;
            float angle = Unit(flash.Seed, i * 7 + 41) * MathF.Tau;
            float horizontalSpeed = 5 + Unit(flash.Seed, i * 13 + 71) * 13;
            float riseSpeed = -1 + Unit(flash.Seed, i * 17 + 83) * 11;
            var velocity = new Vector3f(MathF.Cos(angle) * horizontalSpeed, riseSpeed - age * 9.81f,
                MathF.Sin(angle) * horizontalSpeed);
            var position = flash.Position + new Vector3f(velocity.X * age,
                riseSpeed * age - 4.905f * age * age, velocity.Z * age);
            float alpha = MathF.Pow(Math.Clamp(1 - age / life, 0, 1), .7f);
            float width = .065f + Unit(flash.Seed, i * 19 + 97) * .10f;
            instances.Add(new(position, new(width, .55f + Unit(flash.Seed, i * 23 + 101) * .70f, 1),
                Vector3f.Zero, new(.55f, 1f, .84f, alpha), velocity, age / life, 1));
        }
    }

    private static float Pulse(Flash flash, float seconds)
    {
        float age = Math.Clamp(seconds - flash.StartSeconds, 0, flash.Duration);
        float flicker = MathF.Sin(age * 47 + Unit(flash.Seed, 53) * MathF.Tau);
        return MathF.Pow(1 - age / flash.Duration, 1.6f) * (.65f + .35f * flicker * flicker);
    }
    private static RenderParticleInstanceBatch Batch(string id, AssetHandle material, IReadOnlyList<RenderParticleInstance> instances) =>
        new(id, Quad, material, RenderParticleFacingMode.Velocity, RenderParticleBlendMode.Additive, false, 0, instances);
    private static RenderMaterialDefinition Material(AssetHandle handle, ColorRgba color, float emission) => new()
    {
        Handle = handle, Texture = UniStormAssetIds.LightningTrailTexture, ShaderProgramId = UniStormAssetIds.LightningShader,
        Tint = color, EmissiveColor = color with { A = 1 }, EmissiveIntensity = emission,
        CullMode = RenderCullMode.None, DepthEnabled = true, OpacityMode = RenderOpacityMode.Translucent, AlphaCutoff = .002f
    };
    private static float DistanceSquared(Vector3f a, Vector3f b) =>
        (a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y) + (a.Z - b.Z) * (a.Z - b.Z);
    private static bool Finite(Vector3f value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    private static float Signed(uint seed, int index) => Unit(seed, index) * 2 - 1;
    private static float Unit(uint seed, int index)
    {
        uint value = seed ^ unchecked((uint)index * 0x9E3779B9u);
        value ^= value >> 16; value *= 0x7FEB352Du; value ^= value >> 15; value *= 0x846CA68Bu; value ^= value >> 16;
        return (value & 0x00FFFFFFu) / 16777216f;
    }
    private static uint Hash(string text)
    {
        uint hash = 2166136261;
        foreach (char c in text) hash = (hash ^ c) * 16777619;
        return hash;
    }
    private readonly record struct Flash(string Id, Vector3f Position, float StartSeconds, float Duration, uint Seed);
}
