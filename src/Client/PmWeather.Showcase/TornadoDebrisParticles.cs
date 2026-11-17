using Shared.Contracts.Content;
using Shared.Contracts.Rendering;
using Shared.Contracts.World;

/// <summary>Supplied mist/smoke textures fill the ground debris dome with three bounded instanced draws.</summary>
internal static class TornadoDebrisParticles
{
    private const int InstancesPerBatch = 48;
    private const float GoldenAngle = 2.3999632f;
    private static readonly AssetHandle ParticleQuad = new("mesh", "engine/particle-quad");
    private static readonly (string Name, float Alpha)[] Layers =
        [("mist", 0.10f), ("smoke_0", 0.20f), ("smoke_1", 0.16f)];

    public static float Radius(float width) => Math.Clamp(width * 0.85f + 20, 35, 400);
    public static float Height(float width) => Math.Clamp(Radius(width) * 0.7f, 30, 140);
    public static (float Radius, float Height) GetDimensions(float width) => (Radius(width), Height(width));

    public static IReadOnlyList<RenderParticleInstanceBatch> CreateBatches(float time, RenderStormCellState storm, SkyGradientState? sky = null,
        string? batchSuffix = null)
    {
        var tornado = storm.Tornadoes.FirstOrDefault();
        if (tornado is null || tornado.VisualOnly || tornado.DebrisAmount <= 0) return [];
        // DebrisAmount already contains the continuous native 45..75 mph ramp.
        // Applying wind/touchdown again would suppress an established 76 mph ball.
        float presence = Math.Clamp(tornado.DebrisAmount, 0, 1);
        float width = tornado.SmoothedWidthMeters ?? storm.SmoothedWidthMeters ?? tornado.WidthMeters;
        float wind = tornado.SmoothedWindSpeed ?? storm.SmoothedWindSpeed ?? tornado.WindSpeed;
        var (radius, height) = GetDimensions(width);
        var center = tornado.Position;
        float angularRate = Math.Clamp(wind * 0.6f / Math.Max(width, 20), 0.18f, 1.2f);
        float liftPeriod = Math.Clamp(18 - wind * 0.04f, 6, 14);
        var lighting = LightingFactor(sky);
        var batches = new RenderParticleInstanceBatch[Layers.Length];

        for (int layer = 0; layer < Layers.Length; layer++)
        {
            var (name, alpha) = Layers[layer];
            var instances = new RenderParticleInstance[InstancesPerBatch];
            for (int index = 0; index < InstancesPerBatch; index++)
            {
                float radialUniform = Fraction((index + 0.5f) / InstancesPerBatch + layer * 0.271f);
                float variation = Fraction(index * 0.7548777f + layer * 0.317f);
                float age = Fraction(index * 0.618034f + layer * 0.233f + time / liftPeriod);
                float puffSize = Math.Clamp(radius * (0.35f + variation * 0.20f), 12, 120);
                float puffHeight = puffSize * (0.72f + variation * 0.18f);
                float radialDistance = MathF.Sqrt(radialUniform) * Math.Max(1, radius - puffSize * 0.45f);
                // Puffs spiral inward as they lift, filling a tapered dome rather
                // than repeating identical rings in each texture batch.
                radialDistance *= 1 - age * 0.14f;
                float angle = index * GoldenAngle + layer * 1.917f + time * angularRate
                    + age * 0.85f + MathF.Sin(time * 0.37f + index * 1.13f) * 0.10f;
                float dome = MathF.Sqrt(Math.Max(0, 1 - radialDistance * radialDistance / (radius * radius)));
                float minimumY = puffHeight * 0.5f + 0.5f;
                float maximumY = Math.Max(minimumY, height * dome - puffHeight * 0.5f);
                float relativeY = minimumY + (maximumY - minimumY) * age;
                float rise = relativeY / height;
                float dry = Math.Clamp(rise * 0.75f + variation * 0.25f, 0, 1);
                // Instance RGB is authored in sRGB; the particle vertex shader
                // converts it to linear. Higher, drier puffs are lighter earth.
                var color = new ColorRgba(LitSrgb(0.60f + dry * 0.10f, lighting.X),
                    LitSrgb(0.48f + dry * 0.11f, lighting.Y), LitSrgb(0.34f + dry * 0.12f, lighting.Z),
                    alpha * presence * MathF.Sqrt(Math.Max(0, MathF.Sin(MathF.PI * age))));
                float cosine = MathF.Cos(angle), sine = MathF.Sin(angle);
                float verticalSpeed = (maximumY - minimumY) / liftPeriod;
                instances[index] = new RenderParticleInstance(
                    new(center.X + cosine * radialDistance, center.Y + relativeY, center.Z + sine * radialDistance),
                    new(puffSize, puffHeight, puffSize), new(0, 0, angle * (180 / MathF.PI) + variation * 90), color,
                    new(-sine * radialDistance * angularRate, verticalSpeed, cosine * radialDistance * angularRate), age, 1);
            }
            batches[layer] = new RenderParticleInstanceBatch("live-debris-" + name + (batchSuffix is null ? "" : "/" + batchSuffix), ParticleQuad,
                new("material", "showcase/" + name), RenderParticleFacingMode.Camera, RenderParticleBlendMode.Alpha,
                SoftParticles: true, SoftParticleDistance: 6, instances);
        }
        return batches;
    }

    private static Vector3f LightingFactor(SkyGradientState? sky)
    {
        if (sky is null) return new(1, 1, 1);
        float solarElevation = SmoothStep(-0.02f, 0.35f, sky.SunDirection.Y);
        float sunlight = Math.Clamp(Math.Max(0, sky.SunIntensity) * solarElevation * 1.5f, 0, 1);
        float moonlight = Math.Clamp(Math.Max(0, sky.MoonIntensity) * SmoothStep(0, 0.35f, sky.MoonDirection.Y), 0, 1);
        float highSun = Math.Clamp(sky.SunDirection.Y / 0.35f, 0, 1);
        float sunMaximum = Math.Max(0.001f, Math.Max(sky.SunColor.R, Math.Max(sky.SunColor.G, sky.SunColor.B)));
        float Channel(float ambient, float moonColor, float sunColor)
        {
            // A small sky floor plus actual ambient/moon lighting keeps night
            // earth dark. The calibrated daylight blend reaches one under the
            // normal afternoon sun, preserving the established daytime palette.
            float night = Math.Clamp(0.006f + Math.Clamp(ambient, 0, 1) * 0.15f
                + Math.Clamp(moonColor, 0, 1) * moonlight * 0.10f, 0, 1);
            float sunTint = Math.Clamp(sunColor / sunMaximum, 0, 1);
            sunTint += (1 - sunTint) * highSun;
            return night + (sunTint - night) * sunlight;
        }
        return new(Channel(sky.AmbientColor.R, sky.MoonColor.R, sky.SunColor.R),
            Channel(sky.AmbientColor.G, sky.MoonColor.G, sky.SunColor.G),
            Channel(sky.AmbientColor.B, sky.MoonColor.B, sky.SunColor.B));
    }

    private static float LitSrgb(float authoredSrgb, float linearLighting)
    {
        if (linearLighting >= 1) return authoredSrgb;
        // The particle vertex shader decodes sRGB. Apply lighting in linear
        // space and re-encode so its decode receives precisely the shaded RGB.
        float linear = authoredSrgb <= 0.04045f ? authoredSrgb / 12.92f
            : MathF.Pow((authoredSrgb + 0.055f) / 1.055f, 2.4f);
        linear *= linearLighting;
        return linear <= 0.0031308f ? linear * 12.92f : 1.055f * MathF.Pow(linear, 1 / 2.4f) - 0.055f;
    }

    private static float SmoothStep(float minimum, float maximum, float value)
    {
        float amount = Math.Clamp((value - minimum) / (maximum - minimum), 0, 1);
        return amount * amount * (3 - 2 * amount);
    }

    private static float Fraction(float value) => value - MathF.Floor(value);
}
