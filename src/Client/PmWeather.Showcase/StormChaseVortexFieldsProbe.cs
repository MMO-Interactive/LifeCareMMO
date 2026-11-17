using System.Numerics;
using System.Text.Json;
using Engine.Environment;
using Engine.Rendering.Pipeline;
using Engine.Rendering.Scene;
using Shared.Contracts.Rendering;
using Shared.Contracts.World;

internal static class StormChaseVortexFieldsProbe
{
    public static int Run(string output)
    {
        Directory.CreateDirectory(output);
        var checks = new List<object>();
        void Check(bool success, string name)
        {
            checks.Add(new { Name = name, Passed = success });
            if (!success) throw new InvalidOperationException(name);
        }
        var environment = new ProceduralEnvironmentService().Evaluate(BuiltInEnvironmentProfiles.TemperateOutdoor, 15, BuiltInWeatherProfiles.Clear);
        var tornado = new WeatherTornadoState("fixture-tornado", "fixture-storm", default, new(100, 0, 200), default,
            100, 220, 800, 1, 1, 1, WindSpeed: 220, TouchdownSpeed: 90, WidthMeters: 250,
            Stage: 3, Energy: 70, TouchdownAmount: 1, FunnelLoweringAmount: 1, WindfieldRadiusMeters: 900,
            SmoothedWindSpeed: 220, SmoothedWidthMeters: 250);
        var parent = new WeatherStormCellState("fixture-storm", default, default, 1000, 300, 1800, 800, 3200,
            1, 0, 1, .5f, 1, [tornado], Stage: 3, Energy: 70, SmoothedStage: 3.7f);
        var baseline = environment with { Weather = WeatherState.Clear, WeatherEffects = WeatherEffectsState.None, Storms = [parent] };
        const double seconds = 37.25;
        var first = StormChaseVortexFields.Apply(baseline, 73019, seconds);
        var fields = first.ActiveStorms.Single().ActiveVorticies;
        Check(fields.Count is >= 2 and <= StormChaseVortexFields.MaximumFieldsPerStorm,
            "Strong organized wide tornadoes receive a bounded two or three physical subfields");
        Check(Json(first) == Json(StormChaseVortexFields.Apply(baseline, 73019, seconds)) &&
            Json(first) == Json(StormChaseVortexFields.Apply(first, 73019, seconds)),
            "Identical authoritative seed and clock give identical fields; applying the helper twice cannot duplicate them");
        var later = StormChaseVortexFields.Apply(baseline, 73019, seconds + .4).ActiveStorms.Single().ActiveVorticies;
        Check(fields.Select(v => v.Id).SequenceEqual(later.Select(v => v.Id)) && fields.Zip(later).All(pair =>
                HorizontalDistance(pair.First.Position, pair.Second.Position) > 1 &&
                MathF.Abs(HorizontalDistance(tornado.Position, pair.Second.Position) - pair.Second.DistanceMeters) < .0002f),
            "Stable subfield identities orbit within the parent circulation rather than teleporting or depending on frame cadence");
        Check(Json(first) != Json(StormChaseVortexFields.Apply(baseline, 73020, seconds)), "Different server seeds change the physical orbital ensemble");
        var builder = new DefaultRenderFrameBuilder();
        RenderWeatherState Render(EnvironmentSceneState scene) => builder.Build(1, DateTimeOffset.UnixEpoch, new(1280, 720),
            new RenderScene { Environment = scene }).Weather!;
        var baseWeather = Render(baseline);
        var weather = Render(first);
        var mapped = weather.ActiveStorms.Single().ActiveVorticies;
        Check(Json(fields) == Json(mapped), "The standard engine frame builder preserves every authoritative subfield parameter");
        var restored = JsonSerializer.Deserialize<EnvironmentSceneState>(Json(first), JsonOptions)!;
        Check(Json(Render(restored).ActiveStorms.Single().ActiveVorticies) == Json(mapped),
            "Weather JSON round trips preserve the same fields used by client probes, vehicles and server destruction");

        var axis = tornado.Position;
        var mainRing = axis + new Vector3f(100, 0, 0);
        var ringVelocity = StormChaseDestruction.SampleWindVelocity(mainRing, baseWeather);
        float peakMeters = 220 * .44704f;
        float mainCore = MathF.Exp(-1 / (1.85f * 1.85f)), mainCapture = MathF.Exp(-1 / 9f);
        var expectedMain = new Vector3(-.48f * peakMeters, peakMeters * (.68f * mainCore + .1f * mainCapture), .94f * peakMeters);
        Check(StormChaseDestruction.SampleWindSpeedMph(mainRing, baseWeather) == 220 && Vector3.Distance(ringVelocity, expectedMain) < .00002f,
            "The legacy main-vortex wind and lift response remain unchanged when physical subfields are absent");
        var samples = new List<object>();
        foreach (var field in fields)
        {
            float radius = field.WidthMeters / 2.5f;
            var positive = field.Position + new Vector3f(radius, 0, 0);
            var negative = field.Position + new Vector3f(-radius, 0, 0);
            Vector3 positiveExtra = StormChaseDestruction.SampleWindVelocity(positive, weather) - StormChaseDestruction.SampleWindVelocity(positive, baseWeather);
            Vector3 negativeExtra = StormChaseDestruction.SampleWindVelocity(negative, weather) - StormChaseDestruction.SampleWindVelocity(negative, baseWeather);
            Check(positiveExtra.Z > 0 && negativeExtra.Z < 0,
                $"Opposite sides of {field.Id} experience opposite localized circulation (multiplier {field.WindSpeedMultiplier}; deltas {positiveExtra.Z}, {negativeExtra.Z})");
            samples.Add(new { field.Id, field.Position, field.WidthMeters, PositiveExtraVelocity = positiveExtra, NegativeExtraVelocity = negativeExtra });
        }
        var far = axis + new Vector3f(650, 5, -400);
        Check(StormChaseDestruction.SampleWindVelocity(far, weather) == StormChaseDestruction.SampleWindVelocity(far, baseWeather) &&
            StormChaseDestruction.SampleWindSpeedMph(far, weather) == StormChaseDestruction.SampleWindSpeedMph(far, baseWeather),
            "Localized fields leave the surrounding main windfield unchanged outside their bounded footprint");
        int gridSamples = 0;
        float maximum = 0;
        for (int z = -140; z <= 140; z += 4)
            for (int x = -140; x <= 140; x += 4)
            {
                var point = axis + new Vector3f(x, 1, z);
                float mph = StormChaseDestruction.SampleWindSpeedMph(point, weather);
                var velocity = StormChaseDestruction.SampleWindVelocity(point, weather);
                maximum = MathF.Max(maximum, mph); gridSamples++;
                if (!float.IsFinite(mph) || !float.IsFinite(velocity.X + velocity.Y + velocity.Z) ||
                    mph > MathF.Min(StormChaseVortexFields.MaximumCombinedWindMph, 220 * StormChaseVortexFields.MaximumPeakEnhancement) + .001f)
                    throw new InvalidOperationException("Localized wind exceeded its finite physical budget.");
            }
        Check(gridSamples > 5000 && maximum > 220, "Dense spatial sampling finds finite bounded local enhancements above the main peak");

        RenderWeatherState ChangedParent(RenderStormCellState storm) => weather with { Storms = [storm] };
        var renderParent = weather.ActiveStorms.Single();
        var renderTornado = renderParent.Tornadoes.Single();
        var localPoint = fields[0].Position + new Vector3f(fields[0].WidthMeters / 2.5f, 0, 0);
        Check(StormChaseDestruction.SampleWindVelocity(localPoint, ChangedParent(renderParent with { VisualOnly = true })) == Vector3.Zero &&
            StormChaseDestruction.SampleWindVelocity(localPoint, ChangedParent(renderParent with { Tornadoes = [renderTornado with { VisualOnly = true }] })) == Vector3.Zero,
            "Visual-only storms and tornadoes never produce destructive subfield forces");
        var failed = renderParent with { Tornadoes = [renderTornado with { TouchdownAmount = 0 }] };
        Check(StormChaseDestruction.SampleWindVelocity(localPoint, ChangedParent(failed)) ==
            StormChaseDestruction.SampleWindVelocity(localPoint, ChangedParent(failed with { Vorticies = [] })),
            "A funnel without ground circulation cannot acquire destructive subfields");
        var invisible = renderParent with { Tornadoes = [renderTornado with { CondensationAmount = 0, FunnelLoweringAmount = 0 }] };
        Check(StormChaseDestruction.SampleWindVelocity(localPoint, ChangedParent(invisible)) == StormChaseDestruction.SampleWindVelocity(localPoint, weather),
            "Ground circulation remains physical when condensation does not reach the ground");
        Check(StormChaseDestruction.SampleWindVelocity(localPoint, ChangedParent(renderParent with { Vorticies = mapped.Select(v => v with { IsDead = true }).ToArray() })) ==
                StormChaseDestruction.SampleWindVelocity(localPoint, baseWeather) &&
            StormChaseDestruction.SampleWindVelocity(localPoint, ChangedParent(renderParent with { Vorticies = mapped.Select(v => v with { Id = "unrelated-tornado/physical-sub/0" }).ToArray() })) ==
                StormChaseDestruction.SampleWindVelocity(localPoint, baseWeather),
            "Dead and unrelated-parent fields cannot affect the tornado being sampled");
        foreach (var safeParent in new[] { parent with { VisualOnly = true }, parent with { SmoothedStage = 2.9f },
            parent with { Tornadoes = [tornado with { VisualOnly = true }] }, parent with { Tornadoes = [tornado with { TouchdownAmount = 0 }] },
            parent with { Tornadoes = [tornado with { SmoothedWindSpeed = 90 }] }, parent with { Tornadoes = [tornado with { SmoothedWidthMeters = 30 }] },
            parent with { Tornadoes = [] } })
        {
            var safe = baseline with { Storms = [safeParent] };
            Check(ReferenceEquals(StormChaseVortexFields.Apply(safe, 73019, seconds), safe),
                "Unorganized, weak, narrow, absent or visual-only source remains untouched: " + checks.Count);
        }
        var detached = StormChaseVortexFields.Apply(first with { Storms = [first.ActiveStorms.Single() with { Tornadoes = [] }] }, 73019, seconds);
        Check(detached.ActiveStorms.Single().ActiveVorticies.Count == 0, "Retiring the parent tornado removes its physical subfields");
        string report = Path.Combine(output, "vortex-fields.json");
        File.WriteAllText(report, JsonSerializer.Serialize(new { Passed = true, Checks = checks, GridSamples = gridSamples,
            MaximumSampledMph = maximum, Scope = "Deterministic replicated physical wind sampling; native PhysX impulses and motion use this shared sampler.", Samples = samples }, JsonOptions));
        Console.WriteLine($"Physical vortex field checks passed: {checks.Count}; {report}");
        return 0;
    }
    private static readonly JsonSerializerOptions JsonOptions = new() { IncludeFields = true, WriteIndented = true };
    private static string Json<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);
    private static float HorizontalDistance(Vector3f a, Vector3f b) => MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z));
}
