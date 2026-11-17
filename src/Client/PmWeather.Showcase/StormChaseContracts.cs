using Shared.Contracts.World;

internal enum ChaseContractKind { EquipmentDelivery, WeatherSurvey, ReconRoute, CommunityOutreach, ReliefDelivery, DebrisClearance,
    // Player jobs (StormChaseContracts.Jobs.cs). Append only: the kind name is sent to clients.
    PolicePatrol, PoliceTraffic, PoliceBriefing, PoliceWarning, PoliceScene,
    FireInspection, FireSafetyTalk, FireRescue, EmsTransfer, EmsStandby, EmsInjured }
internal sealed record ChaseContractStop(string Name, Vector3f Position, string Instruction, float Radius = 12, float WorkSeconds = 0, bool OnFoot = false);
/// <param name="Job">Which player job's board this offer belongs to.</param>
/// <param name="Dynamic">Stops are resolved from live storms/damage, then frozen into the run when accepted.</param>
/// <param name="AtDamage">The work happens at destroyed buildings, so damage does not close it.</param>
internal sealed record ChaseContractOffer(int Id, int Day, ChaseContractKind Kind, string Title, string Description,
    long CashCents, int Reputation, int Popularity, IReadOnlyList<ChaseContractStop> Stops, bool Available = true,
    string Job = StormChaseJobs.Chaser, bool Dynamic = false, bool AtDamage = false);
internal sealed record ChaseContractReward(string ReceiptId, int ContractId, long CashCents, int Reputation, int Popularity);
internal sealed class ChaseContractRun
{
    public int OfferId { get; set; }
    public int Day { get; set; }
    public string InstanceId { get; set; } = Guid.NewGuid().ToString("N");
    public int Step { get; set; }
    public float WorkSeconds { get; set; }
    public bool Working { get; set; }
    public int ClearedPieces { get; set; }
    public Vector3f? CleanupOrigin { get; set; }
    /// <summary>Stops of a storm call, frozen when it was accepted.</summary>
    public List<ChaseContractStop>? Stops { get; set; }
}
internal sealed class ChaseContractProgress
{
    public ChaseContractRun? Active { get; set; }
    public HashSet<int> Completed { get; set; } = [];
    public int TotalCompleted { get; set; }
    /// <summary>The player's job (StormChaseJobs); saved with the contract progress locally and on the server.</summary>
    public string Job { get; set; } = StormChaseJobs.Chaser;
}

/// <summary>Shared job rules. In multiplayer, all positions, work time, salvage decisions and receipts come from the server.</summary>
internal sealed partial class StormChaseContracts(StormChaseWorld world, int seed)
{
    private int cachedDay;
    private ChaseContractOffer[] cached = [];
    public string Message { get; private set; } = "O opens dispatch. Quiet-weather jobs fund your next chase.";
    public Func<IReadOnlyList<StormChaseDebrisState>>? Debris { get; set; }
    public Func<string, Vector3f, bool>? CollectDebris { get; set; }
    public Func<string, bool>? BuildingDestroyed { get; set; }

    /// <summary>The dispatch board of one player job for a day.</summary>
    public IReadOnlyList<ChaseContractOffer> Offers(int day, string job = StormChaseJobs.Chaser)
    {
        if (day is < 1 or > 100_000) throw new ArgumentOutOfRangeException(nameof(day));
        EnsureDay(day);
        // A cleanup job follows actual wreckage, never a fabricated damage marker.
        var debris = Debris?.Invoke().Where(body => !body.Collected && body.Sleeping && body.Archived).ToArray() ?? [];
        var first = debris.OrderBy(body => body.PartId, StringComparer.Ordinal).FirstOrDefault(body =>
            debris.Where(other => Distance(body.Position, other.Position) < 170).Take(3).Count() == 3);
        return cached.Where(offer => offer.Job == job).Select(offer =>
        {
            if (offer.Kind == ChaseContractKind.DebrisClearance && first is not null)
                offer = offer with { Available = true, Stops = [new("Storm damage", first.Position,
                    "On foot, collect three settled fragments nearby with H.", 180, 0, true)] };
            else if (offer.Dynamic) offer = ResolveDynamic(offer);
            return offer with { Available = offer.Available && (offer.Kind == ChaseContractKind.DebrisClearance || offer.AtDamage
                || offer.Stops.All(stop => !IsDestroyedSite(stop.Position))) };
        }).ToArray();
    }

    public ChaseContractOffer? ActiveOffer(ChaseContractProgress progress)
    {
        if (progress.Active is not { } run) return null;
        // Active work already has an accepted immutable daily definition. Rebuilding
        // the entire board here would rescan every settled fragment on each physics tick.
        EnsureDay(run.Day);
        var offer = cached.FirstOrDefault(offer => offer.Id == run.OfferId);
        if (offer is not null && run.Stops is { Count: > 0 } frozen) offer = offer with { Stops = frozen };
        return offer is { Kind: ChaseContractKind.DebrisClearance } && run.CleanupOrigin is { } origin
            ? offer with { Stops = [new("Storm damage", origin, $"Collect settled fragments: {run.ClearedPieces}/3. Press H beside a piece.", 180, 0, true)] } : offer;
    }
    private void EnsureDay(int day)
    {
        if (day is < 1 or > 100_000) throw new ArgumentOutOfRangeException(nameof(day));
        if (cachedDay != day) { cachedDay = day; cached = CreateOffers(day); }
    }
    public ChaseContractStop? Target(ChaseContractProgress progress) => ActiveOffer(progress) is { } offer && progress.Active is { } run
        && run.Step >= 0 && run.Step < offer.Stops.Count ? offer.Stops[run.Step] : null;

    public bool Accept(int id, int day, ChaseContractProgress progress)
    {
        if (progress.Active is not null) { Message = "Finish or cancel your active assignment first."; return false; }
        var offer = Offers(day, progress.Job).FirstOrDefault(item => item.Id == id);
        if (offer is null || !offer.Available || progress.Completed.Contains(id))
        { Message = "That assignment is no longer available."; return false; }
        progress.Active = new() { OfferId = id, Day = day, CleanupOrigin = offer.Kind == ChaseContractKind.DebrisClearance ? offer.Stops[0].Position : null,
            Stops = offer.Dynamic ? offer.Stops.ToList() : null };
        Message = offer.Title + " accepted. Your first stop is marked on the map.";
        return true;
    }
    public void Cancel(ChaseContractProgress progress)
    { progress.Active = null; Message = "Assignment cancelled. Select another job from dispatch."; }

    public bool Interact(ChaseContractProgress progress, Vector3f feet, Vector3f vehicle, bool driving, float speed, float windMph)
    {
        if (ActiveOffer(progress) is not { } offer || progress.Active is not { } run || Target(progress) is not { } stop)
        { Message = "Accept an assignment from dispatch first."; return false; }
        if (!CanWork(stop, feet, vehicle, driving, speed, windMph, offer.Kind == ChaseContractKind.DebrisClearance || offer.AtDamage))
        { Message = stop.OnFoot && driving ? "Leave your vehicle to do this work." : windMph >= 45
            ? "Conditions are unsafe. Resume this assignment after the wind eases." : "Park at the marked assignment stop."; return false; }
        if (offer.Kind == ChaseContractKind.DebrisClearance)
        {
            var nearest = Debris?.Invoke().Where(body => !body.Collected && body.Sleeping && body.Archived
                && Distance(body.Position, feet) <= 6 && MathF.Abs(body.Position.Y - feet.Y) <= 4)
                .OrderBy(body => Distance(body.Position, feet)).FirstOrDefault();
            if (nearest is null || CollectDebris?.Invoke(nearest.PartId, feet) != true)
            { Message = "Move within 6 m of a settled fragment. Keep clear of moving wreckage."; return false; }
            run.ClearedPieces++;
            Message = $"Cleanup: {run.ClearedPieces}/3 fragments collected.";
            if (run.ClearedPieces >= 3) run.Step = offer.Stops.Count;
        }
        else if (stop.WorkSeconds > 0)
        { run.Working = true; Message = stop.Instruction + " Stay at the marked spot while the work finishes."; }
        else Advance(run, offer);
        return true;
    }

    public void Update(ChaseContractProgress progress, float deltaSeconds, Vector3f feet, Vector3f vehicle,
        bool driving, float speed, float windMph, Func<ChaseContractReward, bool> commitReward)
    {
        if (!float.IsFinite(deltaSeconds) || deltaSeconds < 0 || deltaSeconds > 1) throw new ArgumentOutOfRangeException(nameof(deltaSeconds));
        if (progress.Active is not { } run || ActiveOffer(progress) is not { } offer) return;
        if (run.Step < 0 || run.Step > offer.Stops.Count || !float.IsFinite(run.WorkSeconds) || run.InstanceId is not { Length: 32 })
        { Cancel(progress); return; }
        if (run.Step < offer.Stops.Count && run.Working)
        {
            var stop = offer.Stops[run.Step];
            if (!CanWork(stop, feet, vehicle, driving, speed, windMph, offer.AtDamage))
            { run.Working = false; Message = "Work paused. Return to the marker and press H to continue."; }
            else
            {
                run.WorkSeconds = Math.Min(stop.WorkSeconds, run.WorkSeconds + deltaSeconds);
                if (run.WorkSeconds >= stop.WorkSeconds) Advance(run, offer);
            }
        }
        if (run.Step < offer.Stops.Count) return;
        // The same durable receipt survives retry/reconnect; payment and completion must commit together.
        var reward = new ChaseContractReward("contract:" + run.InstanceId, offer.Id, offer.CashCents, offer.Reputation, offer.Popularity);
        if (!commitReward(reward)) { Message = "Assignment complete. Waiting for the career service to save your reward."; return; }
        progress.Completed.Add(offer.Id); progress.TotalCompleted++; progress.Active = null;
        Message = $"{offer.Title} completed: +${offer.CashCents / 100m:0}, +{offer.Reputation} reputation, +{offer.Popularity} popularity.";
    }

    private bool CanWork(ChaseContractStop stop, Vector3f feet, Vector3f vehicle, bool driving, float speed, float windMph, bool cleanup = false) =>
        float.IsFinite(speed) && float.IsFinite(windMph) && speed <= .7f && windMph < 45 && (!stop.OnFoot || !driving)
        && Distance(stop.Position, driving ? vehicle : feet) <= stop.Radius
        && MathF.Abs(stop.Position.Y - (driving ? vehicle.Y : feet.Y)) <= 5 && (cleanup || !IsDestroyedSite(stop.Position));
    private bool IsDestroyedSite(Vector3f position) => BuildingDestroyed is not null && world.Buildings.Any(building =>
        Distance(building.Position, position) < 35 && BuildingDestroyed(building.Id));
    private void Advance(ChaseContractRun run, ChaseContractOffer offer)
    {
        run.Step++; run.WorkSeconds = 0; run.Working = false;
        Message = run.Step >= offer.Stops.Count ? "Assignment complete. Saving your reward…" : "Next stop: " + offer.Stops[run.Step].Name + ".";
    }
    private ChaseContractOffer[] CreateOffers(int day)
    {
        var stations = world.GasStations.OrderBy(station => station.Id, StringComparer.Ordinal).ToArray();
        if (stations.Length < 3) throw new InvalidOperationException("Dispatch needs three connected service stations.");
        var random = new Random(unchecked(seed * 397 ^ day * 7919));
        int start = random.Next(stations.Length);
        var origin = stations[start];
        var nearby = stations.Where(station => station.Id != origin.Id).OrderBy(station => Distance(station.ParkingPosition, origin.ParkingPosition)).ToArray();
        var destination = nearby[0]; var second = nearby[1];
        var civic = world.BuildingInteriors.Where(interior => interior.Kind == "cityhall")
            .OrderBy(interior => Distance(interior.InteriorFeet, origin.ParkingPosition)).First();
        var nextCivic = world.BuildingInteriors.Where(interior => interior.Kind == "cityhall" && interior.BuildingId != civic.BuildingId)
            .OrderBy(interior => Distance(interior.InteriorFeet, destination.ParkingPosition)).First();
        ChaseContractStop Station(StormChaseGasStation station, string text, float work = 0, bool foot = false) =>
            new(station.Name, station.ParkingPosition, text, 14, work, foot);
        ChaseContractOffer Job(int slot, ChaseContractKind kind, string title, string description, long cash, int rep, int popularity, params ChaseContractStop[] stops) =>
            new(checked(day * 100 + slot), day, kind, title, description, cash, rep, popularity, stops);
        return [
            Job(1, ChaseContractKind.EquipmentDelivery, "Field equipment run", "Collect the sensor crate and deliver it to the next service depot.", 45000, 6, 0,
                Station(origin, "Load the sensor crate.", 4), Station(destination, "Deliver the equipment.", 4)),
            Job(2, ChaseContractKind.WeatherSurvey, "Weather station calibration", "Take a sustained ground reading and calibrate the station instruments.", 27500, 10, 1,
                Station(origin, "Calibrate and record a surface weather observation.", 18, true)),
            Job(3, ChaseContractKind.ReconRoute, "Chase route reconnaissance", "Survey three linked service stops and file road-access observations.", 65000, 12, 2,
                Station(origin, "Record the first route checkpoint.", 6), Station(destination, "Survey the next access route.", 6), Station(second, "Submit the completed access report.", 6)),
            Job(4, ChaseContractKind.CommunityOutreach, "Storm safety workshop", "Visit the civic hall and help residents plan safe shelter routes.", 22000, 5, 8,
                new ChaseContractStop(civic.Name, civic.InteriorFeet, "Lead the community storm safety workshop.", 5, 35, true)),
            Job(5, ChaseContractKind.ReliefDelivery, "Community supply delivery", "Bring emergency supplies from the service depot to a town shelter.", 52500, 8, 6,
                Station(destination, "Load the emergency supplies.", 4), new(nextCivic.Name, nextCivic.ExteriorFeet, "Unload the shelter supplies.", 12, 6, true)),
            Job(6, ChaseContractKind.WeatherSurvey, "Regional observation", "Travel to the next station and log a careful surface observation.", 35000, 12, 2,
                Station(destination, "Observe and calibrate the regional station.", 22, true)),
            Job(7, ChaseContractKind.CommunityOutreach, "Shelter readiness visit", "Inspect a second community refuge and help prepare its supplies.", 25000, 5, 7,
                new ChaseContractStop(nextCivic.Name, nextCivic.InteriorFeet, "Help prepare the community refuge.", 5, 30, true)),
            Job(8, ChaseContractKind.DebrisClearance, "Storm damage cleanup", "Help clear three settled fragments from an actual damage site.", 37500, 8, 6,
                new ChaseContractStop("Awaiting storm damage", origin.ParkingPosition, "Find three safe, settled fragments.", 180, 0, true)) with { Available = false },
            .. CreateJobOffers(day, origin, destination, second, civic, nextCivic)
        ];
    }
    internal static float ContentValueMultiplier(int popularity) => 1 + Math.Clamp(popularity, 0, 200) / 100f;
    private static float Distance(Vector3f a, Vector3f b) => MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z));
}
