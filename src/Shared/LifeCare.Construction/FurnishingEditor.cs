namespace LifeCare.Construction;

/// <summary>
/// Tiny Clinic-style furniture placement. Pieces are not placed from build mode: they arrive as a purchased package
/// (Pedizon) and are set down in the world, or picked up and moved, with a live ghost preview. The rules: every
/// sub-cell of the footprint stands on a floor on the owner's lot, the footprint never crosses a wall, and pieces never overlap.
/// </summary>
public static class FurnishingEditor
{
    public const int MaximumFurnishingsPerLot = 512;
    public const int MaximumFootprintSubCells = 32;

    /// <summary>The ghost's verdict for setting <paramref name="candidate"/> down; the same rules the server enforces.</summary>
    public static FurnishingPlacementPreview Preview(BuildingPlan plan, string actorCharacterId, PlacedFurnishing candidate)
    {
        if (actorCharacterId != plan.Lot.OwnerCharacterId) return Invalid(candidate, "not_owner", "Only the lot's owner can furnish here.");
        if (Problem(plan, candidate) is { } problem) return Invalid(candidate, problem.Code, problem.Message);
        var taken = plan.Furnishings.Where(f => f.InstanceId != candidate.InstanceId && f.Level == candidate.Level).SelectMany(f => f.SubCells()).ToHashSet();
        if (candidate.SubCells().Any(taken.Contains)) return Invalid(candidate, "furnishing_overlap", "Another furnishing is already there.");
        return new(candidate, true, "Place here.");
    }

    /// <summary>Sets down a piece carried from its package.</summary>
    public static BuildResult Place(BuildingPlan plan, string actorCharacterId, PlacedFurnishing piece)
    {
        if (plan.Furnishings.Any(f => f.InstanceId == piece.InstanceId)) return new(false, plan, "duplicate_furnishing", "That furnishing is already placed.");
        if (plan.Furnishings.Count >= MaximumFurnishingsPerLot) return new(false, plan, "lot_full", $"A lot holds at most {MaximumFurnishingsPerLot} furnishings.");
        return Commit(plan, actorCharacterId, piece, plan.Furnishings.Append(piece).ToList());
    }

    /// <summary>Moves or turns a piece already on the lot.</summary>
    public static BuildResult Move(BuildingPlan plan, string actorCharacterId, PlacedFurnishing moved)
    {
        var existing = plan.Furnishings.FirstOrDefault(f => f.InstanceId == moved.InstanceId);
        if (existing is null) return new(false, plan, "unknown_furnishing", "That furnishing is no longer present.");
        if (existing.DefinitionId != moved.DefinitionId || existing.FootprintWidthSubCells != moved.FootprintWidthSubCells || existing.FootprintDepthSubCells != moved.FootprintDepthSubCells)
            return new(false, plan, "invalid_furnishing", "A move cannot change what the furnishing is.");
        return Commit(plan, actorCharacterId, moved, plan.Furnishings.Select(f => f.InstanceId == moved.InstanceId ? moved : f).ToList());
    }

    /// <summary>Picks a piece up (back into its package).</summary>
    public static BuildResult PickUp(BuildingPlan plan, string actorCharacterId, string instanceId)
    {
        if (actorCharacterId != plan.Lot.OwnerCharacterId) return new(false, plan, "not_owner", "Only the lot's owner can furnish here.");
        var remaining = plan.Furnishings.Where(f => f.InstanceId != instanceId).ToList();
        return remaining.Count == plan.Furnishings.Count ? new(false, plan, "unknown_furnishing", "That furnishing is no longer present.") : new(true, plan with { Furnishings = remaining });
    }

    /// <summary>Why a piece cannot stand where it is in this plan, or null. Build edits use it so they never undercut furniture.</summary>
    public static (string Code, string Message)? Problem(BuildingPlan plan, PlacedFurnishing piece)
    {
        if (piece.Level < 0 || piece.YawQuarterTurns is < 0 or > 3 || piece.SubCellX is < 0 or >= BuildGrid.SubCellsPerTile || piece.SubCellZ is < 0 or >= BuildGrid.SubCellsPerTile
            || piece.FootprintWidthSubCells is < 1 or > MaximumFootprintSubCells || piece.FootprintDepthSubCells is < 1 or > MaximumFootprintSubCells)
            return ("invalid_furnishing", "That furnishing position, size or rotation is not valid.");
        var floor = plan.Floors.Where(f => f.Level == piece.Level).Select(f => f.Tile).ToHashSet();
        var tiles = new HashSet<TileCell>();
        foreach (var (x, z) in piece.SubCells())
        {
            var tile = PlacedFurnishing.TileOf(x, z);
            if (!tiles.Add(tile)) continue;
            if (!plan.Lot.Contains(tile)) return ("outside_lot", "Furnishings must stay on your lot.");
            if (!floor.Contains(tile)) return ("furnishing_requires_floor", "Every furnishing footprint must be supported by a constructed floor.");
        }
        var walls = plan.Walls.Where(w => w.Level == piece.Level).ToHashSet();
        foreach (var tile in tiles)
            foreach (var next in new TileCell[] { new(tile.X + 1, tile.Z), new(tile.X, tile.Z + 1) })
                if (tiles.Contains(next) && walls.Contains(WallSegment.Between(tile, next, piece.Level))) return ("furnishing_crosses_wall", "Furnishings cannot cross a wall.");
        float x0 = piece.MinimumX * BuildGrid.SubCellMeters + .01f, z0 = piece.MinimumZ * BuildGrid.SubCellMeters + .01f;
        float x1 = (piece.MinimumX + piece.SpanX) * BuildGrid.SubCellMeters - .01f, z1 = (piece.MinimumZ + piece.SpanZ) * BuildGrid.SubCellMeters - .01f;
        if (plan.CurvedWalls.Any(w => w.Level == piece.Level && CurvedWalls.Crosses(w, x0, z0, x1, z1)))
            return ("furnishing_crosses_wall", "Furnishings cannot cross a curved wall or counter.");
        return null;
    }

    private static BuildResult Commit(BuildingPlan plan, string actor, PlacedFurnishing piece, List<PlacedFurnishing> all)
    {
        var preview = Preview(plan, actor, piece);
        return preview.IsValid ? new(true, plan with { Furnishings = all }) : new(false, plan, preview.ErrorCode, preview.Status);
    }

    private static FurnishingPlacementPreview Invalid(PlacedFurnishing candidate, string code, string status) => new(candidate, false, status, code);
}
