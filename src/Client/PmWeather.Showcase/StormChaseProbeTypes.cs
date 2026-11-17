using Shared.Contracts.Rendering;
using Shared.Contracts.World;

internal enum StormChaseProbeMeasure { Wind, Gusts, Pressure, Video, Sounding, Impact, Hail, RemoteWind }

/// <summary>One deployable instrument: its Blender model, what it measures, how much wind it survives and what it pays.</summary>
internal sealed record StormChaseProbeType(string Id, string ShortName, string Name, StormChaseProbeMeasure Measure,
    float RatingMph, float PayMultiplier, bool RequiresUpgrade, string Summary)
{
    /// <summary>Model id in storm-chase-probes.json (the type id itself).</summary>
    public string ModelId => Id;
}

/// <summary>What one probe saw in one sample, in research dollars per second plus a short HUD reading.</summary>
internal readonly record struct StormChaseProbeSample(double DollarsPerSecond, float WindMph, string Reading, bool OneShotComplete = false, double LumpSum = 0);

/// <summary>
/// The ten field instruments. Each earns research grants from a different part of the storm, so the right
/// probe depends on where it can be placed: in the tornado's path, under the hail shaft, in the inflow,
/// in the rear-flank gusts, or kilometres away with a clear line of sight.
/// </summary>
internal static class StormChaseProbeTypes
{
    public static readonly StormChaseProbeType[] All =
    [
        new("probe-tripod", "TRIPOD", "Tripod anemometer mast", StormChaseProbeMeasure.Wind, 115, 1.0f, false,
            "Starter cup anemometer. Pays for any wind over 40 mph; blows over above 115 mph."),
        new("probe-turtle", "TURTLE", "Low-profile turtle probe", StormChaseProbeMeasure.Wind, 190, 1.5f, true,
            "Hugs the ground under the vortex. Pays 1.5x for wind; survives 190 mph."),
        new("probe-hardened", "HARDENED", "Hardened steel turtle", StormChaseProbeMeasure.Wind, 260, 1.25f, true,
            "Ballasted armour for violent tornadoes. Survives 260 mph."),
        new("probe-sled", "SLED", "Barometric pressure sled", StormChaseProbeMeasure.Pressure, 220, 1.0f, true,
            "Measures the pressure drop in the vortex core. Pays by the depth of the deficit."),
        new("probe-video", "VIDEO", "Armored 360 video pod", StormChaseProbeMeasure.Video, 170, 1.0f, false,
            "Films a tornado passing within 600 m. Rain between pod and funnel spoils the footage."),
        new("probe-sonde", "SONDE", "Radiosonde balloon launcher", StormChaseProbeMeasure.Sounding, 70, 1.0f, false,
            "One launch. Pays well released into a storm's clear inflow, little anywhere else."),
        new("probe-impact", "IMPACT", "Debris impact sensor", StormChaseProbeMeasure.Impact, 230, 1.0f, true,
            "Records debris strikes. Pays only in winds over 75 mph, when debris is airborne."),
        new("probe-hailpad", "HAILPAD", "Hail pad station", StormChaseProbeMeasure.Hail, 110, 1.0f, false,
            "Foam pad dented by hailstones. Pays by hail rate and stone size."),
        new("probe-mesonet", "MESONET", "Portable mesonet tower", StormChaseProbeMeasure.Gusts, 95, 1.2f, false,
            "Samples rear-flank and outflow gusts from 30 mph. Too tall for a tornado."),
        new("probe-lidar", "LIDAR", "Doppler lidar pod", StormChaseProbeMeasure.RemoteWind, 110, 1.0f, false,
            "Scans a tornado 250 m-3 km away. Heavy rain attenuates the beam."),
    ];

    public static StormChaseProbeType Default => All[0];
    public static StormChaseProbeType Get(string? id) => All.FirstOrDefault(type => type.Id == id) ?? Default;

    /// <summary>Sample one probe. <paramref name="seconds"/> drives gusts; the result is deterministic for a given state.</summary>
    public static StormChaseProbeSample Sample(StormChaseProbeType type, Vector3f position, RenderWeatherState? weather,
        float seconds, bool firstSample)
    {
        var storms = weather?.ActiveStorms ?? [];
        float tornadoMph = StormChaseDestruction.SampleWindSpeedMph(position, weather);
        var local = StormChaseStormFields.Sample(position, storms);
        float gustMph = local.WindSpeedMetersPerSecond * StormChaseStormFields.GustFactor(position, seconds, local.WindSpeedMetersPerSecond) / .44704f;
        float windMph = MathF.Max(tornadoMph, gustMph);
        // Nearest grounded tornado: its distance and peak wind (for pressure, video and lidar).
        float nearest = float.PositiveInfinity, nearestWind = 0, nearestRadius = 50; Vector3f nearestPosition = default;
        foreach (var storm in storms)
            foreach (var tornado in storm.Tornadoes)
            {
                if (tornado.VisualOnly || tornado.TouchdownAmount < .5f) continue;
                float d = StormChaseCareer.HorizontalDistance(tornado.Position, position);
                if (d < nearest)
                {
                    nearest = d; nearestWind = tornado.SmoothedWindSpeed ?? tornado.WindSpeed; nearestPosition = tornado.Position;
                    nearestRadius = MathF.Max(10, (tornado.SmoothedWidthMeters ?? tornado.WidthMeters) / 2);
                }
            }
        string name = type.ShortName;
        switch (type.Measure)
        {
            // Intercept anemometers and the debris sensor are paid for the tornado's wind; storm gusts can still
            // overload them (WindMph), but only the mesonet tower is built to earn from rear-flank/outflow gusts.
            case StormChaseProbeMeasure.Wind:
                return new(tornadoMph >= 40 ? (tornadoMph - 25) * .55 * type.PayMultiplier : 0, windMph, $"{name} {windMph:0} MPH");
            case StormChaseProbeMeasure.Gusts:
                return new(windMph >= 30 ? (windMph - 20) * .5 * type.PayMultiplier : 0, windMph, $"{name} GUST {windMph:0}");
            case StormChaseProbeMeasure.Pressure:
            {
                // Rankine vortex: the deficit is deepest at the centre, ρv²(1 - r²/2R²) inside the core radius R
                // and ρv²R²/2r² outside it (hPa, ρ ≈ 1.1 kg/m³). The calm eye is where the sled earns most.
                float v = nearestWind * .44704f, r = float.IsFinite(nearest) ? nearest : float.PositiveInfinity, R = nearestRadius;
                float deficit = !float.IsFinite(r) ? 0 : 1.1f * v * v * (r <= R ? 1 - r * r / (2 * R * R) : R * R / (2 * r * r)) / 100;
                return new(deficit >= 2 ? deficit * 2.2 * type.PayMultiplier : 0, windMph, $"{name} -{deficit:0.0} HPA");
            }
            case StormChaseProbeMeasure.Video:
            {
                if (nearest > 600) return new(0, windMph, $"{name} NO FUNNEL");
                float clarity = 1 - StormChaseStormFields.RainObscuration(position, nearestPosition, storms);
                double rate = 60 * Math.Pow(1 - nearest / 600, 1.5) * Math.Clamp(nearestWind / 120, .3, 1.6) * clarity * type.PayMultiplier;
                return new(rate, windMph, $"{name} {nearest:0} M {(clarity < .5f ? "RAIN" : "CLEAR")}");
            }
            case StormChaseProbeMeasure.Sounding:
            {
                if (!firstSample) return new(0, windMph, $"{name} RELEASED");
                // Clear air feeding a mature storm is what a forecaster wants sampled.
                bool inflow = local.Region == "Inflow" || (local.Rain01 < .1f && storms.Any(s => !s.VisualOnly
                    && StormChaseStormFields.MatureAmount(s) > .3f && StormChaseCareer.HorizontalDistance(s.Position, position) < 15000));
                return new(0, windMph, $"{name} {(inflow ? "INFLOW" : "AMBIENT")}", true, (inflow ? 250 : 40) * type.PayMultiplier);
            }
            case StormChaseProbeMeasure.Impact:
                return new(tornadoMph >= 75 ? (tornadoMph - 65) * 1.1 * type.PayMultiplier : 0, windMph, $"{name} {windMph:0} MPH");
            case StormChaseProbeMeasure.Hail:
                return new(local.Hail01 * local.HailSizeInches * 25 * type.PayMultiplier, windMph,
                    local.Hail01 >= .05f ? $"{name} {StormChaseStormFields.CommonHailSize(local.HailSizeInches):0.00} IN" : $"{name} NO HAIL");
            default:
            {
                if (nearest is < 250 or > 3000) return new(0, windMph, nearest < 250 ? $"{name} TOO CLOSE" : $"{name} NO TARGET");
                double rate = nearestWind * .25 * (1 - nearest / 3000) * (1 - .8 * local.Rain01) * type.PayMultiplier;
                return new(rate, windMph, $"{name} {nearestWind:0} MPH @{nearest / 1000:0.0}K");
            }
        }
    }
}
