using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Engine.Environment;
using Engine.Rendering.Backend;
using Engine.UniStorm;
using Shared.Contracts.Rendering;
using Shared.Contracts.World;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

internal static partial class Showcase
{
    private static int VerifyLiveStormControls(string[] args, string output)
    {
        using var surface = new NativeSurface(640, 360, visible: false);
        surface.EnableFlyCameraControls();
        var controller = new LiveStormController([]);
        var checks = new List<string>();
        void Check(bool valid, string message)
        {
            if (!valid) throw new InvalidOperationException(message);
            checks.Add(message);
        }
        void Key(int key, bool repeated = false)
        {
            if (!PostStormKey(surface.WindowHandle, 0x0100, (nuint)key, repeated ? new nint(1L << 30) : 0))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            surface.PumpMessages();
            while (surface.TryGetStormKey(out int queued)) controller.HandleKey(queued);
        }
        Key(0x50);
        controller.Update(1);
        Check(controller.Paused && controller.Simulation.TickCount == 0, "Native P pauses the source tick clock.");
        Key(0x50, repeated: true);
        Check(controller.Paused, "Repeated keydown does not toggle pause.");
        Key(0x50);
        controller.Update(0.5);
        Check(!controller.Paused && controller.Simulation.TickCount == 200, "Unpaused20x advances exactly200ticks in half a second.");
        Key(0xDB);
        Check(controller.PlaybackSpeed == 10, "Native [ lowers playback speed.");
        Key(0xDD);
        Check(controller.PlaybackSpeed == 20, "Native ] increases playback speed.");
        foreach (var (speed, lower, higher) in new (string, double, double)[] { ("15", 10, 20), ("200", 80, 200), ("0.1", 0.1, 1) })
        {
            var custom = new LiveStormController(["--storm-speed", speed]);
            custom.HandleKey(0xDD);
            Check(custom.PlaybackSpeed == higher, $"Increase from custom{speed}x selects the next higher supported speed or preserves the upper bound.");
            custom = new LiveStormController(["--storm-speed", speed]);
            custom.HandleKey(0xDB);
            Check(custom.PlaybackSpeed == lower, $"Decrease from custom{speed}x selects the next lower supported speed or preserves the lower bound.");
        }
        for (int index = 0; index < 6; index++)
        {
            Key(0x31 + index);
            var storm = controller.Simulation.Current.ActiveStorms.Single();
            Check(controller.PresetIndex == index && storm.MaxWindSpeed == LiveStormController.Presets[index].PeakMph && controller.Simulation.TickCount == 0,
                $"Native {index + 1} restarts a full {controller.Preset.Name} source lifecycle.");
        }
        Key(0x47);
        Check(controller.Phase == "Growing" && controller.Simulation.TickCount > 0, "Native G advances actual simulation ticks to the next phase.");
        var frame = CreateLiveFrame(0, 0, controller.Simulation.Current, 640, 360, true, true, controller.HistoryVersion);
        Check(frame.Weather!.ActiveStorms.Single().SmoothedStage is not null, "Live frame uses the engine builder and native stage smoothing.");
        Key(0x52);
        Check(controller.Simulation.TickCount == 0 && controller.Phase == "Cumulus", "Native R restarts from storm birth.");
        controller.Simulation.Advance(100_000);
        Check(controller.Simulation.IsComplete, "Source lifecycle reaches complete dissipation.");
        var complete = CreateLiveFrame(1, 0, controller.Simulation.Current, 640, 360, true, true, controller.HistoryVersion);
        Check(complete.Weather!.ActiveStorms.Count == 0 && complete.Particles is null && complete.Weather.TornadoFunnelAmount == 0 && complete.Weather.Precipitation == 0,
            "Dissipation clears storms, funnel, rain and original-texture particles.");
        controller.Update(2);
        Check(controller.Simulation.IsComplete, "Scheduler leaves a clear interval after death.");
        controller.Update(1.1);
        Check(!controller.Simulation.IsComplete && controller.Simulation.TickCount == 0, "Scheduler starts a fresh seeded storm after the clear interval.");
        File.WriteAllText(Path.Combine(output, "lifecycle-controls.json"), JsonSerializer.Serialize(new { Checks = checks, Passed = checks.Count }, JsonOptions));
        Console.WriteLine($"Live lifecycle native controls passed: {checks.Count}.");
        return 0;
    }

    private static async Task<int> CaptureLifecyclesAsync(Dx12RenderBackend backend, NativeSurface surface,
        RenderGpuDebugValidation validation, string output, string staging, int width, int height, bool particles, WeatherRenderSettings settings)
    {
        var snapshots = new List<(string Name, EnvironmentSceneState State, double SimulationSeconds, CameraState? Camera)>();
        var timelines = new List<object>();
        for (int index = 0; index < 6; index++)
        {
            var simulation = new PmWeatherSupercellSimulation(LiveStormController.CreateOptions(index, 13));
            string previousPhase = LiveStormController.GetPhase(simulation);
            var transitions = new List<object> { new { Phase = previousPhase, Seconds = 0.0, Wind = 0, Width = 15f } };
            bool capturedPeak = false;
            float maximumWidth = 0;
            for (int tick = 0; tick < 100_000 && !simulation.IsComplete; tick++)
            {
                simulation.Advance(0.05);
                var storm = simulation.Current.ActiveStorms.FirstOrDefault();
                maximumWidth = Math.Max(maximumWidth, storm?.WidthMeters ?? 0);
                string phase = LiveStormController.GetPhase(simulation);
                if (phase != previousPhase)
                {
                    transitions.Add(new { Phase = phase, Seconds = simulation.SimulationSeconds, Wind = storm?.WindSpeed ?? 0, Width = storm?.WidthMeters ?? 0 });
                    previousPhase = phase;
                }
                if (!capturedPeak && storm is { IsDying: true, Stage: 3, TicksSinceDying: >= 600 and <= 620 })
                {
                    snapshots.Add(($"intensity-{index + 1}-{LiveStormController.Presets[index].Name.ToLowerInvariant()}", simulation.Current, simulation.SimulationSeconds, null));
                    capturedPeak = true;
                }
            }
            if (!simulation.IsComplete || !capturedPeak) throw new InvalidOperationException($"Incomplete source lifecycle preset {index + 1}.");
            timelines.Add(new { Preset = LiveStormController.Presets[index], DurationSeconds = simulation.SimulationSeconds, simulation.TickCount, MaximumWidth = maximumWidth, Transitions = transitions });
        }
        var life = new PmWeatherSupercellSimulation(LiveStormController.CreateOptions(3, 13));
        void Capture(string name, Func<WeatherStormCellState?, bool> predicate)
        {
            for (int tick = 0; tick < 100_000 && !predicate(life.Current.ActiveStorms.FirstOrDefault()); tick++)
            {
                if (life.IsComplete) throw new InvalidOperationException("Lifecycle completed before " + name);
                life.Advance(0.05);
            }
            if (!predicate(life.Current.ActiveStorms.FirstOrDefault())) throw new InvalidOperationException("Lifecycle never reached " + name);
            snapshots.Add((name, life.Current, life.SimulationSeconds, null));
        }
        Capture("life-01-cumulus", storm => storm is { Stage: 0, Energy: >= 60 });
        Capture("life-02-developing", storm => storm is { Stage: 1, Energy: >= 50 });
        Capture("life-03-wall-cloud", storm => storm is { Stage: 2, Energy: >= 50 });
        Capture("life-04-forming", storm => storm is { Stage: 3, WindSpeed: >= 35 });
        Capture("life-05-touchdown", storm => storm is { Stage: 3, WindSpeed: >= 95 });
        Capture("life-06-mature", storm => storm is { Stage: 3, IsDying: true, TicksSinceDying: >= 600 });
        Capture("life-07-weakening", storm => storm is { Stage: 3, IsDying: true, WindSpeed: <= 110, TicksSinceDying: > 1200 });
        Capture("life-08-rope", storm => storm is { Stage: 3, IsDying: true, WindSpeed: <= 25 });
        Capture("life-09-dissipating", storm => storm is { Stage: 1, IsDying: true, Energy: <= 60 });
        Capture("life-10-clear", _ => life.IsComplete);

        var local = new UniStormSpatialWeatherController(UniStormWeatherCatalog.LoadDefault(), 13,
            new UniStormOptions { AutomaticWeatherEnabled = false, TransitionSeconds = 2 });
        var source = snapshots.First(snapshot => snapshot.Name == "life-06-mature");
        var spatialReports = new List<object>();
        var spatialStates = new Dictionary<string, EnvironmentSceneState>();
        var parent = source.State.ActiveStorms.Single();
        float localClock = 0;
        foreach (var (name, observer, expectedInside) in new (string, Vector3f, bool)[]
        {
            ("local-01-outside", new(-5000, 85, 100), false),
            ("local-02-under-cloud", new(-300, 85, 100), true),
            ("local-03-under-anvil", new(parent.Position.X + 2000, 85, parent.Position.Z - 2000), true),
            ("local-04-leaving", new(-5000, 85, 100), false),
            ("local-05-above-cloud", new(0, 4000, 1000), false),
            ("local-06-beyond-anvil", new(parent.Position.X + 6500, 85, parent.Position.Z - 1000), false),
        })
        {
            EnvironmentSceneState composed = source.State;
            for (int tick = 0; tick < 200; tick++)
            {
                localClock += 0.05f;
                composed = local.Update(new(0.05f, localClock, 1, 40, 15, 22, observer), source.State);
            }
            if ((expectedInside && local.StormInfluence < 0.9f) || (!expectedInside && local.StormInfluence > 0.01f))
                throw new InvalidOperationException("UniStorm position transition did not settle: " + name);
            if (!ReferenceEquals(composed.Storms, source.State.Storms))
                throw new InvalidOperationException("Local weather replaced source lifecycle states: " + name);
            if (!ReferenceEquals(composed.Clouds, source.State.Clouds))
                throw new InvalidOperationException("Local weather replaced source cloud geometry: " + name);
            if (expectedInside && local.Snapshot?.TargetProfile.Id != "thunderstorm")
                throw new InvalidOperationException("Local weather did not use the imported rainy Thunderstorm profile.");
            spatialStates[name] = composed;
            var target = name == "local-05-above-cloud" ? new Vector3f(3000, 2000, 4000) : new Vector3f(0, 250, 1000);
            var direction = new System.Numerics.Vector3(target.X - observer.X, target.Y - observer.Y, target.Z - observer.Z);
            direction = System.Numerics.Vector3.Normalize(direction);
            var camera = new CameraState(observer, new(direction.X, direction.Y, direction.Z), 58, 0.5f, 16000);
            snapshots.Add((name, composed, source.SimulationSeconds, camera));
            spatialReports.Add(new { Name = name, Observer = observer, local.LocalWeatherName, local.StormInfluence,
                local.TargetStormInfluence, Precipitation = composed.Precipitation, SunIntensity = composed.Sun.Intensity,
                FogDensity = composed.Atmosphere.FogDensity, SourceStormsPreserved = true });
        }
        var outside = spatialStates["local-01-outside"];
        var inside = spatialStates["local-02-under-cloud"];
        var exited = spatialStates["local-04-leaving"];
        if (inside.Precipitation <= outside.Precipitation + 0.1f || exited.Precipitation > 0.01f
            || inside.Atmosphere.FogDensity <= outside.Atmosphere.FogDensity
            || inside.Sun.Intensity >= outside.Sun.Intensity)
            throw new InvalidOperationException("UniStorm must increase rain/fog, dim the sun, and restore ambient precipitation on exit.");

        long frameNumber = 0, history = 100;
        var reports = new List<object>();
        var images = new Dictionary<string, byte[]>();
        foreach (var snapshot in snapshots)
        {
            history++;
            // Freeze both the genuine simulation snapshot and the texture clock for repeatable captures.
            var frame = CreateLiveFrame(frameNumber, 42, snapshot.State, width, height, particles, true, history, snapshot.Camera);
            //32frames per state also aligns final TAA jitter phase across comparisons.
            for (int warmup = 0; warmup < 31; warmup++)
            {
                surface.PumpMessages();
                await backend.RenderAsync(frame with { FrameNumber = frameNumber++ }, CancellationToken.None);
            }
            ExportCompleted.SetValue(backend, false);
            await backend.RenderAsync(frame with { FrameNumber = frameNumber++ }, CancellationToken.None);
            validation.CollectErrors(backend, snapshot.Name, waitForCompletion: validation.Enabled);
            if (!(bool)ExportCompleted.GetValue(backend)! || !backend.LastFrameStatistics.RequiredResourcesResident)
                throw new InvalidOperationException("Incomplete native lifecycle capture " + snapshot.Name);
            string path = Path.Combine(output, snapshot.Name + ".png");
            File.Copy(staging, path, overwrite: true);
            using var image = Image.Load<Rgba32>(path);
            byte[] rgba = new byte[width * height * 4];
            image.CopyPixelDataTo(rgba);
            double mean = MeanLuminance(rgba), variation = LuminanceVariation(rgba, mean);
            if (image.Width != width || image.Height != height || mean < 0.005 || variation < 0.0001)
                throw new InvalidDataException("Blank or wrongly sized native lifecycle capture " + snapshot.Name);
            images[snapshot.Name] = rgba;
            reports.Add(new { snapshot.Name, snapshot.SimulationSeconds, Path = path, Sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))),
                MeanLuminance = mean, LuminanceVariation = variation, Weather = frame.Weather, Statistics = backend.LastFrameStatistics, GpuTiming = backend.LastGpuTiming });
            Console.WriteLine($"Captured source lifecycle {snapshot.Name}: simulation{snapshot.SimulationSeconds:0.00}s; GPU{backend.LastGpuTiming?.TotalMilliseconds:0.00}ms.");
        }
        var differences = new List<object>();
        for (int index = 1; index < 6; index++)
        {
            string a = snapshots[index - 1].Name, b = snapshots[index].Name;
            double difference = PixelDifference(images[a], images[b]);
            if (difference < 0.0001) throw new InvalidDataException("Intensity images indistinguishable: " + a + " / " + b);
            differences.Add(new { First = a, Second = b, MeanAbsoluteRgbDifference = difference });
        }
        File.WriteAllText(Path.Combine(output, "lifecycle-manifest.json"), JsonSerializer.Serialize(new
        {
            NativeGpu = true, Backend = backend.Info, Hardware = Hardware(backend), Width = width, Height = height,
            WeatherQuality = settings.Name, WeatherRenderScale = settings.Scale, SourceTickRate = 20, Timelines = timelines,
            Captures = reports, IntensityDifferences = differences, SpatialTransitions = spatialReports, DebugValidation = validation.Snapshot,
            EngineAssemblySha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(Dx12RenderBackend).Assembly.Location))),
            EnvironmentAssemblySha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(PmWeatherSupercellSimulation).Assembly.Location))),
            UniStormAssemblySha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(UniStormSpatialWeatherController).Assembly.Location)))
        }, JsonOptions));
        Console.WriteLine("All6source lifecycles and22native lifecycle/intensity/UniStorm captures passed.");
        return 0;
    }

    [DllImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)]
    private static extern bool PostStormKey(nint window, uint message, nuint parameter, nint data);
}
