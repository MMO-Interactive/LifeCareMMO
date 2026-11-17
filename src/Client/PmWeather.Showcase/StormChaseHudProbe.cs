using Engine.Rendering.Assets;
using Engine.Rendering.Backend;
using Engine.Rendering.Configuration;
using LifeCare.Construction;
using Shared.Contracts.Rendering;
using Shared.Contracts.World;
using StormChase.Protocol;

internal static partial class Showcase
{
    /// <summary>
    /// Renders the LifeCare HUD in each of its states through the actual DX12 renderer and saves a PNG per state:
    /// the everyday HUD, build mode, the pie wheel, the phone, its Weather app, the world map and driving.
    /// </summary>
    private static async Task<int> VerifyLifeCareHudAsync(RendererOptions options, NativeSurface surface,
        MutableRenderAssetCatalog catalog, CaptureLogger logger, string output, string staging)
    {
        var source = StormChaseCampaignWeather.CreateMorphologyCapturePoints().First();
        var world = new StormChaseWorld(catalog);
        var town = world.Settlements.OrderBy(s => s.RadiusMeters).Last();
        var feet = town.Position with { Y = world.HeightAt(town.Position.X, town.Position.Z) };
        var camera = ChaseCaptureCamera(feet + new Vector3f(0, 1.7f, 0), feet + new Vector3f(40, 0, 60));
        var parent = source.Environment.ActiveStorms.Single(storm => storm.Id == source.StormId);
        var atmosphere = StormChaseAtmosphere.Snapshot(source.Seed, source.DayNumber, 15.4f, source.ElapsedSeconds,
            (long)(source.ElapsedSeconds * 20), new(parent.Position.X, parent.Position.Y, parent.Position.Z));
        var mission = new MissionState(1, "Secure the hardware store", "police", 1, 3, "Tape off the damaged storefront and keep shoppers back.",
            new(feet.X + 180, feet.Y, feet.Z + 260), false, 12, 40);
        var c = new StormChaseCampaignTelemetry(atmosphere.DayNumber, 15.4f, atmosphere.ThreeDayForecast[0].Name, "",
            atmosphere.Diagnostics.TornadoPotential01, 2, 12450, "Police cruiser", 38, 41, 60, 82, false, false, 0, "Shift started. Check the job board.", false,
            world.Settlements, world.GasStations, [], feet, world.RoadRoutes, Popularity: 34, Mission: mission, Atmosphere: atmosphere, Job: StormChaseJobs.Police, Seed: source.Seed,
            Boundary: StormChaseAtmosphere.ActiveBoundary(source.Seed, atmosphere.DayNumber, 15.4f));
        var tornado = parent.Tornadoes.FirstOrDefault();
        var t = new StormChaseTelemetry(source.Phase, tornado?.SmoothedWindSpeed ?? 140, tornado?.SmoothedWidthMeters ?? 180, 5200, "Thunderstorm", 1, 60, 10,
            "ON FOOT", "AFTERNOON", 0, 0, 0, false, FunnelEpisode: 1, Touchdowns: 1, HasFunnel: true, Campaign: c);
        var lotTile = (X: (int)MathF.Floor((feet.X + 60) / 4), Z: (int)MathF.Floor((feet.Z - 40) / 4));
        var lots = new[] { new MapLot("starter-you", lotTile.X * 4, lotTile.Z * 4, (lotTile.X + 10) * 4, (lotTile.Z + 10) * 4, true, 22),
            new MapLot("neighbour", (lotTile.X + 14) * 4, lotTile.Z * 4, (lotTile.X + 24) * 4, (lotTile.Z + 10) * 4, false, 0) };

        using var hud = new StormChaseHud(surface.WindowHandle);
        hud.AttachWorld(world); hud.PlayerName = "Jamie Rivera"; hud.MapLots = () => lots;
        hud.AddChat(new("3:20", StormChase.Protocol.ChatChannel.World, "District", "Sam Okafor arrived in the district.", true));
        hud.AddChat(new("3:21", StormChase.Protocol.ChatChannel.Local, "Sam Okafor", "Anyone heading to the hardware store? The front windows are gone.", false));
        hud.AddChat(new("3:22", StormChase.Protocol.ChatChannel.Local, "Jamie Rivera", "On my way, taping it off now.", false));
        hud.AddChat(new("3:23", StormChase.Protocol.ChatChannel.World, "Lena Marlow", "Shelter at Meridian school is open.", false));
        hud.Inventory = new("Empty hands", [new("phone", "Phone", "Weather, jobs, home and more  ·  P", "phone"), new("keys", "Keys", "Police cruiser  ·  E to enter", "equipment"),
            new("probes", "Field probes ×2", "Deploy with B", "equipment")]);
        var crate = new HudContainer("delivery-crate", "Delivery crate", "Packages delivered to your lot. Take one to carry it and set it down.",
            FurnishingCatalog.Entries.Select(e => new HudItem(e.Definition.DefinitionId, e.Definition.Name, "Set it down on your lot's floor", "delivery")).ToArray());
        await using var backend = new Dx12RenderBackend(options, surface, catalog, logger);
        await backend.InitializeAsync(default);
        var bed = PlacedFurnishing.Of("bed-1", FurnishingCatalog.Find("exam-bed")!.Definition, 0, 0, 0, 0, 0, 0);
        long number = 0; var saved = new List<string>();
        foreach (string page in new[] { "hud", "dock", "inventory", "build", "pie", "phone", "weather", "today", "week", "map", "driving" })
        {
            hud.DockOpen = page is "dock" or "inventory"; hud.SnapDock(); hud.InventoryOpen = page == "inventory"; hud.InventorySelection = page == "inventory" ? 0 : -1;
            if (page == "inventory") { hud.OpenContainer(crate); hud.ContainerSelection = 1; } else hud.HandleHudAction("container/close", out _);
            hud.CursorMode = page == "dock";
            hud.PhoneOpen = page is "phone" or "weather" or "today" or "week"; hud.PhoneApp = page is "weather" or "today" or "week" ? "weather" : null;
            hud.WeatherTab = page is "today" or "week" ? page : "now"; hud.MapOpen = page == "map";
            hud.Hotbar = page == "build" ? [new("Floor", false), new("Wall", true), new("Roof", false), new("Delete", false)] : null;
            hud.LotStatus = page == "build" ? "BUILD · WALL · GROUND  —  Build 3 walls." : null;
            hud.LotMessage = page == "build" ? "Lay 9 floor tiles done." : null;
            var pie = page == "pie" ? InteractionPieMenu.ForFurnishing(bed, "Exam bed", true, surface.Width / 2, surface.Height / 2, surface.Width, surface.Height) : null;
            hud.WorldOverlay = pie is null ? null : (w, h) => StormChaseLotPlay.PieOverlay(pie, w, h);
            var telemetry = page == "driving" ? t with { CameraMode = "DRIVING" } : t;
            var frame = CreateLiveFrame(number, (float)source.ElapsedSeconds, source.Environment, surface.Width, surface.Height, false, false, 960000 + number, camera);
            hud.Update(surface, telemetry, frame);
            hud.Update(surface, telemetry, frame);
            frame = frame with { Ui = hud.Ui };
            ExportCompleted.SetValue(backend, false);
            for (int warm = 0; warm < 40 && !(bool)ExportCompleted.GetValue(backend)!; warm++)
                await backend.RenderAsync(frame with { FrameNumber = number++ }, default);
            if (!(bool)ExportCompleted.GetValue(backend)!) throw new InvalidOperationException("HUD frame did not export: " + page);
            string path = Path.Combine(output, "lifecare-hud-" + page + ".png"); File.Copy(staging, path, true); saved.Add(path);
            Console.WriteLine("Saved " + path);
        }
        Console.WriteLine($"LifeCare HUD captured: {saved.Count} states in {output}");
        return 0;
    }
}
