using Engine.GUI;
using Shared.Contracts.Rendering;
using Shared.Contracts.World;

/// <summary>
/// Storm-chaser instrument HUD. Layout is authored in 1600×900 design units and
/// converted to real pixels (so glyphs are rasterised at their displayed size and stay
/// sharp at any resolution). Panels are Engine.GUI elements; gauges, the compass and the
/// radar scope are drawn as GPU geometry in <see cref="DrawInstruments"/>.
/// </summary>
internal sealed partial class StormChaseHud
{
    internal static class Theme
    {
        public const string Font = "Bahnschrift", Bold = "Bahnschrift SemiBold";
        public static readonly GuiColor Glass = new(8, 14, 20, 200), GlassEdge = new(255, 255, 255, 30);
        public static readonly GuiColor Ink = new(238, 243, 245), Muted = new(162, 180, 188), Faint = new(120, 138, 146);
        public static readonly GuiColor Accent = new(84, 226, 198), Warn = new(255, 198, 74), Danger = new(255, 88, 94);
        public static readonly GuiColor Track = new(255, 255, 255, 34), KeyCap = new(255, 255, 255, 22);
        public static readonly (float Mph, string Label)[] EfScale = [(65, "EF0"), (86, "EF1"), (111, "EF2"), (136, "EF3"), (166, "EF4"), (200, "EF5")];

        /// <summary>Peak-wind colour: teal below tornadic strength, then green→yellow→orange→red→magenta by EF class.</summary>
        public static GuiColor Threat(float mph) => mph < 40 ? new(120, 205, 196) : mph < 65 ? new(118, 214, 124)
            : mph < 111 ? new(244, 214, 72) : mph < 136 ? new(255, 158, 62) : mph < 166 ? new(255, 86, 90) : new(234, 112, 238);

        public static GuiColor Risk(float risk) => risk < .2f ? new(118, 214, 124) : risk < .4f ? new(244, 214, 72)
            : risk < .6f ? new(255, 158, 62) : risk < .8f ? new(255, 86, 90) : new(234, 112, 238);
    }

    /// <summary>Design-space rectangles shared by the GUI document and the GPU instruments.</summary>
    private sealed record HudLayout(float Scale, float Width, float Height, GuiRect Storm, GuiRect Compass,
        GuiRect? Outlook, GuiRect? Mission, GuiRect Radar, GuiRect Cluster, GuiRect Prompt, bool ShowRadar);

    private static float HudScale(int w, int h) => Math.Clamp(Math.Min(w / 1500f, h / 850f), .78f, 2.4f);

    private HudLayout ComputeLayout(int w, int h, StormChaseTelemetry t)
    {
        float s = HudScale(w, h), W = w / s, H = h / s, m = 22;
        var c = t.Campaign;
        bool radar = W >= 900 && H >= 560;
        var storm = new GuiRect(m, m, 372, 166);
        float compassWidth = Math.Clamp(2 * (W / 2 - storm.Right - 16), 240, 560);
        var compass = new GuiRect(W / 2 - compassWidth / 2, m, compassWidth, 42);
        GuiRect? outlook = c is null || W < 1180 ? null : new GuiRect(W - m - 340, m, 340, 196);
        GuiRect? mission = c?.Mission is not null && outlook is { } o ? new GuiRect(o.X, o.Bottom + 12, 340, 140) : null;
        var radarRect = new GuiRect(W - m - 300, H - m - 356, 300, 356);
        var cluster = c is null ? new GuiRect(m, H - m - 128, 372, 128) : new GuiRect(m, H - m - 182, 372, 182);
        float left = cluster.Right + 24, right = radar ? radarRect.X - 24 : W - m;
        float promptWidth = Math.Clamp(right - left, 320, 760);
        var prompt = new GuiRect(left + (right - left - promptWidth) / 2, H - m - 74, promptWidth, 74);
        return new(s, W, H, storm, compass, outlook, mission, radarRect, cluster, prompt, radar);
    }

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

        GuiLabel Label(GuiRect panel, string text, float x, float y, float width, float size, GuiColor color,
            bool bold = false, GuiTextAlignment align = GuiTextAlignment.Left) => new()
        {
            Text = text, Translation = new((panel.X + x) * s, (panel.Y + y) * s), Width = GuiLength.Pixels(width * s),
            Height = GuiLength.Pixels(size * 1.9f * s), TextAlignment = align, WrapMode = GuiTextWrapMode.NoWrap,
            Style = new(bold ? Theme.Bold : Theme.Font, size * s, 1.1f, color),
        };
        GuiOverlayPanel Glass(GuiRect r, GuiColor? edge = null) => new()
        {
            Translation = new(r.X * s, r.Y * s), Width = GuiLength.Pixels(r.Width * s), Height = GuiLength.Pixels(r.Height * s),
            BackgroundColor = Theme.Glass, BorderColor = Theme.GlassEdge, BorderThickness = Math.Max(1, s), CornerRadius = 10 * s,
        };
        GuiOverlayPanel Fill(GuiRect r, GuiColor color, float radius) => new()
        {
            Translation = new(r.X * s, r.Y * s), Width = GuiLength.Pixels(r.Width * s), Height = GuiLength.Pixels(r.Height * s),
            BackgroundColor = color, CornerRadius = radius * s,
        };
        GuiButton Button(string id, string text, GuiRect r, bool selected = false, float size = 9.5f) => new()
        {
            Id = id, Text = text, Translation = new(r.X * s, r.Y * s), Width = GuiLength.Pixels(r.Width * s), Height = GuiLength.Pixels(r.Height * s),
            TextStyle = new(Theme.Bold, size * s, 1, selected ? new GuiColor(8, 20, 24) : Theme.Ink), Padding = new(8 * s, 2 * s),
            CornerRadius = r.Height * s / 2, TextAlignment = GuiTextAlignment.Center,
            BackgroundColor = selected ? Theme.Accent : new(255, 255, 255, 18), HoverBackgroundColor = selected ? new(120, 238, 214) : new(255, 255, 255, 44),
            PressedBackgroundColor = new(84, 226, 198, 160), BorderColor = selected ? Theme.Accent : new(255, 255, 255, 40), BorderThickness = Math.Max(1, s),
        };
        void Chip(GuiRect panel, float x, float y, string text, GuiColor color, out float width)
        {
            var style = new GuiTextStyle(Theme.Bold, 8.5f * s, 1, color);
            width = composer.Measure(text, style, GuiSize.Zero, GuiTextWrapMode.NoWrap).Width / s + 16;
            root.Add(Fill(new(panel.X + x, panel.Y + y, width, 20), color.WithAlpha(38), 10));
            root.Add(Label(panel, text, x + 8, y + 3, width, 8.5f, color, bold: true));
        }

        // ---------------------------------------------------------------- storm card
        var storm = L.Storm;
        bool active = t.HasFunnel && t.PeakWind > 0;
        var threat = active ? Theme.Threat(t.PeakWind) : Theme.Accent;
        root.Add(Glass(storm));
        root.Add(Fill(new(storm.X, storm.Y + 14, 4, storm.Height - 28), threat, 2));
        bool noStorms = c?.StormCount == 0 || t.Distance <= 0;
        Chip(storm, 18, 14, noStorms ? "QUIET SKIES" : t.Phase.ToUpperInvariant(), noStorms ? Theme.Muted : threat, out _);
        root.Add(Label(storm, noStorms ? "NO ACTIVE CELLS" : active ? "NEAREST TORNADO" : "NEAREST STORM", 150, 16, 204, 8.5f, Theme.Muted, align: GuiTextAlignment.Right));
        if (noStorms)
        {
            root.Add(Label(storm, "Clear skies", 18, 44, 330, 20, Theme.Ink, bold: true));
            root.Add(Label(storm, c is null ? "Waiting for convection." : $"{c.Forecast} today  ·  tornado potential {c.Risk:P0}", 18, 80, 336, 10, Theme.Muted));
        }
        else
        {
            root.Add(Label(storm, active ? $"{t.PeakWind:0}" : $"{t.Distance / 1000:0.0}", 18, 40, 140, 30, Theme.Ink, bold: true));
            root.Add(Label(storm, active ? "MPH PEAK WIND" : "KM TO CORE", 18, 92, 160, 8.5f, Theme.Muted));
        }
        if (!noStorms)
        {
            root.Add(Label(storm, active ? $"{t.Width:0} m wide" : "Developing cell", 174, 46, 180, 11, Theme.Ink));
            root.Add(Label(storm, $"{t.Distance / 1000:0.00} km away", 200, 70, 154, 11, Theme.Ink));
        }
        root.Add(Label(storm, $"{t.FunnelEpisode} FUNNEL{(t.FunnelEpisode == 1 ? "" : "S")}   ·   {t.Touchdowns} TOUCHDOWN{(t.Touchdowns == 1 ? "" : "S")}",
            18, 140, 336, 8, Theme.Faint));

        // ---------------------------------------------------------------- compass + clock
        root.Add(Glass(L.Compass));
        if (c is not null)
        {
            var pill = new GuiRect(L.Width / 2 - 150, L.Compass.Bottom + 8, 300, 28);
            root.Add(Glass(pill));
            root.Add(Label(pill, $"DAY {c.Day}   {(int)c.Hour:00}:{(int)(c.Hour % 1 * 60):00}", 16, 6, 150, 10.5f, Theme.Ink, bold: true));
            root.Add(Label(pill, $"${c.Cash:#,0}", 150, 6, 134, 10.5f, Theme.Accent, bold: true, align: GuiTextAlignment.Right));
            // What the storm is doing right here: hail, torrential rain, damaging gusts, near-zero visibility.
            if (t.LocalStorm is { } here && here.Summary(t.Gust) is { Length: > 0 } conditions)
            {
                var color = here.Hail01 >= .08f || here.WindSpeedMetersPerSecond * t.Gust >= 26 ? Theme.Danger : Theme.Warn;
                var style = new GuiTextStyle(Theme.Bold, 9 * s, 1, color);
                float chipWidth = composer.Measure(conditions, style, GuiSize.Zero, GuiTextWrapMode.NoWrap).Width / s + 20;
                var chip = new GuiRect(L.Width / 2 - chipWidth / 2, pill.Bottom + 6, chipWidth, 22);
                root.Add(Fill(chip, color.WithAlpha(190), 11));
                root.Add(Label(chip, conditions, 10, 4, chipWidth, 9, new GuiColor(16, 10, 8), bold: true));
            }
        }
        else
        {
            var pill = new GuiRect(L.Width / 2 - 150, L.Compass.Bottom + 8, 300, 28);
            root.Add(Glass(pill));
            root.Add(Label(pill, $"{t.TimeOfDay.ToUpperInvariant()}   ·   {t.CameraMode}   ·   {(t.Muted ? "SOUND OFF" : "SOUND ON")}", 0, 6, 300, 9.5f, Theme.Ink, align: GuiTextAlignment.Center));
        }

        // ---------------------------------------------------------------- outlook
        if (L.Outlook is { } o && c is not null)
        {
            var risk = Theme.Risk(c.Risk);
            root.Add(Glass(o));
            root.Add(Fill(new(o.X, o.Y + 14, 4, o.Height - 28), risk, 2));
            Chip(o, 18, 14, c.Forecast.ToUpperInvariant(), risk, out _);
            root.Add(Label(o, $"{c.StormCount} ACTIVE CELL{(c.StormCount == 1 ? "" : "S")}", 170, 16, 152, 8.5f, Theme.Muted, align: GuiTextAlignment.Right));
            var d = c.Atmosphere?.Diagnostics;
            (string Name, string Value)[] metrics = d is null
                ? [("RISK", $"{c.Risk:P0}"), ("STORMS", $"{c.StormCount}"), ("POPULARITY", $"{c.Popularity}"), ("VALUE", $"{StormChaseContracts.ContentValueMultiplier(c.Popularity):0.0}x")]
                : [("CAPE", $"{d.CapeJkg:0}"), ("SRH 0-1", $"{d.Srh01KmM2S2:0}"), ("STP", $"{d.Stp:0.0}"), ("VTP-G", $"{d.VtpGame:0.0}")];
            for (int i = 0; i < metrics.Length; i++)
            {
                float x = 18 + i * 78;
                root.Add(Label(o, metrics[i].Name, x, 46, 74, 7.5f, Theme.Faint));
                root.Add(Label(o, metrics[i].Value, x, 60, 74, 15, Theme.Ink, bold: true));
            }
            root.Add(Label(o, "TORNADO POTENTIAL", 18, 96, 200, 7.5f, Theme.Faint));
            root.Add(Label(o, $"{c.Risk:P0}", 230, 94, 92, 9.5f, risk, bold: true, align: GuiTextAlignment.Right));
            root.Add(Label(o, $"POPULARITY {c.Popularity}  ·  CONTENT {StormChaseContracts.ContentValueMultiplier(c.Popularity):0.00}x", 18, 126, 304, 8, Theme.Muted));
            root.Add(Button("forecast/toggle", "FORECAST & HODOGRAPH   Z", new(o.X + 18, o.Y + 150, 304, 30)));
        }
        if (L.Mission is { } mr && c?.Mission is { } mission)
        {
            root.Add(Glass(mr));
            root.Add(Fill(new(mr.X, mr.Y + 14, 4, mr.Height - 28), Theme.Warn, 2));
            Chip(mr, 18, 14, $"JOB  {Math.Min(mission.Step + 1, mission.StepCount)}/{mission.StepCount}", Theme.Warn, out float jobChip);
            float meters = StormChaseCareer.HorizontalDistance(observerPosition, new(mission.TargetPosition.X, mission.TargetPosition.Y, mission.TargetPosition.Z));
            root.Add(Label(mr, meters >= 1000 ? $"{meters / 1000:0.0} KM" : $"{meters:0} M", 170, 16, 152, 9.5f, Theme.Warn, bold: true, align: GuiTextAlignment.Right));
            root.Add(Label(mr, mission.Title, 18, 42, 304, 12, Theme.Ink, bold: true));
            root.Add(new GuiLabel
            {
                Text = mission.Objective, Translation = new((mr.X + 18) * s, (mr.Y + 64) * s), Width = GuiLength.Pixels(304 * s),
                Height = GuiLength.Pixels(34 * s), WrapMode = GuiTextWrapMode.Wrap, Style = new(Theme.Font, 8.5f * s, 1.15f, Theme.Muted),
            });
            root.Add(Label(mr, mission.RequiredWorkSeconds > 0 ? $"WORK {mission.WorkSeconds:0}/{mission.RequiredWorkSeconds:0} s   ·   H TO WORK" : "H INTERACT   ·   O DISPATCH",
                18, 102, 304, 8, Theme.Faint));
        }

        // ---------------------------------------------------------------- vehicle / field cluster
        var k = L.Cluster;
        root.Add(Glass(k));
        if (c is not null)
        {
            bool onFoot = t.CameraMode != "DRIVING";
            root.Add(Label(k, $"{c.SpeedMph:0}", 18, 62, 132, 26, Theme.Ink, bold: true, align: GuiTextAlignment.Center));
            root.Add(Label(k, "MPH", 18, 104, 132, 8, Theme.Muted, align: GuiTextAlignment.Center));
            root.Add(Label(k, c.VehicleName.ToUpperInvariant(), 166, 18, 190, 10.5f, Theme.Ink, bold: true));
            root.Add(Label(k, onFoot ? "ON FOOT  ·  E TO ENTER" : "DRIVING", 166, 38, 190, 8, onFoot ? Theme.Muted : Theme.Accent));
            bool lowFuel = c.Fuel < c.FuelCapacity * .2f;
            root.Add(Label(k, "FUEL", 166, 62, 80, 7.5f, Theme.Faint));
            root.Add(Label(k, $"{c.Fuel:0.0} / {c.FuelCapacity:0} L", 236, 60, 120, 8.5f, lowFuel ? Theme.Warn : Theme.Ink, align: GuiTextAlignment.Right));
            root.Add(Label(k, "BODY", 166, 92, 80, 7.5f, Theme.Faint));
            root.Add(Label(k, $"{c.Health:0}%", 236, 90, 120, 8.5f, c.Health < 35 ? Theme.Danger : Theme.Ink, align: GuiTextAlignment.Right));
            float chipX = 166;
            if (c.Refueling) { Chip(k, chipX, 124, "REFUELING  R STOP", Theme.Warn, out float wd); chipX += wd + 6; }
            if (c.Anchored) { Chip(k, chipX, 124, "ANCHORED", Theme.Accent, out float wd); chipX += wd + 6; }
            root.Add(Label(k, $"PROBES {c.Probes.Count}  ·  PHOTOS {t.Photos}", 166, 154, 190, 8, Theme.Faint));
        }
        else
        {
            root.Add(Label(k, "FIELD TELEMETRY", 18, 16, 200, 8.5f, Theme.Muted));
            root.Add(Label(k, t.Distance > 0 ? $"{t.Distance / 1000:0.00} KM" : "No storm", 18, 36, 200, t.Distance > 0 ? 22 : 16, Theme.Ink, bold: true));
            root.Add(Label(k, $"{(t.HasFunnel ? "TORNADO" : "STORM")} RANGE", 18, 72, 200, 8, Theme.Muted));
            root.Add(Label(k, $"PROBES {t.ProbeCount}   PHOTOS {t.Photos}   DEBRIS {t.BrokenParts}", 18, 98, 340, 8.5f, Theme.Faint));
            if (!string.IsNullOrEmpty(t.ProbeReading)) root.Add(Label(k, t.ProbeReading, 196, 40, 160, 9.5f, Theme.Accent, bold: true, align: GuiTextAlignment.Right));
        }

        // ---------------------------------------------------------------- radar
        if (L.ShowRadar)
        {
            var r = L.Radar;
            root.Add(Glass(r));
            root.Add(Label(r, RegionalMap ? "REGION RADAR" : "LOCAL RADAR", 18, 14, 140, 9.5f, Theme.Ink, bold: true));
            root.Add(Label(r, RegionalMap ? "24 KM  ·  N" : "3 KM  ·  N", 18, 32, 140, 7.5f, Theme.Faint));
            root.Add(Button("radar/reflectivity", "REF", new(r.X + 168, r.Y + 14, 56, 24), RadarProduct == StormChaseRadarProduct.Reflectivity, 8.5f));
            root.Add(Button("radar/velocity", "VEL", new(r.X + 228, r.Y + 14, 56, 24), RadarProduct == StormChaseRadarProduct.RadialVelocity, 8.5f));
        }

        // ---------------------------------------------------------------- prompt bar
        var p = L.Prompt;
        string message = c?.Message ?? "Click to capture the mouse  ·  Shift boosts  ·  Esc releases the mouse";
        root.Add(Glass(p));
        root.Add(Label(p, string.IsNullOrWhiteSpace(message) ? "Storm chase in progress." : message, 16, 10, p.Width - 32, 9.5f, Theme.Ink, align: GuiTextAlignment.Center));
        var hints = KeyHints(t);
        var keyStyle = new GuiTextStyle(Theme.Bold, 8 * s, 1, Theme.Ink);
        var hintStyle = new GuiTextStyle(Theme.Font, 8 * s, 1, Theme.Muted);
        var measured = hints.Select(hint => (hint.Key, hint.Label,
            Key: Math.Max(20, composer.Measure(hint.Key, keyStyle, GuiSize.Zero, GuiTextWrapMode.NoWrap).Width / s + 12),
            Text: composer.Measure(hint.Label, hintStyle, GuiSize.Zero, GuiTextWrapMode.NoWrap).Width / s)).ToList();
        while (measured.Count > 1 && measured.Sum(m => m.Key + m.Text + 20) > p.Width - 24) measured.RemoveAt(measured.Count - 1);
        float hx = p.X + (p.Width - measured.Sum(m => m.Key + m.Text + 20) + 14) / 2;
        foreach (var hint in measured)
        {
            root.Add(Fill(new(hx, p.Y + 40, hint.Key, 20), Theme.KeyCap, 5));
            root.Add(new GuiLabel { Text = hint.Item1, Translation = new(hx * s, (p.Y + 43) * s), Width = GuiLength.Pixels(hint.Key * s), Height = GuiLength.Pixels(16 * s),
                TextAlignment = GuiTextAlignment.Center, WrapMode = GuiTextWrapMode.NoWrap, Style = keyStyle });
            root.Add(new GuiLabel { Text = hint.Item2, Translation = new((hx + hint.Key + 6) * s, (p.Y + 43) * s), Width = GuiLength.Pixels((hint.Text + 4) * s),
                Height = GuiLength.Pixels(16 * s), WrapMode = GuiTextWrapMode.NoWrap, Style = hintStyle });
            hx += hint.Key + hint.Text + 20;
        }

        // Developer readout, deliberately quiet.
        root.Add(new GuiLabel { Text = $"{t.Fps:0} FPS  ·  GPU {t.GpuMilliseconds:0.0} MS", Translation = new((L.Width - 222) * s, (L.Height - 18) * s),
            Width = GuiLength.Pixels(200 * s), Height = GuiLength.Pixels(14 * s), TextAlignment = GuiTextAlignment.Right, WrapMode = GuiTextWrapMode.NoWrap,
            Style = new(Theme.Font, 7 * s, 1, Theme.Faint) });

        if (WorldOverlay is { } overlay) root.AddRange(overlay(w, h));
        return new(new GuiOverlayPanel { Width = GuiLength.Pixels(w), Height = GuiLength.Pixels(h), Items = root });
    }

    private static IReadOnlyList<(string Key, string Label)> KeyHints(StormChaseTelemetry t)
    {
        if (t.Campaign is null)
            return [("WASD", "Move"), ("C", "Walk / fly"), ("V", "Avatar"), ("B", "Probe"), ("F", "Photo"), ("TAB", "Views"), ("1-6", "Storm"), ("T", "Light"), ("H", "HUD")];
        if (t.Campaign.ShopOpen) return [("UP / DOWN", "Select"), ("ENTER", "Buy"), ("ESC", "Close")];
        return t.CameraMode == "DRIVING"
            ? [("W/S", "Drive"), ("SPACE", "Brake"), ("E", "Exit"), ("K", "Anchor"), ("R", "Refuel"), ("B", "Probe"), ("N", "Map"), ("Z", "Forecast")]
            : [("E", "Vehicle"), ("B", "Probe"), ("J", "Retrieve"), ("F", "Photo"), ("U", "Shop"), ("O", "Jobs"), ("N", "Map"), ("Z", "Forecast")];
    }

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
            items.Add(Text(offer.Name, 42, top + 8, wide - 240, 11.5f, selected ? Theme.Ink : Theme.Ink, bold: true));
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

        // Storm intensity bar: six EF segments lit up to the current peak wind.
        {
            var r = L.Storm;
            float x0 = r.X + 18, y0 = r.Y + 116, total = 336, gap = 4, seg = (total - gap * 5) / 6;
            for (int i = 0; i < 6; i++)
            {
                var (mph, label) = Theme.EfScale[i];
                bool lit = t.HasFunnel && t.PeakWind >= mph;
                float sx = x0 + i * (seg + gap);
                mesh.FillRect(new(X(sx), X(y0), X(seg), X(6)), lit ? Theme.Threat(mph + 1) : Theme.Track, X(3));
            }
            // Bearing arrow to the nearest storm, relative to where the camera faces.
            if (NearestStorm(frame) is { } storm && t.Distance > 0)
            {
                float rel = Bearing(frame.Camera.Position, storm.Position) - Heading(frame.Camera.Forward);
                DrawArrow(mesh, X(r.X + 174 + 12), X(r.Y + 79), X(10), rel, t.HasFunnel ? Theme.Threat(t.PeakWind) : Theme.Ink);
                if (MathF.Sqrt(storm.Velocity.X * storm.Velocity.X + storm.Velocity.Z * storm.Velocity.Z) > 1.3f)
                {
                    float motion = MathF.Atan2(storm.Velocity.X, storm.Velocity.Z) * 180 / MathF.PI;
                    float mph = MathF.Sqrt(storm.Velocity.X * storm.Velocity.X + storm.Velocity.Z * storm.Velocity.Z) / .44704f;
                    mesh.Text($"MOVING {Cardinal(motion)} {mph:0} MPH", X(r.X + 174), X(r.Y + 94), X(184), X(16), 8 * s, Theme.Muted, Theme.Font);
                }
            }
        }

        // Compass strip with heading ticks and storm markers.
        {
            var r = L.Compass;
            float heading = Heading(frame.Camera.Forward), cx = r.X + r.Width / 2, span = 120;
            for (int d = -180; d <= 540; d += 5)
            {
                float rel = d - heading;
                rel = (rel + 540) % 360 - 180;
                if (MathF.Abs(rel) > span / 2) continue;
                float x = cx + rel / span * (r.Width - 40);
                bool major = d % 45 == 0, mid = d % 15 == 0;
                mesh.Line(X(x), X(r.Y + (major ? 6 : mid ? 10 : 13)), X(x), X(r.Y + 17), major ? Theme.Ink : Theme.Faint, Math.Max(1, s * (major ? 1.6f : 1)));
                if (major)
                {
                    string name = Cardinal(d);
                    mesh.Text(name, X(x - 20), X(r.Y + 19), X(40), X(18), (name.Length == 1 ? 10.5f : 8.5f) * s, name == "N" ? Theme.Accent : Theme.Ink, Theme.Bold, GuiTextAlignment.Center);
                }
            }
            foreach (var storm in frame.Weather?.ActiveStorms ?? [])
            {
                float rel = Bearing(frame.Camera.Position, storm.Position) - heading;
                rel = (rel + 540) % 360 - 180;
                float x = cx + Math.Clamp(rel / span, -.5f, .5f) * (r.Width - 40);
                float wind = storm.Tornadoes.Count > 0 ? storm.Tornadoes.Max(tornado => tornado.WindSpeed) : 0;
                var color = wind > 0 ? Theme.Threat(wind) : Theme.Warn;
                DrawArrow(mesh, X(x), X(r.Y + 2), X(6), 180, color);
            }
            mesh.FillRect(new(X(cx - 1), X(r.Y + 2), Math.Max(2, X(2)), X(r.Height - 4)), Theme.Accent.WithAlpha(150));
        }

        // Tornado potential bar.
        if (L.Outlook is { } o && c is not null)
        {
            mesh.FillRect(new(X(o.X + 18), X(o.Y + 114), X(304), X(5)), Theme.Track, X(2.5f));
            mesh.FillRect(new(X(o.X + 18), X(o.Y + 114), X(304 * Math.Clamp(c.Risk, .02f, 1)), X(5)), Theme.Risk(c.Risk), X(2.5f));
        }
        if (L.Mission is { } mr && c?.Mission is { } mission)
        {
            float progress = mission.RequiredWorkSeconds > 0 ? mission.WorkSeconds / mission.RequiredWorkSeconds
                : (float)mission.Step / Math.Max(1, mission.StepCount);
            mesh.FillRect(new(X(mr.X + 18), X(mr.Y + 124), X(304), X(4)), Theme.Track, X(2));
            mesh.FillRect(new(X(mr.X + 18), X(mr.Y + 124), X(304 * Math.Clamp(progress, .02f, 1)), X(4)), Theme.Warn, X(2));
        }

        // Vehicle cluster: speed arc, fuel and body bars.
        if (c is not null)
        {
            var k = L.Cluster;
            float gx = k.X + 84, gy = k.Y + 92, radius = 62, maxMph = 140;
            Arc(mesh, X(gx), X(gy), X(radius), 135, 405, Theme.Track, X(7));
            float value = Math.Clamp(c.SpeedMph / maxMph, 0, 1);
            if (value > .002f) Arc(mesh, X(gx), X(gy), X(radius), 135, 135 + 270 * value, c.SpeedMph > 100 ? Theme.Warn : Theme.Accent, X(7));
            for (int mph = 0; mph <= maxMph; mph += 20)
            {
                float a = (135 + 270 * mph / maxMph) * MathF.PI / 180;
                mesh.Line(X(gx + MathF.Cos(a) * (radius - 13)), X(gy + MathF.Sin(a) * (radius - 13)),
                    X(gx + MathF.Cos(a) * (radius - 8)), X(gy + MathF.Sin(a) * (radius - 8)), Theme.Faint, Math.Max(1, s));
            }
            Bar(mesh, k.X + 166, k.Y + 78, 190, c.FuelCapacity <= 0 ? 0 : c.Fuel / c.FuelCapacity, c.Fuel < c.FuelCapacity * .2f ? Theme.Warn : Theme.Accent, s);
            Bar(mesh, k.X + 166, k.Y + 108, 190, c.Health / 100, c.Health < 35 ? Theme.Danger : c.Health < 65 ? Theme.Warn : Theme.Accent, s);
        }

        if (L.ShowRadar)
        {
            var r = L.Radar;
            int size = (int)X(256);
            int rx = (int)X(r.X + 22), ry = (int)X(r.Y + 52);
            DrawGpuRadar(mesh, rx, ry, size, frame, c, s);
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
