namespace LifeCare.Construction;

public enum BuildEditKind
{
    AddFloor, RemoveFloor, AddWall, RemoveWall, AddRoof, RemoveRoof,
    /// <summary>Restyles existing straight walls and sets the opening (door, window) each one hosts.</summary>
    SetWallFinish,
    /// <summary>Adds a true curved wall (an arc or a full ring) from <see cref="BuildEdit.Curve"/>.</summary>
    AddCurvedWall,
    /// <summary>Replaces an existing curved wall, matched by id: reshape it, restyle it, or change its openings.</summary>
    SetCurvedWall,
    /// <summary>Removes the curved wall whose id is <see cref="BuildEdit.TargetId"/>.</summary>
    RemoveCurvedWall,
}

/// <summary>
/// One atomic build-mode edit. Floors use an inclusive tile rectangle; walls a list of edges; roofs one area; curved walls
/// an analytic arc. <see cref="Style"/>/<see cref="Opening"/> finish straight walls, <see cref="Finish"/> sets a floor or
/// roof finish and <see cref="Shape"/> a roof shape. Omitted finishes keep the defaults (or what is already there).
/// </summary>
public sealed record BuildEdit(BuildEditKind Kind, int Level, int MinimumTileX = 0, int MinimumTileZ = 0, int MaximumTileX = 0, int MaximumTileZ = 0,
    IReadOnlyList<WallSegment>? Walls = null, string? RoofId = null, string? Style = null, string? Opening = null, string? Finish = null,
    string? Shape = null, CurvedWall? Curve = null, string? TargetId = null, string? Paint = null, string? InsidePaint = null, string? OutsidePaint = null)
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
    /// <summary>A paint value that clears a face back to its finish's own colour.</summary>
    public const string NoPaint = "none";

    public static BuildResult Apply(BuildingPlan plan, string actorCharacterId, BuildEdit edit)
    {
        BuildResult Reject(string code, string message) => new(false, plan, code, message);
        if (actorCharacterId != plan.Lot.OwnerCharacterId) return Reject("not_owner", "Only the lot's owner can build here.");
        if (edit.Level < 0) return Reject("invalid_level", "Build on the ground level or above.");

        var floors = new HashSet<FloorCell>(plan.Floors); var walls = new HashSet<WallSegment>(plan.Walls); var roofs = plan.Roofs.ToList();
        var wallFinishes = plan.WallFinishes.ToDictionary(f => f.Wall); var floorFinishes = plan.FloorFinishes.ToDictionary(f => f.Floor);
        var curved = plan.CurvedWalls.ToList();
        switch (edit.Kind)
        {
            case BuildEditKind.AddFloor or BuildEditKind.RemoveFloor:
                var cells = edit.FloorCells().Take(MaximumTilesPerEdit + 1).ToList();
                if (cells.Count > MaximumTilesPerEdit) return Reject("edit_too_large", $"One edit can change at most {MaximumTilesPerEdit} tiles.");
                if (cells.Any(c => !plan.Lot.Contains(c.Tile))) return Reject("outside_lot", "Every tile must be on your lot.");
                if (edit.Kind == BuildEditKind.AddFloor)
                {
                    if (edit.Finish is not null && !ModuleCatalog.IsFloorFinish(edit.Finish)) return Reject("invalid_finish", "Unknown floor finish.");
                    if (edit.Paint is not null && edit.Paint != NoPaint && ModuleCatalog.Paint(edit.Paint) is null) return Reject("invalid_paint", "Unknown paint.");
                    floors.UnionWith(cells);
                    if (edit.Finish is not null || edit.Paint is not null)
                        foreach (var c in cells)
                        {
                            var old = floorFinishes.GetValueOrDefault(c);
                            string finish = edit.Finish ?? old?.Finish ?? ModuleCatalog.DefaultFloorFinish;
                            string? paint = edit.Paint == NoPaint ? null : edit.Paint ?? (edit.Finish is null ? old?.Paint : null);
                            if (finish == ModuleCatalog.DefaultFloorFinish && paint is null) floorFinishes.Remove(c); else floorFinishes[c] = new(c, finish, paint);
                        }
                }
                else { floors.ExceptWith(cells); foreach (var c in cells) floorFinishes.Remove(c); }
                break;
            case BuildEditKind.AddWall or BuildEditKind.RemoveWall or BuildEditKind.SetWallFinish:
                if (edit.Walls is not { Count: > 0 and <= MaximumTilesPerEdit } segments) return Reject("invalid_walls", "Give between 1 and 1024 wall edges.");
                if (segments.Any(w => w.Level != edit.Level)) return Reject("invalid_walls", "All walls in one edit share its level.");
                if (segments.Any(w => !plan.Lot.Contains(w))) return Reject("outside_lot", "Every wall must face a tile on your lot.");
                if (edit.Kind == BuildEditKind.RemoveWall) { walls.ExceptWith(segments); foreach (var w in segments) wallFinishes.Remove(w); break; }
                if (edit.Style is not null && ModuleCatalog.Style(edit.Style) is null) return Reject("invalid_style", "Unknown wall finish.");
                if (edit.Opening is not null && !ModuleCatalog.StraightOpenings.Any(o => o.Id == edit.Opening)) return Reject("invalid_opening", "That opening does not fit a straight wall.");
                if (edit.Kind == BuildEditKind.SetWallFinish && segments.Any(w => !walls.Contains(w))) return Reject("unknown_wall", "Build the wall before finishing it.");
                foreach (var paint in new[] { edit.InsidePaint, edit.OutsidePaint })
                    if (paint is not null && paint != NoPaint && ModuleCatalog.Paint(paint) is null) return Reject("invalid_paint", "Unknown paint.");
                walls.UnionWith(segments);
                if (edit.Style is not null || edit.Opening is not null || edit.InsidePaint is not null || edit.OutsidePaint is not null)
                    foreach (var w in segments)
                    {
                        var old = wallFinishes.GetValueOrDefault(w);
                        string? Paint(string? requested, string? current) => requested == NoPaint ? null : requested ?? current;
                        var finish = new WallFinish(w, edit.Style ?? old?.Style ?? "", edit.Opening ?? old?.Opening ?? ModuleCatalog.NoOpening,
                            Paint(edit.InsidePaint, old?.InsidePaint), Paint(edit.OutsidePaint, old?.OutsidePaint));
                        if (finish is { Style: "", Opening: ModuleCatalog.NoOpening, InsidePaint: null, OutsidePaint: null }) wallFinishes.Remove(w); else wallFinishes[w] = finish;
                    }
                break;
            case BuildEditKind.AddRoof:
                if (string.IsNullOrWhiteSpace(edit.RoofId) || roofs.Any(r => r.RoofId == edit.RoofId)) return Reject("invalid_roof", "A new roof needs an unused id.");
                string shape = edit.Shape ?? ModuleCatalog.DefaultRoofShape, roofFinish = edit.Finish ?? ModuleCatalog.DefaultRoofFinish;
                if (!ModuleCatalog.IsRoofShape(shape)) return Reject("invalid_roof", "Unknown roof shape.");
                if (!ModuleCatalog.IsRoofFinish(roofFinish)) return Reject("invalid_finish", "Unknown roof finish.");
                var roof = new RoofArea(edit.RoofId, Math.Min(edit.MinimumTileX, edit.MaximumTileX), Math.Min(edit.MinimumTileZ, edit.MaximumTileZ),
                    Math.Max(edit.MinimumTileX, edit.MaximumTileX), Math.Max(edit.MinimumTileZ, edit.MaximumTileZ), edit.Level, shape, roofFinish);
                if (!plan.Lot.Contains(new TileCell(roof.MinimumTileX, roof.MinimumTileZ)) || !plan.Lot.Contains(new TileCell(roof.MaximumTileX, roof.MaximumTileZ))) return Reject("outside_lot", "The roof must be on your lot.");
                if (shape != "flat" && !(roof.DepthTiles == 2 && roof.WidthTiles >= 2 || roof.WidthTiles == 2 && roof.DepthTiles >= 2))
                    return Reject("pitched_roof_span", "Gable and hip roofs span exactly 2 tiles (8 m) across and at least 2 tiles along the ridge.");
                roofs.Add(roof); break;
            case BuildEditKind.RemoveRoof:
                if (roofs.RemoveAll(r => r.RoofId == edit.RoofId) == 0) return Reject("invalid_roof", "There is no roof with that id.");
                break;
            case BuildEditKind.AddCurvedWall or BuildEditKind.SetCurvedWall:
                if (edit.Curve is not { } curve) return Reject("invalid_curved_wall", "Describe the curved wall to build.");
                if (curve.Level != edit.Level) return Reject("invalid_curved_wall", "The curved wall must be on the edit's level.");
                if (CurvedWalls.Problem(curve, plan.Lot) is { } problem) return Reject(problem.Code, problem.Message);
                int existing = curved.FindIndex(w => w.WallId == curve.WallId);
                if (edit.Kind == BuildEditKind.AddCurvedWall)
                {
                    if (existing >= 0) return Reject("invalid_curved_wall", "A new curved wall needs an unused id.");
                    if (curved.Count >= CurvedWalls.MaximumPerLot) return Reject("lot_full", $"A lot holds at most {CurvedWalls.MaximumPerLot} curved walls.");
                    curved.Add(curve);
                }
                else
                {
                    if (existing < 0) return Reject("unknown_wall", "There is no curved wall with that id.");
                    curved[existing] = curve;
                }
                break;
            case BuildEditKind.RemoveCurvedWall:
                if (curved.RemoveAll(w => w.WallId == edit.TargetId) == 0) return Reject("unknown_wall", "There is no curved wall with that id.");
                break;
            default: return Reject("invalid_edit", "Unknown build edit.");
        }

        var candidate = plan with
        {
            Floors = floors, Walls = walls, Roofs = roofs, CurvedWalls = curved,
            WallFinishes = wallFinishes.Values.Where(f => walls.Contains(f.Wall)).ToList(),
            FloorFinishes = floorFinishes.Values.Where(f => floors.Contains(f.Floor)).ToList(),
        };
        // A door that was removed or replaced is no longer open.
        if (candidate.OpenDoors.Count > 0)
        {
            var doors = LotDoors.All(candidate).Select(d => d.DoorId).ToHashSet();
            candidate = candidate with { OpenDoors = candidate.OpenDoors.Where(doors.Contains).ToList() };
        }
        if (edit.Kind is BuildEditKind.RemoveFloor or BuildEditKind.AddWall or BuildEditKind.AddCurvedWall or BuildEditKind.SetCurvedWall
            && candidate.Furnishings.FirstOrDefault(f => FurnishingEditor.Problem(candidate, f) is not null) is { } blocked)
            return Reject("furnishing_in_the_way", $"Move the {blocked.DefinitionId} first; this edit would leave it {(edit.Kind == BuildEditKind.RemoveFloor ? "without a floor" : "crossed by a wall")}.");
        if (StructuralSupport.FindWorsenedViolation(plan, candidate) is { } violation) return Reject("insufficient_structural_support", violation.Message);
        return new(true, candidate);
    }
}
