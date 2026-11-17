using Engine.GUI;
using StormChase.Protocol;

internal sealed partial class StormChaseHud
{
    private static (float X, float Y, float Width, float Height) ForecastBounds(int w, int h)
    {
        float wide = Math.Min(860, w - 40), tall = Math.Min(574, h - 40);
        return ((w - wide) / 2f, (h - tall) / 2f, wide, tall);
    }

    private GuiDocument BuildForecastDocument(int w, int h, StormChaseCampaignTelemetry telemetry)
    {
        var bounds = ForecastBounds(w, h);
        GuiColor ink = new(224, 237, 240), muted = new(152, 178, 187), accent = new(80, 224, 189);
        var items = new List<GuiElement>();
        void Label(string text, float x, float y, float wide, float size = 10, GuiColor? color = null) => items.Add(new GuiLabel
        {
            Text = text, Translation = new(x, y), Width = GuiLength.Pixels(wide), Height = GuiLength.Pixels(size * 2.2f),
            Style = new(Theme.Font, size, 1.1f, color ?? ink), WrapMode = GuiTextWrapMode.NoWrap
        });
        GuiButton Button(string id, string text, float x, float y, float wide) => new()
        {
            Id = id, Text = text, Translation = new(x, y), Width = GuiLength.Pixels(wide), Height = GuiLength.Pixels(28),
            TextStyle = new(Theme.Font, 10, 1, ink), Padding = new(7, 4), BackgroundColor = new(23, 52, 60),
            HoverBackgroundColor = new(28, 78, 82), PressedBackgroundColor = new(34, 103, 108),
            BorderColor = new(56, 102, 110), BorderThickness = 1
        };
        Label("ATMOSPHERIC OUTLOOK", 22, 16, bounds.Width - 140, 18, accent);
        items.Add(Button("forecast/close", "CLOSE [Z]", bounds.Width - 130, 14, 108));
        Label($"DAY {telemetry.Day}  {(int)telemetry.Hour:00}:{(int)(telemetry.Hour % 1 * 60):00}  {telemetry.Forecast}", 22, 52, (bounds.Width - 44) * .45f, 11, muted);
        if (telemetry.Boundary is { } boundary)
            items.Add(new GuiLabel
            {
                Text = BoundaryLocation(boundary, observerPosition.X, observerPosition.Z), Translation = new(22 + (bounds.Width - 44) * .45f, 52),
                Width = GuiLength.Pixels((bounds.Width - 44) * .55f), Height = GuiLength.Pixels(24), TextAlignment = GuiTextAlignment.Right,
                WrapMode = GuiTextWrapMode.NoWrap, Style = new(Theme.Bold, 11, 1.1f, BoundaryColor)
            });
        if (telemetry.Boundary is { } tripleBoundary && tripleBoundary.TryTriplePoint(out float tripleX, out float tripleZ))
            items.Add(new GuiLabel
            {
                Text = $"TRIPLE POINT · {Bearing(tripleX - observerPosition.X, tripleZ - observerPosition.Z)}",
                Translation = new(22 + (bounds.Width - 44) * .45f, 72), Width = GuiLength.Pixels((bounds.Width - 44) * .55f),
                Height = GuiLength.Pixels(20), TextAlignment = GuiTextAlignment.Right, WrapMode = GuiTextWrapMode.NoWrap,
                Style = new(Theme.Font, 9, 1.1f, BoundaryColor)
            });
        if (telemetry.Atmosphere is not { } atmosphere)
        {
            Label("Atmospheric observations are being received.", 22, 95, bounds.Width - 44, 12);
        }
        else
        {
            var d = atmosphere.Diagnostics;
            // Label / right-aligned value columns (the page no longer uses a monospaced font).
            (string Name, string Value)[] rows =
            [
                ("CAPE", $"{d.CapeJkg:0} J/kg"), ("CIN", $"{d.CinJkg:0} J/kg"), ("0-3 KM CAPE", $"{d.Cape03KmJkg:0} J/kg"),
                ("LCL / LFC", $"{d.LclMeters:0} / {d.LfcMeters:0} m"), ("0-1 KM SRH", $"{d.Srh01KmM2S2:0} m²/s²"),
                ("0-3 KM SRH", $"{d.Srh03KmM2S2:0} m²/s²"), ("0-6 KM SHEAR", $"{d.Shear06KmMetersPerSecond:0.0} m/s"),
            ];
            for (int i = 0; i < rows.Length; i++)
            {
                Label(rows[i].Name, 22, 90 + i * 31, 150, 11, muted);
                items.Add(new GuiLabel { Text = rows[i].Value, Translation = new(172, 90 + i * 31), Width = GuiLength.Pixels(200),
                    Height = GuiLength.Pixels(26), TextAlignment = GuiTextAlignment.Right, WrapMode = GuiTextWrapMode.NoWrap,
                    Style = new(Theme.Bold, 12, 1.1f, ink) });
            }
            Label($"STP {d.Stp:0.00}   VTP-G {d.VtpGame:0.00}", 22, 311, 370, 12, accent);
            Label("Fixed-layer STP / VTP-G: game estimate", 22, 339, 390, 9, muted);
            string town = NearestTown(telemetry.Settlements, atmosphere.SamplePosition.X, atmosphere.SamplePosition.Z);
            Label(telemetry.Boundary is { } target
                ? $"SOUNDING  near {town}, {MathF.Abs(target.SignedDistance(atmosphere.SamplePosition.X, atmosphere.SamplePosition.Z)) / 1000:0} km {Sides(target.NearestKind(atmosphere.SamplePosition.X, atmosphere.SamplePosition.Z)).Moist} side"
                : $"SOUNDING  near {town}", 22, 360, 390, 9, accent);
            float speed = MathF.Sqrt(d.StormMotion.X * d.StormMotion.X + d.StormMotion.Z * d.StormMotion.Z);
            Label($"STORM MOTION {speed:0.0} m/s", bounds.Width - 338, 88, 315, 10, accent);
            Label("HODOGRAPH  /  WIND WITH HEIGHT", bounds.Width - 338, 111, 315, 9, muted);
            Label("0-1 KM  /  1-3  /  3-6  /  6+", bounds.Width - 338, 360, 315, 9, muted);
            Label("THREE DAY OUTLOOK", 22, bounds.Height - 163, bounds.Width - 44, 11, accent);
            float column = (bounds.Width - 44) / 3;
            for (int i = 0; i < Math.Min(3, atmosphere.ThreeDayForecast.Length); i++)
            {
                var day = atmosphere.ThreeDayForecast[i];
                float x = 22 + column * i;
                Label($"DAY {day.DayNumber}  {day.Name}", x, bounds.Height - 133, column - 10, 9);
                Label($"PEAK CAPE {day.Peak.CapeJkg:0}  STP {day.Peak.Stp:0.0}", x, bounds.Height - 106, column - 10, 8, muted);
                Label($"NIGHT CAPE {day.Overnight.CapeJkg:0}  STP {day.Overnight.Stp:0.0}", x, bounds.Height - 80, column - 10, 8, muted);
            }
        }
        items.Add(Button("radar/reflectivity", "RADAR REFLECTIVITY", 22, bounds.Height - 44, 226));
        items.Add(Button("radar/velocity", "RADAR RADIAL VELOCITY", 258, bounds.Height - 44, 252));
        Label("Potential changes odds; it does not promise a tornado.", 22, bounds.Height - 192, bounds.Width - 44, 9, muted);
        return new(new GuiOverlayPanel
        {
            Width = GuiLength.Pixels(w), Height = GuiLength.Pixels(h), BackgroundColor = new(5, 13, 20, 215),
            Items = [new GuiOverlayPanel
            {
                Translation = new(bounds.X, bounds.Y), Width = GuiLength.Pixels(bounds.Width), Height = GuiLength.Pixels(bounds.Height),
                BackgroundColor = new(13, 24, 33), BorderColor = new(58, 97, 107), BorderThickness = 1, Items = items
            }]
        });
    }

    /// <summary>Boundary overlay colour: magenta-violet, distinct from radar echoes, roads and the teal accent.</summary>
    private static readonly GuiColor BoundaryColor = new(236, 128, 255);

    private static (string Dry, string Moist) Sides(string kind) => kind switch
    {
        "Dryline" => ("dry", "moist"), "Cold front" => ("cold", "warm"), _ => ("cool", "warm"),
    };

    /// <summary>"DRYLINE · 11 km E of you · dry side": distance/direction to the nearest point on the boundary.</summary>
    internal static string BoundaryLocation(StormChaseBoundary boundary, float x, float z)
    {
        // Report the nearer of the two lines (the main boundary or the warm front), even inside the warm sector.
        float primary = boundary.PrimaryDistance(x, z), secondary = boundary.SecondaryDistance(x, z);
        bool main = MathF.Abs(primary) <= MathF.Abs(secondary);
        float distance = main ? primary : secondary;
        string kind = (main ? boundary.Kind : StormChaseBoundary.SecondaryKind).ToUpperInvariant();
        float overall = boundary.SignedDistance(x, z);
        string side = overall > 0 ? Sides(boundary.Kind).Moist : Sides(boundary.NearestKind(x, z)).Dry;
        if (MathF.Abs(distance) < 750) return $"{kind} · on you now · {side} side";
        // The nearest point lies down the distance gradient (numerical: the main boundary is curved).
        float D(float px, float pz) => main ? boundary.PrimaryDistance(px, pz) : boundary.SecondaryDistance(px, pz);
        float gx = (D(x + 50, z) - D(x - 50, z)) / 100, gz = (D(x, z + 50) - D(x, z - 50)) / 100;
        return $"{kind} · {Bearing(-distance * gx, -distance * gz)} · {side} side";
    }

    /// <summary>"11 km E of you" for an east/north offset in metres.</summary>
    private static string Bearing(float east, float north)
    {
        float bearing = (MathF.Atan2(east, north) * 180 / MathF.PI + 360) % 360;
        string[] compass = ["N", "NE", "E", "SE", "S", "SW", "W", "NW"];
        return $"{MathF.Sqrt(east * east + north * north) / 1000:0} km {compass[(int)MathF.Round(bearing / 45) % 8]} of you";
    }

    private static string NearestTown(IReadOnlyList<StormChaseSettlement> settlements, float x, float z)
    {
        string name = "open country"; float best = float.MaxValue;
        foreach (var settlement in settlements)
        {
            float dx = settlement.Position.X - x, dz = settlement.Position.Z - z, d = dx * dx + dz * dz;
            if (d < best) { best = d; name = settlement.Name; }
        }
        return name;
    }

    private void DrawGpuHodograph(GuiDx12MeshBuilder mesh, int w, int h, AtmosphereSnapshot atmosphere)
    {
        var bounds = ForecastBounds(w, h);
        float centerX = bounds.X + bounds.Width - 180, centerY = bounds.Y + 230, radius = 101;
        float maximum = Math.Max(30, atmosphere.Sounding.Select(level => MathF.Sqrt(level.WindEastMetersPerSecond
            * level.WindEastMetersPerSecond + level.WindNorthMetersPerSecond * level.WindNorthMetersPerSecond)).DefaultIfEmpty(0).Max());
        float limit = MathF.Ceiling(maximum / 10) * 10;
        GuiColor grid = new(45, 72, 84);
        for (int ring = 1; ring <= 3; ring++) mesh.Circle(centerX, centerY, radius * ring / 3, grid);
        mesh.Line(centerX - radius, centerY, centerX + radius, centerY, grid);
        mesh.Line(centerX, centerY - radius, centerX, centerY + radius, grid);
        mesh.Text($"{limit:0} m/s", centerX + 58, centerY + 2, 70, 14, 8, new(150, 175, 184));
        mesh.Text("N", centerX - 4, centerY - radius - 16, 16, 14, 8, new(150, 175, 184));
        mesh.Text("E", centerX + radius + 3, centerY - 7, 16, 14, 8, new(150, 175, 184));
        for (int i = 1; i < atmosphere.Sounding.Length; i++)
        {
            var from = atmosphere.Sounding[i - 1]; var to = atmosphere.Sounding[i];
            GuiColor color = to.HeightMeters <= 1000 ? new(255, 197, 79) : to.HeightMeters <= 3000 ? new(75, 232, 181)
                : to.HeightMeters <= 6000 ? new(90, 169, 255) : new(207, 134, 241);
            float x = centerX + to.WindEastMetersPerSecond / limit * radius, y = centerY - to.WindNorthMetersPerSecond / limit * radius;
            mesh.Line(centerX + from.WindEastMetersPerSecond / limit * radius, centerY - from.WindNorthMetersPerSecond / limit * radius, x, y, color, 2);
            mesh.Disc(x, y, 2, color);
        }
        var motion = atmosphere.Diagnostics.StormMotion;
        float mx = centerX + motion.X / limit * radius, my = centerY - motion.Z / limit * radius;
        mesh.Line(mx - 4, my - 4, mx + 4, my + 4, new(255, 255, 255), 2);
        mesh.Line(mx - 4, my + 4, mx + 4, my - 4, new(255, 255, 255), 2);
        mesh.Text("X STORM", centerX - 105, centerY + radius + 9, 115, 14, 8, new(208, 225, 232));
    }
}
