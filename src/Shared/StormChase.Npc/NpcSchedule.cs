namespace StormChase.Npc;

public enum NpcActivity { Asleep, AtHome, Away, AtWork, AtSchool, Shopping, Walking }

/// <summary>One continuous part of a resident's day. Times are campaign total hours.</summary>
/// <param name="Offset">Sideways offset from the path in metres (a child walking beside a parent).</param>
public sealed record NpcSegment(double Start, double End, NpcActivity Activity, int Place, NpcPath? Path = null, string Purpose = "", float Offset = 0)
{
    public bool Contains(double totalHours) => totalHours >= Start && totalHours < End;
}

/// <summary>
/// Pure daily plans: a resident's whole day is a function of (resident, day, map) — and, for young children, their
/// parent's plan — so any process can reproduce where they are without simulation history. Adults commute by car and
/// children ride the school bus (both off-screen): on the 72× campaign clock even a few hundred metres at real walking
/// speed takes game-hours, so walking is kept for errands, play and neighbourhood walks.
/// </summary>
public static class NpcSchedule
{
    /// <summary>Campaign seconds per wall second (the campaign day lasts 1,200 wall seconds).</summary>
    public const double GameSecondsPerWallSecond = 86400d / 1200d;
    public const float MaximumWalkMeters = 260;
    public const double SchoolAge = 5;

    public static double WalkHours(float meters, float speed) => meters / Math.Max(.4f, speed) * GameSecondsPerWallSecond / 3600d;
    /// <summary>Campaign day 1 is a Monday; days 6 and 7 of each week are the weekend.</summary>
    public static bool IsWeekday(int day) => (day - 1) % 7 < 5;
    public static bool AttendsSchool(NpcResident resident, int day) => resident.AgeYears is >= SchoolAge and < 18 && IsWeekday(day);

    /// <param name="parentPlan">The plan of the parent a young child goes out with (mother if she lives at home), if any.</param>
    public static IReadOnlyList<NpcSegment> Plan(NpcResident resident, int day, NpcMap map, NpcRouter router,
        IReadOnlyList<NpcSegment>? parentPlan = null, string parentName = "")
    {
        var random = DayRandom(resident, day);
        double dayStart = (day - 1) * 24d;
        int home = resident.HomePlace;
        double wake = AdjustWake(resident, day, random.Range(5.5, 8.0));
        double bed = resident.AgeYears switch
        {
            < 6 => random.Range(19.2, 20.2),
            < 12 => random.Range(20.0, 21.2),
            < 18 => random.Range(21.4, 23.0),
            _ => random.Range(21.4, 23.7),
        };
        var plan = new List<NpcSegment> { new(dayStart, dayStart + wake, NpcActivity.Asleep, home, null, "asleep") };
        double cursor = dayStart + wake;

        void Stay(double until, NpcActivity activity, int place, string purpose)
        {
            if (until <= cursor) return;
            plan.Add(new(cursor, until, activity, place, null, purpose)); cursor = until;
        }
        bool Walk(NpcPath path, int destination, string purpose)
        {
            double end = cursor + WalkHours(path.Length, resident.WalkSpeed);
            if (end > dayStart + bed - .25) return false;
            plan.Add(new(cursor, end, NpcActivity.Walking, destination, path, purpose)); cursor = end;
            return true;
        }

        if (resident.WorkPlace is { } work && (day + (int)(resident.Seed % 7)) % 7 is not (0 or 1))
        {
            double start = random.Range(6.75, 9.5), length = random.Range(7.5, 9.25);
            Stay(dayStart + Math.Max(wake + .3, start - .35), NpcActivity.AtHome, home, "getting ready");
            Stay(cursor + .3, NpcActivity.Away, work, "driving to work");
            Stay(Math.Min(dayStart + start + length, dayStart + bed - 1.5), NpcActivity.AtWork, work, "at work");
            Stay(cursor + .3, NpcActivity.Away, home, "driving home");
        }
        if (AttendsSchool(resident, day) && router.School(home) is { } school)
        {
            string name = map.Places[school].Name;
            Stay(dayStart + random.Range(7.35, 7.7), NpcActivity.AtHome, home, "getting ready for school");
            Stay(cursor + .45, NpcActivity.Away, school, "on the school bus");
            Stay(dayStart + random.Range(15.05, 15.35), NpcActivity.AtSchool, school, "at " + name);
            Stay(cursor + .45, NpcActivity.Away, home, "on the school bus home");
        }

        // One outing of their own: an errand on foot (12+), a walk or play around the block (8+), or a drive into town.
        double outingRoll = random.NextDouble();
        double outingStart = cursor > dayStart + wake + .5 ? cursor + random.Range(.3, 1.6) : dayStart + random.Range(9.0, 16.5);
        bool ownOuting = resident.AgeYears >= 8 && outingRoll < (resident.IsChild ? .8 : .72) && outingStart < dayStart + bed - 3;
        if (ownOuting)
        {
            Stay(outingStart, NpcActivity.AtHome, home, "at home");
            var shops = resident.AgeYears >= 12 ? router.WalkableShops(home) : [];
            var stroll = router.StrollTarget(home, random);
            if (outingRoll < .45 && shops.Count > 0)
            {
                var (shop, there) = shops[random.Next(shops.Count)];
                double save = cursor; int count = plan.Count;
                bool done = false;
                if (Walk(there, shop, "walking to " + map.Places[shop].Name))
                {
                    Stay(cursor + random.Range(.35, 1.0), NpcActivity.Shopping, shop, "shopping");
                    done = Walk(Reverse(there), home, "walking home");
                }
                if (!done) { plan.RemoveRange(count, plan.Count - count); cursor = save; }
            }
            else if (stroll is { } loop)
            {
                double save = cursor; int count = plan.Count;
                string purpose = resident.IsChild ? "playing out around the block" : "out for a walk";
                if (!(Walk(loop, home, purpose) && Walk(Reverse(loop), home, "walking home")))
                { plan.RemoveRange(count, plan.Count - count); cursor = save; }
            }
            else if (!resident.IsChild && shops.Count == 0 && map.Places.FirstOrDefault(p => p.IsShop && p.Settlement == map.Places[home].Settlement) is { } town)
            {
                Stay(cursor + .25, NpcActivity.Away, town.Index, "driving into town");
                Stay(cursor + random.Range(.5, 1.2), NpcActivity.Shopping, town.Index, "shopping");
                Stay(cursor + .25, NpcActivity.Away, home, "driving home");
            }
        }
        // Younger children go along on a parent's walk or errand when they are free, walking beside them.
        else if (resident.AgeYears < 12 && parentPlan is not null)
        {
            var outing = new List<NpcSegment>();
            foreach (var segment in parentPlan)
            {
                bool outside = segment.Activity is NpcActivity.Walking or NpcActivity.Shopping;
                if (outside && segment.Start >= cursor && segment.End <= dayStart + bed - .1) outing.Add(segment);
                else if (outing.Count > 0) break;
            }
            if (outing.Count > 0 && outing[0].Activity == NpcActivity.Walking)
            {
                Stay(outing[0].Start, NpcActivity.AtHome, home, "at home");
                foreach (var segment in outing)
                {
                    string with = segment.Activity == NpcActivity.Shopping ? "shopping with " + parentName : "out with " + parentName;
                    plan.Add(segment with { Purpose = with, Offset = .75f });
                    cursor = segment.End;
                }
            }
        }
        Stay(dayStart + bed, NpcActivity.AtHome, home, "at home");
        Stay(dayStart + 24, NpcActivity.Asleep, home, "asleep");
        return plan;
    }

    private static NpcRandom DayRandom(NpcResident resident, int day) => new(NpcRandom.Hash(resident.Seed, (uint)day * 7919u + 3));
    /// <summary>School children are up by about seven on school days.</summary>
    private static double AdjustWake(NpcResident resident, int day, double drawn) =>
        AttendsSchool(resident, day) ? 6.3 + (drawn - 5.5) / 2.5 * .7 : resident.AgeYears < SchoolAge ? 6 + (drawn - 5.5) / 2.5 * 1.5 : drawn;
    /// <summary>Hour of day the resident wakes (the first draw of the day's stream, shared with biology meals).</summary>
    public static double WakeHour(NpcResident resident, int day) => AdjustWake(resident, day, DayRandom(resident, day).Range(5.5, 8.0));

    public static NpcPath Reverse(NpcPath path) => new(path.Points.Reverse().ToArray());
}

/// <summary>Cached door-to-door sidewalk routes for one map.</summary>
public sealed class NpcRouter
{
    private readonly NpcMap map;
    private readonly int[] doorNodes;
    private readonly Dictionary<(int, int), NpcPath?> routes = [];
    private readonly Dictionary<int, IReadOnlyList<(int Shop, NpcPath Path)>> shops = [];
    private readonly object gate = new();

    /// <summary>Front paths longer than this would cross yards and lots; such homes drive instead.</summary>
    public const float MaximumDoorMeters = 70;
    public NpcRouter(NpcMap map)
    {
        this.map = map;
        doorNodes = map.Places.Select(p => map.Sidewalks.Nearest(p.Door, MaximumDoorMeters)).ToArray();
    }

    public int DoorNode(int place) => doorNodes[place];

    /// <summary>The home's school: its own settlement's, else the nearest one (rural and harbour children ride the bus).</summary>
    public int? School(int home)
    {
        var place = map.Places[home];
        var schools = map.Places.Where(p => p.Kind == NpcPlaceKind.School).ToArray();
        if (schools.Length == 0) return null;
        return (schools.FirstOrDefault(s => s.Settlement == place.Settlement)
            ?? schools.MinBy(s => System.Numerics.Vector2.DistanceSquared(s.Door, place.Door)))!.Index;
    }

    public NpcPath? Route(int fromPlace, int toPlace)
    {
        lock (gate)
        {
            if (routes.TryGetValue((fromPlace, toPlace), out var cached)) return cached;
            NpcPath? path = null;
            var nodes = map.Sidewalks.Route(doorNodes[fromPlace], doorNodes[toPlace]);
            if (nodes is not null)
                path = new([map.Places[fromPlace].Door, .. nodes.Select(map.Sidewalks.Node), map.Places[toPlace].Door]);
            routes[(fromPlace, toPlace)] = path;
            return path;
        }
    }

    /// <summary>Shops within walking distance of a home, by actual sidewalk route.</summary>
    public IReadOnlyList<(int Shop, NpcPath Path)> WalkableShops(int home)
    {
        lock (gate) if (shops.TryGetValue(home, out var known)) return known;
        var door = map.Places[home].Door;
        var found = new List<(int, NpcPath)>();
        if (doorNodes[home] >= 0)
            foreach (var shop in map.Places.Where(p => p.IsShop && System.Numerics.Vector2.Distance(p.Door, door) < NpcSchedule.MaximumWalkMeters))
                if (Route(home, shop.Index) is { } path && path.Length <= NpcSchedule.MaximumWalkMeters) found.Add((shop.Index, path));
        lock (gate) shops[home] = found;
        return found;
    }

    /// <summary>A short out-and-back neighbourhood walk from a home's door, or null without sidewalks.</summary>
    public NpcPath? StrollTarget(int home, NpcRandom random)
    {
        int start = doorNodes[home];
        if (start < 0) return null;
        var graph = map.Sidewalks;
        // Random walk along the graph without immediately doubling back, 90–220 m.
        var nodes = new List<int> { start };
        // The door-to-sidewalk leg counts toward the walk limit.
        float length = System.Numerics.Vector2.Distance(map.Places[home].Door, graph.Node(start));
        float target = Math.Min((float)random.Range(90, 220) + length, NpcSchedule.MaximumWalkMeters);
        int previous = -1, current = start;
        for (int step = 0; step < 64 && length < target; step++)
        {
            var options = graph.Neighbors(current).Where(n => n != previous).ToArray();
            if (options.Length == 0) break;
            int next = options[random.Next(options.Length)];
            float edge = System.Numerics.Vector2.Distance(graph.Node(current), graph.Node(next));
            if (length + edge > NpcSchedule.MaximumWalkMeters) break;
            length += edge;
            nodes.Add(next); previous = current; current = next;
        }
        if (nodes.Count < 2) return null;
        return new([map.Places[home].Door, .. nodes.Select(graph.Node)]);
    }
}
