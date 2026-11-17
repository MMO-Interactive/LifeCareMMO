using Engine.CrestWater.Content;
using Engine.Rendering.Assets;
using Shared.Contracts.Content;
using Shared.Contracts.Rendering;
using Shared.Contracts.World;

/// <summary>
/// Client-only Gulf surface: finite Crest water patches covering the sea beyond the shoreline in the
/// world's south-east corner (see <see cref="StormChaseCoast"/>). The authoritative server never builds it.
/// Each 2 km patch keeps only cells seaward of the dunes; the terrain beach hides the water edge.
/// </summary>
internal sealed partial class StormChaseWorld
{
    private const float OceanPatchSize = 2048;
    private const int OceanCells = 48;
    private const float OceanVisibleMeters = 9000;
    private readonly List<(Vector3f Center, AssetHandle Mesh)> oceanPatches = [];

    public int OceanPatchCount => oceanPatches.Count;

    public void EnableOcean()
    {
        if (oceanPatches.Count > 0) return;
        float half = PlayableExtentMeters / 2;
        for (float cz = PlayableCenterZ - half + OceanPatchSize / 2; cz < PlayableCenterZ + half; cz += OceanPatchSize)
        for (float cx = -half + OceanPatchSize / 2; cx < half; cx += OceanPatchSize)
        {
            // Skip patches wholly inland of the beach (half-diagonal 1449 m).
            if (StormChaseCoast.SeawardDistance(cx, cz) < -1449 - 700) continue;
            var handle = Mesh($"ocean/{(int)cx}/{(int)cz}");
            var mesh = BuildOceanPatch(handle, cx, cz);
            if (mesh is null) continue;
            catalog.UpsertMesh(mesh);
            oceanPatches.Add((new(cx, StormChaseCoast.SeaLevel, cz), handle));
        }
    }

    public IReadOnlyList<RenderWaterBody> CreateOceanBodies(RenderWeatherState? weather, CameraState camera, float seconds)
    {
        if (oceanPatches.Count == 0) return [];
        float x = camera.Position.X, z = camera.Position.Z;
        // Cheap reject: the whole sea is more than the visibility range away.
        if (StormChaseCoast.SeawardDistance(x, z) < -OceanVisibleMeters - 1449) return [];
        float wind = Math.Clamp(weather?.WindStrength ?? .3f, 0, 2);
        var bodies = new List<RenderWaterBody>();
        foreach (var (center, mesh) in oceanPatches)
        {
            if (DistanceSquared(x, z, center.X, center.Z) > Square(OceanVisibleMeters + 1449)) continue;
            bodies.Add(new("chase-gulf-" + mesh.Id, center, new(OceanPatchSize, 1, OceanPatchSize), new(.6f, 0, .8f),
                Material: CrestWaterAssetIds.InlandMaterial, WaveStrength: Math.Clamp(.55f + wind * .45f, .55f, 1.2f),
                FoamAmount: Math.Clamp(.22f + wind * .2f, .22f, .6f), IsInfinite: false,
                FlowSpeedMetersPerSecond: .45f + wind * .3f, Mesh: mesh));
        }
        return bodies;
    }

    private static RenderMeshData? BuildOceanPatch(AssetHandle handle, float cx, float cz)
    {
        int side = OceanCells + 1;
        float cell = OceanPatchSize / OceanCells;
        var vertices = new RenderMeshVertex[side * side + 2];
        var indices = new List<ushort>(OceanCells * OceanCells * 6);
        var white = new ColorRgba(1, 1, 1, 1);
        var up = new Vector3f(0, 1, 0);
        var tangent = new Vector3f(1, 0, 0);
        for (int z = 0; z <= OceanCells; z++)
        for (int x = 0; x <= OceanCells; x++)
            vertices[z * side + x] = new(new(x / (float)OceanCells - .5f, 0, z / (float)OceanCells - .5f), white,
                x / (float)OceanCells * 16, z / (float)OceanCells * 16, up, tangent);
        for (int z = 0; z < OceanCells; z++)
        for (int x = 0; x < OceanCells; x++)
        {
            float wx = cx - OceanPatchSize / 2 + (x + .5f) * cell, wz = cz - OceanPatchSize / 2 + (z + .5f) * cell;
            // Water runs ~110 m under the beach so its edge is always below the sand, never floating.
            if (StormChaseCoast.SeawardDistance(wx, wz) < -110 - cell) continue;
            ushort first = (ushort)(z * side + x), right = (ushort)(first + 1), north = (ushort)(first + side), northRight = (ushort)(north + 1);
            indices.AddRange([first, north, right, right, north, northRight]);
        }
        if (indices.Count == 0) return null;
        // Unreferenced bounds vertices include wave displacement in the renderer's frustum bounds.
        vertices[^2] = new(new(-.5f, -1.5f, -.5f), white, 0, 0, up, tangent);
        vertices[^1] = new(new(.5f, 1.5f, .5f), white, 1, 1, up, tangent);
        return new() { Handle = handle, ContentVersion = 1, Vertices = vertices, Indices = indices };
    }
}
