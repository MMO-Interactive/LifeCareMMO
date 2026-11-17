using System.Collections.Immutable;
using Engine.GUI;
using Shared.Contracts.Rendering;
using StormChase.Protocol;

/// <summary>
/// The phone's Weather app, with three tabs. Now: conditions, the nearest storm and the radar. Today: the detailed
/// atmospheric outlook (instability, shear, composites, the nearby boundary and a hodograph). Week: seven days ahead —
/// the server's three-day outlook, extended with the same deterministic model the server runs.
/// </summary>
internal sealed partial class StormChaseHud
{
    public const int WeekDays = 7;
    /// <summary>now, today or week.</summary>
    public string WeatherTab { get; set; } = "now";
    private (int Seed, int Day)? weekKey;
    private Task<ImmutableArray<AtmosphereDayForecast>>? weekTask;

    /// <summary>Opens the phone on the Weather app's Today page (the Z key, and the old forecast shortcut).</summary>
    public void OpenOutlook() { PhoneOpen = true; PhoneApp = "weather"; WeatherTab = "today"; MapOpen = false; }

    private void BuildWeatherApp(List<GuiElement> root, HudKit k, GuiRect screen, StormChaseTelemetry t)
    {
        root.Add(k.Button("phone/home", "‹ Home", new(screen.X + 12, screen.Y + 30, 64, 24), size: 8.5f, fill: new(255, 255, 255, 14)));
        root.Add(k.Label(screen, "Weather", 0, 32, screen.Width, 12, Theme.Ink, bold: true, align: GuiTextAlignment.Center));
        string[] tabs = ["now", "today", "week"];
        float tw = (screen.Width - 24 - 12) / 3;
        for (int i = 0; i < tabs.Length; i++)
            root.Add(k.Button("phone/weather/tab/" + tabs[i], tabs[i] switch { "now" => "NOW", "today" => "TODAY", _ => "WEEK" },
                new(screen.X + 12 + i * (tw + 6), screen.Y + 60, tw, 24), WeatherTab == tabs[i], 8));
        switch (WeatherTab)
        {
            case "today": BuildToday(root, k, screen, t); break;
            case "week": BuildWeek(root, k, screen, t); break;
            default: BuildNow(root, k, screen, t); break;
        }
    }

    private const float NowTop = 92;

    private void BuildNow(List<GuiElement> root, HudKit k, GuiRect screen, StormChaseTelemetry t)
    {
        var c = t.Campaign;
        var now = new GuiRect(screen.X + 12, screen.Y + NowTop, screen.Width - 24, 66);
        root.Add(k.Fill(now, new(255, 255, 255, 12), 12));
        root.Add(k.Label(now, c?.Forecast ?? t.LocalWeather, 12, 8, 170, 13, Theme.Ink, bold: true));
        root.Add(k.Label(now, c is null ? "" : TemperatureText(c).TrimStart(' ', '·'), now.Width - 102, 6, 90, 16, Theme.Ink, bold: true, align: GuiTextAlignment.Right));
        string here = t.LocalStorm is { } local && local.Summary(t.Gust) is { Length: > 0 } summary ? summary : "Calm where you are";
        root.Add(k.Label(now, here, 12, 32, now.Width - 24, 8.5f, Theme.Muted));
        root.Add(k.Label(now, c is null ? "" : $"Tornado potential {c.Risk:P0}  ·  {c.StormCount} active cell{(c.StormCount == 1 ? "" : "s")}", 12, 47, now.Width - 24, 7.5f, c is null ? Theme.Faint : Theme.Risk(c.Risk)));

        var storm = new GuiRect(screen.X + 12, screen.Y + NowTop + 74, screen.Width - 24, 86);
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
        root.Add(k.Label(storm, $"{t.FunnelEpisode} funnel{(t.FunnelEpisode == 1 ? "" : "s")}  ·  {t.Touchdowns} touchdown{(t.Touchdowns == 1 ? "" : "s")}", 12, 64, storm.Width - 24, 7.5f, Theme.Faint));

        float y = screen.Bottom - 64, bw = (screen.Width - 24 - 12) / 3;
        root.Add(k.Button("phone/weather/ref", "REF", new(screen.X + 12, y, bw, 26), RadarProduct == StormChaseRadarProduct.Reflectivity, 8));
        root.Add(k.Button("phone/weather/vel", "VEL", new(screen.X + 18 + bw, y, bw, 26), RadarProduct == StormChaseRadarProduct.RadialVelocity, 8));
        root.Add(k.Button("phone/weather/range", RegionalMap ? "REGION" : "LOCAL", new(screen.X + 24 + bw * 2, y, bw, 26), size: 8));
    }

    private void BuildToday(List<GuiElement> root, HudKit k, GuiRect screen, StormChaseTelemetry t)
    {
        var c = t.Campaign; float y = NowTop;
        if (c?.Atmosphere is not { } atmosphere) { root.Add(k.Label(screen, "Observations are being received…", 14, y + 10, screen.Width - 28, 9, Theme.Faint)); return; }
        var d = atmosphere.Diagnostics;
        var today = atmosphere.ThreeDayForecast.FirstOrDefault();
        root.Add(k.Label(screen, $"DAY {c.Day}  ·  {ClockText(c.Hour)}", 14, y, 160, 8, Theme.Faint));
        root.Add(k.Label(screen, today?.Name ?? c.Forecast, 14, y + 14, screen.Width - 28, 14, today is null ? Theme.Ink : Theme.Risk(today.Risk), bold: true));
        y += 40;
        if (c.Boundary is { } boundary)
        {
            root.Add(k.Wrapped(screen, BoundaryLocation(boundary, observerPosition.X, observerPosition.Z), 14, y, screen.Width - 28, 28, 8, BoundaryColor));
            y += 26;
        }
        (string Name, string Value)[] cells =
        [
            ("CAPE", $"{d.CapeJkg:0} J/kg"), ("CIN", $"{d.CinJkg:0} J/kg"), ("0-3 KM CAPE", $"{d.Cape03KmJkg:0} J/kg"), ("LCL / LFC", $"{d.LclMeters:0} / {d.LfcMeters:0} m"),
            ("0-1 KM SRH", $"{d.Srh01KmM2S2:0} m²/s²"), ("0-3 KM SRH", $"{d.Srh03KmM2S2:0} m²/s²"), ("0-6 KM SHEAR", $"{d.Shear06KmMetersPerSecond:0.0} m/s"),
            ("STORM MOTION", $"{MathF.Sqrt(d.StormMotion.X * d.StormMotion.X + d.StormMotion.Z * d.StormMotion.Z):0.0} m/s"),
        ];
        float cw = (screen.Width - 24 - 6) / 2;
        for (int i = 0; i < cells.Length; i++)
        {
            var cell = new GuiRect(screen.X + 12 + i % 2 * (cw + 6), screen.Y + y + i / 2 * 34, cw, 30);
            root.Add(k.Fill(cell, new(255, 255, 255, 10), 7));
            root.Add(k.Label(cell, cells[i].Name, 8, 3, cw - 16, 6.5f, Theme.Faint));
            root.Add(k.Label(cell, cells[i].Value, 8, 14, cw - 16, 9, Theme.Ink, bold: true));
        }
        y += 4 * 34 + 2;
        root.Add(k.Label(screen, $"STP {d.Stp:0.00}   VTP-G {d.VtpGame:0.00}", 14, y, 170, 9.5f, Theme.Accent, bold: true));
        root.Add(k.Label(screen, "game estimate", screen.Width - 104, y + 2, 90, 7, Theme.Faint, align: GuiTextAlignment.Right));
        y += 22;
        todayHodographTop = y;
        root.Add(k.Label(screen, "HODOGRAPH  ·  0-1 / 1-3 / 3-6 / 6+ KM", 14, y, screen.Width - 28, 7, Theme.Faint));
        float hodograph = TodayHodographSize(screen);
        y += 14 + hodograph + 6;
        if (today is not null)
        {
            root.Add(k.Label(screen, $"Peak  CAPE {today.Peak.CapeJkg:0}  ·  STP {today.Peak.Stp:0.0}", 14, y, screen.Width - 28, 8, Theme.Muted));
            root.Add(k.Label(screen, $"Tonight  CAPE {today.Overnight.CapeJkg:0}  ·  STP {today.Overnight.Stp:0.0}", 14, y + 15, screen.Width - 28, 8, Theme.Muted));
        }
        string town = NearestTown(c.Settlements, atmosphere.SamplePosition.X, atmosphere.SamplePosition.Z);
        root.Add(k.Label(screen, $"Sounding near {town}  ·  potential is not a promise", 14, y + 31, screen.Width - 28, 7, Theme.Faint));
    }

    private float todayHodographTop;
    private static float TodayHodographSize(GuiRect screen) => Math.Clamp(screen.Height - 420, 110, 170);

    private void BuildWeek(List<GuiElement> root, HudKit k, GuiRect screen, StormChaseTelemetry t)
    {
        var days = WeekForecast(t);
        root.Add(k.Label(screen, "SEVEN-DAY OUTLOOK", 14, NowTop, 200, 8, Theme.Faint));
        if (days.IsDefaultOrEmpty) { root.Add(k.Label(screen, "Working out the week…", 14, NowTop + 22, screen.Width - 28, 9, Theme.Faint)); return; }
        float row = Math.Min(60, (screen.Height - NowTop - 18 - 40) / days.Length);
        for (int i = 0; i < days.Length; i++)
        {
            var day = days[i];
            var r = new GuiRect(screen.X + 12, screen.Y + NowTop + 16 + i * row, screen.Width - 24, row - 4);
            root.Add(k.Fill(r, new(255, 255, 255, i == 0 ? (byte)18 : (byte)10), 8));
            string when = i == 0 ? "Today" : i == 1 ? "Tomorrow" : $"Day {day.DayNumber}";
            root.Add(k.Label(r, when, 10, 5, 80, 8.5f, Theme.Ink, bold: true));
            root.Add(k.Label(r, day.Name, 92, 5, r.Width - 150, 8.5f, Theme.Risk(day.Risk), bold: true));
            root.Add(k.Label(r, $"{day.Risk:P0}", r.Width - 56, 5, 46, 8.5f, Theme.Risk(day.Risk), bold: true, align: GuiTextAlignment.Right));
            if (row >= 50)
                root.Add(k.Label(r, $"CAPE {day.Peak.CapeJkg:0}  ·  STP {day.Peak.Stp:0.0}  ·  night CAPE {day.Overnight.CapeJkg:0}", 10, 34, r.Width - 20, 7, Theme.Muted));
        }
    }

    /// <summary>The server's three days, extended to a week with the same deterministic model (worked out off the frame thread).</summary>
    private ImmutableArray<AtmosphereDayForecast> WeekForecast(StormChaseTelemetry t)
    {
        var served = t.Campaign?.Atmosphere?.ThreeDayForecast ?? [];
        if (t.Campaign?.Seed is not int seed || served.IsDefaultOrEmpty) return served;
        int first = served[0].DayNumber;
        if (weekKey != (seed, first)) { weekKey = (seed, first); weekTask = Task.Run(() => StormChaseAtmosphere.CreateForecast(seed, first + served.Length, WeekDays - served.Length)); }
        if (weekTask is not { IsCompletedSuccessfully: true } done) return served;
        return [.. served, .. done.Result];
    }

    private void DrawWeatherAppInstruments(GuiDx12MeshBuilder mesh, GuiRect screen, float s, StormChaseTelemetry t, RenderFrame frame)
    {
        switch (WeatherTab)
        {
            case "today":
                if (t.Campaign?.Atmosphere is { } atmosphere)
                {
                    float size = TodayHodographSize(screen);
                    DrawHodograph(mesh, (screen.X + screen.Width / 2) * s, (screen.Y + todayHodographTop + 14 + size / 2) * s, size / 2 * .86f * s, atmosphere, s);
                }
                break;
            case "week":
            {
                var days = WeekForecast(t);
                if (days.IsDefaultOrEmpty) break;
                float row = Math.Min(60, (screen.Height - NowTop - 18 - 40) / days.Length);
                for (int i = 0; i < days.Length; i++)
                {
                    float x = screen.X + 22, y = screen.Y + NowTop + 16 + i * row + 22, width = screen.Width - 44;
                    mesh.FillRect(new(x * s, y * s, width * s, 4 * s), Theme.Track, 2 * s);
                    mesh.FillRect(new(x * s, y * s, width * Math.Clamp(days[i].Risk, .03f, 1) * s, 4 * s), Theme.Risk(days[i].Risk), 2 * s);
                }
                break;
            }
            default:
            {
                var storm = new GuiRect(screen.X + 12, screen.Y + NowTop + 74, screen.Width - 24, 86);
                float seg = (storm.Width - 24 - 20) / 6;
                for (int i = 0; i < 6; i++)
                {
                    var (mph, _) = Theme.EfScale[i];
                    mesh.FillRect(new((storm.X + 12 + i * (seg + 4)) * s, (storm.Y + 52) * s, seg * s, 5 * s), t.HasFunnel && t.PeakWind >= mph ? Theme.Threat(mph + 1) : Theme.Track, 2.5f * s);
                }
                if (NearestStorm(frame) is { } cell && t.Distance > 0)
                    DrawArrow(mesh, (storm.Right - 22) * s, (storm.Y + 16) * s, 7 * s, Bearing(frame.Camera.Position, cell.Position) - Heading(frame.Camera.Forward), Theme.Ink);
                float top = NowTop + 74 + 86 + 10, scope = Math.Clamp(screen.Height - top - 64 - 40, 120, 220);
                DrawGpuRadar(mesh, (int)((screen.X + (screen.Width - scope) / 2) * s), (int)((screen.Y + top) * s), (int)(scope * s), frame, t.Campaign, s * .9f);
                break;
            }
        }
    }
}
