using System.Diagnostics;
using System.Numerics;
using System.Reflection;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using Engine.GUI;
using Engine.Rendering.Assets;
using Engine.Rendering.Backend;
using Engine.Rendering.Configuration;
using Engine.UniStorm;
using Shared.Contracts.Rendering;
using Shared.Contracts.World;

internal static partial class Showcase
{
    [SupportedOSPlatform("windows")]
    private static int RunStormChaseDemo(RendererOptions options, NativeSurface surface, MutableRenderAssetCatalog catalog,
        CaptureLogger logger, RenderGpuDebugValidation validation, bool particles, string qualityName,
        LiveStormController storms, string output, string[] args)
    {
        surface.EnableFlyCameraControls();
        surface.ChaseControlsEnabled = true;
        Console.WriteLine("Loading storm-chase terrain, native PhysX and the clothed UMA avatar...");
        var loading = Task.Run(() => new StormChaseSession(catalog, logger, OptionalValue(args, "--avatar-catalog")));
        DemoThreadPump.Wait(loading, surface);
        using var session = loading.GetAwaiter().GetResult();
        if (surface.IsCloseRequested) return 0;
        bool campaignMode = !args.Contains("--sandbox", StringComparer.OrdinalIgnoreCase);
        var career = campaignMode ? new StormChaseCareer(session.World, OptionalValue(args, "--save-path")) : null;
        using var vehicles = career is null ? null : new StormChaseVehicles(session.World, session.Destruction, career);
        if (vehicles is not null) session.Destruction.PersistentPhysicsAnchor = () => vehicles.Position;
        var campaignArgs = career is not null && OptionalValue(args, "--campaign-seed") is null
            ? args.Concat(new[] { "--campaign-seed", career.Save.CampaignSeed.ToString(System.Globalization.CultureInfo.InvariantCulture) }).ToArray() : args;
        if (career is not null && OptionalValue(args, "--campaign-start-hour") is null)
            campaignArgs = campaignArgs.Concat(new[] { "--campaign-start-hour",
                career.Save.CampaignStartHour.ToString(System.Globalization.CultureInfo.InvariantCulture) }).ToArray();
        var campaign = career is null ? null : new StormChaseCampaignWeather(storms, campaignArgs, career.Save.CampaignSeconds);
        if (campaign is not null) career!.Save.CampaignSeed = campaign.Seed;
        if (campaign is not null) career!.Save.CampaignStartHour = campaign.StartHour;
        session.Career = career; session.Vehicles = vehicles;
        if (career is not null) career.DestroyedBuildings = () => session.Destruction.DestroyedBuildingIds;
        if (career is not null) career.VehiclePosition = () => vehicles!.Position;
        if (campaign is not null && career is not null)
        {
            session.CampaignDay = campaign.DayNumber; session.CampaignHour = campaign.Hour;
            session.EnableNpcs(campaign.Seed, campaign.DayNumber, campaign.Hour, career.SavePath + ".npc.gz");
        }
        using var hud = new StormChaseHud(surface.WindowHandle);
        var photos = new StormChasePhotos(output, options.FrameExportPath!, career is null ? null : career.QueuePhoto);
        var audioLoading = StormChaseAudio.CreateAsync(logger, args.Contains("--mute"));
        DemoThreadPump.Wait(audioLoading, surface);
        var audio = audioLoading.GetAwaiter().GetResult();
        audio.ConfigureWarningSites(session.World.Settlements.Select(s => s.Position with { Y = s.Position.Y + 15 }));
        var host = new DedicatedRenderBackendHost("MMOI Storm Chase DX12", maximumQueuedFrames: 1);
        StormChaseFramePacing.EnableHighResolutionTimer();
        using var profiler = StormChaseLiveProfiler.FromEnvironment();
        session.FrameTimingSink = profiler?.Prefixed("Game.");
        Console.WriteLine("MMOI Storm Chase: native Terrain + CrestWater + UniStorm + Avatar/Animation + PhysX + Particles + Audio + GUI + ECS + spline roads.");
        Console.WriteLine(campaignMode
            ? "E enter/exit; WASD drive; Space brake; R fuel; U shop, arrows select, Enter buy; B/J deploy/retrieve probe; F photograph; N regional map; X tow; K interceptor anchors. C walk/fly, V avatar, P pause, F11 fullscreen."
            : "C walk/fly; V third-person avatar; B deploy sensor; F save photograph; T afternoon/sunset/moonlight; L torch; M mute; H instruments. Original storm and fullscreen controls remain available.");
        try
        {
            var renderSurface = new DemoRenderSurface(surface.WindowHandle, surface.Width, surface.Height);
            DemoThreadPump.Wait(Task.Run(() => host.Reconfigure(() =>
            {
                var backend = new Dx12RenderBackend(options, renderSurface, catalog, logger);
                backend.FrameCpuTimingSink = profiler?.Prefixed("Render.");
                return new DemoRenderBackend(backend, renderSurface,
                    () => validation.CollectErrors(backend, "storm chase initialization"),
                    () => validation.CollectErrors(backend, "storm chase shutdown", waitForCompletion: validation.Enabled),
                    photos.BeforeFrame, photos.AfterFrame);
            })), surface);
            var camera = new FlyCamera();
            camera.SetPose(new(-300, session.World.HeightAt(-300, 20) + 3.5f, 20), new(160, 165, 1080));
            camera.SetSpeed(65);
            if (vehicles is not null)
            {
                camera.Grounded = true; camera.SetSpeed(6);
                camera.SetPose(new(vehicles.Position.X - 3, vehicles.Position.Y + 1.72f, vehicles.Position.Z),
                    new(vehicles.Position.X, vehicles.Position.Y + 1.5f, vehicles.Position.Z + 5));
            }
            // Enter a visible thunderstorm without skipping to a promised tornado.
            if (campaign is null && !args.Contains("--storm-from-birth"))
            {
                if (storms.VariedLifecycleEnabled)
                {
                    for (int tick = 0; tick < 100_000 && !storms.Simulation.IsComplete
                        && storms.Simulation.Current.ActiveStorms.FirstOrDefault()?.SmoothedStage < 2.15f; tick++)
                        storms.Simulation.Advance(.05);
                }
                else for (int i = 0; i < 4 && storms.Phase != "Forming"; i++) storms.AdvanceToNextPhase();
            }
            var local = new UniStormSpatialWeatherController(UniStormWeatherCatalog.LoadDefault(), 13,
                new UniStormOptions { AutomaticWeatherEnabled = false, AdvanceInternalClock = false, TransitionSeconds = 2 });
            var previousSimulation = storms.Simulation;
            long previousWorldReset = storms.WorldResetVersion;
            var clock = Stopwatch.StartNew();
            double previousSeconds = 0, nextUi = 0, lastUi = 0, nextLog = 1;
            long number = 0, completed = 0, history = 1000;
            long hudLayoutVersion = -1;
            GuiInputState previousGuiInput = GuiInputState.Empty;
            double fps = 0, gpuMs = 0;
            bool firstPresented = false;
            int viewPreset = 0;
            Vector3 previousPosition = camera.Position;
            RenderWeatherState? previousWeather = null;
            while (!surface.IsCloseRequested)
            {
                surface.PumpMessages();
                if (surface.IsCloseRequested) break;
                host.ThrowIfFaulted();
                double seconds = clock.Elapsed.TotalSeconds;
                float delta = (float)Math.Clamp(seconds - previousSeconds, 0, .25);
                previousSeconds = seconds;
                profiler?.BeginUiFrame();
                while (surface.TryGetStormKey(out int key))
                {
                    nextUi = 0;
                    switch (key)
                    {
                        case 0x45 when vehicles is not null: vehicles.Interact(camera, seconds); history++; break;
                        case 0x52 when vehicles is not null: vehicles.ToggleRefuel(seconds); break;
                        case 0x55 when career is not null:
                            career.ShopOpen = !career.ShopOpen;
                            surface.GuiInputEnabled = career.ShopOpen;
                            break;
                        case 0x1B when career?.ShopOpen == true:
                            career.ShopOpen = false; surface.GuiInputEnabled = false; break;
                        case 0x26 when career?.ShopOpen == true:
                            career.ShopSelection = (career.ShopSelection + StormChaseCareer.Offers.Length - 1) % StormChaseCareer.Offers.Length; break;
                        case 0x28 when career?.ShopOpen == true:
                            career.ShopSelection = (career.ShopSelection + 1) % StormChaseCareer.Offers.Length; break;
                        case 0x0D when career?.ShopOpen == true:
                            PurchaseSelectedOffer(seconds);
                            break;
                        case 0x4A when career is not null: career.RetrieveProbe(camera.ToCameraState().Position, seconds); break;
                        case 0x4B when vehicles is not null: vehicles.ToggleAnchors(seconds); break;
                        case 0x58 when vehicles is not null: vehicles.Recover(camera, seconds); history++; break;
                        case 0x4E: hud.RegionalMap = !hud.RegionalMap; break;
                        case 0x09:
                            vehicles?.ExitForCameraMode();
                            viewPreset = (viewPreset + 1) % 4;
                            camera.Grounded = false;
                            session.ThirdPerson = false;
                            session.Destruction.ResetAvatarMovement();
                            camera.SetSpeed(viewPreset == 3 ? 220 : 65);
                            SetChaseView(camera, session.World, viewPreset, storms.Simulation.Current);
                            history++;
                            Console.WriteLine("Chase controls: view=" + new[] { "ROADSIDE", "CREST LAKESHORE", "FARM INTERCEPT", "PRAIRIE OVERVIEW" }[viewPreset]);
                            break;
                        case 0x43:
                            vehicles?.ExitForCameraMode();
                            camera.Grounded = !camera.Grounded;
                            session.ThirdPerson = false;
                            camera.SetSpeed(camera.Grounded ? 6 : 65);
                            session.Destruction.ResetAvatarMovement();
                            Console.WriteLine("Chase controls: " + (camera.Grounded ? "ON FOOT" : "FLY CAMERA"));
                            history++; break;
                        case 0x56:
                            vehicles?.ExitForCameraMode();
                            session.ThirdPerson = !session.ThirdPerson;
                            if (session.ThirdPerson) { camera.Grounded = true; camera.SetSpeed(6); }
                            session.Destruction.ResetAvatarMovement();
                            Console.WriteLine("Chase controls: third-person avatar=" + session.ThirdPerson);
                            history++; break;
                        case 0x42:
                            session.DeployProbe(vehicles?.Driving == true ? vehicles.Position : camera.ToCameraState().Position);
                            Console.WriteLine("Chase controls: probes=" + session.ProbeCount); break;
                        case 0x46: photos.Request(); Console.WriteLine("Chase controls: photograph requested"); break;
                        case 0x54: session.CycleLighting(); Console.WriteLine("Chase controls: " + session.LightingName); history++; break;
                        case 0x4D: Console.WriteLine("Chase controls: muted=" + audio.ToggleMuted()); break;
                        case 0x48: hud.Visible = !hud.Visible; Console.WriteLine("Chase controls: HUD=" + hud.Visible); break;
                        case 0x4C: session.Flashlight = !session.Flashlight; Console.WriteLine("Chase controls: torch=" + session.Flashlight); break;
                        default: if (campaign is null || key == 0x50) storms.HandleKey(key); break;
                    }
                }
                if (campaign is not null && (surface.IsMinimized || surface.Width == 0 || surface.Height == 0))
                {
                    if (hud.Visible) hud.Update(surface, EmptyTelemetry(), new(0, DateTimeOffset.UtcNow,
                        new(1, 1), camera.ToCameraState(), default, null, [], []));
                    surface.WaitForMessages(16); continue;
                }
                profiler?.Mark("InputAndKeys");
                if (campaign is null) storms.Update(delta);
                else
                {
                    campaign.Update(delta);
                    session.CampaignHour = campaign.Hour;
                    session.CampaignDay = campaign.DayNumber;
                    session.CampaignWallSeconds = seconds;
                }
                if (!ReferenceEquals(previousSimulation, storms.Simulation))
                {
                    if (storms.WorldResetVersion != previousWorldReset)
                    {
                        session.Reset();
                        previousWorldReset = storms.WorldResetVersion;
                    }
                    previousWeather = null; previousSimulation = storms.Simulation; history++;
                }
                if (surface.IsMinimized || surface.Width == 0 || surface.Height == 0)
                {
                    // Hide the owned instruments while keeping the source lifecycle alive.
                    if (hud.Visible) hud.Update(surface, EmptyTelemetry(), new(0, DateTimeOffset.UtcNow,
                        new(1, 1), camera.ToCameraState(), default, null, [], []));
                    surface.WaitForMessages(16); continue;
                }
                profiler?.Mark("CampaignWeather");
                if (camera.Grounded && Math.Abs(camera.Position.Y - session.World.HeightAt(camera.Position.X, camera.Position.Z) - 1.72f) > 3)
                    camera.SetPosition(new(camera.Position.X, session.World.HeightAt(camera.Position.X, camera.Position.Z) + 1.72f, camera.Position.Z));
                var movementStart = camera.Position;
                surface.UpdateFlyCamera(camera, delta, vehicles?.Driving != true && career?.ShopOpen != true);
                if (vehicles is not null)
                {
                    var input = surface.DrivingInput();
                    vehicles.SetInput(career?.ShopOpen == true ? 0 : input.Throttle,
                        career?.ShopOpen == true ? 0 : input.Steering,
                        input.Brake || career?.ShopOpen == true, previousWeather, delta);
                    if (vehicles.Driving) camera.SetPosition(new(vehicles.Position.X, vehicles.Position.Y + 2, vehicles.Position.Z));
                }
                float ground = session.World.HeightAt(camera.Position.X, camera.Position.Z);
                if (camera.Grounded)
                {
                    var actualFeet = session.Destruction.ResolveAvatarMovement(
                        new(movementStart.X, movementStart.Y - 1.72f, movementStart.Z),
                        new(camera.Position.X, ground, camera.Position.Z), delta, previousWeather);
                    camera.SetPosition(new(actualFeet.X, actualFeet.Y + 1.72f, actualFeet.Z));
                    ground = actualFeet.Y;
                }
                else camera.SetPosition(new(camera.Position.X, Math.Max(camera.Position.Y, ground + .8f), camera.Position.Z));
                bool moving = Vector3.DistanceSquared(previousPosition, camera.Position) > .0004f;
                previousPosition = camera.Position;
                var feet = new Vector3f(camera.Position.X, ground, camera.Position.Z);
                var renderCamera = vehicles?.Driving == true ? vehicles.Camera(camera) : ChaseCamera(camera, session.World, session.ThirdPerson);
                profiler?.Mark("CameraAndAvatar");
                var source = session.GroundStorms(campaign?.Current ?? storms.Simulation.Current);
                var environment = local.Update(new(delta, (float)seconds, 1, 40, session.Hour, 22, renderCamera.Position), source);
                profiler?.Mark("UniStorm");
                var frame = session.BuildFrame(number++, (float)seconds, delta, environment, renderCamera,
                    surface.Width, surface.Height, particles, storms.HistoryVersion + history,
                    feet, moving, local.Snapshot);
                profiler?.Mark("BuildFrame");
                previousWeather = frame.Weather;
                career?.Update(seconds, campaign!.ElapsedSeconds, frame.Weather);
                audio.Update(frame, (float)seconds, local.Snapshot, session.Effects.LastStrike);
                profiler?.Mark("CareerAndAudio");
                var guiInput = surface.ReadGuiInput();
                if (seconds >= nextUi || surface.WindowLayoutVersion != hudLayoutVersion || guiInput != previousGuiInput)
                {
                    var progress = host.GetProgressSnapshot();
                    fps = (progress.CompletedFrameCount - completed) / Math.Max(.001, seconds - lastUi);
                    gpuMs = host.LastGpuTiming?.TotalMilliseconds ?? 0;
                    var telemetry = ChaseTelemetry(session, storms, local, renderCamera, fps, gpuMs, photos.Count, audio.Muted,
                        vehicles?.Driving == true ? "DRIVING" : camera.Grounded ? session.ThirdPerson ? "AVATAR" : "ON FOOT" : "FLY CAMERA", campaign);
                    if (career is not null)
                        telemetry = telemetry with { Campaign = new(campaign!.DayNumber, campaign.Hour, campaign.ForecastName,
                            campaign.ForecastSummary, campaign.Risk, campaign.ActiveStormCount, career.Save.Cash,
                            career.SelectedVehicle.Name, vehicles!.SpeedMph, career.Condition.FuelLiters,
                            career.SelectedVehicle.FuelCapacityLiters, career.Condition.Health, vehicles.Anchored,
                            career.ShopOpen, career.ShopSelection, career.Message, career.Refueling,
                            session.World.Settlements, session.World.GasStations, career.Probes, vehicles.Position,
                            session.World.RoadRoutes) };
                    hud.Update(surface, telemetry, frame, guiInput);
                    hudLayoutVersion = surface.WindowLayoutVersion;
                    previousGuiInput = guiInput;
                    string? action = hud.LastFrame.Interaction.ActivatedElementId;
                    bool uiAction = action is not null && career?.ShopOpen == true;
                    if (uiAction)
                    {
                        if (action!.StartsWith("shop/offer/", StringComparison.Ordinal) &&
                            int.TryParse(action.AsSpan("shop/offer/".Length), out int selected) && selected >= 0 && selected < StormChaseCareer.Offers.Length)
                            career!.ShopSelection = selected;
                        else if (action == "shop/buy") PurchaseSelectedOffer(seconds);
                        else if (action == "shop/previous") career!.ShopSelection = (career.ShopSelection + StormChaseCareer.Offers.Length - 1) % StormChaseCareer.Offers.Length;
                        else if (action == "shop/next") career!.ShopSelection = (career.ShopSelection + 1) % StormChaseCareer.Offers.Length;
                        else if (action == "shop/close") { career!.ShopOpen = false; surface.GuiInputEnabled = false; }
                        Console.WriteLine("Game UI: " + action);
                    }
                    string weatherStatus = campaign is null ? storms.Status + $" | Track {storms.TrackSpeedMph:0} mph" :
                        $"DAY {campaign.DayNumber} | {campaign.ActiveStormCount} storms | {telemetry.Phase} | {telemetry.PeakWind:0} mph | {telemetry.Width:0} m";
                    surface.UpdateFlyCameraTitle(camera, "STORM CHASE | " + weatherStatus +
                        $" | Local {local.LocalWeatherName} {local.StormInfluence:P0} | {qualityName} | {fps:0.0} FPS | GPU {gpuMs:0.0} ms | PhysX {session.Destruction.DestroyedPartCount} parts");
                    if (!firstPresented && progress.CompletedFrameCount > 0)
                    {
                        firstPresented = true; Console.WriteLine("Storm Chase first native frame presented.");
                    }
                    if (seconds >= nextLog)
                    {
                        var cell = source.ActiveStorms.FirstOrDefault();
                        Console.WriteLine($"Chase: {storms.Status}; track={storms.TrackSpeedMph:0}mph center=({cell?.Position.X:0},{cell?.Position.Z:0}); {fps:0.0}FPS GPU {gpuMs:0.00}ms; PhysX broken={session.Destruction.DestroyedPartCount}, moving={session.Destruction.ActiveDebrisCount}; particles={session.Effects.SimulatedParticleCount}; audio={audio.BackendName}; local={local.StormInfluence:P0}; UI={hud.LastFrame.DrawCommands.Count} commands; completed={progress.CompletedFrameCount}.");
                        nextLog = seconds + 2;
                    }
                    completed = progress.CompletedFrameCount; lastUi = seconds; nextUi = uiAction ? 0 : seconds + .2;
                }
                profiler?.Mark("Hud");
                host.SubmitLatest(frame with { Ui = hud.Ui });
                profiler?.Mark("Submit");
                StormChaseFramePacing.WaitForRenderer(host, surface);
                if (profiler is not null)
                {
                    profiler.Mark("WaitForRenderer");
                    profiler.EndUiFrame((clock.Elapsed.TotalSeconds - seconds) * 1000, host.GetProgressSnapshot().CompletedFrameCount,
                        host.LastGpuTiming?.TotalMilliseconds ?? 0, campaign is null ? storms.Status
                            : $"day {campaign.DayNumber} {campaign.Hour:0.0}h, {campaign.ActiveStormCount} storms, items {frame.Items.Count}");
                    if (profiler.ShouldExit) break;
                }
            }
            Console.WriteLine("Storm Chase close requested; draining renderer, audio and native PhysX resources.");
            void PurchaseSelectedOffer(double seconds)
            {
                if (career is null || vehicles is null) return;
                var servicePosition = vehicles.Driving ? vehicles.Position : camera.ToCameraState().Position;
                string offerId = StormChaseCareer.Offers[career.ShopSelection].Id;
                if (career.Purchase(offerId, servicePosition, seconds) && StormChaseCareer.Vehicles.Any(v => v.Id == offerId))
                    vehicles.SwitchIfChanged(career.NearestStation(servicePosition)?.ParkingPosition, collectFromGarage: true);
            }
        }
        finally
        {
            surface.ReleaseMouseLook();
            DemoThreadPump.Wait(Task.Run(host.Dispose), surface);
            DemoThreadPump.Wait(audio.DisposeAsync().AsTask(), surface);
            career?.DrainPhotoRewards(session.CampaignWallSeconds);
            career?.Flush();
        }
        host.ThrowIfFaulted();
        return 0;
    }

    internal static CameraState ChaseCamera(FlyCamera camera, StormChaseWorld world, bool thirdPerson)
    {
        var normal = camera.ToCameraState();
        if (!thirdPerson) return normal;
        var forward = camera.Forward;
        var position = camera.Position - forward * 5 + Vector3.UnitY * 1;
        position.Y = Math.Max(position.Y, world.HeightAt(position.X, position.Z) + 1.5f);
        return normal with { Position = new(position.X, position.Y, position.Z) };
    }

    private static void SetChaseView(FlyCamera camera, StormChaseWorld world, int index, Engine.Environment.EnvironmentSceneState state)
    {
        var center = state.ActiveStorms.FirstOrDefault()?.Position ?? new Vector3f(95, 0, 1040);
        Vector3 target = new(center.X, 160, center.Z);
        Vector3 position = index switch
        {
            1 => new(980, world.HeightAt(980, 55) + 3.5f, 55),
            2 => new(195, world.HeightAt(195, 930) + 4, 930),
            3 => new(-1500, 350, -1200),
            _ => new(-300, world.HeightAt(-300, 20) + 3.5f, 20)
        };
        if (index == 1) target = new(1210, 9, 480);
        camera.SetPose(position, target);
    }

    private static StormChaseTelemetry ChaseTelemetry(StormChaseSession session, LiveStormController storms,
        UniStormSpatialWeatherController local, CameraState camera, double fps, double gpu, int photos, bool muted, string mode,
        StormChaseCampaignWeather? campaign = null)
    {
        var cells = campaign?.Current.ActiveStorms ?? storms.Simulation.Current.ActiveStorms;
        var storm = cells.OrderBy(s => StormChaseCareer.HorizontalDistance(s.Tornadoes.FirstOrDefault()?.Position ?? s.Position, camera.Position)).FirstOrDefault();
        var tornado = storm?.Tornadoes.OrderBy(t => StormChaseCareer.HorizontalDistance(t.Position, camera.Position)).FirstOrDefault();
        var simulation = campaign?.ActiveCells.FirstOrDefault(c => c.Simulation.Current.ActiveStorms.Any(s => s.Id == storm?.Id))?.Simulation ?? storms.Simulation;
        var center = tornado?.Position ?? storm?.Position ?? new Vector3f(0, 0, 1000);
        float dx = center.X - camera.Position.X, dz = center.Z - camera.Position.Z;
        return new(storm is null ? "Clear" : campaign is null ? storms.Phase : simulation.CurrentPhase,
            tornado?.SmoothedWindSpeed ?? tornado?.WindSpeed ?? 0, tornado?.SmoothedWidthMeters ?? tornado?.WidthMeters ?? 0, MathF.Sqrt(dx * dx + dz * dz),
            local.LocalWeatherName, local.StormInfluence, fps, gpu, mode, session.LightingName,
            session.Destruction.DestroyedPartCount, session.ProbeCount, photos, muted, session.ProbeReading,
            storm is null ? 0 : simulation.TornadoEpisodeNumber, storm is null ? 0 : simulation.TouchdownCount, tornado is not null);
    }

    private static StormChaseTelemetry EmptyTelemetry() => new("", 0, 0, 0, "", 0, 0, 0, "", "", 0, 0, 0, true);
    private static string? OptionalValue(string[] args, string option)
    {
        int index = Array.FindIndex(args, a => a.Equals(option, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}

/// <summary>One requested native PNG readback; all renderer state changes occur on its owning thread.</summary>
internal sealed class StormChasePhotos(string output, string staging, Action<ChasePhotoReceipt>? onSaved = null)
{
    private static readonly FieldInfo Completed = typeof(Dx12RenderBackend).GetField("frameExportCompleted", BindingFlags.NonPublic | BindingFlags.Instance)!;
    private int pending, count;
    public int Count => Volatile.Read(ref count);
    public void Request() => Interlocked.CompareExchange(ref pending, 1, 0);
    public void BeforeFrame(IRenderBackend backend)
    {
        if (Interlocked.CompareExchange(ref pending, 2, 1) == 1) Completed.SetValue(backend, false);
    }
    public void AfterFrame(IRenderBackend backend, RenderFrame frame)
    {
        if (Volatile.Read(ref pending) != 2 || !(bool)Completed.GetValue(backend)!) return;
        int number = Interlocked.Increment(ref count);
        string directory = Path.Combine(output, "photographs");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"storm-chase-{DateTime.UtcNow:yyyyMMdd-HHmmssfff}-{number:000}.png");
        File.Copy(staging, path, overwrite: true);
        File.WriteAllText(Path.ChangeExtension(path, ".json"), JsonSerializer.Serialize(new
        {
            frame.FrameNumber, frame.AnimationTimeSeconds, frame.Camera, frame.Weather,
            Sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("Saved storm photograph: " + path);
        onSaved?.Invoke(new(path, frame));
        Volatile.Write(ref pending, 0);
    }
}
