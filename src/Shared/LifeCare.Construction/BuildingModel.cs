namespace LifeCare.Construction;

/// <summary>
/// The Sims-style building grid, following Life Beyond's model: structure snaps to 4 m tiles, furnishing to a 1 m sub-grid.
/// Level 0 is the ground story. Pure data and rules, shared by the Construction service, the district host and the client.
/// </summary>
public static class BuildGrid
{
    public const float TileMeters = 4;
    public const float FurnishingMeters = 1;
    public const int FurnishingCellsPerTile = 4;
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
    public static BuildingPlan Empty(LotDeed lot) => new(lot, new HashSet<FloorCell>(), new HashSet<WallSegment>(), []);
}
