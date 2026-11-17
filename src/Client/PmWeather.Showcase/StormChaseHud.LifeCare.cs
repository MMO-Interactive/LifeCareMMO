using Engine.GUI;
using Shared.Contracts.Rendering;
using Shared.Contracts.World;

/// <summary>One hotbar slot (Tiny Clinic style): its label, and whether it is the one in hand.</summary>
internal sealed record HudSlot(string Label, bool Selected);

/// <summary>
/// The LifeCare parts of the HUD: notifications, the hotbar, the phone (and its Weather app), the Life Beyond minimap and
/// full world map, and the clicks they raise. Clicks the HUD cannot finish itself come back as a request for the game.
/// </summary>
internal sealed partial class StormChaseHud
{
    private readonly List<(string Time, string Text, GuiColor Color)> notifications = [];
    private string lastMessage = "", lastLotStatus = "";
    private bool tornadoWarned;
    private StormChaseMapView? mapView;
    private HudLayout? phoneLayout;

    public static readonly IReadOnlyList<HudSlot> DefaultHotbar = [new("Hands", true)];
    public static readonly float[] MiniZoomMeters = [180, 360, 720, 1440];
    public static readonly float[] FullZoomMeters = [400, 800, 1600, 3200, 6400, 12800];

    /// <summary>The avatar's name, shown on the character card.</summary>
    public string PlayerName { get; set; } = "";
    /// <summary>Hotbar contents; null shows empty hands. Build mode fills it with its tools.</summary>
    public IReadOnlyList<HudSlot>? Hotbar { get; set; }
    /// <summary>The lot layer's latest status (build mode, furniture), shown in the status pill and as a notification.</summary>
    public string? LotStatus { get; set; }
    /// <summary>The lot layer's last confirmed event (edit done or refused); only these become notifications.</summary>
    public string? LotMessage { get; set; }
    /// <summary>LifeCare lots for the map.</summary>
    public Func<IReadOnlyList<MapLot>>? MapLots { get; set; }
    public StormChaseWorld? World { get; private set; }
    public bool PhoneOpen { get; set; }
    /// <summary>The phone app on screen; null is the home screen.</summary>
    public string? PhoneApp { get; set; }
    public bool MapOpen { get; set; }
    public int MiniZoom { get; set; } = 1;
    public int FullZoom { get; set; } = 2;
    /// <summary>Where the full map looks; null follows the player.</summary>
    public Vector3f? MapCenter { get; set; }
    public bool MapRoads { get; set; } = true;
    public bool MapBuildings { get; set; } = true;
    public bool MapLotsShown { get; set; } = true;

    public void AttachWorld(StormChaseWorld world) { World = world; mapView = new(world); }

    public void Notify(string text, GuiColor? color = null, float? hour = null)
    {
        notifications.Insert(0, (hour is { } h ? ClockText(h) : DateTime.Now.ToString("h:mm tt"), text, color ?? Theme.Sky));
        if (notifications.Count > 6) notifications.RemoveAt(notifications.Count - 1);
    }

    private void TrackNotifications(StormChaseTelemetry t)
    {
        float? hour = t.Campaign?.Hour;
        if (t.Campaign?.Message is { Length: > 0 } message && message != lastMessage) { lastMessage = message; Notify(message, Theme.Sky, hour); }
        if (LotMessage is { Length: > 0 } lot && lot != lastLotStatus) { lastLotStatus = lot; Notify(lot, Theme.Good, hour); }
        bool near = t.HasFunnel && t.Distance > 0 && t.Distance < 8000;
        if (near && !tornadoWarned) { tornadoWarned = true; Notify($"Tornado warning  ·  {t.Distance / 1000:0.0} km  ·  P for Weather", Theme.Danger, hour); }
        if (!t.HasFunnel || t.Distance > 12000) tornadoWarned = false;
    }

    private MapMarkers Markers(RenderFrame frame)
    {
        var mission = telemetryForInstruments?.Campaign?.Mission;
        return new(frame.Camera.Position, frame.Camera.Forward,
            mission is null ? null : new Vector3f(mission.TargetPosition.X, mission.TargetPosition.Y, mission.TargetPosition.Z), MapLots?.Invoke() ?? []);
    }

    /// <summary>
    /// Handles a HUD button. Returns true when the id belonged to the HUD; <paramref name="request"/> names what the game
    /// must do (jobs, home, shop, build, menu, forecast) when the HUD cannot do it alone.
    /// </summary>
    public bool HandleHudAction(string action, out string? request)
    {
        request = null;
        switch (action)
        {
            case "hud/map": MapOpen = !MapOpen; PhoneOpen = false; MapCenter = null; return true;
            case "hud/phone": PhoneOpen = !PhoneOpen; PhoneApp = null; return true;
            case "hud/jobs": request = "jobs"; return true;
            case "hud/build": request = "build"; return true;
            case "hud/shop": request = "shop"; return true;
            case "hud/menu": request = "menu"; return true;
            case "phone/home": PhoneApp = null; return true;
            case "phone/close": PhoneOpen = false; return true;
            case "phone/app/weather": PhoneApp = "weather"; return true;
            case "phone/app/jobs": PhoneOpen = false; request = "jobs"; return true;
            case "phone/app/home": PhoneOpen = false; request = "home"; return true;
            case "phone/app/shop": PhoneOpen = false; request = "shop"; return true;
            case "phone/app/build": PhoneOpen = false; request = "build"; return true;
            case "phone/app/map": PhoneOpen = false; MapOpen = true; MapCenter = null; return true;
            case "phone/weather/ref": RadarProduct = StormChaseRadarProduct.Reflectivity; return true;
            case "phone/weather/vel": RadarProduct = StormChaseRadarProduct.RadialVelocity; return true;
            case "phone/weather/range": RegionalMap = !RegionalMap; InvalidateRadar(); return true;
            case "phone/weather/forecast": PhoneOpen = false; request = "forecast"; return true;
            case "map/mini-in": MiniZoom = Math.Max(0, MiniZoom - 1); return true;
            case "map/mini-out": MiniZoom = Math.Min(MiniZoomMeters.Length - 1, MiniZoom + 1); return true;
            case "map/close": MapOpen = false; return true;
            case "map/in": FullZoom = Math.Max(0, FullZoom - 1); return true;
            case "map/out": FullZoom = Math.Min(FullZoomMeters.Length - 1, FullZoom + 1); return true;
            case "map/center": MapCenter = null; return true;
            case "map/roads": MapRoads = !MapRoads; return true;
            case "map/buildings": MapBuildings = !MapBuildings; return true;
            case "map/lots": MapLotsShown = !MapLotsShown; return true;
            case "map/left" or "map/right" or "map/up" or "map/down":
            {
                var center = MapCenter ?? observerPosition; float step = FullZoomMeters[FullZoom] * .25f;
                MapCenter = action switch
                {
                    "map/left" => center with { X = center.X - step }, "map/right" => center with { X = center.X + step },
                    "map/up" => center with { Z = center.Z + step }, _ => center with { Z = center.Z - step },
                };
                return true;
            }
        }
        if (action.StartsWith("map/town/", StringComparison.Ordinal) && int.TryParse(action.AsSpan(9), out int index))
        {
            if (TownTabs().ElementAtOrDefault(index) is { } town) { MapCenter = town.Position; FullZoom = Math.Min(FullZoom, 2); }
            return true;
        }
        return false;
    }

    private IReadOnlyList<StormChaseSettlement> TownTabs() => (World?.Settlements ?? [])
        .OrderBy(town => StormChaseCareer.HorizontalDistance(observerPosition, town.Position)).Take(5).ToArray();

    // ==================================================================== phone
    private static readonly (string Id, string Label, GuiColor Color)[] PhoneApps =
    [
        ("weather", "Weather", new(64, 160, 240)), ("jobs", "Jobs", new(232, 162, 52)), ("home", "Home", new(70, 184, 110)),
        ("shop", "Shop", new(150, 104, 232)), ("map", "Map", new(40, 170, 160)), ("build", "Build", new(236, 120, 64)),
    ];

    private static GuiRect PhoneScreen(GuiRect phone) => new(phone.X + 12, phone.Y + 14, phone.Width - 24, phone.Height - 28);

    private void BuildPhone(List<GuiElement> root, HudLayout L, StormChaseTelemetry t)
    {
        phoneLayout = L;
        var k = new HudKit(L.Scale); var p = L.Phone; var screen = PhoneScreen(p); var c = t.Campaign;
        root.Add(k.Fill(p, new(6, 12, 20, 250), 34, Theme.TileEdge, 2));
        root.Add(k.Fill(screen, Theme.Glass.WithAlpha(255), 24, Theme.GlassEdge));
        root.Add(k.Label(screen, ClockText(c?.Hour ?? 12), 20, 8, 80, 8.5f, Theme.Ink, bold: true));
        root.Add(k.Label(screen, "LifeCare", 0, 8, screen.Width, 8, Theme.Muted, align: GuiTextAlignment.Center));
        root.Add(k.Button("phone/close", "×", new(p.Right - 44, p.Y - 12, 30, 28), size: 12));
        root.Add(new GuiButton { Id = "phone/home", Text = "", Translation = new((screen.X + screen.Width / 2 - 70) * L.Scale, (screen.Bottom - 26) * L.Scale),
            Width = GuiLength.Pixels(140 * L.Scale), Height = GuiLength.Pixels(20 * L.Scale), CornerRadius = 10 * L.Scale,
            BackgroundColor = new(0, 0, 0, 0), HoverBackgroundColor = new(255, 255, 255, 20), PressedBackgroundColor = new(255, 255, 255, 40) });
        if (PhoneApp == "weather") { BuildWeatherApp(root, k, screen, t); return; }

        string first = (string.IsNullOrWhiteSpace(PlayerName) ? "there" : PlayerName.Split(' ')[0]);
        float hour = c?.Hour ?? 12;
        root.Add(k.Label(screen, ClockText(hour).Split(' ')[0], 0, 44, screen.Width, 34, Theme.Ink, bold: true, align: GuiTextAlignment.Center));
        root.Add(k.Label(screen, $"{(hour < 12 ? "Good morning" : hour < 18 ? "Good afternoon" : "Good evening")}, {first}", 0, 112, screen.Width, 10, Theme.Muted, align: GuiTextAlignment.Center));
        for (int i = 0; i < PhoneApps.Length; i++)
        {
            var tile = new GuiRect(screen.X + 24 + i % 3 * 84, screen.Y + 160 + i / 3 * 108, 72, 72);
            root.Add(new GuiButton { Id = "phone/app/" + PhoneApps[i].Id, Text = "", Translation = new(tile.X * L.Scale, tile.Y * L.Scale),
                Width = GuiLength.Pixels(tile.Width * L.Scale), Height = GuiLength.Pixels(tile.Height * L.Scale), CornerRadius = 14 * L.Scale,
                BackgroundColor = Theme.Tile, HoverBackgroundColor = new(34, 104, 156, 250), PressedBackgroundColor = new(10, 40, 70, 255),
                BorderColor = Theme.TileEdge, BorderThickness = Math.Max(1, L.Scale) });
            root.Add(k.Label(tile, PhoneApps[i].Label, -10, 78, 92, 8.5f, Theme.Ink, align: GuiTextAlignment.Center));
        }
        if (t.HasFunnel && t.Distance > 0 && t.Distance < 15000)
        {
            var alert = new GuiRect(screen.X + 16, screen.Y + 400, screen.Width - 32, 54);
            root.Add(k.Fill(alert, new(120, 24, 30, 240), 12, Theme.Danger));
            root.Add(k.Label(alert, "TORNADO WARNING", 12, 8, alert.Width - 24, 9.5f, Theme.Ink, bold: true));
            root.Add(k.Label(alert, $"{t.Distance / 1000:0.0} km away  ·  open Weather", 12, 28, alert.Width - 24, 8.5f, Theme.Ink));
        }
    }

    private void BuildWeatherApp(List<GuiElement> root, HudKit k, GuiRect screen, StormChaseTelemetry t)
    {
        var c = t.Campaign;
        root.Add(k.Button("phone/home", "‹ Home", new(screen.X + 12, screen.Y + 30, 64, 24), size: 8.5f, fill: new(255, 255, 255, 14)));
        root.Add(k.Label(screen, "Weather", 0, 32, screen.Width, 12, Theme.Ink, bold: true, align: GuiTextAlignment.Center));
        var now = new GuiRect(screen.X + 12, screen.Y + 64, screen.Width - 24, 70);
        root.Add(k.Fill(now, new(255, 255, 255, 12), 12));
        root.Add(k.Label(now, c?.Forecast ?? t.LocalWeather, 12, 8, 170, 13, Theme.Ink, bold: true));
        root.Add(k.Label(now, c is null ? "" : TemperatureText(c).TrimStart(' ', '·'), now.Width - 102, 6, 90, 16, Theme.Ink, bold: true, align: GuiTextAlignment.Right));
        string here = t.LocalStorm is { } local && local.Summary(t.Gust) is { Length: > 0 } summary ? summary : "Calm where you are";
        root.Add(k.Label(now, here, 12, 34, now.Width - 24, 8.5f, Theme.Muted));
        root.Add(k.Label(now, c is null ? "" : $"Tornado potential {c.Risk:P0}  ·  {c.StormCount} active cell{(c.StormCount == 1 ? "" : "s")}", 12, 50, now.Width - 24, 7.5f, c is null ? Theme.Faint : Theme.Risk(c.Risk)));

        var storm = new GuiRect(screen.X + 12, screen.Y + 142, screen.Width - 24, 92);
        bool quiet = c?.StormCount == 0 || t.Distance <= 0, active = t.HasFunnel && t.PeakWind > 0;
        var threat = active ? Theme.Threat(t.PeakWind) : Theme.Sky;
        root.Add(k.Fill(storm, threat.WithAlpha(24), 12, threat.WithAlpha(120)));
        root.Add(k.Label(storm, quiet ? "NO ACTIVE CELLS" : active ? "NEAREST TORNADO" : "NEAREST STORM", 12, 8, 160, 7.5f, threat, bold: true));
        if (quiet) root.Add(k.Label(storm, "Clear skies", 12, 26, storm.Width - 24, 15, Theme.Ink, bold: true));
        else
        {
            root.Add(k.Label(storm, active ? $"{t.PeakWind:0} mph" : $"{t.Distance / 1000:0.0} km", 12, 24, 140, 16, Theme.Ink, bold: true));
            root.Add(k.Label(storm, active ? $"{t.Width:0} m wide  ·  {t.Distance / 1000:0.0} km" : t.Phase, 150, 28, storm.Width - 162, 8.5f, Theme.Muted, align: GuiTextAlignment.Right));
        }
        root.Add(k.Label(storm, $"{t.FunnelEpisode} funnel{(t.FunnelEpisode == 1 ? "" : "s")}  ·  {t.Touchdowns} touchdown{(t.Touchdowns == 1 ? "" : "s")}", 12, 68, storm.Width - 24, 7.5f, Theme.Faint));

        float y = screen.Bottom - 64;
        float bw = (screen.Width - 24 - 18) / 4;
        root.Add(k.Button("phone/weather/ref", "REF", new(screen.X + 12, y, bw, 26), RadarProduct == StormChaseRadarProduct.Reflectivity, 8));
        root.Add(k.Button("phone/weather/vel", "VEL", new(screen.X + 18 + bw, y, bw, 26), RadarProduct == StormChaseRadarProduct.RadialVelocity, 8));
        root.Add(k.Button("phone/weather/range", RegionalMap ? "REGION" : "LOCAL", new(screen.X + 24 + bw * 2, y, bw, 26), size: 8));
        root.Add(k.Button("phone/weather/forecast", "OUTLOOK", new(screen.X + 30 + bw * 3, y, bw, 26), size: 8));
    }

    private void DrawPhoneInstruments(GuiDx12MeshBuilder mesh, HudLayout L, StormChaseTelemetry t, RenderFrame frame)
    {
        float s = L.Scale; var screen = PhoneScreen(L.Phone);
        // Battery and home indicator.
        mesh.BorderRect(new((screen.Right - 40) * s, (screen.Y + 10) * s, 20 * s, 10 * s), Theme.Ink, Math.Max(1, s), 2 * s);
        mesh.FillRect(new((screen.Right - 38) * s, (screen.Y + 12) * s, 13 * s, 6 * s), Theme.Good, s);
        mesh.FillRect(new((screen.X + screen.Width / 2 - 56) * s, (screen.Bottom - 18) * s, 112 * s, 5 * s), Theme.Ink.WithAlpha(200), 2.5f * s);
        if (PhoneApp != "weather")
        {
            for (int i = 0; i < PhoneApps.Length; i++)
            {
                float cx = (screen.X + 24 + i % 3 * 84 + 36) * s, cy = (screen.Y + 160 + i / 3 * 108 + 36) * s, r = 15 * s;
                switch (PhoneApps[i].Id)
                {
                    case "weather": DrawWeatherIcon(mesh, cx, cy, r, s); break;
                    case "home":
                        mesh.Line(cx - r, cy - r * .1f, cx, cy - r, Theme.Ink, 2.4f * s); mesh.Line(cx, cy - r, cx + r, cy - r * .1f, Theme.Ink, 2.4f * s);
                        mesh.BorderRect(new(cx - r * .7f, cy - r * .2f, r * 1.4f, r * 1.1f), Theme.Ink, 2.4f * s); break;
                    default: DrawIcon(mesh, "hud/" + PhoneApps[i].Id, cx, cy, r, s * 1.3f); break;
                }
            }
            return;
        }
        // Weather app: EF bar, bearing to the storm and the radar scope.
        var storm = new GuiRect(screen.X + 12, screen.Y + 142, screen.Width - 24, 92);
        float seg = (storm.Width - 24 - 20) / 6;
        for (int i = 0; i < 6; i++)
        {
            var (mph, _) = Theme.EfScale[i];
            mesh.FillRect(new((storm.X + 12 + i * (seg + 4)) * s, (storm.Y + 56) * s, seg * s, 5 * s), t.HasFunnel && t.PeakWind >= mph ? Theme.Threat(mph + 1) : Theme.Track, 2.5f * s);
        }
        if (NearestStorm(frame) is { } cell && t.Distance > 0)
            DrawArrow(mesh, (storm.Right - 22) * s, (storm.Y + 16) * s, 7 * s, Bearing(frame.Camera.Position, cell.Position) - Heading(frame.Camera.Forward), Theme.Ink);
        float scope = Math.Clamp(screen.Height - 244 - 64 - 42, 140, 232);
        DrawGpuRadar(mesh, (int)((screen.X + (screen.Width - scope) / 2) * s), (int)((screen.Y + 244) * s), (int)(scope * s), frame, t.Campaign, s * .9f);
    }

    private static void DrawWeatherIcon(GuiDx12MeshBuilder mesh, float cx, float cy, float r, float s)
    {
        mesh.Disc(cx - r * .3f, cy - r * .3f, r * .55f, new(255, 214, 90));
        mesh.Disc(cx + r * .1f, cy + r * .25f, r * .5f, new(240, 246, 250));
        mesh.Disc(cx + r * .55f, cy + r * .3f, r * .4f, new(240, 246, 250));
        mesh.FillRect(new(cx - r * .3f, cy + r * .3f, r * 1.25f, r * .42f), new(240, 246, 250), r * .2f);
    }

    // ==================================================================== full world map (Life Beyond)
    private (GuiRect Panel, GuiRect Canvas, GuiRect Details, float Scale)? worldMapLayout;

    private GuiDocument BuildWorldMapDocument(int w, int h, StormChaseTelemetry t)
    {
        float s = HudScale(w, h), W = w / s, H = h / s;
        float pw = Math.Min(1160, W - 40), ph = Math.Min(720, H - 40);
        var panel = new GuiRect((W - pw) / 2, (H - ph) / 2, pw, ph);
        var canvas = new GuiRect(panel.X + 16, panel.Y + 112, pw - 16 - 16 - 360 - 12, ph - 112 - 92);
        var details = new GuiRect(canvas.Right + 12, canvas.Y, 360, canvas.Height + 42);
        worldMapLayout = (panel, canvas, details, s);
        var k = new HudKit(s); var root = new List<GuiElement> { new GuiOverlayPanel { Width = GuiLength.Pixels(w), Height = GuiLength.Pixels(h), BackgroundColor = new(2, 8, 14, 120) } };
        root.Add(k.Fill(panel, Theme.Glass, 14, Theme.GlassEdge));
        root.Add(k.Fill(new(panel.X + 1, panel.Y + 1, panel.Width - 2, 38), Theme.Header, 13));
        root.Add(k.Label(panel, "WORLD MAP", 18, 9, 300, 12, Theme.Ink, bold: true));
        root.Add(k.Button("map/close", "CLOSE  N", new(panel.Right - 108, panel.Y + 6, 94, 28), size: 8.5f));
        var towns = TownTabs(); var center = MapCenter ?? observerPosition;
        float tabWidth = (panel.Width - 32 - (towns.Count - 1) * 6) / Math.Max(1, towns.Count);
        for (int i = 0; i < towns.Count; i++)
        {
            var town = towns[i]; bool selected = StormChaseCareer.HorizontalDistance(center, town.Position) < town.RadiusMeters;
            float km = StormChaseCareer.HorizontalDistance(observerPosition, town.Position) / 1000;
            root.Add(new GuiButton { Id = "map/town/" + i, Text = $"{town.Name}\n{Capital(town.Kind)}  ·  {km:0.0} km", WrapMode = GuiTextWrapMode.Wrap,
                Translation = new((panel.X + 16 + i * (tabWidth + 6)) * s, (panel.Y + 50) * s), Width = GuiLength.Pixels(tabWidth * s), Height = GuiLength.Pixels(50 * s),
                TextStyle = new(Theme.Font, 8.5f * s, 1.15f, selected ? Theme.Ink : Theme.Muted), TextAlignment = GuiTextAlignment.Center, CornerRadius = 6 * s,
                BackgroundColor = selected ? new(31, 103, 92, 250) : new(22, 88, 120, 245), HoverBackgroundColor = new(37, 116, 153, 250), PressedBackgroundColor = new(6, 43, 57, 255),
                BorderColor = selected ? Theme.Warn : new(55, 104, 128, 220), BorderThickness = Math.Max(1, (selected ? 2 : 1) * s) });
        }
        root.Add(k.Fill(canvas, new(8, 20, 25, 255), 4));
        float y = canvas.Bottom + 8, x = canvas.X;
        foreach (var (id, label, width, on) in new (string, string, float, bool)[] { ("map/left", "<", 36, false), ("map/up", "^", 36, false), ("map/down", "v", 36, false), ("map/right", ">", 36, false),
            ("map/center", "CENTER", 70, MapCenter is null), ("map/roads", MapRoads ? "ROADS ON" : "ROADS OFF", 90, MapRoads), ("map/buildings", MapBuildings ? "BUILDINGS ON" : "BUILDINGS OFF", 110, MapBuildings),
            ("map/lots", MapLotsShown ? "LOTS ON" : "LOTS OFF", 80, MapLotsShown), ("map/out", "-", 36, false), ("map/in", "+", 36, false) })
        {
            root.Add(k.Button(id, label, new(x, y, width, 32), size: label.Length == 1 ? 11 : 8.5f, fill: on ? new(31, 103, 92, 250) : null));
            x += width + 5;
        }
        root.Add(k.Label(canvas, $"{FullZoomMeters[FullZoom] / 1000:0.#} km across  ·  zoom {FullZoomMeters.Length - FullZoom}/{FullZoomMeters.Length}", canvas.Width - 220, canvas.Height + 16, 220, 8, Theme.Muted, align: GuiTextAlignment.Right));

        // Details for the town the map is looking at, after Life Beyond's district panel.
        root.Add(k.Fill(details, new(11, 28, 35, 232), 8, new(55, 104, 128, 220)));
        var focus = World?.Settlements.OrderBy(town => StormChaseCareer.HorizontalDistance(center, town.Position)).FirstOrDefault();
        var lots = MapLots?.Invoke() ?? [];
        float dy = 14;
        void Heading(string text) { root.Add(k.Label(details, text, 16, dy, details.Width - 32, 9.5f, Theme.Warn, bold: true)); dy += 24; }
        void Row(string name, string value) { root.Add(k.Label(details, name, 16, dy, 200, 8.5f, Theme.Ink)); root.Add(k.Label(details, value, details.Width - 176, dy, 160, 8.5f, Theme.Ink, align: GuiTextAlignment.Right)); dy += 22; }
        root.Add(k.Label(details, (focus?.Name ?? "Open country").ToUpperInvariant(), 16, dy, details.Width - 32, 14, Theme.Ink, bold: true)); dy += 30;
        root.Add(k.Label(details, focus is null ? "" : $"{Capital(focus.Kind)}  ·  {StormChaseCareer.HorizontalDistance(observerPosition, focus.Position) / 1000:0.0} km from you", 16, dy, details.Width - 32, 8.5f, Theme.Muted)); dy += 30;
        Heading("SETTLEMENT");
        int buildingCount = focus is null ? 0 : World!.Buildings.Count(b => b.SettlementId == focus.Id);
        int stations = focus is null ? 0 : World!.GasStations.Count(g => g.SettlementId == focus.Id);
        Row("Buildings", buildingCount.ToString()); Row("Service stations", stations.ToString());
        Row("Radius", focus is null ? "—" : $"{focus.RadiusMeters:0} m");
        Heading("LIFECARE LOTS");
        var near = lots.Where(l => focus is null || StormChaseCareer.HorizontalDistance(new((l.MinimumX + l.MaximumX) / 2, 0, (l.MinimumZ + l.MaximumZ) / 2), focus.Position) < focus.RadiusMeters * 2).ToArray();
        Row("Lots here", near.Length.ToString());
        Row("Your lots", lots.Count(l => l.Own).ToString());
        Row("Built pieces (yours)", lots.Where(l => l.Own).Sum(l => l.Pieces).ToString());
        Heading("LEGEND");
        mapLegendTop = dy;
        foreach (var label in new[] { "You", "Active task", "Your lot", "Other lot", "Building", "Road" }) { root.Add(k.Label(details, label, 40, dy, 200, 8.5f, Theme.Ink)); dy += 21; }
        root.Add(k.Label(panel, "The map shows the district's roads, buildings and LifeCare lots. Weather radar is in your phone's Weather app.", 18, panel.Height - 30, panel.Width - 36, 8.5f, Theme.Muted));
        return new(new GuiOverlayPanel { Width = GuiLength.Pixels(w), Height = GuiLength.Pixels(h), Items = root });
    }

    private float mapLegendTop;

    private void DrawWorldMapInstruments(GuiDx12MeshBuilder mesh, RenderFrame frame)
    {
        if (worldMapLayout is not { } m || mapView is null) return;
        float s = m.Scale; var center = MapCenter ?? frame.Camera.Position;
        mapView.Draw(mesh, new(m.Canvas.X * s, m.Canvas.Y * s, m.Canvas.Width * s, m.Canvas.Height * s), center.X, center.Z, FullZoomMeters[FullZoom], Markers(frame),
            MapRoads, MapBuildings, MapLotsShown, s, cellPixels: 5);
        mesh.Text("N", (m.Canvas.X + m.Canvas.Width / 2 - 10) * s, (m.Canvas.Y + 6) * s, 20 * s, 16 * s, 9 * s, Theme.Ink, Theme.Bold, GuiTextAlignment.Center);
        float lx = (m.Details.X + 24) * s, ly = (m.Details.Y + mapLegendTop + 9) * s, r = 5 * s;
        mesh.Disc(lx, ly, r + 1.5f * s, new(255, 255, 255)); mesh.Disc(lx, ly, r, StormChaseMapView.PlayerBlue); ly += 21 * s;
        mesh.Line(lx, ly - r, lx + r, ly, StormChaseMapView.JobAmber, 2 * s); mesh.Line(lx + r, ly, lx, ly + r, StormChaseMapView.JobAmber, 2 * s);
        mesh.Line(lx, ly + r, lx - r, ly, StormChaseMapView.JobAmber, 2 * s); mesh.Line(lx - r, ly, lx, ly - r, StormChaseMapView.JobAmber, 2 * s); ly += 21 * s;
        mesh.FillRect(new(lx - r, ly - r, 2 * r, 2 * r), StormChaseMapView.LotTeal.WithAlpha(90), s); mesh.BorderRect(new(lx - r, ly - r, 2 * r, 2 * r), StormChaseMapView.LotTeal, s, s); ly += 21 * s;
        mesh.BorderRect(new(lx - r, ly - r, 2 * r, 2 * r), new(180, 200, 210), s, s); ly += 21 * s;
        mesh.Disc(lx, ly, r + s, StormChaseMapView.BuildingRing); mesh.Disc(lx, ly, r, StormChaseMapView.BuildingFill); ly += 21 * s;
        mesh.Line(lx - 2 * r, ly, lx + 2 * r, ly, StormChaseMapView.Sidewalk, 6 * s); mesh.Line(lx - 2 * r, ly, lx + 2 * r, ly, StormChaseMapView.Road, 4 * s);
    }

    private static string Capital(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    /// <summary>Panel helpers in design units, shared by the phone and the world map.</summary>
    private sealed class HudKit(float s)
    {
        public GuiLabel Label(GuiRect panel, string text, float x, float y, float width, float size, GuiColor color, bool bold = false, GuiTextAlignment align = GuiTextAlignment.Left) => new()
        {
            Text = text, Translation = new((panel.X + x) * s, (panel.Y + y) * s), Width = GuiLength.Pixels(width * s), Height = GuiLength.Pixels(size * 1.9f * s),
            TextAlignment = align, WrapMode = GuiTextWrapMode.NoWrap, Style = new(bold ? Theme.Bold : Theme.Font, size * s, 1.1f, color),
        };
        public GuiOverlayPanel Fill(GuiRect r, GuiColor color, float radius, GuiColor? border = null, float thickness = 1) => new()
        {
            Translation = new(r.X * s, r.Y * s), Width = GuiLength.Pixels(r.Width * s), Height = GuiLength.Pixels(r.Height * s),
            BackgroundColor = color, CornerRadius = radius * s, BorderColor = border ?? default, BorderThickness = border is null ? 0 : Math.Max(1, thickness * s),
        };
        public GuiButton Button(string id, string text, GuiRect r, bool selected = false, float size = 9f, GuiColor? fill = null) => new()
        {
            Id = id, Text = text, Translation = new(r.X * s, r.Y * s), Width = GuiLength.Pixels(r.Width * s), Height = GuiLength.Pixels(r.Height * s),
            TextStyle = new(Theme.Bold, size * s, 1, selected ? new GuiColor(8, 20, 24) : Theme.Ink), Padding = new(4 * s, Math.Max(1, (r.Height - size * 1.35f) / 2) * s),
            CornerRadius = 7 * s, TextAlignment = GuiTextAlignment.Center,
            BackgroundColor = selected ? Theme.Accent : fill ?? new(22, 88, 120, 245), HoverBackgroundColor = selected ? new(120, 238, 214) : new(37, 116, 153, 250),
            PressedBackgroundColor = new(6, 43, 57, 255), BorderColor = selected ? Theme.Accent : new(78, 150, 196, 140), BorderThickness = Math.Max(1, s),
        };
    }
}
