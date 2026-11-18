using Engine.GUI;
using Shared.Contracts.Rendering;
using Shared.Contracts.World;

/// <summary>
/// The LifeCare HUD: about the player's avatar and life, after Life Beyond's panels and Tiny Clinic's tiles and hotbar.
/// Layout is authored in design units (about 1500×850) and converted to real pixels so type stays sharp. Panels are
/// Engine.GUI elements; the compass, icons, bars, the map and the phone's radar are GPU geometry drawn in
/// <see cref="DrawInstruments"/>. Weather lives in the phone's Weather app, not on the HUD.
/// </summary>
internal sealed partial class StormChaseHud
{
    internal static class Theme
    {
        public const string Font = "Bahnschrift", Bold = "Bahnschrift SemiBold";
        // Deep navy glass with a cool cyan edge: Life Beyond's panels in Tiny Clinic's blue.
        public static readonly GuiColor Glass = new(7, 17, 28, 224), GlassEdge = new(78, 150, 196, 96), Header = new(12, 30, 48, 240);
        public static readonly GuiColor Ink = new(236, 244, 248), Muted = new(160, 182, 196), Faint = new(112, 136, 152);
        public static readonly GuiColor Accent = new(84, 226, 198), Sky = new(64, 180, 255), Warn = new(255, 198, 74), Danger = new(255, 88, 94), Good = new(88, 214, 140);
        public static readonly GuiColor Track = new(255, 255, 255, 34), KeyCap = new(255, 255, 255, 22), Tile = new(18, 58, 96, 240), TileEdge = new(110, 190, 240, 170);
        public static readonly (float Mph, string Label)[] EfScale = [(65, "EF0"), (86, "EF1"), (111, "EF2"), (136, "EF3"), (166, "EF4"), (200, "EF5")];

        public static GuiColor Threat(float mph) => mph < 40 ? new(120, 205, 196) : mph < 65 ? new(118, 214, 124)
            : mph < 111 ? new(244, 214, 72) : mph < 136 ? new(255, 158, 62) : mph < 166 ? new(255, 86, 90) : new(234, 112, 238);

        public static GuiColor Risk(float risk) => risk < .2f ? new(118, 214, 124) : risk < .4f ? new(244, 214, 72)
            : risk < .6f ? new(255, 158, 62) : risk < .8f ? new(255, 86, 90) : new(234, 112, 238);
    }

    /// <summary>Design-space rectangles shared by the GUI document and the GPU instruments.</summary>
    private sealed record HudLayout(float Scale, float Width, float Height, GuiRect Character, GuiRect Compass, GuiRect Clock, GuiRect Dock,
        GuiRect Task, GuiRect Notes, GuiRect Minimap, GuiRect MinimapCanvas, GuiRect Hotbar, GuiRect Status, GuiRect? Vehicle, GuiRect Phone, GuiRect Chat, GuiRect Inventory, GuiRect Container);


    private static float HudScale(int w, int h) => Math.Clamp(Math.Min(w / 1500f, h / 850f), .78f, 2.4f);

    private HudLayout ComputeLayout(int w, int h, StormChaseTelemetry t)
    {
        float s = HudScale(w, h), W = w / s, H = h / s, m = 16;
        // Everything that sits at the bottom stays above the dock's live top edge, so opening the dock pushes it up.
        float dockTop = H - DockReserve(), floor = dockTop - 10;
        var dock = new GuiRect(0, dockTop, W, H - dockTop);
        var character = Place("character", new GuiRect(m, m, 344, 170), W, floor);
        var compass = new GuiRect(W / 2 - 240, m, 480, 34);
        var clock = new GuiRect(W / 2 - 210, compass.Bottom + 6, 420, 60);
        var homeTask = new GuiRect(W - m - 344, m, 344, 124);
        var task = Place("task", homeTask, W, floor);
        var notes = Place("notes", new GuiRect(W - m - 344, homeTask.Bottom + 10, 344, 136), W, floor);
        var minimap = Place("minimap", new GuiRect(W - m - 320, floor - 278, 320, 278), W, floor);
        var canvas = new GuiRect(minimap.X + 12, minimap.Y + 44, 296, 186);
        var hotbar = new GuiRect(W / 2 - 325, floor - 78, 650, 78);
        var status = new GuiRect(hotbar.X, hotbar.Y - 38, hotbar.Width, 30);
        GuiRect? vehicle = t.CameraMode == "DRIVING" && t.Campaign is not null ? Place("vehicle", new GuiRect(m, floor - 136, 300, 136), W, floor) : null;
        var phone = Place("phone", new GuiRect(W - m - 312, homeTask.Bottom + 12, 312, Math.Min(640, floor - homeTask.Bottom - 12)), W, floor);
        var chat = Place("chat", new GuiRect(m, floor - 136 - 12 - 270, 400, 270), W, floor);
        var inventory = Place("inventory", new GuiRect(W / 2 - 440, 140, 430, 360), W, floor);
        var container = Place("container", new GuiRect(W / 2 + 10, 140, 430, 360), W, floor);
        return new(s, W, H, character, compass, clock, dock, task, notes, minimap, canvas, hotbar, status, vehicle, phone, chat, inventory, container);
    }

    /// <summary>An invisible one-pixel fill that marks where the top layer (opened windows) starts in the draw commands.</summary>
    private static readonly GuiColor TopLayerMarker = new(1, 2, 3, 1);
    private HudLayout? layoutForInstruments;
    private StormChaseTelemetry? telemetryForInstruments;

    private GuiDocument BuildChaseDocument(int w, int h, StormChaseTelemetry t)
    {
        var L = ComputeLayout(w, h, t);
        layoutForInstruments = L; telemetryForInstruments = t;
        float s = L.Scale;
        var c = t.Campaign;
        var root = new List<GuiElement>();
        if (c?.ShopOpen == true)
            return new(new GuiOverlayPanel { Width = GuiLength.Pixels(w), Height = GuiLength.Pixels(h), Items = BuildShop(w, h, s, c).ToList() });
        TrackNotifications(t);
        dragHandles.Clear();
        if (BuildMode && WorldOverlay is { } buildPanels)
            return new(new GuiOverlayPanel { Width = GuiLength.Pixels(w), Height = GuiLength.Pixels(h), Items = buildPanels(w, h).ToList() });
        var kit = new HudKit(s, composer);

        GuiLabel Label(GuiRect panel, string text, float x, float y, float width, float size, GuiColor color,
            bool bold = false, GuiTextAlignment align = GuiTextAlignment.Left) => new()
        {
            Text = text, Translation = new((panel.X + x) * s, (panel.Y + y) * s), Width = GuiLength.Pixels(width * s),
            Height = GuiLength.Pixels(size * 1.9f * s), TextAlignment = align, WrapMode = GuiTextWrapMode.NoWrap,
            Style = new(bold ? Theme.Bold : Theme.Font, size * s, 1.1f, color),
        };
        GuiLabel Wrapped(GuiRect panel, string text, float x, float y, float width, float height, float size, GuiColor color) => new()
        {
            Text = text, Translation = new((panel.X + x) * s, (panel.Y + y) * s), Width = GuiLength.Pixels(width * s), Height = GuiLength.Pixels(height * s),
            WrapMode = GuiTextWrapMode.Wrap, Style = new(Theme.Font, size * s, 1.18f, color),
        };
        GuiOverlayPanel Fill(GuiRect r, GuiColor color, float radius, GuiColor? border = null, float thickness = 1) => new()
        {
            Translation = new(r.X * s, r.Y * s), Width = GuiLength.Pixels(r.Width * s), Height = GuiLength.Pixels(r.Height * s),
            BackgroundColor = color, CornerRadius = radius * s, BorderColor = border ?? default, BorderThickness = border is null ? 0 : Math.Max(1, thickness * s),
        };
        // Life Beyond panel: a header strip with the title in caps and a thin accent rule.
        void Panel(GuiRect r, string title, GuiColor? accent = null, string? right = null)
        {
            root.Add(Fill(r, Theme.Glass, 12, Theme.GlassEdge));
            root.Add(Fill(new(r.X + 1, r.Y + 1, r.Width - 2, 30), Theme.Header, 11));
            root.Add(Fill(new(r.X + 14, r.Y + 11, 8, 8), accent ?? Theme.Sky, 4));
            root.Add(Label(r, title, 30, 7, r.Width - 140, 10, Theme.Ink, bold: true));
            if (right is not null) root.Add(Label(r, right, r.Width - 160, 8, 146, 8.5f, accent ?? Theme.Muted, bold: true, align: GuiTextAlignment.Right));
        }
        GuiButton Button(string id, string text, GuiRect r, bool selected = false, float size = 9f, GuiColor? fill = null) => new()
        {
            Id = id, Text = text, Translation = new(r.X * s, r.Y * s), Width = GuiLength.Pixels(r.Width * s), Height = GuiLength.Pixels(r.Height * s),
            TextStyle = new(Theme.Bold, size * s, 1, selected ? new GuiColor(8, 20, 24) : Theme.Ink), Padding = new(6 * s, Math.Max(1, (r.Height - size * 1.35f) / 2) * s),
            CornerRadius = 7 * s, TextAlignment = GuiTextAlignment.Center,
            BackgroundColor = selected ? Theme.Accent : fill ?? new(22, 88, 120, 245), HoverBackgroundColor = selected ? new(120, 238, 214) : new(37, 116, 153, 250),
            PressedBackgroundColor = new(6, 43, 57, 255), BorderColor = selected ? Theme.Accent : new(78, 150, 196, 140), BorderThickness = Math.Max(1, s),
        };

        // ---------------------------------------------------------------- character
        var ch = L.Character;
        var job = StormChaseJobs.Get(c?.Job);
        if (CharacterOpen) {
        Panel(ch, "CHARACTER", Theme.Accent, c is null ? null : job.Department.ToUpperInvariant()); RegisterHandle("character", ch);
        root.Add(Fill(new(ch.X + 14, ch.Y + 42, 58, 58), new(16, 46, 70, 250), 10, Theme.TileEdge));
        string name = string.IsNullOrWhiteSpace(PlayerName) ? "Citizen" : PlayerName;
        root.Add(Label(new(ch.X + 14, ch.Y + 42, 58, 58), name[..1].ToUpperInvariant(), 0, 12, 58, 20, Theme.Ink, bold: true, align: GuiTextAlignment.Center));
        root.Add(Label(ch, name, 84, 40, 246, 17, Theme.Ink, bold: true));
        root.Add(Label(ch, c is null ? "Observer" : $"{job.Title}  ·  {(t.CameraMode == "DRIVING" ? "Driving" : "On foot")}", 84, 70, 246, 9.5f, Theme.Muted));
        root.Add(Label(ch, NearestPlace(c) is { } place ? "Near " + place : "Open country", 84, 88, 246, 9.5f, Theme.Sky));
        if (c is not null)
        {
            (string Name, string Value, GuiColor Color)[] stats = [("CASH", $"${c.Cash:#,0}", Theme.Accent), ("REPUTATION", $"{c.Popularity}", Theme.Ink), ("DAY", $"{c.Day}", Theme.Ink)];
            for (int i = 0; i < stats.Length; i++)
            {
                var cell = new GuiRect(ch.X + 14 + i * 108, ch.Y + 112, 100, 44);
                root.Add(Fill(cell, new(255, 255, 255, 10), 8));
                root.Add(Label(cell, stats[i].Name, 10, 5, 84, 7.5f, Theme.Faint));
                root.Add(Label(cell, stats[i].Value, 10, 19, 84, 12.5f, stats[i].Color, bold: true));
            }
        }

        }

        // ---------------------------------------------------------------- compass + clock (weather is one quiet line; the rest is on the phone)
        root.Add(Fill(L.Compass, Theme.Glass, 17, Theme.GlassEdge));
        var clockRect = L.Clock;
        root.Add(Fill(clockRect, Theme.Glass, 12, Theme.GlassEdge));
        float hour = c?.Hour ?? 12;
        root.Add(Label(clockRect, ClockText(hour), 18, 8, 160, 18, Theme.Ink, bold: true));
        root.Add(Label(clockRect, c is null ? t.TimeOfDay : $"Day {c.Day}  ·  {c.Forecast}{TemperatureText(c)}", 18, 38, 220, 8.5f, Theme.Muted));
        var tile = (X: (int)MathF.Floor(observerPosition.X / 4), Z: (int)MathF.Floor(observerPosition.Z / 4));
        root.Add(Label(clockRect, NearestPlace(c) ?? "Open country", 214, 10, 190, 11, Theme.Ink, bold: true, align: GuiTextAlignment.Right));
        root.Add(Label(clockRect, $"Tile {tile.X}, {tile.Z}", 214, 32, 190, 8.5f, Theme.Faint, align: GuiTextAlignment.Right));

        // ---------------------------------------------------------------- bottom dock
        BuildDock(root, L, kit);

        // ---------------------------------------------------------------- active task
        var tr = L.Task;
        if (TaskOpen) { RegisterHandle("task", tr);
        if (c?.Mission is { } mission)
        {
            float meters = StormChaseCareer.HorizontalDistance(observerPosition, new(mission.TargetPosition.X, mission.TargetPosition.Y, mission.TargetPosition.Z));
            Panel(tr, "ACTIVE TASK", Theme.Warn, $"STEP {Math.Min(mission.Step + 1, mission.StepCount)}/{mission.StepCount}");
            root.Add(Label(tr, mission.Title, 14, 38, 316, 14, Theme.Ink, bold: true));
            root.Add(Wrapped(tr, mission.Objective, 14, 64, 240, 34, 8.5f, Theme.Muted));
            root.Add(Label(tr, meters >= 1000 ? $"{meters / 1000:0.0} km" : $"{meters:0} m", 254, 64, 76, 13, Theme.Warn, bold: true, align: GuiTextAlignment.Right));
            root.Add(Label(tr, mission.RequiredWorkSeconds > 0 ? $"WORK {mission.WorkSeconds:0}/{mission.RequiredWorkSeconds:0} s  ·  H TO WORK" : "H INTERACT  ·  O JOB BOARD", 14, 98, 316, 7.5f, Theme.Faint));
        }
        else
        {
            Panel(tr, "ACTIVE TASK", Theme.Warn);
            root.Add(Label(tr, c is null ? "Explore" : "No active job", 14, 40, 316, 14, Theme.Ink, bold: true));
            root.Add(Wrapped(tr, c is null ? "Look around the district." : $"Your {job.Department.ToLowerInvariant()} job board has calls waiting. Press O to choose one.", 14, 66, 316, 40, 8.5f, Theme.Muted));
        }

        }

        // ---------------------------------------------------------------- notifications (phone-style toasts)
        var nr = L.Notes;
        if (NotesOpen) { RegisterHandle("notes", nr);
        Panel(nr, "NOTIFICATIONS", Theme.Sky, notifications.Count == 0 ? null : notifications.Count.ToString());
        float ny = 38;
        foreach (var note in notifications.Take(3))
        {
            var item = new GuiRect(nr.X + 10, nr.Y + ny, nr.Width - 20, 30);
            root.Add(Fill(item, note.Color.WithAlpha(26), 7));
            root.Add(Fill(new(item.X, item.Y + 4, 3, 22), note.Color, 2));
            root.Add(Label(item, note.Time, 10, 7, 54, 8, Theme.Faint));
            root.Add(Label(item, note.Text, 64, 6, item.Width - 72, 8.5f, Theme.Ink));
            ny += 32;
        }
        if (notifications.Count == 0) root.Add(Label(nr, "Nothing new.", 14, 42, 300, 9, Theme.Faint));
        }

        // ---------------------------------------------------------------- minimap (Life Beyond)
        if (MinimapOpen)
        {
            var mm = L.Minimap; RegisterHandle("minimap", mm);
            Panel(mm, "MINIMAP", Theme.Accent, "LIVE MAP");
            root.Add(Fill(L.MinimapCanvas, new(8, 20, 25, 255), 4));
            root.Add(Button("map/mini-in", "+", new(mm.X + 12, mm.Bottom - 40, 32, 28), size: 12));
            root.Add(Button("map/mini-out", "-", new(mm.X + 48, mm.Bottom - 40, 32, 28), size: 12));
            root.Add(Label(mm, $"Zoom {MiniZoom + 1}/{MiniZoomMeters.Length}  ·  NORTH ^", 88, mm.Height - 34, 110, 7.5f, Theme.Muted));
            root.Add(Button("hud/map", "OPEN FULL MAP", new(mm.Right - 128, mm.Bottom - 40, 116, 28), size: 8.5f));
        }

        // ---------------------------------------------------------------- vehicle (only while driving)
        if (L.Vehicle is { } k && c is not null)
        {
            Panel(k, "VEHICLE", Theme.Accent, c.VehicleName.ToUpperInvariant()); RegisterHandle("vehicle", k);
            root.Add(Label(k, $"{c.SpeedMph:0}", 14, 50, 110, 22, Theme.Ink, bold: true, align: GuiTextAlignment.Center));
            root.Add(Label(k, "MPH", 14, 88, 110, 7.5f, Theme.Muted, align: GuiTextAlignment.Center));
            root.Add(Label(k, "FUEL", 136, 44, 60, 7.5f, Theme.Faint));
            root.Add(Label(k, $"{c.Fuel:0} / {c.FuelCapacity:0} L", 180, 42, 106, 8.5f, c.Fuel < c.FuelCapacity * .2f ? Theme.Warn : Theme.Ink, align: GuiTextAlignment.Right));
            root.Add(Label(k, "CONDITION", 136, 74, 80, 7.5f, Theme.Faint));
            root.Add(Label(k, $"{c.Health:0}%", 206, 72, 80, 8.5f, c.Health < 35 ? Theme.Danger : Theme.Ink, align: GuiTextAlignment.Right));
            root.Add(Label(k, c.Anchored ? "ANCHORED  ·  K RELEASE" : c.Refueling ? "REFUELING  ·  R STOP" : "E EXIT  ·  K ANCHOR", 136, 104, 150, 7.5f, c.Anchored || c.Refueling ? Theme.Warn : Theme.Faint));
        }

        // ---------------------------------------------------------------- status pill + Tiny Clinic hotbar
        var st = L.Status;
        string message = LotStatus is { Length: > 0 } lotStatus ? lotStatus : c?.Message ?? "";
        root.Add(Fill(st, Theme.Glass, 15, Theme.GlassEdge));
        root.Add(Label(st, string.IsNullOrWhiteSpace(message) ? "Welcome to LifeCare." : message, 16, 7, st.Width - 200, 9, Theme.Ink));
        root.Add(Label(st, HintText(t), st.Width - 200, 8, 186, 7.5f, Theme.Faint, align: GuiTextAlignment.Right));
        var hb = L.Hotbar;
        root.Add(Fill(hb, Theme.Glass, 12, Theme.GlassEdge));
        var slots = Hotbar ?? DefaultHotbar;
        for (int i = 0; i < 10; i++)
        {
            var r = new GuiRect(hb.X + 8 + i * 64, hb.Y + 8, 58, 62);
            var slot = i < slots.Count ? slots[i] : new HudSlot("", false);
            root.Add(Fill(r, slot.Selected ? new(26, 92, 140, 250) : Theme.Tile, 9, slot.Selected ? Theme.Accent : Theme.TileEdge, slot.Selected ? 2 : 1));
            root.Add(Label(r, i == 9 ? "0" : (i + 1).ToString(), 5, 2, 20, 7.5f, Theme.Ink));
            if (slot.Label.Length > 0) root.Add(Label(r, slot.Label.ToUpperInvariant(), 0, 44, 58, 7, Theme.Ink, bold: true, align: GuiTextAlignment.Center));
            else root.Add(Label(r, "+", 0, 16, 58, 15, Theme.Faint, align: GuiTextAlignment.Center));
        }

        // ---------------------------------------------------------------- windows the player opens (top layer)
        root.Add(new GuiOverlayPanel { Width = GuiLength.Pixels(1), Height = GuiLength.Pixels(1), BackgroundColor = TopLayerMarker });
        if (ChatOpen) { BuildChat(root, kit, L.Chat); RegisterHandle("chat", L.Chat); }
        if (InventoryOpen) { BuildInventory(root, kit, L.Inventory, t); RegisterHandle("inventory", L.Inventory); }
        if (Container is { } openBox) { BuildContainer(root, kit, L.Container, openBox); RegisterHandle("container", L.Container); }
        if (PhoneOpen) { BuildPhone(root, L, t); dragHandles.Add(("phone", new(L.Phone.X + 30, L.Phone.Y, L.Phone.Width - 90, 40))); }
        if (CursorMode && !ChatTyping) root.Add(Label(new(L.Width / 2 - 200, L.Status.Y - 30, 400, 22), "CURSOR MODE  ·  DRAG WINDOWS BY THEIR TITLE  ·  ALT TO RETURN", 0, 2, 400, 8, Theme.Accent, bold: true, align: GuiTextAlignment.Center));

        if (WorldOverlay is { } overlay) root.AddRange(overlay(w, h));
        return new(new GuiOverlayPanel { Width = GuiLength.Pixels(w), Height = GuiLength.Pixels(h), Items = root });
    }

    private static string ClockText(float hour)
    {
        int minutes = (int)(((hour % 24) + 24) % 24 * 60), h = minutes / 60, mm = minutes % 60;
        return $"{(h % 12 == 0 ? 12 : h % 12)}:{mm:00} {(h < 12 ? "AM" : "PM")}";
    }

    private static string TemperatureText(StormChaseCampaignTelemetry c) =>
        c.Atmosphere is { } a ? $"  ·  {a.Diagnostics.SurfaceTemperatureCelsius * 9 / 5 + 32:0}°F" : "";

    private string? NearestPlace(StormChaseCampaignTelemetry? c)
    {
        var best = c?.Settlements.OrderBy(town => StormChaseCareer.HorizontalDistance(observerPosition, town.Position)).FirstOrDefault();
        return best is null ? null : StormChaseCareer.HorizontalDistance(observerPosition, best.Position) < Math.Max(1200, best.RadiusMeters * 1.6f) ? best.Name : null;
    }

    private static string HintText(StormChaseTelemetry t) => t.CameraMode == "DRIVING"
        ? "W/S DRIVE  ·  E EXIT  ·  ALT CURSOR"
        : "E VEHICLE  ·  H INTERACT  ·  ALT CURSOR";

    private IEnumerable<GuiElement> BuildShop(int w, int h, float s, StormChaseCampaignTelemetry c)
    {
        float W = w / s, H = h / s, wide = Math.Min(620, W - 40), tall = Math.Min(560, H - 40);
        float x = (W - wide) / 2, y = (H - tall) / 2;
        var items = new List<GuiElement>
        {
            new GuiOverlayPanel { Width = GuiLength.Pixels(w), Height = GuiLength.Pixels(h), BackgroundColor = new(2, 8, 14, 150) },
            new GuiOverlayPanel { Translation = new(x * s, y * s), Width = GuiLength.Pixels(wide * s), Height = GuiLength.Pixels(tall * s),
                BackgroundColor = new(9, 16, 23, 244), BorderColor = Theme.GlassEdge, BorderThickness = Math.Max(1, s), CornerRadius = 14 * s },
        };
        GuiLabel Text(string text, float tx, float ty, float width, float size, GuiColor color, bool bold = false, GuiTextAlignment align = GuiTextAlignment.Left) => new()
        {
            Text = text, Translation = new((x + tx) * s, (y + ty) * s), Width = GuiLength.Pixels(width * s), Height = GuiLength.Pixels(size * 1.9f * s),
            WrapMode = GuiTextWrapMode.NoWrap, TextAlignment = align, Style = new(bold ? Theme.Bold : Theme.Font, size * s, 1.1f, color),
        };
        items.Add(Text("FIELD SUPPLY & GARAGE", 28, 24, 360, 16, Theme.Ink, bold: true));
        items.Add(Text($"${c.Cash:#,0.00}", wide - 228, 26, 200, 14, Theme.Accent, bold: true, GuiTextAlignment.Right));
        items.Add(Text("Equipment and vehicles are sold at city and town service stations.", 28, 58, wide - 56, 9, Theme.Muted));
        float rowTop = 92, row = Math.Min(62, (tall - 172) / StormChaseCareer.Offers.Length);
        for (int i = 0; i < StormChaseCareer.Offers.Length; i++)
        {
            var offer = StormChaseCareer.Offers[i];
            bool selected = i == c.ShopSelection;
            float top = rowTop + i * row;
            items.Add(new GuiButton
            {
                Id = "shop/offer/" + i, Text = "", Translation = new((x + 24) * s, (y + top) * s), Width = GuiLength.Pixels((wide - 48) * s),
                Height = GuiLength.Pixels((row - 8) * s), CornerRadius = 10 * s, BorderThickness = Math.Max(1, s),
                BackgroundColor = selected ? new(84, 226, 198, 34) : new(255, 255, 255, 10), HoverBackgroundColor = new(255, 255, 255, 26),
                PressedBackgroundColor = new(84, 226, 198, 70), BorderColor = selected ? Theme.Accent : new(255, 255, 255, 26),
            });
            items.Add(Text(offer.Name, 42, top + 8, wide - 240, 11.5f, Theme.Ink, bold: true));
            bool affordable = offer.Price <= c.Cash;
            items.Add(Text(offer.Price == 0 ? "FREE" : $"${offer.Price:#,0}", wide - 176, top + 8, 134, 11.5f,
                !affordable ? Theme.Danger : selected ? Theme.Accent : Theme.Muted, bold: true, GuiTextAlignment.Right));
            if (row >= 50) items.Add(Text(offer.Description, 42, top + 30, wide - 90, 8.5f, Theme.Muted));
        }
        GuiButton Action(string id, string label, float bx, float width, bool primary) => new()
        {
            Id = id, Text = label, Translation = new((x + bx) * s, (y + tall - 62) * s), Width = GuiLength.Pixels(width * s), Height = GuiLength.Pixels(38 * s),
            CornerRadius = 19 * s, TextStyle = new(Theme.Bold, 10 * s, 1, primary ? new GuiColor(8, 20, 24) : Theme.Ink), BorderThickness = Math.Max(1, s),
            BackgroundColor = primary ? Theme.Accent : new(255, 255, 255, 18), HoverBackgroundColor = primary ? new(120, 238, 214) : new(255, 255, 255, 40),
            PressedBackgroundColor = new(84, 226, 198, 160), BorderColor = primary ? Theme.Accent : new(255, 255, 255, 40),
        };
        float half = (wide - 60) / 2;
        items.Add(Action("shop/buy", "BUY / SELECT   ENTER", 24, half, true));
        items.Add(Action("shop/close", "CLOSE   ESC", 36 + half, half, false));
        return items;
    }

    // ==================================================================== GPU instruments
    private void DrawInstruments(GuiDx12MeshBuilder mesh, RenderFrame frame)
    {
        if (layoutForInstruments is not { } L || telemetryForInstruments is not { } t) return;
        float s = L.Scale;
        var c = t.Campaign;
        float X(float v) => v * s;

        // Compass strip with heading ticks and the job marker.
        {
            var r = L.Compass;
            float heading = Heading(frame.Camera.Forward), cx = r.X + r.Width / 2, span = 140;
            for (int d = -180; d <= 540; d += 5)
            {
                float rel = d - heading; rel = (rel + 540) % 360 - 180;
                if (MathF.Abs(rel) > span / 2) continue;
                float x = cx + rel / span * (r.Width - 40);
                bool major = d % 45 == 0, mid = d % 15 == 0;
                mesh.Line(X(x), X(r.Y + (major ? 5 : mid ? 9 : 12)), X(x), X(r.Y + 15), major ? Theme.Ink : Theme.Faint, Math.Max(1, s * (major ? 1.6f : 1)));
                if (major)
                {
                    string name = Cardinal(d);
                    mesh.Text(name, X(x - 20), X(r.Y + 16), X(40), X(16), (name.Length == 1 ? 10 : 8) * s, name == "N" ? Theme.Accent : Theme.Ink, Theme.Bold, GuiTextAlignment.Center);
                }
            }
            if (c?.Mission is { } mission)
            {
                float rel = Bearing(frame.Camera.Position, new(mission.TargetPosition.X, mission.TargetPosition.Y, mission.TargetPosition.Z)) - heading;
                rel = (rel + 540) % 360 - 180;
                float x = cx + Math.Clamp(rel / span, -.5f, .5f) * (r.Width - 40);
                DrawArrow(mesh, X(x), X(r.Y + 2), X(6), 180, Theme.Warn);
            }
            mesh.FillRect(new(X(cx - 1), X(r.Y + 3), Math.Max(2, X(2)), X(r.Height - 6)), Theme.Accent.WithAlpha(160));
        }

        DrawDockInstruments(mesh, L);

        // Mission progress.
        if (TaskOpen && c?.Mission is { } m)
        {
            float progress = m.RequiredWorkSeconds > 0 ? m.WorkSeconds / m.RequiredWorkSeconds : (float)m.Step / Math.Max(1, m.StepCount);
            Bar(mesh, L.Task.X + 14, L.Task.Y + 114, 316, progress, Theme.Warn, s);
        }

        // The Life Beyond minimap.
        if (MinimapOpen && World is not null)
            mapView!.Draw(mesh, new(X(L.MinimapCanvas.X), X(L.MinimapCanvas.Y), X(L.MinimapCanvas.Width), X(L.MinimapCanvas.Height)),
                frame.Camera.Position.X, frame.Camera.Position.Z, MiniZoomMeters[MiniZoom], Markers(frame), s: s);

        // Vehicle speed arc and bars, only while driving.
        if (L.Vehicle is { } k && c is not null)
        {
            float gx = k.X + 69, gy = k.Y + 80, radius = 38, maxMph = 140;
            Arc(mesh, X(gx), X(gy), X(radius), 135, 405, Theme.Track, X(6));
            float value = Math.Clamp(c.SpeedMph / maxMph, 0, 1);
            if (value > .002f) Arc(mesh, X(gx), X(gy), X(radius), 135, 135 + 270 * value, c.SpeedMph > 100 ? Theme.Warn : Theme.Accent, X(6));
            Bar(mesh, k.X + 136, k.Y + 62, 150, c.FuelCapacity <= 0 ? 0 : c.Fuel / c.FuelCapacity, c.Fuel < c.FuelCapacity * .2f ? Theme.Warn : Theme.Accent, s);
            Bar(mesh, k.X + 136, k.Y + 92, 150, c.Health / 100, c.Health < 35 ? Theme.Danger : c.Health < 65 ? Theme.Warn : Theme.Accent, s);
        }


    }

    private static void DrawIcon(GuiDx12MeshBuilder mesh, string id, float x, float y, float r, float s)
    {
        var ink = Theme.Ink; float w = Math.Max(1.5f, 1.8f * s);
        switch (id)
        {
            case "hud/map":
                mesh.Line(x - r, y - r * .7f, x - r * .33f, y - r, ink, w); mesh.Line(x - r * .33f, y - r, x + r * .33f, y - r * .7f, ink, w);
                mesh.Line(x + r * .33f, y - r * .7f, x + r, y - r, ink, w); mesh.Line(x + r, y - r, x + r, y + r * .7f, ink, w);
                mesh.Line(x + r, y + r * .7f, x + r * .33f, y + r, ink, w); mesh.Line(x + r * .33f, y + r, x - r * .33f, y + r * .7f, ink, w);
                mesh.Line(x - r * .33f, y + r * .7f, x - r, y + r, ink, w); mesh.Line(x - r, y + r, x - r, y - r * .7f, ink, w);
                mesh.Line(x - r * .33f, y - r, x - r * .33f, y + r * .7f, ink.WithAlpha(150), w * .7f); mesh.Line(x + r * .33f, y - r * .7f, x + r * .33f, y + r, ink.WithAlpha(150), w * .7f);
                mesh.Disc(x + r * .1f, y - r * .1f, r * .22f, Theme.Danger); break;
            case "hud/jobs":
                mesh.BorderRect(new(x - r * .75f, y - r * .85f, r * 1.5f, r * 1.85f), ink, w, r * .15f);
                mesh.FillRect(new(x - r * .35f, y - r * 1.05f, r * .7f, r * .4f), Theme.Warn, r * .1f);
                for (int i = 0; i < 3; i++) mesh.Line(x - r * .45f, y - r * .2f + i * r * .4f, x + r * .45f, y - r * .2f + i * r * .4f, ink.WithAlpha(190), w * .8f);
                break;
            case "hud/phone":
                mesh.BorderRect(new(x - r * .55f, y - r, r * 1.1f, r * 2), ink, w, r * .25f);
                mesh.FillRect(new(x - r * .38f, y - r * .72f, r * .76f, r * 1.25f), Theme.Sky.WithAlpha(200), r * .08f);
                mesh.Disc(x, y + r * .75f, r * .1f, ink); break;
            case "hud/build":
                mesh.Line(x - r * .8f, y + r * .8f, x + r * .35f, y - r * .35f, Theme.Warn, w * 1.6f);
                mesh.FillRect(new(x + r * .05f, y - r * .95f, r * .9f, r * .5f), ink, r * .1f); break;
            case "hud/shop":
                mesh.BorderRect(new(x - r * .8f, y - r * .4f, r * 1.6f, r * 1.3f), ink, w, r * .15f);
                mesh.Circle(x, y - r * .45f, r * .45f, ink, w); mesh.FillRect(new(x - r * .8f, y - r * .4f, r * 1.6f, r * .25f), Theme.Good, r * .1f); break;
            case "hud/character":
                mesh.Circle(x, y - r * .45f, r * .38f, ink, w);
                mesh.Line(x - r * .75f, y + r * .9f, x - r * .55f, y + r * .2f, ink, w); mesh.Line(x - r * .55f, y + r * .2f, x + r * .55f, y + r * .2f, ink, w);
                mesh.Line(x + r * .55f, y + r * .2f, x + r * .75f, y + r * .9f, ink, w); break;
            case "hud/task":
                mesh.Line(x - r * .6f, y - r, x - r * .6f, y + r, ink, w);
                mesh.FillRect(new(x - r * .6f, y - r, r * 1.3f, r * .9f), Theme.Warn, r * .1f); break;
            case "hud/notes":
                mesh.Circle(x, y - r * .1f, r * .65f, ink, w); mesh.FillRect(new(x - r * .85f, y + r * .35f, r * 1.7f, w), ink, w / 2);
                mesh.Disc(x, y + r * .7f, r * .18f, ink); mesh.Disc(x + r * .55f, y - r * .6f, r * .22f, Theme.Danger); break;
            case "hud/minimap":
                mesh.Circle(x, y, r * .9f, ink, w);
                mesh.Line(x, y - r * .6f, x - r * .3f, y + r * .3f, Theme.Accent, w); mesh.Line(x, y - r * .6f, x + r * .3f, y + r * .3f, Theme.Accent, w); break;
            case "hud/home":
                mesh.Line(x - r, y - r * .05f, x, y - r * .9f, ink, w); mesh.Line(x, y - r * .9f, x + r, y - r * .05f, ink, w);
                mesh.BorderRect(new(x - r * .7f, y - r * .15f, r * 1.4f, r * 1.05f), ink, w); mesh.FillRect(new(x - r * .18f, y + r * .35f, r * .36f, r * .55f), Theme.Good, r * .05f); break;
            case "hud/inventory":
                mesh.BorderRect(new(x - r * .8f, y - r * .5f, r * 1.6f, r * 1.4f), ink, w, r * .3f);
                mesh.Circle(x, y - r * .55f, r * .38f, ink, w);
                mesh.FillRect(new(x - r * .45f, y + r * .1f, r * .9f, r * .4f), Theme.Warn, r * .1f); break;
            case "hud/chat":
                mesh.BorderRect(new(x - r * .9f, y - r * .75f, r * 1.8f, r * 1.2f), ink, w, r * .35f);
                mesh.Line(x - r * .4f, y + r * .45f, x - r * .7f, y + r * .9f, ink, w); mesh.Line(x - r * .7f, y + r * .9f, x, y + r * .45f, ink, w);
                for (int i = -1; i <= 1; i++) mesh.Disc(x + i * r * .45f, y - r * .15f, r * .12f, Theme.Sky);
                break;
            default:
                for (int i = -1; i <= 1; i++) mesh.FillRect(new(x - r * .8f, y + i * r * .55f - w / 2, r * 1.6f, w), ink, w / 2); break;
        }
    }

    private static void Bar(GuiDx12MeshBuilder mesh, float x, float y, float width, float value, GuiColor color, float s)
    {
        mesh.FillRect(new(x * s, y * s, width * s, 5 * s), Theme.Track, 2.5f * s);
        if (value > .005f) mesh.FillRect(new(x * s, y * s, width * Math.Clamp(value, .03f, 1) * s, 5 * s), color, 2.5f * s);
    }

    private static void Arc(GuiDx12MeshBuilder mesh, float cx, float cy, float radius, float fromDegrees, float toDegrees, GuiColor color, float thickness)
    {
        int steps = Math.Max(6, (int)(MathF.Abs(toDegrees - fromDegrees) / 4));
        for (int i = 0; i < steps; i++)
        {
            float a0 = (fromDegrees + (toDegrees - fromDegrees) * i / steps) * MathF.PI / 180;
            float a1 = (fromDegrees + (toDegrees - fromDegrees) * (i + 1) / steps) * MathF.PI / 180;
            mesh.Line(cx + MathF.Cos(a0) * radius, cy + MathF.Sin(a0) * radius, cx + MathF.Cos(a1) * radius, cy + MathF.Sin(a1) * radius, color, thickness);
        }
        float end = toDegrees * MathF.PI / 180, start = fromDegrees * MathF.PI / 180;
        mesh.Disc(cx + MathF.Cos(start) * radius, cy + MathF.Sin(start) * radius, thickness / 2, color);
        mesh.Disc(cx + MathF.Cos(end) * radius, cy + MathF.Sin(end) * radius, thickness / 2, color);
    }

    /// <summary>A filled arrowhead pointing <paramref name="degrees"/> clockwise from screen-up.</summary>
    private static void DrawArrow(GuiDx12MeshBuilder mesh, float x, float y, float size, float degrees, GuiColor color)
    {
        float a = degrees * MathF.PI / 180;
        (float X, float Y) P(float fx, float fy) => (x + fx * MathF.Cos(a) - fy * MathF.Sin(a), y + fx * MathF.Sin(a) + fy * MathF.Cos(a));
        var tip = P(0, -size); var left = P(-size * .7f, size * .7f); var right = P(size * .7f, size * .7f); var notch = P(0, size * .3f);
        mesh.Line(tip.X, tip.Y, left.X, left.Y, color, Math.Max(1.5f, size * .3f));
        mesh.Line(tip.X, tip.Y, right.X, right.Y, color, Math.Max(1.5f, size * .3f));
        mesh.Line(left.X, left.Y, notch.X, notch.Y, color, Math.Max(1.5f, size * .3f));
        mesh.Line(right.X, right.Y, notch.X, notch.Y, color, Math.Max(1.5f, size * .3f));
    }

    private static RenderStormCellState? NearestStorm(RenderFrame frame)
    {
        var p = frame.Camera.Position;
        return frame.Weather?.ActiveStorms.OrderBy(cell => (cell.Position.X - p.X) * (cell.Position.X - p.X) + (cell.Position.Z - p.Z) * (cell.Position.Z - p.Z)).FirstOrDefault();
    }

    /// <summary>Compass heading in degrees: 0 = north (+Z), 90 = east (+X).</summary>
    private static float Heading(Vector3f forward) => (MathF.Atan2(forward.X, forward.Z) * 180 / MathF.PI + 360) % 360;
    private static float Bearing(Vector3f from, Vector3f to) => (MathF.Atan2(to.X - from.X, to.Z - from.Z) * 180 / MathF.PI + 360) % 360;
    private static string Cardinal(float degrees) =>
        new[] { "N", "NE", "E", "SE", "S", "SW", "W", "NW" }[(int)MathF.Round(((degrees % 360) + 360) % 360 / 45) % 8];
}
