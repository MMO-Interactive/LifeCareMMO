using System.Numerics;
using BiologyLib.Physiology.Human;

namespace StormChase.Npc;

/// <summary>A resident visible outdoors at a moment: world X/Z, unit heading, and whether they are walking.</summary>
public readonly record struct NpcAgent(int ResidentId, Vector2 Position, Vector2 Heading, bool Walking, float HeightMeters, float DistanceMeters);

/// <summary>
/// The unit of NPC simulation that phase 2 distributes: one shard owns a set of residents. The single-player
/// client hosts one in-process shard covering the whole population.
/// </summary>
public interface INpcShard : IDisposable
{
    IReadOnlyList<NpcResident> Residents { get; }
    void AdvanceTo(NpcTime now);
    IReadOnlyList<NpcAgent> AgentsNear(Vector2 center, float radius, NpcTime now, int maximum);
    NpcSegment Activity(int residentId, NpcTime now);
    NpcVitals? Vitals(int residentId);
}

public sealed record NpcOptions(bool Biology = true, bool BackgroundBiology = true, int? MaximumResidents = null);

public sealed class NpcSimulation : INpcShard
{
    private const float BucketSize = 256;
    private readonly Dictionary<(int, int), List<int>> residentsByHome = [];
    private readonly Dictionary<(int Resident, int Day), IReadOnlyList<NpcSegment>> plans = [];
    private readonly object planGate = new();
    private int planDay;

    public NpcSimulation(int worldSeed, NpcMap map, NpcTime start, NpcOptions? options = null,
        IReadOnlyDictionary<int, HumanBodySimulationState>? restoredBodies = null)
    {
        options ??= new();
        WorldSeed = worldSeed;
        Map = map;
        Router = new(map);
        var population = NpcPopulation.Generate(worldSeed, map, options.MaximumResidents);
        Residents = population.Residents;
        Genomes = population.Genomes;
        foreach (var resident in Residents)
        {
            var door = map.Places[resident.HomePlace].Door;
            var key = Bucket(door);
            if (!residentsByHome.TryGetValue(key, out var list)) residentsByHome.Add(key, list = []);
            list.Add(resident.Id);
        }
        Biology = options.Biology ? new NpcBiology(Residents, start, restoredBodies, options.BackgroundBiology) : null;
        planDay = start.Day;
    }

    public int WorldSeed { get; }
    public NpcMap Map { get; }
    public NpcRouter Router { get; }
    public NpcBiology? Biology { get; }
    public IReadOnlyList<NpcResident> Residents { get; }
    /// <summary>BiologyLib genome per resident (index = resident id); parents' genomes produced their children's.</summary>
    public IReadOnlyList<BiologyLib.Inheritance.DiploidGenome> Genomes { get; }

    public void AdvanceTo(NpcTime now)
    {
        Biology?.AdvanceTo(now);
        if (now.Day != planDay)
        {
            // Plans are pure; keep only today and yesterday (segments never cross midnight).
            lock (planGate) foreach (var key in plans.Keys.Where(k => k.Day < now.Day - 1).ToArray()) plans.Remove(key);
            planDay = now.Day;
        }
    }

    public IReadOnlyList<NpcSegment> Plan(int residentId, int day)
    {
        lock (planGate)
            if (plans.TryGetValue((residentId, day), out var known)) return known;
        // Young children's outings follow a parent at home (the mother if present), whose plan never depends on theirs.
        var resident = Residents[residentId];
        int? parent = resident.AgeYears < 12 ? resident.MotherId ?? resident.FatherId : null;
        var plan = parent is int p
            ? NpcSchedule.Plan(resident, day, Map, Router, Plan(p, day), Residents[p].Sex == BiologicalSex.Female ? "mum" : "dad")
            : NpcSchedule.Plan(resident, day, Map, Router);
        lock (planGate) plans[(residentId, day)] = plan;
        return plan;
    }

    public NpcSegment Activity(int residentId, NpcTime now)
    {
        var plan = Plan(residentId, now.Day);
        double t = now.TotalHours;
        foreach (var segment in plan) if (segment.Contains(t)) return segment;
        return plan[^1];
    }

    public NpcVitals? Vitals(int residentId) => Biology?.Vitals(residentId);

    public IReadOnlyList<NpcAgent> AgentsNear(Vector2 center, float radius, NpcTime now, int maximum)
    {
        // Residents only walk within MaximumWalkMeters of home, so homes beyond that margin can be skipped.
        float reach = radius + NpcSchedule.MaximumWalkMeters + 40;
        var found = new List<NpcAgent>();
        int r = (int)MathF.Ceiling(reach / BucketSize);
        var (cx, cz) = Bucket(center);
        for (int z = cz - r; z <= cz + r; z++)
        for (int x = cx - r; x <= cx + r; x++)
        {
            if (!residentsByHome.TryGetValue((x, z), out var list)) continue;
            foreach (int id in list)
            {
                var home = Map.Places[Residents[id].HomePlace].Door;
                if (Vector2.DistanceSquared(home, center) > reach * reach) continue;
                if (Pose(id, now) is not { } agent) continue;
                float distance = Vector2.Distance(agent.Position, center);
                if (distance <= radius) found.Add(agent with { DistanceMeters = distance });
            }
        }
        return found.OrderBy(a => a.DistanceMeters).ThenBy(a => a.ResidentId).Take(maximum).ToArray();
    }

    /// <summary>The resident's outdoor pose, or null while indoors or away.</summary>
    public NpcAgent? Pose(int residentId, NpcTime now)
    {
        var segment = Activity(residentId, now);
        if (segment.Activity != NpcActivity.Walking || segment.Path is not { } path) return null;
        double fraction = (now.TotalHours - segment.Start) / Math.Max(1e-6, segment.End - segment.Start);
        var (position, heading) = path.At((float)(fraction * path.Length));
        if (segment.Offset != 0) position += new Vector2(heading.Y, -heading.X) * segment.Offset;
        var resident = Residents[residentId];
        return new(residentId, position, heading, true, DisplayHeight(resident), 0);
    }

    /// <summary>
    /// Rendered stature from the genome's height potential (±8% around typical adults) along a growth curve for
    /// children. BiologyLib's body anthropometry keeps growing to age 25 (men average ~2.07 m), so it is not used here.
    /// </summary>
    public static float DisplayHeight(NpcResident resident)
    {
        bool female = resident.Sex == BiologicalSex.Female;
        double adult = (female ? 1.64 : 1.78) * (.92 + .16 * resident.Genes.HeightPotential);
        double age = resident.AgeYears;
        // Fraction of adult height by age (smoothed reference growth): birth .29, 2 y .50, 10 y .76, girls finish ~16, boys ~18.
        double finish = female ? 16 : 18;
        double fraction = age switch
        {
            < 2 => .29 + .105 * age,
            < 10 => .50 + (age - 2) * .0325,
            _ => .76 + .24 * Math.Clamp((age - 10) / (finish - 10), 0, 1),
        };
        return (float)(adult * Math.Min(1, fraction));
    }

    /// <summary>Human-readable status line for HUD and probes.</summary>
    public string Describe(int residentId, NpcTime now)
    {
        var resident = Residents[residentId];
        var segment = Activity(residentId, now);
        string job = resident.WorkPlace is { } work ? Map.Places[work].Name : resident.AgeYears > 66 ? "retired" : "not working";
        string body = Vitals(residentId) is { } v ? $" · HR {v.HeartRateBpm:0} · {v.CoreTemperatureCelsius:0.0} °C · alert {v.AlertnessPercent:0}%" : "";
        return $"{resident.Name}, {resident.AgeYears:0} · {job} · {segment.Purpose}{body}";
    }

    public void Dispose() => Biology?.Dispose();
    private static (int, int) Bucket(Vector2 p) => ((int)MathF.Floor(p.X / BucketSize), (int)MathF.Floor(p.Y / BucketSize));
}
