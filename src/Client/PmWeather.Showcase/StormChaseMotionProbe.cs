using System.Text.Json;
using Engine.Environment;
using Engine.Rendering.Pipeline;
using Engine.Rendering.Scene;
using Engine.UniStorm;
using Shared.Contracts.Rendering;
using Shared.Contracts.World;

internal static partial class Showcase
{
    /// <summary>CPU integration over actual seeded source tracks, frame mapping and local canopy weather.</summary>
    private static int VerifyChaseMotion(string output)
    {
        var checks = new List<string>();
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            checks.Add(message);
        }
        var firstOptions = LiveStormController.CreateChaseOptions(3, 13);
        var secondOptions = LiveStormController.CreateChaseOptions(3, 14);
        Check(firstOptions.Origin != secondOptions.Origin && firstOptions.Velocity != secondOptions.Velocity,
            "Seeds 13 and 14 produce distinct storm birthplaces and native track velocities.");
        var controller = new LiveStormController(["--chase-motion", "--controlled-lifecycle", "--storm-speed", "20"]);
        var birth = controller.Simulation.Current.ActiveStorms.Single();
        Check(controller.ChaseMotionEnabled && birth.Position == firstOptions.Origin && birth.Velocity == firstOptions.Velocity,
            "The live chase starts from its real configured source origin and velocity.");
        controller.Update(1);
        var moving = controller.Simulation.Current.ActiveStorms.Single();
        float traveled = MotionProbeDistance(moving.Position, birth.Position);
        Check(controller.Simulation.TickCount == 400 && traveled is >= 30 and <= 42.1f,
            "One wall second at 20x advances 400 native ticks and translates the whole storm 30..42 meters.");
        Check(MotionProbeDistance(moving.Position, birth.Position + birth.Velocity * 20) < .05f,
            "The accumulated native position agrees with velocity times simulation seconds.");
        Check(Math.Abs(controller.TrackSpeedMph - traveled / .44704f) < .1f,
            "The displayed chase speed agrees with the actual storm translation.");

        var lowRate = new LiveStormController(["--chase-motion", "--controlled-lifecycle", "--storm-speed", "20"]);
        var highRate = new LiveStormController(["--chase-motion", "--controlled-lifecycle", "--storm-speed", "20"]);
        for (int i = 0; i < 60; i++) lowRate.Update(1d / 30);
        for (int i = 0; i < 288; i++) highRate.Update(1d / 144);
        var lowStorm = lowRate.Simulation.Current.ActiveStorms.Single();
        var highStorm = highRate.Simulation.Current.ActiveStorms.Single();
        Check(lowRate.Simulation.TickCount == 800 && highRate.Simulation.TickCount == 800
            && lowStorm.Position == highStorm.Position && lowStorm.Stage == highStorm.Stage
            && lowStorm.Energy == highStorm.Energy && lowStorm.WidthMeters == highStorm.WidthMeters
            && lowStorm.SpinRadians == highStorm.SpinRadians,
            "30Hz and 144Hz UI updates produce identical source ticks, movement and storm state.");
        controller.HandleKey(0x50);
        int pausedTick = controller.Simulation.TickCount;
        float pausedVisual = controller.VisualSeconds;
        var paused = controller.Simulation.Current;
        controller.Update(2);
        Check(controller.Paused && controller.Simulation.TickCount == pausedTick
            && controller.VisualSeconds == pausedVisual && ReferenceEquals(paused, controller.Simulation.Current),
            "P freezes source time, native motion and visual time.");
        controller.HandleKey(0x50); controller.Update(.25);
        Check(!controller.Paused && controller.Simulation.TickCount == pausedTick + 100
            && MotionProbeDistance(controller.Simulation.Current.ActiveStorms.Single().Position, moving.Position) >= 7.5f,
            "Resuming advances the original source route without teleporting or losing elapsed time.");

        for (int tick = 0; tick < 100_000
            && controller.Simulation.Current.ActiveStorms.Single() is not { Stage: 3, IsDying: true }; tick++)
            controller.Simulation.Advance(.05);
        var matureScene = controller.Simulation.Current;
        var mature = matureScene.ActiveStorms.Single();
        Check(mature is { Stage: 3, IsDying: true } && mature.Tornadoes.Count > 0,
            "The real moving source lifecycle reaches a mature tornado.");
        Check(MotionProbeDistance(mature.Position, new(95, 0, 1040)) < 2,
            "Seed 13 reaches its authored farm intercept at actual source peak time.");
        var builder = new DefaultRenderFrameBuilder();
        var camera = new CameraState(new(-300, 3.5f, 20), new(0, .1f, 1), 58, .1f, 16000);
        RenderFrame Map(EnvironmentSceneState source, long number) => builder.Build(number,
            DateTimeOffset.UnixEpoch.AddSeconds(controller.VisualSeconds), new(1280, 720),
            new RenderScene { Camera = camera, Environment = source, AnimationTimeSeconds = controller.VisualSeconds });
        var beforeFrame = Map(matureScene, 0);
        controller.Update(.5);
        var afterScene = controller.Simulation.Current;
        var after = afterScene.ActiveStorms.Single();
        var afterFrame = Map(afterScene, 1);
        var mapped = afterFrame.Weather!.ActiveStorms.Single();
        Check(MotionProbeDistance(after.Position, mature.Position) is >= 15 and <= 21.1f,
            "The complete cloud and tornado system keeps translating during the mature hold.");
        Check(mapped.Position == after.Position && mapped.Velocity == after.Velocity
            && mapped.SmoothedStage == after.SmoothedStage && mapped.SmoothedWindSpeed == after.SmoothedWindSpeed,
            "The actual native frame builder transfers moving centers, velocities and source smoothing.");
        Check(after.Tornadoes.All(t => t.Position == after.Position + t.LocalOffset && t.Velocity == after.Velocity)
            && mapped.Tornadoes.All(t => t.Position == mapped.Position + t.LocalOffset && t.Velocity == mapped.Velocity),
            "Source and renderer tornado positions and velocities stay attached to their parent storm.");
        Check(beforeFrame.Weather!.ActiveStorms.Single().Position != mapped.Position,
            "Consecutive native render contracts contain different storm positions.");

        long worldVersion = controller.WorldResetVersion, historyVersion = controller.HistoryVersion;
        controller.Restart(newSeed: true);
        var newBirth = controller.Simulation.Current.ActiveStorms.Single();
        Check(newBirth.Position == secondOptions.Origin && newBirth.Velocity == secondOptions.Velocity
            && controller.Simulation.TickCount == 0 && controller.HistoryVersion == historyVersion + 1,
            "A fresh seed starts a genuinely different source track and resets temporal history.");
        Check(controller.WorldResetVersion == worldVersion,
            "A new naturally scheduled storm preserves world destruction and deployed instruments.");
        controller.HandleKey(0x52);
        Check(controller.WorldResetVersion == worldVersion + 1 && controller.Simulation.TickCount == 0,
            "Explicit R advances the world reset version and restarts storm birth.");

        // A deliberately spawned, slow-growing wall cloud traverses a fixed
        // observer through real 20Hz source integration. No snapshots are moved
        // by the test, and shape changes remain part of the native lifecycle.
        var canopy = new PmWeatherSupercellSimulation(firstOptions with
        {
            Origin = new(-1800, 0, 0), Velocity = new(120, 0, 0), InitialStage = 2,
            InitialEnergy = 80, InitialGrowthSpeed = 1000, CoreRadiusMeters = 350,
            AnvilRadiusMeters = 450, UpdraftRadiusMeters = 100
        });
        var local = new UniStormSpatialWeatherController(UniStormWeatherCatalog.LoadDefault(), 13,
            new UniStormOptions { AutomaticWeatherEnabled = false, AdvanceInternalClock = false,
                TransitionSeconds = .15f, LightningEnabled = false });
        var observer = new Vector3f(0, 2, 0);
        var samples = new List<object>();
        float firstRain = 0, strongestRain = 0, finalRain = 0, maximumInfluence = 0;
        float firstFog = 0, greatestFog = 0, firstSun = 0, weakestSun = float.PositiveInfinity;
        bool sourcePreserved = true;
        for (int tick = 0; tick <= 900; tick++)
        {
            if (tick > 0) canopy.Advance(.05);
            var source = canopy.Current;
            var composed = local.Update(new(.05f, (float)canopy.SimulationSeconds, 2026, 150, 15, 22, observer), source);
            sourcePreserved &= ReferenceEquals(composed.Storms, source.Storms) && ReferenceEquals(composed.Clouds, source.Clouds);
            float rain = composed.WeatherEffects?.RainParticleAlpha ?? 0;
            if (tick == 0) { firstRain = rain; firstFog = composed.Atmosphere.FogDensity; firstSun = composed.Sun.Intensity; }
            strongestRain = Math.Max(strongestRain, rain); finalRain = rain;
            greatestFog = Math.Max(greatestFog, composed.Atmosphere.FogDensity);
            weakestSun = Math.Min(weakestSun, composed.Sun.Intensity);
            maximumInfluence = Math.Max(maximumInfluence, local.StormInfluence);
            if (tick % 20 == 0) samples.Add(new { canopy.TickCount, canopy.SimulationSeconds,
                Center = source.ActiveStorms.Single().Position, local.StormInfluence, local.TargetStormInfluence,
                Rain = rain, Fog = composed.Atmosphere.FogDensity, Sun = composed.Sun.Intensity, Weather = local.LocalWeatherName });
        }
        Check(sourcePreserved, "Observer-local UniStorm preserves every actual moving source storm and cloud snapshot.");
        Check(maximumInfluence > .8f && firstRain < .01f && strongestRain > .5f && finalRain < .01f,
            "At a fixed observer, genuine source translation brings canopy rain in and restores clear weather after it leaves.");
        Check(greatestFog > firstFog + .00001f && weakestSun < firstSun - .05f,
            "The moving canopy also increases local fog and dims the sun through actual UniStorm transitions.");
        Check(canopy.TickCount == 900 && canopy.Current.ActiveStorms.Single().Position == new Vector3f(3600, 0, 0),
            "The canopy enter/exit test advances the real source position through 900 fixed ticks.");

        Directory.CreateDirectory(output);
        string path = Path.Combine(output, "storm-chase-motion.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            Passed = checks.Count, Checks = checks, SourceTickRate = 20, PlaybackSpeed = 20,
            OneWallSecondDistanceMeters = traveled, SourceRoutes = new[] { firstOptions, secondOptions },
            MaturePosition = mature.Position, RenderPositionBefore = beforeFrame.Weather!.ActiveStorms.Single().Position,
            RenderPositionAfter = mapped.Position, FreshBirth = newBirth.Position,
            WorldPersistsBetweenStorms = true, CanopyMovement = new { NativeTicks = canopy.TickCount,
                FirstRain = firstRain, StrongestRain = strongestRain, FinalRain = finalRain,
                MaximumInfluence = maximumInfluence, Samples = samples }
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Chase motion CPU checks passed: {checks.Count}; storm traveled {traveled:F2}m/wall-second; canopy rain {firstRain:F3} → {strongestRain:F3} → {finalRain:F3}; {path}");
        return 0;
    }

    private static float MotionProbeDistance(Vector3f a, Vector3f b) =>
        MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y) + (a.Z - b.Z) * (a.Z - b.Z));
}
