namespace LifeCare.Construction;

public enum BuildEditKind { AddFloor, RemoveFloor, AddWall, RemoveWall, AddRoof, RemoveRoof }

/// <summary>One atomic build-mode edit. Floors use an inclusive tile rectangle; walls a list of edges; roofs one area.</summary>
public sealed record BuildEdit(BuildEditKind Kind, int Level, int MinimumTileX = 0, int MinimumTileZ = 0, int MaximumTileX = 0, int MaximumTileZ = 0,
    IReadOnlyList<WallSegment>? Walls = null, string? RoofId = null)
{
    public IEnumerable<FloorCell> FloorCells()
    {
        for (int z = Math.Min(MinimumTileZ, MaximumTileZ); z <= Math.Max(MinimumTileZ, MaximumTileZ); z++)
            for (int x = Math.Min(MinimumTileX, MaximumTileX); x <= Math.Max(MinimumTileX, MaximumTileX); x++)
                yield return new(x, z, Level);
    }
}

public sealed record BuildResult(bool Accepted, BuildingPlan Plan, string ErrorCode = "", string Message = "");

/// <summary>Applies edits to a lot's plan. All-or-nothing: a rejected edit leaves the plan unchanged.</summary>
public static class BuildingEditor
{
    public const int MaximumTilesPerEdit = 1024;

    public static BuildResult Apply(BuildingPlan plan, string actorCharacterId, BuildEdit edit)
    {
        BuildResult Reject(string code, string message) => new(false, plan, code, message);
        if (actorCharacterId != plan.Lot.OwnerCharacterId) return Reject("not_owner", "Only the lot's owner can build here.");
        if (edit.Level < 0) return Reject("invalid_level", "Build on the ground level or above.");

        var floors = new HashSet<FloorCell>(plan.Floors); var walls = new HashSet<WallSegment>(plan.Walls); var roofs = plan.Roofs.ToList();
        switch (edit.Kind)
        {
            case BuildEditKind.AddFloor or BuildEditKind.RemoveFloor:
                var cells = edit.FloorCells().Take(MaximumTilesPerEdit + 1).ToList();
                if (cells.Count > MaximumTilesPerEdit) return Reject("edit_too_large", $"One edit can change at most {MaximumTilesPerEdit} tiles.");
                if (cells.Any(c => !plan.Lot.Contains(c.Tile))) return Reject("outside_lot", "Every tile must be on your lot.");
                if (edit.Kind == BuildEditKind.AddFloor) floors.UnionWith(cells); else floors.ExceptWith(cells);
                break;
            case BuildEditKind.AddWall or BuildEditKind.RemoveWall:
                if (edit.Walls is not { Count: > 0 and <= MaximumTilesPerEdit } segments) return Reject("invalid_walls", "Give between 1 and 1024 wall edges.");
                if (segments.Any(w => w.Level != edit.Level)) return Reject("invalid_walls", "All walls in one edit share its level.");
                if (segments.Any(w => !plan.Lot.Contains(w))) return Reject("outside_lot", "Every wall must face a tile on your lot.");
                if (edit.Kind == BuildEditKind.AddWall) walls.UnionWith(segments); else walls.ExceptWith(segments);
                break;
            case BuildEditKind.AddRoof:
                if (string.IsNullOrWhiteSpace(edit.RoofId) || roofs.Any(r => r.RoofId == edit.RoofId)) return Reject("invalid_roof", "A new roof needs an unused id.");
                var roof = new RoofArea(edit.RoofId, Math.Min(edit.MinimumTileX, edit.MaximumTileX), Math.Min(edit.MinimumTileZ, edit.MaximumTileZ),
                    Math.Max(edit.MinimumTileX, edit.MaximumTileX), Math.Max(edit.MinimumTileZ, edit.MaximumTileZ), edit.Level);
                if (!plan.Lot.Contains(new TileCell(roof.MinimumTileX, roof.MinimumTileZ)) || !plan.Lot.Contains(new TileCell(roof.MaximumTileX, roof.MaximumTileZ))) return Reject("outside_lot", "The roof must be on your lot.");
                roofs.Add(roof); break;
            case BuildEditKind.RemoveRoof:
                if (roofs.RemoveAll(r => r.RoofId == edit.RoofId) == 0) return Reject("invalid_roof", "There is no roof with that id.");
                break;
            default: return Reject("invalid_edit", "Unknown build edit.");
        }

        var candidate = plan with { Floors = floors, Walls = walls, Roofs = roofs };
        if (edit.Kind is BuildEditKind.RemoveFloor or BuildEditKind.AddWall
            && candidate.Furnishings.FirstOrDefault(f => FurnishingEditor.Problem(candidate, f) is not null) is { } blocked)
            return Reject("furnishing_in_the_way", $"Move the {blocked.DefinitionId} first; this edit would leave it {(edit.Kind == BuildEditKind.AddWall ? "crossed by a wall" : "without a floor")}.");
        if (StructuralSupport.FindWorsenedViolation(plan, candidate) is { } violation) return Reject("insufficient_structural_support", violation.Message);
        return new(true, candidate);
    }
}
