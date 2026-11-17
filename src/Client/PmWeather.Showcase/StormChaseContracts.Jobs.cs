using Shared.Contracts.World;

/// <summary>A storm as the job board sees it: where it is, where it is heading, whether a tornado is on the ground.</summary>
internal readonly record struct ChaseJobStorm(Vector3f Position, Vector3f Velocity, bool Tornadic);

/// <summary>
/// Emergency-service calls for the player jobs. Quiet-weather calls are fixed for the day (like the chaser board);
/// storm calls are resolved live from the actual world: a tornado's track (police warnings) and destroyed buildings
/// (scene security, search and rescue, injured residents). Once accepted, a storm call keeps the stops it was
/// accepted with (ChaseContractRun.Stops), so a moving storm cannot move the player's marker mid-assignment.
/// </summary>
internal sealed partial class StormChaseContracts
{
    /// <summary>Active storms (position, motion, tornado on the ground) from the frame or server weather.</summary>
    public Func<IReadOnlyList<ChaseJobStorm>>? Storms { get; set; }

    /// <summary>Go on duty in a job (or back to chasing). Any active assignment is handed back.</summary>
    public bool SelectJob(string jobId, ChaseContractProgress progress)
    {
        if (StormChaseJobs.Find(jobId) is not { } job) { Message = "Unknown job."; return false; }
        if (progress.Job == job.Id) { Message = job.Id == StormChaseJobs.Chaser ? "You are already chasing storms." : $"You are already on duty as a {job.Title.ToLowerInvariant()}."; return true; }
        progress.Active = null;
        progress.Job = job.Id;
        Message = job.Id == StormChaseJobs.Chaser
            ? "Off duty. Your own vehicle is at the station; the chase dispatch board is back."
            : $"On duty: {job.Title}, {job.Department}. Your department vehicle is at the station. {job.Summary}";
        return true;
    }

    private IEnumerable<ChaseContractOffer> CreateJobOffers(int day, StormChaseGasStation origin, StormChaseGasStation destination,
        StormChaseGasStation second, StormChaseBuildingInterior civic, StormChaseBuildingInterior nextCivic)
    {
        ChaseContractStop Station(StormChaseGasStation station, string text, float work = 0, bool foot = false) =>
            new(station.Name, station.ParkingPosition, text, 16, work, foot);
        ChaseContractStop Hall(StormChaseBuildingInterior hall, string text, float work, bool inside = true) =>
            inside ? new(hall.Name, hall.InteriorFeet, text, 5, work, true) : new(hall.Name, hall.ExteriorFeet, text, 16, work, false);
        ChaseContractOffer Call(int slot, string job, ChaseContractKind kind, string title, string description, long cash, int rep, int popularity,
            bool dynamic, bool atDamage, params ChaseContractStop[] stops) =>
            new(checked(day * 100 + slot), day, kind, title, description, cash, rep, popularity, stops,
                Available: !dynamic, Job: job, Dynamic: dynamic, AtDamage: atDamage);
        // Placeholder stop for storm calls until the world supplies a real one.
        var standby = Station(origin, "Stand by for a storm call.");

        // ------------------------------------------------------------------ police
        yield return Call(21, StormChaseJobs.Police, ChaseContractKind.PolicePatrol, "Highway patrol",
            "Drive the patrol loop through three service stops and log road conditions.", 42000, 8, 1, false, false,
            Station(origin, "Check in at the first patrol point.", 5), Station(destination, "Log the second patrol point.", 5),
            Station(second, "Close out the patrol at the last checkpoint.", 5));
        yield return Call(22, StormChaseJobs.Police, ChaseContractKind.PoliceTraffic, "Traffic control",
            "Park with your lights on at the station junction and keep traffic moving.", 30000, 6, 2, false, false,
            Station(destination, "Hold traffic control at the junction.", 25));
        yield return Call(23, StormChaseJobs.Police, ChaseContractKind.PoliceBriefing, "Storm safety briefing",
            "Brief residents at the civic hall on warning sirens and shelter routes.", 26000, 5, 7, false, false,
            Hall(civic, "Lead the storm safety briefing.", 25));
        yield return Call(24, StormChaseJobs.Police, ChaseContractKind.PoliceWarning, "Tornado warning",
            "A tornado is heading for a town. Get there first and sound the warning.", 120000, 20, 10, true, false, standby);
        yield return Call(25, StormChaseJobs.Police, ChaseContractKind.PoliceScene, "Secure damage scene",
            "Secure a storm-damaged building and keep people out of the wreckage.", 70000, 12, 4, true, true, standby);
        // ------------------------------------------------------------------ fire
        yield return Call(31, StormChaseJobs.Fire, ChaseContractKind.FireInspection, "Hydrant and equipment check",
            "Inspect the hydrants and pumps at two service depots.", 38000, 8, 1, false, false,
            Station(origin, "Flow-test the depot hydrant.", 8), Station(destination, "Inspect the second depot's pumps.", 8));
        yield return Call(32, StormChaseJobs.Fire, ChaseContractKind.FireSafetyTalk, "Shelter readiness inspection",
            "Inspect a community refuge's exits, extinguishers and supplies.", 28000, 6, 6, false, false,
            Hall(nextCivic, "Inspect the refuge's exits and supplies.", 28));
        yield return Call(33, StormChaseJobs.Fire, ChaseContractKind.FireRescue, "Search and rescue",
            "A building has collapsed. Search the wreckage on foot for trapped residents.", 110000, 18, 9, true, true, standby);
        yield return Call(34, StormChaseJobs.Fire, ChaseContractKind.DebrisClearance, "Clear storm debris",
            "Clear three settled fragments from an actual damage site.", 52000, 10, 5, false, true,
            new ChaseContractStop("Awaiting storm damage", origin.ParkingPosition, "Find three safe, settled fragments.", 180, 0, true)) with { Available = false };
        // ------------------------------------------------------------------ EMS
        yield return Call(41, StormChaseJobs.Ems, ChaseContractKind.EmsTransfer, "Patient transfer",
            "Carry a patient from the clinic in one town to the clinic in the next.", 46000, 8, 3, false, false,
            Hall(civic, "Load the patient at the clinic.", 6, inside: false), Hall(nextCivic, "Hand the patient over at the receiving clinic.", 6, inside: false));
        yield return Call(42, StormChaseJobs.Ems, ChaseContractKind.EmsStandby, "Shelter medical standby",
            "Staff the first-aid station at the civic hall shelter.", 30000, 6, 6, false, false,
            Hall(civic, "Staff the shelter's first-aid station.", 30));
        yield return Call(43, StormChaseJobs.Ems, ChaseContractKind.EmsInjured, "Injured resident",
            "A resident was hurt when the storm hit their building. Treat them on scene, then transport them to the clinic.",
            115000, 18, 9, true, true, standby, standby);
    }

    /// <summary>Resolve a storm call against the live world; unavailable (with its placeholder) when there is no such event.</summary>
    private ChaseContractOffer ResolveDynamic(ChaseContractOffer offer)
    {
        switch (offer.Kind)
        {
            case ChaseContractKind.PoliceWarning:
            {
                // The town whose centre lies 2-20 km ahead of a tornado and within 5 km of its track.
                StormChaseSettlement? town = null; float best = float.MaxValue;
                foreach (var storm in Storms?.Invoke() ?? [])
                {
                    if (!storm.Tornadic) continue;
                    float speed = MathF.Sqrt(storm.Velocity.X * storm.Velocity.X + storm.Velocity.Z * storm.Velocity.Z);
                    if (speed < .1f) continue;
                    float ux = storm.Velocity.X / speed, uz = storm.Velocity.Z / speed;
                    foreach (var settlement in world.Settlements)
                    {
                        float dx = settlement.Position.X - storm.Position.X, dz = settlement.Position.Z - storm.Position.Z;
                        float ahead = dx * ux + dz * uz, lateral = MathF.Abs(-dx * uz + dz * ux);
                        if (ahead is < 2000 or > 20000 || lateral > 5000 || ahead >= best) continue;
                        best = ahead; town = settlement;
                    }
                }
                var hall = town is null ? null : world.BuildingInteriors.Where(i => i.Kind == "cityhall")
                    .OrderBy(i => Distance(i.ExteriorFeet, town.Position)).FirstOrDefault();
                return hall is null || town is null ? offer with { Available = false } : offer with
                {
                    Title = "Tornado warning: " + town.Name, Available = true,
                    Stops = [new(town.Name + " civic hall", hall.ExteriorFeet, "Sound the sirens and direct residents to shelter.", 18, 10, false)],
                };
            }
            case ChaseContractKind.PoliceScene:
            case ChaseContractKind.FireRescue:
            case ChaseContractKind.EmsInjured:
            {
                // Each service takes a different collapsed building when several are down.
                var destroyed = DestroyedBuildings();
                int index = offer.Kind switch { ChaseContractKind.PoliceScene => 0, ChaseContractKind.FireRescue => 1, _ => 2 };
                if (destroyed.Count == 0) return offer with { Available = false };
                var building = destroyed[index % destroyed.Count];
                string place = world.Settlements.FirstOrDefault(s => s.Id == building.SettlementId)?.Name ?? "the county";
                var site = new Vector3f(building.Position.X, world.HeightAt(building.Position.X, building.Position.Z), building.Position.Z);
                string label = building.Kind + " in " + place;
                if (offer.Kind == ChaseContractKind.PoliceScene)
                    return offer with { Available = true, Title = "Secure damage scene: " + place,
                        Stops = [new("Damaged " + label, site, "Tape off the wreckage and keep people clear.", 40, 20, true)] };
                if (offer.Kind == ChaseContractKind.FireRescue)
                    return offer with { Available = true, Title = "Search and rescue: " + place,
                        Stops = [new("Collapsed " + label, site, "Search the collapsed building for trapped residents.", 40, 30, true)] };
                var clinic = world.BuildingInteriors.Where(i => i.Kind == "cityhall").OrderBy(i => Distance(i.ExteriorFeet, site)).First();
                return offer with { Available = true, Title = "Injured resident: " + place,
                    Stops = [new("Damaged " + label, site, "Treat the injured resident and load them into the ambulance.", 40, 15, true),
                             new(clinic.Name + " clinic", clinic.ExteriorFeet, "Hand the patient over at the clinic.", 16, 5, false)] };
            }
            default: return offer;
        }
    }

    private List<StormChaseBuilding> DestroyedBuildings() => BuildingDestroyed is null ? []
        : world.Buildings.Where(building => building.DestructiblePartIds.Count > 0 && BuildingDestroyed(building.Id))
            .OrderBy(building => building.Id, StringComparer.Ordinal).ToList();
}
