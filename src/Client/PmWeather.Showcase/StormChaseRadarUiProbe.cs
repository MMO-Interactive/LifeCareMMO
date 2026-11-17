using System.Text.Json;
using Engine.GUI;
using Engine.Rendering.Assets;
using Engine.Rendering.Backend;
using Engine.Rendering.Configuration;
using Shared.Contracts.Rendering;
using Shared.Contracts.World;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

internal static partial class Showcase
{
    private static async Task<int> VerifyStormChaseRadarUiAsync(RendererOptions options, NativeSurface surface,
        MutableRenderAssetCatalog catalog, CaptureLogger logger, RenderGpuDebugValidation validation, string output, string staging)
    {
        var checks = new List<object>(); bool passed = true;
        void Check(bool success, string name) { checks.Add(new { Name = name, Passed = success }); passed &= success; }
        var source = StormChaseCampaignWeather.CreateMorphologyCapturePoints().First();
        var parent = source.Environment.ActiveStorms.Single(storm => storm.Id == source.StormId);
        var atmosphere = StormChaseAtmosphere.Snapshot(source.Seed, source.DayNumber, source.Hour, source.ElapsedSeconds,
            (long)(source.ElapsedSeconds * 20), new(parent.Position.X, parent.Position.Y, parent.Position.Z));
        var camera = ChaseCaptureCamera(parent.Position + new Vector3f(-1500, 90, -1750),
            parent.Position + new Vector3f(0, 450, 0));
        var c = new StormChaseCampaignTelemetry(atmosphere.DayNumber, atmosphere.Hour,
            atmosphere.ThreeDayForecast[0].Name, $"CAPE {atmosphere.Diagnostics.CapeJkg:0} J/KG  CIN {atmosphere.Diagnostics.CinJkg:0}",
            atmosphere.Diagnostics.TornadoPotential01, source.Environment.ActiveStorms.Count, 900, "Scout", 0, 45, 60,
            100, false, false, 0, "Atmosphere and native radar validation", false, [], [], [], camera.Position, Atmosphere: atmosphere,
            Boundary: StormChaseAtmosphere.ActiveBoundary(source.Seed, source.DayNumber, source.Hour));
        var tornado = parent.Tornadoes.FirstOrDefault();
        var t = new StormChaseTelemetry(source.Phase, tornado?.SmoothedWindSpeed ?? 0,
            tornado?.SmoothedWidthMeters ?? 0, 2400, "Thunderstorm", 1, 60, 10, "ON FOOT", "AFTERNOON",
            0, 0, 0, false, FunnelEpisode: 1, Touchdowns: tornado?.TouchdownAmount >= .95f ? 1 : 0, HasFunnel: tornado is not null, Campaign: c);
        using var hud = new StormChaseHud(surface.WindowHandle);
        await using var backend = new Dx12RenderBackend(options, surface, catalog, logger);
        await backend.InitializeAsync(default);
        var captures = new List<object>(); long number = 0;
        // A day whose boundary has a warm front and a triple point inside the regional map.
        var tripleDay = Enumerable.Range(1, 64).Select(seed => (Seed: seed, Boundary: StormChaseAtmosphere.Boundary(seed, 1, 15)))
            .First(day => day.Boundary.TryTriplePoint(out float tx, out float tz) && MathF.Abs(tx) < 15000
                && MathF.Abs(tz - StormChaseWorld.PlayableCenterZ) < 15000);
        var regionalTelemetry = t with { Campaign = c with { Boundary = tripleDay.Boundary } };
        {
            // Clear-air fine line and wind shift, sampled directly (no storms) near the radar site.
            var b = tripleDay.Boundary;
            var (tx, tz) = (0f, 0f); b.TryTriplePoint(out tx, out tz);
            float along = -tx * b.NormalZ + tz * b.NormalX;
            // A point on the main boundary 6 km from the triple point, down the warm-sector edge.
            float sign = b.SecondaryDistance(tx - b.NormalZ * 1000, tz + b.NormalX * 1000) > b.SecondaryDistance(tx, tz) ? 1 : -1;
            var (lx, lz) = b.Point(along + sign * 6000, 1500);
            // Clear warm-sector air: at least 5 km from both the main boundary and the warm front.
            var (fx, fz) = new[] { (6000f, 12000f), (14000f, 10000f), (20000f, 9000f), (28000f, 9000f) }
                .Select(o => b.Point(along + sign * o.Item1, o.Item2))
                .First(p => b.PrimaryDistance(p.X, p.Z) > 5000 && b.SecondaryDistance(p.X, p.Z) > 5000);
            var (dx, dz) = b.Point(along + sign * 6000, -3000);
            var site = new Vector3f(lx - 8000, 25, lz);
            float onLine = StormChaseRadar.FineLineDbz(new(lx, 300, lz), b, site), far = StormChaseRadar.FineLineDbz(new(fx, 300, fz), b, site);
            float farFromRadar = StormChaseRadar.FineLineDbz(new(lx, 300, lz), b, new(lx + 90000, 25, lz));
            Console.WriteLine($"Fine line: {onLine:0.0} dBZ on the line, {far:0.0} in clear warm-sector air, {farFromRadar:0.0} at 90 km from the radar.");
            Check(onLine is > 14 and < 18 && far == 0 && farFromRadar == 0,
                "Radar fine line returns weak clear-air echo just inside the warm side, nothing away from it or beyond ~70 km");
            var behind = StormChaseRadar.BoundaryWindShift(new(dx, 0, dz), b, 50);
            var ahead = StormChaseRadar.BoundaryWindShift(new(fx, 0, fz), b, 50);
            var aloft = StormChaseRadar.BoundaryWindShift(new(dx, 0, dz), b, 4000);
            float behindSpeed = MathF.Sqrt(behind.East * behind.East + behind.North * behind.North);
            Check(behindSpeed > 3 && MathF.Abs(ahead.East) + MathF.Abs(ahead.North) < .2f
                && MathF.Abs(aloft.East) + MathF.Abs(aloft.North) < behindSpeed * .1f
                && behind.East * b.NormalX + behind.North * b.NormalZ > 0,
                "Velocity shows a low-level wind shift pushing toward the boundary from behind, absent aloft and ahead");
            var cache = new StormChaseRadarCache();
            var sweep = cache.Get(0, 0, StormChaseWorld.PlayableCenterZ, StormChaseWorld.PlayableExtentMeters * .5f,
                StormChaseRadarProduct.Reflectivity, null, null, b);
            int lineCells = sweep.Values.Count(v => v is >= 10 and < 20);
            Check(lineCells is > 10 and < 400, $"Regional reflectivity draws a thin fine line ({lineCells} of 2304 cells)");
        }
        {
            // Storm structure felt on the ground, from the same rendered storm the radar and sky use.
            var structureFrame = CreateLiveFrame(0, (float)source.ElapsedSeconds, source.Environment,
                surface.Width, surface.Height, false, false, 930000, camera);
            var storms = structureFrame.Weather!.ActiveStorms;
            var cell = storms.Single(s => s.Id == source.StormId);
            float radius = MathF.Max(80, cell.CoreRadiusMeters);
            var far = StormChaseStormFields.Sample(cell.Position + new Vector3f(25000, 0, 0), storms);
            var core = StormChaseStormFields.Sample(cell.Position + cell.ForwardFlankCoreOffset, storms);
            var rfdCenter = cell.Position + cell.RearFlankCoreOffset;
            float ox = cell.RearFlankCoreOffset.X, oz = cell.RearFlankCoreOffset.Z, ol = MathF.Max(1, MathF.Sqrt(ox * ox + oz * oz));
            var surgePoint = rfdCenter + new Vector3f(ox / ol, 0, oz / ol) * (radius * 1.1f);
            var surge = StormChaseStormFields.Sample(surgePoint, storms);
            int inflowPoints = 0;
            for (int i = 0; i < 16; i++)
            {
                float a = i * MathF.Tau / 16;
                var p = cell.Position + new Vector3f(MathF.Cos(a), 0, MathF.Sin(a)) * (radius * 3f);
                var here = StormChaseStormFields.Sample(p, storms);
                float tx = cell.Position.X - p.X, tz = cell.Position.Z - p.Z, tl = MathF.Sqrt(tx * tx + tz * tz);
                if ((here.Wind.X * tx + here.Wind.Z * tz) / tl > 3) inflowPoints++;
            }
            Console.WriteLine($"Storm structure: far {far.Rain01:0.00}/{far.WindSpeedMetersPerSecond:0.0}; core {core.ReflectivityDbz:0} dBZ rain {core.Rain01:0.00} hail {core.Hail01:0.00} ({core.HailSizeInches:0.00} in) vis {core.VisibilityMeters:0} m; " +
                $"RFD surge {surge.WindSpeedMetersPerSecond:0.0} m/s ({surge.Region}); inflow on {inflowPoints}/16 ring points.");
            Check(far.Rain01 == 0 && far.Hail01 == 0 && far.WindSpeedMetersPerSecond < .5f, "25 km from the storm the air is calm and dry");
            Check(core.Rain01 > .6f && core.VisibilityMeters < 2000 && core.Hail01 > .2f && core.HailSizeInches >= 1,
                "The forward-flank core brings torrential rain, low visibility and 1-inch-plus hail");
            float sx = surgePoint.X - rfdCenter.X, sz = surgePoint.Z - rfdCenter.Z;
            Check(surge.WindSpeedMetersPerSecond >= 18 && surge.Wind.X * sx + surge.Wind.Z * sz > 0,
                "The rear-flank downdraft surges outward at damaging speed without a tornado's help");
            Check(inflowPoints >= 3, "Low-level inflow converges toward the updraft outside the downdrafts");

            // Field-probe types: each earns where its instrument belongs, and the specialists earn nothing elsewhere.
            var weather = structureFrame.Weather!;
            var tornadoAt = cell.Tornadoes.First().Position;
            var inflowAt = Enumerable.Range(0, 16).Select(i => cell.Position + new Vector3f(MathF.Cos(i * MathF.Tau / 16), 0, MathF.Sin(i * MathF.Tau / 16)) * (radius * 3f))
                .First(p => StormChaseStormFields.Sample(p, storms).Region == "Inflow");
            Vector3f Toward(float meters) => tornadoAt + new Vector3f(meters, 0, 0);
            // Wind instruments go where the vortex wind peaks (around the core radius), not in the calm eye.
            var peakWindAt = Enumerable.Range(0, 16 * 12).Select(i => tornadoAt + new Vector3f(MathF.Cos(i % 16 * MathF.Tau / 16), 0,
                    MathF.Sin(i % 16 * MathF.Tau / 16)) * (10 + i / 16 * 20f))
                .MaxBy(p => StormChaseDestruction.SampleWindSpeedMph(p, structureFrame.Weather));
            var placements = new (string Type, Vector3f Right, Vector3f Wrong)[]
            {
                ("probe-tripod", peakWindAt, cell.Position + new Vector3f(25000, 0, 0)),
                ("probe-turtle", peakWindAt, cell.Position + new Vector3f(25000, 0, 0)),
                ("probe-hardened", peakWindAt, cell.Position + new Vector3f(25000, 0, 0)),
                ("probe-sled", tornadoAt, surgePoint),
                ("probe-video", Toward(300), Toward(2000)),
                ("probe-sonde", inflowAt, cell.Position + cell.ForwardFlankCoreOffset),
                ("probe-impact", peakWindAt, inflowAt),
                ("probe-hailpad", cell.Position + cell.ForwardFlankCoreOffset, inflowAt),
                ("probe-mesonet", surgePoint, cell.Position + new Vector3f(25000, 0, 0)),
                ("probe-lidar", Toward(1500), tornadoAt),
            };
            var strongWeather = weather with { Storms = weather.ActiveStorms.Select(s => s.Id != cell.Id ? s : s with
                { Tornadoes = s.Tornadoes.Select(t => t with { WindSpeed = 140, SmoothedWindSpeed = 140 }).ToArray() }).ToArray() };
            var earnings = new List<string>(); int correct = 0;
            foreach (var (typeId, right, wrong) in placements)
            {
                var type = StormChaseProbeTypes.Get(typeId);
                // The debris sensor needs a strong tornado (75+ mph); this source tornado is still forming,
                // so it is sampled against the same vortex strengthened to 140 mph.
                var sampled = type.Measure == StormChaseProbeMeasure.Impact ? strongWeather : weather;
                var good = StormChaseProbeTypes.Sample(type, right, sampled, 0, true);
                var bad = StormChaseProbeTypes.Sample(type, wrong, sampled, 0, true);
                double goodPay = good.DollarsPerSecond * 10 + good.LumpSum, badPay = bad.DollarsPerSecond * 10 + bad.LumpSum;
                earnings.Add($"{type.ShortName} {goodPay:0}/{badPay:0} ({good.Reading})");
                if (goodPay > 20 && goodPay > badPay * 3) correct++;
            }
            Console.WriteLine("Probe earnings per 10 s, right/wrong placement: " + string.Join("; ", earnings));
            Check(correct == placements.Length && StormChaseProbeTypes.All.Select(t => t.Id).SequenceEqual(StormChaseModelLibrary.ProbeModelIds),
                "All ten probe types earn in their own part of the storm and far less (or nothing) out of place");
        }
        foreach (string page in new[] { "reflectivity", "velocity", "forecast", "regional", "regional-reflectivity" })
        {
            hud.ForecastOpen = page == "forecast";
            hud.RegionalMap = page.StartsWith("regional");
            hud.RadarProduct = page is "velocity" or "regional" ? StormChaseRadarProduct.RadialVelocity : StormChaseRadarProduct.Reflectivity;
            hud.InvalidateRadar();
            var frame = CreateLiveFrame(number, (float)source.ElapsedSeconds, source.Environment,
                surface.Width, surface.Height, false, false, 940000 + number, camera);
            hud.Update(surface, page.StartsWith("regional") ? regionalTelemetry : t, frame);
            Check(hud.Ui is { Vertices.Count: > 0 }, page + " creates native DX12 UI geometry");

            if (page == "forecast")
            {
                var text = hud.LastFrame.DrawCommands.OfType<GuiTextCommand>().Select(command => command.Text).ToArray();
                Check(text.Any(value => value.Contains("CAPE")) && text.Any(value => value.Contains("STP"))
                    && text.Any(value => value.Contains("VTP-G")) && text.Any(value => value.Contains("THREE DAY")),
                    "Forecast exposes received atmospheric observations, honest composites and outlook");
                var buttons = hud.LastFrame.Elements.Where(element => element.IsInteractive).ToArray();
                Check(buttons.Length == 3 && buttons.All(button => button.Bounds.X >= 0 && button.Bounds.Y >= 0
                    && button.Bounds.Right <= hud.Ui!.Width && button.Bounds.Bottom <= hud.Ui.Height),
                    "Forecast close and product controls fit the native viewport");
                var button = buttons.Single(element => element.ElementId == "radar/velocity").Bounds;
                hud.Update(surface, t, frame, new(new(true, button.X + 15, button.Y + 10, true, false)));
                hud.Update(surface, t, frame, new(new(true, button.X + 15, button.Y + 10, false, true)));
                Check(hud.LastFrame.Interaction.ActivatedElementId == "radar/velocity"
                    && hud.HandleWeatherGuiAction("radar/velocity") && hud.RadarProduct == StormChaseRadarProduct.RadialVelocity,
                    "Native product button activates the actual shared HUD action");
            }
            frame = frame with { Ui = hud.Ui };
            ExportCompleted.SetValue(backend, false);
            for (int warm = 0; warm < 40 && !(bool)ExportCompleted.GetValue(backend)!; warm++)
                await backend.RenderAsync(frame with { FrameNumber = number++ }, default);
            Check((bool)ExportCompleted.GetValue(backend)! && backend.LastFrameStatistics.RequiredResourcesResident,
                page + " captured resident assets and UI in actual DX12");
            if (!(bool)ExportCompleted.GetValue(backend)!) throw new InvalidOperationException("Native radar UI frame did not export.");
            string path = Path.Combine(output, "radar-ui-" + page + ".png"); File.Copy(staging, path, true);
            using var image = Image.Load<Rgba32>(path); int colored = 0;
            if (page == "forecast")
            {
                float wide = Math.Min(860, surface.Width - 40), tall = Math.Min(574, surface.Height - 40);
                int left = (int)((surface.Width - wide) / 2 + wide - 287), top = (int)((surface.Height - tall) / 2 + 127);
                image.ProcessPixelRows(rows =>
                {
                    for (int y = top; y < Math.Min(image.Height, top + 220); y++)
                    for (int x = left; x < Math.Min(image.Width, left + 220); x++)
                    {
                        var pixel = rows.GetRowSpan(y)[x];
                        if (pixel.R > 150 && pixel.G > 120 && pixel.B < 120 || pixel.B > 130 && pixel.G > 100 && pixel.R < 130
                            || pixel.G > 150 && pixel.B > 110 && pixel.R < 130) colored++;
                    }
                });
                Check(colored > 60, "Actual native forecast pixels contain the height-colored hodograph");
            }
            if (page == "regional")
            {
                // Boundary violet (236,128,255) cannot come from the red/green velocity palette.
                image.ProcessPixelRows(rows =>
                {
                    for (int y = 0; y < image.Height; y++)
                        foreach (var pixel in rows.GetRowSpan(y))
                            if (pixel.R > 170 && pixel.B > 190 && pixel.G < 150) colored++;
                });
                Check(colored > 80, "Actual native regional map pixels contain the dashed boundary lines");
            }
            captures.Add(new { Page = page, Path = path, Width = image.Width, Height = image.Height,
                NativeDx12 = true, HodographColoredPixels = colored, VertexCount = hud.Ui!.Vertices.Count });
        }
        validation.CollectErrors(backend, "radar and atmospheric UI", true);
        File.WriteAllText(Path.Combine(output, "radar-ui-check.json"), JsonSerializer.Serialize(new
        {
            Passed = passed, Checks = checks, Captures = captures, Atmosphere = atmosphere,
            SourceStorm = new { source.Seed, source.StormId, source.Form, source.Phase, source.DayNumber },
            Reflectivity = "Game precipitation proxy", Velocity = "Physical wind projected from fixed site",
            HudIsNativeDx12 = true
        }, JsonOptions));
        Console.WriteLine($"Radar/forecast native UI: {checks.Count} checks, passed={passed}.");
        return passed ? 0 : 1;
    }
}
