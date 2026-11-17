namespace LifeCare.Construction;

/// <summary>
/// The Sims-style building grid. Structure snaps to Life Beyond's 4 m tiles; furniture snaps to Tiny Clinic's 0.5 m
/// sub-cells, eight per tile. Level 0 is the ground story. Pure data and rules, shared by the Construction service,
/// the district host and the client.
/// </summary>
public static class BuildGrid
{
    public const float TileMeters = 4;
    public const float SubCellMeters = 0.5f;
    public const int SubCellsPerTile = 8;
}

public readonly record struct TileCell(int X, int Z);

/// <summary>One 4 m floor tile on a story.</summary>
public readonly record struct FloorCell(int TileX, int TileZ, int Level)
{
    public TileCell Tile => new(TileX, TileZ);
}

/// <summary>
/// Walls sit on tile edges. An <see cref="WallAxis.AlongX"/> wall at (EdgeX, EdgeZ) separates tiles (EdgeX, EdgeZ-1) and (EdgeX, EdgeZ);
/// an <see cref="WallAxis.AlongZ"/> wall separates (EdgeX-1, EdgeZ) and (EdgeX, EdgeZ).
/// </summary>
public enum WallAxis { AlongX, AlongZ }

public readonly record struct WallSegment(int EdgeX, int EdgeZ, WallAxis Axis, int Level)
{
    public (TileCell Before, TileCell After) Sides => Axis == WallAxis.AlongX
        ? (new(EdgeX, EdgeZ - 1), new(EdgeX, EdgeZ))
        : (new(EdgeX - 1, EdgeZ), new(EdgeX, EdgeZ));

    /// <summary>The wall on the shared edge of two orthogonally adjacent tiles.</summary>
    public static WallSegment Between(TileCell a, TileCell b, int level)
    {
        if (a.X == b.X && Math.Abs(a.Z - b.Z) == 1) return new(a.X, Math.Max(a.Z, b.Z), WallAxis.AlongX, level);
        if (a.Z == b.Z && Math.Abs(a.X - b.X) == 1) return new(Math.Max(a.X, b.X), a.Z, WallAxis.AlongZ, level);
        throw new ArgumentException("Tiles are not orthogonal neighbours.");
    }
}

/// <summary>A flat roof over an inclusive tile rectangle on one story.</summary>
public sealed record RoofArea(string RoofId, int MinimumTileX, int MinimumTileZ, int MaximumTileX, int MaximumTileZ, int Level);

/// <summary>A deeded lot: the tile rectangle its owner may build on.</summary>
public sealed record LotDeed(string LotId, string OwnerCharacterId, int MinimumTileX, int MinimumTileZ, int MaximumTileX, int MaximumTileZ)
{
    public bool Contains(TileCell tile) => tile.X >= MinimumTileX && tile.X <= MaximumTileX && tile.Z >= MinimumTileZ && tile.Z <= MaximumTileZ;
    /// <summary>A wall belongs to the lot when at least one tile it faces is on the lot, so boundary walls are allowed.</summary>
    public bool Contains(WallSegment wall) => Contains(wall.Sides.Before) || Contains(wall.Sides.After);
}

/// <summary>Everything built on one lot.</summary>
public sealed record BuildingPlan(LotDeed Lot, IReadOnlySet<FloorCell> Floors, IReadOnlySet<WallSegment> Walls, IReadOnlyList<RoofArea> Roofs)
{
    public IReadOnlyList<PlacedFurnishing> Furnishings { get; init; } = [];
    public static BuildingPlan Empty(LotDeed lot) => new(lot, new HashSet<FloorCell>(), new HashSet<WallSegment>(), []);
}

/// <summary>A furnishing definition (Tiny Clinic style): its footprint in 0.5 m sub-cells before rotation.</summary>
public sealed record FurnishingDefinition(string DefinitionId, string Name, int FootprintWidthSubCells, int FootprintDepthSubCells);

/// <summary>
/// A placed furnishing, anchored like Tiny Clinic: a tile plus a sub-cell (0..7) inside it marks the footprint's minimum
/// corner, and odd quarter turns swap width and depth.
/// </summary>
public sealed record PlacedFurnishing(string InstanceId, string DefinitionId, int Level, int TileX, int TileZ, int SubCellX, int SubCellZ,
    int YawQuarterTurns, int FootprintWidthSubCells, int FootprintDepthSubCells)
{
    public static PlacedFurnishing Of(string instanceId, FurnishingDefinition definition, int level, int tileX, int tileZ, int subCellX, int subCellZ, int yawQuarterTurns) =>
        new(instanceId, definition.DefinitionId, level, tileX, tileZ, subCellX, subCellZ, yawQuarterTurns, definition.FootprintWidthSubCells, definition.FootprintDepthSubCells);

    public int SpanX => (YawQuarterTurns & 1) == 0 ? FootprintWidthSubCells : FootprintDepthSubCells;
    public int SpanZ => (YawQuarterTurns & 1) == 0 ? FootprintDepthSubCells : FootprintWidthSubCells;
    /// <summary>Absolute sub-cell of the minimum corner.</summary>
    public int MinimumX => checked(TileX * BuildGrid.SubCellsPerTile + SubCellX);
    public int MinimumZ => checked(TileZ * BuildGrid.SubCellsPerTile + SubCellZ);
    public IEnumerable<(int X, int Z)> SubCells()
    {
        for (int z = MinimumZ; z < MinimumZ + SpanZ; z++) for (int x = MinimumX; x < MinimumX + SpanX; x++) yield return (x, z);
    }
    public static TileCell TileOf(int subCellX, int subCellZ) => new(FloorDiv(subCellX), FloorDiv(subCellZ));
    private static int FloorDiv(int value) => (int)Math.Floor(value / (double)BuildGrid.SubCellsPerTile);
}

/// <summary>The ghost shown while a piece is carried or moved: whether it may be set down here, and why not.</summary>
public sealed record FurnishingPlacementPreview(PlacedFurnishing Candidate, bool IsValid, string Status, string ErrorCode = "");
