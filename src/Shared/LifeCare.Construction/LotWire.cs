namespace LifeCare.Construction;

/// <summary>The wire and disk form of one lot. Revision increases by one with every accepted change.</summary>
/// <remarks>Curved walls and finishes are optional on the wire, so lot files written before they existed load unchanged.</remarks>
public sealed record LotDocument(LotDeed Lot, long Revision, FloorCell[] Floors, WallSegment[] Walls, RoofArea[] Roofs, PlacedFurnishing[] Furnishings)
{
    public CurvedWall[] CurvedWalls { get; init; } = [];
    public WallFinish[] WallFinishes { get; init; } = [];
    public FloorFinish[] FloorFinishes { get; init; } = [];
    public string[] OpenDoors { get; init; } = [];

    public BuildingPlan ToPlan() => new BuildingPlan(Lot, Floors.ToHashSet(), Walls.ToHashSet(), Roofs)
    {
        Furnishings = Furnishings, CurvedWalls = CurvedWalls ?? [], WallFinishes = WallFinishes ?? [], FloorFinishes = FloorFinishes ?? [], OpenDoors = OpenDoors ?? [],
    };

    public static LotDocument From(BuildingPlan plan, long revision) =>
        new(plan.Lot, revision, plan.Floors.OrderBy(f => f.Level).ThenBy(f => f.TileZ).ThenBy(f => f.TileX).ToArray(),
            plan.Walls.OrderBy(w => w.Level).ThenBy(w => w.EdgeZ).ThenBy(w => w.EdgeX).ThenBy(w => w.Axis).ToArray(), plan.Roofs.ToArray(), plan.Furnishings.ToArray())
        {
            CurvedWalls = plan.CurvedWalls.OrderBy(w => w.WallId, StringComparer.Ordinal).ToArray(),
            WallFinishes = plan.WallFinishes.OrderBy(f => f.Wall.Level).ThenBy(f => f.Wall.EdgeZ).ThenBy(f => f.Wall.EdgeX).ThenBy(f => f.Wall.Axis).ToArray(),
            FloorFinishes = plan.FloorFinishes.OrderBy(f => f.Floor.Level).ThenBy(f => f.Floor.TileZ).ThenBy(f => f.Floor.TileX).ToArray(),
            OpenDoors = plan.OpenDoors.Order(StringComparer.Ordinal).ToArray(),
        };
}

/// <summary>One lot in a listing: enough for a client or district host to know whether its copy is current.</summary>
public sealed record LotSummary(LotDeed Lot, long Revision);

public sealed record ChangeResult(bool Accepted, LotDocument Lot, string ErrorCode = "", string Message = "");

public sealed record BuildRequest(string CharacterId, BuildEdit Edit, long? ExpectedRevision = null);

/// <summary>Play-testing only: claim a starter lot next to the given tile (the service must run with --dev-starter-lots).</summary>
public sealed record StarterLotRequest(string CharacterId, int TileX, int TileZ);

public sealed record FurnishingRequest(string CharacterId, PlacedFurnishing? Piece = null, string? InstanceId = null, long? ExpectedRevision = null);
