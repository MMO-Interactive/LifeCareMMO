using Shared.Contracts.Rendering;
using Shared.Contracts.World;

/// <summary>
/// Client-only vegetation: Blender trees, shrubs and boulders placed ecologically
/// (river corridors, shelterbelts, farmsteads, town canopies, savanna clumps) and
/// ground cover streamed in 48 m chunks around the viewer. Nothing here affects
/// gameplay, collision or the world fingerprint, so the server never builds it.
/// </summary>
internal sealed partial class StormChaseWorld
{
    private const float PlantCellSize = 128, GroundCellSize = 6, GroundChunkSize = 48;
    private const int GroundCellsPerChunk = 8, GroundChunksBuiltPerFrame = 10, GroundChunkCacheLimit = 1400;
    private StormChaseVegetationLibrary? vegetation;
    private readonly Dictionary<(int X, int Z), PlantCell> plantCells = [];
    private readonly Dictionary<(int X, int Z), GroundChunk> groundChunks = [];
    private HashSet<(int X, int Z)> blockedGround = [];
    private float plantReach, groundReach;
    private long vegetationFrame;

    private readonly record struct Plant(float X, float Y, float Z, float Yaw, float Scale, int Asset);

    private sealed class PlantCell(float centerX, float centerZ)
    {
        public readonly float CenterX = centerX, CenterZ = centerZ;
        public readonly List<Plant> Plants = [];
        public float Reach;
        public readonly RenderItem[]?[] Items = new RenderItem[]?[3];
        public readonly int[]?[] Starts = new int[]?[3];
    }

    private sealed class GroundChunk(float centerX, float centerZ, RenderItem[] near, RenderItem[] far, float[] xs, float[] zs)
    {
        public readonly float CenterX = centerX, CenterZ = centerZ;
        public readonly RenderItem[] Near = near, Far = far;
        public readonly float[] Xs = xs, Zs = zs;
        public long LastUsed;
    }

    public StormChaseVegetationLibrary? Vegetation => vegetation;

    /// <summary>Attach Blender vegetation to this client's world. Call once, before rendering starts.</summary>
    public void EnableVegetation(StormChaseVegetationLibrary library)
    {
        ArgumentNullException.ThrowIfNull(library);
        if (vegetation is not null) throw new InvalidOperationException("Vegetation is already attached to this world.");
        vegetation = library;
        BuildBlockedGround();
        var plants = new List<Plant>(150_000);
        PlaceVegetation(plants, new Random(41731));
        foreach (var plant in plants)
        {
            var key = (PlantCellIndex(plant.X), PlantCellIndex(plant.Z));
            if (!plantCells.TryGetValue(key, out var cell))
                plantCells.Add(key, cell = new((key.Item1 + .5f) * PlantCellSize, (key.Item2 + .5f) * PlantCellSize));
            cell.Plants.Add(plant);
            cell.Reach = MathF.Max(cell.Reach, library.Assets[plant.Asset].MaxDistance);
        }
        plantReach = plantCells.Values.Select(c => c.Reach).DefaultIfEmpty(0).Max();
        groundReach = library.Species("prairie").Max(a => a.MaxDistance);
        vegetationTreeCount = plants.Count;
    }

    // ---------------------------------------------------------------- per frame
    partial void AddVegetation(List<RenderItem> items, CameraState camera)
    {
        if (vegetation is null) return;
        vegetationFrame++;
        float cx = camera.Position.X, cz = camera.Position.Z;
        float flat = MathF.Sqrt(camera.Forward.X * camera.Forward.X + camera.Forward.Z * camera.Forward.Z);
        float fx = flat > 1e-3f ? camera.Forward.X / flat : 0, fz = flat > 1e-3f ? camera.Forward.Z / flat : 0;
        bool cull = flat > .25f;   // looking almost straight down: keep everything around the viewer
        // Shadow casters just behind the viewer still land in view; keep a near ring.
        const float plantHalf = PlantCellSize * .7072f, chunkHalf = GroundChunkSize * .7072f;

        int span = (int)MathF.Ceiling(plantReach / PlantCellSize);
        int ox = PlantCellIndex(cx), oz = PlantCellIndex(cz);
        for (int iz = oz - span; iz <= oz + span; iz++)
            for (int ix = ox - span; ix <= ox + span; ix++)
            {
                if (!plantCells.TryGetValue((ix, iz), out var cell)) continue;
                float dx = cell.CenterX - cx, dz = cell.CenterZ - cz, d = MathF.Sqrt(dx * dx + dz * dz);
                if (d - plantHalf > cell.Reach) continue;
                if (cull && d > plantHalf + 60 && dx * fx + dz * fz < d * .26f - plantHalf) continue;
                var plants = cell.Plants;
                for (int i = 0; i < plants.Count; i++)
                {
                    var plant = plants[i];
                    float px = plant.X - cx, pz = plant.Z - cz;
                    int lod = vegetation.Assets[plant.Asset].LodAt(MathF.Sqrt(px * px + pz * pz) / plant.Scale);
                    if (lod < 0) continue;
                    var (cellItems, starts) = CellLod(cell, lod);
                    for (int k = starts[i]; k < starts[i + 1]; k++) items.Add(cellItems[k]);
                }
            }

        int built = 0;
        span = (int)MathF.Ceiling(groundReach / GroundChunkSize);
        ox = GroundChunkIndex(cx); oz = GroundChunkIndex(cz);
        float nearDistance = vegetation.Species("prairie")[0].Lods[0].MaxDistance;
        for (int iz = oz - span; iz <= oz + span; iz++)
            for (int ix = ox - span; ix <= ox + span; ix++)
            {
                float ccx = (ix + .5f) * GroundChunkSize, ccz = (iz + .5f) * GroundChunkSize;
                float dx = ccx - cx, dz = ccz - cz, d = MathF.Sqrt(dx * dx + dz * dz);
                if (d - chunkHalf > groundReach) continue;
                if (cull && d > chunkHalf + 12 && dx * fx + dz * fz < d * .26f - chunkHalf) continue;
                if (!groundChunks.TryGetValue((ix, iz), out var chunk))
                {
                    // Spread generation across frames; nearer chunks win because
                    // the caller's view keeps them in range on the next frame.
                    if (built >= GroundChunksBuiltPerFrame && d > chunkHalf * 2) continue;
                    groundChunks.Add((ix, iz), chunk = BuildGroundChunk(ix, iz)); built++;
                }
                chunk.LastUsed = vegetationFrame;
                if (d + chunkHalf < nearDistance) { items.AddRange(chunk.Near); continue; }
                for (int i = 0; i < chunk.Xs.Length; i++)
                {
                    float px = chunk.Xs[i] - cx, pz = chunk.Zs[i] - cz, pd = px * px + pz * pz;
                    if (pd <= nearDistance * nearDistance) items.Add(chunk.Near[i]);
                    else if (pd <= groundReach * groundReach) items.Add(chunk.Far[i]);
                }
            }
        if (groundChunks.Count > GroundChunkCacheLimit)
            foreach (var key in groundChunks.Where(p => p.Value.LastUsed < vegetationFrame - 240).Select(p => p.Key).ToArray())
                groundChunks.Remove(key);
    }

    private (RenderItem[] Items, int[] Starts) CellLod(PlantCell cell, int lod)
    {
        if (cell.Items[lod] is { } cached) return (cached, cell.Starts[lod]!);
        var items = new List<RenderItem>(cell.Plants.Count * 2);
        var starts = new int[cell.Plants.Count + 1];
        for (int i = 0; i < cell.Plants.Count; i++)
        {
            starts[i] = items.Count;
            var plant = cell.Plants[i];
            var asset = vegetation!.Assets[plant.Asset];
            if (lod >= asset.Lods.Length) continue;
            // The farthest tree level skips shadow casting; shrubs/rocks keep it only up close.
            bool shadow = asset.Kind switch { "tree" => lod < 2, "rock" => true, _ => lod == 0 };
            foreach (var part in asset.Lods[lod].Parts)
                items.Add(new(part.MaterialId, part.Mesh, null, new(plant.X, plant.Y, plant.Z),
                    new(plant.Scale, plant.Scale, plant.Scale), plant.Yaw, shadow, AllowStaticInstancing: true));
        }
        starts[^1] = items.Count;
        cell.Starts[lod] = starts;
        return (cell.Items[lod] = items.ToArray(), starts);
    }

    // ---------------------------------------------------------------- ground cover
    private GroundChunk BuildGroundChunk(int chunkX, int chunkZ)
    {
        var near = new List<RenderItem>(32); var far = new List<RenderItem>(32);
        var xs = new List<float>(32); var zs = new List<float>(32);
        // Open ground takes one 12 m patch; edges against roads, lots and water fall back
        // to 6 m patches so cover still reaches the shoulder without crossing it.
        for (int wz = 0; wz < GroundCellsPerChunk / 2; wz++)
            for (int wx = 0; wx < GroundCellsPerChunk / 2; wx++)
            {
                int baseX = chunkX * GroundCellsPerChunk + wx * 2, baseZ = chunkZ * GroundCellsPerChunk + wz * 2;
                var sub = new string[4];
                bool uniform = true;
                for (int k = 0; k < 4; k++)
                {
                    sub[k] = CoverAt(baseX + (k & 1), baseZ + (k >> 1));
                    uniform &= sub[k].Length > 0 && sub[k] == sub[0];
                }
                if (uniform)
                {
                    uint hash = Hash(baseX * 7 + 3, baseZ * 7 + 5);
                    if (hash % 100 < 5 && sub[0] != "corn") continue;   // occasional bare patch breaks up repetition
                    Place(sub[0] + "-wide", (baseX + 1) * GroundCellSize, (baseZ + 1) * GroundCellSize, hash, 1.8f);
                    continue;
                }
                for (int k = 0; k < 4; k++)
                {
                    if (sub[k].Length == 0) continue;
                    int cellX = baseX + (k & 1), cellZ = baseZ + (k >> 1);
                    Place(sub[k], (cellX + .5f) * GroundCellSize, (cellZ + .5f) * GroundCellSize, Hash(cellX, cellZ), 1.2f);
                }
            }
        void Place(string species, float x, float z, uint hash, float jitter)
        {
            bool corn = species.StartsWith("corn", StringComparison.Ordinal);
            float yaw = corn ? 0 : (hash & 0xFFFF) / 65535f * 360;
            if (!corn) { x += ((hash >> 16 & 0xFF) / 255f - .5f) * 2 * jitter; z += ((hash >> 24) / 255f - .5f) * 2 * jitter; }
            var variants = vegetation!.Species(species);
            var asset = variants[(int)(hash >> 7) % variants.Length];
            float scale = corn ? 1 : .9f + (hash >> 11 & 0xFF) / 255f * .24f;
            var position = new Vector3f(x, HeightAt(x, z) - .02f, z);
            var size = new Vector3f(scale, scale, scale);
            RenderItem Item(int lod) { var part = asset.Lods[lod].Parts[0]; return new(part.MaterialId, part.Mesh, null, position, size, yaw, false, AllowStaticInstancing: true); }
            near.Add(Item(0)); far.Add(Item(Math.Min(1, asset.Lods.Length - 1)));
            xs.Add(x); zs.Add(z);
        }
        vegetationPatchCount += near.Count;
        return new((chunkX + .5f) * GroundChunkSize, (chunkZ + .5f) * GroundChunkSize, near.ToArray(), far.ToArray(), xs.ToArray(), zs.ToArray());
    }

    /// <summary>The ground-cover species for one 6 m cell, or "" for bare ground.</summary>
    private string CoverAt(int cellX, int cellZ)
    {
        float x = (cellX + .5f) * GroundCellSize, z = (cellZ + .5f) * GroundCellSize;
        if (MathF.Abs(x) > PlayableExtentMeters / 2 || MathF.Abs(z - PlayableCenterZ) > PlayableExtentMeters / 2) return "";
        if (blockedGround.Contains((cellX, cellZ))) return "";
        if (LakeRadius(x, z) < 1.03f || x > 1650 && x < 6400 && MathF.Abs(z - RiverZ(x)) < 58) return "";
        uint roll = Hash(cellX, cellZ) % 100;
        return SurfaceAt(x, z) switch
        {
            "terrain/farmland/tilled" => "corn",
            // Flowers come in drifts (coarse hash) rather than salt-and-pepper cells.
            "terrain/grass" => InSettlement(x, z) ? (roll < 88 ? "lawn" : "prairie")
                : Hash(cellX >> 3, cellZ >> 3) % 100 < 14 && roll < 70 ? "flowers" : "prairie",
            "terrain/rock" => roll < 35 ? "prairie" : "",
            _ => "",
        };
    }

    private bool InSettlement(float x, float z)
    {
        foreach (var pad in SettlementPads) if (DistanceSquared(x, z, pad.X, pad.Z) < Square(pad.Radius * .85f)) return true;
        return false;
    }

    /// <summary>Roads (with shoulders) and building lots, rasterised to the 6 m ground-cover grid.</summary>
    private void BuildBlockedGround()
    {
        var blocked = new HashSet<(int X, int Z)>();
        void Mark(float minX, float minZ, float maxX, float maxZ)
        {
            for (int z = GroundCell(minZ); z <= GroundCell(maxZ); z++)
                for (int x = GroundCell(minX); x <= GroundCell(maxX); x++) blocked.Add((x, z));
        }
        foreach (var triangle in StormChaseLotGeometry.RoadTriangles(roads, catalog))
            Mark(triangle.Minimum.X - 2.5f, triangle.Minimum.Y - 2.5f, triangle.Maximum.X + 2.5f, triangle.Maximum.Y + 2.5f);
        foreach (var lot in buildingLots.Values)
            Mark(lot.Minimum.X - 2, lot.Minimum.Z - 2, lot.Maximum.X + 2, lot.Maximum.Z + 2);
        blockedGround = blocked;
    }

    private bool TreeSiteBlocked(float x, float z, int clearanceCells)
    {
        int cx = GroundCell(x), cz = GroundCell(z);
        for (int dz = -clearanceCells; dz <= clearanceCells; dz++)
            for (int dx = -clearanceCells; dx <= clearanceCells; dx++)
                if (blockedGround.Contains((cx + dx, cz + dz))) return true;
        return false;
    }

    // ---------------------------------------------------------------- placement
    private void PlaceVegetation(List<Plant> plants, Random rng)
    {
        var library = vegetation!;
        float Next() => (float)rng.NextDouble();
        string Pick(params (string Species, float Weight)[] weights)
        {
            float roll = Next() * weights.Sum(w => w.Weight);
            foreach (var (species, weight) in weights) { if ((roll -= weight) <= 0) return species; }
            return weights[^1].Species;
        }
        void Add(float x, float z, string species, float minScale = .85f, float maxScale = 1.15f, int clearance = 1)
        {
            if (MathF.Abs(x) > PlayableExtentMeters / 2 - 150 || MathF.Abs(z - PlayableCenterZ) > PlayableExtentMeters / 2 - 150) return;
            if (TreeSiteBlocked(x, z, clearance) || LakeRadius(x, z) < 1.1f) return;
            if (x > 1650 && x < 6400 && MathF.Abs(z - RiverZ(x)) < 52) return;
            // Keep the open field where the chase starts so the first funnel stays visible.
            if (DistanceSquared(x, z, 0, 1000) < 300 * 300) return;
            float y = HeightAt(x, z);
            if (y < LakeWaterLevel + .6f) return;
            var variants = library.Species(species);
            var asset = variants[rng.Next(variants.Length)];
            float scale = minScale + Next() * (maxScale - minScale);
            float sink = asset.Kind == "rock" ? .3f * scale : .05f;
            plants.Add(new(x, y - sink, z, Next() * 360, scale, asset.Index));
        }
        (string, float)[] riparian = [("cottonwood", 55), ("silver-maple", 20), ("bur-oak", 8), ("plum-shrub", 17)];

        // River corridor: cottonwood galleries thinning away from the banks.
        for (float x = 1660; x < 6400; x += 7)
            foreach (float side in new[] { -1f, 1f })
                if (Next() < .8f) Add(x + (Next() - .5f) * 6, RiverZ(x) + side * (56 + MathF.Pow(Next(), 1.7f) * 150), Pick(riparian));
        // Dry creek toward the lake.
        for (float x = 1680; x < 3400; x += 9)
        {
            float creek = 460 + 65 * MathF.Sin((x - 1650) * .003f);
            foreach (float side in new[] { -1f, 1f })
                if (Next() < .6f) Add(x + (Next() - .5f) * 6, creek + side * (14 + Next() * 45), Pick(riparian), clearance: 0);
            if (Next() < .35f) Add(x, creek + (Next() - .5f) * 50, "boulder", .5f, 1.6f, 0);
        }
        // Lakeshore groves.
        for (int i = 0; i < 520; i++)
        {
            float a = Next() * MathF.Tau, r = 1.13f + MathF.Pow(Next(), 1.5f) * .45f;
            Add(LakeCenterX + MathF.Cos(a) * LakeRadiusX * r, LakeCenterZ + MathF.Sin(a) * LakeRadiusZ * r, Pick(riparian));
        }

        // Shelterbelts on the field grid used by the tilled-farmland surface.
        float half = PlayableExtentMeters / 2;
        for (int iz = (int)MathF.Floor((PlayableCenterZ - half) / 650); iz <= (int)MathF.Floor((PlayableCenterZ + half) / 650); iz++)
            for (int ix = (int)MathF.Floor(-half / 700); ix <= (int)MathF.Floor(half / 700); ix++)
            {
                float x0 = ix * 700, z0 = iz * 650;
                float mx = x0 + 350, mz = z0 + 325;
                if (SurfaceAt(mx, mz) != "terrain/farmland/tilled") continue;
                uint hash = Hash(ix, iz);
                if (hash % 100 < 48) Belt(x0 + 12, z0 + 650 - 8, x0 + 688, z0 + 650 - 8, hash);
                if (hash / 100 % 100 < 26) Belt(x0 + 8, z0 + 12, x0 + 8, z0 + 638, hash / 7);
            }
        void Belt(float ax, float az, float bx, float bz, uint hash)
        {
            float length = Distance(ax, az, bx, bz); float ux = (bx - ax) / length, uz = (bz - az) / length;
            bool cedarRow = hash % 3 != 1, broadRow = hash % 3 != 0;
            if (cedarRow) for (float s = 0; s < length; s += 5.5f) if (Next() < .9f) Add(ax + ux * s, az + uz * s, Next() < .9f ? "red-cedar" : "plum-shrub", clearance: 0);
            if (broadRow) for (float s = 4; s < length; s += 9.5f)
                    if (Next() < .85f) Add(ax + ux * s - uz * (cedarRow ? 8 : 0), az + uz * s + ux * (cedarRow ? 8 : 0), Pick(("cottonwood", 50), ("silver-maple", 30), ("bur-oak", 20)), clearance: 0);
        }
        // The original chase farmland keeps its western windbreak.
        for (float z = 500; z < 3000; z += 6) { Add(-1255 + Next() * 3, z, "red-cedar", clearance: 0); if (Next() < .6f) Add(-1265, z + 3, "cottonwood", clearance: 0); }

        // Farmsteads: an L-shaped windbreak on the north and west, with yard trees.
        var farmsteads = new List<(float X, float Z)>();
        foreach (var building in buildings.Where(b => b.SettlementId == "rural" && b.Kind is "house" or "barn"))
        {
            float bx = building.Position.X, bz = building.Position.Z;
            if (farmsteads.Any(f => DistanceSquared(f.X, f.Z, bx, bz) < 140 * 140)) continue;
            farmsteads.Add((bx, bz));
            for (float s = -70; s <= 50; s += 6) { Add(bx + s, bz + 55 + Next() * 2, "red-cedar"); if (Next() < .7f) Add(bx + s + 3, bz + 64, "cottonwood"); }
            for (float s = -60; s <= 50; s += 6) { Add(bx - 70 - Next() * 2, bz + s, "red-cedar"); if (Next() < .6f) Add(bx - 79, bz + s + 3, "cottonwood"); }
            for (int i = 0; i < 7; i++) { float a = Next() * MathF.Tau, r = 18 + Next() * 22; Add(bx + MathF.Cos(a) * r, bz + MathF.Sin(a) * r, Pick(("silver-maple", 50), ("bur-oak", 35), ("plum-shrub", 15))); }
        }

        // Town canopies: street and yard trees anywhere a lot and road leave room.
        foreach (var pad in SettlementPads)
        {
            int attempts = (int)(MathF.PI * pad.Radius * pad.Radius / 900);
            for (int i = 0; i < attempts; i++)
            {
                float a = Next() * MathF.Tau, r = MathF.Sqrt(Next()) * pad.Radius * 1.05f;
                Add(pad.X + MathF.Cos(a) * r, pad.Z + MathF.Sin(a) * r,
                    Pick(("silver-maple", 52), ("bur-oak", 18), ("cottonwood", 10), ("red-cedar", 7), ("plum-shrub", 13)));
            }
        }

        // Open prairie: oak savanna, invading cedars, plum thickets and lone cottonwoods.
        for (int i = 0; i < 4200; i++)
        {
            float x = (Next() - .5f) * (PlayableExtentMeters - 400), z = PlayableCenterZ + (Next() - .5f) * (PlayableExtentMeters - 400);
            if (InSettlement(x, z) || SurfaceAt(x, z) == "terrain/farmland/tilled") continue;
            float roll = Next();
            if (roll < .35f) { int n = 3 + rng.Next(7); for (int k = 0; k < n; k++) Add(x + (Next() - .5f) * 90, z + (Next() - .5f) * 90, Next() < .85f ? "bur-oak" : "plum-shrub"); }
            else if (roll < .65f) { int n = 4 + rng.Next(10); for (int k = 0; k < n; k++) Add(x + (Next() - .5f) * 120, z + (Next() - .5f) * 120, "red-cedar", .6f, 1.1f); }
            else if (roll < .85f) { int n = 4 + rng.Next(6); for (int k = 0; k < n; k++) Add(x + (Next() - .5f) * 26, z + (Next() - .5f) * 26, "plum-shrub", .8f, 1.3f, 0); }
            else { Add(x, z, "cottonwood", 1.0f, 1.25f); if (Next() < .4f) Add(x + 14, z + 9, "cottonwood"); }
        }
        // Glacial erratics and field-edge rock piles.
        for (int i = 0; i < 2600; i++)
        {
            float x = (Next() - .5f) * (PlayableExtentMeters - 400), z = PlayableCenterZ + (Next() - .5f) * (PlayableExtentMeters - 400);
            if (InSettlement(x, z)) continue;
            int n = 1 + rng.Next(3);
            for (int k = 0; k < n; k++) Add(x + (Next() - .5f) * 8, z + (Next() - .5f) * 8, "boulder", .45f, 1.7f, 0);
        }
    }

    private static int PlantCellIndex(float coordinate) => (int)MathF.Floor(coordinate / PlantCellSize);
    private static int GroundChunkIndex(float coordinate) => (int)MathF.Floor(coordinate / GroundChunkSize);
    private static int GroundCell(float coordinate) => (int)MathF.Floor(coordinate / GroundCellSize);
    private static uint Hash(int x, int z)
    {
        unchecked
        {
            uint h = (uint)x * 0x8DA6B343u ^ (uint)z * 0xD8163841u ^ 0x9E3779B9u;
            h ^= h >> 15; h *= 0x2C1B3C6Du; h ^= h >> 12; h *= 0x297A2D39u; h ^= h >> 15;
            return h;
        }
    }
}
