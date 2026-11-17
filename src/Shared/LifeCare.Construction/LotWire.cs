namespace LifeCare.Construction;

/// <summary>The wire and disk form of one lot. Revision increases by one with every accepted change.</summary>
public sealed record LotDocument(LotDeed Lot, long Revision, FloorCell[] Floors, WallSegment[] Walls, RoofArea[] Roofs, PlacedFurnishing[] Furnishings)
{
    public BuildingPlan ToPlan() => new BuildingPlan(Lot, Floors.ToHashSet(), Walls.ToHashSet(), Roofs) { Furnishings = Furnishings };
    public static LotDocument From(BuildingPlan plan, long revision) =>
        new(plan.Lot, revision, plan.Floors.OrderBy(f => f.Level).ThenBy(f => f.TileZ).ThenBy(f => f.TileX).ToArray(),
            plan.Walls.OrderBy(w => w.Level).ThenBy(w => w.EdgeZ).ThenBy(w => w.EdgeX).ThenBy(w => w.Axis).ToArray(), plan.Roofs.ToArray(), plan.Furnishings.ToArray());
}

/// <summary>One lot in a listing: enough for a client or district host to know whether its copy is current.</summary>
public sealed record LotSummary(LotDeed Lot, long Revision);

public sealed record ChangeResult(bool Accepted, LotDocument Lot, string ErrorCode = "", string Message = "");

public sealed record BuildRequest(string CharacterId, BuildEdit Edit, long? ExpectedRevision = null);

public sealed record FurnishingRequest(string CharacterId, PlacedFurnishing? Piece = null, string? InstanceId = null, long? ExpectedRevision = null);
