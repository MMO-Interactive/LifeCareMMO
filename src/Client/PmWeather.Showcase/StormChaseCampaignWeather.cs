using System.Globalization;
using System.Collections.Immutable;
using System.Text.Json;
using Engine.Environment;
using Shared.Contracts.Rendering;
using Shared.Contracts.World;
using StormChase.Protocol;

/// <summary>A deterministic local campaign clock and regional storm director.</summary>
internal sealed class StormChaseCampaignWeather
{
    public const double DefaultDayLengthSeconds = 1200;
    private const int ClockTicksPerSecond = 20;
    private const double TickSeconds = 1d / ClockTicksPerSecond;
    private readonly LiveStormController controls;
    private readonly ProceduralEnvironmentService environment = new();
    private readonly List<StormChaseCampaignCell> cells = [];
    private readonly List<StormChaseCampaignSpawn> spawnHistory = [];
    private readonly float startHour;
    private List<Opportunity> opportunities = [];
    private int nextOpportunity, nextCellNumber;
    private long tickCount;
    private double elapsedSeconds, elapsedCompensation;
    private AtmosphereSnapshot atmosphere = null!;
    private ImmutableArray<AtmosphereDayForecast> outlook = [];
    private NetVector3 outlookPoint = new(6000, 0, 7000);

    public StormChaseCampaignWeather(LiveStormController controls, string[] args, double elapsedSeconds = 0)
    {
        this.controls = controls;
        Seed = int.Parse(Read(args, "--campaign-seed", "73019"), CultureInfo.InvariantCulture);
        startHour = float.Parse(Read(args, "--campaign-start-hour", "8"), CultureInfo.InvariantCulture);
        if (!float.IsFinite(startHour) || startHour is < 0 or >= 24)
            throw new ArgumentException("--campaign-start-hour must be 0..24 exclusive.");
        controls.EnterCampaign();
        Day = CreateForecast(Seed, 1);
        Current = BuildEnvironment();
        SetElapsedSeconds(elapsedSeconds);
    }

    public int Seed { get; }
    public double DayLengthSeconds => DefaultDayLengthSeconds;
    public double ElapsedSeconds => elapsedSeconds;
    public int DayNumber => Day.DayNumber;
    public float Hour => HourAt(elapsedSeconds);
    public float StartHour => startHour;
    public bool IsNight => Hour is < 6 or >= 19.5f;
    public StormChaseDayForecast Day { get; private set; }
    public AtmosphereSnapshot Atmosphere => atmosphere;
    public string ForecastName => outlook.IsDefaultOrEmpty ? Day.Name : outlook[0].Name;
    public float Risk => outlook.IsDefaultOrEmpty ? Day.Risk : outlook[0].Risk;
    public float AvailableInstability => atmosphere.Diagnostics.CapeJkg;
    public string ForecastSummary => $"CAPE {atmosphere.Diagnostics.CapeJkg:0} J/KG  CIN {atmosphere.Diagnostics.CinJkg:0}  SRH {atmosphere.Diagnostics.Srh01KmM2S2:0}  STP {atmosphere.Diagnostics.Stp:0.0}";
    public int ActiveStormCount => cells.Count;
    public bool StormActive => cells.Count > 0;
    public IReadOnlyList<StormChaseCampaignCell> ActiveCells => cells;
    public IReadOnlyList<StormChaseCampaignSpawn> SpawnHistory => spawnHistory;
    public int TotalStormsSpawned { get; private set; }
    public int StormsSpawnedToday { get; private set; }
    public PmWeatherSupercellSimulation? PrimarySimulation => cells.FirstOrDefault()?.Simulation;
    public EnvironmentSceneState Current { get; private set; }
    public EnvironmentSceneState RegionalEnvironment { get; private set; } = null!;
    public string Warning
    {
        get
        {
            var tornadoCell = cells.FirstOrDefault(cell => cell.Simulation.Current.ActiveStorms
                .Any(storm => storm.Tornadoes.Any(tornado => tornado.TouchdownAmount
                    * (1 - BoundaryTornadoInfluence(cell.BoundaryKind, cell.BoundaryDistance).Suppression) >= .95f)));
            if (tornadoCell is not null) return "TORNADO WARNING · " + tornadoCell.Region;
            var mature = cells.FirstOrDefault(cell => cell.Simulation.Current.ActiveStorms
                .Any(storm => storm.SmoothedStage >= 2.15f && storm.Intensity >= .85f));
            if (mature is not null) return "SEVERE THUNDERSTORM WARNING · " + mature.Region;
            if (StormActive) return "Storm developing · " + cells[0].Region;
            return Risk >= .55f&&AvailableInstability>=600
                ? IsNight&&Day.NocturnalSupport>=.3f?"Nocturnal severe weather watch":"Severe weather watch"
                : "No active weather warnings";
        }
    }

    public float StormInfluence(Vector3f observer)
    {
        float influence = 0;
        foreach (var storm in Current.ActiveStorms)
        {
            float x = observer.X - storm.Position.X, z = observer.Z - storm.Position.Z;
            float normalized = MathF.Sqrt(x * x + z * z) / Math.Max(1, storm.AnvilRadiusMeters);
            influence = Math.Max(influence, Math.Clamp((1.25f - normalized) / .65f, 0, 1) * Math.Clamp(storm.Intensity, 0, 1));
        }
        return influence;
    }

    /// <summary>Replay the prior night as well as the current day; world destruction is not replayed.</summary>
    public void SetElapsedSeconds(double value)
    {
        if (!double.IsFinite(value) || value < 0 || value > 10_000_000)
            throw new ArgumentOutOfRangeException(nameof(value));
        int targetDay = DayAt(value);
        int replayDay=Math.Max(1,targetDay-1);
        double dayStartSeconds = Math.Max(0, (replayDay - 1) * DefaultDayLengthSeconds - startHour / 24 * DefaultDayLengthSeconds);
        cells.Clear();
        spawnHistory.Clear();
        TotalStormsSpawned = 0;
        nextCellNumber = 0;
        elapsedSeconds = dayStartSeconds;
        elapsedCompensation = 0;
        tickCount = (long)Math.Floor(dayStartSeconds * ClockTicksPerSecond + 1e-8);
        BeginDay(replayDay);
        RefreshAtmosphere(dayStartSeconds);
        controls.SetCampaignIdle();
        AdvanceClock(value - dayStartSeconds);
        Current = BuildEnvironment();
    }

    public void Update(double elapsedWallSeconds)
    {
        if (!double.IsFinite(elapsedWallSeconds) || elapsedWallSeconds < 0)
            throw new ArgumentOutOfRangeException(nameof(elapsedWallSeconds));
        if (controls.Paused || elapsedWallSeconds == 0) return;
        controls.AdvanceCampaignVisualClock(elapsedWallSeconds);
        AdvanceClock(elapsedWallSeconds);
        Current = BuildEnvironment();
    }

    private void AdvanceClock(double seconds)
    {
        double corrected = seconds - elapsedCompensation;
        double next = elapsedSeconds + corrected;
        if (!double.IsFinite(next) || next > 10_000_000) throw new ArgumentOutOfRangeException(nameof(seconds));
        elapsedCompensation = (next - elapsedSeconds) - corrected;
        elapsedSeconds = next;
        long targetTick = (long)Math.Floor(elapsedSeconds * ClockTicksPerSecond + 1e-8);
        while (tickCount < targetTick)
        {
            tickCount++;
            AdvanceTick(tickCount * TickSeconds);
        }
    }

    private void AdvanceTick(double wallSeconds)
    {
        int dayNumber = DayAt(wallSeconds);
        bool dayChanged=dayNumber != DayNumber;
        if (dayChanged) BeginDay(dayNumber);
        float hour = HourAt(wallSeconds);
        if(dayChanged||tickCount%ClockTicksPerSecond==0)RefreshAtmosphere(wallSeconds);
        foreach(var cell in cells)
        {
            // A cell retains its birth ingredients across midnight. Surface warmth
            // ebbs continuously; moisture and shear may sustain an occasional
            // nocturnal updraft. Future cycling stops only after support is lost.
            float support=CellSupport(cell);
            if(support<cell.MinimumSupport&&wallSeconds-cell.BornSeconds>=35)
            {
                double fadeHours=2.4+.6*cell.BirthForecast.NocturnalSupport;
                double sourceFadeSeconds=fadeHours*(DefaultDayLengthSeconds/24)*cell.PlaybackSpeed;
                cell.Simulation.RequestDissipation(Math.Clamp(sourceFadeSeconds,5,3600));
            }
        }

        // Independent opportunities create independent cells. A live storm is
        // never replaced simply because another storm forms elsewhere.
        while (nextOpportunity < opportunities.Count && hour >= opportunities[nextOpportunity].Hour)
        {
            var opportunity = opportunities[nextOpportunity++];
            if (cells.Count < 4 && hour <= opportunity.Hour + 1.5f
                )
                Spawn(opportunity,wallSeconds);
        }
        for (int index = cells.Count - 1; index >= 0; index--)
        {
            var cell = cells[index];
            if(cell.Simulation.Current.ActiveStorms.FirstOrDefault() is{} sourceStorm)
            {
                var target=new Vector3f(cell.Atmosphere.StormMotion.X,0,cell.Atmosphere.StormMotion.Z);
                float speed=MathF.Sqrt(target.X*target.X+target.Z*target.Z);
                if(speed>.01f)target*=Math.Clamp(speed,6,28)/speed;
                cell.MotionVelocity+=(target-cell.MotionVelocity)*.008f;
                cell.MotionOffset+=(cell.MotionVelocity-sourceStorm.Velocity*cell.PlaybackSpeed)*(float)TickSeconds;
            }
            cell.Simulation.Advance(TickSeconds * cell.PlaybackSpeed);
            if (cell.Simulation.IsComplete) cells.RemoveAt(index);
        }
        if (cells.Count == 0) controls.SetCampaignIdle();
        else if (!cells.Any(cell => ReferenceEquals(cell.Simulation, controls.Simulation)))
        {
            var primary = cells[0];
            controls.SetCampaignPrimary(primary.Simulation, primary.PresetIndex, primary.Seed, primary.PlaybackSpeed);
        }
    }

    private void BeginDay(int dayNumber)
    {
        Day = CreateForecast(Seed, dayNumber);
        StormsSpawnedToday = 0;
        nextCellNumber = 0;
        nextOpportunity = 0;
        opportunities = CreateOpportunities(Day);
        // The outlook samples the day's target area on the moist side of its boundary, not a fixed town.
        outlookPoint=StormChaseAtmosphere.OutlookPoint(Seed,dayNumber);
        outlook=StormChaseAtmosphere.CreateThreeDayForecast(Seed,dayNumber);
    }

    private void RefreshAtmosphere(double seconds)
    {
        atmosphere=StormChaseAtmosphere.Snapshot(Seed,DayNumber,HourAt(seconds),seconds,tickCount,outlookPoint,includeForecast:false)
            with{ThreeDayForecast=outlook,CampaignStartHour=startHour,CampaignDayLengthSeconds=DefaultDayLengthSeconds};
        var boundary=StormChaseAtmosphere.ActiveBoundary(Seed,DayNumber,HourAt(seconds));
        foreach(var cell in cells)
        {
            var position=(cell.Simulation.Current.ActiveStorms.FirstOrDefault()?.Position??Vector3f.Zero)+cell.MotionOffset;
            cell.Atmosphere=StormChaseAtmosphere.Sample(Seed,DayNumber,HourAt(seconds),new(position.X,0,position.Z)).Diagnostics;
            cell.BoundaryDistance=boundary.SignedDistance(position.X,position.Z);cell.BoundaryKind=boundary.NearestKind(position.X,position.Z);
        }
    }

    private static Vector3f CellPosition(StormChaseCampaignCell cell) =>
        (cell.Simulation.Current.ActiveStorms.FirstOrDefault()?.Position ?? Vector3f.Zero) + cell.MotionOffset;
    private static float HorizontalDistanceSquared(Vector3f a, Vector3f b) => (a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z);

    private static float CellSupport(StormChaseCampaignCell cell)=>Math.Clamp(
        .7f*cell.Atmosphere.CapeJkg/Math.Max(350,cell.BirthAtmosphere.CapeJkg)
        +.3f*cell.Atmosphere.InitiationPotential01/Math.Max(.1f,cell.BirthAtmosphere.InitiationPotential01),0,1.3f);

    private void Spawn(Opportunity opportunity,double wallSeconds)
    {
        var random = new Random(Mix(unchecked(Day.Seed ^ opportunity.Seed ^ 0x3156af13)));
        var regions = new[]
        {
            ("Meridian City", new Vector3f(6000, 0, 7000)), ("Cedar Junction", new Vector3f(-6500, 0, 2500)),
            ("Redbank", new Vector3f(3500, 0, -5500)), ("Lakeside farms", new Vector3f(95, 0, 1040)),
            ("Dunfield", new Vector3f(-15500, 0, 13500)), ("Northcrest", new Vector3f(15100, 0, 15500)),
            ("Willowbend", new Vector3f(-15000, 0, -14200)), ("Fairview", new Vector3f(16200, 0, -13800)),
            ("Gull Harbor", new Vector3f(StormChaseWorld.GullHarborX, 0, StormChaseWorld.GullHarborZ)),
        };
        float initiationHour = HourAt(wallSeconds);
        (string, Vector3f) region;
        if (DayNumber == 1 && StormsSpawnedToday == 0) region = regions[0];   // a new career's first storm stays near Meridian City
        else
        {
            // Initiation happens where boundary convergence erodes the cap: sample points just
            // inside the moist side of today's boundary, keep cells discrete (>= 14 km apart)
            // and choose the best combination of lift and instability.
            var boundary = StormChaseAtmosphere.ActiveBoundary(Seed, DayNumber, initiationHour);
            float bestScore = 0; region = ("", Vector3f.Zero); bool found = false;
            for (int attempt = 0; attempt < 12; attempt++)
            {
                float along = RandomFloat(random, -21000, 21000), into = RandomFloat(random, 1500, 8000);
                var (px, pz) = boundary.Point(along, into);
                if (MathF.Abs(px) > 21000 || MathF.Abs(pz - StormChaseWorld.PlayableCenterZ) > 21000) continue;
                var candidate = new Vector3f(px, 0, pz);
                // Only same-day cells: resume replays from the previous day's start, so spacing must not
                // depend on storms born before it (they are not reconstructed).
                if (cells.Any(existing => existing.BirthForecast.DayNumber == DayNumber
                    && HorizontalDistanceSquared(CellPosition(existing), candidate) < 14000f * 14000f)) continue;
                var sample = StormChaseAtmosphere.Sample(Seed, DayNumber, initiationHour, new(px, 0, pz)).Diagnostics;
                float score = sample.InitiationPotential01 * Math.Clamp(sample.CapeJkg / 2500, .05f, 1) * (.6f + .4f * (float)random.NextDouble());
                if (score <= bestScore) continue;
                var town = regions.MinBy(r => HorizontalDistanceSquared(r.Item2, candidate));
                bestScore = score; found = true;
                region = ($"{town.Item1} {boundary.NearestKind(px, pz).ToLowerInvariant()}", candidate);
            }
            // The triple point focuses lift: always consider the warm sector just off it as well.
            if (boundary.TryTriplePoint(out float tx, out float tz))
            {
                var (dx, dz) = boundary.WarmSectorDirection();
                for (int step = 0; step < 3; step++)
                {
                    var candidate = new Vector3f(tx + dx * (2000 + 1750 * step), 0, tz + dz * (2000 + 1750 * step));
                    if (MathF.Abs(candidate.X) > 21000 || MathF.Abs(candidate.Z - StormChaseWorld.PlayableCenterZ) > 21000) continue;
                    if (cells.Any(existing => existing.BirthForecast.DayNumber == DayNumber
                        && HorizontalDistanceSquared(CellPosition(existing), candidate) < 14000f * 14000f)) continue;
                    var sample = StormChaseAtmosphere.Sample(Seed, DayNumber, initiationHour, new(candidate.X, 0, candidate.Z)).Diagnostics;
                    // No random handicap: the triple point's lift is the focus, not one more random draw.
                    float score = sample.InitiationPotential01 * Math.Clamp(sample.CapeJkg / 2500, .05f, 1);
                    if (score <= bestScore) continue;
                    var town = regions.MinBy(r => HorizontalDistanceSquared(r.Item2, candidate));
                    bestScore = score; found = true;
                    region = ($"{town.Item1} triple point", candidate);
                }
            }
            // An active sea-breeze front is a second, independent line of convergence near the coast.
            // Its own random stream leaves every synoptic candidate draw (and resume replay) unchanged.
            var breeze = StormChaseCoast.SeaBreeze(Seed, DayNumber, initiationHour);
            if (breeze.IsActive)
            {
                var coastRandom = new Random(Mix(unchecked(Day.Seed ^ opportunity.Seed ^ 0x5eab12e5)));
                for (int attempt = 0; attempt < 6; attempt++)
                {
                    float along = RandomFloat(coastRandom, -12000, 12000), into = RandomFloat(coastRandom, -1800, 500);
                    var (px, pz) = breeze.Point(along, into);
                    if (MathF.Abs(px) > 21000 || MathF.Abs(pz - StormChaseWorld.PlayableCenterZ) > 21000 || StormChaseCoast.IsSea(px, pz)) continue;
                    var candidate = new Vector3f(px, 0, pz);
                    if (cells.Any(existing => existing.BirthForecast.DayNumber == DayNumber
                        && HorizontalDistanceSquared(CellPosition(existing), candidate) < 14000f * 14000f)) continue;
                    var sample = StormChaseAtmosphere.Sample(Seed, DayNumber, initiationHour, new(px, 0, pz)).Diagnostics;
                    float score = sample.InitiationPotential01 * Math.Clamp(sample.CapeJkg / 2500, .05f, 1)
                        * .65f * Math.Min(1, breeze.Strength) * (.6f + .4f * (float)coastRandom.NextDouble());
                    if (score <= bestScore) continue;
                    var town = regions.MinBy(r => HorizontalDistanceSquared(r.Item2, candidate));
                    bestScore = score; found = true;
                    region = ($"{town.Item1} {StormChaseSeaBreeze.Kind.ToLowerInvariant()}", candidate);
                }
            }
            if (!found) return;
        }
        var ingredients=StormChaseAtmosphere.Sample(Seed,DayNumber,initiationHour,new(region.Item2.X,0,region.Item2.Z)).Diagnostics;
        // An opportunity is lift along a boundary, not a guaranteed storm or tornado.
        if(ingredients.CapeJkg<120||ingredients.InitiationPotential01<.045f)return;
        float potential=Math.Clamp(.65f*ingredients.TornadoPotential01+.35f*Math.Clamp(ingredients.CapeJkg/3200,0,1),0,1);
        int presetIndex = Math.Clamp((int)MathF.Round(potential*5+RandomFloat(random,-.7f,.6f)),0,5);
        string desiredBehavior;
        float roll = (float)random.NextDouble();
        float nonTornadic=Math.Clamp(.72f-.62f*ingredients.TornadoPotential01,.1f,.72f);
        float cycleChance=Math.Clamp(.06f+.38f*ingredients.TornadoPotential01*Math.Clamp(ingredients.Srh01KmM2S2/180,0,1),.06f,.44f);
        if(ingredients.Shear06KmMetersPerSecond<10||roll<nonTornadic)desiredBehavior="Non-tornadic thunderstorm";
        else if(roll<nonTornadic+.14f)desiredBehavior="Interrupted mesocyclone";
        else if(roll>1-cycleChance)desiredBehavior="Cyclic supercell";
        else desiredBehavior = "Tornadic supercell";

        int stormSeed = opportunity.Seed;
        PmWeatherSupercellOptions source = LiveStormController.CreateOptions(presetIndex, stormSeed) with { VariedLifecycle = true };
        for (int attempt = 0; attempt < 128; attempt++)
        {
            source = LiveStormController.CreateOptions(presetIndex, stormSeed) with { VariedLifecycle = true };
            if (new PmWeatherSupercellSimulation(source).BehaviorName == desiredBehavior) break;
            stormSeed = unchecked(stormSeed + 1);
        }
        int number = ++nextCellNumber;
        float heading=MathF.Atan2(ingredients.StormMotion.Z,ingredients.StormMotion.X)+RandomFloat(random,-.08f,.08f);
        float playbackSpeed = 18 + potential * 10;
        var shapeRandom=new Random(Mix(unchecked(Day.Seed^opportunity.Seed^0x6ba451ef)));
        float moistureRoll=(float)shapeRandom.NextDouble();
        int form=ingredients.DewPointCelsius<15?0:ingredients.DewPointCelsius>21&&moistureRoll<.72f?2:
            ingredients.LclMeters<850&&moistureRoll<.42f?3:moistureRoll<.68f?1:shapeRandom.Next(4);
        (string Name,float CoreMin,float CoreMax,float AnvilMin,float AnvilMax,float BaseMin,float BaseMax,float DepthMin,float DepthMax,
            float UpdraftMin,float UpdraftMax,float WidthMin,float WidthMax,int ShapeMin,int ShapeMax,float DensityMin,float DensityMax,float RainMin,float RainMax) ensemble=form switch
        {
            0=>("Compact towering",500,850,1100,1700,800,1350,3000,4700,100,210,.55f,1.1f,3,10,.68f,.83f,.45f,.72f),
            1=>("Classic organized",850,1450,1700,2600,600,1050,2400,4000,180,330,.8f,1.6f,8,20,.8f,.97f,.75f,1),
            2=>("Broad precipitation",1300,2050,2500,3550,450,800,1900,3200,240,450,1.15f,2,15,30,.87f,1.1f,.9f,1.3f),
            _=>("Low sprawling",1050,1600,2150,3100,500,950,1500,2600,150,300,.6f,1.4f,5,16,.68f,.87f,.6f,.9f),
        };
        float cloudBase=Math.Clamp(ingredients.LclMeters*RandomFloat(shapeRandom,.9f,1.06f),400,2000);
        float developmentWallSeconds=Math.Clamp(RandomFloat(shapeRandom,35,65)+(1-ingredients.InitiationPotential01)*20,30,80);
        var appearance=new StormChaseStormAppearance(ensemble.Name,heading+RandomFloat(shapeRandom,-.55f,.55f),
            RandomFloat(shapeRandom,ensemble.DensityMin,ensemble.DensityMax),RandomFloat(shapeRandom,ensemble.RainMin,ensemble.RainMax),developmentWallSeconds);
        // Simulation time is accelerated, but road chase tracks remain at a
        // playable 20–55 mph rather than multiplying highway-speed motion.
        float actualSpeed = Math.Clamp(MathF.Sqrt(ingredients.StormMotion.X*ingredients.StormMotion.X+ingredients.StormMotion.Z*ingredients.StormMotion.Z),6,28);
        var velocity = new Vector3f(MathF.Cos(heading), 0, MathF.Sin(heading)) * (actualSpeed / playbackSpeed);
        float crosswind = RandomFloat(random, -650, 650);
        var intercept = region.Item2 + new Vector3f(-MathF.Sin(heading) * crosswind, 0, MathF.Cos(heading) * crosswind);
        source = source with
        {
            Id = $"campaign-{Seed}-day-{DayNumber}-cell-{number}",
            CoreRadiusMeters = RandomFloat(shapeRandom,ensemble.CoreMin,ensemble.CoreMax),
            UpdraftRadiusMeters = RandomFloat(shapeRandom,ensemble.UpdraftMin,ensemble.UpdraftMax),
            AnvilRadiusMeters = RandomFloat(shapeRandom,ensemble.AnvilMin,ensemble.AnvilMax),
            CloudBaseMeters = cloudBase,
            CloudTopMeters = cloudBase+Math.Clamp((ingredients.EquilibriumLevelMeters-cloudBase)*.6f,1800,6000),
            MaximumWidthMeters=Math.Clamp(source.MaximumWidthMeters!.Value*RandomFloat(shapeRandom,ensemble.WidthMin,ensemble.WidthMax)*(.7f+.6f*Math.Clamp(ingredients.CapeJkg/3000,0,1)),15,1200),
            // Favorable profiles widen the distribution; a composite is never a wind-speed or EF guarantee.
            PeakWindSpeedMph=Math.Clamp((int)MathF.Round((65+potential*155+Math.Clamp(ingredients.VtpGame/6,0,1)*80)*RandomFloat(shapeRandom,.88f,1.12f)
                +MathF.Pow(RandomFloat(shapeRandom,0,1),4)*potential*50),65,330),
            TornadoShape=shapeRandom.Next(ensemble.ShapeMin,ensemble.ShapeMax+1),
            InitialStage=0,InitialEnergy=0,DevelopmentDurationSeconds=developmentWallSeconds*playbackSpeed,
            Origin = Vector3f.Zero, Velocity = Vector3f.Zero,
        };
        var anchor = new PmWeatherSupercellSimulation(source);
        anchor.Advance(anchor.FirstActivityTimeSeconds);
        var funnelOffset = anchor.Current.ActiveStorms.FirstOrDefault()?.Tornadoes.FirstOrDefault()?.LocalOffset ?? Vector3f.Zero;
        source = source with { Origin = intercept - funnelOffset - velocity * (float)anchor.FirstActivityTimeSeconds, Velocity = velocity };
        var simulation = new PmWeatherSupercellSimulation(source);
        var cell = new StormChaseCampaignCell(number, presetIndex, stormSeed, region.Item1, playbackSpeed, simulation,Day,wallSeconds,
            RandomFloat(random,.25f,.42f),appearance,ingredients);
        var birthBoundary = StormChaseAtmosphere.ActiveBoundary(Seed, DayNumber, initiationHour);
        cell.BoundaryDistance = birthBoundary.SignedDistance(region.Item2.X, region.Item2.Z); cell.BoundaryKind = birthBoundary.NearestKind(region.Item2.X, region.Item2.Z);
        cells.Add(cell);
        TotalStormsSpawned++;
        StormsSpawnedToday++;
        spawnHistory.Add(new(DayNumber, opportunity.Hour, source.Id, region.Item1, stormSeed, simulation.BehaviorName,
            source.Origin, source.Velocity));
        if (spawnHistory.Count > 256) spawnHistory.RemoveAt(0);
        if (cells.Count == 1) controls.SetCampaignPrimary(simulation, presetIndex, stormSeed, playbackSpeed);
    }

    private EnvironmentSceneState BuildEnvironment()
    {
        var regional=atmosphere?.Diagnostics;
        float coverage = Math.Clamp(.04f + (regional?.DewPointCelsius??Day.DewPointCelsius) / 120
            + .015f*Math.Clamp(-(regional?.CinJkg??0)/200,0,1), .04f, .28f);
        var profile = BuiltInEnvironmentProfiles.TemperateOutdoor with
        {
            BaseFogDensity = .000012f, BaseFogStartDistance = 4000,
            CloudCoverage = coverage, CloudDensity = .07f + coverage * .28f,
            CloudSpeed = .012f + (regional?.Shear06KmMetersPerSecond/.51444445f??Day.ShearKnots) / 2500,
        };
        var background = environment.Evaluate(profile, Hour, BuiltInWeatherProfiles.Clear) with
        {
            Weather = WeatherState.Clear, WeatherEffects = WeatherEffectsState.None,
            Storms = Array.Empty<WeatherStormCellState>(), Precipitation = 0,
        };
        RegionalEnvironment=background;
        if (cells.Count == 0) return background;
        var scenes = cells.Select(cell => StormChaseVortexFields.Apply(ApplyAtmosphere(ApplyAppearance(cell.Simulation.Current,cell.Appearance),cell),cell.Seed,elapsedSeconds)).ToArray();
        var allStorms = scenes.SelectMany(scene => scene.ActiveStorms).ToArray();
        var dominant = scenes.OrderByDescending(scene => scene.ActiveStorms.Max(storm => storm.Intensity
            + storm.Tornadoes.Select(t => (t.SmoothedWindSpeed ?? t.WindSpeed) / 200).DefaultIfEmpty(0).Max())).First();
        float funnel = scenes.Max(scene => scene.Weather?.TornadoFunnelAmount ?? 0);
        float debris = scenes.Max(scene => scene.WeatherEffects?.TornadoDebrisParticleAlpha ?? 0);
        var weather = dominant.Weather! with { TornadoFunnelAmount = funnel, DustAmount = debris * .3f };
        var effects = dominant.WeatherEffects! with { TornadoVortexAmount = funnel, TornadoDebrisParticleAlpha = debris };
        return background with
        {
            Weather = weather, WeatherEffects = effects, Storms = allStorms,
            // Storm geometry is carried by each Storms entry. Preserve the regional
            // ambient layer instead of replacing it with PMWeather's sparse fallback.
            Clouds = background.Clouds,
            WindStrength = dominant.WindStrength, SurfaceWetness = dominant.SurfaceWetness,
            Precipitation = dominant.Precipitation,
        };
    }

    /// <summary>
    /// How the day's boundary modulates a cell's tornadoes. Just inside the moist side, convergence and
    /// boundary-generated horizontal vorticity favour tornadoes (most strongly along a warm front, the
    /// classic tornado corridor). A storm that moves onto the cool/dry side is cut off from surface-based
    /// inflow: a cold front undercuts it quickly, the dry side of a dryline mixes it out more gradually,
    /// and north of a warm front it lives on as an elevated storm whose tornadoes lift.
    /// Returns a tornado enhancement (&gt;= 1) and a 0..1 suppression (1 = fully lifted).
    /// </summary>
    internal static (float Enhancement, float Suppression) BoundaryTornadoInfluence(string kind, float signedDistance)
    {
        if (!float.IsFinite(signedDistance) || kind.Length == 0) return (1, 0);
        static float Smooth01(float v) { v = Math.Clamp(v, 0, 1); return v * v * (3 - 2 * v); }
        float corridor = MathF.Exp(-MathF.Pow((signedDistance - 1500) / 3500, 2));
        return kind switch
        {
            "Warm front" => (1 + .35f * corridor, Smooth01((-signedDistance - 1000) / 5000)),
            "Cold front" => (1 + .10f * corridor, Smooth01(-signedDistance / 3000)),
            _ => (1 + .12f * corridor, Smooth01(-signedDistance / 6000)),
        };
    }

    private static EnvironmentSceneState ApplyAtmosphere(EnvironmentSceneState scene,StormChaseCampaignCell cell)
    {
        float support=Math.Clamp(CellSupport(cell),0,1.15f);
        float strength=.48f+.52f*support;
        var (enhancement,suppression)=BoundaryTornadoInfluence(cell.BoundaryKind,cell.BoundaryDistance);
        // Enhanced winds are capped so a favourable boundary cannot push an already-violent source far past its design.
        float tornadoWind=Math.Min(strength*enhancement,1.25f)*(1-.6f*suppression);
        float tornadoWidth=(.78f+.22f*support)*(1+.5f*(enhancement-1))*(1-.35f*suppression);
        float cloudBase=Math.Clamp(cell.Atmosphere.LclMeters,400,2000);
        return scene with
        {
            Weather=scene.Weather is{} weather?weather with{TornadoRisk=cell.Atmosphere.TornadoPotential01}:null,
            Storms=scene.ActiveStorms.Select(storm=>storm with
            {
                Intensity=storm.Intensity*strength,
                Position=storm.Position+cell.MotionOffset,Velocity=cell.MotionVelocity,
                CloudBaseMeters=cloudBase,
                UpdraftRadiusMeters=storm.UpdraftRadiusMeters*(.78f+.22f*support),
                CloudTopMeters=cloudBase+Math.Max(1200,storm.CloudTopMeters-storm.CloudBaseMeters)*(.72f+.28f*support),
                LightningAmount=storm.LightningAmount*Math.Clamp(cell.Atmosphere.CapeJkg/Math.Max(500,cell.BirthAtmosphere.CapeJkg),.25f,1.2f),
                SmoothedWindSpeed=(storm.SmoothedWindSpeed??storm.WindSpeed)*strength,
                Tornadoes=storm.Tornadoes.Select(t=>t with
                {
                    Position=t.Position+cell.MotionOffset,Velocity=cell.MotionVelocity,
                    HeightMeters=cloudBase,
                    TouchdownAmount=t.TouchdownAmount*(1-suppression),
                    SmoothedWindSpeed=(t.SmoothedWindSpeed??t.WindSpeed)*tornadoWind,
                    SmoothedWidthMeters=(t.SmoothedWidthMeters??t.WidthMeters)*tornadoWidth,
                    DamagePotential=t.DamagePotential*tornadoWind
                }).ToArray()
            }).ToArray()
        };
    }

    private static EnvironmentSceneState ApplyAppearance(EnvironmentSceneState scene,StormChaseStormAppearance appearance)
    {
        Vector3f Rotate(Vector3f value)
        {
            float cosine=MathF.Cos(appearance.RotationRadians),sine=MathF.Sin(appearance.RotationRadians);
            return new(value.X*cosine-value.Z*sine,value.Y,value.X*sine+value.Z*cosine);
        }
        return scene with
        {
            Weather=scene.Weather is {} weather?weather with{PrecipitationRate=Math.Clamp(weather.PrecipitationRate*appearance.PrecipitationMultiplier,0,1)}:null,
            Precipitation=Math.Clamp(scene.Precipitation*appearance.PrecipitationMultiplier,0,1),
            WeatherEffects=scene.WeatherEffects is {} effects?effects with{RainParticleAlpha=Math.Clamp(effects.RainParticleAlpha*appearance.PrecipitationMultiplier,0,1)}:null,
            Storms=scene.ActiveStorms.Select(storm=>storm with
        {
            RotationRadians=appearance.RotationRadians,Intensity=storm.Intensity*appearance.DensityMultiplier,
            PrecipitationCoreAmount=Math.Clamp(storm.PrecipitationCoreAmount*appearance.PrecipitationMultiplier,0,1),
            HailCoreAmount=Math.Clamp(storm.HailCoreAmount*appearance.PrecipitationMultiplier,0,1),
            RainWrapAmount=Math.Clamp(storm.RainWrapAmount*appearance.PrecipitationMultiplier,0,1),
            InflowDirection=Rotate(storm.InflowDirection),ForwardFlankCoreOffset=Rotate(storm.ForwardFlankCoreOffset),
            RearFlankCoreOffset=Rotate(storm.RearFlankCoreOffset),
        }).ToArray()};
    }

    internal static StormChaseDayForecast CreateForecast(int campaignSeed, int dayNumber)
    {
        int seed = Mix(unchecked(campaignSeed ^ dayNumber * 486187739));
        var random = new Random(seed);
        float pattern = (float)random.NextDouble();
        bool quiet = pattern < .24f;
        float instability = quiet ? RandomFloat(random, 100, 650) : pattern < .52f ? RandomFloat(random, 550, 1600)
            : pattern < .84f ? RandomFloat(random, 1500, 3100) : RandomFloat(random, 2700, 4500);
        float moisture = quiet ? RandomFloat(random, 6, 13) : pattern < .52f ? RandomFloat(random, 12, 18)
            : RandomFloat(random, 17, 24);
        float shear = quiet ? RandomFloat(random, 7, 20) : pattern < .52f ? RandomFloat(random, 15, 30)
            : pattern < .84f ? RandomFloat(random, 26, 46) : RandomFloat(random, 38, 60);
        float cap = quiet ? RandomFloat(random, .65f, .96f) : RandomFloat(random, .08f, .66f);
        float risk = quiet ? RandomFloat(random, .02f, .18f)
            : Math.Clamp(instability / 4500 * .38f + moisture / 24 * .2f + shear / 60 * .42f - cap * .17f, .15f, .98f);
        string name = risk < .2f ? "Quiet / capped" : risk < .35f ? "Marginal risk" : risk < .5f ? "Slight risk"
            : risk < .65f ? "Enhanced risk" : risk < .8f ? "Moderate risk" : "High risk";
        int count = quiet ? random.NextDouble() < .13 ? 1 : 0
            : risk < .35f ? random.NextDouble() < .25 ? 0 : 1
            : risk < .5f ? random.Next(1, 3) : risk < .7f ? random.Next(2, 4) : random.Next(3, 5);
        float nocturnalSupport=Math.Clamp((moisture-14)/9,0,1)*Math.Clamp((shear-20)/32,0,1)*(1-cap*.65f);
        return new(dayNumber, seed, name, instability, moisture, shear, cap, risk, count, RandomFloat(random, -32, 32),nocturnalSupport);
    }

    private static List<Opportunity> CreateOpportunities(StormChaseDayForecast day)
    {
        var random = new Random(Mix(unchecked(day.Seed ^ 0x17239411)));
        var result = new List<Opportunity>();
        float outbreakHour = RandomFloat(random, 14.6f, 16.75f);
        for (int index = 0; index < day.Opportunities; index++)
        {
            // Two draws give a broad afternoon peak instead of a fixed daily
            // timetable. Occasional late-morning cells still develop naturally.
            float hour = day.Risk >= .65f && day.Opportunities >= 3
                ? outbreakHour + RandomFloat(random, -.35f, .35f)
                : 11.25f + ((float)random.NextDouble() + (float)random.NextDouble()) * 3.1f;
            result.Add(new(hour, random.Next()));
        }
        // Most nights stay quiet. Supported low-level moisture/shear can create
        // an evening or overnight opportunity without guaranteeing its outcome.
        if(day.NocturnalSupport>=.3f&&random.NextDouble()<.16+.48*day.NocturnalSupport)
        {
            bool afterMidnight=random.NextDouble()<.3;
            result.Add(new(afterMidnight?RandomFloat(random,.35f,2.8f):RandomFloat(random,19.6f,23.4f),random.Next(),true));
        }
        result.Sort((left, right) => left.Hour.CompareTo(right.Hour));
        return result;
    }

    private int DayAt(double seconds) => (int)Math.Floor((startHour + seconds / DefaultDayLengthSeconds * 24) / 24) + 1;
    private float HourAt(double seconds)
    {
        double hour = startHour + seconds / DefaultDayLengthSeconds * 24;
        // A value just below midnight (e.g. 23.99999999999997) rounds to exactly
        // 24f when narrowed, which every consumer rejects as out of range.
        float wrapped = (float)(hour - Math.Floor(hour / 24) * 24);
        return wrapped >= 24f ? 0f : wrapped;
    }
    internal static float AtmosphericSupport(StormChaseDayForecast day,float hour)
    {
        float age=hour>=19?hour-19:hour+5;
        float nocturnal=.72f*day.NocturnalSupport*Smooth(age/2)*(1-Smooth((age-7)/5));
        return Math.Clamp(Heating(hour)+nocturnal,0,1);
    }
    private static float AvailableAt(StormChaseDayForecast day,float hour)=>day.InstabilityJkg*AtmosphericSupport(day,hour);
    private static float Heating(float hour)
    {
        if(hour>=13&&hour<19)return .7f+.3f*(19-hour)/6;
        float age=hour>=19?hour-19:hour+5;
        float retained=.7f*MathF.Exp(-age/5);
        float solar=hour>7&&hour<19?MathF.Pow(MathF.Sin((hour-7)/12*MathF.PI),.85f):0;
        return Math.Max(retained,solar);
    }
    private static float Smooth(float value){float t=Math.Clamp(value,0,1);return t*t*(3-2*t);}
    private static float RandomFloat(Random random, float minimum, float maximum)
        => minimum + (maximum - minimum) * (float)random.NextDouble();
    private static int Mix(int seed)
    {
        uint value = unchecked((uint)seed);
        value = unchecked((value ^ (value >> 16)) * 0x7feb352du);
        value = unchecked((value ^ (value >> 15)) * 0x846ca68bu);
        return unchecked((int)(value ^ (value >> 16)));
    }
    private static string Read(string[] args, string option, string fallback)
    {
        int index = Array.FindIndex(args, value => value.Equals(option, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : fallback;
    }

    private sealed record Opportunity(float Hour, int Seed,bool Nocturnal=false);

    /// <summary>
    /// A genuine, naturally developed post-midnight touchdown. Seed 68 is tried first; because
    /// storm initiation follows each day's boundary, other seeds are searched if its history changes.
    /// </summary>
    internal static StormChaseNightCapture CreateNightCapture(int? seed=null)
    {
        foreach(int candidate in seed is {} fixedSeed?[fixedSeed]:new[]{68}.Concat(Enumerable.Range(1,160).Where(value=>value!=68)))
            if(TryNightCapture(candidate) is {} capture)return capture;
        throw new InvalidOperationException("No searched campaign seed had a naturally developed post-midnight touchdown in the capture interval.");
    }

    private static StormChaseNightCapture? TryNightCapture(int seed)
    {
        var campaign=new StormChaseCampaignWeather(new LiveStormController(["--demo"]),["--campaign-seed",seed.ToString(CultureInfo.InvariantCulture)]);
        for(int second=0;second<2400;second++)
        {
            campaign.Update(1);
            if(campaign.Hour>=6)continue;
            var storm=campaign.Current.ActiveStorms.FirstOrDefault(s=>s.Tornadoes.Any(t=>t.TouchdownAmount>=.95f));
            if(storm is null)continue;
            var cell=campaign.ActiveCells.Single(c=>c.Simulation.Current.ActiveStorms.Any(s=>s.Id==storm.Id));
            var tornado=storm.Tornadoes.First(t=>t.TouchdownAmount>=.95f);
            return new(seed,campaign.ElapsedSeconds,campaign.DayNumber,campaign.Hour,campaign.AvailableInstability,
                cell.BirthForecast.NocturnalSupport,cell.Simulation.TornadoEpisodeNumber,cell.Simulation.TouchdownCount,storm.Id,tornado,campaign.Current);
        }
        return null;
    }

    internal static IReadOnlyList<StormChaseSourceCapture> CreateDevelopmentCapturePoints(int seed=1)
    {
        var campaign=new StormChaseCampaignWeather(new LiveStormController(["--demo"]),["--campaign-seed",seed.ToString(CultureInfo.InvariantCulture)]);
        var points=new List<StormChaseSourceCapture>();string? id=null;
        for(int tick=0;tick<24000;tick++)
        {
            campaign.Update(TickSeconds);
            var cell=id is null?campaign.ActiveCells.FirstOrDefault():campaign.ActiveCells.FirstOrDefault(c=>c.Simulation.Current.ActiveStorms.Any(s=>s.Id==id));
            if(cell is null)continue;
            var storm=cell.Simulation.Current.ActiveStorms.First();id??=storm.Id;
            float stage=storm.SmoothedStage??storm.Stage;
            if(points.Count==0)points.Add(Capture("birth",campaign,cell));
            else if(points.Count==1&&stage>=.8f)points.Add(Capture("developing",campaign,cell));
            else if(points.Count==2&&stage>=1.85f){points.Add(Capture("organized",campaign,cell));return points;}
        }
        throw new InvalidOperationException("The selected source did not naturally organize during the capture interval.");
    }

    internal static IReadOnlyList<StormChaseSourceCapture> CreateMorphologyCapturePoints()
    {
        var points=new List<StormChaseSourceCapture>();var forms=new HashSet<string>();
        for(int seed=1;seed<=128&&forms.Count<4;seed++)
        {
            var campaign=new StormChaseCampaignWeather(new LiveStormController(["--demo"]),["--campaign-seed",seed.ToString(CultureInfo.InvariantCulture)]);
            for(int second=0;second<1200&&forms.Count<4;second++)
            {
                campaign.Update(1);
                foreach(var cell in campaign.ActiveCells)
                    // A touchdown capture needs a tornado on the ground as shown, i.e. not lifted on the cool/dry side of a boundary.
                    if(cell.Simulation.Current.ActiveStorms.Any(s=>s.Tornadoes.Any(t=>t.TouchdownAmount
                        *(1-BoundaryTornadoInfluence(cell.BoundaryKind,cell.BoundaryDistance).Suppression)>=.95f))&&forms.Add(cell.Appearance.Name))
                        points.Add(Capture("touchdown",campaign,cell));
            }
        }
        if(forms.Count!=4)throw new InvalidOperationException("Naturally spawned source storms did not cover all morphology ensembles.");
        return points;
    }

    private static StormChaseSourceCapture Capture(string name,StormChaseCampaignWeather campaign,StormChaseCampaignCell cell)
    {
        var storm=cell.Simulation.Current.ActiveStorms.First();
        return new(name,campaign.Seed,campaign.ElapsedSeconds,campaign.DayNumber,campaign.Hour,storm.Id,cell.Appearance.Name,
            cell.Simulation.CurrentPhase,storm.SmoothedStage??storm.Stage,campaign.ElapsedSeconds-cell.BornSeconds,
            cell.Appearance.DevelopmentWallSeconds,campaign.Current);
    }
}

internal sealed record StormChaseNightCapture(int Seed,double ElapsedSeconds,int DayNumber,float Hour,float AvailableInstability,
    float NocturnalSupport,int Episode,int Touchdowns,string StormId,WeatherTornadoState Tornado,EnvironmentSceneState Environment);
internal sealed record StormChaseSourceCapture(string Name,int Seed,double ElapsedSeconds,int DayNumber,float Hour,string StormId,string Form,
    string Phase,float Stage,double AgeSeconds,float DevelopmentWallSeconds,EnvironmentSceneState Environment);
internal sealed record StormChaseTornadoForm(int Seed,string StormId,string TornadoId,string Form,int Episode,float Shape,float Width,Vector3f Offset);

internal sealed record StormChaseDayForecast(int DayNumber, int Seed, string Name, float InstabilityJkg,
    float DewPointCelsius, float ShearKnots, float CapStrength, float Risk, int Opportunities, float TrackDirectionDegrees,float NocturnalSupport=0);
internal sealed record StormChaseCampaignCell(int Number, int PresetIndex, int Seed, string Region, float PlaybackSpeed,
    PmWeatherSupercellSimulation Simulation,StormChaseDayForecast BirthForecast,double BornSeconds,float MinimumSupport,
    StormChaseStormAppearance Appearance,AtmosphereDiagnostics BirthAtmosphere)
{
    public AtmosphereDiagnostics Atmosphere { get; set; } = BirthAtmosphere;
    /// <summary>Signed distance (m) to the active boundary, refreshed with <see cref="Atmosphere"/>; &gt; 0 on the moist/warm side.</summary>
    public float BoundaryDistance { get; set; } = float.PositiveInfinity;
    public string BoundaryKind { get; set; } = "";
    public Vector3f MotionOffset { get; set; }
    public Vector3f MotionVelocity { get; set; } = Simulation.Current.ActiveStorms.First().Velocity*PlaybackSpeed;
}
internal sealed record StormChaseStormAppearance(string Name,float RotationRadians,float DensityMultiplier,float PrecipitationMultiplier,float DevelopmentWallSeconds);
internal sealed record StormChaseCampaignSpawn(int DayNumber, float Hour, string Id, string Region, int Seed,
    string Behavior, Vector3f Origin, Vector3f Velocity);

internal static partial class Showcase
{
    /// <summary>Campaign verification requires neither a window nor a GPU.</summary>
    internal static int VerifyCampaignWeather(string output)
    {
        var checks = new List<object>();
        void Check(string name, bool pass)
        {
            checks.Add(new { Name = name, Passed = pass });
            if (!pass) throw new InvalidOperationException("Campaign weather check failed: " + name);
        }
        StormChaseCampaignWeather Make(int seed = 13, double elapsed = 0) => new(
            new LiveStormController(["--demo"]), ["--campaign-seed", seed.ToString(CultureInfo.InvariantCulture)], elapsed);
        var clock = Make();
        Check("Default starts at 08:00 with no guaranteed storm", clock.Hour == 8 && clock.ActiveStormCount == 0);
        clock.Update(1200);
        Check("A complete 24-hour cycle takes 1200 real seconds", clock.DayNumber == 2 && Math.Abs(clock.Hour - 8) < .0001);
        var forecasts = Enumerable.Range(1, 32).Select(day => StormChaseCampaignWeather.CreateForecast(13, day)).ToArray();
        Check("Seeded days differ in atmospheric ingredients", forecasts.Select(day => day.InstabilityJkg).Distinct().Count() >= 16);
        Check("Quiet days and multiple-storm days both occur", forecasts.Any(day => day.Opportunities == 0)
            && forecasts.Any(day => day.Opportunities >= 3));
        Check("Daily risks and ingredients remain finite and bounded", forecasts.All(day => day.Risk is >= 0 and <= 1
            && float.IsFinite(day.InstabilityJkg) && day.InstabilityJkg >= 0 && day.ShearKnots is >= 0 and <= 60));
        int activeSeed = 0;
        StormChaseCampaignWeather? active = null;
        int maximumConcurrent = 0;
        List<object> snapshots = [];
        foreach (int candidate in Enumerable.Range(1, 256).Where(seed =>
            StormChaseCampaignWeather.CreateForecast(seed, 1) is { Opportunities: 4, Risk: >= .65f }))
        {
            activeSeed = candidate;
            active = Make(candidate);
            maximumConcurrent = 0;
            snapshots = [];
            for (int seconds = 0; seconds < 1200; seconds++)
            {
                active.Update(1);
                maximumConcurrent = Math.Max(maximumConcurrent, active.ActiveStormCount);
                if (seconds % 60 == 0) snapshots.Add(new { active.DayNumber, active.Hour, active.ForecastName,
                    active.ActiveStormCount, Phases = active.ActiveCells.Select(cell => cell.Simulation.CurrentPhase).ToArray() });
            }
            if (maximumConcurrent == 4) break;
        }
        Check("Four independent storms coexist", maximumConcurrent == 4 && active is not null);
        if (active is null) throw new InvalidOperationException("No active campaign day was found.");
        Check("Independent cloud identities and tracks stay attached", active.SpawnHistory.Select(spawn => spawn.Id).Distinct().Count()
            == active.SpawnHistory.Count && active.SpawnHistory.All(spawn => float.IsFinite(spawn.Origin.X) && float.IsFinite(spawn.Velocity.X)));
        var samples=new List<object>();var allSpawns=new List<StormChaseCampaignSpawn>();var nightlyTouchdowns=new List<object>();
        var coolingProof=new List<object>();var coolingResults=new List<bool>();var births=new List<object>();var birthResults=new List<bool>();
        var formsByPotential=new Dictionary<int,HashSet<string>>();var rainByForm=new Dictionary<string,List<float>>();
        var tornadoForms=new List<StormChaseTornadoForm>();int cutoffSurvivors=0,quietNights=0,coolSideTouchdownFrames=0,coolSideGrounded=0,corridorTouchdownFrames=0;float totalWindAt23=0,totalWindAt5=0,totalWindAt7=0,totalNightPeak=0;
        foreach(int seed in Enumerable.Range(1,96))
        {
            var state=Make(seed);var touched=new HashSet<string>();var born=new HashSet<string>();var episodes=new HashSet<string>();var coolingSeen=new HashSet<string>();var fades=new Dictionary<string,(double Seconds,float Stage,float Wind)>();
            int nightSpawns=0;float wind23=0,wind5=0,wind7=0,nightPeak=0;
            for(int second=0;second<1200;second++)
            {
                state.Update(1);
                if(state.Hour is >=18.6f and <18.63f&&state.ActiveCells.Any(c=>!c.Simulation.IsDissipationRequested))cutoffSurvivors++;
                float wind=state.Current.ActiveStorms.Select(s=>s.SmoothedWindSpeed??s.WindSpeed).DefaultIfEmpty(0).Max();
                if(state.Hour is >=23 and <23.02f)wind23=wind;
                if(state.Hour is >=5 and <5.02f)wind5=wind;
                if(state.Hour is >=7 and <7.02f)wind7=wind;
                if(state.IsNight)nightPeak=Math.Max(nightPeak,wind);
                foreach(var cell in state.ActiveCells)
                {
                    var source=cell.Simulation.Current.ActiveStorms.FirstOrDefault();if(source is null)continue;
                    if(state.Current.ActiveStorms.FirstOrDefault(s=>s.Id==source.Id) is{} rendered&&source.Tornadoes.Any(t=>t.TouchdownAmount>=.95f))
                    {
                        var influence=StormChaseCampaignWeather.BoundaryTornadoInfluence(cell.BoundaryKind,cell.BoundaryDistance);
                        bool grounded=rendered.Tornadoes.Any(t=>t.TouchdownAmount>=.95f);
                        if(influence.Suppression>.99f){coolSideTouchdownFrames++;if(grounded)coolSideGrounded++;}
                        if(influence.Enhancement>1.05f&&grounded)corridorTouchdownFrames++;
                    }
                    if(born.Add(source.Id))
                    {
                        bool thin=source.Intensity<.005f&&(source.SmoothedStage??source.Stage)<.1f&&source.PrecipitationCoreAmount<.005f&&source.WallCloudAmount==0&&source.Tornadoes.Count==0;
                        if(!formsByPotential.TryGetValue(cell.PresetIndex,out var potentialForms))formsByPotential[cell.PresetIndex]=potentialForms=[];
                        potentialForms.Add(cell.Appearance.Name);
                        birthResults.Add(thin);births.Add(new{Seed=seed,source.Id,cell.PresetIndex,cell.Appearance,Stage=source.SmoothedStage,source.Intensity,source.PrecipitationCoreAmount,source.WallCloudAmount,TornadoCount=source.Tornadoes.Count,Thin=thin});
                    }
                    foreach(var tornado in source.Tornadoes.Where(t=>t.TouchdownAmount>=.95f))
                        if(episodes.Add(tornado.Id))
                        {
                            tornadoForms.Add(new(seed,source.Id,tornado.Id,cell.Appearance.Name,cell.Simulation.TornadoEpisodeNumber,tornado.TornadoShape,tornado.WidthMeters,tornado.LocalOffset));
                            if(state.Current.ActiveStorms.FirstOrDefault(s=>s.Id==source.Id) is{} shown)
                            {
                                if(!rainByForm.TryGetValue(cell.Appearance.Name,out var rain))rainByForm[cell.Appearance.Name]=rain=[];
                                rain.Add(shown.PrecipitationCoreAmount);
                            }
                        }
                    if((state.Hour>=19.5f||state.Hour<6)&&source.Tornadoes.Any(t=>t.TouchdownAmount>=.95f)&&touched.Add(source.Id))
                        nightlyTouchdowns.Add(new{Seed=seed,state.DayNumber,state.Hour,state.ElapsedSeconds,source.Id,
                            cell.Simulation.BehaviorName,cell.Simulation.TornadoEpisodeNumber,cell.Simulation.TouchdownCount,
                            Wind=source.SmoothedWindSpeed??source.WindSpeed,Support=StormChaseCampaignWeather.AtmosphericSupport(cell.BirthForecast,state.Hour)});
                    if(cell.Simulation.IsDissipationRequested&&coolingSeen.Add(source.Id))
                        fades[source.Id]=(state.ElapsedSeconds,source.SmoothedStage??source.Stage,source.SmoothedWindSpeed??source.WindSpeed);
                }
                foreach(var fade in fades.ToArray())
                {
                    if(state.ElapsedSeconds-fade.Value.Seconds<100)continue;
                    var after=state.Current.ActiveStorms.FirstOrDefault(s=>s.Id==fade.Key);
                    float stage=after?.SmoothedStage??after?.Stage??0,windAfter=after?.SmoothedWindSpeed??after?.WindSpeed??0;
                    bool weakened=stage<fade.Value.Stage||windAfter<fade.Value.Wind;coolingResults.Add(weakened);
                    coolingProof.Add(new{Seed=seed,Id=fade.Key,ElapsedWallSeconds=state.ElapsedSeconds-fade.Value.Seconds,
                        FromStage=fade.Value.Stage,ToStage=stage,FromWind=fade.Value.Wind,ToWind=windAfter,
                        Weakened=weakened});
                    fades.Remove(fade.Key);
                }
            }
            allSpawns.AddRange(state.SpawnHistory);nightSpawns=state.SpawnHistory.Count(s=>s.Hour>=19.5f||s.Hour<6);
            if(nightSpawns==0)quietNights++;totalWindAt23+=wind23;totalWindAt5+=wind5;totalWindAt7+=wind7;totalNightPeak+=nightPeak;
            samples.Add(new{Seed=seed,NightSpawns=nightSpawns,WindAt23=wind23,WindAt5=wind5,WindAt7=wind7,NightPeak=nightPeak});
        }
        Console.WriteLine($"Boundary tornado influence: {coolSideTouchdownFrames} source-touchdown seconds deep on the cool/dry side ({coolSideGrounded} still grounded), {corridorTouchdownFrames} grounded seconds in the moist-side corridor.");
        Check("Tornadoes lift when their storm moves deep onto the cool/dry side of the boundary",coolSideGrounded==0);
        Check("Tornadoes still touch down in the moist-side boundary corridor",corridorTouchdownFrames>0);
        {
            var warm=StormChaseCampaignWeather.BoundaryTornadoInfluence("Warm front",1500);
            var dry=StormChaseCampaignWeather.BoundaryTornadoInfluence("Dryline",1500);
            var cold=StormChaseCampaignWeather.BoundaryTornadoInfluence("Cold front",1500);
            Check("Boundary corridor favours warm fronts and is neutral far from any boundary",warm.Enhancement>dry.Enhancement&&dry.Enhancement>1&&cold.Enhancement>1
                &&StormChaseCampaignWeather.BoundaryTornadoInfluence("Dryline",30000)==(1,0)&&StormChaseCampaignWeather.BoundaryTornadoInfluence("",-30000)==(1,0));
            Check("Cold fronts undercut faster than drylines; warm fronts still allow tornadoes right at the front",
                StormChaseCampaignWeather.BoundaryTornadoInfluence("Cold front",-2500).Suppression>StormChaseCampaignWeather.BoundaryTornadoInfluence("Dryline",-2500).Suppression
                &&StormChaseCampaignWeather.BoundaryTornadoInfluence("Warm front",-500).Suppression==0&&warm.Suppression==0);
        }
        // A new career's first storm is placed at Meridian City by design; only later storms choose a boundary point.
        int chosen=allSpawns.Count(s=>s.Region!="Meridian City"),atTriple=allSpawns.Count(s=>s.Region.EndsWith("triple point"));
        Console.WriteLine($"Triple-point initiations: {atTriple}/{chosen} boundary-chosen storms ({allSpawns.Count} total incl. the fixed first storm).");
        Check("Storms sometimes initiate at a triple point, not always",atTriple>0&&atTriple<chosen/2);
        int atSeaBreeze=allSpawns.Count(s=>s.Region.EndsWith(StormChaseSeaBreeze.Kind.ToLowerInvariant()));
        Console.WriteLine($"Sea-breeze front initiations: {atSeaBreeze}/{chosen} boundary-chosen storms.");
        Check("The afternoon Gulf sea-breeze front initiates some coastal storms without dominating",atSeaBreeze>0&&atSeaBreeze<chosen/3
            &&allSpawns.Where(s=>s.Region.EndsWith(StormChaseSeaBreeze.Kind.ToLowerInvariant())).All(s=>s.Hour is >=10 and <22));
        Check("Afternoon remains the usual storm development window",allSpawns.Count(s=>s.Hour is >=10 and <19.5f)>allSpawns.Count/2);
        Check("Supported nocturnal opportunities are occasional rather than guaranteed",quietNights>48&&quietNights<96);
        Check("Existing storms survive the former 18:36 kill boundary",cutoffSurvivors>0);
        Check("Naturally spawned varied storms produce nighttime touchdowns",nightlyTouchdowns.Count>0);
        Check("Actual source decay weakens cells gradually over two campaign hours",coolingResults.Count>0&&coolingResults.All(value=>value));
        Check("Nighttime source winds generally weaken toward morning across the seed set",totalWindAt7<totalNightPeak);
        float SupportMean(float hour)=>forecasts.Average(day=>StormChaseCampaignWeather.AtmosphericSupport(day,hour));
        Check("Atmospheric energy falls progressively later overnight",SupportMean(20)>SupportMean(23)&&SupportMean(23)>SupportMean(2)&&SupportMean(2)>SupportMean(5));
        var capture=StormChaseCampaignWeather.CreateNightCapture();
        Check("Post-midnight capture is a real source touchdown",capture.Hour<6&&capture.Tornado.TouchdownAmount>=.95f&&capture.Touchdowns>=1&&capture.Episode>=1);
        Check("Storms retain the regional ambient cloud background",capture.Environment.Clouds.Coverage>.04f&&capture.Environment.Clouds.VolumetricDensity>0);
        Check("All naturally born clouds start thin without precipitation, wall cloud or funnel",birthResults.Count>100&&birthResults.All(result=>result));
        Check("Parent morphology varies independently within the same intensity potential",formsByPotential.Values.Any(forms=>forms.Count==4));
        var development=StormChaseCampaignWeather.CreateDevelopmentCapturePoints();
        Check("Actual source development progresses gradually before organization",development.Count==3&&development[0].Stage<.001f&&development[1].Stage>=.8f&&development[1].Stage<1.5f&&development[2].AgeSeconds>=30&&development[2].AgeSeconds<=81);
        var morphology=StormChaseCampaignWeather.CreateMorphologyCapturePoints();
        var morphologyStorms=morphology.Select(point=>point.Environment.ActiveStorms.Single(s=>s.Id==point.StormId)).ToArray();
        Check("Four distinct geometric ensembles naturally produce ground tornadoes",morphology.Select(point=>point.Form).Distinct().Count()==4&&morphologyStorms.All(storm=>storm.Tornadoes.Any(t=>t.TouchdownAmount>=.95f)));
        Check("Actual mature parent geometry spans broad and tall forms",morphologyStorms.Max(s=>s.AnvilRadiusMeters)/morphologyStorms.Min(s=>s.AnvilRadiusMeters)>1.7f&&morphologyStorms.Select(s=>(s.CloudTopMeters-s.CloudBaseMeters)/s.AnvilRadiusMeters).Max()/morphologyStorms.Select(s=>(s.CloudTopMeters-s.CloudBaseMeters)/s.AnvilRadiusMeters).Min()>2);
        Check("Mature source funnels vary shape and width",tornadoForms.Select(t=>t.Shape).Distinct().Count()>=10&&tornadoForms.Max(t=>t.Width)/tornadoForms.Min(t=>t.Width)>3);
        Check("A parent storm recycles into genuinely different tornado forms",tornadoForms.GroupBy(t=>t.StormId).Any(g=>g.Select(t=>t.Episode).Distinct().Count()>1&&g.Select(t=>(t.Shape,t.Width,t.Offset)).Distinct().Count()>1));
        Console.WriteLine("Morphology captures: "+string.Join("; ",morphology.Zip(morphologyStorms,(point,storm)=>
            FormattableString.Invariant($"{point.Form} seed {point.Seed} rain {storm.PrecipitationCoreAmount:0.000} stage {point.Stage:0.00}"))));
        // Rendered rain at each tornado episode's first touchdown, averaged per ensemble over the 96-seed population
        // (a single first-found storm per form sat on the threshold and flipped with any change to the atmosphere).
        // The four morphology captures join the pool so every ensemble is represented (compact storms need dry air and are rare).
        foreach(var (point,storm) in morphology.Zip(morphologyStorms))
        {
            if(!rainByForm.TryGetValue(point.Form,out var rain))rainByForm[point.Form]=rain=[];
            rain.Add(storm.PrecipitationCoreAmount);
        }
        var rainMeans=rainByForm.ToDictionary(pair=>pair.Key,pair=>pair.Value.Average());
        Console.WriteLine("Touchdown rain by ensemble: "+string.Join("; ",rainMeans.Select(pair=>FormattableString.Invariant($"{pair.Key} {pair.Value:0.000} (n={rainByForm[pair.Key].Count})"))));
        Check("Low and high precipitation ensembles retain distinct rain profiles",rainMeans.Count==4&&rainMeans.Values.Max()/rainMeans.Values.Min()>1.4f);
        foreach(var point in development.Concat(morphology))
            Check("Source capture reconstructs deterministically: "+point.Name+" / "+point.Form,
                JsonSerializer.Serialize(Make(point.Seed,point.ElapsedSeconds).Current,new JsonSerializerOptions{IncludeFields=true})==JsonSerializer.Serialize(point.Environment,new JsonSerializerOptions{IncludeFields=true}));
        var batched = Make(activeSeed);
        var framed = Make(activeSeed);
        batched.Update(450);
        for (int update = 0; update < 450 * 30; update++) framed.Update(1d / 30);
        string Fingerprint(StormChaseCampaignWeather state) => JsonSerializer.Serialize(new
        {
            state.Day, state.Current, state.TotalStormsSpawned, state.SpawnHistory,
            Phases = state.ActiveCells.Select(cell => new { cell.Simulation.TickCount, cell.Simulation.CurrentPhase,
                cell.Simulation.TouchdownCount, cell.Simulation.TornadoEpisodeNumber }).ToArray()
        },new JsonSerializerOptions{IncludeFields=true});
        Check("Campaign weather is independent of update cadence", Fingerprint(batched) == Fingerprint(framed));
        var resumed = Make(activeSeed, 450);
        Check("Current-day resume reconstructs the same weather", Fingerprint(batched) == Fingerprint(resumed));
        var overnight=Make(capture.Seed);overnight.Update(capture.ElapsedSeconds);
        var overnightResume=Make(capture.Seed,capture.ElapsedSeconds);
        // A resume replays from the previous day's start, so lifetime counters (total spawned,
        // older spawn history) cannot match a capture made two or more days in; the live weather must.
        string LiveFingerprint(StormChaseCampaignWeather state) => JsonSerializer.Serialize(new
        {
            state.Day, state.Current,
            Phases = state.ActiveCells.Select(cell => new { cell.Simulation.TickCount, cell.Simulation.CurrentPhase,
                cell.Simulation.TouchdownCount, cell.Simulation.TornadoEpisodeNumber }).ToArray()
        },new JsonSerializerOptions{IncludeFields=true});
        if(LiveFingerprint(overnight)!=LiveFingerprint(overnightResume))
        {
            File.WriteAllText(Path.Combine(output,"overnight-bulk.json"),LiveFingerprint(overnight));
            File.WriteAllText(Path.Combine(output,"overnight-resume.json"),LiveFingerprint(overnightResume));
        }
        Check("Resume reconstructs storms carried across midnight",LiveFingerprint(overnight)==LiveFingerprint(overnightResume));
        Directory.CreateDirectory(output);
        string path = Path.Combine(output, "campaign-weather.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new { Checks = checks, ActiveSeed = activeSeed,
            MaximumConcurrent = maximumConcurrent, Forecasts = forecasts, Timeline = snapshots,
            Spawns = active.SpawnHistory,NightSeedSamples=samples,NightTouchdowns=nightlyTouchdowns,Cooling=coolingProof,
            MeanWindAt23=totalWindAt23/96,MeanWindAt5=totalWindAt5/96,MeanWindAt7=totalWindAt7/96,MeanNightPeak=totalNightPeak/96,
            AtmosphericSupport=new{At20=SupportMean(20),At23=SupportMean(23),At2=SupportMean(2),At5=SupportMean(5)},Capture=capture,
            Births=births,DevelopmentCaptures=development,MorphologyCaptures=morphology,TornadoForms=tornadoForms },
            new JsonSerializerOptions { WriteIndented = true,IncludeFields=true }));
        File.WriteAllText(Path.Combine(output,"campaign-night-capture.json"),JsonSerializer.Serialize(capture,new JsonSerializerOptions{WriteIndented=true,IncludeFields=true}));
        File.WriteAllText(Path.Combine(output,"campaign-appearance-captures.json"),JsonSerializer.Serialize(new{Development=development,Morphology=morphology},new JsonSerializerOptions{WriteIndented=true,IncludeFields=true}));
        Console.WriteLine("Campaign weather verification passed: " + path);
        return 0;
    }
}
