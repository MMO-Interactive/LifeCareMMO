using System.Numerics;

namespace LifeCare.Construction;

/// <summary>
/// Sets one door to an explicit state (Life Beyond's ConstructionDoorStateRequest). This is an interaction, not a
/// construction edit: any signed-in character near the door may operate it without owning the lot. The operation id
/// makes a retried request harmless.
/// </summary>
public sealed record DoorRequest(string CharacterId, string DoorId, bool Open, string? OperationId = null);

/// <summary>A door on a lot: where its opening is, which way the wall runs there, and how big the opening is.</summary>
public sealed record LotDoor(string DoorId, string Kind, int Level, float CenterX, float CenterZ, float TangentX, float TangentZ, float WidthMeters, float HeightMeters)
{
    public string Name => ModuleCatalog.Opening(Kind)?.Name ?? "Door";
}

/// <summary>
/// How a door piece is drawn and animated by clients. The pivot is a hinge (swing) or the wall origin (slide, overhead);
/// the closed yaw is the wall's. Straight doors use pack leaf modules; curved-wall doors use a generated leaf.
/// </summary>
public sealed record DoorLeaf(string DoorId, string Motion, float PivotX, float PivotY, float PivotZ, float ClosedYawDegrees, float SlideX, float SlideZ,
    string? ClosedModule, string? OpenModule, bool Open, float CurvedWidth = 0, string? CurvedKind = null, string? CurvedColour = null);

/// <summary>One leaf of a door kind, in wall-local metres (origin at the edge midpoint at the level base, front +Z).</summary>
public sealed record DoorLeafSpec(string ModuleId, string Motion, float PivotX, float PivotZ, Vector3 Min, Vector3 Max, Vector3 OpenMin, Vector3 OpenMax,
    float SlideX = 0, string? OpenModuleId = null);

public static class LotDoors
{
    /// <summary>A character may operate a door within one tile of it, as in Life Beyond.</summary>
    public const float InteractionRangeMeters = BuildGrid.TileMeters;
    public const string OpenAction = "lifecare.door.open", CloseAction = "lifecare.door.close";
    public const float LeafHeight = 2.07f, SwingWidth = .84f;

    public static bool IsDoor(string? opening) => opening is "door_wood" or "door_halfglass" or "door_screen" or "door_garage" or "door_barn";

    public static string StraightId(WallSegment w) => $"wall/{w.Level}/{(w.Axis == WallAxis.AlongX ? 'x' : 'z')}/{w.EdgeX}/{w.EdgeZ}";
    public static string CurvedId(string wallId, CurvedOpening opening) => $"curved/{wallId}/{opening.CenterMilliDegrees}";

    public static string Colour(string style) => style == "lap_white" ? "green" : "wood";

    /// <summary>The leaves of a door kind. Hinged leaves pivot on their hinge and swing into the room (one quarter turn).</summary>
    public static IReadOnlyList<DoorLeafSpec> Leaves(string kind, string style)
    {
        const float floor = LotGeometry.FloorThickness;
        switch (kind)
        {
            case "door_wood" or "door_halfglass" or "door_screen":
                string module = $"door_leaf_{kind[5..]}_{Colour(style)}";
                // Closed: the leaf runs +X from the hinge; open: a quarter turn puts it along -Z (into the room).
                return [new(module, "swing", -.42f, .0425f, new(0, floor, -.03f), new(SwingWidth, floor + LeafHeight, .03f),
                    new(-.03f, floor, -SwingWidth), new(.03f, floor + LeafHeight, 0))];
            case "door_garage":
                return [new("door_leaf_garage", "overhead", 0, 0, new(-1.32f, floor, 0), new(1.32f, floor + 2.37f, .05f),
                    new(-1.32f, floor + 2.44f, -2.52f), new(1.32f, floor + 2.5f, -.15f), OpenModuleId: "door_leaf_garage_open")];
            case "door_barn":
                return [new("door_leaf_barn_left", "slide", 0, 0, new(-1.6f, floor - .02f, .145f), new(0, floor + 2.65f, .205f),
                        new(-3.15f, floor - .02f, .145f), new(-1.55f, floor + 2.65f, .205f), SlideX: -1.55f),
                    new("door_leaf_barn_right", "slide", 0, 0, new(0, floor - .02f, .145f), new(1.6f, floor + 2.65f, .205f),
                        new(1.55f, floor - .02f, .145f), new(3.15f, floor + 2.65f, .205f), SlideX: 1.55f)];
            default: return [];
        }
    }

    /// <summary>Every door on the lot (straight-wall doors and doors in curved walls; open passages have no leaf).</summary>
    public static IEnumerable<LotDoor> All(BuildingPlan plan)
    {
        foreach (var finish in plan.WallFinishes.Where(f => IsDoor(f.Opening) && plan.Walls.Contains(f.Wall)))
        {
            var w = finish.Wall; var spec = ModuleCatalog.Opening(finish.Opening)!;
            bool alongX = w.Axis == WallAxis.AlongX;
            float cx = alongX ? (w.EdgeX + .5f) * BuildGrid.TileMeters : w.EdgeX * BuildGrid.TileMeters;
            float cz = alongX ? w.EdgeZ * BuildGrid.TileMeters : (w.EdgeZ + .5f) * BuildGrid.TileMeters;
            yield return new(StraightId(w), finish.Opening, w.Level, cx, cz, alongX ? 1 : 0, alongX ? 0 : 1, spec.WidthMeters, spec.HeightMeters);
        }
        foreach (var wall in plan.CurvedWalls)
            foreach (var opening in wall.HostedOpenings.Where(o => IsDoor(o.Kind)))
            {
                var spec = ModuleCatalog.Opening(opening.Kind)!;
                double angle = wall.StartRadians + opening.CenterMilliDegrees / 1000.0 * Math.PI / 180;
                var (x, z) = CurvedWalls.Point(wall, angle, wall.Radius);
                yield return new(CurvedId(wall.WallId, opening), opening.Kind, wall.Level, x, z, -(float)Math.Sin(angle), (float)Math.Cos(angle), spec.WidthMeters, spec.HeightMeters);
            }
    }

    /// <summary>The authority's rule: the door must exist; the state is explicit, so repeating it changes nothing.</summary>
    public static BuildResult SetOpen(BuildingPlan plan, string characterId, string doorId, bool open)
    {
        if (string.IsNullOrWhiteSpace(characterId)) return new(false, plan, "not_signed_in", "Sign in to use doors.");
        if (!All(plan).Any(d => d.DoorId == doorId)) return new(false, plan, "door_not_found", "There is no door there.");
        var doors = plan.OpenDoors.Where(d => d != doorId).ToList();
        if (open) doors.Add(doorId);
        return new(true, plan with { OpenDoors = doors.Order(StringComparer.Ordinal).ToList() });
    }

    /// <summary>Whether a character standing at <paramref name="feet"/> can reach the door (one tile, about one story).</summary>
    public static bool InReach(LotDoor door, Vector3 feet, float baseElevation)
    {
        float dx = feet.X - door.CenterX, dz = feet.Z - door.CenterZ;
        float level = LotGeometry.LevelElevation(baseElevation, door.Level);
        return dx * dx + dz * dz <= InteractionRangeMeters * InteractionRangeMeters && feet.Y > level - 1.5f && feet.Y < level + LotGeometry.StoryMeters;
    }

    /// <summary>The door the ray points at within <paramref name="reach"/> metres: its opening as a box across the wall.</summary>
    public static (LotDoor Door, float Distance)? Hit(IEnumerable<LotDoor> doors, float baseElevation, Vector3 origin, Vector3 direction, float reach = 5)
    {
        (LotDoor, float)? best = null;
        foreach (var door in doors)
        {
            var t = new Vector3(door.TangentX, 0, door.TangentZ); var n = new Vector3(-t.Z, 0, t.X);
            float bottom = LotGeometry.LevelElevation(baseElevation, door.Level) + LotGeometry.FloorThickness;
            var centre = new Vector3(door.CenterX, bottom + door.HeightMeters / 2, door.CenterZ);
            var o = origin - centre;
            // Slab test in the door's frame: along the wall, up, and across the wall.
            float t0 = 0, t1 = reach;
            foreach (var (axis, half) in new[] { (t, door.WidthMeters / 2), (Vector3.UnitY, door.HeightMeters / 2), (n, .35f) })
            {
                float od = Vector3.Dot(o, axis), dd = Vector3.Dot(direction, axis);
                if (MathF.Abs(dd) < 1e-6f) { if (MathF.Abs(od) > half) { t0 = float.MaxValue; break; } continue; }
                float a = (-half - od) / dd, b = (half - od) / dd;
                t0 = MathF.Max(t0, MathF.Min(a, b)); t1 = MathF.Min(t1, MathF.Max(a, b));
            }
            if (t0 <= t1 && (best is null || t0 < best.Value.Item2)) best = (door, t0);
        }
        return best;
    }
}

/// <summary>The leaf of a door in a curved wall: a straight leaf on the opening's chord, generated with its origin on the hinge.</summary>
public static class DoorLeafMesher
{
    public static MeshBuffer Build(string kind, string colour, float width)
    {
        var mesh = new MeshBuffer();
        var door = ModuleCatalog.Color(colour == "green" ? "door_green" : "door_wood");
        float y0 = LotGeometry.FloorThickness, h = LotDoors.LeafHeight, t = .0225f;
        if (kind == "door_halfglass")
        {
            float split = y0 + 1.0f;
            mesh.Box(new(width / 2, (y0 + split) / 2, 0), new(width / 2, (split - y0) / 2, t), Vector3.UnitX, door);
            mesh.Box(new(width / 2, (split + y0 + h) / 2, 0), new(width / 2 - .1f, (y0 + h - split) / 2 - .05f, .01f), Vector3.UnitX, ModuleCatalog.Color("glass"));
            mesh.Box(new(.05f, (split + y0 + h) / 2, 0), new(.05f, (y0 + h - split) / 2, t), Vector3.UnitX, door);
            mesh.Box(new(width - .05f, (split + y0 + h) / 2, 0), new(.05f, (y0 + h - split) / 2, t), Vector3.UnitX, door);
            mesh.Box(new(width / 2, y0 + h - .05f, 0), new(width / 2, .05f, t), Vector3.UnitX, door);
        }
        else mesh.Box(new(width / 2, y0 + h / 2, 0), new(width / 2, h / 2, t), Vector3.UnitX, door);
        foreach (float side in new[] { -1f, 1f })
            mesh.Box(new(width - .08f, y0 + .98f, side * .05f), new(.025f, .025f, .03f), Vector3.UnitX, ModuleCatalog.Color("brass"));
        return mesh;
    }

    /// <summary>The leaf as a piece mesh (centred on the piece, in its yaw), for drawing it at rest and as debris.</summary>
    public static MeshBuffer ForPiece(LotPiece piece)
    {
        var leaf = piece.Door ?? throw new ArgumentException("Not a door leaf piece.", nameof(piece));
        var mesh = Build(leaf.CurvedKind!, leaf.CurvedColour ?? "wood", leaf.CurvedWidth);
        float yaw = leaf.ClosedYawDegrees + (leaf.Open ? 90 : 0);
        var rotate = Matrix4x4.CreateRotationY(yaw * MathF.PI / 180);
        for (int i = 0; i < mesh.VertexCount; i++)
        {
            mesh.Positions[i] = Vector3.Transform(mesh.Positions[i], rotate) + new Vector3(leaf.PivotX, leaf.PivotY, leaf.PivotZ);
            mesh.Normals[i] = Vector3.TransformNormal(mesh.Normals[i], rotate);
        }
        mesh.ToLocal(new(piece.CenterX, piece.CenterY, piece.CenterZ), piece.YawDegrees);
        return mesh;
    }
}
