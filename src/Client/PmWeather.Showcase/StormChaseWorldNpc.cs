using System.Numerics;
using StormChase.Npc;

/// <summary>
/// Client adapter from the generated world to the engine-free NPC simulation: homes, shops and workplaces
/// from the placed buildings (doors from their interior entrances), and a sidewalk graph alongside the
/// town streets with crosswalks at each junction. Built from the same deterministic world on every machine.
/// </summary>
internal sealed partial class StormChaseWorld
{
    private const float SidewalkOffset = 3.6f, SidewalkSpacing = 15;
    private NpcMap? npcMap;

    public NpcMap CreateNpcMap() => npcMap ??= BuildNpcMap();

    private NpcMap BuildNpcMap()
    {
        var doors = BuildingInteriors.ToDictionary(i => i.BuildingId, i => new Vector2(i.ExteriorFeet.X, i.ExteriorFeet.Z), StringComparer.Ordinal);
        var places = new List<NpcPlace>();
        void Add(StormChaseBuilding building, NpcPlaceKind kind, int households, int jobs, string name)
        {
            var door = doors.TryGetValue(building.Id, out var d) ? d : new Vector2(building.Position.X, building.Position.Z);
            places.Add(new(places.Count, building.Id, kind, building.SettlementId, name, door, households, jobs));
        }
        foreach (var building in buildings)
        {
            string name = building.DisplayName.Length > 0 ? building.DisplayName : building.Kind;
            switch (building.Kind)
            {
                case "house": Add(building, NpcPlaceKind.Home, 1, 0, name); break;
                case "store":
                {
                    // Multi-storey Main Street blocks have apartments above the shop.
                    float levelHeight = Models.GetModel("store").FootprintMeters.Y * building.Scale;
                    int levels = Math.Max(1, (int)MathF.Round((building.BoundsMaximum.Y - building.Position.Y) / Math.Max(1, levelHeight)));
                    Add(building, NpcPlaceKind.Store, 0, 3 + 2 * (levels - 1), name);
                    if (levels > 1) Add(building, NpcPlaceKind.Home, 2 * (levels - 1), 0, name + " apartments");
                    break;
                }
                case "warehouse": Add(building, NpcPlaceKind.Workplace, 0, 8, name); break;
                case "cityhall":
                    Add(building, NpcPlaceKind.Civic, 0, 6, name);
                    // No school buildings exist yet: each town's first civic hall also hosts its school (with teachers).
                    if (building.SettlementId != "rural" && !places.Any(p => p.Kind == NpcPlaceKind.School && p.Settlement == building.SettlementId))
                    {
                        string town = settlements.FirstOrDefault(s => s.Id == building.SettlementId)?.Name ?? building.SettlementId;
                        Add(building, NpcPlaceKind.School, 0, 10, town + " school (civic hall)");
                    }
                    break;
                case "gas-station": Add(building, NpcPlaceKind.Fuel, 0, 3, name); break;
                case "barn": Add(building, NpcPlaceKind.Farm, 0, 2, name); break;
            }
        }
        return new(places, BuildSidewalks());
    }

    private NpcWalkGraph BuildSidewalks()
    {
        var streets = TownStreets();
        var intersections = FindIntersections(streets);
        var nodes = new List<Vector2>();
        var edges = new List<(int, int)>();
        // Node lists per street side, for attaching junction corners.
        var sideNodes = new List<(int Street, int Side, List<int> Nodes)>();
        for (int s = 0; s < streets.Count; s++)
        {
            var street = streets[s];
            foreach (int side in new[] { -1, 1 })
            {
                var line = Resample(Offset(street.Points, side * (street.HalfWidth + SidewalkOffset)), SidewalkSpacing);
                var kept = new List<int>();
                int previous = -1;
                foreach (var point in line)
                {
                    // A sidewalk stops where it would run across another street; junction crosswalks join it.
                    bool onRoad = false;
                    for (int o = 0; o < streets.Count && !onRoad; o++)
                        if (o != s && PolylineDistance(streets[o].Points, point) < streets[o].HalfWidth + 2.4f) onRoad = true;
                    if (onRoad || StormChaseCoast.SeawardDistance(point.X, point.Y) > -150) { previous = -1; continue; }
                    nodes.Add(point); int index = nodes.Count - 1; kept.Add(index);
                    if (previous >= 0) edges.Add((previous, index));
                    previous = index;
                }
                sideNodes.Add((s, side, kept));
            }
        }
        var streetIndex = streets.Select((street, index) => (street, index)).ToDictionary(p => p.street, p => p.index);
        foreach (var x in intersections)
        {
            int a = streetIndex[x.Major], b = streetIndex[x.Minor];
            var rightA = new Vector2(x.MajorDirection.Y, -x.MajorDirection.X);
            var rightB = new Vector2(x.MinorDirection.Y, -x.MinorDirection.X);
            var corners = new int[2, 2];
            for (int i = 0; i < 2; i++)
            for (int j = 0; j < 2; j++)
            {
                float sa = i == 0 ? -1 : 1, sb = j == 0 ? -1 : 1;
                var corner = x.Point + x.MajorDirection * sa * (x.Minor.HalfWidth + SidewalkOffset) + x.MinorDirection * sb * (x.Major.HalfWidth + SidewalkOffset);
                nodes.Add(corner); int c = corners[i, j] = nodes.Count - 1;
                // Join the corner to the nearest surviving node on each street's sidewalk on this side.
                foreach (var (street, right) in new[] { (a, rightA), (b, rightB) })
                {
                    int side = Vector2.Dot(corner - x.Point, right) >= 0 ? 1 : -1;
                    // Offset() puts +side on the left of travel; match against the actual node positions.
                    int best = -1; float bestDistance = 22 * 22;
                    foreach (var (owner, _, list) in sideNodes.Where(n => n.Street == street))
                        foreach (int n in list)
                        {
                            if (MathF.Sign(Vector2.Dot(nodes[n] - x.Point, right)) != side) continue;
                            float d = Vector2.DistanceSquared(nodes[n], corner);
                            if (d < bestDistance) { bestDistance = d; best = n; }
                        }
                    if (best >= 0) edges.Add((c, best));
                }
            }
            // Crosswalks: across the minor street and across the major street.
            edges.Add((corners[0, 0], corners[1, 0])); edges.Add((corners[0, 1], corners[1, 1]));
            edges.Add((corners[0, 0], corners[0, 1])); edges.Add((corners[1, 0], corners[1, 1]));
        }
        return new(nodes, edges);
    }

    private static List<Vector2> Offset(Vector2[] points, float distance)
    {
        var result = new List<Vector2>(points.Length);
        for (int i = 0; i < points.Length; i++)
        {
            var before = i > 0 ? points[i] - points[i - 1] : points[1] - points[0];
            var after = i < points.Length - 1 ? points[i + 1] - points[i] : points[i] - points[i - 1];
            var direction = Vector2.Normalize(Vector2.Normalize(before) + Vector2.Normalize(after));
            result.Add(points[i] + new Vector2(-direction.Y, direction.X) * distance);
        }
        return result;
    }

    private static List<Vector2> Resample(List<Vector2> points, float spacing)
    {
        var result = new List<Vector2> { points[0] };
        float carry = 0;
        for (int i = 0; i < points.Count - 1; i++)
        {
            var a = points[i]; var b = points[i + 1]; float length = Vector2.Distance(a, b);
            float t = spacing - carry;
            while (t <= length) { result.Add(Vector2.Lerp(a, b, t / length)); t += spacing; }
            carry = length - (t - spacing);
        }
        if (Vector2.Distance(result[^1], points[^1]) > 2) result.Add(points[^1]);
        return result;
    }

    private static float PolylineDistance(Vector2[] line, Vector2 point)
    {
        float best = float.PositiveInfinity;
        for (int i = 0; i < line.Length - 1; i++)
        {
            var a = line[i]; var d = line[i + 1] - a;
            float t = Math.Clamp(Vector2.Dot(point - a, d) / Math.Max(1e-6f, d.LengthSquared()), 0, 1);
            best = MathF.Min(best, Vector2.Distance(point, a + d * t));
        }
        return best;
    }
}
