namespace LifeCare.Construction;

public enum LotPieceKind { Floor, Wall, Roof, Furnishing }

/// <summary>
/// One box of a built lot in world metres (Y up). <see cref="PieceId"/> is stable for the same element across revisions,
/// so the district host's storm damage and every client agree on which piece broke. Wind and mass feed the host's
/// PhysX destruction: a piece is torn loose when the local wind reaches <see cref="ReleaseWindMph"/>.
/// </summary>
public sealed record LotPiece(string PieceId, string LotId, LotPieceKind Kind, float CenterX, float CenterY, float CenterZ,
    float SizeX, float SizeY, float SizeZ, float YawDegrees, float ReleaseWindMph, float MassKilograms, string Material);

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

/// <summary>Turns a lot document into boxes. Tile (x, z) covers world [4x, 4x+4] × [4z, 4z+4].</summary>
public static class LotGeometry
{
    public const float StoryMeters = 3.2f;
    public const float FloorThickness = .2f;
    public const float WallThickness = .2f;
    public const float RoofThickness = .25f;

    /// <summary>The ground height a lot is built on: the highest of its corners and centre, so no floor is buried.</summary>
    public static float BaseElevation(LotDeed lot, Func<float, float, float> heightAt)
    {
        float x0 = lot.MinimumTileX * BuildGrid.TileMeters, z0 = lot.MinimumTileZ * BuildGrid.TileMeters;
        float x1 = (lot.MaximumTileX + 1) * BuildGrid.TileMeters, z1 = (lot.MaximumTileZ + 1) * BuildGrid.TileMeters;
        return new[] { heightAt(x0, z0), heightAt(x1, z0), heightAt(x0, z1), heightAt(x1, z1), heightAt((x0 + x1) / 2, (z0 + z1) / 2) }.Max();
    }

    public static float LevelElevation(float baseElevation, int level) => baseElevation + level * StoryMeters;

    public static IReadOnlyList<LotPiece> Build(LotDocument lot, float baseElevation)
    {
        string id = lot.Lot.LotId; var pieces = new List<LotPiece>();
        var floors = lot.Floors.ToHashSet();
        foreach (var f in lot.Floors) pieces.Add(Floor(id, f, baseElevation));
        foreach (var w in lot.Walls)
        {
            var (a, b) = w.Sides;
            bool interior = floors.Contains(new(a.X, a.Z, w.Level)) && floors.Contains(new(b.X, b.Z, w.Level));
            pieces.Add(Wall(id, w, baseElevation, interior));
        }
        foreach (var r in lot.Roofs)
            for (int z = r.MinimumTileZ; z <= r.MaximumTileZ; z++)
                for (int x = r.MinimumTileX; x <= r.MaximumTileX; x++)
                    pieces.Add(Roof(id, r.RoofId, x, z, r.Level, baseElevation));
        foreach (var f in lot.Furnishings) pieces.Add(Furnishing(id, f, baseElevation));
        return pieces;
    }

    public static LotPiece Floor(string lotId, FloorCell f, float baseElevation) => new($"lot/{lotId}/floor/{f.Level}/{f.TileX}/{f.TileZ}", lotId, LotPieceKind.Floor,
        (f.TileX + .5f) * BuildGrid.TileMeters, LevelElevation(baseElevation, f.Level) + FloorThickness / 2, (f.TileZ + .5f) * BuildGrid.TileMeters,
        BuildGrid.TileMeters, FloorThickness, BuildGrid.TileMeters, 0, f.Level == 0 ? float.PositiveInfinity : 150, 900, "floor");

    /// <summary>Walls between two floored tiles brace each other and hold longer than outside walls.</summary>
    public static LotPiece Wall(string lotId, WallSegment w, float baseElevation, bool interior)
    {
        float y = LevelElevation(baseElevation, w.Level) + FloorThickness + (StoryMeters - FloorThickness) / 2;
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

    public static LotPiece Furnishing(string lotId, PlacedFurnishing f, float baseElevation)
    {
        var entry = FurnishingCatalog.Find(f.DefinitionId);
        float height = entry?.HeightMeters ?? .8f, sx = f.SpanX * BuildGrid.SubCellMeters, sz = f.SpanZ * BuildGrid.SubCellMeters;
        return new($"lot/{lotId}/furnishing/{f.InstanceId}", lotId, LotPieceKind.Furnishing,
            f.MinimumX * BuildGrid.SubCellMeters + sx / 2, LevelElevation(baseElevation, f.Level) + FloorThickness + height / 2, f.MinimumZ * BuildGrid.SubCellMeters + sz / 2,
            sx, height, sz, 0, 95, Math.Max(15, sx * sz * 45), entry?.Material ?? "wood");
    }

    /// <summary>Ghost boxes for a build preview: the floor or roof rectangle, or the wall run.</summary>
    public static IReadOnlyList<LotPiece> Ghost(string lotId, LotBuildPreview preview, float baseElevation)
    {
        if (preview.Edit is not { } e) return [];
        return e.Kind switch
        {
            BuildEditKind.AddFloor or BuildEditKind.RemoveFloor => e.FloorCells().Select(c => Floor(lotId, c, baseElevation) with { SizeY = FloorThickness + .04f }).ToArray(),
            BuildEditKind.AddWall or BuildEditKind.RemoveWall => e.Walls!.Select(w => Wall(lotId, w, baseElevation, false)).ToArray(),
            BuildEditKind.AddRoof => e.FloorCells().Select(c => Roof(lotId, "ghost", c.TileX, c.TileZ, e.Level, baseElevation)).ToArray(),
            _ => [],
        };
    }
}
