using System.Collections.Concurrent;
using System.Text.Json;
using Shared.Contracts.Rendering;
using Shared.Contracts.World;
using StormChase.Protocol;

internal sealed record ChaseVehicleSpec(string Id, string Name, decimal Price, float MassKg, float TopSpeedMps,
    float FuelCapacityLiters, float LitersPerKm, float WindToleranceMph, int ProbeCapacity);
internal sealed record ChaseShopOffer(string Id, string Name, decimal Price, string Description);
internal sealed record ChasePhotoReceipt(string Path, RenderFrame Frame);

/// <summary>Single-player field research, purchases and durable career progress.</summary>
internal sealed partial class StormChaseCareer
{
    internal static readonly ChaseVehicleSpec[] Vehicles =
    [
        new("pickup", "PRAIRIE PICKUP", 0, 1900, 42, 75, .17f, 95, 2),
        new("suv", "ALL-TERRAIN SCOUT", 8000, 2350, 49, 90, .20f, 125, 4),
        new("interceptor", "ARMORED INTERCEPTOR", 24000, 4800, 36, 125, .32f, 175, 6),
        // Appended so existing indices (and the online VehicleType 0-2) are unchanged: this is type 3.
        new("chase-truck", "CLASSIC CHASE PICKUP", 4500, 2100, 46, 95, .19f, 105, 3),
        // Department vehicles (types 4-6): issued free with a job (StormChaseJobs), never sold in the shop.
        new("police-cruiser", "SHERIFF CRUISER", 0, 1800, 58, 70, .15f, 90, 2),
        new("fire-engine", "FIRE ENGINE", 0, 14000, 31, 190, .55f, 140, 0),
        new("ambulance", "AMBULANCE", 0, 4200, 40, 110, .30f, 105, 0)
    ];
    internal static readonly ChaseShopOffer[] Offers =
    [
        new("probe", "REINFORCED PROBE ARRAY", 1600, "Unlocks turtle, hardened, pressure-sled and impact probes; carry up to 6"),
        new("camera", "TELEPHOTO CAMERA", 1400, "Detailed shots out to 8 km; 1.5x photo pay"),
        new("mesonet", "MOBILE MESONET", 2400, "Earn field-data income while driving in strong winds"),
        new("suv", "ALL-TERRAIN SCOUT", 8000, "110 mph road speed, 4 probes, stronger body"),
        new("interceptor", "ARMORED INTERCEPTOR", 24000, "6 probes; K deploys anchors for close interceptions"),
        new("repair", "REPAIR VEHICLE", 250, "Restore the selected vehicle at this service station"),
        // Index 6 matches the server's CareerStore.Offers[6] for online purchases.
        new("chase-truck", "CLASSIC CHASE PICKUP", 4500, "Built-in mobile mesonet rack, 3 probes, 103 mph; the chaser's classic"),
        new("pickup", "SELECT PRAIRIE PICKUP", 0, "Switch to your starter truck")
    ];
    private readonly string savePath;
    private readonly StormChaseWorld world;
    private readonly Dictionary<string, double> lastPhoto = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<ChasePhotoReceipt> photos = new();
    private readonly List<ChaseFieldProbe> probes = [];
    private double nextSample, nextSave;
    private int probeSequence;
    private float drivenKm, nextDistanceAward = 1;
    private double mesonetSeconds;
    private StormChaseContracts? contracts;
    private int contractsSeed;
    public bool DispatchOpen { get; set; }
    public int ContractDay { get; private set; } = 1;
    public StormChaseContracts Contracts
    {
        get
        {
            if (contracts is null || contractsSeed != Save.CampaignSeed)
            { contractsSeed = Save.CampaignSeed; contracts = new(world, contractsSeed) { BuildingDestroyed = id => DestroyedBuildings?.Invoke().Contains(id) == true, Storms = () => jobStorms }; }
            return contracts;
        }
    }
    public ChaseCareerSave Save { get; private set; }
    public IReadOnlyList<ChaseFieldProbe> Probes => probes;
    /// <summary>The player's job; on duty the department vehicle replaces the player's own selected vehicle.</summary>
    public StormChaseJob Job => StormChaseJobs.Get(Save.Contracts.Job);
    public bool OnDuty => Job.DutyVehicle is not null;
    public ChaseVehicleSpec SelectedVehicle => Vehicles.Single(v => v.Id == (Job.DutyVehicle ?? Save.SelectedVehicle));
    public ChaseVehicleCondition Condition => Save.VehicleConditions[SelectedVehicle.Id];
    private IReadOnlyList<ChaseJobStorm> jobStorms = [];

    /// <summary>Go on duty (or back to chasing) at a service station, where department vehicles are kept.</summary>
    public bool SelectJob(string jobId, Vector3f observer, double seconds)
    {
        if (!AtServiceStation(observer)) { Notify("Change jobs at a service station, where the department vehicles are parked.", seconds); return false; }
        bool changed = Save.Contracts.Job != jobId;
        bool selected = Contracts.SelectJob(jobId, Save.Contracts);
        // A department vehicle is handed over fuelled and serviced.
        if (selected && changed && OnDuty) { Condition.FuelLiters = SelectedVehicle.FuelCapacityLiters; Condition.Health = 100; }
        Notify(Contracts.Message, seconds); if (selected) Flush(); return selected;
    }
    public int ProbeCapacity => Math.Min(SelectedVehicle.ProbeCapacity, Save.ProbeLevel > 0 ? 6 : 2);
    public int ActiveProbeCount => probes.Count(p => !p.Destroyed);
    public string Message { get; private set; } = "Plan your chase. Deploy B probes ahead of the track; F takes a photograph.";
    public double MessageUntil { get; private set; } = 15;
    public bool ShopOpen { get; set; }
    public int ShopSelection { get; set; }
    public bool Refueling { get; set; }
    /// <summary>The classic chase pickup carries a factory-installed mesonet rack.</summary>
    public bool HasMesonet => Save.Mesonet || SelectedVehicle.Id == "chase-truck";
    public Func<IReadOnlySet<string>>? DestroyedBuildings { get; set; }
    public Func<Vector3f>? VehiclePosition { get; set; }
    public StormChaseProbeType SelectedProbeType => StormChaseProbeTypes.Get(Save.SelectedProbeType);
    public string ProbeReading => probes.Count == 0 ? $"NEXT: {SelectedProbeType.ShortName} [G]" :
        probes[^1].Destroyed ? $"{StormChaseProbeTypes.Get(probes[^1].TypeId).ShortName} {probes[^1].Sequence} LOST" : probes[^1].Reading;
    public IEnumerable<StormChaseProbeType> AvailableProbeTypes => StormChaseProbeTypes.All.Where(type => !type.RequiresUpgrade || Save.ProbeLevel > 0);

    /// <summary>G: choose which instrument the next deployment uses (advanced types need the reinforced array).</summary>
    public void CycleProbeType(double seconds)
    {
        var available = AvailableProbeTypes.ToArray();
        int index = Array.FindIndex(available, type => type.Id == SelectedProbeType.Id);
        var next = available[(index + 1) % available.Length];
        Save.SelectedProbeType = next.Id;
        Notify($"Next probe: {next.Name} ({next.RatingMph:0} mph rating). {next.Summary}", seconds);
    }

    public string SavePath => savePath;
    public StormChaseCareer(StormChaseWorld world, string? path = null)
    {
        this.world = world;
        savePath = path ?? Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
            "MMOI", "StormChase", "Career", "career-v1.json");
        Save = Load(savePath);
        foreach (var vehicle in Vehicles)
            if (!Save.VehicleConditions.ContainsKey(vehicle.Id))
                Save.VehicleConditions[vehicle.Id] = new() { FuelLiters = vehicle.FuelCapacityLiters };
        if (!Vehicles.Any(v => v.Id == Save.SelectedVehicle) || !Save.OwnedVehicles.Contains(Save.SelectedVehicle))
            Save.SelectedVehicle = "pickup";
    }

    public void Notify(string text, double seconds) { Message = text; MessageUntil = seconds + 8; }
    public void QueuePhoto(ChasePhotoReceipt receipt) => photos.Enqueue(receipt);
    public bool DeployProbe(Vector3f observer, double seconds)
    {
        if (ActiveProbeCount >= ProbeCapacity) { Notify("Probe rack empty. J retrieves nearby pods; upgrade your equipment and vehicle for more.", seconds); return false; }
        float x = observer.X, z = observer.Z + 2;
        if (!world.ContainsPlayablePosition(x, z))
            return false;
        var type = SelectedProbeType.RequiresUpgrade && Save.ProbeLevel == 0 ? StormChaseProbeTypes.Default : SelectedProbeType;
        probes.Add(new(++probeSequence, new(x, world.HeightAt(x, z) + .04f, z), Save.ProbeLevel) { TypeId = type.Id, Reading = type.ShortName + " READY" });
        Notify($"{type.Name} deployed. {type.Summary}", seconds);
        return true;
    }
    public void RetrieveProbe(Vector3f observer, double seconds)
    {
        var nearest = probes.OrderBy(p => HorizontalDistance(p.Position, observer)).FirstOrDefault();
        if (nearest is null || HorizontalDistance(nearest.Position, observer) > 15)
        { Notify("Move within 15 m of a deployed probe to retrieve it.", seconds); return; }
        probes.Remove(nearest); Notify("Probe retrieved; rack space available.", seconds);
    }

    public void Update(double seconds, double campaignSeconds, RenderWeatherState? weather)
    {
        DrainPhotoRewards(seconds);
        Save.CampaignSeconds = campaignSeconds;
        if (seconds >= nextSample)
        {
            double sampleDelta = nextSample == 0 ? .2 : Math.Clamp(seconds - nextSample + .2, 0, .5);
            nextSample = seconds + .2;
            foreach (var probe in probes.Where(p => !p.Destroyed))
            {
                var type = StormChaseProbeTypes.Get(probe.TypeId);
                var sample = probe.Spent ? new StormChaseProbeSample(0, StormChaseDestruction.SampleWindSpeedMph(probe.Position, weather), type.ShortName + " RELEASED")
                    : StormChaseProbeTypes.Sample(type, probe.Position, weather, (float)seconds, probe.Samples == 0);
                probe.WindMph = sample.WindMph; probe.Reading = sample.Reading;
                probe.PeakMph = Math.Max(probe.PeakMph, probe.WindMph);
                probe.Samples++;
                if (sample.OneShotComplete) probe.Spent = true;
                decimal payout = (decimal)(sampleDelta * sample.DollarsPerSecond + sample.LumpSum);
                if (payout > 0)
                {
                    Save.Cash += payout; Save.ProbeEarnings += payout;
                    probe.Earned += payout;
                }
                if (probe.WindMph > type.RatingMph) probe.OverloadSeconds += (float)sampleDelta;
                else probe.OverloadSeconds = Math.Max(0, probe.OverloadSeconds - (float)sampleDelta);
                if (probe.OverloadSeconds > 4)
                { probe.Destroyed = true; Notify($"Pod {probe.Sequence} lost in extreme winds. Earned ${probe.Earned:0} before losing signal.", seconds); }
            }
        }
        if (seconds >= nextSave) { Flush(); nextSave = seconds + 10; }
    }

    public void DrainPhotoRewards(double seconds)
    {
        while (photos.TryDequeue(out var photo)) EvaluatePhoto(photo, seconds);
    }

    public void RecordDriving(float distanceMeters, float speedMps, float localWind, float delta)
    {
        drivenKm += Math.Max(0, distanceMeters) / 1000;
        Condition.FuelLiters = Math.Max(0, Condition.FuelLiters - Math.Max(0, distanceMeters) / 1000 * SelectedVehicle.LitersPerKm
            - (speedMps > .1f ? .0005f * delta : 0));
        while (drivenKm >= nextDistanceAward) { Save.Cash += 25; Save.DrivingEarnings += 25; nextDistanceAward += 1; }
        if (HasMesonet && localWind >= 25 && speedMps >= .5)
        {
            mesonetSeconds += delta;
            while (mesonetSeconds >= 1) { decimal pay = (decimal)Math.Min(35, localWind * .16f); Save.Cash += pay; Save.MesonetEarnings += pay; mesonetSeconds -= 1; }
        }
    }

    public StormChaseGasStation? NearestStation(Vector3f point)
    {
        var destroyed = DestroyedBuildings?.Invoke();
        return world.GasStations.Where(s => destroyed?.Contains(s.BuildingId) != true)
            .OrderBy(s => HorizontalDistance(point, s.Position)).FirstOrDefault();
    }
    public bool AtServiceStation(Vector3f point) => NearestStation(point) is { } station && HorizontalDistance(point, station.Position) <= 70;
    public void UpdateFuel(float delta, Vector3f vehiclePosition, float speedMps, double seconds)
    {
        if (!Refueling) return;
        if (!AtServiceStation(vehiclePosition) || speedMps > .5f) { Refueling = false; Notify("Refueling stopped. Park beside a station pump.", seconds); return; }
        float liters = Math.Min(SelectedVehicle.FuelCapacityLiters - Condition.FuelLiters, delta * 5);
        if (OnDuty)
            // The department pays for its own vehicles' fuel.
            Condition.FuelLiters = Math.Min(SelectedVehicle.FuelCapacityLiters, Condition.FuelLiters + Math.Max(0, liters));
        else
        {
            liters = Math.Min(liters, (float)(Save.Cash / 1.35m));
            if (liters > 0)
            {
                decimal cost = Math.Min(Save.Cash, (decimal)liters * 1.35m);
                Condition.FuelLiters = Math.Min(SelectedVehicle.FuelCapacityLiters,
                    Condition.FuelLiters + (float)(cost / 1.35m));
                Save.Cash -= cost;
            }
        }
        if (Condition.FuelLiters >= SelectedVehicle.FuelCapacityLiters - .01f || (!OnDuty && Save.Cash <= .01m))
        { Refueling = false; Notify("Refueling complete.", seconds); Flush(); }
    }
    public bool Purchase(string id, Vector3f observer, double seconds)
    {
        if (!AtServiceStation(observer)) { Notify("Visit a city or town service station to buy equipment or change vehicles.", seconds); return false; }
        var offer = Offers.FirstOrDefault(o => o.Id == id);
        if (offer is null) return false;
        if (id == "repair" && VehiclePosition is not null &&
            HorizontalDistance(VehiclePosition(), NearestStation(observer)!.Position) > 70)
        { Notify("Bring the vehicle to the service station for repairs.", seconds); return false; }
        bool owned = id switch { "probe" => Save.ProbeLevel > 0, "camera" => Save.CameraLevel > 0, "mesonet" => Save.Mesonet, _ => Save.OwnedVehicles.Contains(id) };
        decimal cost = owned ? 0 : offer.Price;
        if (cost > Save.Cash) { Notify($"Need ${cost - Save.Cash:0} more. Probes and tornado photos fund upgrades.", seconds); return false; }
        Save.Cash -= cost;
        switch (id)
        {
            case "probe": Save.ProbeLevel = 1; break;
            case "camera": Save.CameraLevel = 1; break;
            case "mesonet": Save.Mesonet = true; break;
            case "repair": Condition.Health = 100; break;
            default: Save.OwnedVehicles.Add(id); Save.SelectedVehicle = id; break;
        }
        Notify(cost == 0 ? $"Selected {offer.Name}." : $"Purchased {offer.Name} for ${cost:0}.", seconds);
        Flush(); return true;
    }
    public void Tow(Vector3f observer, double seconds)
    {
        Save.Cash = Math.Max(0, Save.Cash - 200);
        Condition.Health = Math.Max(Condition.Health, 50); Condition.FuelLiters = Math.Max(Condition.FuelLiters, 8);
        Notify("Roadside recovery: $200. Vehicle returned to the nearest service station.", seconds);
    }

    public IReadOnlyList<RenderItem> CreateProbeItems() => probes.SelectMany(p => world.Models.CreateItems(StormChaseProbeTypes.Get(p.TypeId).ModelId, p.Position, 0, $"career/probe/{p.Sequence}"))
        .ToArray();

    private void EvaluatePhoto(ChasePhotoReceipt receipt, double seconds)
    {
        var frame = receipt.Frame;
        var camera = frame.Camera;
        var destroyedBuildings = DestroyedBuildings?.Invoke();
        decimal best = 0; string? bestId = null;
        foreach (var tornado in frame.Weather?.ActiveStorms.SelectMany(s => s.Tornadoes) ?? [])
        {
            float windMph = tornado.SmoothedWindSpeed ?? tornado.WindSpeed;
            if (tornado.CondensationAmount < .15f || windMph < 15) continue;
            var aim = tornado.Position with { Y = tornado.Position.Y + tornado.HeightMeters * .4f };
            float dx = aim.X - camera.Position.X, dy = aim.Y - camera.Position.Y, dz = aim.Z - camera.Position.Z;
            float range = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
            float maxRange = Save.CameraLevel > 0 ? 8000 : 4500;
            if (range < 80 || range > maxRange) continue;
            var forward = camera.Forward;
            float dot = (dx * forward.X + dy * forward.Y + dz * forward.Z) / range;
            if (dot < MathF.Cos(camera.VerticalFieldOfViewDegrees * MathF.PI / 360 * .85f)) continue;
            if (!world.IsLineOfSightClear(camera.Position, aim, destroyedBuildings)) continue;
            if (lastPhoto.TryGetValue(tornado.Id, out double taken) && seconds - taken < 12) continue;
            float framing = Math.Clamp((dot - .8f) / .2f, .2f, 1);
            float distanceQuality = Math.Clamp(1 - range / maxRange, .15f, 1);
            decimal reward = (decimal)((60 + windMph * 2.2f) * framing * distanceQuality * (Save.CameraLevel > 0 ? 1.5f : 1)
                * StormChaseContracts.ContentValueMultiplier(Save.Popularity));
            if (reward > best) { best = decimal.Round(reward); bestId = tornado.Id; }
        }
        Save.PhotosTaken++;
        if (bestId is null) { Notify("Photograph saved. Frame a visible tornado for a research reward; paid shots have a 12-second cooldown.", seconds); return; }
        lastPhoto[bestId] = seconds; Save.Cash += best; Save.PhotoEarnings += best; Save.PaidPhotos++;
        File.WriteAllText(Path.ChangeExtension(receipt.Path, ".reward.json"), JsonSerializer.Serialize(new { TornadoId = bestId, Reward = best, frame.FrameNumber }));
        Notify($"Tornado photograph accepted: +${best:0}.", seconds);
        Flush();
    }

    public void Flush()
    {
        string directory = Path.GetDirectoryName(Path.GetFullPath(savePath))!;
        Directory.CreateDirectory(directory);
        string temporary = savePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(Save, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, savePath, true);
    }
    public void BindContractPhysics(StormChaseDestruction destruction)
    { Contracts.Debris = destruction.GetReleasedPartStates; Contracts.CollectDebris = destruction.TryCollectSettledPart; }
    public bool AcceptContract(int id, double seconds)
    { bool accepted = Contracts.Accept(id, ContractDay, Save.Contracts); Notify(Contracts.Message, seconds); if (accepted) Flush(); return accepted; }
    public void CancelContract(double seconds)
    { Contracts.Cancel(Save.Contracts); Notify(Contracts.Message, seconds); Flush(); }
    public bool WorkContract(Vector3f feet, Vector3f vehicle, bool driving, float speed, RenderWeatherState? weather, double seconds)
    {
        bool worked = Contracts.Interact(Save.Contracts, feet, vehicle, driving, speed, StormChaseDestruction.SampleWindSpeedMph(feet, weather));
        Notify(Contracts.Message, seconds); if (worked) Flush(); return worked;
    }
    public void UpdateContracts(float delta, int day, Vector3f feet, Vector3f vehicle, bool driving, float speed,
        RenderWeatherState? weather, double seconds)
    {
        ContractDay = day;
        jobStorms = weather?.ActiveStorms.Where(storm => !storm.VisualOnly).Select(storm => new ChaseJobStorm(storm.Position, storm.Velocity,
            storm.Tornadoes.Any(tornado => tornado.TouchdownAmount >= .5f))).ToArray() ?? [];
        Save.Contracts.Completed.RemoveWhere(id => id / 100 < day - 2);
        string before = Contracts.Message;
        Contracts.Update(Save.Contracts, Math.Clamp(delta, 0, 1), feet, vehicle, driving, speed,
            StormChaseDestruction.SampleWindSpeedMph(feet, weather), CommitContract);
        if (before != Contracts.Message) Notify(Contracts.Message, seconds);
    }
    public MissionState? DispatchMission
    {
        get
        {
            var offer = Contracts.ActiveOffer(Save.Contracts); var target = Contracts.Target(Save.Contracts); var run = Save.Contracts.Active;
            return offer is null || target is null || run is null ? null : new(offer.Id, offer.Title, offer.Kind.ToString(), run.Step,
                offer.Stops.Count, target.Instruction, new(target.Position.X, target.Position.Y, target.Position.Z), false, run.WorkSeconds, target.WorkSeconds);
        }
    }
    public ActivityState[] DispatchActivities => Contracts.Offers(ContractDay, Save.Contracts.Job).Select(offer => new ActivityState(offer.Id, offer.Title,
        offer.Kind.ToString(), new(offer.Stops[0].Position.X, offer.Stops[0].Position.Y, offer.Stops[0].Position.Z), offer.Stops.Count,
        offer.CashCents, offer.Reputation, offer.Available && !Save.Contracts.Completed.Contains(offer.Id), offer.Description)).ToArray();
    private bool CommitContract(ChaseContractReward reward)
    {
        if (Save.ContractReceipts.Contains(reward.ReceiptId)) return true;
        var liveProgress = Save.Contracts;
        var completed = JsonSerializer.Deserialize<ChaseContractProgress>(JsonSerializer.Serialize(liveProgress))!;
        completed.Completed.Add(reward.ContractId); completed.TotalCompleted++; completed.Active = null;
        decimal cash = Save.Cash; int reputation = Save.Reputation, popularity = Save.Popularity;
        Save.Cash += reward.CashCents / 100m; Save.Reputation = checked(Save.Reputation + reward.Reputation);
        Save.Popularity = checked(Save.Popularity + reward.Popularity); Save.ContractReceipts.Add(reward.ReceiptId); Save.Contracts = completed;
        try { Flush(); return true; }
        catch (IOException)
        { Save.Cash = cash; Save.Reputation = reputation; Save.Popularity = popularity; Save.ContractReceipts.Remove(reward.ReceiptId); return false; }
        finally { Save.Contracts = liveProgress; }
    }
    private static ChaseCareerSave Load(string path)
    {
        if (!File.Exists(path)) return new();
        try
        {
            var save = JsonSerializer.Deserialize<ChaseCareerSave>(File.ReadAllText(path));
            if (save is null || save.Schema != 1 || save.Cash < 0 || !double.IsFinite(save.CampaignSeconds)
                || !float.IsFinite(save.CampaignStartHour) || save.CampaignStartHour is < 0 or >= 24) return new();
            save.OwnedVehicles.Add("pickup"); return save;
        }
        catch (Exception error) when (error is JsonException or IOException) { return new(); }
    }
    internal static float HorizontalDistance(Vector3f a, Vector3f b) => MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z));
}

internal sealed partial class ChaseCareerSave
{
    public int Schema { get; set; } = 1;
    public decimal Cash { get; set; } = 900;
    public int ProbeLevel { get; set; }
    public string SelectedProbeType { get; set; } = "probe-tripod";
    public int CameraLevel { get; set; }
    public bool Mesonet { get; set; }
    public HashSet<string> OwnedVehicles { get; set; } = ["pickup"];
    public string SelectedVehicle { get; set; } = "pickup";
    public Dictionary<string, ChaseVehicleCondition> VehicleConditions { get; set; } = [];
    public double CampaignSeconds { get; set; }
    public int CampaignSeed { get; set; } = 73019;
    public float CampaignStartHour { get; set; } = 8;
    public decimal ProbeEarnings { get; set; }
    public decimal PhotoEarnings { get; set; }
    public decimal DrivingEarnings { get; set; }
    public decimal MesonetEarnings { get; set; }
    public int PhotosTaken { get; set; }
    public int PaidPhotos { get; set; }
    public int Reputation { get; set; }
    public int Popularity { get; set; }
    public ChaseContractProgress Contracts { get; set; } = new();
    public HashSet<string> ContractReceipts { get; set; } = [];
}
internal sealed class ChaseVehicleCondition
{
    public float FuelLiters { get; set; }
    public float Health { get; set; } = 100;
}
internal sealed class ChaseFieldProbe(int sequence, Vector3f position, int level)
{
    public int Sequence { get; } = sequence;
    public Vector3f Position { get; } = position;
    public int Level { get; } = level;
    public bool Destroyed { get; set; }
    public float WindMph { get; set; }
    public float PeakMph { get; set; }
    public int Samples { get; set; }
    public float OverloadSeconds { get; set; }
    public decimal Earned { get; set; }
    public string TypeId { get; init; } = "probe-tripod";
    /// <summary>Short HUD reading from the last sample ("TURTLE 84 MPH").</summary>
    public string Reading { get; set; } = "";
    /// <summary>One-shot instruments (the radiosonde) stop sampling after their launch.</summary>
    public bool Spent { get; set; }
}
