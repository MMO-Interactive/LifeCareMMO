namespace LifeCare.Construction;

public enum LotPieceKind { Floor, Wall, Roof, Furnishing }

/// <summary>
/// One pack module drawn as part of a piece: offset from the piece's centre in world axes, turned by quarter turns about
/// Y (each turn maps local +X to -Z and the module's front, +Z, to +X).
/// </summary>
public sealed record ModulePart(string ModuleId, float OffsetX, float OffsetY, float OffsetZ, int QuarterTurns);

/// <summary>The arc a curved wall piece is cut from: relative radians along the wall, and the lot's base elevation.</summary>
public sealed record CurvedPieceSource(CurvedWall Wall, double From, double To, float BaseElevation);

/// <summary>
/// One box of a built lot in world metres (Y up). <see cref="PieceId"/> is stable for the same element across revisions,
/// so the district host's storm damage and every client agree on which piece broke. Wind and mass feed the host's
/// PhysX destruction: a piece is torn loose when the local wind reaches <see cref="ReleaseWindMph"/>.
/// The box is the piece's physics shape. What clients draw comes from <see cref="Modules"/> (pack GLBs) or
/// <see cref="Curve"/> (a generated curved mesh); a piece with neither is drawn as its box.
/// </summary>
public sealed record LotPiece(string PieceId, string LotId, LotPieceKind Kind, float CenterX, float CenterY, float CenterZ,
    float SizeX, float SizeY, float SizeZ, float YawDegrees, float ReleaseWindMph, float MassKilograms, string Material)
{
    public IReadOnlyList<ModulePart> Modules { get; init; } = [];
    public CurvedPieceSource? Curve { get; init; }
    /// <summary>Identifies the drawn shape, so equal pieces share one mesh.</summary>
    public string? MeshKey => Curve is { } c
        ? $"curve:{c.Wall.Fingerprint}:{c.From:R}:{c.To:R}:{c.BaseElevation:R}:{CenterX:R}:{CenterY:R}:{CenterZ:R}:{YawDegrees:R}"
        : Modules.Count == 0 ? null : string.Join('|', Modules.Select(m => $"{m.ModuleId}@{m.OffsetX:0.###},{m.OffsetY:0.###},{m.OffsetZ:0.###}q{m.QuarterTurns & 3}"));

    /// <summary>Pieces are equal when their box, release rules and drawn shape are, so geometry compares by value across revisions.</summary>
    public bool Equals(LotPiece? other) => other is not null && PieceId == other.PieceId && LotId == other.LotId && Kind == other.Kind
        && CenterX == other.CenterX && CenterY == other.CenterY && CenterZ == other.CenterZ && SizeX == other.SizeX && SizeY == other.SizeY && SizeZ == other.SizeZ
        && YawDegrees == other.YawDegrees && ReleaseWindMph.Equals(other.ReleaseWindMph) && MassKilograms == other.MassKilograms && Material == other.Material
        && MeshKey == other.MeshKey;

    public override int GetHashCode() => HashCode.Combine(PieceId, CenterX, CenterY, CenterZ, SizeX, SizeY, SizeZ, MeshKey);
}

/// <summary>Furnishing content the client and host both know: name and height for the box drawn and simulated.</summary>
public static class FurnishingCatalog
{
    public sealed record Entry(FurnishingDefinition Definition, float HeightMeters, string Material);
    public static readonly IReadOnlyList<Entry> Entries =
    [
        new(new("exam-bed", "Exam bed", 4, 2), .75f, "fabric"),
        new(new("chair", "Chair", 1, 1), .9f, "wood"),
        new(new("desk", "Desk", 3, 2), .76f, "wood"),
        new(new("cabinet", "Supply cabinet", 2, 1), 1.9f, "metal"),
        new(new("sofa", "Sofa", 4, 2), .85f, "fabric"),
        new(new("bed", "Bed", 4, 3), .6f, "fabric"),
        new(new("bookshelf", "Bookshelf", 2, 1), 1.8f, "wood"),
        new(new("floor-lamp", "Floor lamp", 1, 1), 1.6f, "lamp"),
    ];
    public static Entry? Find(string definitionId) => Entries.FirstOrDefault(e => e.Definition.DefinitionId == definitionId);
}

/// <summary>
/// Turns a lot document into pieces. Tile (x, z) covers world [4x, 4x+4] × [4z, 4z+4]. Straight walls, floors and roofs
/// are drawn with the rural module pack: a wall picks its module from its finish and turns so its exterior faces away
/// from the floored side; outer corners get corner trims; pitched roofs use gable or hip end and middle modules with
/// gable infill under each gable end. Curved walls become pieces of at most 2.4 m of arc, each drawn as a true curve.
/// </summary>
public static class LotGeometry
{
    public const float StoryMeters = 3.2f;
    public const float FloorThickness = .2f;
    public const float WallThickness = .2f;
    public const float RoofThickness = .25f;
    /// <summary>Height of a pitched roof piece's box, from the wall top up toward the ridge.</summary>
    public const float PitchedRoofBoxMeters = 1.2f;
    private const float WallCentreAboveBase = FloorThickness + (StoryMeters - FloorThickness) / 2;

    /// <summary>The ground height a lot is built on: the highest of its corners and centre, so no floor is buried.</summary>
    public static float BaseElevation(LotDeed lot, Func<float, float, float> heightAt)
    {
        float x0 = lot.MinimumTileX * BuildGrid.TileMeters, z0 = lot.MinimumTileZ * BuildGrid.TileMeters;
        float x1 = (lot.MaximumTileX + 1) * BuildGrid.TileMeters, z1 = (lot.MaximumTileZ + 1) * BuildGrid.TileMeters;
        return new[] { heightAt(x0, z0), heightAt(x1, z0), heightAt(x0, z1), heightAt(x1, z1), heightAt((x0 + x1) / 2, (z0 + z1) / 2) }.Max();
    }

    public static float LevelElevation(float baseElevation, int level) => baseElevation + level * StoryMeters;

    /// <summary>Turns (x, z) by quarter turns the way <see cref="ModulePart.QuarterTurns"/> does: (x, z) → (z, −x) per turn.</summary>
    public static (float X, float Z) Turn(float x, float z, int quarterTurns)
    {
        for (int i = 0; i < (quarterTurns & 3); i++) (x, z) = (z, -x);
        return (x, z);
    }

    public static IReadOnlyList<LotPiece> Build(LotDocument lot, float baseElevation)
    {
        string id = lot.Lot.LotId; var pieces = new List<LotPiece>();
        var floors = lot.Floors.ToHashSet();
        var floorFinish = (lot.FloorFinishes ?? []).ToDictionary(f => f.Floor, f => f.Finish);
        var wallFinish = (lot.WallFinishes ?? []).ToDictionary(f => f.Wall);
        foreach (var f in lot.Floors) pieces.Add(Floor(id, f, baseElevation, floorFinish.GetValueOrDefault(f, ModuleCatalog.DefaultFloorFinish)));

        var faces = lot.Walls.ToDictionary(w => w, w => Facing(w, floors));
        var corners = OuterCorners(lot.Walls, faces);
        foreach (var w in lot.Walls)
        {
            var face = faces[w];
            var finish = wallFinish.GetValueOrDefault(w);
            string style = StyleOf(finish, face.Interior), opening = finish?.Opening is { Length: > 0 } o ? o : ModuleCatalog.NoOpening;
            var piece = Wall(id, w, baseElevation, face.Interior);
            var parts = new List<ModulePart> { new(ModuleCatalog.WallModule(style, opening), 0, -WallCentreAboveBase, 0, face.Turns) };
            foreach (var (vx, vz, turns) in corners.GetValueOrDefault(w, []))
                parts.Add(new(ModuleCatalog.OuterCornerModule(style), vx * BuildGrid.TileMeters - piece.CenterX, -WallCentreAboveBase, vz * BuildGrid.TileMeters - piece.CenterZ, turns));
            pieces.Add(piece with { Modules = parts });
        }
        foreach (var r in lot.Roofs) pieces.AddRange(RoofPieces(id, r, baseElevation, lot.Walls, wallFinish, floors));
        foreach (var c in lot.CurvedWalls ?? []) pieces.AddRange(Curved(id, c, baseElevation));
        foreach (var f in lot.Furnishings) pieces.Add(Furnishing(id, f, baseElevation));
        return pieces;
    }

    private static string StyleOf(WallFinish? finish, bool interior) =>
        finish?.Style is { Length: > 0 } s ? s : interior ? ModuleCatalog.InteriorStyle : ModuleCatalog.DefaultExteriorStyle;

    /// <summary>Which way a wall's exterior faces: away from the floored side. Walls with floor on both sides are interior.</summary>
    public static (int Turns, bool Interior) Facing(WallSegment w, IReadOnlySet<FloorCell> floors)
    {
        var (a, b) = w.Sides;
        bool before = floors.Contains(new(a.X, a.Z, w.Level)), after = floors.Contains(new(b.X, b.Z, w.Level));
        bool alongX = w.Axis == WallAxis.AlongX;
        int toAfter = alongX ? 0 : 1, toBefore = alongX ? 2 : 3;
        return (before && !after ? toAfter : !before && after ? toBefore : toAfter, before && after);
    }

    /// <summary>Outer corners: a grid vertex where one exterior wall along X meets one along Z, both facing out of the same quadrant.</summary>
    private static Dictionary<WallSegment, List<(int X, int Z, int Turns)>> OuterCorners(IEnumerable<WallSegment> walls, Dictionary<WallSegment, (int Turns, bool Interior)> faces)
    {
        var at = new Dictionary<(int X, int Z, int Level), List<WallSegment>>();
        void Add(int x, int z, WallSegment w) { var k = (x, z, w.Level); if (!at.TryGetValue(k, out var l)) at[k] = l = []; l.Add(w); }
        foreach (var w in walls.Where(w => !faces[w].Interior))
            if (w.Axis == WallAxis.AlongX) { Add(w.EdgeX, w.EdgeZ, w); Add(w.EdgeX + 1, w.EdgeZ, w); }
            else { Add(w.EdgeX, w.EdgeZ, w); Add(w.EdgeX, w.EdgeZ + 1, w); }
        var result = new Dictionary<WallSegment, List<(int, int, int)>>();
        foreach (var ((x, z, _), list) in at)
        {
            var xs = list.Where(w => w.Axis == WallAxis.AlongX).ToList(); var zs = list.Where(w => w.Axis == WallAxis.AlongZ).ToList();
            if (xs.Count != 1 || zs.Count != 1) continue;
            var wx = xs[0]; var wz = zs[0];
            int ex = wx.EdgeX == x ? 1 : -1, ez = wz.EdgeZ == z ? 1 : -1;      // direction each wall runs away from the vertex
            int frontZ = faces[wx].Turns == 0 ? 1 : -1, frontX = faces[wz].Turns == 1 ? 1 : -1;
            if (frontZ != -ez || frontX != -ex) continue;                      // concave or mismatched: no outer trim
            int turns = (frontX, frontZ) switch { (-1, 1) => 0, (1, 1) => 1, (1, -1) => 2, _ => 3 };
            if (!result.TryGetValue(wx, out var l)) result[wx] = l = [];
            l.Add((x, z, turns));
        }
        return result;
    }

    public static LotPiece Floor(string lotId, FloorCell f, float baseElevation, string finish = ModuleCatalog.DefaultFloorFinish) =>
        new LotPiece($"lot/{lotId}/floor/{f.Level}/{f.TileX}/{f.TileZ}", lotId, LotPieceKind.Floor,
            (f.TileX + .5f) * BuildGrid.TileMeters, LevelElevation(baseElevation, f.Level) + FloorThickness / 2, (f.TileZ + .5f) * BuildGrid.TileMeters,
            BuildGrid.TileMeters, FloorThickness, BuildGrid.TileMeters, 0, f.Level == 0 ? float.PositiveInfinity : 150, 900, "floor")
        { Modules = [new(ModuleCatalog.FloorModule(finish), 0, -FloorThickness / 2, 0, 0)] };

    /// <summary>Walls between two floored tiles brace each other and hold longer than outside walls.</summary>
    public static LotPiece Wall(string lotId, WallSegment w, float baseElevation, bool interior)
    {
        float y = LevelElevation(baseElevation, w.Level) + WallCentreAboveBase;
        bool alongX = w.Axis == WallAxis.AlongX;
        float cx = alongX ? (w.EdgeX + .5f) * BuildGrid.TileMeters : w.EdgeX * BuildGrid.TileMeters;
        float cz = alongX ? w.EdgeZ * BuildGrid.TileMeters : (w.EdgeZ + .5f) * BuildGrid.TileMeters;
        float length = BuildGrid.TileMeters + WallThickness;
        return new($"lot/{lotId}/wall/{w.Level}/{(alongX ? 'x' : 'z')}/{w.EdgeX}/{w.EdgeZ}", lotId, LotPieceKind.Wall, cx, y, cz,
            alongX ? length : WallThickness, StoryMeters - FloorThickness, alongX ? WallThickness : length, 0, interior ? 135 : 110, 650, interior ? "wall-interior" : "wall");
    }

    /// <summary>Roofs come apart tile by tile and go first, as shingles and decking do.</summary>
    public static LotPiece Roof(string lotId, string roofId, int tileX, int tileZ, int level, float baseElevation) => new($"lot/{lotId}/roof/{roofId}/{tileX}/{tileZ}", lotId, LotPieceKind.Roof,
        (tileX + .5f) * BuildGrid.TileMeters, LevelElevation(baseElevation, level + 1) + RoofThickness / 2, (tileZ + .5f) * BuildGrid.TileMeters,
        BuildGrid.TileMeters + WallThickness, RoofThickness, BuildGrid.TileMeters + WallThickness, 0, 90, 450, "roof");

    /// <summary>
    /// A flat roof is one piece per tile. A pitched roof is one piece per 4 m along its ridge, each covering the 8 m span:
    /// end modules at both ends (a hip roof two tiles long is one 2x2 hip), middles between.
    /// </summary>
    public static IEnumerable<LotPiece> RoofPieces(string lotId, RoofArea r, float baseElevation, IEnumerable<WallSegment> walls,
        IReadOnlyDictionary<WallSegment, WallFinish> wallFinish, IReadOnlySet<FloorCell> floors)
    {
        float top = LevelElevation(baseElevation, r.Level + 1);
        if (r.Shape is not ("gable" or "hip"))
        {
            for (int z = r.MinimumTileZ; z <= r.MaximumTileZ; z++)
                for (int x = r.MinimumTileX; x <= r.MaximumTileX; x++)
                    yield return Roof(lotId, r.RoofId, x, z, r.Level, baseElevation) with { Modules = [new("roof_flat_1x1", 0, -RoofThickness / 2, 0, 0)] };
            yield break;
        }
        bool alongX = r.RidgeAlongX;
        int length = alongX ? r.WidthTiles : r.DepthTiles;
        float spanCentre = alongX ? (r.MinimumTileZ + 1) * BuildGrid.TileMeters : (r.MinimumTileX + 1) * BuildGrid.TileMeters;
        float y = top + PitchedRoofBoxMeters / 2, span = 2 * BuildGrid.TileMeters + 2 * .3f;
        if (r.Shape == "hip" && length == 2)
        {
            float cx = (r.MinimumTileX + 1) * BuildGrid.TileMeters, cz = (r.MinimumTileZ + 1) * BuildGrid.TileMeters;
            yield return new LotPiece($"lot/{lotId}/roof/{r.RoofId}/{r.MinimumTileX}/{r.MinimumTileZ}", lotId, LotPieceKind.Roof, cx, y, cz,
                span, PitchedRoofBoxMeters, span, 0, 90, 1800, "roof") { Modules = [new($"roof_hip_2x2_{r.Finish}", 0, -PitchedRoofBoxMeters / 2, 0, 0)] };
            yield break;
        }
        int midTurns = alongX ? 0 : 1, firstTurns = alongX ? 0 : 3, lastTurns = alongX ? 2 : 1;
        string end = r.Shape == "hip" ? "roof_hip_end" : "roof_gable_end";
        for (int i = 0; i < length; i++)
        {
            int tx = alongX ? r.MinimumTileX + i : r.MinimumTileX, tz = alongX ? r.MinimumTileZ : r.MinimumTileZ + i;
            float cx = alongX ? (tx + .5f) * BuildGrid.TileMeters : spanCentre, cz = alongX ? spanCentre : (tz + .5f) * BuildGrid.TileMeters;
            bool first = i == 0, last = i == length - 1;
            int turns = first ? firstTurns : last ? lastTurns : midTurns;
            var parts = new List<ModulePart> { new($"{(first || last ? end : "roof_gable_mid")}_{r.Finish}", 0, -PitchedRoofBoxMeters / 2, 0, turns) };
            if (r.Shape == "gable" && (first || last))
            {
                // The gable end's wall triangle, finished like the wall below it.
                var (ox, oz) = Turn(-2, 0, turns);
                string style = GableStyle(r, first, alongX, walls, wallFinish, floors);
                parts.Add(new(ModuleCatalog.GableInfillModule(style), ox, -PitchedRoofBoxMeters / 2, oz, (turns + 3) & 3));
            }
            yield return new LotPiece($"lot/{lotId}/roof/{r.RoofId}/{tx}/{tz}", lotId, LotPieceKind.Roof, cx, y, cz,
                alongX ? BuildGrid.TileMeters + WallThickness : span, PitchedRoofBoxMeters, alongX ? span : BuildGrid.TileMeters + WallThickness, 0, 90, 900, "roof")
            { Modules = parts };
        }
    }

    private static string GableStyle(RoofArea r, bool first, bool alongX, IEnumerable<WallSegment> walls, IReadOnlyDictionary<WallSegment, WallFinish> wallFinish, IReadOnlySet<FloorCell> floors)
    {
        var below = alongX
            ? Enumerable.Range(r.MinimumTileZ, 2).Select(z => new WallSegment(first ? r.MinimumTileX : r.MaximumTileX + 1, z, WallAxis.AlongZ, r.Level))
            : Enumerable.Range(r.MinimumTileX, 2).Select(x => new WallSegment(x, first ? r.MinimumTileZ : r.MaximumTileZ + 1, WallAxis.AlongX, r.Level));
        var built = walls.ToHashSet();
        foreach (var w in below.Where(built.Contains))
            return StyleOf(wallFinish.GetValueOrDefault(w), Facing(w, floors).Interior);
        return ModuleCatalog.DefaultExteriorStyle;
    }

    /// <summary>
    /// A curved wall's pieces. Each is a box around its stretch of arc (the physics shape, turned to the arc's tangent)
    /// carrying the arc itself, from which every client generates the true curved mesh.
    /// </summary>
    public static IEnumerable<LotPiece> Curved(string lotId, CurvedWall wall, float baseElevation)
    {
        var segments = CurvedWalls.Segments(wall);
        float level = LevelElevation(baseElevation, wall.Level), height = wall.TopMeters + (wall.Profile is "half" or "counter" ? .05f : 0);
        float inner = wall.Radius - CurvedWalls.Thickness / 2 - (wall.Profile == "counter" ? .45f : 0), outer = wall.Radius + CurvedWalls.Thickness / 2 + .06f;
        for (int i = 0; i < segments.Count; i++)
        {
            var (from, to) = segments[i];
            double half = (to - from) / 2, mid = wall.StartRadians + from + half;
            float nearest = inner * (float)Math.Cos(half), depth = outer - nearest, centreRadius = (outer + nearest) / 2;
            var (cx, cz) = CurvedWalls.Point(wall, mid, centreRadius);
            float yaw = -(float)(mid * 180 / Math.PI + 90);
            float arc = (float)((to - from) * wall.Radius);
            yield return new LotPiece($"lot/{lotId}/curved/{wall.WallId}/{i}", lotId, LotPieceKind.Wall, cx, level + height / 2, cz,
                2 * outer * (float)Math.Sin(half), height, depth, yaw, wall.Profile is "half" or "counter" ? 120 : 110,
                MathF.Max(40, arc * height * 70), "wall")
            { Curve = new(wall, from, to, baseElevation) };
        }
    }

    public static LotPiece Furnishing(string lotId, PlacedFurnishing f, float baseElevation)
    {
        var entry = FurnishingCatalog.Find(f.DefinitionId);
        float height = entry?.HeightMeters ?? .8f, sx = f.SpanX * BuildGrid.SubCellMeters, sz = f.SpanZ * BuildGrid.SubCellMeters;
        return new($"lot/{lotId}/furnishing/{f.InstanceId}", lotId, LotPieceKind.Furnishing,
            f.MinimumX * BuildGrid.SubCellMeters + sx / 2, LevelElevation(baseElevation, f.Level) + FloorThickness + height / 2, f.MinimumZ * BuildGrid.SubCellMeters + sz / 2,
            sx, height, sz, 0, 95, Math.Max(15, sx * sz * 45), entry?.Material ?? "wood");
    }

    /// <summary>Ghosts for a build preview: the floor or roof rectangle, the wall run, or the curved wall itself.</summary>
    public static IReadOnlyList<LotPiece> Ghost(string lotId, LotBuildPreview preview, float baseElevation)
    {
        if (preview.Edit is not { } e) return [];
        return e.Kind switch
        {
            BuildEditKind.AddFloor or BuildEditKind.RemoveFloor => e.FloorCells().Select(c => Floor(lotId, c, baseElevation) with { SizeY = FloorThickness + .04f, Modules = [] }).ToArray(),
            BuildEditKind.AddWall or BuildEditKind.RemoveWall or BuildEditKind.SetWallFinish => e.Walls!.Select(w => Wall(lotId, w, baseElevation, false)).ToArray(),
            BuildEditKind.AddRoof => e.FloorCells().Select(c => Roof(lotId, "ghost", c.TileX, c.TileZ, e.Level, baseElevation)).ToArray(),
            BuildEditKind.AddCurvedWall or BuildEditKind.SetCurvedWall when e.Curve is { } c => Curved(lotId, c, baseElevation).ToArray(),
            _ => [],
        };
    }
}
