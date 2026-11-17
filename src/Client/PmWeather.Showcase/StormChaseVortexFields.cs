using Engine.Environment;
using Shared.Contracts.Rendering;
using Shared.Contracts.World;

/// <summary>Small physical vortices carried by the authoritative weather state.
/// Analytic orbits depend on the world clock, never on client frame cadence.</summary>
internal static class StormChaseVortexFields
{
    private const string IdSuffix = "/physical-sub/";
    public const int MaximumFieldsPerStorm = 3;
    public const float MaximumCombinedWindMph = 330;
    public const float MaximumPeakEnhancement = 1.35f;

    public static EnvironmentSceneState Apply(EnvironmentSceneState scene, int seed, double wallSeconds)
    {
        ArgumentNullException.ThrowIfNull(scene);
        if (!double.IsFinite(wallSeconds) || wallSeconds < 0) throw new ArgumentOutOfRangeException(nameof(wallSeconds));
        bool changed = false;
        var storms = new WeatherStormCellState[scene.ActiveStorms.Count];
        for (int s = 0; s < storms.Length; s++)
        {
            var storm = scene.ActiveStorms[s];
            var tornado = !storm.VisualOnly && (storm.SmoothedStage ?? storm.Stage + storm.Energy / 100f) >= 3
                ? storm.Tornadoes.Where(t => !t.VisualOnly && t.TouchdownAmount > 0 &&
                    (t.SmoothedWindSpeed ?? t.WindSpeed) >= 135 && (t.SmoothedWidthMeters ?? t.WidthMeters) >= 70)
                    .OrderByDescending(t => t.SmoothedWindSpeed ?? t.WindSpeed).FirstOrDefault()
                : null;
            var retained = storm.ActiveVorticies.Where(v => !v.Id.Contains(IdSuffix, StringComparison.Ordinal)).ToList();
            if (tornado is null && retained.Count == storm.ActiveVorticies.Count) { storms[s] = storm; continue; }
            if (tornado is not null)
            {
                uint parentSeed = StableHash(tornado.Id, unchecked((uint)seed));
                int count = 2 + (int)(parentSeed % 2);
                float parentWidth = tornado.SmoothedWidthMeters ?? tornado.WidthMeters;
                float parentRadius = MathF.Max(tornado.BaseRadiusMeters, parentWidth / 2.5f);
                float strength = Smooth(125, 185, tornado.SmoothedWindSpeed ?? tornado.WindSpeed) * Smooth(60, 150, parentWidth);
                for (int i = 0; i < count; i++)
                {
                    uint fieldSeed = StableHash(i.ToString(System.Globalization.CultureInfo.InvariantCulture), parentSeed);
                    float orbitalRadius = parentRadius * (.79f + Unit(fieldSeed) * .14f);
                    float width = MathF.Max(5, parentWidth * (.09f + Unit(fieldSeed ^ 0x54b413a5) * .045f));
                    double angularSpeed = .28 + Unit(fieldSeed ^ 0x6a09e667) * .26;
                    double phase = Unit(parentSeed) * Math.Tau + i * Math.Tau / count + (Unit(fieldSeed ^ 0xbb67ae85) - .5) * .20;
                    float angle = (float)((phase + wallSeconds * angularSpeed) % Math.Tau);
                    int lifetime = 720 + (int)(fieldSeed % 720);
                    int age = (int)((wallSeconds * 60 + fieldSeed % (uint)lifetime) % lifetime);
                    float life = MathF.Sin(MathF.PI * age / lifetime);
                    float maximum = (.26f + Unit(fieldSeed ^ 0x3c6ef372) * .13f) * strength;
                    retained.Add(new WeatherSubVortexState(tornado.Id + IdSuffix + i,
                        tornado.Position + new Vector3f(MathF.Cos(angle) * orbitalRadius, 0, MathF.Sin(angle) * orbitalRadius),
                        angle, width, orbitalRadius, width / parentWidth, orbitalRadius / parentWidth,
                        maximum * life * life, maximum, lifetime, age));
                }
            }
            storms[s] = storm with { Vorticies = retained.ToArray() }; changed = true;
        }
        return changed ? scene with { Storms = storms } : scene;
    }

    internal static bool BelongsTo(RenderSubVortexState field, RenderTornadoState tornado) =>
        field.Id.Length > tornado.Id.Length && field.Id.AsSpan().StartsWith(tornado.Id.AsSpan(), StringComparison.Ordinal) &&
        field.Id.AsSpan(tornado.Id.Length).StartsWith(IdSuffix.AsSpan(), StringComparison.Ordinal);

    private static uint StableHash(string text, uint seed)
    {
        uint value = 2166136261 ^ seed;
        foreach (char character in text) value = unchecked((value ^ character) * 16777619);
        return value;
    }
    private static float Unit(uint value) => (value & 0x00ffffff) / 16777216f;
    private static float Smooth(float low, float high, float value)
    {
        float t = Math.Clamp((value - low) / (high - low), 0, 1);
        return t * t * (3 - 2 * t);
    }
}
