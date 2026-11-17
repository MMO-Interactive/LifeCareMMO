using System.Numerics;

namespace StormChase.Npc;

public enum NpcPlaceKind { Home, Store, Workplace, Civic, Fuel, Farm, School }

/// <summary>A building residents live in, work at or visit. Positions are world X/Z metres.</summary>
/// <param name="Households">Homes only: separate households (a house is 1; a store's upper floors add apartments).</param>
/// <param name="Jobs">Workplaces: staffed positions.</param>
public sealed record NpcPlace(int Index, string Id, NpcPlaceKind Kind, string Settlement, string Name,
    Vector2 Door, int Households, int Jobs)
{
    public bool IsShop => Kind is NpcPlaceKind.Store or NpcPlaceKind.Fuel;
}

/// <summary>World description supplied by the host (client or server adapter). Immutable once built.</summary>
public sealed class NpcMap
{
    public NpcMap(IReadOnlyList<NpcPlace> places, NpcWalkGraph sidewalks)
    {
        Places = places;
        Sidewalks = sidewalks;
        for (int i = 0; i < places.Count; i++)
            if (places[i].Index != i) throw new ArgumentException("Place indices must match their list position.");
    }
    public IReadOnlyList<NpcPlace> Places { get; }
    public NpcWalkGraph Sidewalks { get; }
}

/// <summary>Undirected sidewalk/crosswalk graph with A* routing and door attachment.</summary>
public sealed class NpcWalkGraph
{
    private const float BucketSize = 64;
    private readonly Vector2[] nodes;
    private readonly int[][] neighbors;
    private readonly Dictionary<(int, int), List<int>> buckets = [];

    public NpcWalkGraph(IReadOnlyList<Vector2> nodes, IEnumerable<(int A, int B)> edges)
    {
        this.nodes = nodes.ToArray();
        var adjacency = Enumerable.Range(0, this.nodes.Length).Select(_ => new HashSet<int>()).ToArray();
        foreach (var (a, b) in edges)
        {
            if (a == b || (uint)a >= this.nodes.Length || (uint)b >= this.nodes.Length) continue;
            adjacency[a].Add(b); adjacency[b].Add(a);
        }
        neighbors = adjacency.Select(set => set.Order().ToArray()).ToArray();
        for (int i = 0; i < this.nodes.Length; i++)
        {
            var key = Bucket(this.nodes[i]);
            if (!buckets.TryGetValue(key, out var list)) buckets.Add(key, list = []);
            list.Add(i);
        }
    }

    public int NodeCount => nodes.Length;
    public int EdgeCount => neighbors.Sum(n => n.Length) / 2;
    public Vector2 Node(int index) => nodes[index];
    public IReadOnlyList<int> Neighbors(int index) => neighbors[index];

    /// <summary>The nearest node within <paramref name="maximumDistance"/>, or -1.</summary>
    public int Nearest(Vector2 point, float maximumDistance = 160)
    {
        int best = -1; float bestDistance = maximumDistance * maximumDistance;
        int reach = (int)MathF.Ceiling(maximumDistance / BucketSize);
        var (cx, cz) = Bucket(point);
        for (int z = cz - reach; z <= cz + reach; z++)
        for (int x = cx - reach; x <= cx + reach; x++)
        {
            if (!buckets.TryGetValue((x, z), out var list)) continue;
            foreach (int i in list)
            {
                float d = Vector2.DistanceSquared(nodes[i], point);
                if (d < bestDistance || d == bestDistance && i < best) { bestDistance = d; best = i; }
            }
        }
        return best;
    }

    /// <summary>A* node route, or null when the nodes are not connected.</summary>
    public int[]? Route(int from, int to)
    {
        if (from < 0 || to < 0) return null;
        if (from == to) return [from];
        var cost = new Dictionary<int, float> { [from] = 0 };
        var parent = new Dictionary<int, int>();
        var open = new PriorityQueue<int, float>();
        open.Enqueue(from, Vector2.Distance(nodes[from], nodes[to]));
        var closed = new HashSet<int>();
        while (open.TryDequeue(out int current, out _))
        {
            if (current == to)
            {
                var route = new List<int> { to };
                while (parent.TryGetValue(route[^1], out int p)) route.Add(p);
                route.Reverse();
                return route.ToArray();
            }
            if (!closed.Add(current)) continue;
            foreach (int next in neighbors[current])
            {
                float g = cost[current] + Vector2.Distance(nodes[current], nodes[next]);
                if (cost.TryGetValue(next, out float known) && known <= g) continue;
                cost[next] = g; parent[next] = current;
                open.Enqueue(next, g + Vector2.Distance(nodes[next], nodes[to]));
            }
        }
        return null;
    }

    private static (int, int) Bucket(Vector2 p) => ((int)MathF.Floor(p.X / BucketSize), (int)MathF.Floor(p.Y / BucketSize));
}

/// <summary>A walkable polyline with cumulative lengths (door → sidewalk route → door).</summary>
public sealed class NpcPath
{
    private readonly Vector2[] points;
    private readonly float[] distance;
    public NpcPath(IReadOnlyList<Vector2> points)
    {
        var cleaned = new List<Vector2>();
        foreach (var p in points) if (cleaned.Count == 0 || Vector2.DistanceSquared(cleaned[^1], p) > .0001f) cleaned.Add(p);
        if (cleaned.Count == 1) cleaned.Add(cleaned[0] + new Vector2(.01f, 0));
        this.points = cleaned.ToArray();
        distance = new float[this.points.Length];
        for (int i = 1; i < this.points.Length; i++) distance[i] = distance[i - 1] + Vector2.Distance(this.points[i - 1], this.points[i]);
    }
    public float Length => distance[^1];
    public IReadOnlyList<Vector2> Points => points;
    public Vector2 Start => points[0];
    public Vector2 End => points[^1];

    /// <summary>Position and unit heading after travelling <paramref name="meters"/> (clamped to the path).</summary>
    public (Vector2 Position, Vector2 Heading) At(float meters)
    {
        meters = Math.Clamp(meters, 0, Length);
        int segment = Array.BinarySearch(distance, meters);
        if (segment < 0) segment = ~segment - 1;
        segment = Math.Clamp(segment, 0, points.Length - 2);
        var a = points[segment]; var b = points[segment + 1];
        float span = distance[segment + 1] - distance[segment];
        float t = span > 1e-4f ? (meters - distance[segment]) / span : 0;
        var heading = b - a;
        return (Vector2.Lerp(a, b, t), heading.LengthSquared() > 1e-8f ? Vector2.Normalize(heading) : Vector2.UnitY);
    }
}
