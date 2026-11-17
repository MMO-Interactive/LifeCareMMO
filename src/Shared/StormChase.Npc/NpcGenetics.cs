using BiologyLib.Development;
using BiologyLib.Inheritance;
using BiologyLib.Phenotypes;
using BiologyLib.Physiology.Human;

namespace StormChase.Npc;

/// <summary>
/// What a person looks like because of their genes: BiologyLib's appearance traits (pigmentation, hair texture,
/// height potential) plus one value per morphology locus (face and body shape). All values are 0..1, 0.5 typical.
/// </summary>
public sealed record NpcAppearanceGenes(double EyePigmentation, double HairPigmentation, double HairTexture,
    double SkinPigmentation, double HeightPotential, double Build, IReadOnlyDictionary<string, double> Morphology);

/// <summary>
/// Genomes for the population: BiologyLib's 23-pair <see cref="DiploidGenome"/> (its fixture loci for pigmentation,
/// hair texture and height) extended with synthetic additive morphology loci, one per avatar shape slider, spread
/// over the 22 autosomes. Children receive one homolog of every pair from each parent, so face and body shape are
/// inherited (and linked) the same way the pigmentation genes are.
/// </summary>
public static class NpcGenetics
{
    /// <summary>Shape traits, named after the avatar system's UMA DNA sliders they drive.</summary>
    public static IReadOnlyList<string> MorphologyTraits { get; } =
    [
        "headSize", "headWidth", "foreheadSize", "foreheadPosition", "eyeSize", "eyeDistance", "eyePosition", "eyeRotation", "eyeDepth", "eyeSquint",
        "earsSize", "earsPosition", "earsRotation", "noseSize", "noseWidth", "nosePosition", "nosePronounced", "noseFlatten", "noseBridge", "noseCurve",
        "noseInclination", "cheekSize", "cheekPosition", "lowCheek", "jawsSize", "jawsPosition", "mandibleSize", "chinSize", "chinPosition", "chinPronounced",
        "mouthSize", "mouthPosition", "mouthPronounced", "lipsSize", "neckThickness", "neckHeight", "armLength", "armWidth", "forearmWidth", "handsSize",
        "legSize", "legSeparation", "feetSize", "waist", "belly", "gluteusSize", "gluteusWidth", "breastSize", "muscleUpper", "muscleLower", "muscleTone",
    ];

    private static readonly Dictionary<string, LocusId> MorphologyLoci =
        MorphologyTraits.ToDictionary(t => t, t => new LocusId("MORPH-" + t));

    /// <summary>An unrelated founder (an adult who moved to town): fixture genome plus random morphology alleles.</summary>
    public static DiploidGenome Founder(BiologicalSex sex, uint seed)
    {
        var random = new Random((int)NpcRandom.Hash(seed, 0x47454e45));
        var genome = HumanGeneticsFixture.CreateFounderGenome(sex == BiologicalSex.Female ? Gender.Female : Gender.Male, random);
        return new DiploidGenome(genome.ChromosomePairs.Select(pair => pair.Number > 22 ? pair : new ChromosomePair(pair.Number,
            WithMorphology(pair.Maternal, random), WithMorphology(pair.Paternal, random))));
    }

    /// <summary>
    /// A child of <paramref name="mother"/> and <paramref name="father"/>: each autosome pair takes one homolog from
    /// each parent (independent assortment), and the father's 23rd chromosome decides the child's sex.
    /// Fertilization itself uses BiologyLib's <see cref="FertilizationService"/> semantics (ovum maternal, sperm paternal).
    /// </summary>
    public static DiploidGenome Child(DiploidGenome mother, DiploidGenome father, BiologicalSex sex, uint seed)
    {
        var random = new NpcRandom(NpcRandom.Hash(seed, 0x4b494e44));
        Chromosome Pick(ChromosomePair pair) => random.NextDouble() < .5 ? pair.Maternal : pair.Paternal;
        var pairs = new List<ChromosomePair>(23);
        for (int n = 1; n <= 23; n++)
        {
            var m = mother.ChromosomePairs.Single(p => p.Number == n);
            var f = father.ChromosomePairs.Single(p => p.Number == n);
            if (n < 23) { pairs.Add(new(n, Pick(m), Pick(f))); continue; }
            // Sex chromosomes: an X from the mother; X or Y from the father, as the child's sex requires.
            var fromFather = sex == BiologicalSex.Male
                ? (f.Paternal.Kind == ChromosomeKind.Y ? f.Paternal : f.Maternal)
                : (f.Paternal.Kind == ChromosomeKind.X ? f.Paternal : f.Maternal);
            pairs.Add(new(n, Pick(m), fromFather));
        }
        return new DiploidGenome(pairs);
    }

    /// <summary>Phenotype from a genome. Developmental noise is seeded by the person, so it is reproducible.</summary>
    public static NpcAppearanceGenes Express(DiploidGenome genome, uint seed)
    {
        var context = new EmbryoDevelopmentContext(HumanGeneticsFixture.CreateAppearanceResolver(new Random((int)NpcRandom.Hash(seed, 0x4150))))
        {
            OxygenSupport = 1, NutrientSupport = 1, HormoneSupport = 1,
        };
        var appearance = context.AppearanceResolver.Resolve(genome, context);
        var random = new NpcRandom(NpcRandom.Hash(seed, 0x4d4f5250));
        var morphology = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var trait in MorphologyTraits)
        {
            double sum = 0;
            foreach (var pair in genome.ChromosomePairs)
                foreach (var chromosome in new[] { pair.Maternal, pair.Paternal })
                    if (chromosome.Alleles.TryGetValue(MorphologyLoci[trait], out var allele))
                        sum += allele.Contributions.Sum(c => c.Effect);
            // Two additive alleles of ±1 → mostly 0.32–0.68, with a little developmental variation.
            morphology[trait] = Math.Clamp(.5 + sum * .09 + (random.NextDouble() - .5) * .05, .05, .95);
        }
        double build = Math.Clamp(.5 + (morphology["belly"] - .5) * .6 + (morphology["waist"] - .5) * .6 + (random.NextDouble() - .5) * .08, .05, .95);
        return new(appearance.EyePigmentation.FinalValue, appearance.HairPigmentation.FinalValue, appearance.HairTexture.FinalValue,
            appearance.SkinPigmentation.FinalValue, appearance.HeightPotential.FinalValue, build, morphology);
    }

    private static Chromosome WithMorphology(Chromosome chromosome, Random random)
    {
        var alleles = chromosome.Alleles.ToDictionary(p => p.Key, p => p.Value);
        for (int i = 0; i < MorphologyTraits.Count; i++)
        {
            if (i % 22 + 1 != chromosome.Number) continue;
            string trait = MorphologyTraits[i];
            // Population allele effects are roughly normal around 0.
            double effect = Math.Clamp((random.NextDouble() + random.NextDouble() + random.NextDouble() - 1.5) * 1.15, -1, 1);
            var id = new AlleleId($"MORPH-{trait}-{effect:+0.000;-0.000}");
            alleles[MorphologyLoci[trait]] = new Allele(id, new GeneId("MORPH-" + trait), id.Value, InheritanceMode.Additive, 1,
                [new TraitContribution(new TraitId("morphology-" + trait), effect)]);
        }
        return new Chromosome(chromosome.Number, chromosome.Kind, alleles);
    }
}
