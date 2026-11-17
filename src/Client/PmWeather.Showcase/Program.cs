using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Engine.Core.Diagnostics;
using Engine.Environment;
using Engine.GUI;
using Engine.Rendering.Assets;
using Engine.Rendering.Backend;
using Engine.Rendering.Configuration;
using Engine.Rendering.Platform;
using Engine.Rendering.Pipeline;
using Engine.Rendering.Scene;
using Engine.UniStorm;
using Shared.Contracts.Content;
using Shared.Contracts.Rendering;
using Shared.Contracts.World;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Vortice.DXGI;

return await ShowcaseLauncher.RunAsync(args);

internal static partial class Showcase
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly FieldInfo ExportCompleted = typeof(Dx12RenderBackend).GetField("frameExportCompleted", BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new InvalidOperationException("MMOI native PNG export interface changed.");
    private static readonly string[] RequiredTextures = ["noise", "noisex", "noisey", "noisez", "rain", "mist", "smoke_0", "smoke_1", "moon", "nightsky"];

    public static async Task<int> RunAsync(string[] args)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The native MMOI DX12 showcase requires Windows.");
        string output = Path.GetFullPath(Value(args, "--output", Path.Combine(AppContext.BaseDirectory, "captures")));
        if (args.Contains("--package-avatar", StringComparer.OrdinalIgnoreCase))
        {
            string avatarSource = OptionalValue(args, "--avatar-catalog")
                ?? @"C:\Users\reviv\youradventure\Old Engine\artifacts\avatar\uma\uma-catalog.json";
            using var packagedAvatar = new StormChaseAvatar(CreateCatalog(), avatarSource);
            StormChaseAvatarCache.ExportPackage(avatarSource, output);
            return 0;
        }
        int width = int.Parse(Value(args, "--width", "1280"), CultureInfo.InvariantCulture);
        int height = int.Parse(Value(args, "--height", "720"), CultureInfo.InvariantCulture);
        bool demo = args.Contains("--demo", StringComparer.OrdinalIgnoreCase);
        bool game = args.Contains("--game", StringComparer.OrdinalIgnoreCase);
        bool legacyDemo = args.Contains("--legacy-demo", StringComparer.OrdinalIgnoreCase);
        bool windowSmoke = args.Contains("--window-smoke", StringComparer.OrdinalIgnoreCase);
        bool flySmoke = args.Contains("--fly-smoke", StringComparer.OrdinalIgnoreCase);
        bool demoThreadSmoke = args.Contains("--demo-thread-smoke", StringComparer.OrdinalIgnoreCase);
        bool performance = args.Contains("--performance", StringComparer.OrdinalIgnoreCase);
        bool lifecycle = args.Contains("--lifecycle", StringComparer.OrdinalIgnoreCase);
        bool particles = !args.Contains("--no-particles", StringComparer.OrdinalIgnoreCase);
        bool quick = args.Contains("--quick", StringComparer.OrdinalIgnoreCase);
        bool debug = args.Contains("--debug", StringComparer.OrdinalIgnoreCase);
        string scene = Value(args, "--scene", "all");
        var weatherSettings = WeatherRenderSettings.Read(args);
        if (args.Contains("--contracts-smoke", StringComparer.OrdinalIgnoreCase))
            return StormChaseContractsProbe.Run(output);
        if (args.Contains("--campaign-housing-smoke", StringComparer.OrdinalIgnoreCase))
            return StormChaseHousingProbe.Run(output);
        if (performance && (demo || windowSmoke || flySmoke || quick || scene != "all"))
            throw new ArgumentException("--performance runs the fixed three-scene benchmark; omit demo/smoke/quick/scene switches.");
        if (width is < 320 or > 4096 || height is < 180 or > 4096) throw new ArgumentOutOfRangeException(nameof(args));
        Directory.CreateDirectory(output);
        if (args.Contains("--verify-atmosphere", StringComparer.OrdinalIgnoreCase))
            return VerifyAtmosphere(output);
        if (args.Contains("--atmosphere-integration-smoke", StringComparer.OrdinalIgnoreCase))
            return VerifyAtmosphereIntegration(output);
        if (args.Contains("--online-atmosphere-smoke", StringComparer.OrdinalIgnoreCase))
            return StormChaseOnlineAtmosphereProbe.Run(output);
        if (args.Contains("--vortex-fields-smoke", StringComparer.OrdinalIgnoreCase))
            return StormChaseVortexFieldsProbe.Run(output);
        if (args.Contains("--radar-test", StringComparer.OrdinalIgnoreCase))
            return StormChaseRadarProbe.Verify(output);
        if (args.Contains("--campaign-weather-smoke", StringComparer.OrdinalIgnoreCase))
            return VerifyCampaignWeather(output);
        if (args.Contains("--campaign-world-smoke", StringComparer.OrdinalIgnoreCase))
            return StormChaseWorldProbe.Run(output, args.Contains("--destruction-stress"));
        if (args.Contains("--npc-avatar-cost", StringComparer.OrdinalIgnoreCase))
            return StormChaseNpcAvatarCostProbe.Run(output, args);
        if (args.Contains("--npc-smoke", StringComparer.OrdinalIgnoreCase))
            return StormChaseNpcProbe.Run(output, args);
        if (args.Contains("--campaign-gameplay-smoke", StringComparer.OrdinalIgnoreCase))
            return VerifyCampaignGameplay(output);
        if (args.Contains("--campaign-lot-smoke", StringComparer.OrdinalIgnoreCase))
            return StormChaseLotProbe.Run(output);
        if (args.Contains("--campaign-interior-smoke", StringComparer.OrdinalIgnoreCase))
            return StormChaseInteriorProbe.Run(output);
        if (args.Contains("--wall-cloud-smoke", StringComparer.OrdinalIgnoreCase))
            return VerifyWallCloudSource(output);
        if (args.Contains("--storm-variation-smoke", StringComparer.OrdinalIgnoreCase))
            return VerifyStormVariation(output);
        if (args.Contains("--chase-motion-smoke", StringComparer.OrdinalIgnoreCase))
            return VerifyChaseMotion(output);
        if (args.Contains("--lifecycle-smoke", StringComparer.OrdinalIgnoreCase))
            return VerifyLiveStormControls(args, output);
        if (demoThreadSmoke)
        {
            using var probeSurface = new NativeSurface(width, height, visible: false);
            return DemoThreadProbe.Run(probeSurface, output, number => CreateFrame(
                new("thread-probe", number, 42, 3, 76, 150, 230, 0.08f, true, true), width, height, false));
        }
        Console.WriteLine($"Weather quality: {weatherSettings.Name}; weather scale={weatherSettings.Scale.ToString("0.####", CultureInfo.InvariantCulture)}, lighting samples={weatherSettings.LightSamples}.");
        using var validation = RenderGpuDebugValidation.TryEnableBeforeDevice(debug);
        using var surface = new NativeSurface(width, height, game || demo || windowSmoke || flySmoke);
        if ((game || demo) && args.Contains("--fullscreen", StringComparer.OrdinalIgnoreCase)) surface.ToggleFullscreen();
        var catalog = CreateCatalog();
        var logger = new CaptureLogger();
        string staging = Path.Combine(output, "native-frame.png");
        var options = new RendererOptions
        {
            DefaultViewport = new(width, height), EnableDebugOverlays = false, AllowSoftwareAdapter = false,
            EnableTearingPresentation = true, EnableGpuTimings = true, EnableHdrPipeline = true,
            FrameExportPath = demo && legacyDemo ? null : staging, ShadowMapResolution = 2048, ShadowCascadeCount = 2, ShadowDistance = 1400,
            RenderScale = 1f, PostProcessQuality = 2,
            // The engine's 512 MB default cannot hold the chase world's terrain
            // arrays, avatar, Crest and weather textures; the remainder never
            // became resident. 2 GB fits comfortably on the 12 GB target GPU.
            TextureMemoryBudgetBytes = 2048L * 1024 * 1024,
            WeatherRenderScale = weatherSettings.Scale, WeatherQuality = weatherSettings.Quality,
            // Keep one live-demo GPU frame outstanding; its wait belongs to the
            // render thread, then SubmitLatest supplies the freshest camera.
            SynchronizeFrameResources = demo || game || args.Contains("--client-performance-smoke",StringComparer.OrdinalIgnoreCase),
        };
        if (game) return RunStormChaseGame(options, surface, catalog, logger, validation, particles, weatherSettings.Name, output, args);
        if (args.Contains("--client-performance-smoke",StringComparer.OrdinalIgnoreCase))
        {
            var clientPerformance=VerifyClientPerformanceAsync(options,surface,catalog,logger,validation,output,staging,args);
            DemoThreadPump.Wait(clientPerformance,surface);
            return clientPerformance.GetAwaiter().GetResult();
        }
        if (args.Contains("--hud-smoke", StringComparer.OrdinalIgnoreCase))
        {
            var hudProof = VerifyLifeCareHudAsync(options, surface, catalog, logger, output, staging);
            DemoThreadPump.Wait(hudProof, surface);
            return hudProof.GetAwaiter().GetResult();
        }
        if (args.Contains("--online-smoke", StringComparer.OrdinalIgnoreCase))
        {
            var onlineProof = VerifyStormChaseOnlineAsync(options, surface, catalog, logger, validation, output, staging, args);
            DemoThreadPump.Wait(onlineProof, surface);
            return onlineProof.GetAwaiter().GetResult();
        }
        if (args.Contains("--frontend-smoke", StringComparer.OrdinalIgnoreCase))
            return await VerifyStormChaseFrontendAsync(options, surface, catalog, logger, validation, output, staging);
        if (args.Contains("--radar-ui-smoke", StringComparer.OrdinalIgnoreCase))
            return await VerifyStormChaseRadarUiAsync(options, surface, catalog, logger, validation, output, staging);
        if (args.Contains("--weather-integration-smoke", StringComparer.OrdinalIgnoreCase))
            return await VerifyStormChaseWeatherIntegrationAsync(options, surface, catalog, logger, validation, output, staging);
        if (demo) return legacyDemo
            ? RunDemo(options, surface, catalog, logger, validation, particles, weatherSettings.Name, new LiveStormController(args))
            : RunStormChaseDemo(options, surface, catalog, logger, validation, particles, weatherSettings.Name, new LiveStormController(args), output, args);
        if (args.Contains("--campaign-capture", StringComparer.OrdinalIgnoreCase))
            return await RunCampaignCaptureAsync(options, surface, catalog, logger, validation,
                particles, weatherSettings.Name, output, staging, width, height, args);
        if (args.Contains("--wall-cloud-capture", StringComparer.OrdinalIgnoreCase))
            return await RunWallCloudCaptureAsync(options, surface, catalog, logger, validation,
                particles, weatherSettings.Name, output, staging, width, height, args);
        if (args.Contains("--storm-variation-capture", StringComparer.OrdinalIgnoreCase))
            return await RunStormVariationCaptureAsync(options, surface, catalog, logger, validation,
                particles, weatherSettings.Name, output, staging, width, height, args);
        if (args.Contains("--chase-capture", StringComparer.OrdinalIgnoreCase))
            return await RunStormChaseCaptureAsync(options, surface, catalog, logger, validation,
                particles, weatherSettings.Name, output, staging, width, height, args);
        await using var backend = new Dx12RenderBackend(options, surface, catalog, logger);
        await backend.InitializeAsync(CancellationToken.None);
        validation.CollectErrors(backend, "initialization");
        if (windowSmoke) return await VerifyWindowModesAsync(backend, surface, validation, output, staging);
        if (flySmoke) return await VerifyFlyCameraAsync(backend, surface, validation, output, staging);
        if (args.Contains("--debris", StringComparer.OrdinalIgnoreCase))
            return await CaptureDebrisAsync(backend, surface, validation, output, staging, width, height);
        if (lifecycle) return await CaptureLifecyclesAsync(backend, surface, validation, output, staging, width, height, particles, weatherSettings);
        CaptureState[] states =
        [
            new("clear-wide", 0, 0, 0, 0, 0, 0, 0, false, false),
            new("developing-wide", 600, 10, 1, 42, 25, 30, 0, false, true),
            new("mature-wide", 2400, 40, 3, 76, 150, 230, 0.08f, false, true),
            new("mature-tornado", 3600, 60, 3, 76, 150, 230, 0.08f, true, true),
            new("mature-tornado-motion", 4080, 68, 3, 76, 150, 230, 0.08f, true, true),
            new("occluding-tornado", 5400, 90, 3, 24, 70, 80, 0.83f, true, true),
            new("squall-wide", 3000, 50, 2, 70, 90, 200, 0, false, true, Kind: RenderStormKind.Squall),
            new("cyclone-wide", 4200, 70, 3, 70, 135, 650, 0, false, true, Kind: RenderStormKind.Cyclone),
            new("fire-whirl", 3600, 60, 3, 70, 90, 80, 0, true, true, Kind: RenderStormKind.FireWhirl),
            new("sunset-tornado", 4800, 80, 3, 76, 150, 230, 0.08f, true, true, Sunset: true),
            new("night-sky", 4800, 80, 0, 0, 0, 0, 0, false, false, Night: true),
            new("night-quarter-moon", 4800, 80, 0, 0, 0, 0, 0, false, false, Night: true, MoonPhase: 3),
            new("night-new-moon", 4800, 80, 0, 0, 0, 0, 0, false, false, Night: true, MoonPhase: 5),
            new("night-tornado", 4800, 80, 3, 76, 150, 230, 0.08f, true, true, Night: true),
            new("depth-occlusion-clear", 3600, 60, 0, 0, 0, 0, 0, true, false, true),
            new("depth-occlusion", 3600, 60, 3, 76, 150, 230, 0.08f, true, true, true),
            new("masked-depth-clear", 3600, 60, 0, 0, 0, 0, 0, true, false, true, MaskedDepth: true),
            new("masked-depth-occlusion", 3600, 60, 3, 76, 150, 230, 0.08f, true, true, true, MaskedDepth: true),
        ];
        if (quick) states = [states[0], states[2], states[3]];
        if (scene != "all")
        {
            states = states.Where(state => string.Equals(state.Name, scene, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (states.Length == 0) throw new ArgumentException("Unknown --scene; use clear-wide, developing-wide, mature-wide, mature-tornado, mature-tornado-motion, occluding-tornado, squall-wide, cyclone-wide, fire-whirl, sunset-tornado, night-sky, night-quarter-moon, night-new-moon, night-tornado, depth-occlusion-clear, depth-occlusion, masked-depth-clear, or masked-depth-occlusion.");
        }
        if (performance)
        {
            var benchmarkStates = states.ToDictionary(state => state.Name, StringComparer.OrdinalIgnoreCase);
            var performanceReport = await WeatherPerformanceProbe.MeasureAsync(backend, surface,
                (name, frameNumber) => CreateFrame(benchmarkStates[name] with { Frame = frameNumber }, width, height, particles),
                output, staging);
            Console.WriteLine($"Performance report: {performanceReport.ReportPath}");
            return 0;
        }
        var reports = new List<object>();
        var images = new Dictionary<string, byte[]>();
        foreach (var state in states)
        {
            // Warm temporal history with a fixed physical time before the exact requested capture.
            for (int warmup = 0; warmup < 24; warmup++)
            {
                surface.PumpMessages();
                await backend.RenderAsync(CreateFrame(state, width, height, particles), CancellationToken.None);
            }
            ExportCompleted.SetValue(backend, false);
            var frame = CreateFrame(state, width, height, particles);
            var started = Stopwatch.GetTimestamp();
            await backend.RenderAsync(frame, CancellationToken.None);
            for (int settle = 0; settle < 32 && !(bool)ExportCompleted.GetValue(backend)!; settle++)
            {
                surface.PumpMessages();
                await backend.RenderAsync(frame, CancellationToken.None);
            }
            validation.CollectErrors(backend, state.Name, waitForCompletion: debug);
            double captureMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (!(bool)ExportCompleted.GetValue(backend)!) throw new InvalidOperationException($"Native capture did not complete: {state.Name}.");
            var statistics = backend.LastFrameStatistics;
            if (!statistics.RequiredResourcesResident || statistics.OpaqueBatchCount == 0)
                throw new InvalidOperationException($"Capture {state.Name} lacks fully resident opaque depth geometry: {statistics}.");
            if (particles && state.Storm && statistics.ParticleBatchCount < 2)
                throw new InvalidOperationException($"Capture {state.Name} did not submit the supplied rain/mist/smoke textures.");
            string path = Path.Combine(output, state.Name + ".png");
            File.Copy(staging, path, overwrite: true);
            using var image = Image.Load<Rgba32>(path);
            if (image.Width != width || image.Height != height) throw new InvalidDataException("Native PNG dimensions mismatch.");
            byte[] pixels = new byte[width * height * 4];
            image.CopyPixelDataTo(pixels);
            images[state.Name] = pixels;
            double mean = MeanLuminance(pixels);
            double variation = LuminanceVariation(pixels, mean);
            if (mean < 0.005 || variation < 0.0001) throw new InvalidDataException($"Native capture is blank: {state.Name}.");
            reports.Add(new { State = state, Path = path, Width = width, Height = height,
                Sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))),
                MeanLuminance = mean, LuminanceVariation = variation, CaptureMilliseconds = captureMilliseconds,
                Statistics = statistics, GpuTiming = backend.LastGpuTiming,
                Weather = frame.Weather, Camera = frame.Camera });
            Console.WriteLine($"Captured {state.Name}: {path}; resident opaque={statistics.OpaqueBatchCount}, particles={statistics.ParticleBatchCount}.");
        }
        double? wideDifference = images.TryGetValue("clear-wide", out var clearWide) && images.TryGetValue("mature-wide", out var matureWide)
            ? PixelDifference(clearWide, matureWide) : null;
        if (wideDifference is < 0.003) throw new InvalidDataException("Storm and clear captures are visually indistinguishable.");
        double? motionDifference = images.TryGetValue("mature-tornado-motion", out var animated) && images.TryGetValue("mature-tornado", out var still)
            ? PixelDifference(still, animated) : null;
        if (motionDifference is < 0.00001) throw new InvalidDataException("Animated storm capture is visually unchanged.");
        var assets = RequiredTextures.Select(name =>
        {
            if (!catalog.TryGetTexture(new("texture", "pmweather/" + name), out var texture) || texture is null)
                throw new InvalidDataException("Missing PMWeather texture: " + name);
            return new { Name = name, texture.Width, texture.Height, texture.MipLevels, texture.ColorSpace,
                RgbaSha256 = Convert.ToHexString(SHA256.HashData(texture.PixelDataRgba8)) };
        }).ToArray();
        string reportPath = Path.Combine(output, "manifest.json");
        File.WriteAllText(reportPath, JsonSerializer.Serialize(new
        {
            Schema = 1, Backend = backend.Info, Hardware = Hardware(backend), Runtime = RuntimeInformation.FrameworkDescription,
            OperatingSystem = RuntimeInformation.OSDescription, ProcessArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
            EngineAssembly = typeof(Dx12RenderBackend).Assembly.Location,
            EngineAssemblySha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(Dx12RenderBackend).Assembly.Location))),
            ShaderSha256 = ShaderHashes(), TextureAssets = assets, NativeGpu = true, SoftwareAdapterAllowed = false,
            Width = width, Height = height, Particles = particles, ClearToStormMeanAbsoluteRgbDifference = wideDifference,
            WeatherQuality = weatherSettings.Name, WeatherRenderScale = weatherSettings.Scale,
            WeatherQualityIndex = weatherSettings.Quality, WeatherLightSamples = weatherSettings.LightSamples,
            AnimatedMeanAbsoluteRgbDifference = motionDifference, Captures = reports, Warnings = logger.Warnings, DebugValidation = validation.Snapshot,
        }, JsonOptions));
        Console.WriteLine($"Native PMWeather showcase passed. Manifest: {reportPath}");
        return 0;
    }

    private static int RunDemo(RendererOptions options, NativeSurface surface, MutableRenderAssetCatalog catalog,
        CaptureLogger logger, RenderGpuDebugValidation validation, bool particles, string qualityName, LiveStormController storms)
    {
        surface.EnableFlyCameraControls();
        Console.WriteLine("Fly camera: click to look, WASD to fly, Space/E up, Ctrl/Q down, Shift boost, wheel speed, Tab presets. Escape releases the mouse; F11 or Alt+Enter toggles fullscreen.");
        Console.WriteLine("Storms: 1–6 select strength, R restarts, P pauses, [ / ] change playback speed, G advances to the next lifecycle phase. Default playback is20x; each completed storm clears for3seconds before a new seed.");
        var host = new DedicatedRenderBackendHost("PMWeather DX12 Render Thread", maximumQueuedFrames: 1);
        try
        {
            // DXGI may send synchronous window messages from its render thread.
            // Keep the HWND's owning thread pumping while initialization/disposal waits.
            var renderSurface = new DemoRenderSurface(surface.WindowHandle, surface.Width, surface.Height);
            DemoThreadPump.Wait(Task.Run(() => host.Reconfigure(() =>
            {
                var backend = new Dx12RenderBackend(options, renderSurface, catalog, logger);
                return new DemoRenderBackend(backend, renderSurface,
                    () => validation.CollectErrors(backend, "demo initialization"),
                    () => validation.CollectErrors(backend, "demo shutdown", waitForCompletion: validation.Enabled));
            })), surface);
            var flyCamera = new FlyCamera();
            var localWeather = new UniStormSpatialWeatherController(UniStormWeatherCatalog.LoadDefault(), 13,
                new UniStormOptions { AutomaticWeatherEnabled = false, AdvanceInternalClock = false, TransitionSeconds = 2 });
            var stopwatch = Stopwatch.StartNew();
            double previousSeconds = 0, lastStatusSeconds = 0, nextTitleUpdate = 0, nextConsoleUpdate = 1;
            double longestInputIntervalMilliseconds = 0;
            long number = 0, lastCompletedCount = 0;
            bool firstFrameReported = false;
            while (!surface.IsCloseRequested)
            {
                surface.PumpMessages();
                if (surface.IsCloseRequested) break;
                host.ThrowIfFaulted();
                double seconds = stopwatch.Elapsed.TotalSeconds;
                double elapsedSeconds = seconds - previousSeconds;
                float deltaSeconds = (float)elapsedSeconds;
                previousSeconds = seconds;
                longestInputIntervalMilliseconds = Math.Max(longestInputIntervalMilliseconds, deltaSeconds * 1000);
                while (surface.TryGetStormKey(out int key)) storms.HandleKey(key);
                storms.Update(elapsedSeconds);
                if (surface.IsMinimized || surface.Width == 0 || surface.Height == 0)
                {
                    surface.WaitForMessages(16);
                    continue;
                }
                surface.UpdateFlyCamera(flyCamera, deltaSeconds);
                var observer = flyCamera.ToCameraState().Position;
                var environment = localWeather.Update(new(deltaSeconds, (float)seconds, 1, 40, 15, 22, observer), storms.Simulation.Current);
                host.SubmitLatest(CreateLiveFrame(number++, storms.VisualSeconds, environment,
                    surface.Width, surface.Height, particles, surface.CloseCamera, storms.HistoryVersion, flyCamera.ToCameraState()));
                if (seconds >= nextTitleUpdate)
                {
                    var progress = host.GetProgressSnapshot();
                    double fps = (progress.CompletedFrameCount - lastCompletedCount) / Math.Max(0.001, seconds - lastStatusSeconds);
                    string gpu = host.LastGpuTiming is { } timing ? $"{timing.TotalMilliseconds:0.0} ms" : "warming up";
                    string status = $"{qualityName} | {fps:0.0} FPS | GPU {gpu} | render {progress.LastRenderDurationMilliseconds:0.0} ms";
                    string localStatus = $"Local {localWeather.LocalWeatherName} {localWeather.StormInfluence:P0}";
                    surface.UpdateFlyCameraTitle(flyCamera, storms.Status + " | " + localStatus + " | " + status);
                    if (!firstFrameReported && progress.CompletedFrameCount > 0)
                    {
                        Console.WriteLine("Demo first native frame presented.");
                        firstFrameReported = true;
                    }
                    if (seconds >= nextConsoleUpdate)
                    {
                        Console.WriteLine($"Demo: {storms.Status}; {localStatus}; simulation={storms.Simulation.SimulationSeconds:0.00}s; {status}; completed={progress.CompletedFrameCount}, replaced pending snapshots={progress.SupersededFrameCount}, longest UI poll={longestInputIntervalMilliseconds:0.0} ms.");
                        longestInputIntervalMilliseconds = 0;
                        nextConsoleUpdate = seconds + 1;
                    }
                    lastCompletedCount = progress.CompletedFrameCount;
                    lastStatusSeconds = seconds;
                    nextTitleUpdate = seconds + 0.5;
                }
                // Input and window messages stay independent of GPU fences.
                surface.WaitForMessages(8);
            }
            Console.WriteLine("Demo close requested; releasing DX12 resources before destroying the native window.");
        }
        finally
        {
            surface.ReleaseMouseLook();
            DemoThreadPump.Wait(Task.Run(host.Dispose), surface);
        }
        host.ThrowIfFaulted();
        return 0;
    }

    private static async Task<int> VerifyWindowModesAsync(Dx12RenderBackend backend, NativeSurface surface,
        RenderGpuDebugValidation validation, string output, string staging)
    {
        int originalWidth = surface.Width, originalHeight = surface.Height;
        long number = 0;
        var results = new List<object>();
        foreach (string mode in new[] { "windowed", "fullscreen", "restored" })
        {
            if (mode != "windowed") surface.ToggleFullscreen();
            surface.PumpMessages();
            if (surface.IsCloseRequested || surface.IsMinimized) throw new InvalidOperationException("Window verification was interrupted.");
            if (surface.IsFullscreen != (mode == "fullscreen")) throw new InvalidOperationException("Fullscreen state did not change.");
            if (mode == "restored" && (surface.Width != originalWidth || surface.Height != originalHeight))
                throw new InvalidOperationException("Fullscreen did not restore the original client size.");
            for (int warmup = 0; warmup < 8; warmup++)
                await backend.RenderAsync(CreateFrame(new(mode, number++, 0, 0, 0, 0, 0, 0, false, false),
                    surface.Width, surface.Height, false), CancellationToken.None);
            ExportCompleted.SetValue(backend, false);
            await backend.RenderAsync(CreateFrame(new(mode, number++, 0, 0, 0, 0, 0, 0, false, false),
                surface.Width, surface.Height, false), CancellationToken.None);
            validation.CollectErrors(backend, mode, waitForCompletion: true);
            if (!(bool)ExportCompleted.GetValue(backend)! || !backend.LastFrameStatistics.RequiredResourcesResident)
                throw new InvalidOperationException("Resized renderer did not complete a resident native capture.");
            string path = Path.Combine(output, mode + ".png");
            File.Copy(staging, path, overwrite: true);
            using var image = Image.Load<Rgba32>(path);
            if (image.Width != surface.Width || image.Height != surface.Height)
                throw new InvalidOperationException("Native render output does not match the resized client area.");
            results.Add(new { Mode = mode, surface.IsFullscreen, Width = image.Width, Height = image.Height,
                Path = path, Statistics = backend.LastFrameStatistics });
            Console.WriteLine($"Window verification: {mode}, native output {image.Width}x{image.Height}.");
        }
        File.WriteAllText(Path.Combine(output, "window-modes.json"), JsonSerializer.Serialize(results, JsonOptions));
        return 0;
    }

    private static async Task<int> VerifyFlyCameraAsync(Dx12RenderBackend backend, NativeSurface surface,
        RenderGpuDebugValidation validation, string output, string staging)
    {
        surface.EnableFlyCameraControls();
        var camera = new FlyCamera();
        object inputChecks = FlyCameraProbe.Verify(surface, camera);
        camera.SetPreset(close: true);
        var results = new List<object>();
        var images = new List<byte[]>();
        long number = 0;
        foreach (string view in new[] { "initial", "moved", "turned" })
        {
            if (view == "moved") camera.Move(0.25f, 1, 1, 1, boost: true, slow: false);
            if (view == "turned") camera.Look(160, -60);
            for (int warmup = 0; warmup < 12; warmup++)
            {
                surface.PumpMessages();
                if (surface.IsCloseRequested) throw new InvalidOperationException("Fly camera verification was interrupted.");
                await backend.RenderAsync(CreateFrame(new(view, number++, 40, 3, 76, 150, 230, 0.08f, true, true),
                    surface.Width, surface.Height, true, camera.ToCameraState()), CancellationToken.None);
            }
            ExportCompleted.SetValue(backend, false);
            await backend.RenderAsync(CreateFrame(new(view, number++, 40, 3, 76, 150, 230, 0.08f, true, true),
                surface.Width, surface.Height, true, camera.ToCameraState()), CancellationToken.None);
            validation.CollectErrors(backend, view, waitForCompletion: true);
            if (!(bool)ExportCompleted.GetValue(backend)! || !backend.LastFrameStatistics.RequiredResourcesResident)
                throw new InvalidOperationException("Fly camera did not complete a resident native frame.");
            string path = Path.Combine(output, view + ".png");
            File.Copy(staging, path, overwrite: true);
            using var image = Image.Load<Rgba32>(path);
            if (image.Width != surface.Width || image.Height != surface.Height) throw new InvalidDataException("Fly camera dimensions mismatch.");
            var pixels = new byte[image.Width * image.Height * 4];
            image.CopyPixelDataTo(pixels);
            if (MeanLuminance(pixels) < 0.005 || LuminanceVariation(pixels, MeanLuminance(pixels)) < 0.0001)
                throw new InvalidDataException("Fly camera native frame is blank.");
            images.Add(pixels);
            results.Add(new { View = view, Path = path, Camera = camera.ToCameraState(), Statistics = backend.LastFrameStatistics });
        }
        double movedDifference = PixelDifference(images[0], images[1]);
        double turnedDifference = PixelDifference(images[1], images[2]);
        if (movedDifference < 0.003 || turnedDifference < 0.003)
            throw new InvalidDataException("Fly camera movement did not visibly change the native render.");
        FlyCameraProbe.VerifyCapturedClose(surface);
        File.WriteAllText(Path.Combine(output, "fly-camera.json"), JsonSerializer.Serialize(new
        {
            Input = inputChecks, NativeViews = results, MovedMeanAbsoluteRgbDifference = movedDifference,
            TurnedMeanAbsoluteRgbDifference = turnedDifference, CapturedCloseReleasedCursor = true,
        }, JsonOptions));
        Console.WriteLine($"Fly camera verification passed: movement image difference {movedDifference:0.0000}, look {turnedDifference:0.0000}.");
        return 0;
    }

    private static object ShaderHashes() => new
    {
        Sky = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(BuiltInShaderLibrary.SkyGradient.PixelShaderSource))),
        Vortex = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(BuiltInShaderLibrary.WeatherVortex.PixelShaderSource))),
    };

    private static object Hardware(Dx12RenderBackend backend)
    {
        var adapter = typeof(Dx12RenderBackend).GetField("adapter", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(backend) as IDXGIAdapter1
            ?? throw new InvalidOperationException("Unable to record selected native DX12 adapter identity.");
        var description = adapter.Description1;
        return new { Name = description.Description.Trim(), description.VendorId, description.DeviceId, description.SubsystemId,
            description.Revision, DedicatedVideoMemoryBytes = (ulong)description.DedicatedVideoMemory, Flags = description.Flags.ToString() };
    }

    private static MutableRenderAssetCatalog CreateCatalog()
    {
        var catalog = new MutableRenderAssetCatalog(new BuiltInRenderAssetCatalog());
        catalog.UpsertMaterial(new() { Handle = new("material", "showcase/ground"), ShaderProgramId = BuiltInShaderLibrary.PbrLit.Id,
            Tint = new(0.19f, 0.27f, 0.13f, 1), RoughnessStrength = 0.98f, CullMode = RenderCullMode.None });
        catalog.UpsertMaterial(new() { Handle = new("material", "showcase/building"), ShaderProgramId = BuiltInShaderLibrary.PbrLit.Id,
            Tint = new(0.57f, 0.46f, 0.34f, 1), RoughnessStrength = 0.8f });
        catalog.UpsertMaterial(new() { Handle = new("material", "showcase/masked-terrain"), ShaderProgramId = BuiltInShaderLibrary.TerrainTextureArray.Id,
            AlbedoTexture = new("texture", "engine/terrain/fallback-array"), Tint = new(1, 1, 1, 1),
            RoughnessStrength = 0.98f, OpacityMode = RenderOpacityMode.Masked, AlphaCutoff = 0.5f });
        foreach (var name in new[] { "rain", "mist", "smoke_0", "smoke_1" })
            catalog.UpsertMaterial(new() { Handle = new("material", "showcase/" + name), ShaderProgramId = BuiltInShaderLibrary.ParticleInstanced.Id,
                AlbedoTexture = new("texture", "pmweather/" + name), Tint = new(1, 1, 1, 1), CullMode = RenderCullMode.None,
                DepthEnabled = true, OpacityMode = RenderOpacityMode.Translucent, AlphaCutoff = 0.002f });
        return catalog;
    }

    private static RenderFrame CreateFrame(CaptureState state, int width, int height, bool particles, CameraState? cameraOverride = null)
    {
        var items = new List<RenderItem>
        {
            new("showcase/ground", new("mesh", "engine/ground-quad"), null, new(0, -1, 1000), new(100000, 1, 100000), 0, true),
        };
        for (int index = 0; index < 9; index++)
            items.Add(new("showcase/building", new("mesh", "engine/cube"), null,
                new(-850 + index * 220, 18 + index % 3 * 7, 600 + index % 2 * 280), new(55, 36 + index % 3 * 14, 70), 8 * index, true));
        // Tall nearby geometry provides an explicit depth occlusion reference.
        items.Add(new("showcase/building", new("mesh", "engine/cube"), null, new(-460, 80, 450), new(8, 160, 8), 0, true));
        if (state.DepthCheck)
            items.Add(new(state.MaskedDepth ? "showcase/masked-terrain" : "showcase/building", new("mesh", "engine/cube"), null,
                new(-175, 130, 650), new(120, 260, 70), 0, true));
        Vector3 position = state.Close ? new(-300, 85, 100) : new(-6000, 1100, -7800);
        Vector3 target = state.Close ? new(0, 250, 1000) : new(1500, 1500, 0);
        if (state.Kind == RenderStormKind.FireWhirl)
        {
            position = new(-110, 36, 740);
            target = new(0, 65, 1000);
        }
        Vector3 forward = Vector3.Normalize(target - position);
        var camera = cameraOverride ?? new CameraState(new(position.X, position.Y, position.Z), new(forward.X, forward.Y, forward.Z), 58, 0.5f, 16000);
        float intensity = state.Storm ? 0.9f : 0;
        var sky = new SkyGradientState(new(0.26f, 0.39f, 0.54f, 1), new(0.62f, 0.64f, 0.65f, 1), new(0.56f, 0.60f, 0.64f, 1),
            new(0.55f, 0.57f, 0.58f, 1), new(1, 0.9f, 0.74f, 1), new(-0.45f, 0.52f, -0.73f), 1.25f, 0.012f, 32, 0.000012f,
            state.Storm ? 0.25f : 0.02f, state.Storm ? 0.3f : 0.04f, state.Time * 0.02f, new(0.75f, 0.77f, 0.78f, 1),
            FogStartDistance: 4000, CloudHeightMeters: 760, CloudThicknessMeters: 2500, VolumetricCloudDensity: state.Storm ? 0.22f : 0,
            CloudTurbulence: 0.65f, CloudShadowStrength: 0.24f, StormAnvilAmount: intensity,
            TornadoFunnelAmount: state.Wind / 150f, TornadoDebrisAmount: state.Wind / 180f, TornadoVortexAmount: state.Wind / 150f,
            HorizonHazeStrength: 0.08f, StormFogAmount: 0.05f);
        if (state.Sunset)
            sky = sky with { SunDirection = new(0.2f, 0.12f, 0.973f), SunColor = new(1, 0.57f, 0.32f, 1),
                SunIntensity = 1.8f, AmbientColor = new(0.42f, 0.35f, 0.39f, 1), CloudTint = new(0.68f, 0.65f, 0.68f, 1) };
        if (state.Night)
            sky = sky with { SunDirection = new(-0.2f, -0.45f, -0.87f), SunIntensity = 0.015f,
                AmbientColor = new(0.1f, 0.12f, 0.2f, 1), MoonDirection = new(0.2f, 0.4f, 0.89f),
                MoonColor = new(0.78f, 0.84f, 1, 1), MoonIntensity = 0.8f, MoonDiskSize = 0.085f, MoonPhaseIndex = state.MoonPhase,
                FogColor = new(0.025f, 0.035f, 0.07f, 1),
                StarIntensity = 1, CelestialTextureStrength = 1 };
        if (state.Kind == RenderStormKind.FireWhirl)
            sky = sky with { CloudCoverage = 0.02f, CloudDensity = 0, VolumetricCloudDensity = 0, StormFogAmount = 0 };
        var tornado = new RenderTornadoState("showcase-tornado", "showcase-supercell", default, new(0, 0, 1000), new(12, 0, 4),
            Math.Max(15, state.Width * 0.42f), Math.Max(25, state.Width * 0.9f), state.Kind == RenderStormKind.FireWhirl ? 120 : 760, Math.Min(1, state.Wind / 100f), 1, 0.9f,
            WindSpeed: state.Wind, TouchdownSpeed: 90, WidthMeters: state.Width, TornadoShape: 8, SpinRadians: state.Time * 0.8f,
            Occlusion: state.Occlusion, IsDying: state.Occlusion > 0.6f, Stage: state.Stage, Energy: state.Energy,
            TouchdownAmount: Math.Min(1, state.Wind / 90f), FunnelLoweringAmount: 1, DebrisRadiusMeters: state.Width,
            WindfieldRadiusMeters: state.Width * 3f, RainWrappedAmount: state.Occlusion * 0.5f);
        var storm = new RenderStormCellState("showcase-supercell", new(0, 0, 1000), new(12, 0, 4), 1000, 220, 1700, 760, 3260,
            0.9f, 0, 0.8f, 0.25f, 0.7f, state.Wind > 40 ? [tornado] : [], Kind: state.Kind,
            Stage: state.Stage, Energy: state.Energy, MaxStage: 3, MaxProgress: 100, WindSpeed: state.Wind, MaxWindSpeed: 180,
            WidthMeters: state.Width, MaxWidthMeters: 280, TouchdownSpeed: 90, Occlusion: state.Occlusion,
            IsDying: state.Occlusion > 0.6f, TornadoShape: 8, SpinRadians: state.Time * 0.8f,
            WallCloudAmount: 1, RainWrapAmount: state.Occlusion * 0.5f, TailCloudAmount: 0.7f);
        var weather = new RenderWeatherState("PMWeather showcase", state.Storm ? "Tornado" : "Clear", "Rain", intensity, 0.25f,
            0, 1, state.Wind / 180f, 0.65f, state.Storm ? 0.7f : 0, 0.3f, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
            VolumetricCloudAmount: intensity, CloudShadowAmount: 0.3f, StormAnvilAmount: intensity,
            TornadoFunnelAmount: state.Wind / 150f, TornadoDebrisParticleAlpha: state.Wind / 180f, TornadoVortexAmount: state.Wind / 150f,
            Storms: state.Storm ? [storm] : []);
        return new(state.Frame, DateTimeOffset.UnixEpoch.AddSeconds(state.Time), new(width, height), camera, new(0.45f, 0.55f, 0.65f, 1), sky,
            items, [], Weather: weather, Particles: particles && state.Storm ? CreateParticles(state.Time, state.Close) : null,
            PostProcessSettings: new(RenderToneMapper.Filmic, Exposure: 1.15f, BloomIntensity: 0.04f, AmbientOcclusionIntensity: 0.35f,
                AntiAliasing: RenderAntiAliasingMode.Taa), QualityHints: new(ShadowCascadeCount: 2, PostProcessQuality: 2),
            TemporalHistoryVersion: state.Close ? 2 : 1, AnimationTimeSeconds: state.Time);
    }

    private static readonly DefaultRenderFrameBuilder LiveFrameBuilder = new();

    internal static RenderFrame CreateLiveFrame(long number, float visualSeconds, EnvironmentSceneState environment,
        int width, int height, bool particles, bool close, long historyVersion, CameraState? cameraOverride = null)
    {
        var baseline = CreateFrame(new("live", number, visualSeconds, 0, 0, 0, 0, 0, close, false), width, height, false, cameraOverride);
        var mapped = LiveFrameBuilder.Build(number, baseline.UtcTimestamp, baseline.Viewport,
            new RenderScene { Camera = baseline.Camera, Environment = environment, AnimationTimeSeconds = visualSeconds });
        return baseline with { Weather = mapped.Weather, Sky = mapped.Sky, ClearColor = environment.ClearColor,
            Particles = particles ? CreateLiveParticles(visualSeconds, mapped.Weather!, baseline.Camera.Position, mapped.Sky) : null,
            TemporalHistoryVersion = historyVersion };
    }

    private static RenderParticleScene? CreateLiveParticles(float time, RenderWeatherState weather, Vector3f observer, SkyGradientState? sky)
    {
        var storm = weather.ActiveStorms.FirstOrDefault();
        if (storm is null) return null;
        var batches = new List<RenderParticleInstanceBatch>();
        if (weather.RainParticleAlpha > 0.002f)
        {
            var instances = new List<RenderParticleInstance>();
            for (int i = 0; i < 96; i++)
            {
                float angle = i * 2.399963f + time * 0.18f;
                float radius = 12 + i % 7 * 8;
                float py = observer.Y - 20 + ((i * 24 - time * 90) % 100 + 100) % 100;
                instances.Add(new(new(observer.X + MathF.Cos(angle) * radius, py, observer.Z + MathF.Sin(angle) * radius),
                    new(0.16f, 6, 0.16f), default, new(0.65f, 0.68f, 0.72f, weather.RainParticleAlpha * 0.5f), new(0, -25, 0), 0.3f, 1));
            }
            batches.Add(new("live-rain", new("mesh", "engine/particle-quad"), new("material", "showcase/rain"),
                RenderParticleFacingMode.Camera, RenderParticleBlendMode.Alpha, true, 12, instances));
        }
        foreach (var active in weather.ActiveStorms)
            if (StormChaseCareer.HorizontalDistance(active.Position, observer) <= 8000)
                batches.AddRange(TornadoDebrisParticles.CreateBatches(time, active, sky,
                    weather.ActiveStorms.Count == 1 ? null : active.Id));
        return batches.Count == 0 ? null : new(batches.Count, batches.Sum(batch => batch.Instances.Count), [], batches);
    }

    internal static RenderParticleScene? CreateChaseDebris(float time, RenderWeatherState weather, Vector3f observer, SkyGradientState? sky)
        => CreateLiveParticles(time, weather, observer, sky);

    private static RenderParticleScene CreateParticles(float time, bool close)
    {
        var batches = new List<RenderParticleInstanceBatch>();
        foreach (string name in new[] { "rain", "mist", "smoke_0", "smoke_1" })
        {
            var instances = new List<RenderParticleInstance>();
            int count = name == "rain" ? 48 : 20;
            for (int i = 0; i < count; i++)
            {
                float angle = i * 2.399963f + time * 0.18f;
                float radius = 90 + i % 7 * 35;
                float px = MathF.Cos(angle) * radius, pz = 1000 + MathF.Sin(angle) * radius;
                float py = name == "rain" ? 70 + i % 8 * 24 : 12 + i % 5 * 10;
                float size = name == "rain" ? 2.3f : 55f;
                instances.Add(new(new(px, py, pz), new(size, name == "rain" ? 30 : size, size), default,
                    new(0.65f, 0.68f, 0.72f, name == "rain" ? 0.25f : 0.055f), new(0, -25, 0), 0.3f, 1));
            }
            batches.Add(new("showcase-" + name, new("mesh", "engine/particle-quad"), new("material", "showcase/" + name),
                RenderParticleFacingMode.Camera, RenderParticleBlendMode.Alpha, true, 12, instances));
        }
        return new(4, batches.Sum(batch => batch.Instances.Count), [], batches);
    }

    private static string Value(string[] args, string name, string fallback)
    {
        int index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : fallback;
    }
    private static double MeanLuminance(byte[] rgba) => Enumerable.Range(0, rgba.Length / 4).Average(i =>
        (rgba[i * 4] * 0.2126 + rgba[i * 4 + 1] * 0.7152 + rgba[i * 4 + 2] * 0.0722) / 255);
    private static double LuminanceVariation(byte[] rgba, double mean) => Enumerable.Range(0, rgba.Length / 4).Average(i =>
        Math.Pow((rgba[i * 4] * 0.2126 + rgba[i * 4 + 1] * 0.7152 + rgba[i * 4 + 2] * 0.0722) / 255 - mean, 2));
    private static double PixelDifference(byte[] left, byte[] right)
    {
        long difference = 0;
        for (int i = 0; i < left.Length; i++) if (i % 4 != 3) difference += Math.Abs(left[i] - right[i]);
        return difference / (left.Length / 4.0 * 3 * 255);
    }
    private sealed record CaptureState(string Name, long Frame, float Time, int Stage, int Energy, int Wind, float Width, float Occlusion, bool Close, bool Storm, bool DepthCheck = false,
        RenderStormKind Kind = RenderStormKind.Supercell, bool Sunset = false, bool Night = false, int MoonPhase = 1, bool MaskedDepth = false);

    private sealed class CaptureLogger : IRuntimeLogger
    {
        public List<string> Warnings { get; } = [];
        public void Info(string category, string message) => Console.WriteLine($"[{category}] {message}");
        public void Warn(string category, string message) { Warnings.Add($"[{category}] {message}"); Console.WriteLine(Warnings[^1]); }
        public void Error(string category, string message, Exception? exception = null) => throw new InvalidOperationException($"[{category}] {message}", exception);
    }
}

internal sealed class NativeSurface : IRenderSurfaceHost, IDisposable
{
    private const uint OverlappedWindowStyle = 0x00CF0000;
    private const uint VisibleStyle = 0x10000000;
    private const uint PopupStyle = 0x80000000;
    private bool disposed, closeRequested;
    private readonly string windowClassName = "MMOI.PmWeather.Showcase." + Guid.NewGuid().ToString("N");
    private readonly WindowProcedure windowProcedure;
    private readonly nint module;
    private nint windowedStyle, windowedExtendedStyle;
    private WindowPlacement windowedPlacement;
    private bool flyControlsEnabled, cursorHidden, cameraPresetPending, absoluteMouse;
    private nint rawInputBuffer;
    private Vector2 mouseDelta;
    private float wheelNotches;
    private readonly Queue<int> stormKeys = new();
    private readonly Queue<WorldClick> worldClicks = new();
    private readonly Queue<GuiPointerState> guiPointerEvents = new();
    private readonly Queue<GuiKeyboardEvent> guiKeyboardEvents = new();
    private GuiPointerState guiPointer = GuiPointerState.Missing;
    private bool guiInputEnabled, guiPrimaryDown;
    private NativePoint mouseRestorePosition;
    public NativeSurface(int width, int height, bool visible)
    {
        windowProcedure = HandleWindowMessage; // Root the callback for the complete HWND/class lifetime.
        module = GetModuleHandleW(null);
        var windowClass = new WindowClass
        {
            Size = (uint)Marshal.SizeOf<WindowClass>(),
            Style = 0x0003,
            Procedure = Marshal.GetFunctionPointerForDelegate(windowProcedure),
            Instance = module,
            Cursor = LoadCursorW(IntPtr.Zero, new IntPtr(32512)),
            ClassName = windowClassName,
        };
        if (RegisterClassExW(ref windowClass) == 0)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            if (visible) SetProcessDpiAwarenessContext(new IntPtr(-4)); // Per-monitor V2; an existing manifest may already set it.
            uint style = visible ? VisibleStyle | OverlappedWindowStyle : PopupStyle;
            var rectangle = new NativeRectangle { Right = width, Bottom = height };
            if (visible) Check(AdjustWindowRectEx(ref rectangle, style, false, 0));
            WindowHandle = CreateWindowExW(0, windowClassName, "MMOI DX12 • PMWeather storm showcase", style, 60, 60,
                rectangle.Right - rectangle.Left, rectangle.Bottom - rectangle.Top,
                IntPtr.Zero, IntPtr.Zero, module, IntPtr.Zero);
            if (WindowHandle == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            if (visible) ShowWindow(WindowHandle, 5);
            UpdateClientSize();
            CloseCamera = true;
        }
        catch
        {
            if (WindowHandle != IntPtr.Zero && IsWindow(WindowHandle)) DestroyWindow(WindowHandle);
            UnregisterClassW(windowClassName, module);
            GC.KeepAlive(windowProcedure);
            throw;
        }
    }
    public IntPtr WindowHandle { get; }
    public bool IsCreated => !disposed && WindowHandle != IntPtr.Zero;
    public bool IsCloseRequested => closeRequested || !IsWindow(WindowHandle);
    public bool CloseCamera { get; private set; }
    public bool IsFullscreen { get; private set; }
    public long WindowLayoutVersion { get; private set; }
    public bool IsMinimized => IsIconic(WindowHandle);
    public int Width { get; private set; }
    public int Height { get; private set; }
    public bool MouseLookCaptured { get; private set; }
    public bool ChaseControlsEnabled { get; set; }
    public bool MenuControlsEnabled { get; set; }
    public bool GameControlsEnabled { get; set; }
    public bool GuiInputEnabled
    {
        get => guiInputEnabled;
        set
        {
            if (guiInputEnabled == value) return;
            ReleaseMouseLook();
            if (GetCapture() == WindowHandle) ReleaseCapture();
            guiInputEnabled = value; guiPrimaryDown = false;
            guiPointerEvents.Clear(); guiPointer = GuiPointerState.Missing;
            guiKeyboardEvents.Clear();
            WindowLayoutVersion++;
        }
    }
    public bool IsInputActive => IsCreated && !IsCloseRequested && !IsMinimized && GetForegroundWindow() == WindowHandle;
    public bool TryGetStormKey(out int key) => stormKeys.TryDequeue(out key);
    /// <summary>Mouse buttons pressed while mouse look is captured, for crosshair-aimed world interaction (building, furniture).</summary>
    public IReadOnlyList<WorldClick> DrainWorldClicks() { var clicks = worldClicks.ToArray(); worldClicks.Clear(); return clicks; }
    public bool TryGetGuiKeyboardEvent(out GuiKeyboardEvent input) => guiKeyboardEvents.TryDequeue(out input);
    public void SetTitle(string title) => SetWindowTextW(WindowHandle, title);
    public void RequestClose() => closeRequested = true;
    public GuiInputState ReadGuiInput()
    {
        if (!GuiInputEnabled || !IsInputActive)
        {
            guiPointerEvents.Clear(); guiPrimaryDown = false; guiPointer = GuiPointerState.Missing;
            return GuiInputState.Empty;
        }
        if (guiPointerEvents.TryDequeue(out var input))
        {
            guiPointer = input with { WasPrimaryButtonDown = input.IsPrimaryButtonDown };
            return new(input);
        }
        return new(guiPointer);
    }

    public void EnableFlyCameraControls()
    {
        if (flyControlsEnabled) return;
        rawInputBuffer = Marshal.AllocHGlobal(Marshal.SizeOf<RawMousePacket>());
        var device = new RawInputDevice { UsagePage = 1, Usage = 2, Target = WindowHandle };
        Check(RegisterRawInputDevices([device], 1, (uint)Marshal.SizeOf<RawInputDevice>()));
        flyControlsEnabled = true;
    }

    public void CaptureMouseLook()
    {
        if (!flyControlsEnabled || MouseLookCaptured || GuiInputEnabled || !IsInputActive) return;
        GetCursorPos(out mouseRestorePosition);
        MouseLookCaptured = true;
        mouseDelta = Vector2.Zero;
        absoluteMouse = false;
        SetCapture(WindowHandle);
        if (GetCapture() != WindowHandle || !UpdateMouseClip()) { ReleaseMouseLook(); return; }
        ShowCursor(false);
        cursorHidden = true;
        SetCursor(IntPtr.Zero);
    }

    public void ReleaseMouseLook()
    {
        bool wasCaptured = MouseLookCaptured;
        MouseLookCaptured = false;
        mouseDelta = Vector2.Zero;
        wheelNotches = 0;
        if (wasCaptured)
        {
            UnclipCursor(IntPtr.Zero);
            if (GetCapture() == WindowHandle) ReleaseCapture();
            SetCursorPos(mouseRestorePosition.X, mouseRestorePosition.Y);
        }
        if (cursorHidden)
        {
            ShowCursor(true);
            cursorHidden = false;
            SetCursor(LoadCursorW(IntPtr.Zero, new IntPtr(32512)));
        }
    }

    private bool UpdateMouseClip()
    {
        if (!GetClientRect(WindowHandle, out var rectangle)) return false;
        var topLeft = new NativePoint { X = rectangle.Left, Y = rectangle.Top };
        var bottomRight = new NativePoint { X = rectangle.Right, Y = rectangle.Bottom };
        if (!ClientToScreen(WindowHandle, ref topLeft) || !ClientToScreen(WindowHandle, ref bottomRight)) return false;
        rectangle = new() { Left = topLeft.X, Top = topLeft.Y, Right = bottomRight.X, Bottom = bottomRight.Y };
        return ClipCursor(ref rectangle);
    }

    private void CenterCursor()
    {
        if (!GetClientRect(WindowHandle, out var rectangle)) return;
        var center = new NativePoint { X = rectangle.Right / 2, Y = rectangle.Bottom / 2 };
        if (ClientToScreen(WindowHandle, ref center)) SetCursorPos(center.X, center.Y);
    }

    private void ReadRawMouse(nint input)
    {
        if (!MouseLookCaptured || !IsInputActive || rawInputBuffer == IntPtr.Zero) return;
        uint size = (uint)Marshal.SizeOf<RawMousePacket>();
        uint read = GetRawInputData(input, 0x10000003, rawInputBuffer, ref size, (uint)Marshal.SizeOf<RawInputHeader>());
        if (read != Marshal.SizeOf<RawMousePacket>()) return;
        var packet = Marshal.PtrToStructure<RawMousePacket>(rawInputBuffer);
        if (packet.Header.Type != 0) return;
        if ((packet.Mouse.Flags & 1) != 0)
        {
            // Remote/absolute pointing devices use a centered cursor fallback.
            if (!absoluteMouse) { absoluteMouse = true; mouseDelta = Vector2.Zero; CenterCursor(); }
        }
        else
        {
            if (absoluteMouse) { absoluteMouse = false; mouseDelta = Vector2.Zero; }
            mouseDelta += new Vector2(packet.Mouse.DeltaX, packet.Mouse.DeltaY);
        }
    }

    public void UpdateFlyCamera(FlyCamera camera, float deltaSeconds, bool move = true)
    {
        if (cameraPresetPending) { camera.SetPreset(CloseCamera); cameraPresetPending = false; }
        if (!IsInputActive)
        {
            ReleaseMouseLook();
            mouseDelta = Vector2.Zero;
            wheelNotches = 0;
            return;
        }
        if (MouseLookCaptured && absoluteMouse && GetCursorPos(out var cursor))
        {
            var center = new NativePoint { X = Width / 2, Y = Height / 2 };
            if (ClientToScreen(WindowHandle, ref center)) mouseDelta += new Vector2(cursor.X - center.X, cursor.Y - center.Y);
            CenterCursor();
        }
        if (MouseLookCaptured) camera.Look(mouseDelta.X, mouseDelta.Y);
        camera.AdjustSpeed(wheelNotches);
        mouseDelta = Vector2.Zero;
        wheelNotches = 0;
        static bool Held(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;
        float forward = (Held(0x57) ? 1 : 0) - (Held(0x53) ? 1 : 0);
        float strafe = (Held(0x44) ? 1 : 0) - (Held(0x41) ? 1 : 0);
        float vertical = (Held(0x20) || Held(0x45) ? 1 : 0) - (Held(0x11) || Held(0x51) ? 1 : 0);
        if (move) camera.Move(deltaSeconds, forward, strafe, vertical, Held(0x10), Held(0x12));
    }

    public (float Throttle, float Steering, bool Brake) DrivingInput()
    {
        if (!IsInputActive) return (0, 0, true);
        static bool Held(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;
        return ((Held(0x57) ? 1 : 0) - (Held(0x53) ? 1 : 0),
            (Held(0x44) ? 1 : 0) - (Held(0x41) ? 1 : 0), Held(0x20));
    }

    public (float Forward,float Strafe,bool Sprint,bool Brake,bool Jump) ChaseInput()
    {
        if(!IsInputActive||GuiInputEnabled)return(0,0,false,true,false);
        bool Down(int key)=>(GetAsyncKeyState(key)&0x8000)!=0;
        return((Down(0x57)?1:0)-(Down(0x53)?1:0),(Down(0x44)?1:0)-(Down(0x41)?1:0),Down(0x10),Down(0x20),Down(0x20));
    }

    public void UpdateFlyCameraTitle(FlyCamera camera, string? renderStatus = null) => SetWindowTextW(WindowHandle,
        $"MMOI DX12 • {(camera.Grounded ? "On foot" : "Fly camera")}{(renderStatus is null ? "" : " | " + renderStatus)} | {camera.SpeedMetersPerSecond:0} m/s | {(MouseLookCaptured ? "Esc: release mouse" : "Click: mouse look")} | WASD · Space/Ctrl · Shift · Wheel · Tab · F11");

    private nint HandleWindowMessage(nint window, uint message, nuint parameter, nint data)
    {
        // Owned overlays must follow movement, mode changes and focus restoration
        // immediately, including transitions that leave the client size unchanged.
        if (message is 0x0003 or 0x0005 or 0x0006 or 0x001C or 0x0047) WindowLayoutVersion++;
        // WM_CLOSE can be sent directly by DefWindowProc (X, system menu,
        // Alt+F4), so it cannot be handled by polling queued keyboard messages.
        if (message == 0x0010)
        {
            ReleaseMouseLook();
            closeRequested = true;
            ShowWindow(window, 0);
            // Keep the HWND alive until the renderer's using scope drains and
            // releases DX12. NativeSurface.Dispose then destroys the window.
            return IntPtr.Zero;
        }
        if (message == 0x0002)
        {
            ReleaseMouseLook();
            closeRequested = true;
            PostQuitMessage(0);
            return IntPtr.Zero;
        }
        if (message == 0x0008 || (message == 0x001C && parameter == 0) || (message == 0x0005 && parameter == 1) ||
            (message == 0x0215 && data != window)) ReleaseMouseLook();
        if (MenuControlsEnabled && GuiInputEnabled && message == 0x0102)
        {
            if (guiKeyboardEvents.Count < 256) guiKeyboardEvents.Enqueue(new(0, (char)parameter, false));
            return 0;
        }
        if (GuiInputEnabled && message is 0x0200 or 0x0201 or 0x0202)
        {
            int x = unchecked((short)(data.ToInt64() & 0xFFFF));
            int y = unchecked((short)((data.ToInt64() >> 16) & 0xFFFF));
            bool wasDown = guiPrimaryDown;
            if (message == 0x0201) { guiPrimaryDown = true; SetCapture(window); }
            if (message == 0x0202)
            {
                guiPrimaryDown = false;
                if (GetCapture() == window) ReleaseCapture();
            }
            var pointer = new GuiPointerState(true, x, y, guiPrimaryDown, wasDown);
            if (message != 0x0200) guiPointerEvents.Enqueue(pointer);
            else guiPointer = pointer with { WasPrimaryButtonDown = pointer.IsPrimaryButtonDown };
            return 0;
        }
        if (flyControlsEnabled)
        {
            if (message == 0x0201)
            {
                if (MouseLookCaptured) { if (worldClicks.Count < 32) worldClicks.Enqueue(WorldClick.PrimaryDown); }
                else CaptureMouseLook();
                return IntPtr.Zero;
            }
            if (MouseLookCaptured && message is 0x0202 or 0x0204)
            {
                if (worldClicks.Count < 32) worldClicks.Enqueue(message == 0x0202 ? WorldClick.PrimaryUp : WorldClick.SecondaryDown);
                return IntPtr.Zero;
            }
            if (message == 0x020A)
            {
                if (IsInputActive) wheelNotches += unchecked((short)((parameter >> 16) & 0xFFFF)) / 120f;
                return IntPtr.Zero;
            }
            if (message == 0x00FF) ReadRawMouse(data);
            if (message == 0x0020 && MouseLookCaptured) { SetCursor(IntPtr.Zero); return new IntPtr(1); }
        }
        if ((message == 0x0003 || message == 0x0005) && MouseLookCaptured && !UpdateMouseClip()) ReleaseMouseLook();
        return DefWindowProcW(window, message, parameter, data);
    }

    public void ToggleFullscreen()
    {
        if (!IsCreated || IsCloseRequested) return;
        bool resumeMouseLook = MouseLookCaptured;
        ReleaseMouseLook();
        if (!IsFullscreen)
        {
            windowedPlacement = new() { Length = (uint)Marshal.SizeOf<WindowPlacement>() };
            Check(GetWindowPlacement(WindowHandle, ref windowedPlacement));
            windowedStyle = GetWindowLongPtrW(WindowHandle, -16);
            windowedExtendedStyle = GetWindowLongPtrW(WindowHandle, -20);
            var monitor = new MonitorInformation { Size = (uint)Marshal.SizeOf<MonitorInformation>() };
            Check(GetMonitorInfoW(MonitorFromWindow(WindowHandle, 2), ref monitor));
            if (IsZoomed(WindowHandle)) ShowWindow(WindowHandle, 9);
            uint fullscreenStyle = (unchecked((uint)windowedStyle.ToInt64()) & ~(OverlappedWindowStyle | 0x01000000u | 0x20000000u)) | PopupStyle;
            SetStyle(-16, (nint)(long)fullscreenStyle);
            SetStyle(-20, (nint)(long)(unchecked((uint)windowedExtendedStyle.ToInt64()) & ~0x00000300u));
            var bounds = monitor.Monitor;
            Check(SetWindowPos(WindowHandle, IntPtr.Zero, bounds.Left, bounds.Top,
                bounds.Right - bounds.Left, bounds.Bottom - bounds.Top, 0x0004 | 0x0020 | 0x0040 | 0x0200));
            IsFullscreen = true;
        }
        else
        {
            SetStyle(-16, windowedStyle);
            SetStyle(-20, windowedExtendedStyle);
            Check(SetWindowPlacement(WindowHandle, ref windowedPlacement));
            Check(SetWindowPos(WindowHandle, IntPtr.Zero, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0004 | 0x0020 | 0x0200));
            IsFullscreen = false;
        }
        UpdateClientSize();
        if (resumeMouseLook) CaptureMouseLook();
        Console.WriteLine($"Window mode: {(IsFullscreen ? "borderless fullscreen" : "windowed")}, client {Width}x{Height}.");
    }

    private void SetStyle(int index, nint value)
    {
        Marshal.SetLastPInvokeError(0);
        if (SetWindowLongPtrW(WindowHandle, index, value) == IntPtr.Zero && Marshal.GetLastPInvokeError() != 0)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
    }

    private void UpdateClientSize()
    {
        if (!IsCreated || IsCloseRequested) return;
        Check(GetClientRect(WindowHandle, out var rectangle));
        Width = Math.Max(0, rectangle.Right - rectangle.Left);
        Height = Math.Max(0, rectangle.Bottom - rectangle.Top);
    }

    private static void Check(bool succeeded)
    {
        if (!succeeded) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }

    public void PumpMessages()
    {
        while (PeekMessageW(out var message, IntPtr.Zero, 0, 0, 1))
        {
            if (message.Id == 0x0012) closeRequested = true;
            bool keyDown = message.Id is 0x0100 or 0x0104;
            bool repeated = (message.LParam.ToInt64() & (1L << 30)) != 0;
            if (keyDown)
            {
                ulong key = message.WParam.ToUInt64();
                bool altEnter = message.Id == 0x0104 && key == 0x0D && (message.LParam.ToInt64() & (1L << 29)) != 0;
                if (key == 0x7A || altEnter)
                { if (!repeated) ToggleFullscreen(); continue; }
                if (MenuControlsEnabled && GuiInputEnabled && message.Id == 0x0100)
                {
                    // Navigation and text editing repeat normally. Holding Enter
                    // or Escape must not repeat purchases or menu transitions.
                    if ((!repeated || key is not (0x0D or 0x1B)) &&
                        (!repeated || !GameControlsEnabled || key is not (0x5A or 0x51)) && guiKeyboardEvents.Count < 256)
                        guiKeyboardEvents.Enqueue(new((int)key, '\0', (GetAsyncKeyState(0x10) & 0x8000) != 0,
                            (GetAsyncKeyState(0x11) & 0x8000) != 0));
                    // Text keys still pass through TranslateMessage to produce WM_CHAR.
                    if (key is 0x09 or 0x0D or 0x1B or 0x25 or 0x26 or 0x27 or 0x28 or 0x2E or 0x24 or 0x23) continue;
                    TranslateMessage(ref message); DispatchMessageW(ref message); continue;
                }
                if (repeated)
                {
                    // Movement uses held-key state; gameplay toggles are edge
                    // triggered. WM_CHAR translation remains available outside play.
                    TranslateMessage(ref message); DispatchMessageW(ref message); continue;
                }
                if (flyControlsEnabled && message.Id == 0x0100 && ((key is >= 0x31 and <= 0x36 or 0x52 or 0x50 or 0x47 or 0xDB or 0xDD)
                    || (ChaseControlsEnabled && key is 0x09 or 0x43 or 0x56 or 0x42 or 0x46 or 0x54 or 0x4D or 0x48 or 0x4C
                        or 0x45 or 0x55 or 0x4A or 0x4B or 0x58 or 0x4E or 0x4F or 0x5A or 0x51 or 0x26 or 0x28 or 0x0D
                        or 0x59 or 0x49 or 0x21 or 0x22)))
                {
                    stormKeys.Enqueue((int)key);
                    continue;
                }
                if (key == 0x1B)
                {
                    if(GameControlsEnabled&&!GuiInputEnabled)
                    {if(MouseLookCaptured)ReleaseMouseLook();stormKeys.Enqueue(0x1B);continue;}
                    if (GuiInputEnabled) { stormKeys.Enqueue(0x1B); continue; }
                    if (MouseLookCaptured) ReleaseMouseLook();
                    else if (IsFullscreen) ToggleFullscreen(); else closeRequested = true;
                    continue;
                }
                if (message.Id == 0x0100 && key == 0x09)
                {
                    CloseCamera = !CloseCamera;
                    cameraPresetPending = true;
                    continue;
                }
            }
            TranslateMessage(ref message); DispatchMessageW(ref message);
        }
        UpdateClientSize();
    }
    public void WaitForMessages(uint milliseconds) => MsgWaitForMultipleObjectsEx(0, IntPtr.Zero, milliseconds, 0x04FF, 0x0004);
    public void Dispose()
    {
        if (disposed) return;
        ReleaseMouseLook();
        if (flyControlsEnabled)
        {
            var device = new RawInputDevice { UsagePage = 1, Usage = 2, Flags = 1 };
            RegisterRawInputDevices([device], 1, (uint)Marshal.SizeOf<RawInputDevice>());
        }
        if (rawInputBuffer != IntPtr.Zero) { Marshal.FreeHGlobal(rawInputBuffer); rawInputBuffer = IntPtr.Zero; }
        disposed = true;
        if (IsWindow(WindowHandle)) Check(DestroyWindow(WindowHandle));
        Check(UnregisterClassW(windowClassName, module));
        GC.KeepAlive(windowProcedure);
        Console.WriteLine("Native demo window destroyed.");
    }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate nint WindowProcedure(nint window, uint message, nuint parameter, nint data);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct WindowClass
    {
        public uint Size; public uint Style; public nint Procedure; public int ExtraClassBytes; public int ExtraWindowBytes;
        public nint Instance; public nint Icon; public nint Cursor; public nint Background; public string? MenuName; public string ClassName; public nint SmallIcon;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Message { public IntPtr Hwnd; public uint Id; public UIntPtr WParam; public IntPtr LParam; public uint Time; public int X; public int Y; public uint Private; }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X; public int Y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRectangle { public int Left; public int Top; public int Right; public int Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct WindowPlacement { public uint Length; public uint Flags; public uint ShowCommand; public NativePoint MinimumPosition; public NativePoint MaximumPosition; public NativeRectangle NormalPosition; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInformation { public uint Size; public NativeRectangle Monitor; public NativeRectangle Work; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)] private struct RawInputDevice { public ushort UsagePage; public ushort Usage; public uint Flags; public nint Target; }
    [StructLayout(LayoutKind.Sequential)] private struct RawInputHeader { public uint Type; public uint Size; public nint Device; public nuint Parameter; }
    [StructLayout(LayoutKind.Explicit, Size = 24)] private struct RawMouse
    {
        [FieldOffset(0)] public ushort Flags;
        [FieldOffset(12)] public int DeltaX;
        [FieldOffset(16)] public int DeltaY;
    }
    [StructLayout(LayoutKind.Sequential)] private struct RawMousePacket { public RawInputHeader Header; public RawMouse Mouse; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateWindowExW(uint extendedStyle, string className, string name, uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr GetModuleHandleW(string? moduleName);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassExW(ref WindowClass windowClass);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool UnregisterClassW(string className, IntPtr instance);
    [DllImport("user32.dll")] private static extern IntPtr LoadCursorW(IntPtr instance, IntPtr cursorName);
    [DllImport("user32.dll")] private static extern nint DefWindowProcW(nint window, uint message, nuint parameter, nint data);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int exitCode);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool SetWindowTextW(IntPtr window, string title);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr SetCapture(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr GetCapture();
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern int ShowCursor(bool show);
    [DllImport("user32.dll")] private static extern IntPtr SetCursor(IntPtr cursor);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr window, ref NativePoint point);
    [DllImport("user32.dll")] private static extern bool ClipCursor(ref NativeRectangle rectangle);
    [DllImport("user32.dll", EntryPoint = "ClipCursor")] private static extern bool UnclipCursor(IntPtr rectangle);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterRawInputDevices(RawInputDevice[] devices, uint count, uint size);
    [DllImport("user32.dll")] private static extern uint GetRawInputData(nint input, uint command, nint buffer, ref uint size, uint headerSize);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr window);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetProcessDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool AdjustWindowRectEx(ref NativeRectangle rectangle, uint style, bool menu, uint extendedStyle);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetClientRect(IntPtr window, out NativeRectangle rectangle);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetMonitorInfoW(IntPtr monitor, ref MonitorInformation information);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetWindowPlacement(IntPtr window, ref WindowPlacement placement);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPlacement(IntPtr window, ref WindowPlacement placement);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr GetWindowLongPtrW(IntPtr window, int index);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowLongPtrW(IntPtr window, int index, IntPtr value);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool DestroyWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool PeekMessageW(out Message message, IntPtr window, uint minimum, uint maximum, uint remove);
    [DllImport("user32.dll")] private static extern uint MsgWaitForMultipleObjectsEx(uint count, IntPtr handles, uint milliseconds, uint wakeMask, uint flags);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessageW(ref Message message);
}
