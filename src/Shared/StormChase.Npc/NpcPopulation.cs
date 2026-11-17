using System.Numerics;
using BiologyLib.Inheritance;
using BiologyLib.Organisms;
using BiologyLib.Physiology.Human;

namespace StormChase.Npc;

/// <summary>Campaign time: day 1 starts at hour 0. <see cref="TotalHours"/> is monotonic across midnights.</summary>
public readonly record struct NpcTime(int Day, double Hour)
{
    public double TotalHours => (Day - 1) * 24d + Hour;
    public static NpcTime FromTotalHours(double hours)
    {
        int day = (int)Math.Floor(hours / 24) + 1;
        return new(day, hours - (day - 1) * 24d);
    }
    /// <summary>Biology clock: campaign day 1 00:00 is a fixed calendar date so bodies are deterministic.</summary>
    public static readonly DateTimeOffset Epoch = new(2040, 5, 1, 0, 0, 0, TimeSpan.Zero);
    public DateTimeOffset ToBiologyTime() => Epoch.AddHours(TotalHours);
}

public sealed record NpcResident(int Id, string FirstName, string LastName, BiologicalSex Sex, double AgeYears,
    int HomePlace, int? WorkPlace, int Household, float WalkSpeed, uint Seed, int? MotherId, int? FatherId, NpcAppearanceGenes Genes)
{
    public string Name => FirstName + " " + LastName;
    public OrganismId OrganismId => new(NpcRandom.Guid(Seed, 0x6f72));
    public bool IsChild => AgeYears < 18;
}

/// <summary>A generated population: residents plus each one's genome (index = resident id).</summary>
public sealed record NpcPopulationResult(IReadOnlyList<NpcResident> Residents, IReadOnlyList<DiploidGenome> Genomes);

/// <summary>
/// Deterministic population for one world seed. Every home holds a household: a single adult, a couple, a couple
/// with children or a single parent with children. Couples are unrelated founders; children are conceived from
/// their parents' genomes (an absent parent still has a genome), so a family resembles itself.
/// </summary>
public static class NpcPopulation
{
    private static readonly string[] FemaleNames = ["Mary","Linda","Susan","Karen","Nancy","Betty","Sandra","Donna","Carol","Ruth","Sharon","Michelle",
        "Laura","Sarah","Kimberly","Deborah","Jessica","Shirley","Cynthia","Angela","Melissa","Brenda","Amy","Anna","Rebecca","Kathleen",
        "Emma","Olivia","Ava","Hannah","Grace","Abigail","Megan","Rachel","Lauren","Paige","Joan","Evelyn","Marilyn","Darlene","Lily","Chloe","Zoe","Ella"];
    private static readonly string[] MaleNames = ["James","Robert","John","Michael","William","David","Richard","Joseph","Thomas","Charles",
        "Daniel","Matthew","Mark","Donald","Steven","Paul","Andrew","Kenneth","Joshua","Kevin","Brian","George","Ronald","Timothy","Jason",
        "Jeffrey","Ryan","Gary","Eric","Larry","Dale","Wayne","Dean","Roger","Travis","Cody","Tyler","Luke","Caleb","Wyatt","Owen","Logan","Jack","Henry"];
    private static readonly string[] LastNames = ["Olsen","Miller","Johnson","Anderson","Peterson","Schmidt","Meyer","Larson","Nelson","Hansen",
        "Becker","Wagner","Schultz","Koch","Hoffman","Carlson","Erickson","Lindquist","Swanson","Bauer","Krueger","Fischer","Weber","Klein",
        "Thompson","Harris","Clark","Lewis","Walker","Hall","Young","King","Wright","Scott","Green","Baker","Adams","Nelsen","Mitchell","Roberts",
        "Turner","Phillips","Campbell","Parker","Evans","Edwards","Collins","Stewart","Morris","Rogers","Reed","Cook","Morgan","Bell","Murphy"];

    public static NpcPopulationResult Generate(int worldSeed, NpcMap map, int? maximumResidents = null)
    {
        var random = new NpcRandom(NpcRandom.Hash((uint)worldSeed, 0x4e5043));
        var residents = new List<NpcResident>();
        var genomes = new List<DiploidGenome>();
        int household = 0, founders = 0;
        uint SeedFor(int index) => NpcRandom.Hash((uint)worldSeed, (uint)index * 2654435761u + 17);
        uint FounderSeed() => NpcRandom.Hash((uint)worldSeed ^ 0xF0F0, (uint)founders++ * 2246822519u + 5);
        bool Full() => maximumResidents is { } limit && residents.Count >= limit;
        BiologicalSex RandomSex() => random.NextDouble() < .5 ? BiologicalSex.Female : BiologicalSex.Male;
        string First(BiologicalSex sex) => sex == BiologicalSex.Female ? FemaleNames[random.Next(FemaleNames.Length)] : MaleNames[random.Next(MaleNames.Length)];

        int Add(string first, string last, BiologicalSex sex, double age, int home, DiploidGenome genome, int? mother, int? father)
        {
            int id = residents.Count;
            uint seed = SeedFor(id);
            float speed = (float)(age < 12 ? 1.0 + random.NextDouble() * .2 : 1.18 + random.NextDouble() * .32 - Math.Max(0, age - 60) * .006);
            residents.Add(new(id, first, last, sex, Math.Round(age, 2), home, null, household, speed, seed, mother, father, NpcGenetics.Express(genome, seed)));
            genomes.Add(genome);
            return id;
        }

        foreach (var home in map.Places.Where(p => p.Kind == NpcPlaceKind.Home))
        for (int h = 0; h < home.Households; h++, household++)
        {
            if (Full()) break;
            string last = LastNames[random.Next(LastNames.Length)];
            double roll = random.NextDouble();
            if (roll < .24)
            {
                // Single adult.
                var sex = RandomSex();
                Add(First(sex), last, sex, 19 + random.NextDouble() * 70, home.Index, NpcGenetics.Founder(sex, FounderSeed()), null, null);
                continue;
            }
            bool withChildren = roll >= .46;
            bool singleParent = roll >= .88;
            double motherAge = withChildren ? 22 + random.NextDouble() * 30 : 20 + random.NextDouble() * 66;
            double fatherAge = Math.Clamp(motherAge + (random.NextDouble() - .35) * 8, 19, 95);
            var motherGenome = NpcGenetics.Founder(BiologicalSex.Female, FounderSeed());
            var fatherGenome = NpcGenetics.Founder(BiologicalSex.Male, FounderSeed());
            int? motherId = null, fatherId = null;
            bool motherHome = !singleParent || random.NextDouble() < .8;
            if (motherHome) motherId = Add(First(BiologicalSex.Female), last, BiologicalSex.Female, motherAge, home.Index, motherGenome, null, null);
            if ((!singleParent || !motherHome) && !Full())
                fatherId = Add(First(BiologicalSex.Male), last, BiologicalSex.Male, fatherAge, home.Index, fatherGenome, null, null);
            if (!withChildren) continue;
            int children = 1 + random.Next(3);
            double maximumChildAge = Math.Min(motherAge - 18, 24);
            for (int c = 0; c < children && !Full(); c++)
            {
                var sex = RandomSex();
                double age = Math.Max(.2, random.NextDouble() * maximumChildAge);
                int id = residents.Count;
                var genome = NpcGenetics.Child(motherGenome, fatherGenome, sex, SeedFor(id));
                Add(First(sex), last, sex, age, home.Index, genome, motherId, fatherId);
            }
        }
        AssignJobs(map, residents, random);
        return new(residents, genomes);
    }

    private static void AssignJobs(NpcMap map, List<NpcResident> residents, NpcRandom random)
    {
        var free = map.Places.Where(p => p.Jobs > 0).ToDictionary(p => p.Index, p => p.Jobs);
        var bySettlement = map.Places.Where(p => p.Jobs > 0).GroupBy(p => p.Settlement).ToDictionary(g => g.Key, g => g.ToArray());
        for (int i = 0; i < residents.Count; i++)
        {
            var resident = residents[i];
            if (resident.AgeYears < 18 || resident.AgeYears > 66 || random.NextDouble() < .14) continue;   // child, retired or not employed
            var home = map.Places[resident.HomePlace];
            if (!bySettlement.TryGetValue(home.Settlement, out var candidates)) continue;
            var open = candidates.Where(p => free[p.Index] > 0)
                .OrderBy(p => Vector2.DistanceSquared(p.Door, home.Door)).ThenBy(p => p.Index).Take(4).ToArray();
            if (open.Length == 0) continue;
            var job = open[random.Next(open.Length)];
            free[job.Index]--;
            residents[i] = resident with { WorkPlace = job.Index };
        }
    }
}

/// <summary>Small deterministic generator (SplitMix-style); independent of System.Random's implementation.</summary>
public sealed class NpcRandom(uint seed)
{
    private ulong state = seed * 0x9E3779B97F4A7C15UL + 0x632BE59BD9B4E019UL;
    public ulong NextUInt64()
    {
        ulong z = state += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }
    public double NextDouble() => (NextUInt64() >> 11) * (1.0 / (1UL << 53));
    public int Next(int maximum) => maximum <= 0 ? 0 : (int)(NextUInt64() % (ulong)maximum);
    public double Range(double low, double high) => low + NextDouble() * (high - low);

    public static uint Hash(uint a, uint b)
    {
        unchecked
        {
            uint h = a * 0x8DA6B343u ^ b * 0xD8163841u ^ 0x9E3779B9u;
            h ^= h >> 15; h *= 0x2C1B3C6Du; h ^= h >> 12; h *= 0x297A2D39u; h ^= h >> 15;
            return h;
        }
    }
    public static Guid Guid(uint seed, uint salt)
    {
        var random = new NpcRandom(Hash(seed, salt));
        Span<byte> bytes = stackalloc byte[16];
        BitConverter.TryWriteBytes(bytes, random.NextUInt64());
        BitConverter.TryWriteBytes(bytes[8..], random.NextUInt64());
        bytes[7] = (byte)(bytes[7] & 0x0F | 0x40); bytes[8] = (byte)(bytes[8] & 0x3F | 0x80);
        return new Guid(bytes);
    }
}
