namespace LifeCare.Construction;

public enum LotPieceKind { Floor, Wall, Roof, Furnishing }

/// <summary>
/// One pack module drawn as part of a piece: offset from the piece's centre in world axes, turned by quarter turns about
/// Y (each turn maps local +X to -Z and the module's front, +Z, to +X).
/// </summary>
/// <remarks>Paints recolour the module's paintable faces: on a wall, its front (+Z, the outside) and back (the room side);
/// on a floor, the walking surface.</remarks>
/// <remarks>A clip keeps only the triangles whose centre lies in [ClipFrom, ClipTo] along the module's own X (before it turns):
/// a door wall is split into jamb and lintel pieces that each draw their slice of the one module.</remarks>
public sealed record ModulePart(string ModuleId, float OffsetX, float OffsetY, float OffsetZ, int QuarterTurns,
    string? InsidePaint = null, string? OutsidePaint = null, string? FloorPaint = null,
    float ClipFrom = float.NegativeInfinity, float ClipTo = float.PositiveInfinity);

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
    /// <summary>Set on a door leaf: clients animate it between closed and open; at rest and as debris it draws in its server state.</summary>
    public DoorLeaf? Door { get; init; }
    /// <summary>Identifies the drawn shape, so equal pieces share one mesh.</summary>
    public string? MeshKey => Door is { CurvedKind: not null } d
        ? $"leafcurve:{d.CurvedKind}:{d.CurvedColour}:{d.CurvedWidth:R}:{d.Open}:{d.PivotX:R},{d.PivotY:R},{d.PivotZ:R}:{d.ClosedYawDegrees:R}:{CenterX:R},{CenterY:R},{CenterZ:R}:{YawDegrees:R}"
        : Curve is { } c
        ? $"curve:{c.Wall.Fingerprint}:{c.From:R}:{c.To:R}:{c.BaseElevation:R}:{CenterX:R}:{CenterY:R}:{CenterZ:R}:{YawDegrees:R}"
        : Modules.Count == 0 ? null : string.Join('|', Modules.Select(m => $"{m.ModuleId}@{m.OffsetX:0.###},{m.OffsetY:0.###},{m.OffsetZ:0.###}q{m.QuarterTurns & 3}p{m.InsidePaint},{m.OutsidePaint},{m.FloorPaint}c{m.ClipFrom:0.###},{m.ClipTo:0.###}"));

    /// <summary>Pieces are equal when their box, release rules and drawn shape are, so geometry compares by value across revisions.</summary>
    public bool Equals(LotPiece? other) => other is not null && PieceId == other.PieceId && LotId == other.LotId && Kind == other.Kind
        && CenterX == other.CenterX && CenterY == other.CenterY && CenterZ == other.CenterZ && SizeX == other.SizeX && SizeY == other.SizeY && SizeZ == other.SizeZ
        && YawDegrees == other.YawDegrees && ReleaseWindMph.Equals(other.ReleaseWindMph) && MassKilograms == other.MassKilograms && Material == other.Material
        && MeshKey == other.MeshKey && Door == other.Door;

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
        var floorFinish = (lot.FloorFinishes ?? []).ToDictionary(f => f.Floor);
        var wallFinish = (lot.WallFinishes ?? []).ToDictionary(f => f.Wall);
        foreach (var f in lot.Floors)
        {
            var finish = floorFinish.GetValueOrDefault(f);
            pieces.Add(Floor(id, f, baseElevation, finish?.Finish ?? ModuleCatalog.DefaultFloorFinish, finish?.Paint));
        }

        var faces = lot.Walls.ToDictionary(w => w, w => Facing(w, floors));
        var corners = OuterCorners(lot.Walls, faces);
        var open = (lot.OpenDoors ?? []).ToHashSet(StringComparer.Ordinal);
        foreach (var w in lot.Walls)
        {
            var face = faces[w];
            var finish = wallFinish.GetValueOrDefault(w);
            string style = StyleOf(finish, face.Interior), opening = finish?.Opening is { Length: > 0 } o ? o : ModuleCatalog.NoOpening;
            var spec = ModuleCatalog.Opening(opening);
            var piece = Wall(id, w, baseElevation, face.Interior);
            if (spec?.Partial == true)
            {
                // Half walls and railings: a low box you can see (and reach) over.
                float h = spec.HeightMeters + .05f;
                piece = piece with { CenterY = LevelElevation(baseElevation, w.Level) + FloorThickness + h / 2, SizeY = h };
            }
            var wallModule = new ModulePart(ModuleCatalog.WallModule(style, opening), 0, 0, 0, face.Turns, finish?.InsidePaint, finish?.OutsidePaint);
            var cornerParts = corners.GetValueOrDefault(w, []).Select(c => (c.X, c.Z, new ModulePart(ModuleCatalog.OuterCornerModule(style), 0, 0, 0, c.Turns))).ToList();
            if (LotDoors.IsDoor(opening) || opening == "passage")
            {
                pieces.AddRange(DoorWall(id, w, baseElevation, face, piece, wallModule, cornerParts, spec!, style, open));
                continue;
            }
            float baseY = LevelElevation(baseElevation, w.Level);
            var parts = new List<ModulePart> { wallModule with { OffsetY = baseY - piece.CenterY } };
            foreach (var (vx, vz, part) in cornerParts)
                parts.Add(part with { OffsetX = vx * BuildGrid.TileMeters - piece.CenterX, OffsetY = baseY - piece.CenterY, OffsetZ = vz * BuildGrid.TileMeters - piece.CenterZ });
            pieces.Add(piece with { Modules = parts });
        }
        foreach (var r in lot.Roofs) pieces.AddRange(RoofPieces(id, r, baseElevation, lot.Walls, wallFinish, floors));
        foreach (var c in lot.CurvedWalls ?? []) pieces.AddRange(Curved(id, c, baseElevation, open));
        foreach (var f in lot.Furnishings) pieces.Add(Furnishing(id, f, baseElevation));
        return pieces;
    }

    /// <summary>
    /// A wall with a door, as Tiny Clinic collides it: jambs either side and a lintel above (each drawing its slice of the
    /// wall module), so the doorway itself is open, plus the leaves: solid while closed, swung or slid clear while open.
    /// </summary>
    private static IEnumerable<LotPiece> DoorWall(string lotId, WallSegment w, float baseElevation, (int Turns, bool Interior) face, LotPiece whole,
        ModulePart wallModule, List<(int X, int Z, ModulePart Part)> corners, WallOpeningSpec spec, string style, HashSet<string> open)
    {
        float levelY = LevelElevation(baseElevation, w.Level), half = (BuildGrid.TileMeters + WallThickness) / 2;
        bool alongX = w.Axis == WallAxis.AlongX;
        var origin = new System.Numerics.Vector3(alongX ? (w.EdgeX + .5f) * BuildGrid.TileMeters : w.EdgeX * BuildGrid.TileMeters, levelY,
            alongX ? w.EdgeZ * BuildGrid.TileMeters : (w.EdgeZ + .5f) * BuildGrid.TileMeters);
        float x0 = spec.OffsetMeters - spec.WidthMeters / 2, x1 = spec.OffsetMeters + spec.WidthMeters / 2, top = FloorThickness + spec.HeightMeters;
        // Wall-local box (x along the wall, y up from the level base, z across) to a world-axis box.
        (System.Numerics.Vector3 Min, System.Numerics.Vector3 Max) World(System.Numerics.Vector3 a, System.Numerics.Vector3 b)
        {
            var (ax, az) = Turn(a.X, a.Z, face.Turns); var (bx, bz) = Turn(b.X, b.Z, face.Turns);
            return (new(origin.X + MathF.Min(ax, bx), origin.Y + MathF.Min(a.Y, b.Y), origin.Z + MathF.Min(az, bz)),
                new(origin.X + MathF.Max(ax, bx), origin.Y + MathF.Max(a.Y, b.Y), origin.Z + MathF.Max(az, bz)));
        }
        LotPiece Section(string suffix, float from, float to, float bottom, bool withCorners, int cornerSide)
        {
            var (min, max) = World(new(from, bottom, -WallThickness / 2), new(to, StoryMeters, WallThickness / 2));
            var centre = (min + max) / 2;
            var parts = new List<ModulePart> { wallModule with { OffsetX = origin.X - centre.X, OffsetY = origin.Y - centre.Y, OffsetZ = origin.Z - centre.Z,
                ClipFrom = from <= -half ? float.NegativeInfinity : from, ClipTo = to >= half ? float.PositiveInfinity : to } };
            if (withCorners)
            {
                var (dirX, dirZ) = Turn(1, 0, face.Turns);
                foreach (var (vx, vz, part) in corners)
                {
                    float along = (vx * BuildGrid.TileMeters - origin.X) * dirX + (vz * BuildGrid.TileMeters - origin.Z) * dirZ;
                    if (MathF.Sign(along) == cornerSide)
                        parts.Add(part with { OffsetX = vx * BuildGrid.TileMeters - centre.X, OffsetY = origin.Y - centre.Y, OffsetZ = vz * BuildGrid.TileMeters - centre.Z });
                }
            }
            return whole with { PieceId = whole.PieceId + "/" + suffix, CenterX = centre.X, CenterY = centre.Y, CenterZ = centre.Z,
                SizeX = max.X - min.X, SizeY = max.Y - min.Y, SizeZ = max.Z - min.Z, MassKilograms = whole.MassKilograms * (to - from) / (2 * half), Modules = parts };
        }
        yield return Section("left", -half, x0, FloorThickness, true, -1);
        yield return Section("right", x1, half, FloorThickness, true, 1);
        yield return Section("top", x0, x1, top, false, 0);
        if (!LotDoors.IsDoor(spec.Id)) yield break;

        string doorId = LotDoors.StraightId(w);
        bool isOpen = open.Contains(doorId);
        foreach (var (leaf, index) in LotDoors.Leaves(spec.Id, style).Select((l, i) => (l, i)))
        {
            var pivotLocal = new System.Numerics.Vector3(leaf.PivotX, 0, leaf.PivotZ);
            var (lo, hi) = isOpen ? (leaf.OpenMin, leaf.OpenMax) : (leaf.Min, leaf.Max);
            var (min, max) = World(pivotLocal + lo, pivotLocal + hi);
            var centre = (min + max) / 2;
            var (px, pz) = Turn(leaf.PivotX, leaf.PivotZ, face.Turns);
            var pivot = new System.Numerics.Vector3(origin.X + px, levelY, origin.Z + pz);
            var (sx, sz) = Turn(leaf.SlideX, 0, face.Turns);
            string module = isOpen && leaf.OpenModuleId is { } openModule ? openModule : leaf.ModuleId;
            int turns = face.Turns + (isOpen && leaf.Motion == "swing" ? 1 : 0);
            float slide = isOpen ? 1 : 0;
            yield return new LotPiece($"{whole.PieceId}/leaf{index}", lotId, LotPieceKind.Wall, centre.X, centre.Y, centre.Z, max.X - min.X, max.Y - min.Y, max.Z - min.Z,
                0, 100, spec.Vehicle ? 160 : 40, "wood")
            {
                Modules = [new(module, pivot.X + sx * slide - centre.X, pivot.Y - centre.Y, pivot.Z + sz * slide - centre.Z, turns)],
                Door = new(doorId, leaf.Motion, pivot.X, pivot.Y, pivot.Z, face.Turns * 90f, sx, sz, leaf.ModuleId, leaf.OpenModuleId, isOpen),
            };
        }
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

    public static LotPiece Floor(string lotId, FloorCell f, float baseElevation, string finish = ModuleCatalog.DefaultFloorFinish, string? paint = null) =>
        new LotPiece($"lot/{lotId}/floor/{f.Level}/{f.TileX}/{f.TileZ}", lotId, LotPieceKind.Floor,
            (f.TileX + .5f) * BuildGrid.TileMeters, LevelElevation(baseElevation, f.Level) + FloorThickness / 2, (f.TileZ + .5f) * BuildGrid.TileMeters,
            BuildGrid.TileMeters, FloorThickness, BuildGrid.TileMeters, 0, f.Level == 0 ? float.PositiveInfinity : 150, 900, "floor")
        { Modules = [new(ModuleCatalog.FloorModule(finish), 0, -FloorThickness / 2, 0, 0, FloorPaint: paint ?? ModuleCatalog.DefaultFloorPaint(finish))] };

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
                var (style, paint) = GableStyle(r, first, alongX, walls, wallFinish, floors);
                parts.Add(new(ModuleCatalog.GableInfillModule(style), ox, -PitchedRoofBoxMeters / 2, oz, (turns + 3) & 3, OutsidePaint: paint));
            }
            yield return new LotPiece($"lot/{lotId}/roof/{r.RoofId}/{tx}/{tz}", lotId, LotPieceKind.Roof, cx, y, cz,
                alongX ? BuildGrid.TileMeters + WallThickness : span, PitchedRoofBoxMeters, alongX ? span : BuildGrid.TileMeters + WallThickness, 0, 90, 900, "roof")
            { Modules = parts };
        }
    }

    private static (string Style, string? Paint) GableStyle(RoofArea r, bool first, bool alongX, IEnumerable<WallSegment> walls, IReadOnlyDictionary<WallSegment, WallFinish> wallFinish, IReadOnlySet<FloorCell> floors)
    {
        var below = alongX
            ? Enumerable.Range(r.MinimumTileZ, 2).Select(z => new WallSegment(first ? r.MinimumTileX : r.MaximumTileX + 1, z, WallAxis.AlongZ, r.Level))
            : Enumerable.Range(r.MinimumTileX, 2).Select(x => new WallSegment(x, first ? r.MinimumTileZ : r.MaximumTileZ + 1, WallAxis.AlongX, r.Level));
        var built = walls.ToHashSet();
        foreach (var w in below.Where(built.Contains))
            return (StyleOf(wallFinish.GetValueOrDefault(w), Facing(w, floors).Interior), wallFinish.GetValueOrDefault(w)?.OutsidePaint);
        return (ModuleCatalog.DefaultExteriorStyle, null);
    }

    /// <summary>
    /// A curved wall's pieces. Each is a box around its stretch of arc (the physics shape, turned to the arc's tangent)
    /// carrying the arc itself, from which every client generates the true curved mesh.
    /// </summary>
    public static IEnumerable<LotPiece> Curved(string lotId, CurvedWall wall, float baseElevation, IReadOnlySet<string>? openDoors = null)
    {
        var segments = CurvedWalls.Segments(wall);
        var passable = wall.HostedOpenings.Where(o => ModuleCatalog.Opening(o.Kind)?.Passable == true).Select(o => (Opening: o, Range: CurvedWalls.Range(wall, o))).ToList();
        float level = LevelElevation(baseElevation, wall.Level), height = wall.TopMeters + (wall.Profile is "half" or "counter" ? .05f : 0);
        float inner = wall.Radius - CurvedWalls.Thickness / 2 - (wall.Profile == "counter" ? .45f : 0), outer = wall.Radius + CurvedWalls.Thickness / 2 + .06f;
        LotPiece Piece(string id, double from, double to, float bottom)
        {
            double half = (to - from) / 2, mid = wall.StartRadians + from + half;
            float nearest = inner * (float)Math.Cos(half), depth = outer - nearest, centreRadius = (outer + nearest) / 2;
            var (cx, cz) = CurvedWalls.Point(wall, mid, centreRadius);
            float yaw = -(float)(mid * 180 / Math.PI + 90);
            float arc = (float)((to - from) * wall.Radius), h = height - bottom;
            return new LotPiece(id, lotId, LotPieceKind.Wall, cx, level + bottom + h / 2, cz,
                2 * outer * (float)Math.Sin(half), h, depth, yaw, wall.Profile is "half" or "counter" ? 120 : 110,
                MathF.Max(40, arc * h * 70), "wall")
            { Curve = new(wall, from, to, baseElevation) };
        }
        for (int i = 0; i < segments.Count; i++)
        {
            var (from, to) = segments[i];
            string id = $"lot/{lotId}/curved/{wall.WallId}/{i}";
            var door = passable.FirstOrDefault(p => p.Range.From >= from - 1e-9 && p.Range.To <= to + 1e-9);
            if (door.Opening is null) { yield return Piece(id, from, to, 0); continue; }
            // A doorway: the wall either side, the lintel above, and the opening itself clear.
            var spec = ModuleCatalog.Opening(door.Opening.Kind)!;
            if (door.Range.From - from > 1e-6) yield return Piece(id + "/left", from, door.Range.From, 0);
            if (to - door.Range.To > 1e-6) yield return Piece(id + "/right", door.Range.To, to, 0);
            yield return Piece(id + "/top", door.Range.From, door.Range.To, FloorThickness + spec.HeightMeters);
            if (LotDoors.IsDoor(door.Opening.Kind)) yield return CurvedLeaf(id + "/leaf0", lotId, wall, door.Opening, door.Range, level, openDoors);
        }
    }

    /// <summary>A straight leaf across the curved doorway's chord, hinged at one end and opening a quarter turn into the room.</summary>
    private static LotPiece CurvedLeaf(string id, string lotId, CurvedWall wall, CurvedOpening opening, (double From, double To) range, float level, IReadOnlySet<string>? openDoors)
    {
        string doorId = LotDoors.CurvedId(wall.WallId, opening);
        bool isOpen = openDoors?.Contains(doorId) == true;
        double margin = .02 / wall.Radius;
        var (ax, az) = CurvedWalls.Point(wall, wall.StartRadians + range.From + margin, wall.Radius);
        var (bx, bz) = CurvedWalls.Point(wall, wall.StartRadians + range.To - margin, wall.Radius);
        var hinge = new System.Numerics.Vector2(ax, az); var chord = new System.Numerics.Vector2(bx - ax, bz - az);
        float yaw = MathF.Atan2(-chord.Y, chord.X) * 180 / MathF.PI;
        // The leaf opens toward local -Z; make that the room side (inside the ring unless the wall faces inward).
        var openTowards = new System.Numerics.Vector2(-MathF.Sin(yaw * MathF.PI / 180), -MathF.Cos(yaw * MathF.PI / 180));
        var toCentre = new System.Numerics.Vector2(wall.CenterX, wall.CenterZ) - (hinge + chord / 2);
        if ((System.Numerics.Vector2.Dot(openTowards, toCentre) > 0) != wall.ExteriorOutside) { hinge += chord; chord = -chord; yaw += 180; }
        float width = chord.Length(), finalYaw = yaw + (isOpen ? 90 : 0), r = finalYaw * MathF.PI / 180;
        var along = new System.Numerics.Vector2(MathF.Cos(r), -MathF.Sin(r));
        var centre = hinge + along * (width / 2);
        var style = ModuleCatalog.Style(wall.Style)?.Id ?? ModuleCatalog.DefaultExteriorStyle;
        return new LotPiece(id, lotId, LotPieceKind.Wall, centre.X, level + FloorThickness + LotDoors.LeafHeight / 2, centre.Y, width, LotDoors.LeafHeight, .05f,
            finalYaw, 100, 40, "wood")
        {
            Door = new(doorId, "swing", hinge.X, level, hinge.Y, yaw, 0, 0, null, null, isOpen, width, opening.Kind, LotDoors.Colour(style)),
        };
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
    public static IReadOnlyList<LotPiece> Ghost(string lotId, LotBuildPreview preview, float baseElevation) =>
        preview.Edits.SelectMany(e => Ghost(lotId, e, baseElevation)).ToArray();

    private static IReadOnlyList<LotPiece> Ghost(string lotId, BuildEdit e, float baseElevation)
    {
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
