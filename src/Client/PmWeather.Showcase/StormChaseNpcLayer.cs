using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;
using Engine.Avatar;
using Engine.Avatar.Generation;
using Engine.Rendering.Assets;
using Shared.Contracts.Rendering;
using Shared.Contracts.World;
using StormChase.Npc;
using Tools.Avatar.UMA;
using BiologicalSex = BiologyLib.Physiology.Human.BiologicalSex;

/// <summary>
/// Single-player NPCs: the in-process <see cref="NpcSimulation"/> shard plus each nearby resident's own MMOI/UMA avatar.
/// A person's look comes from their genome: BiologyLib pigmentation and height potential and the inherited morphology
/// loci drive the avatar system's DNA sliders and colours, so children resemble both parents. Skin, hair and eye colour
/// are deferred to material tints (Tools.Avatar.UMA deferred colours), so people share textures and differ in their
/// small DNA-deformed meshes. Avatars are built on a background thread, nearest first, and kept in a bounded set.
/// </summary>
internal sealed class StormChaseNpcLayer : IDisposable
{
    public const int MaximumAvatars = 20;
    public const int MaximumBuilt = 48;
    public const float AvatarRadius = 140;

    private readonly StormChaseWorld world;
    private readonly MutableRenderAssetCatalog catalog;
    private readonly string catalogPath;
    private readonly string? savePath;
    private readonly ConcurrentDictionary<int, StormChaseAvatar> built = new();
    private readonly ConcurrentDictionary<int, byte> failed = new();
    private readonly HashSet<int> fresh = [];
    private readonly Dictionary<int, long> lastSeen = [];
    private readonly Thread builder;
    private readonly AutoResetEvent wake = new(false);
    private volatile int[] wanted = [];
    private volatile bool stopping;
    private long frame;
    private bool disposed;

    public StormChaseNpcLayer(StormChaseWorld world, MutableRenderAssetCatalog catalog, string? avatarCatalog, int seed, NpcTime start, string? savePath)
    {
        this.world = world; this.catalog = catalog; this.savePath = savePath;
        catalogPath = avatarCatalog is not null && File.Exists(avatarCatalog) && !avatarCatalog.EndsWith("avatar.json", StringComparison.OrdinalIgnoreCase)
            ? Path.GetFullPath(avatarCatalog)
            : System.Environment.GetEnvironmentVariable("MMOI_UMA_CATALOG") ?? @"C:\Users\reviv\youradventure\Old Engine\artifacts\avatar\uma\uma-catalog.json";
        var clock = Stopwatch.StartNew();
        var map = world.CreateNpcMap();
        Dictionary<int, BiologyLib.Physiology.Human.HumanBodySimulationState>? restored = null;
        if (savePath is not null && File.Exists(savePath))
        {
            try { using var file = File.OpenRead(savePath); restored = NpcBiology.Load(file); }
            catch (Exception error) when (error is IOException or InvalidDataException or System.Text.Json.JsonException)
            { Console.WriteLine("NPC biology save ignored: " + error.Message); }
        }
        Simulation = new(seed, map, start, null, restored);
        MapMilliseconds = clock.Elapsed.TotalMilliseconds;
        builder = new(BuildLoop) { IsBackground = true, Name = "Storm Chase NPC avatars", Priority = ThreadPriority.BelowNormal };
        builder.Start();
        Console.WriteLine($"NPCs: {Simulation.Residents.Count} residents ({Simulation.Residents.Count(r => r.IsChild)} children) in " +
            $"{map.Places.Count(p => p.Kind == NpcPlaceKind.Home)} homes; {map.Sidewalks.NodeCount} sidewalk nodes" +
            (restored is null ? "." : $"; {restored.Count} bodies restored."));
    }

    public NpcSimulation Simulation { get; }
    public UmaAvatarFactory? Factory { get; private set; }
    public double MapMilliseconds { get; }
    public double FactoryMilliseconds { get; private set; }
    public int BuiltAvatars => built.Count;
    public int BuildsCompleted => buildsCompleted;
    public double LastBuildMilliseconds { get; private set; }
    public int VisibleAvatars { get; private set; }
    private int buildsCompleted;

    /// <summary>Advance the population and append the nearest pedestrians' own avatars to the frame.</summary>
    public void AddToFrame(List<RenderItem> items, List<RenderSkinPose> poses, Vector3f camera, float seconds, NpcTime now)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        frame++;
        Simulation.AdvanceTo(now);
        var agents = Simulation.AgentsNear(new(camera.X, camera.Z), AvatarRadius, now, MaximumAvatars);
        // Nearest first; the builder thread works through these and anything it has not reached is not drawn yet.
        wanted = agents.Select(a => a.ResidentId).ToArray();
        wake.Set();
        int visible = 0;
        foreach (var agent in agents)
        {
            if (!built.TryGetValue(agent.ResidentId, out var avatar)) continue;
            if (!lastSeen.ContainsKey(agent.ResidentId)) fresh.Add(agent.ResidentId);
            lastSeen[agent.ResidentId] = frame;
            float yaw = MathF.Atan2(agent.Heading.X, agent.Heading.Y) * 180 / MathF.PI;
            var feet = new Vector3f(agent.Position.X, world.HeightAt(agent.Position.X, agent.Position.Y), agent.Position.Y);
            // Each resident walks with its own phase so neighbours do not step in lockstep.
            items.AddRange(avatar.CreateRenderItems(feet, yaw, seconds + agent.ResidentId * .37f, agent.Walking));
            poses.AddRange(avatar.CreateSkinPoses(fresh.Remove(agent.ResidentId)));
            visible++;
        }
        VisibleAvatars = visible;
        // Bounded memory: release the least recently seen avatars beyond the budget.
        if (built.Count > MaximumBuilt)
            foreach (int id in built.Keys.Where(id => !wanted.Contains(id)).OrderBy(id => lastSeen.GetValueOrDefault(id)).Take(built.Count - MaximumBuilt).ToArray())
                if (built.TryRemove(id, out var old)) { lastSeen.Remove(id); old.Dispose(); }
    }

    /// <summary>Build (synchronously) the avatars for these residents; for probes.</summary>
    public void BuildNow(IEnumerable<int> residents)
    {
        foreach (int id in residents) Build(id);
    }

    private void BuildLoop()
    {
        while (!stopping)
        {
            wake.WaitOne(500);
            if (stopping) break;
            foreach (int id in wanted)
            {
                if (stopping) break;
                if (built.ContainsKey(id) || failed.ContainsKey(id)) continue;
                Build(id);
                break;   // re-read priorities after every build: the player may have moved
            }
        }
    }

    private void Build(int residentId)
    {
        if (built.ContainsKey(residentId)) return;
        try
        {
            if (Factory is null)
            {
                var clock = Stopwatch.StartNew();
                Factory = new UmaAvatarFactory(catalogPath, null,
                    [UmaAvatarSharedColorChannels.Skin, UmaAvatarSharedColorChannels.Hair, UmaAvatarSharedColorChannels.Eyes]);
                FactoryMilliseconds = clock.Elapsed.TotalMilliseconds;
            }
            var resident = Simulation.Residents[residentId];
            var started = Stopwatch.StartNew();
            var (recipe, wardrobe) = Recipe(Factory, resident);
            var avatar = new StormChaseAvatar(catalog, Factory, recipe, NpcSimulation.DisplayHeight(resident), wardrobe, "r" + residentId);
            LastBuildMilliseconds = started.Elapsed.TotalMilliseconds;
            if (!built.TryAdd(residentId, avatar)) avatar.Dispose();
            Interlocked.Increment(ref buildsCompleted);
        }
        catch (Exception error) when (error is InvalidDataException or InvalidOperationException or ArgumentException or KeyNotFoundException or IOException)
        {
            failed.TryAdd(residentId, 0);
            Console.WriteLine($"NPC avatar for resident {residentId} could not be built: {error.Message}");
        }
    }

    /// <summary>
    /// A resident's avatar recipe: race from sex, every DNA slider from their genes, colours from BiologyLib
    /// pigmentation (resolved to deferred tints by the avatar system), clothes and hair chosen per person.
    /// </summary>
    public static (AvatarAppearanceRecipe Recipe, string[] Wardrobe) Recipe(UmaAvatarFactory factory, NpcResident resident)
    {
        bool female = resident.Sex == BiologicalSex.Female;
        var genes = resident.Genes;
        var parameters = new Dictionary<AvatarParameterId, float>();
        foreach (var (trait, value) in genes.Morphology)
        {
            if (trait == "breastSize" && !female) continue;
            parameters[new AvatarParameterId("uma.dna." + trait)] = (float)value;
        }
        if (resident.AgeYears < 16)
        {
            // Childhood proportions on the adult archetype: larger head relative to the body, shorter limbs.
            float childhood = (float)Math.Clamp((16 - resident.AgeYears) / 12, 0, 1);
            void Shift(string trait, float delta) => parameters[new AvatarParameterId("uma.dna." + trait)] =
                Math.Clamp((parameters.TryGetValue(new AvatarParameterId("uma.dna." + trait), out var v) ? v : .5f) + delta * childhood, 0, 1);
            Shift("headSize", .3f); Shift("legSize", -.25f); Shift("armLength", -.15f); Shift("neckHeight", -.2f);
            Shift("muscleUpper", -.2f); Shift("muscleLower", -.2f); Shift("breastSize", -.5f); Shift("belly", .05f);
        }
        parameters[AvatarParameterIds.Height] = (float)genes.HeightPotential;
        parameters[AvatarParameterIds.Build] = (float)genes.Build;
        parameters[AvatarParameterIds.SkinPigmentation] = (float)genes.SkinPigmentation;
        parameters[AvatarParameterIds.HairPigmentation] = (float)genes.HairPigmentation;
        parameters[AvatarParameterIds.EyePigmentation] = (float)genes.EyePigmentation;
        var random = new NpcRandom(NpcRandom.Hash(resident.Seed, 0x57415244));
        string Pick(params string[] options) => options[random.Next(options.Length)];
        var wardrobe = new List<string>();
        if (female)
        {
            bool dress = random.NextDouble() < (resident.AgeYears is > 12 and < 70 ? .3 : .15);
            if (dress)
                wardrobe.Add(Pick("st_female_dress_01_Classic_Recipe", "st_female_dress_01_Denim_Recipe", "st_female_dress_01_Knit_Recipe",
                    "st_female_dress_01_Plain_Recipe", "st_female_dress_01_Stripes_Recipe", "st_female_dress_03_Recipe"));
            else
            {
                wardrobe.Add(Pick("st_female_shirt_05_Recipe", "st_female_shirt_Recipe", "st_female_pullover_01_Recipe"));
                wardrobe.Add(Pick("st_female_pants_01_classic_Recipe", "st_female_pants_01_denim_Recipe", "st_female_pants_01_denim_Recipe", "st_female_skirt_Recipe"));
            }
            wardrobe.Add(Pick("st_female_shoes_flat_01_Recipe", "st_female_shoes_flat_01_denim_Recipe", "st_female_shoes_Recipe", "st_sneakers_01_Recipe", "st_shoes_07_Recipe"));
            // Hair style follows the genome's hair texture a little (curlier hair is worn shorter or tied back).
            wardrobe.Add(genes.HairTexture > .7 ? Pick("st_female_hair_ponytail_LOD0_Recipe", "st_short_hair_female_Recipe")
                : random.NextDouble() < .06 ? "st_buzzcut_female_Recipe"
                : Pick("st_female_long_hair_Recipe", "st_female_hair_ponytail_LOD0_Recipe", "st_short_hair_female_Recipe", "st_female_long_hair_Recipe"));
            wardrobe.Add(Pick("st_eyebrows_01_female_Recipe", "st_eyebrows_02_female_Recipe", "st_eyebrows_03_female_Recipe", "st_eyebrows_04_female_Recipe", "st_eyebrows_05_female_Recipe"));
        }
        else
        {
            wardrobe.Add(Pick("st_male_shirt_01_Recipe", "st_male_shirt_01_squared_Recipe", "st_male_shirt_02_long_sleeve_Recipe"));
            wardrobe.Add(Pick("st_male_pants_01_jeans_Recipe", "st_male_pants_blue_jeans_01_Recipe"));
            wardrobe.Add(Pick("st_shoes_07_Recipe", "st_shoes_07_Recipe_black", "st_shoes_07_Recipe_turquoise", "st_sneakers_01_Recipe"));
            wardrobe.Add(resident.AgeYears > 55 ? Pick("st_short_hair_Recipe", "st_buzzcut_male_Recipe")
                : genes.HairTexture > .65 ? Pick("st_long_hair_male_Recipe", "st_short_hair_Recipe")
                : Pick("st_short_hair_Recipe", "st_short_hair_Recipe", "st_buzzcut_male_Recipe", "st_long_hair_male_Recipe"));
            wardrobe.Add(Pick("st_eyebrows_01_male_Recipe", "st_eyebrows_02_male_Recipe", "st_eyebrows_03_male_Recipe", "st_eyebrows_04_male_Recipe", "st_eyebrows_05_male_Recipe"));
            if (resident.AgeYears >= 20 && random.NextDouble() < .4) wardrobe.Add("st_male_beard_stub_01_Recipe");
            if (random.NextDouble() < .18) wardrobe.Add(Pick("St_baseball_hat_01_Recipe", "St_baseball_hat_01_o3n_Recipe", "St_baseball_hat_01_vintage_Recipe"));
        }
        var archetype = factory.ResolveArchetypeId(female ? "o3n Stunner Jane" : "o3n Stunner John");
        var recipe = new AvatarAppearanceRecipe(archetype, ageYears: Math.Max(16, resident.AgeYears), dnaParameters: parameters,
            wardrobeItems: wardrobe.Select(factory.ResolveWardrobeItemId));
        return (recipe, wardrobe.ToArray());
    }

    /// <summary>The closest resident within reach of the player, for the HUD.</summary>
    public string? Nearby(Vector3f player, NpcTime now, float reach = 7)
    {
        var agents = Simulation.AgentsNear(new(player.X, player.Z), reach, now, 1);
        return agents.Count == 0 ? null : Simulation.Describe(agents[0].ResidentId, now);
    }

    public void Save()
    {
        if (savePath is null || Simulation.Biology is not { } biology) return;
        biology.Complete();
        string temporary = savePath + ".tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(savePath))!);
        using (var file = File.Create(temporary)) biology.Save(file);
        File.Move(temporary, savePath, true);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        stopping = true; wake.Set();
        if (builder.IsAlive) builder.Join(120_000);
        try { Save(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Console.WriteLine("NPC biology could not be saved: " + error.Message); }
        Simulation.Dispose();
        foreach (var avatar in built.Values) avatar.Dispose();
        wake.Dispose();
    }
}
