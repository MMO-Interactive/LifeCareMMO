using System.Numerics;

namespace LifeCare.Construction;

/// <summary>
/// Build mode edits only the shell. Furniture is never a build tool (Tiny Clinic rule): it arrives as a package.
/// Room draws a floored, walled rectangle; Wall drags walls (full, half or railing) along grid lines; CurvedWall starts on
/// a corner and drags out the curve; Fitting sets doors and windows into walls; Finish changes a wall's siding; PaintWall
/// paints the face you point at; Floor lays or repaints floors (carpet, vinyl, tile, wood); Roof and Delete as before.
/// </summary>
public enum LotBuildTool { Floor, Wall, Roof, Delete, CurvedWall, Finish, Room, Fitting, PaintWall }

/// <summary>The build ghost: the edit the gesture would send (plus any edits that follow it), and the authority's verdict.</summary>
public sealed record LotBuildPreview(LotBuildTool Tool, int Level, BuildEdit? Edit, bool IsValid, string Status, IReadOnlyList<BuildEdit>? FollowUps = null)
{
    public static LotBuildPreview None(LotBuildTool tool, int level, string status) => new(tool, level, null, false, status);
    public IEnumerable<BuildEdit> Edits => Edit is null ? [] : FollowUps is null ? [Edit] : FollowUps.Prepend(Edit);
}

/// <summary>What the next edit uses, Sims-style: the wall's siding and kind, the fitting, the paint, the floor, the roof and the curve.</summary>
public sealed record LotBuildSelection(string WallStyle, string WallKind, string Fitting, string WallPaint, string FloorFinish, string? FloorPaint,
    string RoofShape, string RoofFinish, string CurvedProfile)
{
    public static LotBuildSelection Default { get; } = new(ModuleCatalog.DefaultExteriorStyle, ModuleCatalog.NoOpening, "door_wood", "white",
        ModuleCatalog.DefaultFloorFinish, null, ModuleCatalog.DefaultRoofShape, ModuleCatalog.DefaultRoofFinish, "full");
}

/// <summary>
/// Client build mode for one lot, after Tiny Clinic's ClinicBuildController and Life Beyond's grid gestures, aimed with the
/// mouse from the build camera. Rectangles (room, floor, roof) and wall runs are dragged on the 4 m grid. A curved wall is
/// pressed on its starting corner and dragged to the point halfway round; releasing locks that half circle, then moving the
/// mouse carries the curve further round, and a click (or H for half, F for the full circle) finishes it. Every ghost is
/// checked with the same <see cref="BuildingEditor"/> rules the Construction service enforces, and edits go one at a time
/// through the <see cref="LotSession"/>.
/// </summary>
public sealed class LotBuildController
{
    public const int MaximumLevels = 8;
    /// <summary>Points snap to this grid; wall corners within <see cref="CornerSnapMeters"/> win.</summary>
    public const float FineSnapMeters = .5f, CornerSnapMeters = .6f;
    private readonly LotSession session;
    private (float X, float Z) pointer;
    private (float X, float Z)? dragStart;
    private (Vector3 Origin, Vector3 Direction)? ray;
    private CurveDraft? curve;
    private readonly Queue<(BuildEdit Edit, string Name)> followUps = new();

    /// <summary>A curve after its half point is set: the circle is fixed, the sweep follows the mouse.</summary>
    private sealed record CurveDraft(float CenterX, float CenterZ, float Radius, double StartAngle);

    public LotBuildController(LotSession session)
    {
        this.session = session ?? throw new ArgumentNullException(nameof(session));
        Preview = LotBuildPreview.None(Tool, Level, "Choose a tool.");
        session.LotChanged += _ => UpdatePreview();
        session.EditResolved += (accepted, _) =>
        {
            if (!accepted) { followUps.Clear(); return; }
            if (followUps.TryDequeue(out var next))
                session.Submit((authority, revision) => authority.BuildAsync(session.Lot.Lot.LotId, new(session.CharacterId, next.Edit, revision)), next.Name);
        };
    }

    public LotSession Session => session;
    public LotBuildTool Tool { get; private set; } = LotBuildTool.Floor;
    public LotBuildSelection Selection { get; private set; } = LotBuildSelection.Default;
    public int Level { get; private set; }
    public LotBuildPreview Preview { get; private set; }
    public bool IsDragging => dragStart is not null || curve is not null;
    public bool CurveExtending => curve is not null;
    public string Status => session.Status;
    /// <summary>Held by the client while Shift is down: wall paint covers the whole room you point into.</summary>
    public bool PaintWholeRoom { get; set; }

    public void SelectTool(LotBuildTool tool)
    {
        Tool = tool; dragStart = null; curve = null;
        session.Status = ToolHelp(tool);
        UpdatePreview();
    }

    public static string ToolName(LotBuildTool tool) => tool switch
    {
        LotBuildTool.Room => "Room", LotBuildTool.Wall => "Wall", LotBuildTool.CurvedWall => "Curved wall", LotBuildTool.Fitting => "Doors & windows",
        LotBuildTool.Finish => "Wall style", LotBuildTool.PaintWall => "Wall paint", LotBuildTool.Floor => "Floor", LotBuildTool.Roof => "Roof", _ => "Delete",
    };

    public static string ToolHelp(LotBuildTool tool) => tool switch
    {
        LotBuildTool.Room => "Room: drag a rectangle to lay its floor and wall it in.",
        LotBuildTool.Wall => "Wall: drag along the grid lines; a click takes the nearest edge.",
        LotBuildTool.CurvedWall => "Curved wall: press on the corner where it starts and drag to the point halfway round.",
        LotBuildTool.Fitting => "Doors & windows: click a wall (or drag along several) to set the fitting into it.",
        LotBuildTool.Finish => "Wall style: click or drag along walls to change their siding.",
        LotBuildTool.PaintWall => "Wall paint: click the face to paint; hold Shift to paint the whole room.",
        LotBuildTool.Floor => "Floor: drag a rectangle to lay or repaint the floor.",
        LotBuildTool.Roof => "Roof: drag over the building; gable and hip roofs span 2 tiles.",
        _ => "Delete: click a wall, curved wall, roof or floor; drag to clear floor.",
    };

    /// <summary>Picks an item from the build catalog: the tool it belongs to and the option it sets.</summary>
    public void Choose(LotBuildTool tool, Func<LotBuildSelection, LotBuildSelection> change)
    {
        Selection = change(Selection);
        if (Tool != tool) SelectTool(tool); else { session.Status = ToolHelp(tool); UpdatePreview(); }
    }

    /// <summary>Build mode has no furniture tools; this always refuses and says where furniture comes from.</summary>
    public bool SelectFurnishing(string definitionId)
    {
        _ = definitionId;
        session.Status = "Build mode edits floors, walls and roofs only. Furniture arrives as a package; set it down in the world.";
        return false;
    }

    public void ChangeLevel(int delta)
    {
        Level = Math.Clamp(Level + delta, 0, MaximumLevels - 1); dragStart = null; curve = null;
        session.Status = Level == 0 ? "Ground floor." : $"Floor {Level + 1}.";
        UpdatePreview();
    }

    /// <summary>The point on the build plane under the cursor, in world metres.</summary>
    public void SetPointer(float worldX, float worldZ)
    {
        if (!float.IsFinite(worldX) || !float.IsFinite(worldZ)) return;
        pointer = (worldX, worldZ); ray = null; UpdatePreview();
    }

    /// <summary>The mouse ray from the build camera and where it meets this level's floor; wall tools hit the face the ray reaches first.</summary>
    public void SetRay(Vector3 origin, Vector3 direction, float worldX, float worldZ, float levelBase)
    {
        if (!float.IsFinite(worldX) || !float.IsFinite(worldZ)) return;
        pointer = (worldX, worldZ); ray = (origin, Vector3.Normalize(direction)); rayLevelBase = levelBase; UpdatePreview();
    }
    private float rayLevelBase;

    public void BeginGesture()
    {
        if (session.HasPendingEdit || followUps.Count > 0) { session.Status = "Waiting for the lot authority to finish the previous edit."; return; }
        if (!session.IsOwner) { session.Status = "Only the lot's owner can build here."; return; }
        if (curve is not null) return;   // the click that finishes a curve completes on release
        dragStart = Tool == LotBuildTool.CurvedWall ? Snap(pointer) : pointer; UpdatePreview();
    }

    /// <summary>Ends the drag. Sends its edit if the ghost is valid; for a curved wall, the first release locks the half circle.</summary>
    public bool CompleteGesture()
    {
        if (Tool == LotBuildTool.CurvedWall && curve is null && dragStart is { } start)
        {
            var half = Snap(pointer);
            float dx = half.X - start.X, dz = half.Z - start.Z, diameter = MathF.Sqrt(dx * dx + dz * dz);
            dragStart = null;
            if (diameter < 2 * CurvedWalls.MinimumRadiusMillimeters / 1000f) { session.Status = "Drag at least 3 m from the corner to the point halfway round."; UpdatePreview(); return false; }
            curve = new((start.X + half.X) / 2, (start.Z + half.Z) / 2, diameter / 2, Math.Atan2(start.Z - (start.Z + half.Z) / 2, start.X - (start.X + half.X) / 2));
            session.Status = "Move the mouse to carry the curve further round; click to finish · H half · F full circle.";
            UpdatePreview(); return false;
        }
        if (dragStart is null && curve is null) return false;
        UpdatePreview();
        return Send();
    }

    /// <summary>Finishes the curve being drawn as exactly a half circle (H) or a full circle (F).</summary>
    public bool FinishCurve(bool fullCircle)
    {
        if (curve is null) return false;
        forcedSweep = fullCircle ? 360_000 : 180_000;
        UpdatePreview(); forcedSweep = null;
        return Send();
    }
    private int? forcedSweep;

    private bool Send()
    {
        var preview = Preview; dragStart = null; curve = null;
        if (preview.Edit is not { } edit || !preview.IsValid) { session.Status = preview.Status; UpdatePreview(); return false; }
        foreach (var next in preview.FollowUps ?? []) followUps.Enqueue((next, Describe(next)));
        bool sent = session.Submit((authority, revision) => authority.BuildAsync(session.Lot.Lot.LotId, new(session.CharacterId, edit, revision)), Describe(edit));
        if (!sent) followUps.Clear();
        UpdatePreview(); return sent;
    }

    public bool CancelGesture() { if (dragStart is null && curve is null) return false; dragStart = null; curve = null; UpdatePreview(); return true; }

    public void Tick() => session.Tick();

    public static TileCell TileAt(float worldX, float worldZ) => new((int)MathF.Floor(worldX / BuildGrid.TileMeters), (int)MathF.Floor(worldZ / BuildGrid.TileMeters));

    /// <summary>Snaps to a 4 m wall corner when close, otherwise to the 0.5 m grid.</summary>
    public static (float X, float Z) Snap((float X, float Z) p)
    {
        float cx = MathF.Round(p.X / BuildGrid.TileMeters) * BuildGrid.TileMeters, cz = MathF.Round(p.Z / BuildGrid.TileMeters) * BuildGrid.TileMeters;
        if ((cx - p.X) * (cx - p.X) + (cz - p.Z) * (cz - p.Z) <= CornerSnapMeters * CornerSnapMeters) return (cx, cz);
        return (MathF.Round(p.X / FineSnapMeters) * FineSnapMeters, MathF.Round(p.Z / FineSnapMeters) * FineSnapMeters);
    }

    private void UpdatePreview()
    {
        var plan = session.Lot.ToPlan();
        IReadOnlyList<BuildEdit>? extra = null;
        BuildEdit? edit = Tool switch
        {
            LotBuildTool.Floor => Rectangle(BuildEditKind.AddFloor) is { } floor ? floor with { Finish = Selection.FloorFinish, Paint = Selection.FloorPaint ?? BuildingEditor.NoPaint } : null,
            LotBuildTool.Roof => Rectangle(BuildEditKind.AddRoof) is { } roof
                ? roof with { RoofId = "roof-" + Guid.NewGuid().ToString("N")[..12], Shape = Selection.RoofShape, Finish = Selection.RoofFinish } : null,
            LotBuildTool.Wall => WallEdit() is { } wall ? wall with { Style = Selection.WallStyle, Opening = Selection.WallKind } : null,
            LotBuildTool.Room => RoomEdits(out extra),
            LotBuildTool.CurvedWall => CurvedEdit(),
            LotBuildTool.Fitting => FittingEdit(plan),
            LotBuildTool.Finish => StyleEdit(plan),
            LotBuildTool.PaintWall => PaintEdits(plan, out extra),
            _ => DeleteEdit(plan),
        };
        if (edit is null)
        {
            Preview = LotBuildPreview.None(Tool, Level, Tool switch
            {
                LotBuildTool.Delete => "Nothing to remove here.",
                LotBuildTool.Fitting or LotBuildTool.Finish or LotBuildTool.PaintWall => "Point at a wall.",
                LotBuildTool.CurvedWall when dragStart is not null => "Drag to the point halfway round the curve.",
                _ => "Point at your lot.",
            });
            return;
        }
        // Check the whole sequence against the authority's rules, as the service will apply it.
        var verdict = BuildingEditor.Apply(plan, session.CharacterId, edit);
        foreach (var next in extra ?? [])
            if (verdict.Accepted) verdict = BuildingEditor.Apply(verdict.Plan, session.CharacterId, next);
        Preview = new(Tool, Level, edit, verdict.Accepted, verdict.Accepted ? Capitalize(Describe(edit)) + "." : verdict.Message, extra);
    }

    private BuildEdit? Rectangle(BuildEditKind kind)
    {
        var a = TileAt(pointer.X, pointer.Z); var b = dragStart is { } s ? TileAt(s.X, s.Z) : a;
        return new(kind, Level, Math.Min(a.X, b.X), Math.Min(a.Z, b.Z), Math.Max(a.X, b.X), Math.Max(a.Z, b.Z));
    }

    /// <summary>The Sims room tool: the rectangle's floor, then walls around its edge.</summary>
    private BuildEdit? RoomEdits(out IReadOnlyList<BuildEdit>? extra)
    {
        extra = null;
        if (Rectangle(BuildEditKind.AddFloor) is not { } floor) return null;
        var walls = new List<WallSegment>();
        for (int x = floor.MinimumTileX; x <= floor.MaximumTileX; x++) { walls.Add(new(x, floor.MinimumTileZ, WallAxis.AlongX, Level)); walls.Add(new(x, floor.MaximumTileZ + 1, WallAxis.AlongX, Level)); }
        for (int z = floor.MinimumTileZ; z <= floor.MaximumTileZ; z++) { walls.Add(new(floor.MinimumTileX, z, WallAxis.AlongZ, Level)); walls.Add(new(floor.MaximumTileX + 1, z, WallAxis.AlongZ, Level)); }
        extra = [new(BuildEditKind.AddWall, Level, Walls: walls, Style: Selection.WallStyle, Opening: ModuleCatalog.NoOpening)];
        return floor with { Finish = Selection.FloorFinish, Paint = Selection.FloorPaint ?? BuildingEditor.NoPaint };
    }

    /// <summary>Life Beyond's wall drag: grid point to grid point along the dominant axis; a click takes the nearest tile edge.</summary>
    private BuildEdit? WallEdit()
    {
        var walls = WallRun();
        return walls.Count is 0 or > BuildingEditor.MaximumTilesPerEdit ? null : new(BuildEditKind.AddWall, Level, Walls: walls);
    }

    private List<WallSegment> WallRun()
    {
        var start = dragStart ?? pointer;
        var first = GridPoint(start); var last = GridPoint(pointer);
        var walls = new List<WallSegment>();
        int dx = last.X - first.X, dz = last.Z - first.Z;
        if (dx == 0 && dz == 0) walls.Add(NearestEdge(dragStart is null ? pointer : start));
        else if (Math.Abs(dx) >= Math.Abs(dz)) for (int x = Math.Min(first.X, last.X); x < Math.Max(first.X, last.X); x++) walls.Add(new(x, first.Z, WallAxis.AlongX, Level));
        else for (int z = Math.Min(first.Z, last.Z); z < Math.Max(first.Z, last.Z); z++) walls.Add(new(first.X, z, WallAxis.AlongZ, Level));
        return walls;
    }

    /// <summary>The existing walls a click or drag targets: the face the mouse ray hits, or the run along the grid.</summary>
    private List<WallSegment> TargetWalls(BuildingPlan plan)
    {
        if (dragStart is { } s && GridPoint(s) != GridPoint(pointer)) return WallRun().Where(plan.Walls.Contains).ToList();
        if (HitWall(plan) is { Straight: { } hit }) return [hit];
        var edge = NearestEdge(pointer);
        return plan.Walls.Contains(edge) && DistanceToEdge(pointer, edge) < .9f ? [edge] : [];
    }

    private BuildEdit? FittingEdit(BuildingPlan plan)
    {
        if (dragStart is null && HitWall(plan) is { Curved: { } c }) return CurvedFitting(c.Wall, c.Relative);
        var walls = TargetWalls(plan);
        return walls.Count == 0 ? null : new(BuildEditKind.SetWallFinish, Level, Walls: walls, Opening: Selection.Fitting);
    }

    /// <summary>On a curved wall the fitting goes where you click; clicking an existing opening removes it.</summary>
    private BuildEdit? CurvedFitting(CurvedWall wall, double rel)
    {
        var openings = wall.HostedOpenings.ToList();
        int existing = openings.FindIndex(o => { var r = CurvedWalls.Range(wall, o); return rel >= r.From && rel <= r.To; });
        if (existing >= 0) { openings.RemoveAt(existing); return new(BuildEditKind.SetCurvedWall, Level, Curve: wall with { Openings = openings.ToArray() }); }
        if (!ModuleCatalog.CurvedOpenings.Any(o => o.Id == Selection.Fitting)) return null;
        int centre = (int)Math.Round(rel * 180 / Math.PI * 1000);
        double half = ModuleCatalog.Opening(Selection.Fitting)!.WidthMeters / 2 / wall.Radius + CurvedWalls.EndMarginMeters / wall.Radius;
        if (wall.IsFullCircle && (rel < half || rel > wall.SweepRadians - half))
        {
            // A ring's seam is arbitrary: turn it half way round so the new opening sits clear of it.
            const int halfTurn = CurvedWall.FullCircleMilliDegrees / 2;
            wall = wall with { StartMilliDegrees = (wall.StartMilliDegrees + halfTurn) % CurvedWall.FullCircleMilliDegrees };
            openings = openings.Select(o => o with { CenterMilliDegrees = (o.CenterMilliDegrees + halfTurn) % CurvedWall.FullCircleMilliDegrees }).ToList();
            centre = (centre + halfTurn) % CurvedWall.FullCircleMilliDegrees;
        }
        openings.Add(new(Selection.Fitting, centre));
        return new(BuildEditKind.SetCurvedWall, Level, Curve: wall with { Openings = openings.ToArray() });
    }

    private BuildEdit? StyleEdit(BuildingPlan plan)
    {
        if (dragStart is null && HitWall(plan) is { Curved: { } c }) return new(BuildEditKind.SetCurvedWall, Level, Curve: c.Wall with { Style = Selection.WallStyle });
        var walls = TargetWalls(plan);
        return walls.Count == 0 ? null : new(BuildEditKind.SetWallFinish, Level, Walls: walls, Style: Selection.WallStyle);
    }

    /// <summary>
    /// Paints the face the mouse is on. Each straight wall stores an inside (room side) and outside paint; which one a face is
    /// depends on the wall's facing, so a run or a whole room becomes at most two edits.
    /// </summary>
    private BuildEdit? PaintEdits(BuildingPlan plan, out IReadOnlyList<BuildEdit>? extra)
    {
        extra = null;
        string paint = Selection.WallPaint;
        var hit = HitWall(plan);
        if (dragStart is null && hit is { Curved: { } c })
        {
            bool outerFace = c.OuterFace;
            bool outside = outerFace == c.Wall.ExteriorOutside;
            return new(BuildEditKind.SetCurvedWall, Level, Curve: outside ? c.Wall with { OutsidePaint = paint == BuildingEditor.NoPaint ? null : paint }
                : c.Wall with { InsidePaint = paint == BuildingEditor.NoPaint ? null : paint });
        }
        var floors = plan.Floors.ToHashSet();
        var faces = new List<(WallSegment Wall, bool Outside)>();
        if (PaintWholeRoom && hit is { Straight: { } wall, Toward: { } toward })
        {
            // The room on the side you are looking at: every wall around it, painted on the room's side.
            var room = RoomFinder.Find(plan, Level).FirstOrDefault(r => r.Tiles.Contains(toward));
            if (room is null) { session.Status = "That side is not an enclosed room; painting one wall."; faces.Add((wall, IsOutside(wall, toward, floors))); }
            else
            {
                var tiles = room.Tiles.ToHashSet();
                foreach (var w in plan.Walls.Where(w => w.Level == Level))
                {
                    var (a, b) = w.Sides;
                    if (tiles.Contains(a) != tiles.Contains(b)) faces.Add((w, IsOutside(w, tiles.Contains(a) ? a : b, floors)));
                }
            }
        }
        else if (hit is { Straight: { } single, Toward: { } side } && dragStart is null) faces.Add((single, IsOutside(single, side, floors)));
        else
        {
            var walls = TargetWalls(plan);
            var near = dragStart ?? pointer;
            foreach (var w in walls) faces.Add((w, IsOutside(w, SideTile(w, near), floors)));
        }
        if (faces.Count == 0) return null;
        var groups = faces.GroupBy(f => f.Outside).Select(g => new BuildEdit(BuildEditKind.SetWallFinish, Level, Walls: g.Select(f => f.Wall).ToList(),
            InsidePaint: g.Key ? null : paint, OutsidePaint: g.Key ? paint : null)).ToList();
        extra = groups.Skip(1).ToList();
        return groups[0];
    }

    /// <summary>Whether looking from <paramref name="toward"/>'s tile you see the wall's outside (its module front) face.</summary>
    private static bool IsOutside(WallSegment w, TileCell toward, IReadOnlySet<FloorCell> floors)
    {
        int turns = LotGeometry.Facing(w, floors).Turns;
        bool frontIsAfter = turns is 0 or 1;
        return (toward == w.Sides.After) == frontIsAfter;
    }

    private static TileCell SideTile(WallSegment w, (float X, float Z) p) => w.Axis == WallAxis.AlongX
        ? (p.Z >= w.EdgeZ * BuildGrid.TileMeters ? w.Sides.After : w.Sides.Before)
        : (p.X >= w.EdgeX * BuildGrid.TileMeters ? w.Sides.After : w.Sides.Before);

    private sealed record WallHit(WallSegment? Straight, TileCell? Toward, (CurvedWall Wall, double Relative, bool OuterFace)? Curved, float Distance);

    /// <summary>The first wall the mouse ray hits on this level (straight or curved), and which side it was hit from.</summary>
    private WallHit? HitWall(BuildingPlan plan)
    {
        if (ray is not { } r)
        {
            // No ray (tests, crosshair aiming): the nearest edge or curve to the build-plane point.
            if (CurvedAt(plan, pointer) is { } c)
            {
                float d = MathF.Sqrt((pointer.X - c.Wall.CenterX) * (pointer.X - c.Wall.CenterX) + (pointer.Z - c.Wall.CenterZ) * (pointer.Z - c.Wall.CenterZ));
                return new(null, null, (c.Wall, c.Relative, d >= c.Wall.Radius), 0);
            }
            var edge = NearestEdge(pointer);
            return plan.Walls.Contains(edge) && DistanceToEdge(pointer, edge) < .9f ? new(edge, SideTile(edge, pointer), null, 0) : null;
        }
        WallHit? best = null;
        float levelBase = rayLevelBase;
        foreach (var w in plan.Walls.Where(w => w.Level == Level))
        {
            bool alongX = w.Axis == WallAxis.AlongX;
            float x0 = alongX ? w.EdgeX * BuildGrid.TileMeters : w.EdgeX * BuildGrid.TileMeters - LotGeometry.WallThickness / 2;
            float x1 = alongX ? (w.EdgeX + 1) * BuildGrid.TileMeters : w.EdgeX * BuildGrid.TileMeters + LotGeometry.WallThickness / 2;
            float z0 = alongX ? w.EdgeZ * BuildGrid.TileMeters - LotGeometry.WallThickness / 2 : w.EdgeZ * BuildGrid.TileMeters;
            float z1 = alongX ? w.EdgeZ * BuildGrid.TileMeters + LotGeometry.WallThickness / 2 : (w.EdgeZ + 1) * BuildGrid.TileMeters;
            var finish = plan.WallFinishes.FirstOrDefault(f => f.Wall == w);
            float top = levelBase + (ModuleCatalog.Opening(finish?.Opening)?.Partial == true ? 1.25f : LotGeometry.StoryMeters);
            if (RayBox(r.Origin, r.Direction, new(x0, levelBase, z0), new(x1, top, z1)) is { } t && (best is null || t < best.Distance))
            {
                var at = r.Origin + r.Direction * t;
                var toward = alongX ? (at.Z >= w.EdgeZ * BuildGrid.TileMeters ? w.Sides.After : w.Sides.Before) : (at.X >= w.EdgeX * BuildGrid.TileMeters ? w.Sides.After : w.Sides.Before);
                best = new(w, toward, null, t);
            }
        }
        foreach (var wall in plan.CurvedWalls.Where(w => w.Level == Level))
            foreach (var (radius, outer) in new[] { (wall.Radius + CurvedWalls.Thickness / 2, true), (wall.Radius - CurvedWalls.Thickness / 2, false) })
                foreach (float t in RayCylinder(r.Origin, r.Direction, wall.CenterX, wall.CenterZ, radius))
                {
                    if (best is not null && t >= best.Distance) continue;
                    var at = r.Origin + r.Direction * t;
                    if (at.Y < levelBase || at.Y > levelBase + wall.TopMeters + .05f) continue;
                    double rel = Math.Atan2(at.Z - wall.CenterZ, at.X - wall.CenterX) - wall.StartRadians;
                    rel = (rel % (2 * Math.PI) + 2 * Math.PI) % (2 * Math.PI);
                    if (rel > wall.SweepRadians) continue;
                    best = new(null, null, (wall, rel, outer), t);
                }
        return best;
    }

    private static float? RayBox(Vector3 o, Vector3 d, Vector3 min, Vector3 max)
    {
        float t0 = 0, t1 = 1e6f;
        for (int i = 0; i < 3; i++)
        {
            float oi = i == 0 ? o.X : i == 1 ? o.Y : o.Z, di = i == 0 ? d.X : i == 1 ? d.Y : d.Z;
            float lo = i == 0 ? min.X : i == 1 ? min.Y : min.Z, hi = i == 0 ? max.X : i == 1 ? max.Y : max.Z;
            if (MathF.Abs(di) < 1e-8f) { if (oi < lo || oi > hi) return null; continue; }
            float a = (lo - oi) / di, b = (hi - oi) / di;
            t0 = MathF.Max(t0, MathF.Min(a, b)); t1 = MathF.Min(t1, MathF.Max(a, b));
            if (t0 > t1) return null;
        }
        return t0;
    }

    private static IEnumerable<float> RayCylinder(Vector3 o, Vector3 d, float cx, float cz, float radius)
    {
        float ox = o.X - cx, oz = o.Z - cz, a = d.X * d.X + d.Z * d.Z;
        if (a < 1e-10f) yield break;
        float b = 2 * (ox * d.X + oz * d.Z), c = ox * ox + oz * oz - radius * radius, disc = b * b - 4 * a * c;
        if (disc < 0) yield break;
        float s = MathF.Sqrt(disc);
        foreach (float t in new[] { (-b - s) / (2 * a), (-b + s) / (2 * a) }) if (t > 0) yield return t;
    }

    /// <summary>
    /// The curved wall being drawn. Before the half point is set, a half circle from the corner to the mouse; after, the
    /// circle is fixed and the sweep follows the mouse round from the half point (half circle up to a full ring).
    /// </summary>
    private BuildEdit? CurvedEdit()
    {
        float cx, cz, radius; double start; int sweep;
        if (curve is { } c)
        {
            (cx, cz, radius) = (c.CenterX, c.CenterZ, c.Radius);
            double phi = Math.Atan2(pointer.Z - cz, pointer.X - cx);
            double ccw = ((phi - c.StartAngle) % (2 * Math.PI) + 2 * Math.PI) % (2 * Math.PI);
            bool counter = ccw >= Math.PI;                            // the direction that has already passed the half point
            double degrees = (counter ? ccw : 2 * Math.PI - ccw) * 180 / Math.PI;
            degrees = degrees >= 350 ? 360 : degrees <= 190 ? 180 : Math.Round(degrees / 5) * 5;
            if (forcedSweep is { } forced) degrees = forced / 1000.0;
            sweep = (int)Math.Round(degrees * 1000);
            start = counter || sweep == CurvedWall.FullCircleMilliDegrees ? c.StartAngle : c.StartAngle - degrees * Math.PI / 180;
        }
        else if (dragStart is { } s)
        {
            var half = Snap(pointer);
            float dx = half.X - s.X, dz = half.Z - s.Z, diameter = MathF.Sqrt(dx * dx + dz * dz);
            if (diameter < .5f) return null;
            (cx, cz, radius) = ((s.X + half.X) / 2, (s.Z + half.Z) / 2, diameter / 2);
            start = Math.Atan2(s.Z - cz, s.X - cx); sweep = 180_000;
        }
        else return null;
        int startMilli = (int)Math.Round((start * 180 / Math.PI % 360 + 360) % 360 * 1000) % CurvedWall.FullCircleMilliDegrees;
        var wall = new CurvedWall("curve-" + Guid.NewGuid().ToString("N")[..12], Level, (int)MathF.Round(cx * 1000), (int)MathF.Round(cz * 1000),
            Math.Clamp((int)MathF.Round(radius * 1000), 1, CurvedWalls.MaximumRadiusMillimeters * 2), startMilli, sweep, Selection.WallStyle, Selection.CurvedProfile);
        return new(BuildEditKind.AddCurvedWall, Level, Curve: wall);
    }

    /// <summary>The curved wall on this level within reach of the point, and the point's angle along it (relative radians).</summary>
    public (CurvedWall Wall, double Relative)? CurvedAt(BuildingPlan plan, (float X, float Z) p)
    {
        foreach (var wall in plan.CurvedWalls.Where(w => w.Level == Level))
        {
            float dx = p.X - wall.CenterX, dz = p.Z - wall.CenterZ;
            if (MathF.Abs(MathF.Sqrt(dx * dx + dz * dz) - wall.Radius) > .6f) continue;
            double rel = Math.Atan2(dz, dx) - wall.StartRadians;
            rel = (rel % (2 * Math.PI) + 2 * Math.PI) % (2 * Math.PI);
            if (rel <= wall.SweepRadians) return (wall, rel);
        }
        return null;
    }

    private BuildEdit? DeleteEdit(BuildingPlan plan)
    {
        if (dragStart is { } s && TileAt(s.X, s.Z) != TileAt(pointer.X, pointer.Z)) return Rectangle(BuildEditKind.RemoveFloor);
        var hit = HitWall(plan);
        if (hit is { Curved: { } c }) return new(BuildEditKind.RemoveCurvedWall, Level, TargetId: c.Wall.WallId);
        if (hit is { Straight: { } w }) return new(BuildEditKind.RemoveWall, Level, Walls: [w]);
        var tile = TileAt(pointer.X, pointer.Z);
        if (plan.Roofs.FirstOrDefault(r => r.Level == Level && tile.X >= r.MinimumTileX && tile.X <= r.MaximumTileX && tile.Z >= r.MinimumTileZ && tile.Z <= r.MaximumTileZ) is { } roof)
            return new(BuildEditKind.RemoveRoof, Level, RoofId: roof.RoofId);
        if (plan.Floors.Contains(new(tile.X, tile.Z, Level))) return new(BuildEditKind.RemoveFloor, Level, tile.X, tile.Z, tile.X, tile.Z);
        return null;
    }

    private static TileCell GridPoint((float X, float Z) p) => new((int)MathF.Round(p.X / BuildGrid.TileMeters), (int)MathF.Round(p.Z / BuildGrid.TileMeters));

    private WallSegment NearestEdge((float X, float Z) p)
    {
        var tile = TileAt(p.X, p.Z);
        float x = p.X / BuildGrid.TileMeters - tile.X, z = p.Z / BuildGrid.TileMeters - tile.Z;
        float north = 1 - z, east = 1 - x, south = z, west = x, nearest = MathF.Min(MathF.Min(north, east), MathF.Min(south, west));
        if (nearest == north) return new(tile.X, tile.Z + 1, WallAxis.AlongX, Level);
        if (nearest == east) return new(tile.X + 1, tile.Z, WallAxis.AlongZ, Level);
        if (nearest == west) return new(tile.X, tile.Z, WallAxis.AlongZ, Level);
        return new(tile.X, tile.Z, WallAxis.AlongX, Level);
    }

    private static float DistanceToEdge((float X, float Z) p, WallSegment w) => w.Axis == WallAxis.AlongX
        ? MathF.Abs(p.Z - w.EdgeZ * BuildGrid.TileMeters) : MathF.Abs(p.X - w.EdgeX * BuildGrid.TileMeters);

    public static string Describe(BuildEdit edit)
    {
        int tiles = (Math.Abs(edit.MaximumTileX - edit.MinimumTileX) + 1) * (Math.Abs(edit.MaximumTileZ - edit.MinimumTileZ) + 1);
        string Paint(string? id) => id is null or BuildingEditor.NoPaint ? "its own colour" : ModuleCatalog.Paint(id)?.Name.ToLowerInvariant() ?? id;
        return edit.Kind switch
        {
            BuildEditKind.AddFloor => (tiles == 1 ? "lay 1 floor tile" : $"lay {tiles} floor tiles")
                + (edit.Finish is null ? "" : ": " + ModuleCatalog.FloorFinishes.FirstOrDefault(f => f.Id == edit.Finish).Name?.ToLowerInvariant()
                    + (edit.Paint is { } p && p != BuildingEditor.NoPaint ? ", " + Paint(p) : "")),
            BuildEditKind.RemoveFloor => tiles == 1 ? "remove 1 floor tile" : $"remove {tiles} floor tiles",
            BuildEditKind.AddWall => (edit.Walls!.Count == 1 ? "build 1 " : $"build {edit.Walls!.Count} ") + (edit.Opening is "half" ? "half wall" : edit.Opening is "railing" ? "railing" : "wall")
                + (edit.Walls.Count == 1 || edit.Opening is "half" or "railing" ? "" : "s"),
            BuildEditKind.RemoveWall => "remove the wall",
            BuildEditKind.AddRoof => (tiles == 1 ? "roof 1 tile" : $"roof {tiles} tiles") + (edit.Shape is { } shape and not "flat" ? $" ({shape})" : ""),
            BuildEditKind.SetWallFinish when edit.InsidePaint is not null || edit.OutsidePaint is not null =>
                $"paint {(edit.Walls!.Count == 1 ? "the wall" : edit.Walls.Count + " walls")} {Paint(edit.InsidePaint ?? edit.OutsidePaint)}",
            BuildEditKind.SetWallFinish when edit.Opening is not null => $"fit {ModuleCatalog.Opening(edit.Opening)?.Name.ToLowerInvariant()} into {(edit.Walls!.Count == 1 ? "the wall" : edit.Walls.Count + " walls")}",
            BuildEditKind.SetWallFinish => $"change {(edit.Walls!.Count == 1 ? "the wall" : edit.Walls.Count + " walls")} to {ModuleCatalog.Style(edit.Style)?.Name.ToLowerInvariant()}",
            BuildEditKind.AddCurvedWall => $"build a {ProfileName(edit.Curve!.Profile).ToLowerInvariant()} {(edit.Curve.IsFullCircle ? "full circle" : edit.Curve.SweepMilliDegrees == 180_000 ? "half circle" : Math.Round(edit.Curve.SweepMilliDegrees / 1000.0) + "° curve")}, radius {edit.Curve.Radius:0.##} m",
            BuildEditKind.SetCurvedWall => "change the curved wall",
            BuildEditKind.RemoveCurvedWall => "remove the curved wall",
            _ => "remove the roof",
        };
    }

    public static string ProfileName(string profile) => profile switch { "glazed" => "Glazed wall", "half" => "Half wall", "counter" => "Counter", _ => "Full wall" };

    private static string Capitalize(string text) => char.ToUpperInvariant(text[0]) + text[1..];
}
