using Shared.Contracts.Rendering;
using Shared.Contracts.World;
using StormChase.Protocol;

internal enum StormChaseRadarProduct { Reflectivity, RadialVelocity }
internal sealed record StormChaseRadarSweep(float CenterX, float CenterZ, float RangeMeters,
    StormChaseRadarProduct Product, float[] Values, IReadOnlyList<StormChaseRotationSignal> Signals, long Revision);
internal readonly record struct StormChaseRotationSignal(Vector3f Position, float EstimatedWindMph,
    float Confidence, bool SurfaceConfirmed);

/// <summary>Game radar products sampled from the same received storm state that renders the sky.
/// Reflectivity is a precipitation proxy, not a scattering simulation; velocity is radial SI wind.</summary>
internal static class StormChaseRadar
{
    public const int Resolution = 48;
    public const float SweepIntervalSeconds = .25f;
    public static readonly Vector3f RadarSite = new(5800, 25, 7800);

    public static float ReflectivityDbz(Vector3f point, IReadOnlyList<RenderStormCellState> storms) =>
        StormChaseStormFields.ReflectivityDbz(point, storms);

    /// <summary>
    /// Clear-air "fine line": insects and refractive-index gradients collected by boundary convergence
    /// return 8-17 dBZ in a band ~1.5 km into the moist side. Only the edges of the warm sector show,
    /// and the low tilt overshoots the shallow line beyond ~70 km from the radar.
    /// </summary>
    public static float FineLineDbz(Vector3f point, StormChaseBoundary boundary, Vector3f? radarSite = null)
    {
        var site = radarSite ?? RadarSite;
        float rx = point.X - site.X, rz = point.Z - site.Z;
        float visibility = Math.Clamp(1 - MathF.Sqrt(rx * rx + rz * rz) / 70000, 0, 1);
        if (visibility <= 0) return 0;
        float primary = boundary.PrimaryDistance(point.X, point.Z), secondary = boundary.SecondaryDistance(point.X, point.Z);
        float line = 0;
        if (secondary > -500) line = MathF.Exp(-MathF.Pow((primary - 1500) / 1100, 2));
        if (boundary.HasSecondary && primary > -500) line = MathF.Max(line, .8f * MathF.Exp(-MathF.Pow((secondary - 1200) / 1000, 2)));
        return line < .05f ? 0 : (8 + 9 * line) * MathF.Sqrt(visibility);
    }

    /// <summary>
    /// Low-level wind change across the boundary for the velocity product: air behind the line pushes
    /// toward it (a cold front hardest, a warm front least), fading above ~1 km. Returned as east/north m/s.
    /// </summary>
    public static (float East, float North) BoundaryWindShift(Vector3f point, StormChaseBoundary boundary, float aglMeters)
    {
        float depth = MathF.Exp(-MathF.Max(0, aglMeters) / 1200);
        float primary = boundary.PrimaryDistance(point.X, point.Z), secondary = boundary.SecondaryDistance(point.X, point.Z);
        static float Behind(float s) { float v = Math.Clamp((-s + 300) / 2500, 0, 1); return v * v * (3 - 2 * v); }
        float push = boundary.Kind switch { "Cold front" => 9, "Dryline" => 6, _ => 4 } * Behind(primary);
        float east = boundary.NormalX * push, north = boundary.NormalZ * push;
        if (boundary.HasSecondary)
        {
            float warm = 4 * Behind(secondary);
            east += boundary.SecondaryNormalX * warm; north += boundary.SecondaryNormalZ * warm;
        }
        return (east * depth, north * depth);
    }

    internal static float HookReturn(Vector3f point, RenderStormCellState storm) => StormChaseStormFields.HookReturn(point, storm);

    public static float RadialVelocityMetersPerSecond(Vector3f point, RenderWeatherState? weather,
        Vector3f? radarSite = null, AtmosphereSnapshot? atmosphere = null, StormChaseBoundary? boundary = null)
    {
        var site = radarSite ?? RadarSite;
        float dx = point.X - site.X, dz = point.Z - site.Z;
        float distance = MathF.Sqrt(dx * dx + dz * dz);
        if (distance < 1 || weather is null) return 0;
        // This sampler is also used by native debris, vehicles and deployed probes.
        var wind = StormChaseDestruction.SampleWindVelocity(point, weather);
        if (wind.LengthSquared() < .01f)
        {
            if (atmosphere is { Sounding.IsDefaultOrEmpty: false })
            {
                float agl = MathF.Max(0, point.Y - atmosphere.SamplePosition.Y);
                var levels = atmosphere.Sounding;
                int index = 0;
                while (index + 1 < levels.Length && levels[index + 1].HeightMeters < agl) index++;
                var lower = levels[index]; var upper = levels[Math.Min(index + 1, levels.Length - 1)];
                float fraction = Math.Clamp((agl - lower.HeightMeters) / MathF.Max(1, upper.HeightMeters - lower.HeightMeters), 0, 1);
                wind.X = lower.WindEastMetersPerSecond + (upper.WindEastMetersPerSecond - lower.WindEastMetersPerSecond) * fraction;
                wind.Z = lower.WindNorthMetersPerSecond + (upper.WindNorthMetersPerSecond - lower.WindNorthMetersPerSecond) * fraction;
            }
            if (boundary is { } line)
            {
                var shift = BoundaryWindShift(point, line, point.Y - (atmosphere?.SamplePosition.Y ?? 0));
                wind.X += shift.East; wind.Z += shift.North;
            }
            foreach (var storm in weather.ActiveStorms)
            {
                if (storm.VisualOnly) continue;
                float sx = point.X - storm.Position.X, sz = point.Z - storm.Position.Z;
                float range = MathF.Sqrt(sx * sx + sz * sz);
                float envelope = MathF.Exp(-range * range / MathF.Max(1, storm.CoreRadiusMeters * storm.CoreRadiusMeters * 3));
                float inflow = 12 * MatureAmount(storm) * Math.Clamp(storm.Intensity, 0, 1.3f) * envelope;
                float radialX = range > 1 ? -sx / range : storm.InflowDirection.X;
                float radialZ = range > 1 ? -sz / range : storm.InflowDirection.Z;
                wind.X += (storm.Velocity.X * envelope + radialX * inflow);
                wind.Z += (storm.Velocity.Z * envelope + radialZ * inflow);
            }
        }
        return Math.Clamp((wind.X * dx + wind.Z * dz) / distance, -150, 150);
    }

    public static IReadOnlyList<StormChaseRotationSignal> RotationSignals(RenderWeatherState? weather)
    {
        var signals = new List<StormChaseRotationSignal>();
        foreach (var storm in weather?.ActiveStorms ?? [])
        {
            if (storm.VisualOnly || MatureAmount(storm) < .5f || storm.WallCloudAmount < .2f) continue;
            if (storm.Tornadoes.Count == 0)
            {
                float confidence = Math.Clamp(storm.RadarConfidence * storm.WallCloudAmount, 0, 1);
                if (confidence > .25f) signals.Add(new(storm.Position + storm.RearFlankCoreOffset, 0, confidence, false));
                continue;
            }
            foreach (var tornado in storm.Tornadoes)
            {
                if (tornado.VisualOnly || (tornado.SmoothedWindSpeed ?? tornado.WindSpeed) < 15) continue;
                float dx = tornado.Position.X - RadarSite.X, dz = tornado.Position.Z - RadarSite.Z;
                float distance = MathF.Max(1, MathF.Sqrt(dx * dx + dz * dz));
                float radius = MathF.Max(tornado.BaseRadiusMeters, (tornado.SmoothedWidthMeters ?? tornado.WidthMeters) / 2.5f);
                float px = -dz / distance * radius, pz = dx / distance * radius;
                float first = RadialVelocityMetersPerSecond(tornado.Position + new Vector3f(px, 2, pz), weather);
                float second = RadialVelocityMetersPerSecond(tornado.Position + new Vector3f(-px, 2, -pz), weather);
                float confidence = Math.Clamp(storm.RadarConfidence * (1 - distance / 85000), .05f, 1);
                float estimate = MathF.Abs(first - second) * .5f / .44704f;
                if (estimate >= 15 && confidence >= .12f) signals.Add(new(tornado.Position, estimate, confidence,
                    tornado.TouchdownAmount >= .95f));
            }
        }
        return signals;
    }

    private static float MatureAmount(RenderStormCellState storm) => StormChaseStormFields.MatureAmount(storm);
}

/// <summary>Bounded four-Hz product work; markers and GUI interaction still update each frame.</summary>
internal sealed class StormChaseRadarCache
{
    private StormChaseRadarSweep? sweep;
    private double sampledAt = double.NegativeInfinity;
    public long RebuildCount { get; private set; }
    /// <summary>Forget the cached sweep (e.g. after the observer teleports).</summary>
    public void Invalidate() => sweep = null;

    public StormChaseRadarSweep Get(double seconds, float centerX, float centerZ, float range,
        StormChaseRadarProduct product, RenderWeatherState? weather, AtmosphereSnapshot? atmosphere = null,
        StormChaseBoundary? boundary = null)
    {
        bool modeChanged = sweep is null || sweep.Product != product || sweep.RangeMeters != range;
        if (!modeChanged && seconds >= sampledAt && seconds - sampledAt < StormChaseRadar.SweepIntervalSeconds)
            return sweep!;
        var values = new float[StormChaseRadar.Resolution * StormChaseRadar.Resolution];
        for (int row = 0; row < StormChaseRadar.Resolution; row++)
        for (int column = 0; column < StormChaseRadar.Resolution; column++)
        {
            float dx = (column / 47f - .5f) * range * 2, dz = (.5f - row / 47f) * range * 2;
            if (dx * dx + dz * dz > range * range) continue;
            float wx = centerX + dx, wz = centerZ + dz;
            float radarX = wx - StormChaseRadar.RadarSite.X, radarZ = wz - StormChaseRadar.RadarSite.Z;
            float siteDistance = MathF.Sqrt(radarX * radarX + radarZ * radarZ);
            // Half-degree lowest tilt and a standard 4/3-Earth-radius beam approximation.
            float beamHeight = StormChaseRadar.RadarSite.Y + siteDistance * .0087269f + siteDistance * siteDistance / 16_990_000;
            var point = new Vector3f(wx, beamHeight, wz);
            values[row * StormChaseRadar.Resolution + column] = product == StormChaseRadarProduct.Reflectivity
                ? MathF.Max(StormChaseRadar.ReflectivityDbz(point, weather?.ActiveStorms ?? []),
                    boundary is { } line ? StormChaseRadar.FineLineDbz(point, line) : 0)
                : StormChaseRadar.RadialVelocityMetersPerSecond(point, weather, atmosphere: atmosphere, boundary: boundary);
        }
        sampledAt = seconds;
        sweep = new(centerX, centerZ, range, product, values, StormChaseRadar.RotationSignals(weather), ++RebuildCount);
        return sweep;
    }
}
