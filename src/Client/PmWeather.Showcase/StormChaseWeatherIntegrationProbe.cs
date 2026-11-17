using System.Text.Json;
using Engine.Environment;
using Engine.Rendering.Assets;
using Engine.Rendering.Backend;
using Engine.Rendering.Configuration;
using Engine.UniStorm;
using Shared.Contracts.Rendering;
using Shared.Contracts.World;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

internal static partial class Showcase
{
    private static async Task<int> VerifyStormChaseWeatherIntegrationAsync(RendererOptions options, NativeSurface surface,
        MutableRenderAssetCatalog catalog, CaptureLogger logger, RenderGpuDebugValidation validation, string output, string staging)
    {
        var checks = new List<string>();
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            checks.Add(message);
        }
        int seed = Enumerable.Range(1, 512).First(value => StormChaseCampaignWeather.CreateForecast(value, 1).Risk >= .8f);
        var campaign = new StormChaseCampaignWeather(new LiveStormController([]), ["--campaign-seed", seed.ToString()]);
        var initial = campaign.Current.Clouds;
        var points = new Dictionary<string, EnvironmentSceneState>();
        var observations = new List<object>();
        for (int second = 0; second < 900; second++)
        {
            campaign.Update(1);
            if (campaign.DayNumber != 1) break;
            var clouds = campaign.Current.Clouds;
            Check(clouds == campaign.RegionalEnvironment.Clouds &&
                clouds.VolumetricDensity > .3f && Math.Abs(clouds.HeightMeters - initial.HeightMeters) < 25 &&
                Math.Abs(clouds.ThicknessMeters - initial.ThicknessMeters) < 30,
                $"Regional cloud layer remains present at campaign second {second + 1}, with {campaign.ActiveStormCount} active cells.");
            if (campaign.ActiveStormCount == 1 && !points.ContainsKey("one-cell")) points.Add("one-cell", campaign.Current);
            if (campaign.ActiveStormCount >= 2 && !points.ContainsKey("multiple-cells") &&
                campaign.Current.ActiveStorms.Any(storm => storm.SmoothedStage >= 2.8f))
                points.Add("multiple-cells", campaign.Current);
            if (second % 60 == 0) observations.Add(new { campaign.ElapsedSeconds, campaign.Hour, campaign.ActiveStormCount, Clouds = clouds });
            if (points.Count == 2 && campaign.Hour > 16) break;
        }
        Check(points.Count == 2, "A genuine seeded campaign developed both single and concurrent cells.");
        var source = points["multiple-cells"];
        var weatherCatalog = UniStormWeatherCatalog.LoadDefault();
        UniStormSpatialWeatherController Controller() => new(weatherCatalog, seed,
            new UniStormOptions { AutomaticWeatherEnabled = false, AdvanceInternalClock = false, TransitionSeconds = 2 });
        var far = new Vector3f(-23000, 90, -23000);
        var local = Controller();
        var farEnvironment = local.Update(new(1, 42, 2026, 150, 15, 22, far), source);
        Check(local.TargetStormInfluence == 0 && local.StormInfluence == 0,
            "An observer outside every storm stays in ambient weather.");
        Check(farEnvironment.Clouds == source.Clouds && ReferenceEquals(farEnvironment.Storms, source.Storms),
            "Observer-local UniStorm preserves the regional cloud layer and exact authoritative storm list.");
        var mature = FindWallCloudSourcePoints().Single(point => point.Name == "wall-04-mature-touchdown").State;
        var nearSource = source with { Storms = mature.Storms };
        var near = mature.ActiveStorms.Single().Position;
        for (int i = 0; i < 100; i++) local.Update(new(.1f, 42 + i * .1f, 2026, 150, 15, 22, near), nearSource);
        float wet = local.StormInfluence;
        Check(wet > .8f, "Entering a mature canopy transitions UniStorm into local thunderstorm weather.");
        var firstExit = local.Update(new(.1f, 53, 2026, 150, 15, 22, far), nearSource);
        Check(local.StormInfluence > .5f && local.StormInfluence < wet,
            "Leaving a storm starts a smooth transition rather than instantly clearing local weather.");
        for (int i = 0; i < 160; i++) local.Update(new(.1f, 53 + i * .1f, 2026, 150, 15, 22, far), nearSource);
        Check(local.StormInfluence < .001f && firstExit.Clouds == source.Clouds,
            "UniStorm returns to ambient weather while retaining the regional cloud layer.");

        await using var backend = new Dx12RenderBackend(options, surface, catalog, logger);
        await backend.InitializeAsync(default);
        long number = 0, history = 870000;
        var captures = new List<object>();
        var pixels = new Dictionary<string, byte[]>();
        async Task Capture(string name, EnvironmentSceneState state, CameraState camera, float seconds = 42)
        {
            history++;
            var frame = CreateLiveFrame(number, seconds, state, surface.Width, surface.Height, false, false, history, camera);
            for (int warm = 0; warm < 32; warm++)
                await backend.RenderAsync(frame with { FrameNumber = number++ }, default);
            ExportCompleted.SetValue(backend, false);
            await backend.RenderAsync(frame with { FrameNumber = number++ }, default);
            Check((bool)ExportCompleted.GetValue(backend)! && backend.LastFrameStatistics.RequiredResourcesResident,
                name + " captured resident assets in the actual DX12 renderer.");
            string path = Path.Combine(output, name + ".png");
            File.Copy(staging, path, true);
            using var image = Image.Load<Rgba32>(path);
            byte[] bytes = new byte[image.Width * image.Height * 4]; image.CopyPixelDataTo(bytes); pixels[name] = bytes;
            captures.Add(new { Name = name, Path = path, NativeDx12 = true, Clouds = state.Clouds,
                StormCount = state.ActiveStorms.Count, backend.LastGpuTiming });
        }
        var awayCamera = ChaseCaptureCamera(far, far + new Vector3f(-800, 650, -800));
        var clearSource = source with { Storms = [], Weather = WeatherState.Clear, WeatherEffects = WeatherEffectsState.None, Precipitation = 0 };
        var clear = Controller().Update(new(1, 42, 2026, 150, 15, 22, far), clearSource);
        await Capture("regional-before-storms", clear, awayCamera);
        await Capture("regional-with-storms", farEnvironment, awayCamera);
        var cloudless = clear with { Clouds = clear.Clouds with { Coverage = 0, Density = 0, VolumetricDensity = 0 } };
        await Capture("regional-cloudless-reference", cloudless, awayCamera);
        double retainedDifference = PixelDifference(pixels["regional-before-storms"], pixels["regional-with-storms"]);
        double cloudDifference = PixelDifference(pixels["regional-before-storms"], pixels["regional-cloudless-reference"]);
        Check(cloudDifference > .0002, "The regional cloud layer produces visible cloud pixels in the native frame.");
        Check(retainedDifference < Math.Max(.004, cloudDifference * .15),
            "Developing concurrent supercells retains the off-storm regional cloud appearance.");
        var storm = source.ActiveStorms[0];
        var stormCamera = ChaseCaptureCamera(storm.Position + new Vector3f(-4400, 220, -4600),
            storm.Position + new Vector3f(0, 1200, 0));
        var stormView = Controller().Update(new(1, 42, 2026, 150, 15, 22, stormCamera.Position), source);
        await Capture("integrated-supercells", stormView, stormCamera);
        var night = StormChaseCampaignWeather.CreateNightCapture();
        Check(night.Hour < 6 && night.Tornado.TouchdownAmount >= .95f && night.Touchdowns >= 1,
            "A naturally developed post-midnight touchdown supplies the nighttime render capture.");
        var nightCamera = ChaseCaptureCamera(night.Tornado.Position + new Vector3f(-1650, 85, -1900),
            night.Tornado.Position + new Vector3f(0, 430, 0));
        var nightController = Controller();
        var nightView = night.Environment;
        for (int warm = 0; warm < 40; warm++)
            nightView = nightController.Update(new(.25f, (float)night.ElapsedSeconds, 2026, 150,
                night.Hour, 22, nightCamera.Position), night.Environment);
        Check(nightView.Clouds == night.Environment.Clouds && ReferenceEquals(nightView.Storms, night.Environment.Storms),
            "Nighttime UniStorm retains the real regional clouds and authoritative touchdown.");
        await Capture("natural-post-midnight-tornado", nightView, nightCamera, (float)night.ElapsedSeconds);
        await Capture("post-midnight-stormless-reference", nightView with { Storms = [] }, nightCamera,
            (float)night.ElapsedSeconds);
        double nightStormDifference = PixelDifference(pixels["natural-post-midnight-tornado"],
            pixels["post-midnight-stormless-reference"]);
        Check(nightStormDifference > .0002, "The real nighttime supercell and tornado produce visible native pixels.");
        var appearanceCaptures = new List<object>();
        var development = StormChaseCampaignWeather.CreateDevelopmentCapturePoints();
        var firstParent = development[0].Environment.ActiveStorms.Single(storm => storm.Id == development[0].StormId);
        var developmentCamera = ChaseCaptureCamera(firstParent.Position + new Vector3f(-5300, 180, -5600),
            firstParent.Position + new Vector3f(0, 1200, 0));
        var developmentVisibility = new List<double>();
        async Task CaptureAppearance(StormChaseSourceCapture point, string name, CameraState camera, bool reference)
        {
            var parent = point.Environment.ActiveStorms.Single(storm => storm.Id == point.StormId);
            var actual = point.Environment with { Storms = [parent] };
            var observer = Controller();
            var view = actual;
            for (int warm = 0; warm < 40; warm++)
                view = observer.Update(new(.25f, (float)point.ElapsedSeconds, 2026, 150, point.Hour, 22, camera.Position), actual);
            await Capture(name, view, camera, (float)point.ElapsedSeconds);
            if (reference)
            {
                await Capture(name + "-cloudless-cell-reference", view with { Storms = [] }, camera, (float)point.ElapsedSeconds);
                developmentVisibility.Add(PixelDifference(pixels[name], pixels[name + "-cloudless-cell-reference"]));
            }
            appearanceCaptures.Add(new { Name = name, point.Seed, point.StormId, point.Form, point.Phase,
                point.Stage, point.AgeSeconds, point.DevelopmentWallSeconds, point.Hour, point.ElapsedSeconds,
                parent.CoreRadiusMeters, parent.UpdraftRadiusMeters, parent.AnvilRadiusMeters, parent.CloudBaseMeters,
                parent.CloudTopMeters, parent.RotationRadians, parent.PrecipitationCoreAmount,
                AppearanceSeed = PmWeatherStormAppearance.Seed(parent.Id),
                Tornadoes = parent.Tornadoes.Select(child => new { child.TornadoShape, child.SmoothedWidthMeters,
                    child.TopRadiusMeters, child.BaseRadiusMeters, child.TouchdownAmount, child.SmoothedWindSpeed }) });
        }
        foreach (var point in development)
            await CaptureAppearance(point, "development-" + point.Name, developmentCamera, true);
        Check(development[2].AgeSeconds > 30 && development[0].Stage < .05f && development[1].Stage < 1,
            "Visible development captures follow the same real parent from thin birth to organization over more than 30 wall seconds.");
        Check(developmentVisibility[2] > .0002 && developmentVisibility[2] > developmentVisibility[0] * 2,
            "The actual native cloud grows from negligible birth pixels into a visible organized cloud.");
        int formNumber = 0;
        var morphology = StormChaseCampaignWeather.CreateMorphologyCapturePoints();
        foreach (var point in morphology)
        {
            var parent = point.Environment.ActiveStorms.Single(storm => storm.Id == point.StormId);
            var camera = ChaseCaptureCamera(parent.Position + new Vector3f(-3700, 140, -4200),
                parent.Position + new Vector3f(0, 900, 0));
            await CaptureAppearance(point, "morphology-" + formNumber++, camera, false);
        }
        Check(morphology.Select(point => point.Form).Distinct().Count() == 4 &&
            morphology.All(point => point.Environment.ActiveStorms.Single(storm => storm.Id == point.StormId)
                .Tornadoes.Any(child => child.TouchdownAmount >= .95f)),
            "Four distinct storm ensembles reach the renderer through naturally developed source touchdowns.");
        var morphologyDifferences = Enumerable.Range(1, 3).Select(index =>
            PixelDifference(pixels["morphology-0"], pixels["morphology-" + index])).ToArray();
        Check(morphologyDifferences.All(difference => difference > .001),
            "Actual native storm morphology captures have distinct visible appearance.");
        validation.CollectErrors(backend, "integrated regional/storm clouds", true);
        string report = Path.Combine(output, "weather-integration-check.json");
        File.WriteAllText(report, JsonSerializer.Serialize(new { Passed = true, Seed = seed, Checks = checks,
            Observations = observations, Captures = captures, RetainedDifference = retainedDifference,
            CloudDifference = cloudDifference, NightStormDifference = nightStormDifference,
            NightTouchdown = new { night.Seed, night.DayNumber, night.Hour, night.ElapsedSeconds,
                night.AvailableInstability, night.NocturnalSupport, night.Episode, night.Touchdowns, night.StormId,
                night.Tornado.Position, night.Tornado.SmoothedWindSpeed, night.Tornado.TouchdownAmount },
            AppearanceCaptures = appearanceCaptures, DevelopmentVisibility = developmentVisibility,
            MorphologyDifferences = morphologyDifferences,
            ShaderHashes = ShaderHashes() }, JsonOptions));
        Console.WriteLine($"Weather integration checks passed: {checks.Count}. {report}");
        return 0;
    }
}
