namespace LifeCare.Construction;

/// <summary>Build mode edits only the shell. Furniture is never a build tool (Tiny Clinic rule): it arrives as a package.</summary>
public enum LotBuildTool { Floor, Wall, Roof, Delete, CurvedWall, Finish }

/// <summary>What the next build edit will use: finishes for straight and curved walls, floors and roofs, and the curve's shape.</summary>
public sealed record LotBuildSelection(string WallStyle, string Opening, string FloorFinish, string RoofShape, string RoofFinish,
    string CurvedProfile, int CurvedSweepDegrees)
{
    public static LotBuildSelection Default { get; } = new(ModuleCatalog.DefaultExteriorStyle, ModuleCatalog.NoOpening, ModuleCatalog.DefaultFloorFinish,
        ModuleCatalog.DefaultRoofShape, ModuleCatalog.DefaultRoofFinish, "full", 360);
    public static readonly IReadOnlyList<int> Sweeps = [360, 270, 180, 90];
}

/// <summary>The build ghost: the edit the gesture would send, and the authority's own rules' verdict on it.</summary>
public sealed record LotBuildPreview(LotBuildTool Tool, int Level, BuildEdit? Edit, bool IsValid, string Status)
{
    public static LotBuildPreview None(LotBuildTool tool, int level, string status) => new(tool, level, null, false, status);
}

/// <summary>
/// Client build mode for one lot, after Tiny Clinic's ClinicBuildController with Life Beyond's grid gestures:
/// floors and roofs are dragged as tile rectangles, walls are dragged along 4 m grid lines (a click takes the nearest
/// tile edge), and Delete removes the wall, roof or floor under the pointer (a drag clears a floor rectangle).
/// The ghost is checked with the same <see cref="BuildingEditor"/> rules the Construction service enforces, and edits
/// go one at a time through the <see cref="LotSession"/>. World X/Z map to the global 4 m tile grid.
/// </summary>
public sealed class LotBuildController
{
    public const int MaximumLevels = 8;
    private readonly LotSession session;
    private (float X, float Z) pointer;
    private (float X, float Z)? dragStart;

    public LotBuildController(LotSession session)
    {
        this.session = session ?? throw new ArgumentNullException(nameof(session));
        Preview = LotBuildPreview.None(Tool, Level, "Choose a tool.");
        session.LotChanged += _ => UpdatePreview();
    }

    public LotSession Session => session;
    public LotBuildTool Tool { get; private set; } = LotBuildTool.Floor;
    public LotBuildSelection Selection { get; private set; } = LotBuildSelection.Default;
    public int Level { get; private set; }
    public LotBuildPreview Preview { get; private set; }
    public bool IsDragging => dragStart is not null;
    public string Status => session.Status;

    public void SelectTool(LotBuildTool tool)
    {
        Tool = tool; dragStart = null;
        session.Status = "Selected " + tool switch
        {
            LotBuildTool.Floor => "floor", LotBuildTool.Wall => "wall", LotBuildTool.Roof => "roof",
            LotBuildTool.CurvedWall => "curved wall: press at the centre, drag out the radius",
            LotBuildTool.Finish => "finish: click a wall to apply the finish and opening", _ => "delete",
        } + ".";
        UpdatePreview();
    }

    /// <summary>Steps the tool's primary option: wall finish (walls, curved walls, finish tool), floor finish (floor) or roof finish (roof).</summary>
    public string CyclePrimary(int delta = 1)
    {
        var s = Selection;
        Selection = Tool switch
        {
            LotBuildTool.Floor => s with { FloorFinish = Next(ModuleCatalog.FloorFinishes.Select(f => f.Id).ToList(), s.FloorFinish, delta) },
            LotBuildTool.Roof => s with { RoofFinish = Next(ModuleCatalog.RoofFinishes.Select(f => f.Id).ToList(), s.RoofFinish, delta) },
            _ => s with { WallStyle = Next(ModuleCatalog.WallStyles.Select(w => w.Id).ToList(), s.WallStyle, delta) },
        };
        return Announce();
    }

    /// <summary>
    /// Steps the tool's secondary option: the opening (walls, finish tool), the roof shape (roof) or the curved wall's
    /// profile (full, glazed, half wall, counter).
    /// </summary>
    public string CycleSecondary(int delta = 1)
    {
        var s = Selection;
        Selection = Tool switch
        {
            LotBuildTool.Roof => s with { RoofShape = Next(ModuleCatalog.RoofShapes.Select(r => r.Id).ToList(), s.RoofShape, delta) },
            LotBuildTool.CurvedWall => s with { CurvedProfile = Next(CurvedWalls.Profiles.ToList(), s.CurvedProfile, delta) },
            _ => s with { Opening = Next(ModuleCatalog.Openings.Select(o => o.Id).ToList(), s.Opening, delta) },
        };
        return Announce();
    }

    /// <summary>Steps the curved wall's sweep: full ring, 270° horseshoe (a nurses' station with its staff gap), half and quarter circle.</summary>
    public string CycleSweep(int delta = 1)
    {
        var sweeps = LotBuildSelection.Sweeps.ToList();
        int i = (sweeps.IndexOf(Selection.CurvedSweepDegrees) + delta + sweeps.Count) % sweeps.Count;
        Selection = Selection with { CurvedSweepDegrees = sweeps[i] };
        return Announce();
    }

    /// <summary>The current options, for the HUD.</summary>
    public string SelectionText => Tool switch
    {
        LotBuildTool.Floor => ModuleCatalog.FloorFinishes.First(f => f.Id == Selection.FloorFinish).Name,
        LotBuildTool.Roof => $"{ModuleCatalog.RoofShapes.First(r => r.Id == Selection.RoofShape).Name} · {ModuleCatalog.RoofFinishes.First(r => r.Id == Selection.RoofFinish).Name}",
        LotBuildTool.CurvedWall => $"{ProfileName(Selection.CurvedProfile)} · {(Selection.CurvedSweepDegrees == 360 ? "full circle" : Selection.CurvedSweepDegrees + "°")} · {ModuleCatalog.Style(Selection.WallStyle)!.Name}",
        LotBuildTool.Wall or LotBuildTool.Finish => $"{ModuleCatalog.Style(Selection.WallStyle)!.Name} · {ModuleCatalog.Opening(Selection.Opening)!.Name}",
        _ => "",
    };

    public static string ProfileName(string profile) => profile switch { "glazed" => "Glazed wall", "half" => "Half wall", "counter" => "Counter", _ => "Full wall" };

    private string Announce() { session.Status = SelectionText + "."; UpdatePreview(); return SelectionText; }

    private static string Next(IReadOnlyList<string> values, string current, int delta)
    {
        int i = values.ToList().IndexOf(current);
        return values[((i < 0 ? 0 : i) + delta % values.Count + values.Count) % values.Count];
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
        Level = Math.Clamp(Level + delta, 0, MaximumLevels - 1); dragStart = null;
        session.Status = Level == 0 ? "Ground floor." : $"Floor {Level + 1}.";
        UpdatePreview();
    }

    /// <summary>The point on the build plane under the cursor, in world metres.</summary>
    public void SetPointer(float worldX, float worldZ)
    {
        if (!float.IsFinite(worldX) || !float.IsFinite(worldZ)) return;
        pointer = (worldX, worldZ); UpdatePreview();
    }

    public void BeginGesture()
    {
        if (session.HasPendingEdit) { session.Status = "Waiting for the lot authority to finish the previous edit."; return; }
        if (!session.IsOwner) { session.Status = "Only the lot's owner can build here."; return; }
        dragStart = pointer; UpdatePreview();
    }

    /// <summary>Ends the drag and sends its edit if the ghost is valid.</summary>
    public bool CompleteGesture()
    {
        if (dragStart is null) return false;
        UpdatePreview();
        var preview = Preview; dragStart = null;
        if (preview.Edit is not { } edit || !preview.IsValid) { session.Status = preview.Status; UpdatePreview(); return false; }
        bool sent = session.Submit((authority, revision) => authority.BuildAsync(session.Lot.Lot.LotId, new(session.CharacterId, edit, revision)), Describe(edit));
        UpdatePreview(); return sent;
    }

    public bool CancelGesture() { if (dragStart is null) return false; dragStart = null; UpdatePreview(); return true; }

    public void Tick() => session.Tick();

    public static TileCell TileAt(float worldX, float worldZ) => new((int)MathF.Floor(worldX / BuildGrid.TileMeters), (int)MathF.Floor(worldZ / BuildGrid.TileMeters));

    private void UpdatePreview()
    {
        var plan = session.Lot.ToPlan();
        BuildEdit? edit = Tool switch
        {
            LotBuildTool.Floor => Rectangle(BuildEditKind.AddFloor) is { } floor ? floor with { Finish = Selection.FloorFinish } : null,
            LotBuildTool.Roof => Rectangle(BuildEditKind.AddRoof) is { } roof
                ? roof with { RoofId = "roof-" + Guid.NewGuid().ToString("N")[..12], Shape = Selection.RoofShape, Finish = Selection.RoofFinish } : null,
            LotBuildTool.Wall => WallEdit() is { } wall ? wall with { Style = Selection.WallStyle, Opening = StraightOpening } : null,
            LotBuildTool.CurvedWall => CurvedEdit(),
            LotBuildTool.Finish => FinishEdit(plan),
            _ => DeleteEdit(plan),
        };
        if (edit is null) { Preview = LotBuildPreview.None(Tool, Level, Tool == LotBuildTool.Delete ? "Nothing to remove here." : "Point at your lot."); return; }
        var verdict = BuildingEditor.Apply(plan, session.CharacterId, edit);
        Preview = new(Tool, Level, edit, verdict.Accepted, verdict.Accepted ? Capitalize(Describe(edit)) + "." : verdict.Message);
    }

    private BuildEdit? Rectangle(BuildEditKind kind)
    {
        var a = TileAt(pointer.X, pointer.Z); var b = dragStart is { } s ? TileAt(s.X, s.Z) : a;
        return new(kind, Level, Math.Min(a.X, b.X), Math.Min(a.Z, b.Z), Math.Max(a.X, b.X), Math.Max(a.Z, b.Z));
    }

    /// <summary>Life Beyond's wall drag: grid point to grid point along the dominant axis; a click takes the nearest tile edge.</summary>
    private BuildEdit? WallEdit()
    {
        var start = dragStart ?? pointer;
        var first = GridPoint(start); var last = GridPoint(pointer);
        var walls = new List<WallSegment>();
        int dx = last.X - first.X, dz = last.Z - first.Z;
        if (dx == 0 && dz == 0) walls.Add(NearestEdge(dragStart is null ? pointer : start));
        else if (Math.Abs(dx) >= Math.Abs(dz)) for (int x = Math.Min(first.X, last.X); x < Math.Max(first.X, last.X); x++) walls.Add(new(x, first.Z, WallAxis.AlongX, Level));
        else for (int z = Math.Min(first.Z, last.Z); z < Math.Max(first.Z, last.Z); z++) walls.Add(new(first.X, z, WallAxis.AlongZ, Level));
        return walls.Count is 0 or > BuildingEditor.MaximumTilesPerEdit ? null : new(BuildEditKind.AddWall, Level, Walls: walls);
    }

    private string StraightOpening => Selection.Opening == "passage" ? ModuleCatalog.NoOpening : Selection.Opening;

    /// <summary>
    /// The curved wall gesture: press at the centre and drag out the radius. The radius snaps to 5 cm and the arc's middle
    /// points at the release point, so a 270° counter's staff gap faces away from where you drag.
    /// </summary>
    private BuildEdit? CurvedEdit()
    {
        if (dragStart is not { } centre) return null;
        float dx = pointer.X - centre.X, dz = pointer.Z - centre.Z, radius = MathF.Sqrt(dx * dx + dz * dz);
        if (radius < CurvedWalls.MinimumRadiusMillimeters / 1000f) return null;
        static int Snap(float metres, int quantum) => (int)MathF.Round(metres * 1000 / quantum) * quantum;
        int sweep = Selection.CurvedSweepDegrees * 1000;
        int middle = (int)MathF.Round(MathF.Atan2(dz, dx) * 180 / MathF.PI);
        int start = sweep == CurvedWall.FullCircleMilliDegrees ? (middle % 360 + 360) % 360 * 1000 : ((middle * 1000 - sweep / 2) % 360_000 + 360_000) % 360_000;
        var curve = new CurvedWall("curve-" + Guid.NewGuid().ToString("N")[..12], Level, Snap(centre.X, CurvedWalls.PositionQuantumMillimeters),
            Snap(centre.Z, CurvedWalls.PositionQuantumMillimeters),
            Math.Clamp(Snap(radius, CurvedWalls.PositionQuantumMillimeters), CurvedWalls.MinimumRadiusMillimeters, CurvedWalls.MaximumRadiusMillimeters),
            start / 1000 * 1000, sweep, Selection.WallStyle, Selection.CurvedProfile);
        return new(BuildEditKind.AddCurvedWall, Level, Curve: curve);
    }

    /// <summary>
    /// The finish tool. On a straight wall a click applies the selected finish and opening. On a curved wall it restyles
    /// the wall, or with a curved-wall opening selected, adds that opening where you clicked (or removes the one you clicked).
    /// </summary>
    private BuildEdit? FinishEdit(BuildingPlan plan)
    {
        if (CurvedAt(plan, pointer) is { } hit)
        {
            var (wall, rel) = hit;
            var openings = wall.HostedOpenings.ToList();
            if (Selection.Opening == ModuleCatalog.NoOpening || !ModuleCatalog.CurvedOpenings.Any(o => o.Id == Selection.Opening))
                return new(BuildEditKind.SetCurvedWall, Level, Curve: wall with { Style = Selection.WallStyle });
            int existing = openings.FindIndex(o => { var r = CurvedWalls.Range(wall, o); return rel >= r.From && rel <= r.To; });
            if (existing >= 0) openings.RemoveAt(existing);
            else
            {
                int centre = (int)Math.Round(rel * 180 / Math.PI) * 1000;
                double half = ModuleCatalog.Opening(Selection.Opening)!.WidthMeters / 2 / wall.Radius + CurvedWalls.EndMarginMeters / wall.Radius;
                if (wall.IsFullCircle && (rel < half || rel > wall.SweepRadians - half))
                {
                    // A ring's seam is arbitrary: turn it half way round so the new opening sits clear of it.
                    const int halfTurn = CurvedWall.FullCircleMilliDegrees / 2;
                    wall = wall with { StartMilliDegrees = (wall.StartMilliDegrees + halfTurn) % CurvedWall.FullCircleMilliDegrees };
                    openings = openings.Select(o => o with { CenterMilliDegrees = (o.CenterMilliDegrees + halfTurn) % CurvedWall.FullCircleMilliDegrees }).ToList();
                    centre = (centre + halfTurn) % CurvedWall.FullCircleMilliDegrees;
                }
                openings.Add(new(Selection.Opening, centre));
            }
            return new(BuildEditKind.SetCurvedWall, Level, Curve: wall with { Openings = openings.ToArray() });
        }
        var edge = NearestEdge(pointer);
        if (!plan.Walls.Contains(edge) || DistanceToEdge(pointer, edge) > .9f) return null;
        return new(BuildEditKind.SetWallFinish, Level, Walls: [edge], Style: Selection.WallStyle, Opening: StraightOpening);
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
        if (dragStart is null && CurvedAt(plan, pointer) is { } curved) return new(BuildEditKind.RemoveCurvedWall, Level, TargetId: curved.Wall.WallId);
        if (dragStart is { } s && TileAt(s.X, s.Z) != TileAt(pointer.X, pointer.Z)) return Rectangle(BuildEditKind.RemoveFloor);
        var edge = NearestEdge(pointer);
        if (plan.Walls.Contains(edge) && DistanceToEdge(pointer, edge) < .9f) return new(BuildEditKind.RemoveWall, Level, Walls: [edge]);
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

    private static string Describe(BuildEdit edit)
    {
        int tiles = (Math.Abs(edit.MaximumTileX - edit.MinimumTileX) + 1) * (Math.Abs(edit.MaximumTileZ - edit.MinimumTileZ) + 1);
        return edit.Kind switch
        {
            BuildEditKind.AddFloor => tiles == 1 ? "lay 1 floor tile" : $"lay {tiles} floor tiles",
            BuildEditKind.RemoveFloor => tiles == 1 ? "remove 1 floor tile" : $"remove {tiles} floor tiles",
            BuildEditKind.AddWall => edit.Walls!.Count == 1 ? "build 1 wall" : $"build {edit.Walls!.Count} walls",
            BuildEditKind.RemoveWall => "remove the wall",
            BuildEditKind.AddRoof => tiles == 1 ? "roof 1 tile" : $"roof {tiles} tiles",
            BuildEditKind.SetWallFinish => $"finish the wall: {ModuleCatalog.Style(edit.Style)?.Name.ToLowerInvariant()}, {ModuleCatalog.Opening(edit.Opening)?.Name.ToLowerInvariant()}",
            BuildEditKind.AddCurvedWall => $"build a {ProfileName(edit.Curve!.Profile).ToLowerInvariant()} {(edit.Curve.IsFullCircle ? "ring" : edit.Curve.SweepMilliDegrees / 1000 + "° arc")}, radius {edit.Curve.Radius:0.##} m",
            BuildEditKind.SetCurvedWall => "change the curved wall",
            BuildEditKind.RemoveCurvedWall => "remove the curved wall",
            _ => "remove the roof",
        };
    }

    private static string Capitalize(string text) => char.ToUpperInvariant(text[0]) + text[1..];
}
