using System.Numerics;
using Shared.Contracts.Rendering;
using Shared.Contracts.World;

/// <summary>
/// What the storm is doing at one spot on the ground. Rain comes from the same reflectivity field the radar
/// shows, so driving into a red echo means driving into a downpour. Winds are the storm's own circulation
/// (rear-flank downdraft surge, forward-flank outflow, inflow toward the updraft), not the tornado's.
/// </summary>
internal readonly record struct StormLocalConditions(float ReflectivityDbz, float Rain01, float Hail01, float HailSizeInches,
    Vector3 Wind, float Visibility01, string Region)
{
    public static readonly StormLocalConditions Calm = new(0, 0, 0, 0, Vector3.Zero, 1, "");
    public float WindSpeedMetersPerSecond => MathF.Sqrt(Wind.X * Wind.X + Wind.Z * Wind.Z);
    /// <summary>Approximate visibility in metres (heavy rain in a supercell core is a few hundred metres).</summary>
    public float VisibilityMeters => 150 + 19850 * Visibility01 * Visibility01;
    /// <summary>Short HUD text ("HAIL 1.75 IN · GUSTS 62 MPH"), empty in benign conditions.</summary>
    public string Summary(float gustFactor = 1.3f)
    {
        var parts = new List<string>();
        if (Hail01 >= .08f) parts.Add($"HAIL {StormChaseStormFields.CommonHailSize(HailSizeInches):0.00} IN");
        else if (Rain01 >= .55f) parts.Add("TORRENTIAL RAIN");
        else if (Rain01 >= .3f) parts.Add("HEAVY RAIN");
        float gustMph = WindSpeedMetersPerSecond * gustFactor / .44704f;
        if (gustMph >= 40) parts.Add($"GUSTS {gustMph:0} MPH");
        if (Visibility01 < .35f) parts.Add($"VIS {VisibilityMeters:0} M");
        return string.Join("  ·  ", parts);
    }
}

internal static class StormChaseStormFields
{
    /// <summary>0..1 maturity from the smoothed lifecycle stage (shared with the radar).</summary>
    public static float MatureAmount(RenderStormCellState storm) => Math.Clamp(((storm.SmoothedStage
        ?? storm.Stage + storm.Energy / 100f) - 1.35f) / 1.15f, 0, 1);

    /// <summary>Unscaled rain, hail and rear-flank precipitation fields of one storm at a point.</summary>
    public static (float Rain, float Hail, float Rear) Cores(Vector3f point, RenderStormCellState storm)
    {
        float coreRadius = MathF.Max(80, storm.CoreRadiusMeters);
        float x = point.X - storm.Position.X, z = point.Z - storm.Position.Z;
        float cosine = MathF.Cos(storm.RotationRadians), sine = MathF.Sin(storm.RotationRadians);
        // Flank offsets already have the parent rotation applied by the campaign.
        float fx = x - storm.ForwardFlankCoreOffset.X, fz = z - storm.ForwardFlankCoreOffset.Z;
        float localX = fx * cosine + fz * sine, localZ = -fx * sine + fz * cosine;
        float rain = MathF.Exp(-(localX * localX / (coreRadius * coreRadius * .70f)
            + localZ * localZ / (coreRadius * coreRadius * 1.55f))) * Math.Clamp(storm.PrecipitationCoreAmount, 0, 1);
        float hail = MathF.Exp(-(fx * fx + fz * fz) / (coreRadius * coreRadius * .16f))
            * Math.Clamp(storm.HailCoreAmount, 0, 1);
        float rx = x - storm.RearFlankCoreOffset.X, rz = z - storm.RearFlankCoreOffset.Z;
        float rear = MathF.Exp(-(rx * rx + rz * rz) / (coreRadius * coreRadius * .24f))
            * storm.RearFlankDowndraftAmount * storm.PrecipitationCoreAmount * .28f;
        return (rain, hail, rear);
    }

    public static float ReflectivityDbz(Vector3f point, IReadOnlyList<RenderStormCellState> storms)
    {
        float linearReturn = 0;
        foreach (var storm in storms)
        {
            if (storm.VisualOnly) continue;
            float maturity = MatureAmount(storm);
            if (maturity <= 0) continue;
            var (rain, hail, rear) = Cores(point, storm);
            float hook = HookReturn(point, storm);
            float returnAmount = maturity * Math.Clamp(storm.Intensity, 0, 1.5f) * (rain + rear + hook + hail * .85f);
            if (returnAmount > .003f) linearReturn += MathF.Pow(10, (20 + 40 * Math.Clamp(returnAmount, 0, 1.3f)) / 10);
        }
        return linearReturn > 0 ? Math.Clamp(10 * MathF.Log10(linearReturn), 0, 75) : 0;
    }

    public static float HookReturn(Vector3f point, RenderStormCellState storm)
    {
        float organized = MatureAmount(storm) * Math.Clamp(storm.WallCloudAmount, 0, 1)
            * Math.Clamp(storm.HookEchoAmount, 0, 1);
        if (organized <= 0) return 0;
        var center = storm.Tornadoes.FirstOrDefault()?.Position
            ?? storm.Position + storm.RearFlankCoreOffset;
        float x = point.X - center.X, z = point.Z - center.Z;
        float c = MathF.Cos(storm.RotationRadians), s = MathF.Sin(storm.RotationRadians);
        float localX = x * c + z * s, localZ = -x * s + z * c;
        float radius = MathF.Max(80, storm.UpdraftRadiusMeters * 1.35f);
        float r = MathF.Sqrt(x * x + z * z);
        float shell = MathF.Exp(-MathF.Pow((r - radius) / (radius * .40f), 2));
        float angle = MathF.Atan2(localZ, localX);
        // An open precipitation hook develops into an increasingly wrapped ring.
        float openArc = .15f + .85f * Math.Clamp((MathF.Cos(angle - .7f) + .35f) / 1.35f, 0, 1);
        float wrap = Math.Clamp(storm.RainWrapAmount, 0, 1);
        return shell * organized * storm.PrecipitationCoreAmount * (.25f + .65f * wrap)
            * (openArc * (1 - wrap) + wrap);
    }

    /// <summary>Snap to the sizes spotters report (pea 0.25 … softball 4.00).</summary>
    public static float CommonHailSize(float inches)
    {
        float[] sizes = [.25f, .50f, .75f, 1.00f, 1.25f, 1.50f, 1.75f, 2.00f, 2.50f, 2.75f, 3.00f, 4.00f];
        return sizes.MinBy(size => MathF.Abs(size - inches));
    }

    /// <summary>
    /// Storm-scale conditions at a ground point (mean wind; callers add gusts). Rain and hail come from the
    /// strongest overlapping core, winds from all storms' circulations combined. Deterministic.
    /// </summary>
    public static StormLocalConditions Sample(Vector3f point, IReadOnlyList<RenderStormCellState> storms)
    {
        if (storms.Count == 0) return StormLocalConditions.Calm;
        float dbz = ReflectivityDbz(point, storms);
        float hail01 = 0, hailSize = 0, rfdSpeed = 0, outflowSpeed = 0, inflowSpeed = 0;
        var wind = Vector3.Zero;
        foreach (var storm in storms)
        {
            if (storm.VisualOnly) continue;
            float m = MatureAmount(storm) * Math.Clamp(storm.Intensity, 0, 1.3f);
            if (m <= .01f) continue;
            float radius = MathF.Max(80, storm.CoreRadiusMeters);
            var (_, hail, _) = Cores(point, storm);
            float stormHail = Math.Clamp(hail * m * 1.3f, 0, 1);
            if (stormHail > hail01)
            {
                hail01 = stormHail;
                // Stone size grows with the hail core and the storm's strength; the edge of the shaft is smaller.
                hailSize = .5f + 3.0f * Math.Clamp(storm.HailCoreAmount, 0, 1) * Math.Clamp(m, 0, 1) * MathF.Sqrt(Math.Clamp(hail, 0, 1));
            }
            var motion = new Vector3(storm.Velocity.X, 0, storm.Velocity.Z);
            // Rear-flank downdraft: a surge spreading out from behind the updraft, strongest ~1 core radius out.
            // A baseline exists in any mature supercell; it strengthens as the RFD (and tornado) organises.
            float rfdAmount = Math.Clamp(MathF.Max(storm.RearFlankDowndraftAmount, .4f * Math.Clamp(m, 0, 1)), 0, 1);
            var rfd = Outflow(point, storm.Position + storm.RearFlankCoreOffset, radius * 1.1f, (12 + 26 * rfdAmount) * m, out float rfdEnvelope);
            // Forward-flank downdraft: rain-cooled air spreading from the precipitation core.
            var ffd = Outflow(point, storm.Position + storm.ForwardFlankCoreOffset, radius * 1.3f,
                14 * m * Math.Clamp(storm.PrecipitationCoreAmount, 0, 1), out float ffdEnvelope);
            // Inflow: low-level air converging toward the updraft from outside the downdrafts.
            float ix = storm.Position.X - point.X, iz = storm.Position.Z - point.Z, ir = MathF.Sqrt(ix * ix + iz * iz);
            float inflowEnvelope = MathF.Exp(-MathF.Pow(ir / (radius * 4.5f), 2)) * (1 - MathF.Exp(-MathF.Pow(ir / radius, 2)));
            var inflow = ir > 1 ? new Vector3(ix / ir, 0, iz / ir) * 12 * m * inflowEnvelope * (1 - Math.Clamp(rfdEnvelope + ffdEnvelope, 0, 1)) : Vector3.Zero;
            // Downdraft air carries part of the storm's own motion with it.
            wind += rfd + ffd + inflow + motion * .5f * Math.Clamp(rfdEnvelope + ffdEnvelope, 0, 1);
            rfdSpeed = MathF.Max(rfdSpeed, rfd.Length()); outflowSpeed = MathF.Max(outflowSpeed, ffd.Length());
            inflowSpeed = MathF.Max(inflowSpeed, inflow.Length());
        }
        // Marshall-Palmer style: ~20 dBZ drizzle, 40 dBZ heavy, 55+ dBZ torrential.
        float rain01 = Smooth((dbz - 18) / 40);
        float visibility = Math.Clamp(1 - .92f * Smooth((dbz - 25) / 32), .03f, 1) * (1 - .35f * hail01);
        string region = hail01 >= .2f ? "Hail core"
            : rfdSpeed >= 15 && rfdSpeed >= outflowSpeed ? "Rear-flank downdraft"
            : dbz >= 48 ? "Precipitation core"
            : outflowSpeed >= 8 ? "Outflow"
            : inflowSpeed >= 5 ? "Inflow"
            : dbz >= 25 ? "Rain shield" : "";
        return new(dbz, rain01, hail01, hail01 > .02f ? hailSize : 0, wind, visibility, region);
    }

    /// <summary>
    /// How much of a view toward <paramref name="target"/> is hidden by falling rain and hail (0 clear, 1 opaque):
    /// optical depth integrated along the ground track using the same extinction as local visibility.
    /// This is what makes a tornado "rain-wrapped" for a viewer looking through the core.
    /// </summary>
    public static float RainObscuration(Vector3f from, Vector3f target, IReadOnlyList<RenderStormCellState> storms)
    {
        float dx = target.X - from.X, dz = target.Z - from.Z, length = MathF.Sqrt(dx * dx + dz * dz);
        if (length < 1 || storms.Count == 0) return 0;
        const int Steps = 16;
        float depth = 0, step = length / Steps;
        for (int i = 0; i < Steps; i++)
        {
            float t = (i + .5f) / Steps;
            float dbz = ReflectivityDbz(new(from.X + dx * t, 0, from.Z + dz * t), storms);
            float visibility = Math.Clamp(1 - .92f * Smooth((dbz - 25) / 32), .03f, 1);
            depth += 3f / (150 + 19850 * visibility * visibility) * step;
        }
        return 1 - MathF.Exp(-depth);
    }

    /// <summary>Deterministic gust multiplier around 1 (0.75–1.45), stronger where the mean wind is strong.</summary>
    public static float GustFactor(Vector3f point, float seconds, float windSpeed)
    {
        float phase = (point.X * .0021f + point.Z * .0017f) % MathF.Tau;
        float wave = MathF.Sin(seconds * 1.9f + phase) * .55f + MathF.Sin(seconds * .71f + phase * 1.7f) * .3f
            + MathF.Sin(seconds * 4.3f + phase * .4f) * .15f;
        float strength = Math.Clamp(windSpeed / 25, 0, 1);
        return 1 + wave * (.12f + .33f * strength);
    }

    private static Vector3 Outflow(Vector3f point, Vector3f center, float r0, float peak, out float envelope)
    {
        float dx = point.X - center.X, dz = point.Z - center.Z, r = MathF.Sqrt(dx * dx + dz * dz);
        float q = r / MathF.Max(1, r0);
        // The cold pool ends at its gust front (~2.4 r0); beyond it the storm's inflow takes over.
        float pool = 1 - Smooth((q - 1.6f) / .8f);
        envelope = MathF.Exp(-q * q * .5f) * pool;
        if (r < 1 || peak <= 0) return Vector3.Zero;
        // Radial outflow peaking at r0 (q * exp((1 - q²)/2) has its maximum, 1, at q = 1).
        float speed = peak * q * MathF.Exp(.5f * (1 - q * q)) * pool;
        return new Vector3(dx / r, 0, dz / r) * speed;
    }

    private static float Smooth(float v) { v = Math.Clamp(v, 0, 1); return v * v * (3 - 2 * v); }
}
