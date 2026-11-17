using System.Numerics;
using Engine.Rendering.Assets;
using Shared.Contracts.Content;
using Shared.Contracts.Rendering;
using Shared.Contracts.World;

/// <summary>
/// Client-only building level of detail. Every wall, window and trim piece is its own
/// destructible render item so the tornado can release it; a town therefore submitted
/// thousands of items per frame, the renderer's dominant CPU cost. Intact buildings
/// beyond <see cref="BuildingDetailDistanceMeters"/> now draw as one baked mesh per
/// material (instanced across identical models). Nearby or damaged buildings keep
/// their individual parts, so destruction is presented exactly as before.
/// </summary>
internal sealed partial class StormChaseWorld
{
    public const float BuildingDetailDistanceMeters = 300;
    private const float BuildingLodRangeMeters = 4200;
    private readonly Dictionary<string, BuildingLod> buildingLods = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (RenderItem Template, bool Foundation)[]> modelLodMeshes = new(StringComparer.Ordinal);
    private readonly ChaseSpatialGrid<BuildingLod> visibleBuildings = new(512);
    private Dictionary<string, StormChaseBuilding>? buildingsById;

    private sealed class BuildingLod(string id, float x, float z)
    {
        public readonly string Id = id;
        public readonly float X = x, Z = z;
        public readonly List<(RenderItem Item, float Range)> Parts = [];
        public RenderItem[] Merged = [];
    }

    public int MergedBuildingCount => buildingLods.Count;

    partial void RegisterBuildingPart(RenderItem item, float range, ref bool handled)
    {
        if (item.MotionVectorId is not { } partId || !partDamage.TryGetValue(partId, out var damage)) return;
        if (!buildingLods.TryGetValue(damage.BuildingId, out var lod))
        {
            buildingsById ??= buildings.ToDictionary(b => b.Id, StringComparer.Ordinal);
            if (!buildingsById.TryGetValue(damage.BuildingId, out var building) || !Models.Models.Any(m => m.Id == building.Kind)) return;
            lod = new(building.Id, building.Position.X, building.Position.Z) { Merged = CreateMergedBuildingItems(building) };
            buildingLods.Add(building.Id, lod);
            visibleBuildings.Add(lod.X, lod.Z, lod);
        }
        lod.Parts.Add((item, range));
        handled = true;
    }

    partial void AddBuildings(List<RenderItem> items, float x, float z, HashSet<string>? damagedBuildings)
    {
        const float detail = BuildingDetailDistanceMeters * BuildingDetailDistanceMeters;
        foreach (var lod in visibleBuildings.Near(x, z, BuildingLodRangeMeters))
        {
            float d = DistanceSquared(x, z, lod.X, lod.Z);
            if (d > detail && damagedBuildings?.Contains(lod.Id) != true)
            {
                if (d < BuildingLodRangeMeters * BuildingLodRangeMeters) items.AddRange(lod.Merged);
                continue;
            }
            foreach (var (item, range) in lod.Parts)
                if (DistanceSquared(x, z, item.Position.X, item.Position.Z) < range * range) items.Add(item);
        }
    }

    private RenderItem[] CreateMergedBuildingItems(StormChaseBuilding building)
    {
        var model = Models.GetModel(building.Kind);
        if (!modelLodMeshes.TryGetValue(building.Kind, out var templates))
            modelLodMeshes.Add(building.Kind, templates = BakeModelLod(model));
        var levels = building.DestructiblePartIds.Select(LevelOf).Distinct().OrderBy(l => l).ToArray();
        var result = new List<RenderItem>();
        foreach (int level in levels)
            foreach (var (template, foundation) in templates)
            {
                // Ground-floor foundations are static scenery already; upper floors carry their own slab.
                if (foundation && level == 0) continue;
                result.Add(template with
                {
                    Position = new(building.Position.X, building.Position.Y + level * model.FootprintMeters.Y * building.Scale, building.Position.Z),
                    Scale = new(building.Scale, building.Scale, building.Scale), RotationYawDegrees = building.YawDegrees,
                });
            }
        return result.ToArray();
    }

    private static int LevelOf(string partId)
    {
        int start = partId.IndexOf("/level", StringComparison.Ordinal);
        if (start < 0) return 0;
        start += "/level".Length;
        int end = partId.IndexOf('/', start);
        return int.Parse(partId.AsSpan(start, (end < 0 ? partId.Length : end) - start), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Bake a model's rigid parts into model-space meshes, one per material (and a separate foundation set).</summary>
    private (RenderItem, bool)[] BakeModelLod(StormChaseModelDefinition model)
    {
        var groups = new Dictionary<(string Material, bool Foundation), (AssetHandle Material, List<RenderMeshVertex> Vertices, List<uint> Indices)>();
        foreach (var part in model.Parts)
        {
            if (part.Category.Equals("interior", StringComparison.OrdinalIgnoreCase)) continue;
            if (!catalog.TryGetMesh(part.Mesh, out var data) || data is null) continue;
            bool foundation = part.Category.Equals("foundation", StringComparison.OrdinalIgnoreCase);
            var key = (part.Material.Id, foundation);
            if (!groups.TryGetValue(key, out var group)) groups.Add(key, group = (part.Material, [], []));
            // Same composition as StormChaseModelLibrary.CreateItems with zero yaw and unit scale.
            var r = part.LocalRotationRadians;
            var rotation = Matrix4x4.CreateRotationX(r.X) * Matrix4x4.CreateRotationY(r.Y) * Matrix4x4.CreateRotationZ(r.Z);
            var size = new Vector3(part.SizeMeters.X, part.SizeMeters.Y, part.SizeMeters.Z);
            var offset = new Vector3(part.LocalPositionMeters.X, part.LocalPositionMeters.Y, part.LocalPositionMeters.Z);
            uint first = (uint)group.Vertices.Count;
            foreach (var v in data.Vertices)
            {
                var p = Vector3.Transform(new Vector3(v.Position.X, v.Position.Y, v.Position.Z) * size, rotation) + offset;
                // Exported normals are pre-compensated for the part's non-uniform size (see build_models.py).
                var n = Vector3.Normalize(Vector3.TransformNormal(new Vector3(v.Normal.X, v.Normal.Y, v.Normal.Z) / size, rotation));
                var t = Vector3.TransformNormal(new Vector3(v.Tangent.X, v.Tangent.Y, v.Tangent.Z) * size, rotation);
                t = t.LengthSquared() > 1e-10f ? Vector3.Normalize(t) : Vector3.UnitX;
                group.Vertices.Add(v with { Position = new(p.X, p.Y, p.Z), Normal = new(n.X, n.Y, n.Z), Tangent = new(t.X, t.Y, t.Z) });
            }
            IEnumerable<uint> indices = data.WideIndices.Count > 0 ? data.WideIndices : data.Indices.Select(i => (uint)i);
            foreach (uint index in indices) group.Indices.Add(first + index);
        }
        var result = new List<(RenderItem, bool)>();
        foreach (var ((materialId, foundation), group) in groups)
        {
            var handle = new AssetHandle("mesh", $"chase/model-lod/{model.Id}/{materialId}{(foundation ? "/foundation" : "")}");
            catalog.UpsertMesh(new() { Handle = handle, Vertices = group.Vertices.ToArray(), WideIndices = group.Indices.ToArray() });
            result.Add((new RenderItem(materialId, handle, group.Material, default, new(1, 1, 1), 0, true, AllowStaticInstancing: true), foundation));
        }
        return result.ToArray();
    }
}
