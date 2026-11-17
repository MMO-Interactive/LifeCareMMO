using System.Diagnostics;
using System.Text.Json;
using System.Collections.Immutable;
using Shared.Contracts.Rendering;
using Shared.Contracts.World;
using StormChase.Protocol;

internal static class StormChaseRadarProbe
{
    public static int Verify(string output)
    {
        Directory.CreateDirectory(output);
        var checks = new List<RadarCheck>();
        void Check(string name, bool passed) => checks.Add(new(name, passed));
        Vector3f Rotate(Vector3f point) => new(-point.Z, point.Y, point.X);
        var storm = new RenderStormCellState("radar-fixture", Vector3f.Zero, Vector3f.Zero, 900, 140, 2100,
            750, 3600, 1, 0, .8f, .5f, .4f, [], Stage: 3, WallCloudAmount: 1, HailCoreAmount: .7f,
            RainWrapAmount: .3f, RearFlankDowndraftAmount: .6f, ForwardFlankCoreOffset: new(500, 0, 300),
            RearFlankCoreOffset: new(-300, 0, -350), HookEchoAmount: .8f, RadarConfidence: 1);
        RenderWeatherState Weather(RenderStormCellState value) => new("Radar", "Thunderstorm", "Rain",
            Intensity: 1, Overcast: 1, FogBoost: 0, VisibilityScale: 1, WindStrength: 1, SurfaceWetness: 1,
            Precipitation: 1, RainParticleAlpha: 1, SnowParticleAlpha: 0, DustParticleAlpha: 0, AshParticleAlpha: 0,
            PollenParticleAlpha: 0, RippleAmount: 0, SplashAmount: 0, PuddleAmount: 0, LightningFlashAmount: 0,
            ThunderAudioAmount: 0, WindAudioAmount: 0, Storms: [value]);
        var point = new Vector3f(400, 2, 200);
        var rotated = storm with { RotationRadians = MathF.PI / 2,
            ForwardFlankCoreOffset = Rotate(storm.ForwardFlankCoreOffset), RearFlankCoreOffset = Rotate(storm.RearFlankCoreOffset) };
        Check("Reflectivity rotates with parent and actual rotated flank offsets",
            MathF.Abs(StormChaseRadar.ReflectivityDbz(point, [storm]) - StormChaseRadar.ReflectivityDbz(Rotate(point), [rotated])) < .001f);
        var immature = storm with { Stage = 0, SmoothedStage = .7f };
        Check("Immature cumulus has no cosmetic hook or rainfall return",
            StormChaseRadar.HookReturn(point, immature) == 0 && StormChaseRadar.ReflectivityDbz(point, [immature]) == 0);
        Check("Hail core changes sampled reflectivity", StormChaseRadar.ReflectivityDbz(storm.ForwardFlankCoreOffset, [storm])
            > StormChaseRadar.ReflectivityDbz(storm.ForwardFlankCoreOffset, [storm with { HailCoreAmount = 0 }]));
        Check("Dry parent has no artificial fixed precipitation blob", StormChaseRadar.ReflectivityDbz(point,
            [storm with { PrecipitationCoreAmount = 0, HailCoreAmount = 0 }]) == 0);
        var tornado = new RenderTornadoState("rotation", storm.Id, Vector3f.Zero, Vector3f.Zero, Vector3f.Zero,
            50, 150, 750, 1, .7f, .4f, WindSpeed: 180, TouchdownSpeed: 90, WidthMeters: 125,
            Stage: 3, TouchdownAmount: 0, WindfieldRadiusMeters: 1500, SmoothedWindSpeed: 180, SmoothedWidthMeters: 125);
        var weather = Weather(storm with { Tornadoes = [tornado] });
        var westSite = new Vector3f(-10000, 25, 0);
        float north = StormChaseRadar.RadialVelocityMetersPerSecond(new(0, 2, 50), weather, westSite);
        float south = StormChaseRadar.RadialVelocityMetersPerSecond(new(0, 2, -50), weather, westSite);
        Check("Same physical vortex produces inbound and outbound radial couplet", north < -20 && south > 20);
        var signals = StormChaseRadar.RotationSignals(weather);
        Check("Strong rotation detection does not assert surface touchdown", signals.Count == 1
            && signals[0].EstimatedWindMph > 80 && !signals[0].SurfaceConfirmed);
        var confirmed = StormChaseRadar.RotationSignals(Weather(storm with { Tornadoes = [tornado with { TouchdownAmount = 1 }] }));
        Check("Surface confirmation is an independent received state", confirmed.Count == 1 && confirmed[0].SurfaceConfirmed
            && MathF.Abs(confirmed[0].EstimatedWindMph - signals[0].EstimatedWindMph) < .001f);
        Check("Velocity projection changes with fixed radar location", MathF.Sign(north) == -MathF.Sign(
            StormChaseRadar.RadialVelocityMetersPerSecond(new(0, 2, 50), weather, new(10000, 25, 0))));
        var sounding = StormChaseAtmosphere.Snapshot(13, 1, 15, 350, 7000, new(0, 0, 0)) with
        {
            Sounding = ImmutableArray.Create(new AtmosphereLevel(0, 1000, 25, 18, 10, 0),
                new AtmosphereLevel(2000, 800, 10, 3, 30, 0))
        };
        Check("Outside storms velocity samples sounding at beam altitude in SI units", MathF.Abs(
            StormChaseRadar.RadialVelocityMetersPerSecond(new(0, 1000, 0), Weather(storm with { VisualOnly = true }), westSite, sounding) - 20) < .001f);
        Check("Sounding fallback never replaces authoritative vortex wind", MathF.Abs(north
            - StormChaseRadar.RadialVelocityMetersPerSecond(new(0, 2, 50), weather, westSite, sounding)) < .001f);
        var cache = new StormChaseRadarCache();
        for (int i = 0; i < 600; i++) cache.Get(i / 60d, i * 5, 0, 3000, StormChaseRadarProduct.Reflectivity, weather);
        Check("Moving camera at 60Hz does not resample radar more than four Hz", cache.RebuildCount <= 40);
        long before = cache.RebuildCount;
        var sweep = cache.Get(10, 0, 0, 3000, StormChaseRadarProduct.RadialVelocity, weather);
        Check("Product toggle refreshes current product immediately", sweep.Product == StormChaseRadarProduct.RadialVelocity
            && cache.RebuildCount == before + 1);
        Check("Bounded circular grid and sampled velocities are finite", sweep.Values.Length == 48 * 48
            && sweep.Values.All(float.IsFinite) && sweep.Values[0] == 0);
        var clock = Stopwatch.StartNew();
        for (int i = 0; i < 40; i++) cache.Get(11 + i * .25, 0, 0, 3000, StormChaseRadarProduct.RadialVelocity, weather);
        clock.Stop();
        bool passed = checks.All(check => check.Passed);
        File.WriteAllText(Path.Combine(output, "radar-check.json"), JsonSerializer.Serialize(new
        {
            Passed = passed, Checks = checks, SweepHz = 4, Resolution = 48,
            FortyVelocitySweepsMilliseconds = clock.Elapsed.TotalMilliseconds,
            Reflectivity = "Game precipitation/hail proxy in dBZ", Velocity = "Physical radial wind in m/s",
            Site = StormChaseRadar.RadarSite
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Radar: {checks.Count} checks, passed={passed}, mean sweep {clock.Elapsed.TotalMilliseconds / 40:0.000} ms.");
        return passed ? 0 : 1;
    }

    private sealed record RadarCheck(string Name, bool Passed);
}
