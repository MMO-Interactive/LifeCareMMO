namespace LifeCare.Construction;

/// <summary>
/// Life Beyond's structural rule (ConstructionStructuralCapacity / ConstructionStructuralSupportValidator), ported unchanged.
/// Story one is the ground level and there is no global story cap: an orthogonally connected ground-level footprint supports
/// 2 + (effective area / 4) stories, where the effective area is capped at the shorter span squared so a long narrow strip
/// cannot hold up a tower. A 2x2 base supports three stories, 4x4 six, 14x14 fifty-one.
/// </summary>
public static class StructuralCapacity
{
    public const int FoundationTilesPerAdditionalStory = 4;

    public static int MaximumStoriesForContiguousFootprint(int tileCount, int widthTiles, int depthTiles)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(tileCount);
        ArgumentOutOfRangeException.ThrowIfNegative(widthTiles);
        ArgumentOutOfRangeException.ThrowIfNegative(depthTiles);
        if (tileCount == 0 || widthTiles == 0 || depthTiles == 0) return 1;
        long shorterSpan = Math.Min(widthTiles, depthTiles);
        long effectiveArea = Math.Min(tileCount, checked(shorterSpan * shorterSpan));
        long stories = 2L + effectiveArea / FoundationTilesPerAdditionalStory;
        return stories >= int.MaxValue ? int.MaxValue : (int)stories;
    }
}

public sealed record StructuralViolation(string ElementKey, string ElementKind, int Level, long RequiredStories, int SupportedStories,
    long DeficitStories, int FoundationTileCount, int FoundationWidth, int FoundationDepth)
{
    public string Message => FoundationTileCount == 0
        ? $"The {ElementKind.ToLowerInvariant()} on level {Level} is story {RequiredStories}, but it has no connected ground-level floor beneath it. Add a ground-level base under the upper story before building higher."
        : $"The {ElementKind.ToLowerInvariant()} on level {Level} is story {RequiredStories}, but its connected {FoundationWidth}x{FoundationDepth} ground-level footprint ({FoundationTileCount} tiles) supports at most {SupportedStories} stories. Widen the connected ground-level base before building higher.";
}

public static class StructuralSupport
{
    /// <summary>
    /// The first violation an edit adds or makes worse, or null. Existing violations are grandfathered so corrective and
    /// cosmetic edits stay possible, but no edit may add a violation or deepen one.
    /// </summary>
    public static StructuralViolation? FindWorsenedViolation(BuildingPlan existing, BuildingPlan candidate)
    {
        var before = FindViolations(existing);
        return FindViolations(candidate).Values
            .Where(v => !before.TryGetValue(v.ElementKey, out var prior) || v.DeficitStories > prior.DeficitStories)
            .OrderBy(v => v.RequiredStories).ThenBy(v => v.ElementKey, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    public static Dictionary<string, StructuralViolation> FindViolations(BuildingPlan plan)
    {
        var support = BuildFoundationSupport(plan.Floors);
        var violations = new Dictionary<string, StructuralViolation>(StringComparer.Ordinal);
        foreach (var floor in plan.Floors.Where(f => f.Level > 0))
            Add(violations, $"floor:{floor.TileX}:{floor.TileZ}:{floor.Level}", "Floor", floor.Level, For(support, floor.Tile));
        foreach (var wall in plan.Walls.Where(w => w.Level > 0))
        {
            var (a, b) = wall.Sides;
            Add(violations, $"wall:{wall.EdgeX}:{wall.EdgeZ}:{(int)wall.Axis}:{wall.Level}", "Wall", wall.Level, Better(For(support, a), For(support, b)));
        }
        foreach (var wall in plan.CurvedWalls.Where(w => w.Level > 0))
            Add(violations, $"curved:{wall.WallId}", "Curved wall", wall.Level, For(support, LotBuildController.TileAt(wall.CenterX, wall.CenterZ)));
        foreach (var roof in plan.Roofs.Where(r => r.Level > 0))
            for (int z = roof.MinimumTileZ; z <= roof.MaximumTileZ; z++)
                for (int x = roof.MinimumTileX; x <= roof.MaximumTileX; x++)
                    Add(violations, $"roof:{roof.RoofId}:{x}:{z}:{roof.Level}", "Roof", roof.Level, For(support, new(x, z)));
        return violations;
    }

    private static Dictionary<TileCell, Foundation> BuildFoundationSupport(IEnumerable<FloorCell> floors)
    {
        var remaining = floors.Where(f => f.Level == 0).Select(f => f.Tile).ToHashSet();
        var result = new Dictionary<TileCell, Foundation>();
        var pending = new Queue<TileCell>();
        while (remaining.Count > 0)
        {
            var first = remaining.First(); remaining.Remove(first); pending.Enqueue(first);
            var component = new List<TileCell>();
            int minX = first.X, maxX = first.X, minZ = first.Z, maxZ = first.Z;
            while (pending.TryDequeue(out var current))
            {
                component.Add(current);
                minX = Math.Min(minX, current.X); maxX = Math.Max(maxX, current.X); minZ = Math.Min(minZ, current.Z); maxZ = Math.Max(maxZ, current.Z);
                foreach (var next in new TileCell[] { new(current.X - 1, current.Z), new(current.X + 1, current.Z), new(current.X, current.Z - 1), new(current.X, current.Z + 1) })
                    if (remaining.Remove(next)) pending.Enqueue(next);
            }
            int width = checked(maxX - minX + 1), depth = checked(maxZ - minZ + 1);
            var foundation = new Foundation(StructuralCapacity.MaximumStoriesForContiguousFootprint(component.Count, width, depth), component.Count, width, depth);
            foreach (var cell in component) result[cell] = foundation;
        }
        return result;
    }

    private static void Add(Dictionary<string, StructuralViolation> violations, string key, string kind, int level, Foundation support)
    {
        long required = level + 1L;
        if (required <= support.SupportedStories) return;
        violations[key] = new(key, kind, level, required, support.SupportedStories, required - support.SupportedStories, support.TileCount, support.Width, support.Depth);
    }

    private static Foundation For(Dictionary<TileCell, Foundation> support, TileCell tile) => support.TryGetValue(tile, out var f) ? f : Foundation.None;
    private static Foundation Better(Foundation a, Foundation b) => a.SupportedStories >= b.SupportedStories ? a : b;

    private readonly record struct Foundation(int SupportedStories, int TileCount, int Width, int Depth)
    {
        public static Foundation None { get; } = new(1, 0, 0, 0);
    }
}
