using System.Numerics;

namespace LifeCare.Construction;

/// <summary>An opening hosted by a curved wall, placed by the angle of its centre measured from the wall's start.</summary>
public sealed record CurvedOpening(string Kind, int CenterMilliDegrees);

/// <summary>
/// A true curved wall: an analytic circular arc (Arcade Clinic's arc wall path), stored in fixed-point millimetres and
/// millidegrees so every client and the authority agree exactly. A sweep of 360 000 is a full ring. The authoritative shape
/// is the arc itself; render meshes, collision boxes and room areas are all derived from it and never saved.
/// Angles run from +X toward +Z. <see cref="Profile"/> is full (3 m), glazed (a continuous curved window band), half
/// (a 1 m low wall) or counter (a 1.05 m reception or nursing-station counter with a worktop on the inner side).
/// </summary>
public sealed record CurvedWall(string WallId, int Level, int CenterXMillimeters, int CenterZMillimeters, int RadiusMillimeters,
    int StartMilliDegrees, int SweepMilliDegrees, string Style = ModuleCatalog.DefaultExteriorStyle, string Profile = "full",
    bool ExteriorOutside = true, CurvedOpening[]? Openings = null)
{
    public const int FullCircleMilliDegrees = 360_000;
    public bool IsFullCircle => SweepMilliDegrees == FullCircleMilliDegrees;
    public IReadOnlyList<CurvedOpening> HostedOpenings => Openings ?? [];
    public float CenterX => CenterXMillimeters / 1000f;
    public float CenterZ => CenterZMillimeters / 1000f;
    public float Radius => RadiusMillimeters / 1000f;
    public double StartRadians => StartMilliDegrees / 1000.0 * Math.PI / 180;
    public double SweepRadians => SweepMilliDegrees / 1000.0 * Math.PI / 180;
    public float ArcLengthMeters => (float)(Radius * SweepRadians);
    /// <summary>Every saved parameter, openings included, as one stable string.</summary>
    public string Fingerprint => $"{WallId}/{Level}/{CenterXMillimeters}/{CenterZMillimeters}/{RadiusMillimeters}/{StartMilliDegrees}/{SweepMilliDegrees}/{Style}/{Profile}/{ExteriorOutside}/"
        + string.Join(',', HostedOpenings.Select(o => o.Kind + "@" + o.CenterMilliDegrees));
    /// <summary>Wall top above the level base.</summary>
    public float TopMeters => CurvedWalls.ProfileTop(Profile);
}

/// <summary>Rules and derived geometry for <see cref="CurvedWall"/>s, shared by the authority, the host and every client.</summary>
public static class CurvedWalls
{
    public const int MinimumRadiusMillimeters = 1500, MaximumRadiusMillimeters = 24_000;
    public const int MinimumSweepMilliDegrees = 15_000;
    public const int PositionQuantumMillimeters = 50, AngleQuantumMilliDegrees = 1000;
    public const int MaximumPerLot = 64, MaximumOpenings = 16;
    public const float Thickness = .2f, Cladding = .03f, EndMarginMeters = .3f, OpeningGapMeters = .3f, SegmentMeters = 2.4f;
    public static readonly IReadOnlyList<string> Profiles = ["full", "glazed", "half", "counter"];

    public static float ProfileTop(string profile) => profile switch { "half" => LotGeometry.FloorThickness + 1.0f, "counter" => LotGeometry.FloorThickness + .95f, _ => LotGeometry.StoryMeters };

    /// <summary>A point on the arc at <paramref name="radius"/>, <paramref name="angle"/> radians from +X toward +Z.</summary>
    public static (float X, float Z) Point(CurvedWall wall, double angle, float radius) =>
        (wall.CenterX + radius * (float)Math.Cos(angle), wall.CenterZ + radius * (float)Math.Sin(angle));

    /// <summary>An opening's angular range relative to the wall's start, in radians.</summary>
    public static (double From, double To) Range(CurvedWall wall, CurvedOpening opening)
    {
        var spec = ModuleCatalog.Opening(opening.Kind)!;
        double centre = opening.CenterMilliDegrees / 1000.0 * Math.PI / 180, half = spec.WidthMeters / 2 / wall.Radius;
        return (centre - half, centre + half);
    }

    /// <summary>Why this curved wall cannot be built on the lot, or null.</summary>
    public static (string Code, string Message)? Problem(CurvedWall wall, LotDeed lot)
    {
        if (string.IsNullOrWhiteSpace(wall.WallId) || wall.WallId.Length > 64) return ("invalid_curved_wall", "A curved wall needs an id.");
        if (wall.Level < 0) return ("invalid_level", "Build on the ground level or above.");
        if (wall.RadiusMillimeters is < MinimumRadiusMillimeters or > MaximumRadiusMillimeters)
            return ("invalid_radius", $"A curved wall's radius must be between {MinimumRadiusMillimeters / 1000.0:0.#} and {MaximumRadiusMillimeters / 1000} m.");
        if (wall.SweepMilliDegrees is < MinimumSweepMilliDegrees or > CurvedWall.FullCircleMilliDegrees)
            return ("invalid_sweep", "A curved wall must sweep between 15° and a full circle.");
        if (wall.StartMilliDegrees is < 0 or >= CurvedWall.FullCircleMilliDegrees) return ("invalid_sweep", "The start angle must be between 0° and 360°.");
        if (wall.CenterXMillimeters % PositionQuantumMillimeters != 0 || wall.CenterZMillimeters % PositionQuantumMillimeters != 0 || wall.RadiusMillimeters % PositionQuantumMillimeters != 0
            || wall.StartMilliDegrees % AngleQuantumMilliDegrees != 0 || wall.SweepMilliDegrees % AngleQuantumMilliDegrees != 0)
            return ("unquantized_geometry", "Curved walls snap to 5 cm and whole degrees.");
        if (ModuleCatalog.Style(wall.Style) is null) return ("invalid_style", "Unknown wall finish.");
        if (!Profiles.Contains(wall.Profile)) return ("invalid_profile", "Unknown curved wall profile.");

        float reach = wall.Radius + Thickness / 2 + Cladding + (wall.Profile == "counter" ? 0 : 0);
        float x0 = lot.MinimumTileX * BuildGrid.TileMeters, z0 = lot.MinimumTileZ * BuildGrid.TileMeters;
        float x1 = (lot.MaximumTileX + 1) * BuildGrid.TileMeters, z1 = (lot.MaximumTileZ + 1) * BuildGrid.TileMeters;
        int samples = Math.Max(8, (int)Math.Ceiling(wall.SweepRadians * reach / .25));
        for (int i = 0; i <= samples; i++)
        {
            var (x, z) = Point(wall, wall.StartRadians + wall.SweepRadians * i / samples, reach);
            if (x < x0 || x > x1 || z < z0 || z > z1) return ("outside_lot", "The whole curved wall must stand on your lot.");
        }

        var openings = wall.HostedOpenings;
        if (openings.Count > MaximumOpenings) return ("too_many_openings", $"A curved wall hosts at most {MaximumOpenings} openings.");
        var allowed = wall.Profile switch
        {
            "full" => ModuleCatalog.CurvedOpenings.Select(o => o.Id).ToHashSet(),
            "glazed" => ["door_wood", "door_halfglass", "passage"],
            _ => new HashSet<string> { "passage" },
        };
        double margin = EndMarginMeters / wall.Radius, gap = OpeningGapMeters / wall.Radius;
        var ranges = new List<(double From, double To)>();
        foreach (var opening in openings)
        {
            if (!allowed.Contains(opening.Kind)) return ("invalid_opening", $"A {wall.Profile} curved wall cannot host a {ModuleCatalog.Opening(opening.Kind)?.Name.ToLowerInvariant() ?? opening.Kind}.");
            var spec = ModuleCatalog.Opening(opening.Kind)!;
            // A straight door leaf spans the opening's chord; the chord must stay inside the wall's thickness.
            double sagitta = wall.Radius * (1 - Math.Cos(spec.WidthMeters / 2 / wall.Radius));
            if (sagitta > Thickness * .45) return ("opening_too_curved", $"This wall curves too tightly for a {spec.WidthMeters:0.0#} m {spec.Name.ToLowerInvariant()}; use a larger radius.");
            var r = Range(wall, opening);
            if (r.From < margin || r.To > wall.SweepRadians - margin) return ("opening_off_wall", "Openings must sit fully on the curved wall, clear of its ends.");
            if (ranges.Any(o => r.From < o.To + gap && o.From < r.To + gap)) return ("openings_overlap", "Openings on a curved wall need at least 30 cm between them.");
            ranges.Add(r);
        }
        return null;
    }

    /// <summary>
    /// Splits the wall into pieces for storm damage and drawing: each opening (with a margin) is its own piece, and the
    /// solid runs between are cut into pieces of at most <see cref="SegmentMeters"/> of arc. Ranges are relative radians.
    /// </summary>
    public static IReadOnlyList<(double From, double To)> Segments(CurvedWall wall)
    {
        double margin = .15 / wall.Radius, sweep = wall.SweepRadians;
        var openings = wall.HostedOpenings.Select(o => Range(wall, o)).Select(r => (From: Math.Max(0, r.From - margin), To: Math.Min(sweep, r.To + margin)))
            .OrderBy(r => r.From).ToList();
        if (wall.Profile == "glazed") openings.Clear();
        var result = new List<(double, double)>();
        double cursor = 0;
        void Run(double from, double to)
        {
            if (to - from < 1e-6) return;
            int n = Math.Max(1, (int)Math.Ceiling((to - from) * wall.Radius / SegmentMeters));
            for (int i = 0; i < n; i++) result.Add((from + (to - from) * i / n, from + (to - from) * (i + 1) / n));
        }
        foreach (var (from, to) in openings) { Run(cursor, from); result.Add((from, to)); cursor = to; }
        Run(cursor, sweep);
        return result;
    }

    /// <summary>Whether a footprint rectangle (world metres) crosses the wall's solid band anywhere outside a passable opening.</summary>
    public static bool Crosses(CurvedWall wall, float x0, float z0, float x1, float z1)
    {
        float inner = wall.Radius - Thickness / 2 - (wall.Profile == "counter" ? .45f : 0), outer = wall.Radius + Thickness / 2 + Cladding;
        float nearX = Math.Clamp(wall.CenterX, x0, x1), nearZ = Math.Clamp(wall.CenterZ, z0, z1);
        float near = MathF.Sqrt((nearX - wall.CenterX) * (nearX - wall.CenterX) + (nearZ - wall.CenterZ) * (nearZ - wall.CenterZ));
        float far = new[] { (x0, z0), (x1, z0), (x0, z1), (x1, z1) }.Max(p => MathF.Sqrt((p.Item1 - wall.CenterX) * (p.Item1 - wall.CenterX) + (p.Item2 - wall.CenterZ) * (p.Item2 - wall.CenterZ)));
        if (far < inner || near > outer) return false;
        var passable = wall.HostedOpenings.Where(o => ModuleCatalog.Opening(o.Kind)?.Passable == true).Select(o => Range(wall, o)).ToList();
        const float step = .1f;
        for (float x = x0; x <= x1 + 1e-4f; x += step)
            for (float z = z0; z <= z1 + 1e-4f; z += step)
            {
                float dx = x - wall.CenterX, dz = z - wall.CenterZ, d = MathF.Sqrt(dx * dx + dz * dz);
                if (d < inner || d > outer) continue;
                double rel = Math.Atan2(dz, dx) - wall.StartRadians;
                rel = ((rel % (2 * Math.PI)) + 2 * Math.PI) % (2 * Math.PI);
                if (rel > wall.SweepRadians) continue;
                if (passable.Any(r => rel >= r.From && rel <= r.To)) continue;
                return true;
            }
        return false;
    }
}

/// <summary>A round room: a closed curved ring with floor inside it. Found from the arc itself, not from a polygon.</summary>
public sealed record RoundRoom(string WallId, int Level, float CenterX, float CenterZ, float InteriorRadiusMeters, int Entrances, bool Floored)
{
    public float AreaSquareMeters => MathF.PI * InteriorRadiusMeters * InteriorRadiusMeters;
}

public static class RoundRoomFinder
{
    /// <summary>Full-circle, full-height curved walls on the level. A ring with no passable opening is still a room, just a closed one.</summary>
    public static IReadOnlyList<RoundRoom> Find(BuildingPlan plan, int level)
    {
        var floor = plan.Floors.Where(f => f.Level == level).Select(f => f.Tile).ToHashSet();
        var rooms = new List<RoundRoom>();
        foreach (var wall in plan.CurvedWalls.Where(w => w.Level == level && w.IsFullCircle && w.Profile is "full" or "glazed"))
        {
            float r = wall.Radius - CurvedWalls.Thickness / 2;
            bool floored = true;
            for (int z = (int)MathF.Floor((wall.CenterZ - r) / BuildGrid.TileMeters); z <= (int)MathF.Floor((wall.CenterZ + r) / BuildGrid.TileMeters) && floored; z++)
                for (int x = (int)MathF.Floor((wall.CenterX - r) / BuildGrid.TileMeters); x <= (int)MathF.Floor((wall.CenterX + r) / BuildGrid.TileMeters); x++)
                {
                    float nx = Math.Clamp(wall.CenterX, x * BuildGrid.TileMeters, (x + 1) * BuildGrid.TileMeters), nz = Math.Clamp(wall.CenterZ, z * BuildGrid.TileMeters, (z + 1) * BuildGrid.TileMeters);
                    if ((nx - wall.CenterX) * (nx - wall.CenterX) + (nz - wall.CenterZ) * (nz - wall.CenterZ) < r * r && !floor.Contains(new(x, z))) { floored = false; break; }
                }
            rooms.Add(new(wall.WallId, level, wall.CenterX, wall.CenterZ, r, wall.HostedOpenings.Count(o => ModuleCatalog.Opening(o.Kind)?.Passable == true), floored));
        }
        return rooms;
    }
}

/// <summary>Plain triangle mesh data (Y up, metres) with per-vertex linear colours, independent of any renderer.</summary>
public sealed class MeshBuffer
{
    public readonly List<Vector3> Positions = [], Normals = [];
    public readonly List<Vector2> Uvs = [];
    public readonly List<Vector4> Colors = [];
    public readonly List<int> Indices = [];
    public int VertexCount => Positions.Count;

    public int Vertex(Vector3 p, Vector3 n, Vector2 uv, (float R, float G, float B) c)
    {
        Positions.Add(p); Normals.Add(n); Uvs.Add(uv); Colors.Add(new(c.R, c.G, c.B, 1));
        return Positions.Count - 1;
    }

    /// <summary>Adds a triangle wound counter-clockwise about its vertices' normals (the glTF convention the modules use).</summary>
    public void Triangle(int a, int b, int c)
    {
        var n = Normals[a] + Normals[b] + Normals[c];
        if (Vector3.Dot(Vector3.Cross(Positions[b] - Positions[a], Positions[c] - Positions[a]), n) < 0) (b, c) = (c, b);
        Indices.Add(a); Indices.Add(b); Indices.Add(c);
    }

    public void Quad(int a, int b, int c, int d) { Triangle(a, b, c); Triangle(a, c, d); }

    /// <summary>A flat-shaded quad from four corners.</summary>
    public void Face(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 normal, (float, float, float) color)
    {
        int i = Vertex(a, normal, new(0, 0), color), j = Vertex(b, normal, new(1, 0), color), k = Vertex(c, normal, new(1, 1), color), l = Vertex(d, normal, new(0, 1), color);
        Quad(i, j, k, l);
    }

    /// <summary>An oriented box: centre, half extents along its local axes, and yaw about Y.</summary>
    public void Box(Vector3 center, Vector3 half, Vector3 axisX, (float, float, float) color)
    {
        axisX = Vector3.Normalize(axisX); var axisY = Vector3.UnitY; var axisZ = Vector3.Normalize(Vector3.Cross(axisX, axisY));
        Vector3 P(float sx, float sy, float sz) => center + axisX * (sx * half.X) + axisY * (sy * half.Y) + axisZ * (sz * half.Z);
        Face(P(-1, -1, 1), P(1, -1, 1), P(1, 1, 1), P(-1, 1, 1), axisZ, color);
        Face(P(1, -1, -1), P(-1, -1, -1), P(-1, 1, -1), P(1, 1, -1), -axisZ, color);
        Face(P(1, -1, 1), P(1, -1, -1), P(1, 1, -1), P(1, 1, 1), axisX, color);
        Face(P(-1, -1, -1), P(-1, -1, 1), P(-1, 1, 1), P(-1, 1, -1), -axisX, color);
        Face(P(-1, 1, 1), P(1, 1, 1), P(1, 1, -1), P(-1, 1, -1), axisY, color);
        Face(P(-1, -1, -1), P(1, -1, -1), P(1, -1, 1), P(-1, -1, 1), -axisY, color);
    }

    /// <summary>Re-expresses the mesh relative to <paramref name="origin"/> in a frame turned by <paramref name="yawDegrees"/>,
    /// so a render item at origin with that yaw (world = local * RotationY(yaw)) draws it back in place.</summary>
    public void ToLocal(Vector3 origin, float yawDegrees)
    {
        var inverse = Matrix4x4.CreateRotationY(-yawDegrees * MathF.PI / 180);
        for (int i = 0; i < Positions.Count; i++)
        {
            Positions[i] = Vector3.Transform(Positions[i] - origin, inverse);
            Normals[i] = Vector3.Normalize(Vector3.TransformNormal(Normals[i], inverse));
        }
    }
}

/// <summary>
/// Builds the visible mesh of one curved wall piece from its arc. Every face follows the curve: the inner and outer
/// faces are sampled at no more than 3° or 12 cm with radial (smooth) normals and shared vertices, lap siding courses,
/// battens and stones follow the arc, and openings are cut out of the curved surface with radial jambs. Only door leaves
/// are straight, across the opening's chord, as real doors in curved walls are.
/// </summary>
public static class CurvedWallMesher
{
    /// <summary>The mesh for a curved wall piece in its own frame: drawn at the piece's centre with the piece's yaw, and
    /// equally right when the piece is torn off and flies as debris with its body's rotation.</summary>
    public static MeshBuffer ForPiece(LotPiece piece)
    {
        var source = piece.Curve ?? throw new ArgumentException("Not a curved wall piece.", nameof(piece));
        var mesh = Build(source.Wall, source.From, source.To, source.BaseElevation);
        mesh.ToLocal(new(piece.CenterX, piece.CenterY, piece.CenterZ), piece.YawDegrees);
        return mesh;
    }

    private sealed record Cut(double From, double To, float Bottom, float Top, WallOpeningSpec Spec);

    public static MeshBuffer Build(CurvedWall wall, double from, double to, float baseElevation)
    {
        var mesh = new MeshBuffer();
        var style = ModuleCatalog.Style(wall.Style) ?? ModuleCatalog.Style(ModuleCatalog.DefaultExteriorStyle)!;
        var (siding, trim, skirt, drywall) = (ModuleCatalog.Color(style.Siding), ModuleCatalog.Color(style.Trim), ModuleCatalog.Color(style.Skirt), ModuleCatalog.Color("drywall"));
        float R = wall.Radius, half = CurvedWalls.Thickness / 2, side = wall.ExteriorOutside ? 1 : -1;
        float yBase = LotGeometry.LevelElevation(baseElevation, wall.Level), yFloor = yBase + LotGeometry.FloorThickness, yTop = yBase + wall.TopMeters;
        double start = wall.StartRadians;
        float Ext(float offset) => R + side * (half + offset);   // radius of an exterior layer `offset` beyond the core face
        float Int(float offset) => R - side * (half + offset);

        // Openings inside this piece, and the glazed profile's continuous window band.
        var cuts = new List<Cut>();
        foreach (var o in wall.HostedOpenings)
        {
            var spec = ModuleCatalog.Opening(o.Kind)!; var r = CurvedWalls.Range(wall, o);
            if (r.From >= from - 1e-6 && r.To <= to + 1e-6)
                cuts.Add(new(r.From, r.To, spec.Passable ? yFloor : yFloor + spec.SillMeters, yFloor + spec.SillMeters + spec.HeightMeters, spec));
        }
        if (wall.Profile == "glazed" && cuts.Count == 0)
        {
            double mullion = .05 / R;
            cuts.Add(new(from + mullion, to - mullion, yFloor + .9f, yTop - .35f, new("band", "Glazing", 0, 0, .9f, 0, false, Glazed: true)));
        }

        // Sample angles: fine enough to read as a true curve, and exact at every opening edge.
        double step = Math.Min(3 * Math.PI / 180, .12 / R);
        var keys = new SortedSet<double> { from, to };
        foreach (var c in cuts) { keys.Add(c.From); keys.Add(c.To); }
        var key = keys.ToArray();

        IEnumerable<double> Samples(double a, double b)
        {
            int n = Math.Max(1, (int)Math.Ceiling((b - a) / step));
            for (int i = 0; i <= n; i++) yield return a + (b - a) * i / n;
        }
        Vector3 At(double rel, float radius, float y) { double a = start + rel; return new(wall.CenterX + radius * (float)Math.Cos(a), y, wall.CenterZ + radius * (float)Math.Sin(a)); }
        Vector3 Radial(double rel) { double a = start + rel; return new((float)Math.Cos(a), 0, (float)Math.Sin(a)); }
        Vector3 Tangent(double rel) { double a = start + rel; return new(-(float)Math.Sin(a), 0, (float)Math.Cos(a)); }

        // A curved band between two heights; the radius may differ top and bottom (lap siding leans).
        void Band(double a, double b, float y0, float y1, float r0, float r1, Vector3 normalSign, (float, float, float) color)
        {
            if (b - a < 1e-7 || y1 - y0 < 1e-4) return;
            int prev0 = -1, prev1 = -1;
            foreach (double t in Samples(a, b))
            {
                var n = Radial(t) * normalSign.X; float u = (float)(t * R);
                int i0 = mesh.Vertex(At(t, r0, y0), n, new(u, y0), color), i1 = mesh.Vertex(At(t, r1, y1), n, new(u, y1), color);
                if (prev0 >= 0) mesh.Quad(prev0, i0, i1, prev1);
                prev0 = i0; prev1 = i1;
            }
        }
        // A flat ring sector at one height between two radii, facing up or down.
        void Ring(double a, double b, float y, float r0, float r1, bool up, (float, float, float) color)
        {
            if (b - a < 1e-7 || MathF.Abs(r1 - r0) < 1e-4) return;
            var n = up ? Vector3.UnitY : -Vector3.UnitY; int p0 = -1, p1 = -1;
            foreach (double t in Samples(a, b))
            {
                var v0 = At(t, r0, y); var v1 = At(t, r1, y);
                int i0 = mesh.Vertex(v0, n, new(v0.X, v0.Z), color), i1 = mesh.Vertex(v1, n, new(v1.X, v1.Z), color);
                if (p0 >= 0) mesh.Quad(p0, i0, i1, p1);
                p0 = i0; p1 = i1;
            }
        }
        // A radial face at one angle (wall ends, jambs), facing along the tangent.
        void Radial2(double t, float y0, float y1, float r0, float r1, float facing, (float, float, float) color) =>
            mesh.Face(At(t, r0, y0), At(t, r1, y0), At(t, r1, y1), At(t, r0, y1), Tangent(t) * facing, color);

        var outward = new Vector3(side, 0, 0); var inward = new Vector3(-side, 0, 0);
        float skirtTop = yFloor + .12f, friezeBottom = yTop - .16f;
        bool tall = wall.Profile is "full" or "glazed";

        // Solid spans: for each interval between key angles, the heights the wall occupies there.
        for (int k = 0; k + 1 < key.Length; k++)
        {
            double a = key[k], b = key[k + 1], mid = (a + b) / 2;
            var cut = cuts.FirstOrDefault(c => mid > c.From && mid < c.To);
            var spans = cut is null ? [(yBase, yTop)] : new List<(float, float)> { (yBase, cut.Bottom), (cut.Top, yTop) };
            foreach (var (s0, s1) in spans.Where(s => s.Item2 - s.Item1 > 1e-3))
            {
                // Interior face: plain drywall above the slab.
                Band(a, b, MathF.Max(s0, yFloor), s1, Int(0), Int(0), inward, drywall);
                // Exterior: skirt over the slab edge, cladding, and a frieze board under the eaves.
                float cladFrom = MathF.Max(s0, skirtTop), cladTo = tall ? MathF.Min(s1, friezeBottom) : s1;
                if (s0 < skirtTop)
                {
                    float top = MathF.Min(s1, cut?.Spec.Passable == true ? yFloor : skirtTop);
                    Band(a, b, s0, top, Ext(.025f), Ext(.025f), outward, skirt);
                    Ring(a, b, top, Ext(0), Ext(.025f), true, skirt);
                }
                if (cladTo > cladFrom) Clad(a, b, cladFrom, cladTo);
                if (tall && s1 > friezeBottom)
                {
                    float bottom = MathF.Max(s0, friezeBottom);
                    Band(a, b, bottom, s1, Ext(.025f), Ext(.025f), outward, trim);
                    Ring(a, b, bottom, Ext(0), Ext(.025f), false, trim);
                }
            }
            if (cut is not null) Opening(cut, a, b);
        }

        // Top: the wall's cap, a rail on a half wall, or a worktop on a counter.
        if (wall.Profile == "counter")
        {
            float inner = Int(.45f), outer = Ext(.06f), t = .04f;
            Ring(from, to, yTop + t, inner, outer, true, ModuleCatalog.Color("counter_top"));
            Ring(from, to, yTop, inner, outer, false, ModuleCatalog.Color("counter_top"));
            Band(from, to, yTop, yTop + t, inner, inner, inward, ModuleCatalog.Color("counter_top"));
            Band(from, to, yTop, yTop + t, outer, outer, outward, ModuleCatalog.Color("counter_top"));
            Band(from, to, yTop - .35f, yTop, Int(.02f), Int(.02f), inward, ModuleCatalog.Color("wood_dark"));   // knee-space front panel
        }
        else if (wall.Profile == "half")
        {
            float inner = Int(.03f), outer = Ext(.06f), t = .05f;
            Ring(from, to, yTop + t, inner, outer, true, trim);
            Ring(from, to, yTop, inner, outer, false, trim);
            Band(from, to, yTop, yTop + t, inner, inner, inward, trim);
            Band(from, to, yTop, yTop + t, outer, outer, outward, trim);
        }
        else Ring(from, to, yTop, Int(0), Ext(.025f), true, trim);
        Ring(from, to, yBase, Int(0), Ext(.025f), false, trim);

        // Piece ends: closed so a torn-off piece is a solid curved panel.
        float capTop = yTop + wall.Profile switch { "half" => .05f, "counter" => .04f, _ => 0 };
        // A real end of an arc gets a full-depth cap; where two pieces meet the caps stay inside the wall's core, so the
        // ring reads as one continuous surface (they still close a torn-off piece).
        bool startIsEnd = !wall.IsFullCircle && from <= 1e-9, endIsEnd = !wall.IsFullCircle && to >= wall.SweepRadians - 1e-9;
        Radial2(from, yBase, startIsEnd ? capTop : yTop, Int(startIsEnd ? 0 : .002f), startIsEnd ? Ext(.025f) : Ext(-.002f), -1, startIsEnd ? trim : drywall);
        Radial2(to, yBase, endIsEnd ? capTop : yTop, Int(endIsEnd ? 0 : .002f), endIsEnd ? Ext(.025f) : Ext(-.002f), 1, endIsEnd ? trim : drywall);
        return mesh;

        void Clad(double a, double b, float y0, float y1)
        {
            switch (style.Pattern)
            {
                case "lap":
                    for (int course = (int)MathF.Floor((y0 - yFloor) / .2f); yFloor + course * .2f < y1 - 1e-4f; course++)
                    {
                        float ca = yFloor + course * .2f, ya = MathF.Max(y0, ca), yb = MathF.Min(y1, ca + .2f);
                        if (yb - ya < 1e-3f) continue;
                        float Lean(float y) => Ext(.012f - .022f * (y - ca) / .2f);   // each board's bottom edge stands proud
                        Band(a, b, ya, yb, Lean(ya), Lean(yb), outward, siding);
                        if (MathF.Abs(ya - ca) < 1e-4f) Ring(a, b, ya, Ext(-.01f), Lean(ya), false, siding);
                    }
                    break;
                case "batten":
                    Band(a, b, y0, y1, Ext(0), Ext(0), outward, siding);
                    double pitch = .4 / R, hw = .03 / R;
                    for (double c = Math.Ceiling((a - hw) / pitch) * pitch; c - hw < b; c += pitch)
                    {
                        double ba = Math.Max(a, c - hw), bb = Math.Min(b, c + hw);
                        if (bb - ba < 1e-6) continue;
                        Band(ba, bb, y0, y1, Ext(.02f), Ext(.02f), outward, siding);
                        Radial2(ba, y0, y1, Ext(0), Ext(.02f), -1, siding);
                        Radial2(bb, y0, y1, Ext(0), Ext(.02f), 1, siding);
                    }
                    break;
                case "stone":
                    Band(a, b, y0, y1, Ext(-.006f), Ext(-.006f), outward, ModuleCatalog.Color("mortar"));
                    for (int course = (int)MathF.Floor((y0 - yFloor) / .25f); yFloor + course * .25f < y1 - 1e-4f; course++)
                    {
                        float ca = yFloor + course * .25f, ya = MathF.Max(y0, ca) + .008f, yb = MathF.Min(y1, ca + .25f) - .008f;
                        if (yb - ya < .02f) continue;
                        var rng = new Random(HashCode.Combine(wall.WallId.GetHashCode(StringComparison.Ordinal), course));
                        double s = -rng.NextDouble() * .4;
                        while (s / R < wall.SweepRadians + .01)
                        {
                            double w = .3 + rng.NextDouble() * .35, depth = rng.NextDouble() * .018;
                            double sa = Math.Max(a, s / R + .008 / R), sb = Math.Min(b, (s + w) / R - .008 / R);
                            if (sb - sa > .02 / R)
                            {
                                float r = Ext((float)depth);
                                Band(sa, sb, ya, yb, r, r, outward, siding);
                                Ring(sa, sb, yb, Ext(-.006f), r, true, siding);
                                Ring(sa, sb, ya, Ext(-.006f), r, false, siding);
                                Radial2(sa, ya, yb, Ext(-.006f), r, -1, siding);
                                Radial2(sb, ya, yb, Ext(-.006f), r, 1, siding);
                            }
                            s += w;
                        }
                    }
                    break;
                default:
                    Band(a, b, y0, y1, Ext(0), Ext(0), outward, siding);
                    break;
            }
        }

        void Opening(Cut c, double a, double b)
        {
            // Radial jambs, head and (for windows) a sill: the wall's thickness shows around the curved opening.
            Radial2(a, c.Bottom, c.Top, Int(0), Ext(.03f), 1, trim);
            Radial2(b, c.Bottom, c.Top, Int(0), Ext(.03f), -1, trim);
            Ring(a, b, c.Top, Int(0), Ext(.03f), false, trim);
            if (!c.Spec.Passable) Ring(a, b, c.Bottom, Int(.04f), Ext(.07f), true, trim);
            // Exterior casing following the curve.
            double cw = .1 / R;
            if (c.Spec.Id != "band")
            {
                Band(a - cw, a, c.Bottom, c.Top + .1f, Ext(.035f), Ext(.035f), outward, trim);
                Band(b, b + cw, c.Bottom, c.Top + .1f, Ext(.035f), Ext(.035f), outward, trim);
                Band(a - cw, b + cw, c.Top, c.Top + .1f, Ext(.036f), Ext(.036f), outward, trim);
                Ring(a - cw, b + cw, c.Top + .1f, Ext(0), Ext(.036f), true, trim);
            }
            if (c.Spec.Glazed && !c.Spec.Passable)
            {
                // Curved glass on the wall's centreline, framed by curved rails and radial mullions.
                Band(a, b, c.Bottom, c.Top, R + .004f, R + .004f, outward, ModuleCatalog.Color("glass"));
                Band(a, b, c.Bottom, c.Top, R - .004f, R - .004f, inward, ModuleCatalog.Color("glass"));
                Band(a, b, c.Bottom, c.Bottom + .05f, R + .03f, R + .03f, outward, trim);
                Band(a, b, c.Top - .05f, c.Top, R + .03f, R + .03f, outward, trim);
                if (c.Spec.Id == "win_large") Band(a, b, (c.Bottom + c.Top) / 2 - .025f, (c.Bottom + c.Top) / 2 + .025f, R + .03f, R + .03f, outward, trim);
                int bars = Math.Max(0, (int)Math.Round((b - a) * R / .6) - 1);
                for (int i = 1; i <= bars; i++)
                {
                    double t = a + (b - a) * i / (bars + 1);
                    mesh.Box(At(t, R, (c.Bottom + c.Top) / 2), new(.02f, (c.Top - c.Bottom) / 2, .035f), Tangent(t), trim);
                }
            }
            if (c.Spec.Passable && c.Spec.Id != "passage")
            {
                // A real door is straight: its leaf spans the opening's chord, set inside the wall's thickness.
                var p0 = At(a + .02 / R, R, 0); var p1 = At(b - .02 / R, R, 0);
                var along = Vector3.Normalize(p1 - p0); float len = Vector3.Distance(p0, p1) / 2; var mid = (p0 + p1) / 2;
                float leafTop = c.Top - .03f, h = (leafTop - yFloor) / 2;
                var door = ModuleCatalog.Color(style.Id == "lap_white" ? "door_green" : "door_wood");
                if (c.Spec.Glazed)
                {
                    float split = yFloor + 1.0f;
                    mesh.Box(new(mid.X, (yFloor + split) / 2, mid.Z), new(len, (split - yFloor) / 2, .0225f), along, door);
                    mesh.Box(new(mid.X, (split + leafTop) / 2, mid.Z), new(len, (leafTop - split) / 2, .012f), along, ModuleCatalog.Color("glass"));
                    foreach (float s in new[] { -1f, 1f })
                        mesh.Box(new Vector3(mid.X, (split + leafTop) / 2, mid.Z) + along * s * (len - .05f), new(.05f, (leafTop - split) / 2, .0225f), along, door);
                    mesh.Box(new(mid.X, leafTop - .05f, mid.Z), new(len, .05f, .0225f), along, door);
                }
                else mesh.Box(new(mid.X, yFloor + h, mid.Z), new(len, h, .0225f), along, door);
                var knob = mid + along * (len - .08f);
                var normal = Vector3.Normalize(Vector3.Cross(along, Vector3.UnitY));
                foreach (float s in new[] { -1f, 1f })
                    mesh.Box(new Vector3(knob.X, yFloor + .98f, knob.Z) + normal * s * .05f, new(.025f, .025f, .03f), along, ModuleCatalog.Color("brass"));
            }
        }
    }
}
