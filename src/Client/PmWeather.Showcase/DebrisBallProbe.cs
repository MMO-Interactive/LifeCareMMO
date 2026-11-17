using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using Engine.Environment;
using Engine.Rendering.Backend;
using Engine.UniStorm;
using Shared.Contracts.Rendering;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

internal static partial class Showcase
{
    private static async Task<int> CaptureDebrisAsync(Dx12RenderBackend backend, NativeSurface surface,
        RenderGpuDebugValidation validation, string output, string staging, int width, int height)
    {
        var snapshots = new List<(string Name, EnvironmentSceneState State, bool LocalWeather, bool RaisedView, float Hour)>();
        void AddSnapshot(string name, EnvironmentSceneState state, bool localWeather, bool raisedView = false, float hour = 15)
            => snapshots.Add((name, state, localWeather, raisedView, hour));
        var severe = new PmWeatherSupercellSimulation(LiveStormController.CreateOptions(3, 13));
        void AdvanceUntil(PmWeatherSupercellSimulation simulation, Func<WeatherStormCellState?, bool> reached)
        {
            for (int tick = 0; tick < 100_000 && !reached(simulation.Current.ActiveStorms.FirstOrDefault()); tick++)
            {
                if (simulation.IsComplete) throw new InvalidOperationException("Storm completed before debris capture state.");
                simulation.Advance(0.05);
            }
            if (!reached(simulation.Current.ActiveStorms.FirstOrDefault())) throw new InvalidOperationException("Unreached debris capture state.");
        }
        AdvanceUntil(severe, storm => storm is { Stage: 3, WindSpeed: >= 35 });
        AddSnapshot("forming-no-debris", severe.Current, true);
        AdvanceUntil(severe, storm => storm is { IsDying: true, Stage: 3, TicksSinceDying: >= 600 });
        AddSnapshot("severe-peak-source", severe.Current, false);
        AddSnapshot("severe-peak-unistorm", severe.Current, true);
        AddSnapshot("severe-peak-sunset", severe.Current, true, hour: 18.3f);
        AddSnapshot("severe-peak-moonlit", severe.Current, true, hour: 22.5f);
        AdvanceUntil(severe, storm => storm is { Stage: 3, IsDying: true, WindSpeed: <= 76, TicksSinceDying: > 1200 });
        AddSnapshot("weakening-76-ground-view", severe.Current, true);
        AddSnapshot("weakening-76-raised-view", severe.Current, true, true);
        AdvanceUntil(severe, storm => storm is { Stage: 3, IsDying: true, WindSpeed: <= 25 });
        AddSnapshot("rope-no-debris", severe.Current, true);
        var violent = new PmWeatherSupercellSimulation(LiveStormController.CreateOptions(5, 13));
        AdvanceUntil(violent, storm => storm is { IsDying: true, Stage: 3, TicksSinceDying: >= 600 });
        AddSnapshot("violent-wide-debris", violent.Current, true);
        AddSnapshot("violent-wide-debris-raised", violent.Current, true, true);
        severe.Advance(5000);
        AddSnapshot("clear-no-debris", severe.Current, true);

        long number = 0, history = 500;
        var reports = new List<object>();
        foreach (var snapshot in snapshots)
        {
            history++;
            var storm = snapshot.State.ActiveStorms.FirstOrDefault();
            var tornado = storm?.Tornadoes.FirstOrDefault();
            CameraState? camera = null;
            if (snapshot.RaisedView && storm is not null)
            {
                var position = new Vector3(storm.Position.X - 220, 180, storm.Position.Z - 460);
                var target = new Vector3(storm.Position.X, storm.Position.Y + 35, storm.Position.Z);
                var forward = Vector3.Normalize(target - position);
                camera = new(new(position.X, position.Y, position.Z), new(forward.X, forward.Y, forward.Z), 58, 0.5f, 16000);
            }
            var frame = CreateLiveFrame(number, 42, snapshot.State, width, height, true, true, history, camera);
            var environment = snapshot.State;
            if (snapshot.LocalWeather)
            {
                var local = new UniStormSpatialWeatherController(UniStormWeatherCatalog.LoadDefault(), 13,
                    new UniStormOptions { AutomaticWeatherEnabled = false, TransitionSeconds = 2 });
                for (int tick = 0; tick < 200; tick++)
                    environment = local.Update(new(0.05f, tick * 0.05f, 1, 40, snapshot.Hour, 22, frame.Camera.Position), snapshot.State);
                frame = CreateLiveFrame(number, 42, environment, width, height, true, true, history, camera);
            }
            var dustBatches = frame.Particles?.Batches.Where(batch => batch.BatchId != "live-rain").ToArray();
            bool shouldHaveDust = tornado is { DebrisAmount: > 0.001f };
            if (shouldHaveDust && dustBatches?.Length != 3 || !shouldHaveDust && dustBatches?.Length > 0)
                throw new InvalidOperationException("Debris particles disagree with actual lifecycle state: " + snapshot.Name);
            for (int warmup = 0; warmup < 31; warmup++)
            {
                surface.PumpMessages();
                await backend.RenderAsync(frame with { FrameNumber = number++ }, CancellationToken.None);
            }
            ExportCompleted.SetValue(backend, false);
            await backend.RenderAsync(frame with { FrameNumber = number++ }, CancellationToken.None);
            for (int settle = 0; settle < 32 && !(bool)ExportCompleted.GetValue(backend)!; settle++)
            {
                surface.PumpMessages();
                await backend.RenderAsync(frame with { FrameNumber = number++ }, CancellationToken.None);
            }
            validation.CollectErrors(backend, snapshot.Name, waitForCompletion: validation.Enabled);
            if (!(bool)ExportCompleted.GetValue(backend)! || !backend.LastFrameStatistics.RequiredResourcesResident)
                throw new InvalidOperationException("Native debris capture incomplete: " + snapshot.Name);
            string path = Path.Combine(output, snapshot.Name + ".png");
            File.Copy(staging, path, overwrite: true);
            using var image = Image.Load<Rgba32>(path);
            byte[] pixels = new byte[width * height * 4];
            image.CopyPixelDataTo(pixels);
            double mean = MeanLuminance(pixels), variation = LuminanceVariation(pixels, mean);
            if (image.Width != width || image.Height != height || mean < 0.005 || variation < 0.0001)
                throw new InvalidDataException("Blank native debris view: " + snapshot.Name);
            reports.Add(new { snapshot.Name, Path = path, WindMph = storm?.WindSpeed ?? 0,
                WidthMeters = storm?.SmoothedWidthMeters ?? 0, DebrisAmount = tornado?.DebrisAmount ?? 0,
                RadiusMeters = tornado is null ? 0 : TornadoDebrisParticles.Radius(tornado.SmoothedWidthMeters ?? tornado.WidthMeters),
                DustInstances = dustBatches?.Sum(batch => batch.Instances.Count) ?? 0,
                FrameNumber = number - 1, Camera = frame.Camera, Weather = frame.Weather, Sky = frame.Sky, snapshot.Hour,
                MeanLuminance = mean, LuminanceVariation = variation, Statistics = backend.LastFrameStatistics,
                GpuTiming = backend.LastGpuTiming, Sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) });
            Console.WriteLine($"Native debris view: {snapshot.Name}; wind {storm?.WindSpeed ?? 0}mph, debris {tornado?.DebrisAmount ?? 0:0.00}, GPU {backend.LastGpuTiming?.TotalMilliseconds:0.00}ms.");
        }
        File.WriteAllText(Path.Combine(output, "debris-manifest.json"), JsonSerializer.Serialize(new
        {
            Hardware = Hardware(backend), NativeGpu = true, Width = width, Height = height, Captures = reports,
            EngineAssemblySha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(Dx12RenderBackend).Assembly.Location))),
            DebugValidation = validation.Snapshot,
        }, JsonOptions));
        Console.WriteLine($"{reports.Count} native debris/lifecycle views passed.");
        return 0;
    }
}
