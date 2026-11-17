/// <summary>
/// The Gulf shoreline across the world's south-east corner, shared by the terrain (client and server),
/// the atmosphere (Gulf moisture and the sea-breeze front) and storm initiation. Pure functions only:
/// the authoritative server and every client compute the same coast.
/// Coordinates: along = (x + z)/√2 runs south-west → north-east parallel to the shore;
/// across = (x - z)/√2 increases toward the sea (south-east).
/// </summary>
internal static class StormChaseCoast
{
    public const float SeaLevel = .4f;
    /// <summary>Mean shoreline position on the across axis (≈ 7 km inside the south-east corner).</summary>
    public const float ShoreAcross = 24000;
    /// <summary>Sand from the dunes to just under the water line.</summary>
    public const float BeachInlandMeters = 190, BeachSeawardMeters = 20;
    private const float InverseRoot2 = .70710678f;

    public static float Along(float x, float z) => (x + z) * InverseRoot2;
    private static float Wiggle(float along) => 380 * MathF.Sin(along / 2300 + .7f) + 140 * MathF.Sin(along / 780 + 2.1f);

    /// <summary>Signed distance from the shoreline: positive over the sea, negative inland.</summary>
    public static float SeawardDistance(float x, float z) => (x - z) * InverseRoot2 - ShoreAcross - Wiggle(Along(x, z));
    public static float InlandDistance(float x, float z) => MathF.Max(0, -SeawardDistance(x, z));
    public static bool IsSea(float x, float z) => SeawardDistance(x, z) > 0;
    public static bool IsBeach(float x, float z)
    {
        float s = SeawardDistance(x, z);
        return s > -BeachInlandMeters && s < BeachSeawardMeters;
    }

    /// <summary>A point at <paramref name="along"/>, <paramref name="seaward"/> metres from the shoreline (negative = inland).</summary>
    public static (float X, float Z) Point(float along, float seaward)
    {
        float across = ShoreAcross + Wiggle(along) + seaward;
        return ((along + across) * InverseRoot2, (along - across) * InverseRoot2);
    }

    /// <summary>
    /// Terrain shape near the coast: a low coastal plain, dunes, a beach ramp and a shelving sea floor.
    /// Returns the blended height given the inland prairie height <paramref name="inlandHeight"/>.
    /// </summary>
    public static float ShapeHeight(float x, float z, float inlandHeight)
    {
        float s = SeawardDistance(x, z);
        if (s < -3400) return inlandHeight;
        float coastal;
        if (s <= 0)
        {
            float inland = -s;
            coastal = SeaLevel + 1.8f * Smooth(0, 140, inland) + .003f * inland
                + 1.1f * MathF.Exp(-Square((inland - 175) / 45)) + .6f * MathF.Exp(-Square((inland - 240) / 35));
        }
        else coastal = MathF.Max(-30, SeaLevel - 1.5f * Smooth(0, 120, s) - .009f * s);
        return Lerp(coastal, inlandHeight, Smooth(0, 3400, -s));
    }

    /// <summary>Extra surface dew point (°C) from Gulf moisture: strongest on the coast, decaying smoothly inland.</summary>
    public static float GulfMoistureDew(float x, float z) => 4f * MathF.Exp(-InlandDistance(x, z) / 9000);

    /// <summary>
    /// The day's sea-breeze front. It forms late morning just inland of the beach once the land heats,
    /// pushes inland through the afternoon (a stronger breeze travels further) and dissipates after sunset.
    /// Deterministic for a campaign seed/day; <paramref name="hour"/> is the local hour of that day.
    /// </summary>
    public static StormChaseSeaBreeze SeaBreeze(int campaignSeed, int dayNumber, float hour)
    {
        var random = new Random(unchecked(campaignSeed * 48271 ^ dayNumber * 69621 ^ 0x2c1b3c6d));
        float vigor = .55f + .45f * (float)random.NextDouble();
        float form = Smooth(10.25f, 12.5f, hour), fade = 1 - Smooth(19.25f, 21.5f, hour);
        float progress = Smooth(10.5f, 19f, hour);
        return new(vigor * form * fade, 600 + (2500 + 5500 * vigor) * progress);
    }

    private static float Square(float v) => v * v;
    private static float Lerp(float a, float b, float t) => a + (b - a) * t;
    private static float Smooth(float low, float high, float x) { float t = Math.Clamp((x - low) / (high - low), 0, 1); return t * t * (3 - 2 * t); }
}

/// <summary>
/// A sea-breeze front parallel to the coast, <see cref="InlandMeters"/> inland. Signed distance is positive
/// on the marine (sea) side, matching the "positive = moist side" convention of <see cref="StormChaseBoundary"/>.
/// </summary>
internal readonly record struct StormChaseSeaBreeze(float Strength, float InlandMeters)
{
    public const string Kind = "Sea-breeze front";
    public bool IsActive => Strength > .3f;
    public float SignedDistance(float x, float z) => StormChaseCoast.SeawardDistance(x, z) + InlandMeters;
    /// <summary>A point on the front at <paramref name="along"/>, moved <paramref name="intoMarineSide"/> metres toward the sea.</summary>
    public (float X, float Z) Point(float along, float intoMarineSide) => StormChaseCoast.Point(along, -InlandMeters + intoMarineSide);
}
