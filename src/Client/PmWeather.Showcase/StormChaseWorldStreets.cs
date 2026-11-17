using System.Numerics;
using Engine.Rendering.Assets;
using Shared.Contracts.Content;
using Shared.Contracts.Rendering;
using Shared.Contracts.World;

/// <summary>
/// Client-only street furniture and distribution lines: stop signs and street-name signs at town
/// intersections, cobra-head street lights along town streets and wooden pole lines with sagging
/// conductors along the regional roads. All of it is static, Blender-authored
/// (tools/storm-chase-models/build_street_furniture.py) and merged into per-area, per-material
/// world-space meshes, so thousands of props cost a few dozen render items. The authoritative server
/// never builds these and they are not part of the world fingerprint.
/// </summary>
internal sealed partial class StormChaseWorld
{
    private const float PropCellSize = 1024;
    private static readonly string[] AvenueNames = ["MAIN ST","1ST AVE","2ND AVE","3RD AVE","4TH AVE","5TH AVE","6TH AVE","7TH AVE","8TH AVE","9TH AVE"];
    private static readonly string[] TreeStreetNames = ["OAK ST","ELM ST","PINE ST","MAPLE ST","CEDAR ST","ASH ST","BIRCH ST","WALNUT ST","HICKORY ST","CHERRY ST"];
    private static readonly string[] HarborCrossNames = ["PELICAN ST","TARPON ST","MARINA ST"];
    private readonly Dictionary<(string Group,int X,int Z,string Material),PropBatch> propBatches = [];
    private readonly Dictionary<AssetHandle,(Vector3[] Positions,Vector3[] Normals,Vector2[] Uvs,uint[] Indices)> weldedParts = [];

    public int StopSignCount { get; private set; }
    public int StreetNameSignCount { get; private set; }
    public int StreetLightCount { get; private set; }
    public int DistributionPoleCount { get; private set; }
    public int StreetPropItemCount => streetProps.Count;
    public long StreetPropVertexCount { get; private set; }

    private sealed class PropBatch
    {
        public readonly List<RenderMeshVertex> Vertices = [];
        public readonly List<uint> Indices = [];
    }
    private sealed record TownStreet(string Name, string Sign, Vector2[] Points, float HalfWidth, bool Major, int Side);
    private readonly record struct Intersection(Vector2 Point, TownStreet Major, Vector2 MajorDirection, TownStreet Minor, Vector2 MinorDirection,
        float MinorBefore, float MinorAfter);

    partial void BuildStreetFurniture()
    {
        // A/B switch for performance comparisons (mirrors MMOI_STORM_CHASE_NO_VEGETATION).
        if (streetProps.Count > 0 || Environment.GetEnvironmentVariable("MMOI_STORM_CHASE_NO_STREET_PROPS") == "1") return;
        var streets = TownStreets();
        var intersections = FindIntersections(streets);
        PlaceIntersectionSigns(intersections);
        PlaceStreetLights(streets, intersections);
        PlaceDistributionLines(streets);
        PlaceJunctionPatches(intersections);
        PlaceEdgeLines(streets, intersections);
        FlushProps();
    }

    // ------------------------------------------------------------------ town streets and intersections
    private List<TownStreet> TownStreets()
    {
        var result = new List<TownStreet>();
        int harborCross = 0;
        foreach (var route in roadRoutes)
        {
            string name = route.Name;
            string? sign = null; bool major = false;
            int Index() => int.Parse(name[(name.LastIndexOf(' ') + 1)..], System.Globalization.CultureInfo.InvariantCulture);
            if (name.StartsWith("Meridian east-west ", StringComparison.Ordinal)) { sign = AvenueNames[Index() % 10]; major = true; }
            else if (name.StartsWith("Meridian north-south ", StringComparison.Ordinal)) sign = TreeStreetNames[Index() % 10];
            else if (name.Contains(" cross street ", StringComparison.Ordinal) && !name.StartsWith("Gull", StringComparison.Ordinal)) { sign = AvenueNames[Index() + 2]; major = true; }
            else if (name.Contains(" side street ", StringComparison.Ordinal)) sign = TreeStreetNames[Index() + 2];
            else if (name.Contains(" avenue ", StringComparison.Ordinal)) { sign = AvenueNames[Index() + 2]; major = true; }
            else if (name.Contains(" street ", StringComparison.Ordinal) && !name.StartsWith("Gull", StringComparison.Ordinal)) sign = TreeStreetNames[Index() + 2];
            else if (name == "Gull Harbor front street") { sign = "BEACH RD"; major = true; }
            else if (name == "Gull Harbor back street") { sign = "GULF AVE"; major = true; }
            else if (name.StartsWith("Gull Harbor cross street", StringComparison.Ordinal)) sign = HarborCrossNames[harborCross++ % 3];
            if (sign is null || !roadCenterlines.TryGetValue(name, out var line)) continue;
            // The actual ribbon centre line, decimated to ~40 m: town streets are straight or gently curved.
            var points = new List<Vector2>();
            for (int i = 0; i < line.Samples.Count; i += 5) points.Add(new(line.Samples[i].Position.X, line.Samples[i].Position.Z));
            var last = line.Samples[^1].Position;
            if (Vector2.Distance(points[^1], new(last.X, last.Z)) > .5f) points.Add(new(last.X, last.Z));
            result.Add(new(name, sign, points.ToArray(), line.Width / 2, major, result.Count % 2 == 0 ? 1 : -1));
        }
        return result;
    }

    private static List<Intersection> FindIntersections(List<TownStreet> streets)
    {
        var found = new List<Intersection>();
        foreach (var a in streets.Where(s => s.Major))
        foreach (var b in streets.Where(s => !s.Major))
        {
            float bLength = 0;
            for (int j = 0; j < b.Points.Length - 1; j++)
            {
                float aLength = 0;
                for (int i = 0; i < a.Points.Length - 1; i++)
                {
                    var p = a.Points[i]; var r = a.Points[i + 1] - p; var q = b.Points[j]; var s = b.Points[j + 1] - q;
                    float denominator = Cross(r, s);
                    if (MathF.Abs(denominator) > 1e-3f)
                    {
                        float t = Cross(q - p, s) / denominator, u = Cross(q - p, r) / denominator;
                        if (t >= -.02f && t <= 1.02f && u >= -.02f && u <= 1.02f)
                        {
                            var point = p + r * Math.Clamp(t, 0, 1);
                            if (!found.Any(f => Vector2.DistanceSquared(f.Point, point) < 100))
                            {
                                float before = bLength + s.Length() * Math.Clamp(u, 0, 1);
                                float after = PolylineLength(b.Points) - before;
                                found.Add(new(point, a, Vector2.Normalize(r), b, Vector2.Normalize(s), before, after));
                            }
                        }
                    }
                    aLength += r.Length();
                }
                bLength += (b.Points[j + 1] - b.Points[j]).Length();
            }
        }
        return found;
    }

    private void PlaceIntersectionSigns(List<Intersection> intersections)
    {
        foreach (var x in intersections)
        {
            // Two-way stop: the minor street stops for the avenue. A stop sign stands on the
            // right of each approach that exists, set back behind the avenue's shoulder.
            foreach (float direction in new[] { 1f, -1f })
            {
                var travel = x.MinorDirection * direction;   // traffic heading into the junction
                float approach = direction > 0 ? x.MinorBefore : x.MinorAfter;
                if (approach < 18) continue;
                var right = new Vector2(travel.Y, -travel.X);
                var at = x.Point - travel * (x.Major.HalfWidth + 4.5f) + right * (x.Minor.HalfWidth + 2.8f);
                if (!PropSiteClear(at, .3f)) continue;
                Prop("street", "stop-sign", at, MathF.Atan2(-travel.X, -travel.Y));
                StopSignCount++;
            }
            // Street-name sign on the first clear corner, blades aligned with their streets.
            foreach (var (sa, sb) in new[] { (1f, 1f), (-1f, 1f), (1f, -1f), (-1f, -1f) })
            {
                var at = x.Point + x.MajorDirection * sa * (x.Minor.HalfWidth + 3.2f) + x.MinorDirection * sb * (x.Major.HalfWidth + 3.2f);
                if (!PropSiteClear(at, .3f)) continue;
                Prop("street", "street-name-post", at, 0);
                Prop("street", "street-blade-" + Slug(x.Major.Sign), at, MathF.Atan2(-x.MajorDirection.Y, x.MajorDirection.X), 2.95f);
                Prop("street", "street-blade-" + Slug(x.Minor.Sign), at, MathF.Atan2(-x.MinorDirection.Y, x.MinorDirection.X), 3.17f);
                StreetNameSignCount++;
                break;
            }
        }
    }

    private void PlaceStreetLights(List<TownStreet> streets, List<Intersection> intersections)
    {
        const float spacing = 60;
        foreach (var street in streets)
        {
            float next = spacing * .5f, travelled = 0;
            for (int i = 0; i < street.Points.Length - 1; i++)
            {
                var a = street.Points[i]; var d = street.Points[i + 1] - a; float length = d.Length();
                if (length < .01f) continue;
                var unit = d / length; var outward = new Vector2(unit.Y, -unit.X) * street.Side;
                while (next <= travelled + length)
                {
                    var centre = a + unit * (next - travelled);
                    next += spacing;
                    if (intersections.Any(x => Vector2.DistanceSquared(x.Point, centre) < 18 * 18)) continue;
                    var at = centre + outward * (street.HalfWidth + 2.75f);
                    if (!PropSiteClear(at, .35f)) continue;
                    var toward = -outward;
                    Prop("street", "street-light", at, MathF.Atan2(toward.X, toward.Y));
                    float ground = HeightAt(at.X, at.Y);
                    var lamp = new Vector3f(at.X + toward.X * 2.2f, ground + 8.95f, at.Y + toward.Y * 2.2f);
                    var cell = ((int)MathF.Floor(lamp.X / 256), (int)MathF.Floor(lamp.Z / 256));
                    if (!streetLamps.TryGetValue(cell, out var bucket)) streetLamps.Add(cell, bucket = []);
                    bucket.Add(lamp);
                    StreetLightCount++;
                }
                travelled += length;
            }
        }
    }

    // ------------------------------------------------------------------ road surface detail
    public int JunctionPatchCount { get; private set; }
    public float EdgeLineMeters { get; private set; }

    /// <summary>
    /// A terrain-following asphalt patch over each town junction hides the two crossing ribbons'
    /// coplanar overlap and the avenue's centre dashes; white stop bars mark each stopping approach.
    /// </summary>
    private void PlaceJunctionPatches(List<Intersection> intersections)
    {
        foreach (var x in intersections)
        {
            var uA = x.MajorDirection; var uB = x.MinorDirection;
            float a = x.Minor.HalfWidth + .6f, b = x.Major.HalfWidth + .6f;
            const int n = 4;
            var batch = Batch("road", x.Point.X, x.Point.Y, "chase/asphalt");
            uint first = (uint)batch.Vertices.Count;
            for (int j = 0; j <= n; j++)
            for (int i = 0; i <= n; i++)
            {
                var p = x.Point + uA * (-a + 2 * a * i / n) + uB * (-b + 2 * b * j / n);
                batch.Vertices.Add(new(new(p.X, HeightAt(p.X, p.Y) + .42f, p.Y), new(1, 1, 1, 1), p.X / 8, p.Y / 8, new(0, 1, 0), new(uA.X, 0, uA.Y)));
            }
            bool upward = Cross(uA, uB) > 0;   // keep the front face up whatever the crossing angle
            for (int j = 0; j < n; j++)
            for (int i = 0; i < n; i++)
            {
                uint v = first + (uint)(j * (n + 1) + i), r = v + 1, u = v + n + 1, ur = u + 1;
                if (upward) batch.Indices.AddRange([v, u, r, r, u, ur]); else batch.Indices.AddRange([v, r, u, r, ur, u]);
            }
            JunctionPatchCount++;
            foreach (float direction in new[] { 1f, -1f })
            {
                float approach = direction > 0 ? x.MinorBefore : x.MinorAfter;
                if (approach < 18) continue;
                var travel = uB * direction; var right = new Vector2(travel.Y, -travel.X);
                // Stop bar across the right-hand lane, just short of the avenue's edge.
                var centre = x.Point - travel * (x.Major.HalfWidth + 1.4f) + right * (x.Minor.HalfWidth * .5f);
                Stripe(centre, right, x.Minor.HalfWidth * .5f - .2f, travel, .25f, .46f);
            }
        }
    }

    /// <summary>Solid white edge lines along every paved regional road (town streets and gravel roads have none).</summary>
    private void PlaceEdgeLines(List<TownStreet> streets, List<Intersection> intersections)
    {
        var townNames = streets.Select(s => s.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var (name, line) in roadCenterlines.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (townNames.Contains(name) || gravelRoads.Contains(name) || line.Width < 9.5f) continue;
            foreach (float side in new[] { -1f, 1f })
            {
                float offset = side * (line.Width / 2 - .45f);
                TerrainPathSampleLike? previous = null;
                for (int i = 0; i < line.Samples.Count; i++)
                {
                    var s = line.Samples[i];
                    var at = new Vector3f(s.Position.X + s.Binormal.X * offset, s.Position.Y + s.Binormal.Y * offset + .07f, s.Position.Z + s.Binormal.Z * offset);
                    bool keep = StormChaseCoast.SeawardDistance(at.X, at.Z) < -RoadShoreClearance - 4
                        && !intersections.Any(x => Vector2.DistanceSquared(x.Point, new(at.X, at.Z)) < 22 * 22);
                    if (!keep) { previous = null; continue; }
                    var current = new TerrainPathSampleLike(at, new(s.Binormal.X, 0, s.Binormal.Z));
                    if (previous is { } p)
                    {
                        var batch = Batch("edge", (p.Position.X + at.X) * .5f, (p.Position.Z + at.Z) * .5f, "chase/edge-line");
                        uint first = (uint)batch.Vertices.Count;
                        foreach (var (q, w) in new[] { (p, -.07f), (p, .07f), (current, .07f), (current, -.07f) })
                        {
                            var b = Vector3.Normalize(new(q.Binormal.X, 0, q.Binormal.Z));
                            batch.Vertices.Add(new(new(q.Position.X + b.X * w, q.Position.Y, q.Position.Z + b.Z * w), new(1, 1, 1, 1), 0, 0, new(0, 1, 0), new(1, 0, 0)));
                        }
                        // Both windings: the binormal's handedness varies by authored direction.
                        batch.Indices.AddRange([first, first + 1, first + 2, first, first + 2, first + 3, first, first + 2, first + 1, first, first + 3, first + 2]);
                        EdgeLineMeters += MathF.Sqrt(Square(at.X - p.Position.X) + Square(at.Z - p.Position.Z));
                    }
                    previous = current;
                }
            }
        }
    }

    private readonly record struct TerrainPathSampleLike(Vector3f Position, Vector3 Binormal);

    private void Stripe(Vector2 centre, Vector2 across, float halfLength, Vector2 along, float halfWidth, float lift)
    {
        var batch = Batch("road", centre.X, centre.Y, "chase/edge-line");
        uint first = (uint)batch.Vertices.Count;
        foreach (var (u, v) in new[] { (-1f, -1f), (1f, -1f), (1f, 1f), (-1f, 1f) })
        {
            var p = centre + across * (u * halfLength) + along * (v * halfWidth);
            batch.Vertices.Add(new(new(p.X, HeightAt(p.X, p.Y) + lift, p.Y), new(1, 1, 1, 1), 0, 0, new(0, 1, 0), new(across.X, 0, across.Y)));
        }
        batch.Indices.AddRange([first, first + 1, first + 2, first, first + 2, first + 3, first, first + 2, first + 1, first, first + 3, first + 2]);
    }

    // ------------------------------------------------------------------ distribution lines
    private void PlaceDistributionLines(List<TownStreet> streets)
    {
        var townNames = streets.Select(s => s.Name).ToHashSet(StringComparer.Ordinal);
        // The prairie highway already carries the destructible demo line (BuildPowerLines); the lake
        // road and farm lane sit in the opening chase view.
        var skip = new HashSet<string>(StringComparer.Ordinal) { "highway", "lake-road", "farm-lane" };
        const float spacing = 82;
        foreach (var (name, line) in roadCenterlines.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (townNames.Contains(name) || skip.Contains(name)) continue;
            float offset = line.Width / 2 + 7.5f, travelled = 0, next = spacing * .5f;
            (Vector3f Base, float Yaw, bool Valid)? previous = null;
            int number = 0;
            for (int i = 1; i < line.Samples.Count; i++)
            {
                var s0 = line.Samples[i - 1].Position; var s1 = line.Samples[i].Position;
                float step = MathF.Sqrt(Square(s1.X - s0.X) + Square(s1.Z - s0.Z));
                travelled += step;
                if (travelled < next) continue;
                next += spacing;
                var sample = line.Samples[i];
                float tx = sample.Tangent.X, tz = sample.Tangent.Z, tl = MathF.Sqrt(tx * tx + tz * tz);
                if (tl < 1e-4f) continue;
                tx /= tl; tz /= tl;
                var at = new Vector2(sample.Position.X - tz * offset, sample.Position.Z + tx * offset);
                bool valid = ContainsPlayablePosition(at.X, at.Y) && StormChaseCoast.SeawardDistance(at.X, at.Y) < -220
                    && LakeRadius(at.X, at.Y) > 1.2f && !(at.X > 1600 && at.X < 6450 && MathF.Abs(at.Y - RiverZ(at.X)) < 80)
                    && PropSiteClear(at, .6f, 1.2f, 2);
                if (!valid) { previous = null; continue; }
                float yaw = MathF.Atan2(tx, tz);
                var basePosition = new Vector3f(at.X, HeightAt(at.X, at.Y), at.Y);
                Prop("power", ++number % 7 == 0 ? "power-pole-transformer" : "power-pole", at, yaw);
                DistributionPoleCount++;
                if (previous is { } p && DistanceSquared(p.Base.X, p.Base.Z, basePosition.X, basePosition.Z) < 175 * 175)
                    foreach (float lx in new[] { -1.1f, 0f, 1.1f })
                    {
                        float ly = lx == 0 ? 11.74f : 11.42f;
                        Wire(Attach(p.Base, p.Yaw, lx, ly), Attach(basePosition, yaw, lx, ly));
                    }
                previous = (basePosition, yaw, true);
            }
        }
    }

    private static Vector3f Attach(Vector3f basePosition, float yaw, float localX, float localY)
    {
        float c = MathF.Cos(yaw), s = MathF.Sin(yaw);
        return new(basePosition.X + localX * c, basePosition.Y + localY, basePosition.Z - localX * s);
    }

    /// <summary>A sagging conductor as two crossed thin ribbons per segment, merged into the area's wire mesh.</summary>
    private void Wire(Vector3f from, Vector3f to)
    {
        const int segments = 4; const float half = .022f, sag = 1.25f;
        var batch = Batch("wire", (from.X + to.X) * .5f, (from.Z + to.Z) * .5f, "chase/wire");
        Vector3 Point(float t) => new(from.X + (to.X - from.X) * t, from.Y + (to.Y - from.Y) * t - MathF.Sin(t * MathF.PI) * sag, from.Z + (to.Z - from.Z) * t);
        var along = Vector3.Normalize(new(to.X - from.X, 0, to.Z - from.Z));
        var side = new Vector3(-along.Z, 0, along.X) * half;
        var up = Vector3.UnitY * half;
        for (int i = 0; i < segments; i++)
        {
            var a = Point(i / (float)segments); var b = Point((i + 1) / (float)segments);
            foreach (var (offset, normal) in new[] { (side, Vector3.UnitY), (up, new Vector3(-along.Z, 0, along.X)) })
            {
                uint first = (uint)batch.Vertices.Count;
                foreach (var p in new[] { a - offset, a + offset, b + offset, b - offset })
                    batch.Vertices.Add(new(V(p), new(1, 1, 1, 1), 0, 0, V(normal), V(along)));
                batch.Indices.AddRange([first, first + 1, first + 2, first, first + 2, first + 3,
                    first, first + 2, first + 1, first, first + 3, first + 2]);
            }
        }
    }

    // ------------------------------------------------------------------ merging
    private bool PropSiteClear(Vector2 at, float halfSize, float roadClearance = .25f, float buildingClearance = .5f)
        => LotIsClear(StormChaseLotGeometry.Rectangle(at, halfSize, halfSize), roadClearance, buildingClearance);

    private PropBatch Batch(string group, float x, float z, string material)
    {
        var key = (group, (int)MathF.Floor(x / PropCellSize), (int)MathF.Floor(z / PropCellSize), material);
        if (!propBatches.TryGetValue(key, out var batch)) propBatches.Add(key, batch = new());
        return batch;
    }

    /// <summary>Bakes one Blender model's rigid parts into the area batches at world transforms.</summary>
    private void Prop(string group, string modelId, Vector2 at, float yawRadians, float lift = 0)
    {
        var basePosition = new Vector3f(at.X, HeightAt(at.X, at.Y) + lift, at.Y);
        foreach (var item in Models.CreateItems(modelId, basePosition, yawRadians, "prop"))
        {
            var part = WeldedPart(item.Mesh);
            var transform = StormChaseLotGeometry.Transform(item);
            var e = item.RotationEulerDegrees ?? new Vector3f(0, item.RotationYawDegrees, 0);
            var rotation = Matrix4x4.CreateRotationX(e.X * MathF.PI / 180) * Matrix4x4.CreateRotationY(e.Y * MathF.PI / 180) * Matrix4x4.CreateRotationZ(e.Z * MathF.PI / 180);
            var scale = new Vector3(item.Scale.X, item.Scale.Y, item.Scale.Z);
            var batch = Batch(group, at.X, at.Y, item.MaterialId);
            uint first = (uint)batch.Vertices.Count;
            for (int i = 0; i < part.Positions.Length; i++)
            {
                var p = Vector3.Transform(part.Positions[i], transform);
                var n = Vector3.Normalize(Vector3.TransformNormal(part.Normals[i] / scale, rotation));
                var t = Vector3.Cross(MathF.Abs(n.Y) > .95f ? Vector3.UnitZ : Vector3.UnitY, n);
                t = t.LengthSquared() > 1e-6f ? Vector3.Normalize(t) : Vector3.UnitX;
                batch.Vertices.Add(new(V(p), new(1, 1, 1, 1), part.Uvs[i].X, part.Uvs[i].Y, V(n), V(t)));
            }
            foreach (uint index in part.Indices) batch.Indices.Add(first + index);
        }
    }

    private (Vector3[] Positions, Vector3[] Normals, Vector2[] Uvs, uint[] Indices) WeldedPart(AssetHandle mesh)
    {
        if (weldedParts.TryGetValue(mesh, out var cached)) return cached;
        if (!catalog.TryGetMesh(mesh, out var data) || data is null) throw new InvalidOperationException("Missing street prop mesh: " + mesh);
        var map = new Dictionary<(Vector3, Vector3, Vector2), uint>();
        var positions = new List<Vector3>(); var normals = new List<Vector3>(); var uvs = new List<Vector2>(); var indices = new List<uint>();
        var source = data.WideIndices.Count > 0 ? data.WideIndices : data.Indices.Select(i => (uint)i).ToArray();
        foreach (uint index in source)
        {
            var v = data.Vertices[(int)index];
            var key = (new Vector3(v.Position.X, v.Position.Y, v.Position.Z), new Vector3(v.Normal.X, v.Normal.Y, v.Normal.Z), new Vector2(v.U, v.V));
            if (!map.TryGetValue(key, out uint welded))
            {
                welded = (uint)positions.Count; map.Add(key, welded);
                positions.Add(key.Item1); normals.Add(key.Item2); uvs.Add(key.Item3);
            }
            indices.Add(welded);
        }
        return weldedParts[mesh] = (positions.ToArray(), normals.ToArray(), uvs.ToArray(), indices.ToArray());
    }

    private void FlushProps()
    {
        foreach (var ((group, cx, cz, material), batch) in propBatches.OrderBy(pair => pair.Key.ToString(), StringComparer.Ordinal))
        {
            if (batch.Indices.Count == 0) continue;
            var handle = Mesh(FormattableString.Invariant($"props/{group}/{cx}/{cz}/{material.Replace('/', '-')}"));
            catalog.UpsertMesh(new() { Handle = handle, Vertices = batch.Vertices.ToArray(), WideIndices = batch.Indices.ToArray() });
            StreetPropVertexCount += batch.Vertices.Count;
            // Only the solid structural materials cast shadows; lettering, lenses and wires do not.
            bool shadow = group is not ("road" or "edge") && material is "chase/model/metal" or "chase/model/wood" or "chase/model/concrete";
            float range = group switch { "power" => 2600, "wire" => 1200, "edge" => 1800, "road" => 1400, _ => 1100 } + PropCellSize * .71f;
            var materialHandle = new AssetHandle("material", material);
            streetProps.Add(new((cx + .5f) * PropCellSize, (cz + .5f) * PropCellSize, range,
                new RenderItem(material, handle, materialHandle, default, new(1, 1, 1), 0, shadow)));
        }
        propBatches.Clear();
        weldedParts.Clear();
    }

    private static float PolylineLength(Vector2[] points)
    {
        float length = 0;
        for (int i = 0; i < points.Length - 1; i++) length += Vector2.Distance(points[i], points[i + 1]);
        return length;
    }
    private static float Cross(Vector2 a, Vector2 b) => a.X * b.Y - a.Y * b.X;
    private static string Slug(string name) => name.ToLowerInvariant().Replace(' ', '-');
    private static Vector3f V(Vector3 v) => new(v.X, v.Y, v.Z);
}
