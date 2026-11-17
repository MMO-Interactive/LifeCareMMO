namespace LifeCare.Construction;

/// <summary>Build mode edits only the shell. Furniture is never a build tool (Tiny Clinic rule): it arrives as a package.</summary>
public enum LotBuildTool { Floor, Wall, Roof, Delete }

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
    public int Level { get; private set; }
    public LotBuildPreview Preview { get; private set; }
    public bool IsDragging => dragStart is not null;
    public string Status => session.Status;

    public void SelectTool(LotBuildTool tool)
    {
        Tool = tool; dragStart = null;
        session.Status = "Selected " + tool switch { LotBuildTool.Floor => "floor", LotBuildTool.Wall => "wall", LotBuildTool.Roof => "roof", _ => "delete" } + ".";
        UpdatePreview();
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
            LotBuildTool.Floor => Rectangle(BuildEditKind.AddFloor),
            LotBuildTool.Roof => Rectangle(BuildEditKind.AddRoof) is { } roof ? roof with { RoofId = "roof-" + Guid.NewGuid().ToString("N")[..12] } : null,
            LotBuildTool.Wall => WallEdit(),
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

    private BuildEdit? DeleteEdit(BuildingPlan plan)
    {
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
            _ => "remove the roof",
        };
    }

    private static string Capitalize(string text) => char.ToUpperInvariant(text[0]) + text[1..];
}
