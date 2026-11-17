using System.Numerics;
using Engine.Core.Diagnostics;
using Engine.Core.Runtime;
using Engine.Particles.Assets;
using Engine.Particles.Configuration;
using Engine.Particles.Runtime;
using Engine.Rendering.Assets;
using Engine.UniStorm;
using Engine.UniStorm.Content;
using Shared.Contracts.Content;
using Shared.Contracts.Rendering;
using Shared.Contracts.World;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

/// <summary>Native particle simulation and lightning presentation for the chase world.</summary>
internal sealed class StormChaseEffects
{
    private static readonly AssetHandle Quad = new("mesh", "engine/particle-quad");
    private static readonly AssetHandle BoltCore = new("material", "chase/lightning-core");
    private static readonly AssetHandle BoltGlow = new("material", "chase/lightning-glow");
    private readonly ParticleEngine particles;
    private readonly Func<float, float, float?>? groundHeight;
    private readonly Random lightningRandom = new(0x51CACE);
    private ParticleEffectHandle rain;
    private ParticleEffectHandle hail;
    private float previousSeconds = float.NaN;
    private float strikeSeconds = float.NegativeInfinity;
    private float nextStrikeSeconds = float.NaN;
    private long sourceStrikeSequence = long.MinValue;
    private long strikeSequence;

    public StormChaseEffects(IRuntimeLogger logger, Func<float, float, float?>? groundHeightResolver = null)
    {
        var catalog = new InMemoryParticleEffectCatalog();
        UniStormParticleCatalog.Register(catalog);
        particles = new(catalog, new ParticleOptions
        {
            MaxActiveEffects = 12, MaxActiveParticles = 1600, MaxParticlesPerEffect = 450,
            MaxSimulationStepSeconds = 1f / 60f, MaxSimulationSubsteps = 8,
        }, logger);
        groundHeight = groundHeightResolver;
    }

    public UniStormLightningStrike? LastStrike { get; private set; }
    /// <summary>Storm conditions at the camera for the last frame (mean wind) and its gust multiplier.</summary>
    public StormLocalConditions LocalConditions { get; private set; } = StormLocalConditions.Calm;
    public float Gust { get; private set; } = 1;
    public float LightningFlash { get; private set; }
    public int SimulatedParticleCount => particles.GetSnapshot().EstimatedAliveParticleCount;

    /// <summary>Imports only the real UniStorm effects used here; PMWeather clouds stay untouched.</summary>
    public static void Register(MutableRenderAssetCatalog catalog, string? contentRoot = null)
    {
        var root = UniStormContentLocator.Require(contentRoot);
        foreach (var (handle, relative) in new (AssetHandle, string)[]
        {
            (UniStormAssetIds.RainTexture, "Textures/Weather/Particles/Rain.png"),
            (UniStormAssetIds.HailTexture, "Textures/Weather/Particles/Hail.png"),
            (UniStormAssetIds.LightningBoltTexture, "Textures/Weather/Particles/Lightning Bolt.png"),
            (UniStormAssetIds.LightningTrailTexture, "Textures/Weather/Particles/Lightning Trail.png"),
        })
        {
            using var image = Image.Load<Rgba32>(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (handle == UniStormAssetIds.RainTexture || handle == UniStormAssetIds.LightningTrailTexture)
                image.Mutate(op => op.Rotate(RotateMode.Rotate90));
            var pixels = new byte[checked(image.Width * image.Height * 4)];
            image.CopyPixelDataTo(pixels);
            var mips = TextureMipChainGenerator.Generate((uint)image.Width, (uint)image.Height, 1, pixels,
                TextureMipSemantic.Color, sourceIsSrgb: true, preserveAlphaCoverage: true);
            catalog.UpsertTexture(new RenderTextureData
            {
                Handle = handle, Width = (uint)image.Width, Height = (uint)image.Height,
                MipLevels = mips.MipLevels, PixelDataRgba8 = mips.PixelDataRgba8, ColorSpace = "srgb",
            });
        }
        foreach (var (id, file) in new[]
        {
            (UniStormAssetIds.WeatherParticleShader, "UniStormWeatherParticle.pixel.hlsl"),
            (UniStormAssetIds.LightningShader, "UniStormLightning.pixel.hlsl"),
        })
        {
            using var stream = typeof(UniStormSystem).Assembly.GetManifestResourceStream("Engine.UniStorm.Shaders." + file)
                ?? throw new InvalidOperationException("Missing native UniStorm shader: " + file);
            using var reader = new StreamReader(stream);
            var source = reader.ReadToEnd();
            catalog.UpsertShaderProgram(new()
            {
                Id = id, VertexShaderSource = BuiltInShaderLibrary.ParticleInstanced.VertexShaderSource,
                PixelShaderSource = id == UniStormAssetIds.WeatherParticleShader ? LocalFogShaderLibrary.Include(source) : source,
            });
        }
        catalog.UpsertMaterial(Material(UniStormAssetIds.RainMaterial, UniStormAssetIds.RainTexture,
            UniStormAssetIds.WeatherParticleShader, new(.70f, .82f, .96f, .78f)));
        catalog.UpsertMaterial(Material(UniStormAssetIds.HailMaterial, UniStormAssetIds.HailTexture,
            UniStormAssetIds.WeatherParticleShader, new(.82f, .90f, 1f, .9f)));
        catalog.UpsertMaterial(Material(UniStormAssetIds.LightningMaterial, UniStormAssetIds.LightningBoltTexture,
            UniStormAssetIds.LightningShader, new(.72f, .88f, 1f, 1), 5));
        catalog.UpsertMaterial(Material(BoltCore, UniStormAssetIds.LightningTrailTexture,
            UniStormAssetIds.LightningShader, new(.86f, .94f, 1f, 1), 10));
        catalog.UpsertMaterial(Material(BoltGlow, UniStormAssetIds.LightningTrailTexture,
            UniStormAssetIds.LightningShader, new(.35f, .55f, 1f, .4f), 2));
    }

    public void Reset()
    {
        particles.StopAll(); rain = default; hail = default;
        previousSeconds = float.NaN; nextStrikeSeconds = float.NaN;
        strikeSeconds = float.NegativeInfinity; sourceStrikeSequence = long.MinValue;
        LastStrike = null; LightningFlash = 0;
        // Sequence stays monotonic so the audio worker can distinguish a restarted storm.
    }

    public RenderFrame AddToFrame(RenderFrame frame, float seconds, UniStormSnapshot? snapshot = null, bool precipitationEnabled = true)
    {
        if (!float.IsFinite(seconds)) throw new ArgumentOutOfRangeException(nameof(seconds));
        if (float.IsFinite(previousSeconds) && seconds < previousSeconds) Reset();
        float dt = float.IsFinite(previousSeconds) ? Math.Clamp(seconds - previousSeconds, 0, .1f) : 1f / 60f;
        previousSeconds = seconds;
        frame = ApplyLocalStorm(frame, seconds);
        var weather = frame.Weather;
        var storms = weather?.ActiveStorms ?? [];
        var storm = storms.OrderByDescending(s => s.LightningAmount).FirstOrDefault();
        var localStorm = LocalConditions;
        float rainAlpha = precipitationEnabled ? Math.Clamp(weather?.RainParticleAlpha ?? 0, 0, 1) : 0;
        // Hail falls where the hail shaft is, not wherever the most electrically active storm happens to be.
        float hailAlpha = precipitationEnabled ? Math.Clamp(localStorm.Hail01 * 1.15f, 0, 1) : 0;
        var emitterPosition = frame.Camera.Position + new Vector3f(0, 18, 0);
        UpdateLoop(ref rain, UniStormAssetIds.RainEffect, rainAlpha, emitterPosition);
        UpdateLoop(ref hail, UniStormAssetIds.HailEffect, hailAlpha, emitterPosition);

        if (snapshot?.LightningStrike is { } native && native.Sequence != sourceStrikeSequence && native.Intensity > .01f)
        {
            sourceStrikeSequence = native.Sequence;
            AcceptStrike(native, frame, storm, seconds);
        }
        if (!float.IsFinite(nextStrikeSeconds)) nextStrikeSeconds = seconds + 3.5f;
        if (storm is { LightningAmount: > .08f } && seconds >= nextStrikeSeconds && seconds - strikeSeconds > 2.5f)
        {
            var electricallyActive = storms.Where(s => s.LightningAmount > .08f).OrderBy(s => s.Id, StringComparer.Ordinal).ToArray();
            if (electricallyActive.Length > 1)
                storm = electricallyActive[(int)(strikeSequence % electricallyActive.Length)];
            // A distant storm remains electrically active even when the observer's local
            // UniStorm profile has blended back to ambient outside the canopy.
            AcceptStrike(CreateDistantStrike(storm, frame.Camera.Position), frame, storm, seconds);
        }
        if (seconds >= nextStrikeSeconds)
            nextStrikeSeconds = seconds + 4f + (float)lightningRandom.NextDouble() * 6f;

        particles.TickAsync(new FrameContext(frame.FrameNumber, TimeSpan.FromSeconds(dt), TimeSpan.FromSeconds(seconds), frame.UtcTimestamp),
            CancellationToken.None).GetAwaiter().GetResult(); // ParticleEngine completes synchronously; no IO.
        var simulated = ParticleRenderSceneBuilder.Build(particles.GetSnapshot(), UniStormAssetIds.RainMaterial);
        var batches = new List<RenderParticleInstanceBatch>();
        if (frame.Particles is { } original)
            foreach (var batch in original.Batches.Where(b => b.BatchId != "live-rain"))
            {
                // Debris and dust clouds behind a rain/hail core are hidden by the precipitation in between.
                if (batch.Instances.Count == 0 || storms.Count == 0) { batches.Add(batch); continue; }
                float cx = 0, cz = 0;
                foreach (var instance in batch.Instances) { cx += instance.Position.X; cz += instance.Position.Z; }
                var centroid = new Vector3f(cx / batch.Instances.Count, 0, cz / batch.Instances.Count);
                float hidden = StormChaseStormFields.RainObscuration(frame.Camera.Position, centroid, storms) * .92f;
                batches.Add(hidden <= .02f ? batch : batch with
                    { Instances = batch.Instances.Select(p => p with { Color = p.Color with { A = p.Color.A * (1 - hidden) } }).ToArray() });
            }
        if (simulated is not null)
        {
            // Rain and hail slant with the storm's actual wind at the camera (gusting), falling back to the
            // ambient breeze away from any circulation.
            float localSpeed = localStorm.WindSpeedMetersPerSecond;
            var wind = new Vector3f(localStorm.Wind.X, 0, localStorm.Wind.Z) * Gust
                + new Vector3f(6f, 0, 2.5f) * Math.Clamp(weather?.WindStrength ?? 0, 0, 3) * (1 - Math.Clamp(localSpeed / 8, 0, 1));
            float hailScale = .7f + .45f * localStorm.HailSizeInches;
            foreach (var batch in simulated.Batches)
            {
                float alpha = batch.Material == UniStormAssetIds.RainMaterial ? rainAlpha
                    : batch.Material == UniStormAssetIds.HailMaterial ? hailAlpha : 1f;
                if (alpha <= .001f) continue;
                bool isHail = batch.Material == UniStormAssetIds.HailMaterial;
                var instances = batch.Instances.Select(p => p with
                {
                    Color = p.Color with { A = p.Color.A * alpha },
                    Scale = isHail ? p.Scale * hailScale : p.Scale,
                    Position = p.Position + wind * (p.NormalizedAge * .8f), Velocity = p.Velocity + wind,
                }).Where(p => p.Position.Y > (groundHeight?.Invoke(p.Position.X, p.Position.Z) ?? -1f)).ToArray();
                if (instances.Length > 0) batches.Add(batch with { Instances = instances });
            }
        }

        float age = seconds - strikeSeconds;
        float pulse = age is >= 0 and < .55f
            ? MathF.Exp(-age * 22f) + (age >= .095f ? .62f * MathF.Exp(-(age - .095f) * 28f) : 0)
                + (age >= .22f ? .34f * MathF.Exp(-(age - .22f) * 25f) : 0)
            : 0;
        float distance = LastStrike is { } active ? Vector3.Distance(ToVector(frame.Camera.Position), ToVector(active.End)) : float.PositiveInfinity;
        LightningFlash = Math.Clamp(pulse * (LastStrike?.Intensity ?? 0) / (1 + distance / 3600f), 0, 1);
        if (LastStrike is { } bolt && age is >= 0 and < .46f)
            AddBoltBatches(batches, bolt, Math.Clamp(pulse, .045f, 1f));
        int count = batches.Sum(b => b.Instances.Count);
        var result = frame with
        {
            Particles = count == 0 && frame.Particles?.Billboards.Count is not > 0 ? null
                : new RenderParticleScene((frame.Particles?.ActiveEffectCount ?? 0) + (simulated?.ActiveEffectCount ?? 0),
                    count, frame.Particles?.Billboards ?? [], batches),
        };
        if (LightningFlash > .001f)
        {
            if (result.Weather is { } w) result = result with { Weather = w with
            {
                LightningFlashAmount = Math.Max(w.LightningFlashAmount, LightningFlash),
                LightningHdrIntensity = Math.Max(w.LightningHdrIntensity, LightningFlash * 1.2f),
            }};
            if (result.Sky is { } sky) result = result with { Sky = sky with
            {
                LightningHdrIntensity = Math.Max(sky.LightningHdrIntensity, LightningFlash * 1.2f),
                AmbientColor = sky.AmbientColor with { R = sky.AmbientColor.R + LightningFlash * .12f,
                    G = sky.AmbientColor.G + LightningFlash * .15f, B = sky.AmbientColor.B + LightningFlash * .20f },
            }};
            var lights = (result.Lights ?? []).ToList();
            if (LastStrike is { } lit) lights.Insert(0, new("chase/lightning", RenderLightType.Point,
                lit.End + new Vector3f(0, 75, 0), new(0, -1, 0), new(.64f, .78f, 1f, 1), LightningFlash * 12f, 900));
            result = result with { Lights = lights, PostProcessSettings = (result.PostProcessSettings ?? RenderPostProcessSettings.Default) with
            { BloomIntensity = Math.Max(result.PostProcessSettings?.BloomIntensity ?? 0, .14f + LightningFlash * .32f) }};
        }
        result = ApplyNightAmbientFloor(result);
        return precipitationEnabled ? result : result with { Particles = null };
    }

    /// <summary>
    /// Replaces canopy-wide storm weather with what the storm is doing where the camera actually is:
    /// rain rate and visibility from the radar reflectivity field, hail from the hail shaft, gusting
    /// downdraft/inflow winds, darker light under the core and wind buffeting of the camera.
    /// With no storms the incoming (ambient/regional) weather is left untouched.
    /// </summary>
    private RenderFrame ApplyLocalStorm(RenderFrame frame, float seconds)
    {
        var storms = frame.Weather?.ActiveStorms ?? [];
        if (frame.Weather is not { } w || !storms.Any(s => !s.VisualOnly))
        {
            LocalConditions = StormLocalConditions.Calm; Gust = 1;
            return frame;
        }
        var camera = frame.Camera.Position;
        var local = StormChaseStormFields.Sample(camera, storms);
        float speed = local.WindSpeedMetersPerSecond;
        float gust = StormChaseStormFields.GustFactor(camera, seconds, speed);
        LocalConditions = local; Gust = gust;
        float gustSpeed = speed * gust, murk = 1 - local.Visibility01;
        // Under the anvil but outside the cores only a little stratiform rain falls.
        float rain = Math.Clamp(local.Rain01 + .2f * Math.Clamp(w.RainParticleAlpha, 0, 1) * (1 - local.Rain01), 0, 1);
        var result = frame with { Weather = w with
        {
            RainParticleAlpha = rain, Precipitation = MathF.Max(w.Precipitation * .2f, local.Rain01),
            PrecipitationVeilAmount = MathF.Max(w.PrecipitationVeilAmount * .3f, murk * .95f),
            VisibilityScale = MathF.Min(w.VisibilityScale, MathF.Max(.05f, local.Visibility01)),
            FogBoost = MathF.Max(w.FogBoost, murk),
            WindStrength = MathF.Max(w.WindStrength, gustSpeed / 20),
            WindShakeAmount = MathF.Max(w.WindShakeAmount, Math.Clamp(gustSpeed / 30, 0, 1)),
            WindAudioAmount = MathF.Max(w.WindAudioAmount, Math.Clamp(gustSpeed / 32, 0, 1)),
            SplashAmount = MathF.Max(w.SplashAmount, rain), RippleAmount = MathF.Max(w.RippleAmount, rain * .8f),
            SurfaceWetness = MathF.Max(w.SurfaceWetness, rain),
        }};
        // Rain-wrapping: a tornado seen through the precipitation core is hidden by the rain in between.
        if (result.Weather is { } wrapped && wrapped.ActiveStorms.Any(s => s.Tornadoes.Count > 0))
        {
            var cells = wrapped.ActiveStorms;
            result = result with { Weather = wrapped with { Storms = cells.Select(cell =>
            {
                if (cell.Tornadoes.Count == 0) return cell;
                float wrap = StormChaseStormFields.RainObscuration(camera, cell.Tornadoes[0].Position, cells);
                if (wrap <= .02f) return cell;
                // The volume renderer thickens the rain curtain around the funnel with the storm's occlusion
                // and fades the native funnel itself by this view's obscuration (the funnel is rain-wrapped).
                return cell with
                {
                    Occlusion = MathF.Max(cell.Occlusion, wrap),
                    Tornadoes = cell.Tornadoes.Select(t => t with { ViewObscuration = MathF.Max(t.ViewObscuration, wrap * .9f) }).ToArray(),
                };
            }).ToArray() }};
        }
        if (result.Sky is { } sky && murk > .01f)
        {
            // Koschmieder: extinction ~ 3 / visibility. Heavy cores also block most daylight.
            float density = 3f / local.VisibilityMeters;
            float dim = 1 - .55f * murk - .15f * local.Hail01;
            result = result with { Sky = sky with
            {
                FogDensity = MathF.Max(sky.FogDensity, density * murk),
                FogStartDistance = MathF.Min(sky.FogStartDistance, 8 + (sky.FogStartDistance - 8) * (1 - murk) * (1 - murk)),
                StormFogAmount = MathF.Max(sky.StormFogAmount, murk),
                PrecipitationVeilAmount = MathF.Max(sky.PrecipitationVeilAmount * .3f, murk * .95f),
                SunIntensity = sky.SunIntensity * dim,
                AmbientColor = sky.AmbientColor with { R = sky.AmbientColor.R * dim, G = sky.AmbientColor.G * dim, B = sky.AmbientColor.B * dim },
                FogColor = sky.FogColor with { R = sky.FogColor.R * (1 - .35f * murk), G = sky.FogColor.G * (1 - .3f * murk), B = sky.FogColor.B * (1 - .25f * murk) },
            }};
        }
        // Buffeting: gusts above ~12 m/s and hail rattle the view a little (centimetres, fractions of a degree).
        float shake = Math.Clamp((gustSpeed - 12) / 30, 0, 1) + .35f * local.Hail01;
        if (shake > .001f)
        {
            float t = seconds;
            float jx = MathF.Sin(t * 41.3f) * .6f + MathF.Sin(t * 23.7f + 1.3f) * .4f;
            float jy = MathF.Sin(t * 37.1f + .7f) * .6f + MathF.Sin(t * 19.9f + 2.1f) * .4f;
            float amplitude = .045f * shake, angle = .0045f * shake;
            var c = result.Camera;
            var right = new Vector3(c.Forward.Z, 0, -c.Forward.X);
            if (right.LengthSquared() > 1e-6f) right = Vector3.Normalize(right);
            var forward = ToVector(c.Forward) + right * (jx * angle) + Vector3.UnitY * (jy * angle);
            forward = Vector3.Normalize(forward);
            result = result with { Camera = c with
            {
                Position = c.Position + new Vector3f(right.X * jx * amplitude, jy * amplitude * .7f, right.Z * jx * amplitude),
                Forward = new(forward.X, forward.Y, forward.Z),
            }};
        }
        return result;
    }

    /// <summary>
    /// UniStorm's night ambient falls to near zero, which rendered the whole landscape
    /// pure black. Keep a faint moonlit/starlit blue floor so terrain, trees and roads
    /// still read as silhouettes; daylight and storm ambient are never reduced.
    /// </summary>
    private static RenderFrame ApplyNightAmbientFloor(RenderFrame frame)
    {
        if (frame.Sky is not { } sky) return frame;
        const float r = .040f, g = .050f, b = .080f;
        var ambient = sky.AmbientColor;
        if (ambient.R >= r && ambient.G >= g && ambient.B >= b) return frame;
        return frame with { Sky = sky with { AmbientColor = ambient with
            { R = Math.Max(ambient.R, r), G = Math.Max(ambient.G, g), B = Math.Max(ambient.B, b) } } };
    }

    private void UpdateLoop(ref ParticleEffectHandle handle, string effect, float amount, Vector3f position)
    {
        if (amount > .005f)
        {
            if (!handle.IsValid) handle = particles.Play(ParticleEffectReference.FromAssetId(effect), new()
                { Loop = true, Position = position, Warmup = TimeSpan.FromSeconds(.35) });
            particles.SetTransform(handle, position, Vector3f.Zero);
        }
        else if (handle.IsValid) { particles.Stop(handle); handle = default; }
    }

    private void AcceptStrike(UniStormLightningStrike native, RenderFrame frame, RenderStormCellState? storm, float seconds)
    {
        var end = native.End with { Y = groundHeight?.Invoke(native.End.X, native.End.Z) ?? -1f };
        var start = native.Start with { Y = Math.Max(end.Y + 280, storm?.CloudBaseMeters ?? native.Start.Y) };
        // Rescale the imported native polyline to meet the actual terrain and cloud base.
        float oldHeight = Math.Max(1, native.Start.Y - native.End.Y);
        var points = native.Points.Count >= 2 ? native.Points.Select((p, i) => i == 0 ? start
            : i == native.Points.Count - 1 ? end : p with { Y = end.Y + Math.Clamp((p.Y - native.End.Y) / oldHeight, 0, 1) * (start.Y - end.Y) }).ToArray()
            : new[] { start, end };
        LastStrike = native with { Sequence = ++strikeSequence, Start = start, End = end, Points = points,
            ThunderDelaySeconds = Vector3.Distance(ToVector(end), ToVector(frame.Camera.Position)) / 343f };
        strikeSeconds = seconds;
        nextStrikeSeconds = seconds + 5f + (float)lightningRandom.NextDouble() * 5f;
        particles.Play(ParticleEffectReference.FromAssetId(UniStormAssetIds.LightningImpactEffect), new() { Position = end });
    }

    private UniStormLightningStrike CreateDistantStrike(RenderStormCellState storm, Vector3f observer)
    {
        float angle = (float)lightningRandom.NextDouble() * MathF.Tau;
        float radius = Math.Clamp(storm.CoreRadiusMeters * (.25f + (float)lightningRandom.NextDouble() * .6f), 120, 1800);
        var end = storm.Position + new Vector3f(MathF.Cos(angle) * radius, 0, MathF.Sin(angle) * radius);
        end = end with { Y = groundHeight?.Invoke(end.X, end.Z) ?? -1f };
        var start = end + new Vector3f(90, Math.Max(300, storm.CloudBaseMeters - end.Y), -65);
        var points = new List<Vector3f> { start };
        for (int i = 1; i < 14; i++)
        {
            float t = i / 14f;
            var basePoint = start * (1 - t) + end * t;
            float jitter = 38 * MathF.Sin(t * MathF.PI);
            points.Add(basePoint + new Vector3f(SignedRandom() * jitter, SignedRandom() * 14, SignedRandom() * jitter));
        }
        points.Add(end);
        return new(0, start, end, Math.Clamp(storm.LightningAmount, .4f, 1), points,
            Vector3.Distance(ToVector(observer), ToVector(end)) / 343f,
            UniStormAssetIds.Audio($"Sounds/Thunder/Thunder {lightningRandom.Next(1, 7)}.wav"));
    }

    private static void AddBoltBatches(List<RenderParticleInstanceBatch> batches, UniStormLightningStrike strike, float alpha)
    {
        var core = new List<RenderParticleInstance>();
        var glow = new List<RenderParticleInstance>();
        for (int i = 1; i < strike.Points.Count; i++)
        {
            AddSegment(strike.Points[i - 1], strike.Points[i], 2.6f, alpha, core, glow);
            if (i is 4 or 7 or 10)
            {
                var branchStart = strike.Points[i];
                var branchMid = branchStart + new Vector3f((i % 2 == 0 ? -1 : 1) * 42, -38, 18);
                AddSegment(branchStart, branchMid, 1.3f, alpha * .6f, core, glow);
                AddSegment(branchMid, branchMid + new Vector3f(25, -35, -12), .8f, alpha * .35f, core, glow);
            }
        }
        batches.Add(new("chase/lightning-core", Quad, BoltCore, RenderParticleFacingMode.Velocity,
            RenderParticleBlendMode.Additive, false, 0, core));
        batches.Add(new("chase/lightning-glow", Quad, BoltGlow, RenderParticleFacingMode.Velocity,
            RenderParticleBlendMode.Additive, false, 0, glow));
    }

    private static void AddSegment(Vector3f start, Vector3f end, float width, float alpha,
        List<RenderParticleInstance> core, List<RenderParticleInstance> glow)
    {
        var delta = end - start;
        float length = MathF.Sqrt(delta.X * delta.X + delta.Y * delta.Y + delta.Z * delta.Z);
        if (length < .01f) return;
        var segment = new RenderParticleInstance((start + end) * .5f, new(width, length * 1.08f, 1), Vector3f.Zero,
            new(.8f, .9f, 1f, alpha), delta * (1 / length), 0, 1);
        core.Add(segment);
        glow.Add(segment with { Scale = new(width * 5f, length * 1.12f, 1), Color = new(.4f, .6f, 1f, alpha * .24f) });
    }

    private float SignedRandom() => (float)lightningRandom.NextDouble() * 2 - 1;
    private static Vector3 ToVector(Vector3f v) => new(v.X, v.Y, v.Z);
    private static RenderMaterialDefinition Material(AssetHandle handle, AssetHandle texture, string shader, ColorRgba tint, float emissive = 0) => new()
    {
        Handle = handle, Texture = texture, ShaderProgramId = shader, Tint = tint,
        EmissiveColor = tint with { A = 1 }, EmissiveIntensity = emissive,
        CullMode = RenderCullMode.None, DepthEnabled = true, OpacityMode = RenderOpacityMode.Translucent, AlphaCutoff = .002f,
    };
}
