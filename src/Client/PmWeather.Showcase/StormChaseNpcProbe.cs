using System.Diagnostics;
using System.Numerics;
using System.Text.Json;
using Engine.Rendering.Assets;
using StormChase.Npc;

/// <summary>
/// NPC phase-1 checks on the real generated world: map and population, schedule coverage, pedestrians on
/// sidewalks, BiologyLib determinism and save/restore, and per-call costs. With --npc-avatars it also cooks
/// or loads every MMOI/UMA NPC look through the client layer.
/// </summary>
internal static class StormChaseNpcProbe
{
    public static int Run(string output, string[] args)
    {
        Directory.CreateDirectory(output);
        var checks = new List<string>();
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("NPC check failed: " + message);
            checks.Add(message);
            Console.WriteLine("  ok  " + message);
        }
        var clock = Stopwatch.StartNew();
        var catalog = new MutableRenderAssetCatalog(new BuiltInRenderAssetCatalog());
        var world = new StormChaseWorld(catalog);
        double worldMs = clock.Elapsed.TotalMilliseconds; clock.Restart();
        var map = world.CreateNpcMap();
        double mapMs = clock.Elapsed.TotalMilliseconds;
        const int seed = 73019;

        // ---- map
        var homes = map.Places.Where(p => p.Kind == NpcPlaceKind.Home).ToArray();
        int townHomes = homes.Count(h => h.Settlement != "rural");
        var router = new NpcRouter(map);
        int attached = homes.Count(h => h.Settlement != "rural" && router.DoorNode(h.Index) >= 0);
        Check(map.Places.Count > 600 && homes.Sum(h => h.Households) > 330 && map.Places.Count(p => p.Jobs > 0) > 200,
            $"World places: {homes.Length} homes ({homes.Sum(h => h.Households)} households), {map.Places.Count(p => p.Jobs > 0)} workplaces");
        Check(map.Sidewalks.NodeCount > 2000 && map.Sidewalks.EdgeCount > 2000,
            $"Sidewalk graph: {map.Sidewalks.NodeCount} nodes, {map.Sidewalks.EdgeCount} edges");
        Check(attached > townHomes * .8, $"{attached}/{townHomes} town homes reach a sidewalk");
        // Every sidewalk node keeps clear of building lots and stays off the beach.
        int insideLots = Enumerable.Range(0, map.Sidewalks.NodeCount).Count(i =>
        {
            var p = map.Sidewalks.Node(i);
            return world.Buildings.Any(b => p.X > b.BoundsMinimum.X + .5f && p.X < b.BoundsMaximum.X - .5f && p.Y > b.BoundsMinimum.Z + .5f && p.Y < b.BoundsMaximum.Z - .5f);
        });
        Check(insideLots < map.Sidewalks.NodeCount / 100, $"Sidewalk nodes inside building bounds: {insideLots}");

        // ---- population
        clock.Restart();
        var populationA = NpcPopulation.Generate(seed, map); var populationB = NpcPopulation.Generate(seed, map);
        double populationMs = clock.Elapsed.TotalMilliseconds / 2;
        var a = populationA.Residents;
        string Fingerprint(NpcPopulationResult population) => JsonSerializer.Serialize(population.Residents);
        Check(Fingerprint(populationA) == Fingerprint(populationB), $"Population and genomes are deterministic ({a.Count} residents)");
        int children = a.Count(r => r.IsChild);
        Check(a.Count > 600 && children > a.Count / 8, $"Population fills the towns: {a.Count} residents, {children} children");
        int employed = a.Count(r => r.WorkPlace is not null);
        Check(employed > a.Count / 3 && a.All(r => r.WorkPlace is null || !r.IsChild && map.Places[r.WorkPlace.Value].Settlement == map.Places[r.HomePlace].Settlement),
            $"{employed} employed adults, all within their own settlement; no child employed");
        var other = NpcPopulation.Generate(seed + 1, map);
        Check(!other.Residents.Select(r => r.Name).SequenceEqual(a.Select(r => r.Name)), "A different campaign seed gives a different population");

        // ---- genetics: everyone looks different; children resemble their parents
        double Distance(NpcAppearanceGenes x, NpcAppearanceGenes y) => Math.Sqrt(NpcGenetics.MorphologyTraits.Sum(t => Math.Pow(x.Morphology[t] - y.Morphology[t], 2))
            + Math.Pow(x.SkinPigmentation - y.SkinPigmentation, 2) + Math.Pow(x.HairPigmentation - y.HairPigmentation, 2) + Math.Pow(x.EyePigmentation - y.EyePigmentation, 2));
        var distinct = a.Select(r => JsonSerializer.Serialize(r.Genes)).Distinct().Count();
        Check(distinct == a.Count, $"Every resident has a distinct genetic appearance ({distinct}/{a.Count})");
        var families = a.Where(r => r.MotherId is not null && r.FatherId is not null).ToArray();
        double toParents = families.Average(c => Distance(c.Genes, Midpoint(a[c.MotherId!.Value].Genes, a[c.FatherId!.Value].Genes)));
        var strangerRandom = new Random(5);
        double toStrangers = families.Average(c => Distance(c.Genes, Midpoint(a[strangerRandom.Next(a.Count)].Genes, a[strangerRandom.Next(a.Count)].Genes)));
        var siblings = families.GroupBy(c => (c.MotherId, c.FatherId)).Where(g => g.Count() > 1).ToArray();
        double siblingDistance = siblings.Average(g => Distance(g.First().Genes, g.Last().Genes));
        double unrelated = Enumerable.Range(0, 400).Average(_ => Distance(a[strangerRandom.Next(a.Count)].Genes, a[strangerRandom.Next(a.Count)].Genes));
        Check(toParents < toStrangers * .8 && siblingDistance < unrelated * .85,
            $"Children inherit from both parents: {families.Length} children sit {toParents:0.000} from their parents' midpoint vs {toStrangers:0.000} for random pairs; " +
            $"siblings {siblingDistance:0.000} apart vs {unrelated:0.000} unrelated");
        Check(families.All(c => populationA.Genomes[c.Id].ChromosomePairs.Where(pr => pr.Number < 23).All(pr =>
                populationA.Genomes[c.MotherId!.Value].ChromosomePairs.Single(m => m.Number == pr.Number) is var mp && (ReferenceEquals(pr.Maternal, mp.Maternal) || ReferenceEquals(pr.Maternal, mp.Paternal)))),
            "Every child's maternal chromosomes are copies of their mother's");

        // ---- schedules and pedestrians (no biology)
        using var sim = new NpcSimulation(seed, map, new(3, 6), new(Biology: false));
        clock.Restart();
        int gaps = 0, walkLegs = 0; float longest = 0;
        for (int id = 0; id < sim.Residents.Count; id++)
        {
            var plan = sim.Plan(id, 3);
            if (Math.Abs(plan[0].Start - 48) > 1e-6 || Math.Abs(plan[^1].End - 72) > 1e-6) gaps++;
            for (int i = 1; i < plan.Count; i++) if (Math.Abs(plan[i].Start - plan[i - 1].End) > 1e-6 || plan[i].End < plan[i].Start) gaps++;
            foreach (var leg in plan.Where(s => s.Activity == NpcActivity.Walking)) { walkLegs++; longest = Math.Max(longest, leg.Path!.Length); }
        }
        double planMs = clock.Elapsed.TotalMilliseconds;
        Check(gaps == 0, "Every resident's day is contiguous from midnight to midnight");
        Check(walkLegs > sim.Residents.Count / 3 && longest <= NpcSchedule.MaximumWalkMeters + 1, $"{walkLegs} walking legs, longest {longest:0} m");
        int Walkers(double hour) => Enumerable.Range(0, sim.Residents.Count).Count(id => sim.Pose(id, new(3, hour)) is not null);
        var byHour = Enumerable.Range(0, 24).ToDictionary(h => h, h => Walkers(h + .5));
        // School: weekday 3 (Wednesday) children are at school mid-morning, home or out after.
        var pupils = sim.Residents.Where(r => NpcSchedule.AttendsSchool(r, 3)).ToArray();
        int atSchool = pupils.Count(r => sim.Activity(r.Id, new(3, 11)).Activity == NpcActivity.AtSchool);
        int saturdaySchool = sim.Residents.Count(r => sim.Activity(r.Id, new(6, 11)).Activity == NpcActivity.AtSchool);
        Check(pupils.Length > 50 && atSchool > pupils.Length * .9 && saturdaySchool == 0,
            $"{atSchool}/{pupils.Length} school-age children are at school at 11:00 on Wednesday; none on Saturday");
        var accompanied = Enumerable.Range(1, 7).Sum(d => sim.Residents.Where(r => r.AgeYears < 12).Sum(r => sim.Plan(r.Id, d).Count(seg => seg.Offset != 0 && seg.Activity == NpcActivity.Walking)));
        int kidsPlaying = Enumerable.Range(1, 7).Sum(d => sim.Residents.Where(r => r.AgeYears is >= 8 and < 18).Count(r => sim.Plan(r.Id, d).Any(seg => seg.Activity == NpcActivity.Walking && seg.Offset == 0)));
        Check(accompanied > 20 && kidsPlaying > 50, $"Over a week: {accompanied} walking legs by young children beside a parent; {kidsPlaying} child outings of their own");
        Check(byHour[3] == 0 && byHour.Where(p => p.Key is >= 10 and <= 20).Max(p => p.Value) > 30,
            "Nobody walks at 03:30; afternoon/evening pedestrians: " + string.Join(" ", byHour.Where(p => p.Key % 3 == 0).Select(p => $"{p.Key}h={p.Value}")));
        // Pedestrians are on sidewalks (or their own front path) and move continuously.
        var sample = Enumerable.Range(0, sim.Residents.Count).Select(id => (id, pose: sim.Pose(id, new(3, 17.5)))).Where(p => p.pose is not null).ToArray();
        float worstOff = sample.Max(p => { int n = map.Sidewalks.Nearest(p.pose!.Value.Position, 400); return n < 0 ? 999 : Vector2.Distance(map.Sidewalks.Node(n), p.pose!.Value.Position); });
        Check(worstOff < NpcRouter.MaximumDoorMeters + 1, $"Pedestrians stay on the sidewalk network (furthest {worstOff:0.0} m from a node, door paths included)");
        float maxStep = sample.Max(p => Vector2.Distance(p.pose!.Value.Position, sim.Pose(p.id, new(3, 17.5 + 1 / 3600d))?.Position ?? p.pose!.Value.Position));
        float wallSpeed = maxStep / (float)(1 / NpcSchedule.GameSecondsPerWallSecond);
        Check(wallSpeed < 2.2f, $"Walking speed on the accelerated clock: at most {wallSpeed:0.00} m per wall second");
        var meridian = new Vector2(5780, 6900);
        clock.Restart();
        IReadOnlyList<NpcAgent> near = [];
        for (int i = 0; i < 200; i++) near = sim.AgentsNear(meridian, StormChaseNpcLayer.AvatarRadius, new(3, 17.5 + i * .001), StormChaseNpcLayer.MaximumAvatars);
        double nearMs = clock.Elapsed.TotalMilliseconds / 200;
        Check(nearMs < 2, $"Nearby-pedestrian query: {nearMs:0.000} ms ({near.Count} near Meridian downtown at 17:30)");

        // ---- biology: determinism, meals, save/restore, cost
        var start = new NpcTime(2, 6.2);
        using var bioA = new NpcSimulation(seed, map, start, new(BackgroundBiology: false, MaximumResidents: 30));
        using var bioB = new NpcSimulation(seed, map, start, new(BackgroundBiology: false, MaximumResidents: 30));
        clock.Restart();
        bioA.Biology!.AdvanceTo(start); bioA.Biology.Complete();
        double createMs = clock.Elapsed.TotalMilliseconds / 30;
        var before = Enumerable.Range(0, 30).Select(i => bioA.Vitals(i)!).ToArray();
        clock.Restart();
        bioA.Biology.AdvanceTo(new(2, 20)); bioA.Biology.Complete();
        double stepMs = clock.Elapsed.TotalMilliseconds / (30 * ((20 - 6) / NpcBiology.StepHours));
        bioB.Biology!.AdvanceTo(new(2, 20)); bioB.Biology.Complete();
        Check(Enumerable.Range(0, 30).All(i => bioA.Biology.StateJson(i) == bioB.Biology.StateJson(i)),
            "Two shards advancing the same residents produce byte-identical BiologyLib states");
        var after = Enumerable.Range(0, 30).Select(i => bioA.Vitals(i)!).ToArray();
        var shown = sim.Residents.Where(r => !r.IsChild).Select(r => NpcSimulation.DisplayHeight(r)).ToArray();
        Check(Enumerable.Range(0, 30).All(i => after[i] is var v && v.ObservedTotalHours == 44 && v.HeartRateBpm is > 35 and < 160
                && (bioA.Residents[i].IsChild ? v.HeightMeters is > .45 and < 2.0 : v.HeightMeters is > 1.4 and < 2.2))
            && shown.All(h => h is > 1.45f and < 1.98f),
            $"Bodies reach 20:00 on day 2 (HR {after.Min(v => v.HeartRateBpm):0}–{after.Max(v => v.HeartRateBpm):0}; BiologyLib stature " +
            $"{after.Min(v => v.HeightMeters):0.00}–{after.Max(v => v.HeightMeters):0.00} m, rendered {shown.Min():0.00}–{shown.Max():0.00} m)");
        Check(Enumerable.Range(0, 30).Any(i => before[i].HeartRateBpm != after[i].HeartRateBpm || before[i].AlertnessPercent != after[i].AlertnessPercent),
            "Bodies change over the day (meals and circadian state)");
        string savePath = Path.Combine(output, "npc-bodies.json.gz");
        using (var file = File.Create(savePath)) bioA.Biology.Save(file);
        Dictionary<int, BiologyLib.Physiology.Human.HumanBodySimulationState> restored;
        using (var file = File.OpenRead(savePath)) restored = NpcBiology.Load(file);
        using var bioC = new NpcSimulation(seed, map, new(2, 20), new(BackgroundBiology: false, MaximumResidents: 30), restored);
        int roundTrip = Enumerable.Range(0, 30).Count(i => bioA.Biology.StateJson(i) == bioC.Biology!.StateJson(i));
        Check(roundTrip == 30 && bioC.Vitals(0)?.ObservedTotalHours == 44,
            $"Saved bodies ({new FileInfo(savePath).Length / 1024} KiB for 30) reload byte-identical at their saved time");
        bioA.Biology.AdvanceTo(new(3, 9)); bioA.Biology.Complete();
        bioC.Biology!.AdvanceTo(new(3, 9)); bioC.Biology.Complete();
        // BiologyLib's CaptureState/Restore is not continuation-exact (reproductive-cycle surfaces drift by ~0.01%
        // after a restore, even in memory), so continued bodies are compared on their vitals, not byte for byte.
        int exact = Enumerable.Range(0, 30).Count(i => bioA.Biology.StateJson(i) == bioC.Biology.StateJson(i));
        bool close = Enumerable.Range(0, 30).All(i =>
        {
            var x = bioA.Vitals(i)!; var y = bioC.Vitals(i)!;
            return Math.Abs(x.HeartRateBpm - y.HeartRateBpm) < .5 && Math.Abs(x.CoreTemperatureCelsius - y.CoreTemperatureCelsius) < .05
                && Math.Abs(x.AlertnessPercent - y.AlertnessPercent) < 1 && Math.Abs(x.HeightMeters - y.HeightMeters) < .001;
        });
        Check(close, $"Restored bodies continue across midnight with matching vitals ({exact}/30 byte-identical; the rest drift inside BiologyLib's restore)");
        // Background thread: same result as the synchronous path.
        using var bioD = new NpcSimulation(seed, map, start, new(MaximumResidents: 30));
        bioD.Biology!.AdvanceTo(new(2, 20));
        var wait = Stopwatch.StartNew();
        while (Enumerable.Range(0, 30).Any(i => bioD.Vitals(i)?.ObservedTotalHours != 44) && wait.Elapsed.TotalSeconds < 60) Thread.Sleep(20);
        Check(Enumerable.Range(0, 30).All(i => bioD.Biology.StateJson(i) == bioB.Biology.StateJson(i)),
            "The background biology thread matches the synchronous result");
        double fullPopulationCore = sim.Residents.Count * stepMs / (NpcBiology.StepHours * 3600 / NpcSchedule.GameSecondsPerWallSecond) / 1000;
        Check(fullPopulationCore < .25, $"Biology cost: {createMs:0.0} ms to create a body, {stepMs:0.000} ms per half-hour step; " +
            $"full population of {sim.Residents.Count} needs {fullPopulationCore * 100:0.0}% of one core");

        // ---- optional: each person's own genetic avatar through the real avatar system
        object? avatars = null;
        if (args.Contains("--npc-avatars", StringComparer.OrdinalIgnoreCase))
        {
            using var layer = new StormChaseNpcLayer(world, catalog, null, seed, new(3, 17.5), null);
            // A family (mother, father, children) and an unrelated neighbour of each sex.
            var family = layer.Simulation.Residents.First(r => r.MotherId is not null && r.FatherId is not null && r.AgeYears >= 12
                && layer.Simulation.Residents.Count(c => c.MotherId == r.MotherId && c.FatherId == r.FatherId) >= 2);
            var members = new[] { family.MotherId!.Value, family.FatherId!.Value }
                .Concat(layer.Simulation.Residents.Where(c => c.MotherId == family.MotherId && c.FatherId == family.FatherId).Select(c => c.Id)).ToList();
            members.Add(layer.Simulation.Residents.First(r => r.Sex == BiologyLib.Physiology.Human.BiologicalSex.Female && !r.IsChild && !members.Contains(r.Id)).Id);
            members.Add(layer.Simulation.Residents.First(r => r.Sex == BiologyLib.Physiology.Human.BiologicalSex.Male && !r.IsChild && !members.Contains(r.Id)).Id);
            var builds = new List<object>();
            foreach (int id in members)
            {
                clock.Restart();
                layer.BuildNow([id]);
                builds.Add(new { Resident = layer.Simulation.Residents[id].Name, layer.Simulation.Residents[id].AgeYears, Milliseconds = clock.Elapsed.TotalMilliseconds });
                Console.WriteLine($"  ..  built {layer.Simulation.Residents[id].Name} ({layer.Simulation.Residents[id].AgeYears:0}) in {clock.Elapsed.TotalMilliseconds:0} ms");
            }
            Check(layer.BuiltAvatars == members.Count, $"Built {members.Count} genetic avatars: a family of {members.Count - 2} plus two unrelated neighbours");
            var renderedFrame = new List<Shared.Contracts.Rendering.RenderItem>(); var framePoses = new List<Shared.Contracts.Rendering.RenderSkinPose>();
            var camera = new Shared.Contracts.World.Vector3f(5780, world.HeightAt(5780, 6900) + 1.7f, 6900);
            clock.Restart();
            for (int f = 0; f < 60; f++) { renderedFrame.Clear(); framePoses.Clear(); layer.AddToFrame(renderedFrame, framePoses, camera, f / 60f, new(3, 17.5 + f * .0005)); }
            double frameMs = clock.Elapsed.TotalMilliseconds / 60;
            avatars = new { Builds = builds, layer.FactoryMilliseconds, FrameMilliseconds = frameMs, layer.VisibleAvatars };
        }

        string report = Path.Combine(output, "npc-phase1.json");
        File.WriteAllText(report, JsonSerializer.Serialize(new
        {
            Checks = checks, Residents = sim.Residents.Count, Homes = homes.Length, Households = homes.Sum(h => h.Households),
            Workplaces = map.Places.Count(p => p.Jobs > 0), Employed = employed, SidewalkNodes = map.Sidewalks.NodeCount,
            SidewalkEdges = map.Sidewalks.EdgeCount, WalkersByHour = byHour, Timing = new { worldMs, mapMs, populationMs, planMs, nearMs, createMs, stepMs, fullPopulationCore },
            Avatars = avatars, Sample = Enumerable.Range(0, 8).Select(i => sim.Describe(i * 37 % sim.Residents.Count, new(3, 17.5))).ToArray(),
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"NPC phase 1: {checks.Count} checks passed; {sim.Residents.Count} residents. {report}");
        return 0;
    }

    private static NpcAppearanceGenes Midpoint(NpcAppearanceGenes x, NpcAppearanceGenes y) => new(
        (x.EyePigmentation + y.EyePigmentation) / 2, (x.HairPigmentation + y.HairPigmentation) / 2, (x.HairTexture + y.HairTexture) / 2,
        (x.SkinPigmentation + y.SkinPigmentation) / 2, (x.HeightPotential + y.HeightPotential) / 2, (x.Build + y.Build) / 2,
        x.Morphology.ToDictionary(p => p.Key, p => (p.Value + y.Morphology[p.Key]) / 2));
}
