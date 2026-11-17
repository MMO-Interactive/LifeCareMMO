using Engine.CrestWater;
using Engine.CrestWater.Content;
using Engine.Rendering.Assets;
using Engine.Rendering.Scene;
using Shared.Contracts.Content;
using Shared.Contracts.Rendering;
using Shared.Contracts.World;

/// <summary>
/// The storm-chasing lake uses Engine.CrestWater's native shaders, supplied
/// textures, GPU Gerstner waves, surface queries and underwater treatment.
/// Its finite shoreline matches the StormChaseWorld terrain basin.
/// </summary>
internal sealed class StormChaseWater : IDisposable
{
    public static readonly Vector3f LakeCenter = new(1100f, 3f, 400f);
    public const float LakeRadiusX = 600f;
    public const float LakeRadiusZ = 340f;
    public const float WaterLevel = 3f;
    private const int PatchesX = 5;
    private const int PatchesZ = 3;
    private const int CellsPerPatch = 64;
    private const string BodyPrefix = "storm-chase-crest-lake-";
    private static readonly Vector3f FlowDirection = new(0.86f, 0f, 0.51f);
    private readonly MutableRenderAssetCatalog catalog;
    private readonly LakePatch[] patches;
    private readonly CrestWaterUnderwaterEffect underwater = new();
    private float waveStrength = 0.32f;
    private float flowSpeed = 0.30f;
    private bool disposed;

    public StormChaseWater(MutableRenderAssetCatalog catalog, string? contentRoot = null)
    {
        this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        Registration = CrestWaterRenderAssetRegistrar.Register(catalog, contentRoot);
        if (Registration.MissingFiles.Count != 0)
            throw new FileNotFoundException("Crest lake textures are missing: " + string.Join(", ", Registration.MissingFiles));
        var generated = new List<LakePatch>(PatchesX * PatchesZ);
        float width = LakeRadiusX * 2f / PatchesX;
        float length = LakeRadiusZ * 2f / PatchesZ;
        for (int z = 0; z < PatchesZ; z++)
        for (int x = 0; x < PatchesX; x++)
        {
            var center = new Vector3f(LakeCenter.X - LakeRadiusX + (x + 0.5f) * width,
                WaterLevel, LakeCenter.Z - LakeRadiusZ + (z + 0.5f) * length);
            var handle = new AssetHandle("mesh", BodyPrefix + x + "-" + z);
            var mesh = BuildPatch(handle, center, width, length);
            if (mesh.Indices.Count == 0) continue;
            catalog.UpsertMesh(mesh);
            generated.Add(new(handle, center, new(width, 1f, length)));
        }
        patches = generated.ToArray();
    }

    public CrestWaterRenderRegistrationResult Registration { get; }
    public CrestWaterUnderwaterEffect Underwater => underwater;
    public int PatchCount => patches.Length;
    public float WaveStrength => waveStrength;
    public float FlowSpeedMetersPerSecond => flowSpeed;

    // These describe the features actually exercised, not unavailable FFT or
    // full volumetric-cloud reflection features of the original Unity package.
    public static IReadOnlyList<string> Capabilities { get; } =
    [
        "Engine.CrestWater inland material and four imported Crest textures",
        "Tessellated GPU Gerstner displacement with matching CPU surface queries",
        "Depth-based shoreline foam, refraction, absorption and shallow caustics",
        "Native sky, opaque-scene and local-light reflections",
        "Camera entry/exit underwater fog and post-processing",
    ];

    public IReadOnlyList<RenderWaterBody> CreateBodies(RenderWeatherState? weather, CameraState camera, float time)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(camera);
        ValidateTime(time);
        float wind = Math.Clamp(weather?.WindStrength ?? 0.25f, 0f, 2f);
        // UniStorm's continuous local wind affects wave/foam strength without
        // changing the shared world-space clock or the water's rest level.
        waveStrength = Math.Clamp(0.18f + wind * 0.50f, 0.18f, 0.95f);
        flowSpeed = Math.Clamp(0.25f + wind * 0.28f, 0.25f, 0.80f);
        float foam = Math.Clamp(0.09f + wind * 0.22f, 0.09f, 0.42f);
        var bodies = new RenderWaterBody[patches.Length];
        for (int index = 0; index < patches.Length; index++)
        {
            var patch = patches[index];
            bodies[index] = new(BodyPrefix + index, patch.Center, patch.Size, FlowDirection,
                Material: CrestWaterAssetIds.InlandMaterial, WaveStrength: waveStrength,
                FoamAmount: foam, IsInfinite: false, FlowSpeedMetersPerSecond: flowSpeed, Mesh: patch.Mesh);
        }
        return bodies;
    }

    public static bool ContainsWater(float x, float z)
    {
        if (!float.IsFinite(x) || !float.IsFinite(z)) return false;
        float dx = (x - LakeCenter.X) / LakeRadiusX;
        float dz = (z - LakeCenter.Z) / LakeRadiusZ;
        return dx * dx + dz * dz <= 1f;
    }

    /// <summary>Returns null on dry land; the wave clock and parameters match the rendered bodies.</summary>
    public CrestWaterSample? SampleSurface(float x, float z, float time)
    {
        ThrowIfDisposed();
        ValidateTime(time);
        if (!ContainsWater(x, z)) return null;
        return CrestWaveSampler.SampleAtWorldPosition(new(x, WaterLevel, z), time,
            CrestWaveProfile.Inland, waveStrength, FlowDirection, flowSpeed);
    }

    /// <summary>Call after local weather is assembled and before building the render frame.</summary>
    public RenderScene ApplyUnderwater(RenderScene scene, float elapsedSeconds, float time)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(scene);
        ValidateTime(time);
        if (ContainsWater(scene.Camera.Position.X, scene.Camera.Position.Z))
            return underwater.Apply(scene, elapsedSeconds, time);
        // Crest's generic finite-body test uses a rectangle. An ellipse's dry
        // corners must not trigger underwater fog when the player flies nearby.
        // Still advance the real exit transition and retain every visible body.
        var otherWater = scene.WaterBodies.Where(body => !body.Id.StartsWith(BodyPrefix, StringComparison.Ordinal)).ToArray();
        var waterless = CopyScene(scene, otherWater);
        var treated = underwater.Apply(waterless, elapsedSeconds, time);
        return ReferenceEquals(waterless, treated) ? scene : CopyScene(treated, scene.WaterBodies);
    }

    public void ResetUnderwater() => underwater.Reset();

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        foreach (var patch in patches) catalog.RemoveMesh(patch.Mesh);
        underwater.Reset();
    }

    private static RenderMeshData BuildPatch(AssetHandle handle, Vector3f center, float width, float length)
    {
        int side = CellsPerPatch + 1;
        var vertices = new RenderMeshVertex[side * side + 2];
        var indices = new List<ushort>(CellsPerPatch * CellsPerPatch * 6);
        var white = new ColorRgba(1f, 1f, 1f, 1f);
        var up = new Vector3f(0f, 1f, 0f);
        var tangent = new Vector3f(1f, 0f, 0f);
        for (int z = 0; z <= CellsPerPatch; z++)
        for (int x = 0; x <= CellsPerPatch; x++)
        {
            float u = x / (float)CellsPerPatch, v = z / (float)CellsPerPatch;
            float worldX = center.X + (u - 0.5f) * width;
            float worldZ = center.Z + (v - 0.5f) * length;
            float dx = (worldX - LakeCenter.X) / LakeRadiusX;
            float dz = (worldZ - LakeCenter.Z) / LakeRadiusZ;
            float radius = MathF.Sqrt(dx * dx + dz * dz);
            if (radius > 1f)
            {
                worldX = LakeCenter.X + dx / radius * LakeRadiusX;
                worldZ = LakeCenter.Z + dz / radius * LakeRadiusZ;
            }
            vertices[z * side + x] = new(new((worldX - center.X) / width, 0f, (worldZ - center.Z) / length),
                white, u, v, up, tangent);
        }
        for (int z = 0; z < CellsPerPatch; z++)
        for (int x = 0; x < CellsPerPatch; x++)
        {
            float worldX = center.X + ((x + 0.5f) / CellsPerPatch - 0.5f) * width;
            float worldZ = center.Z + ((z + 0.5f) / CellsPerPatch - 0.5f) * length;
            if (!ContainsWater(worldX, worldZ)) continue;
            ushort first = checked((ushort)(z * side + x));
            ushort right = (ushort)(first + 1), north = (ushort)(first + side), northRight = (ushort)(north + 1);
            indices.Add(first); indices.Add(north); indices.Add(right);
            indices.Add(right); indices.Add(north); indices.Add(northRight);
        }
        // Unreferenced vertices conservatively include native wave displacement
        // in renderer frustum bounds without modifying the surface geometry.
        vertices[^2] = new(new(-0.5f - 1.2f / width, -1.2f, -0.5f - 1.2f / length), white, 0f, 0f, up, tangent);
        vertices[^1] = new(new(0.5f + 1.2f / width, 1.2f, 0.5f + 1.2f / length), white, 1f, 1f, up, tangent);
        return new() { Handle = handle, ContentVersion = 1, Vertices = vertices, Indices = indices };
    }

    private static RenderScene CopyScene(RenderScene scene, IReadOnlyList<RenderWaterBody> water) => new()
    {
        Camera = scene.Camera, ClearColor = scene.ClearColor, Environment = scene.Environment,
        Items = scene.Items, SkinPoses = scene.SkinPoses, Lights = scene.Lights, FogVolumes = scene.FogVolumes,
        WaterBodies = water, ReflectionProbes = scene.ReflectionProbes, Particles = scene.Particles,
        PostProcessSettings = scene.PostProcessSettings, QualityHints = scene.QualityHints,
        LookProfileId = scene.LookProfileId, LookProfile = scene.LookProfile,
        TemporalHistoryVersion = scene.TemporalHistoryVersion, AnimationTimeSeconds = scene.AnimationTimeSeconds,
        DebugOverlays = scene.DebugOverlays,
    };

    private static void ValidateTime(float time)
    {
        if (!float.IsFinite(time) || time < 0f) throw new ArgumentOutOfRangeException(nameof(time));
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(disposed, this);
    private readonly record struct LakePatch(AssetHandle Mesh, Vector3f Center, Vector3f Size);
}
