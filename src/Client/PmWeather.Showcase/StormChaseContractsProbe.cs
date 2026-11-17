using System.Text.Json;
using Engine.Rendering.Assets;
using Shared.Contracts.World;

internal static class StormChaseContractsProbe
{
    public static int Run(string output)
    {
        Directory.CreateDirectory(output); var checks = new List<object>(); bool passed = true;
        void Check(bool condition, string name) { checks.Add(new { Name = name, Passed = condition }); passed &= condition; }
        var world = new StormChaseWorld(new MutableRenderAssetCatalog(new BuiltInRenderAssetCatalog()));
        var jobs = new StormChaseContracts(world, 73019); var progress = new ChaseContractProgress();
        var offers = jobs.Offers(1);
        Check(offers.Count == 8 && offers.Count(offer => offer.Available) == 7, "Seven quiet-weather assignments exist before any storm damage");
        Check(offers.Select(offer => offer.Id).Distinct().Count() == 8, "Daily dispatch IDs are distinct");
        Check(offers.All(offer => offer.Stops.All(stop => world.ContainsPlayablePosition(stop.Position.X, stop.Position.Z))), "Every work stop lies in the actual expanded world");
        var again = new StormChaseContracts(world, 73019).Offers(1);
        Check(JsonSerializer.Serialize(offers) == JsonSerializer.Serialize(again), "Server and client generate the same jobs for a day and seed");
        Check(jobs.Offers(2).All(offer => !offers.Any(previous => previous.Id == offer.Id)), "A new day offers a new assignment board");
        var survey = offers.First(offer => offer.Kind == ChaseContractKind.WeatherSurvey); var stop = survey.Stops[0];
        Check(jobs.Accept(survey.Id, 1, progress), "A current available survey can be accepted");
        Check(!jobs.Accept(survey.Id, 1, progress), "Only one assignment can be active");
        Check(!jobs.Interact(progress, stop.Position + new Vector3f(200, 0, 0), stop.Position, false, 0, 0), "An on-foot survey uses actual player position, never the nearby vehicle");
        Check(!jobs.Interact(progress, stop.Position, stop.Position, true, 0, 0), "Ground observations require leaving the vehicle");
        Check(!jobs.Interact(progress, stop.Position, stop.Position, false, 0, 70), "Severe wind prevents community work");
        Check(jobs.Interact(progress, stop.Position, stop.Position, false, 0, 0), "A safe grounded survey starts work");
        int payments = 0; ChaseContractReward? reward = null;
        bool Pay(ChaseContractReward value) { payments++; reward = value; return true; }
        jobs.Update(progress, 1, stop.Position, stop.Position, false, 0, 0, Pay);
        jobs.Update(progress, 1, stop.Position + new Vector3f(200, 0, 0), stop.Position, false, 0, 0, Pay);
        Check(progress.Active is { Working: false, WorkSeconds: 1 } && payments == 0, "Leaving the work stop pauses authoritative elapsed work without earning a reward");
        jobs.Interact(progress, stop.Position, stop.Position, false, 0, 0);
        for (int i = 0; i < 40; i++) jobs.Update(progress, 1, stop.Position, stop.Position, false, 0, 0, Pay);
        Check(payments == 1 && reward?.CashCents == survey.CashCents && progress.Active is null && progress.Completed.Contains(survey.Id), "Finishing sustained work grants one reward and completes the assignment");
        Check(!jobs.Accept(survey.Id, 1, progress), "Completed assignment cannot be farmed again that day");
        Check(StormChaseContracts.ContentValueMultiplier(0) == 1 && StormChaseContracts.ContentValueMultiplier(50) == 1.5f
            && StormChaseContracts.ContentValueMultiplier(10000) == 3, "Popularity increases content value with a bounded multiplier");
        jobs.BuildingDestroyed = _ => true;
        Check(jobs.Offers(1).Any(offer => !offer.Available && offer.Kind != ChaseContractKind.DebrisClearance), "Destroyed community or service sites stop accepting work");
        jobs.BuildingDestroyed = null;
        var bodies = Enumerable.Range(0, 3).Select(index => new StormChaseDebrisState("piece" + index, stop.Position,
            System.Numerics.Quaternion.Identity, Vector3f.Zero, Vector3f.Zero, true, true, false)).ToArray();
        jobs.Debris = () => bodies; jobs.CollectDebris = (id, _) => { int index = Array.FindIndex(bodies, body => body.PartId == id); if (index < 0 || bodies[index].Collected) return false;
            bodies[index] = bodies[index] with { Collected = true }; return true; };
        var cleanup = jobs.Offers(1).First(offer => offer.Kind == ChaseContractKind.DebrisClearance);
        Check(cleanup.Available && jobs.Accept(cleanup.Id, 1, progress), "A cleanup becomes available only for actual settled fragments");
        jobs.BuildingDestroyed = _ => true;
        for (int i = 0; i < 3; i++) jobs.Interact(progress, stop.Position, stop.Position, false, 0, 0);
        jobs.Update(progress, 1, stop.Position, stop.Position, false, 0, 0, Pay);
        Check(bodies.All(body => body.Collected) && progress.Completed.Contains(cleanup.Id) && payments == 2, "Cleanup works at damaged sites and collects three permanent tombstones");

        string savePath = Path.Combine(output, "offline-career-fixture.json");
        File.WriteAllText(savePath, "{}"); var career = new StormChaseCareer(world, savePath);
        var outreach = career.Contracts.Offers(1).First(offer => offer.Kind == ChaseContractKind.CommunityOutreach); var hall = outreach.Stops[0].Position;
        decimal initialCash = career.Save.Cash;
        career.AcceptContract(outreach.Id, 0); career.WorkContract(hall, hall, false, 0, null, 0);
        for (int i = 0; i < 50; i++) career.UpdateContracts(1, 1, hall, hall, false, 0, null, i);
        var restored = new StormChaseCareer(world, savePath);
        Check(restored.Save.Popularity == outreach.Popularity && restored.Save.Cash == initialCash + outreach.CashCents / 100m,
            "Offline outreach money and popularity survive reloading the actual save");
        Check(restored.Save.Contracts.Active is null && restored.Save.Contracts.Completed.Contains(outreach.Id) && restored.Save.ContractReceipts.Count == 1,
            "Durable payout includes the completed assignment and its receipt in the same saved document");
        // ------------------------------------------------------------------ player jobs: police, fire, EMS
        var duty = new StormChaseContracts(world, 73019);
        var boards = StormChaseJobs.All.ToDictionary(job => job.Id, job => duty.Offers(1, job.Id));
        Check(boards[StormChaseJobs.Chaser].Count == 8 && boards[StormChaseJobs.Police].Count == 5 && boards[StormChaseJobs.Fire].Count == 4
            && boards[StormChaseJobs.Ems].Count == 3, "Each job has its own dispatch board; the storm chaser board is unchanged");
        var allIds = boards.Values.SelectMany(board => board.Select(offer => offer.Id)).ToArray();
        Check(allIds.Distinct().Count() == allIds.Length && boards.Values.SelectMany(b => b).All(o => o.Stops.All(s => world.ContainsPlayablePosition(s.Position.X, s.Position.Z))),
            "Job call IDs are distinct across jobs and every stop is in the world");
        Check(boards.Where(pair => pair.Key != StormChaseJobs.Chaser).SelectMany(pair => pair.Value).All(o => o.Available == !(o.Dynamic || o.Kind == ChaseContractKind.DebrisClearance)),
            "Quiet-weather job calls are open; storm calls stay closed with no storm or damage");
        var officer = new ChaseContractProgress();
        Check(duty.SelectJob(StormChaseJobs.Police, officer) && officer.Job == StormChaseJobs.Police && !duty.SelectJob("astronaut", officer),
            "A player can go on duty in a listed job only");
        // A tornado 8 km west of a town, moving east: the police board raises a warning for that town.
        var town = world.Settlements.First(s => world.BuildingInteriors.Any(i => i.Kind == "cityhall" && Distance(i.ExteriorFeet, s.Position) < 3000));
        duty.Storms = () => [new ChaseJobStorm(town.Position + new Vector3f(-8000, 0, 0), new(15, 0, 0), true)];
        var warning = duty.Offers(1, StormChaseJobs.Police).Single(o => o.Kind == ChaseContractKind.PoliceWarning);
        Check(warning.Available && warning.Title.Contains(town.Name) && Distance(warning.Stops[0].Position, town.Position) < 3000,
            "A tornado heading for a town opens a police warning call at that town's civic hall");
        Check(duty.Accept(warning.Id, 1, officer) && officer.Active?.Stops is { Count: 1 }, "Accepting a storm call freezes its stop");
        duty.Storms = () => [];
        var siren = duty.Target(officer)!;
        Check(Distance(siren.Position, warning.Stops[0].Position) < .01f && !duty.Offers(1, StormChaseJobs.Police).Single(o => o.Kind == ChaseContractKind.PoliceWarning).Available,
            "An accepted warning keeps its town even after the storm is gone, while the board closes the call");
        int dutyPayments = 0; bool DutyPay(ChaseContractReward value) { dutyPayments++; return true; }
        Check(duty.Interact(officer, siren.Position, siren.Position, true, 0, 0), "The officer sounds the warning from the parked cruiser");
        for (int i = 0; i < 12; i++) duty.Update(officer, 1, siren.Position, siren.Position, true, 0, 0, DutyPay);
        Check(dutyPayments == 1 && officer.Active is null && officer.Completed.Contains(warning.Id), "Sounding the warning completes and pays the call once");
        // A destroyed building opens a call for each emergency service.
        var collapsed = world.Buildings.Where(b => b.DestructiblePartIds.Count > 0).OrderBy(b => b.Id, StringComparer.Ordinal).First();
        duty.BuildingDestroyed = id => id == collapsed.Id;
        var scene = duty.Offers(1, StormChaseJobs.Police).Single(o => o.Kind == ChaseContractKind.PoliceScene);
        var rescue = duty.Offers(1, StormChaseJobs.Fire).Single(o => o.Kind == ChaseContractKind.FireRescue);
        var injured = duty.Offers(1, StormChaseJobs.Ems).Single(o => o.Kind == ChaseContractKind.EmsInjured);
        Check(new[] { scene, rescue, injured }.All(o => o.Available && o.AtDamage && Distance(o.Stops[0].Position, collapsed.Position) < 1),
            "A collapsed building opens police scene, fire search-and-rescue and EMS calls at the actual wreck");
        var medic = new ChaseContractProgress(); duty.SelectJob(StormChaseJobs.Ems, medic);
        Check(injured.Stops.Count == 2 && duty.Accept(injured.Id, 1, medic), "The EMS call has a treatment stop and a clinic stop");
        var wreck = duty.Target(medic)!.Position;
        Check(!duty.Interact(medic, wreck, wreck, true, 0, 0) && duty.Interact(medic, wreck, wreck, false, 0, 0),
            "Treatment happens on foot at the damaged building itself");
        for (int i = 0; i < 16; i++) duty.Update(medic, 1, wreck, wreck, false, 0, 0, DutyPay);
        var clinic = duty.Target(medic)!.Position;
        Check(medic.Active?.Step == 1 && Distance(clinic, wreck) > 1, "After treatment the patient must be driven to the clinic");
        duty.Interact(medic, clinic, clinic, true, 0, 0);
        for (int i = 0; i < 6; i++) duty.Update(medic, 1, clinic, clinic, true, 0, 0, DutyPay);
        Check(dutyPayments == 2 && medic.Completed.Contains(injured.Id), "Delivering the patient completes the EMS call");
        // Career: going on duty swaps in the department vehicle and is saved.
        string dutyPath = Path.Combine(output, "duty-career-fixture.json"); File.WriteAllText(dutyPath, "{}");
        var officerCareer = new StormChaseCareer(world, dutyPath);
        var station = world.GasStations[0].ParkingPosition;
        Check(!officerCareer.SelectJob(StormChaseJobs.Fire, station + new Vector3f(5000, 0, 0), 0) && officerCareer.SelectJob(StormChaseJobs.Fire, station, 0)
            && officerCareer.OnDuty && officerCareer.SelectedVehicle.Id == "fire-engine" && officerCareer.Condition.FuelLiters == officerCareer.SelectedVehicle.FuelCapacityLiters,
            "Going on duty at a service station issues a fuelled fire engine (not away from a station)");
        var reloaded = new StormChaseCareer(world, dutyPath);
        Check(reloaded.Job.Id == StormChaseJobs.Fire && reloaded.SelectedVehicle.Id == "fire-engine" && reloaded.Save.SelectedVehicle == "pickup"
            && reloaded.SelectJob(StormChaseJobs.Chaser, station, 0) && reloaded.SelectedVehicle.Id == "pickup",
            "The job survives reloading; going off duty returns the player's own vehicle");
        string report = Path.Combine(output, "contracts-check.json");
        File.WriteAllText(report, JsonSerializer.Serialize(new { Passed = passed, Scope = "Shared dispatch rules and actual offline save boundaries; native online proof is separate", Checks = checks }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Dispatch checks {(passed ? "passed" : "FAILED")}: {checks.Count}. {report}");
        return passed ? 0 : 1;
    }
    private static float Distance(Vector3f a, Vector3f b) => MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z));
}
