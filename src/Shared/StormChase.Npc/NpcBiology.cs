using System.IO.Compression;
using System.Text.Json;
using BiologyLib.Physiology.Human;
using BiologyLib.Physiology.Human.Gastroenterology;
using BiologyLib.Physiology.Human.Metabolism;

namespace StormChase.Npc;

/// <summary>Published, immutable summary of a resident's body (read from any thread).</summary>
public sealed record NpcVitals(double AgeYears, double HeartRateBpm, double RespiratoryRatePerMinute, double CoreTemperatureCelsius,
    double AlertnessPercent, double ActivityTolerancePercent, double HeightMeters, double MassKilograms, double ObservedTotalHours);

/// <summary>
/// One BiologyLib body per resident, owned by a single background thread. Bodies advance only to
/// half-hour campaign boundaries and take meals at their scheduled times, so the result depends on
/// campaign time alone, never on frame rate or when the thread happened to run.
/// </summary>
public sealed class NpcBiology : IDisposable
{
    public const double StepHours = .5;
    private readonly IReadOnlyList<NpcResident> residents;
    private readonly HumanBodySimulation?[] bodies;
    private readonly double[] advancedTo;
    private readonly NpcVitals?[] vitals;
    private readonly double createdAt;
    private readonly object bodyGate = new();
    private readonly Thread thread;
    private readonly AutoResetEvent wake = new(false);
    private volatile bool stopping;
    private double target;
    private long passes;

    public NpcBiology(IReadOnlyList<NpcResident> residents, NpcTime createdAt, IReadOnlyDictionary<int, HumanBodySimulationState>? restored = null,
        bool background = true)
    {
        this.residents = residents;
        bodies = new HumanBodySimulation?[residents.Count];
        advancedTo = new double[residents.Count];
        vitals = new NpcVitals?[residents.Count];
        this.createdAt = Boundary(createdAt.TotalHours);
        target = this.createdAt;
        if (restored is not null)
            foreach (var (id, state) in restored)
            {
                if ((uint)id >= bodies.Length) continue;
                bodies[id] = HumanBodySimulation.Restore(state);
                advancedTo[id] = (bodies[id]!.Snapshot().ObservedGameUtcTicks - NpcTime.Epoch.UtcTicks) / (double)TimeSpan.TicksPerHour;
                Publish(id);
            }
        thread = new(Run) { IsBackground = true, Name = "Storm Chase NPC biology", Priority = ThreadPriority.BelowNormal };
        if (background) thread.Start();
    }

    public int BodyCount { get { lock (bodyGate) return bodies.Count(b => b is not null); } }
    public long CompletedPasses => Interlocked.Read(ref passes);
    public double TargetHours => Volatile.Read(ref target);
    public NpcVitals? Vitals(int resident) => (uint)resident < vitals.Length ? Volatile.Read(ref vitals[resident]) : null;

    /// <summary>Request bodies to reach the latest half-hour boundary at or before <paramref name="now"/>.</summary>
    public void AdvanceTo(NpcTime now)
    {
        double boundary = Math.Max(createdAt, Boundary(now.TotalHours));
        if (boundary <= Volatile.Read(ref target)) return;
        Volatile.Write(ref target, boundary);
        wake.Set();
    }

    /// <summary>Synchronously bring every body to the current target (probes and saving).</summary>
    public void Complete()
    {
        double goal = Volatile.Read(ref target);
        for (int i = 0; i < residents.Count; i++) Step(i, goal);
        Interlocked.Increment(ref passes);
    }

    private void Run()
    {
        while (!stopping)
        {
            wake.WaitOne(1000);
            if (stopping) break;
            double goal = Volatile.Read(ref target);
            for (int i = 0; i < residents.Count && !stopping; i++) Step(i, goal);
            Interlocked.Increment(ref passes);
        }
    }

    private void Step(int i, double goal)
    {
        lock (bodyGate)
        {
            var resident = residents[i];
            var body = bodies[i];
            if (body is null)
            {
                var created = NpcTime.FromTotalHours(createdAt);
                body = bodies[i] = HumanBodySimulation.Create(resident.OrganismId, resident.Sex,
                    NpcTime.Epoch.AddDays(-resident.AgeYears * 365.2425), created.ToBiologyTime());
                advancedTo[i] = createdAt;
                Publish(i);
            }
            if (goal <= advancedTo[i]) return;
            // Meals between the body's last time and the goal, in order, on each day touched.
            for (int day = NpcTime.FromTotalHours(advancedTo[i]).Day; day <= NpcTime.FromTotalHours(goal).Day; day++)
                foreach (var meal in Meals(resident, day))
                {
                    if (meal.Hour <= advancedTo[i] || meal.Hour > goal) continue;
                    var at = NpcTime.FromTotalHours(meal.Hour).ToBiologyTime();
                    body.AdvanceTo(at);
                    advancedTo[i] = meal.Hour;
                    var source = NpcRandom.Guid(resident.Seed, (uint)(meal.Hour * 10));
                    body.RecordGastrointestinalIntake(GastrointestinalIntakeRequest.Create(source, body.PersonId,
                        GastrointestinalSourceKind.Meal, at.UtcTicks, waterSimulationMl: meal.WaterMl,
                        dryDietaryResidueGrams: meal.Carbohydrate * .2, sourceProductId: meal.Name));
                    body.RecordNutritionDelivery(NutritionDeliveryRequest.Create(source, body.PersonId, meal.Name, at.UtcTicks,
                        carbohydrateGrams: meal.Carbohydrate, gluconeogenicSubstrateGlucoseEquivalentGrams: meal.Carbohydrate * .25));
                }
            body.AdvanceTo(NpcTime.FromTotalHours(goal).ToBiologyTime());
            advancedTo[i] = goal;
            Publish(i);
        }
    }

    private void Publish(int i)
    {
        var s = bodies[i]!.Snapshot();
        Volatile.Write(ref vitals[i], new NpcVitals(s.AgeYears, s.Cardiovascular.HeartRateBpm, s.Respiratory.RespiratoryRatePerMinute,
            s.Endocrine.CoreTemperatureCelsius, s.Nervous.AlertnessPercent, s.Muscular.ActivityTolerancePercent,
            s.Anatomy.StandingHeightMillimeters / 1000d, s.Anatomy.BodyMassKilograms, advancedTo[i]));
    }

    /// <summary>Meals use the same wake time as the resident's plan (first draw of that day's stream).</summary>
    public static IEnumerable<(double Hour, string Name, double Carbohydrate, double WaterMl)> Meals(NpcResident resident, int day)
    {
        double dayStart = (day - 1) * 24d, wake = NpcSchedule.WakeHour(resident, day);
        yield return (dayStart + wake + .4, "breakfast", 45, 300);
        yield return (dayStart + 12.4, "lunch", 70, 350);
        yield return (dayStart + 18.6, "dinner", 85, 400);
    }

    /// <summary>Saved bodies (gzip JSON). Bodies are captured at a consistent time: callers Complete() first.</summary>
    public void Save(Stream output)
    {
        lock (bodyGate)
        {
            using var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true);
            var states = new Dictionary<int, HumanBodySimulationState>();
            for (int i = 0; i < bodies.Length; i++) if (bodies[i] is { } body) states[i] = body.CaptureState();
            JsonSerializer.Serialize(gzip, states, JsonOptions);
        }
    }

    public static Dictionary<int, HumanBodySimulationState> Load(Stream input)
    {
        using var gzip = new GZipStream(input, CompressionMode.Decompress, leaveOpen: true);
        return JsonSerializer.Deserialize<Dictionary<int, HumanBodySimulationState>>(gzip, JsonOptions)
            ?? throw new InvalidDataException("Empty NPC biology save.");
    }

    /// <summary>Byte-exact fingerprint of one body's state (determinism checks).</summary>
    public string StateJson(int resident)
    {
        lock (bodyGate) return bodies[resident] is { } body ? JsonSerializer.Serialize(body.CaptureState(), JsonOptions) : "";
    }

    public static readonly JsonSerializerOptions JsonOptions = new() { IncludeFields = true };
    private static double Boundary(double totalHours) => Math.Floor(totalHours / StepHours + 1e-9) * StepHours;

    public void Dispose()
    {
        stopping = true; wake.Set();
        if (thread.IsAlive) thread.Join(2000);
        wake.Dispose();
    }
}
