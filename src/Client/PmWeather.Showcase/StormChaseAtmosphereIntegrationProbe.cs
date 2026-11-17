using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Engine.Environment;
using Shared.Contracts.Rendering;
using Shared.Contracts.World;
using StormChase.Protocol;

internal static partial class Showcase
{
    /// <summary>Natural source campaigns test diagnosed weather's effect on the visible/physical state.</summary>
    internal static int VerifyAtmosphereIntegration(string output)
    {
        Directory.CreateDirectory(output);
        var checks = new List<object>(); int failures = 0;
        void Check(string name, bool passed)
        {
            checks.Add(new { name, passed });
            if (!passed) { failures++; Console.Error.WriteLine("FAIL: " + name); }
        }
        static StormChaseCampaignWeather Make(int seed, double seconds = 0)
            => new(new LiveStormController(["--demo"]), ["--campaign-seed", seed.ToString(CultureInfo.InvariantCulture)], seconds);
        static float Length(Vector3f value) => MathF.Sqrt(value.X * value.X + value.Z * value.Z);
        static float Distance(Vector3f left, Vector3f right) => Length(left - right);
        static string StormId(StormChaseCampaignCell cell) => cell.Simulation.Current.ActiveStorms.First().Id;
        var birthplace = new NetVector3(6000, 0, 7000);
        var forecast = Make(73019);
        // The outlook samples the day's target area on the moist side of its boundary (not a fixed town).
        var outlookPoint = StormChaseAtmosphere.OutlookPoint(forecast.Seed, forecast.DayNumber);
        var independentlySampled = StormChaseAtmosphere.Sample(forecast.Seed, forecast.DayNumber, forecast.Hour, outlookPoint);
        float outlookSide = StormChaseAtmosphere.Boundary(forecast.Seed, forecast.DayNumber, 15).SignedDistance(outlookPoint.X, outlookPoint.Z);
        Check("outlook sounding is taken on the moist side within ~6 km of the day's boundary or triple point",
            forecast.Atmosphere.SamplePosition == outlookPoint && outlookSide is > 900 and < 6100);
        Check("campaign reports parcel-integrated CAPE instead of nominal day ingredient", forecast.AvailableInstability == independentlySampled.Diagnostics.CapeJkg
            && forecast.AvailableInstability != forecast.Day.InstabilityJkg);
        Check("forecast uses actual sounding and three diagnosed daily outlooks", forecast.Atmosphere.Sounding.Length == 97
            && forecast.Atmosphere.Diagnostics == independentlySampled.Diagnostics && forecast.Atmosphere.ThreeDayForecast.Length == 3);
        {
            int nightAbovePeak = 0, meridianNightAbovePeak = 0, betterThanMeridian = 0; const int Days = 60;
            for (int day = 1; day <= Days; day++)
            {
                var targetDay = StormChaseAtmosphere.CreateThreeDayForecast(73019, day)[0];
                var meridian = StormChaseAtmosphere.CreateThreeDayForecast(73019, day, birthplace)[0];
                if (targetDay.Overnight.CapeJkg > targetDay.Peak.CapeJkg) nightAbovePeak++;
                if (meridian.Overnight.CapeJkg > meridian.Peak.CapeJkg) meridianNightAbovePeak++;
                if (targetDay.Peak.CapeJkg * Math.Max(.05f, targetDay.Peak.InitiationPotential01)
                    >= meridian.Peak.CapeJkg * Math.Max(.05f, meridian.Peak.InitiationPotential01) - 1) betterThanMeridian++;
            }
            Console.Error.WriteLine($"Outlook target vs Meridian over {Days} days: night>peak {nightAbovePeak} vs {meridianNightAbovePeak}; target at least as favourable {betterThanMeridian}/{Days}.");
            Check("boundary-targeted outlook is at least as favourable as the fixed town on most days and rarely peaks overnight",
                betterThanMeridian >= Days * .9f && nightAbovePeak <= meridianNightAbovePeak);
        }
        var cached = forecast.Atmosphere;
        forecast.Update(.95);
        Check("atmosphere column is cached between coarse samples", ReferenceEquals(cached, forecast.Atmosphere));
        forecast.Update(.05);
        Check("coarse atmosphere sample advances at one second", !ReferenceEquals(cached, forecast.Atmosphere)
            && forecast.Atmosphere.CampaignSeconds == 1 && forecast.Atmosphere.ServerTick == 20);

        var births = new List<object>(); var nightTouchdowns = new List<object>();
        var behaviors = new HashSet<string>(); var forms = new HashSet<string>();
        int maxCells = 0, sourceTouchdowns = 0, recycledParents = 0, weakBirthsRejected = 0;
        int deterioratingSamples = 0, scaledWindSamples = 0, supportedCells = 0, motionSamples = 0, lateNightWeakeningSamples = 0;
        int dryCompact = 0, moistBroad = 0, moistBirths = 0;
        bool birthsSupported = true, basesFollowLcl = true, positiveDepth = true, finiteMotion = true;
        bool motionAligned = true, smoothMotion = true, backgroundPresent = true, sourceCentersMatch = true;
        double maximumMotionChange = 0;
        int captureSeed = 0; double captureSeconds = 0; EnvironmentSceneState? wireEnvironment = null; AtmosphereSnapshot? wireAtmosphere = null;
        int maximumWireCells = -1;
        var populationTimer = Stopwatch.StartNew();
        for (int seed = 1; seed <= 96; seed++)
        {
            var campaign = Make(seed);
            var seen = new HashSet<string>(); var nightSeen = new HashSet<string>(); var recycledSeen = new HashSet<string>();
            var priorMotion = new Dictionary<string, Vector3f>();
            int dayOneBirths = 0;
            // 08:00 day one through 08:00 day three; broad seeds rather
            // than a pinned tornado seed exercise genuine opportunity failures.
            for (int second = 1; second <= 2400; second++)
            {
                campaign.Update(1);
                maxCells = Math.Max(maxCells, campaign.ActiveStormCount);
                var rendered = campaign.Current.ActiveStorms;
                if (rendered.Count > 0)
                {
                    backgroundPresent &= campaign.Current.Clouds is { Coverage: > 0 };
                    if (rendered.Count > maximumWireCells || rendered.Count == maximumWireCells && rendered.Sum(s => s.Tornadoes.Count) > (wireEnvironment?.ActiveStorms.Sum(s => s.Tornadoes.Count) ?? -1))
                    {
                        maximumWireCells = rendered.Count; wireEnvironment = campaign.Current; wireAtmosphere = campaign.Atmosphere;
                    }
                }
                foreach (var cell in campaign.ActiveCells)
                {
                    var source = cell.Simulation.Current.ActiveStorms.First(); string id = source.Id;
                    var visible = rendered.Single(storm => storm.Id == id);
                    float age = (float)(campaign.ElapsedSeconds - cell.BornSeconds);
                    sourceCentersMatch &= Distance(visible.Position, source.Position + cell.MotionOffset) < .001f;
                    finiteMotion &= float.IsFinite(cell.MotionVelocity.X) && float.IsFinite(cell.MotionVelocity.Z)
                        && Length(cell.MotionVelocity) is >= 5.9f and <= 28.1f;
                    basesFollowLcl &= Math.Abs(visible.CloudBaseMeters - Math.Clamp(cell.Atmosphere.LclMeters, 400, 2000)) < .01f;
                    positiveDepth &= visible.CloudTopMeters > visible.CloudBaseMeters && visible.Tornadoes.All(t => Math.Abs(t.HeightMeters - visible.CloudBaseMeters) < .01f);
                    if (seen.Add(id))
                    {
                        if (cell.BirthForecast.DayNumber == 1) dayOneBirths++;
                        behaviors.Add(cell.Simulation.BehaviorName); forms.Add(cell.Appearance.Name);
                        birthsSupported &= cell.BirthAtmosphere.CapeJkg >= 120 && cell.BirthAtmosphere.InitiationPotential01 >= .045f;
                        if (cell.BirthAtmosphere.DewPointCelsius < 15 && cell.Appearance.Name == "Compact towering") dryCompact++;
                        if (cell.BirthAtmosphere.DewPointCelsius > 21)
                        { moistBirths++; if (cell.Appearance.Name == "Broad precipitation") moistBroad++; }
                        births.Add(new { seed, cell.BirthForecast.DayNumber, campaign.Hour, id, cell.Region,
                            behavior = cell.Simulation.BehaviorName, form = cell.Appearance.Name,
                            diagnostics = cell.BirthAtmosphere, renderedBase = visible.CloudBaseMeters,
                            velocity = cell.MotionVelocity, developmentSeconds = cell.Appearance.DevelopmentWallSeconds });
                    }
                    if (priorMotion.TryGetValue(id, out var previous))
                    {
                        double change = Distance(previous, cell.MotionVelocity);
                        maximumMotionChange = Math.Max(maximumMotionChange, change);
                        smoothMotion &= change < 1.5;
                    }
                    priorMotion[id] = cell.MotionVelocity;
                    if (age > 20)
                    {
                        var motion = new Vector3f(cell.Atmosphere.StormMotion.X, 0, cell.Atmosphere.StormMotion.Z);
                        float cosine = (motion.X * cell.MotionVelocity.X + motion.Z * cell.MotionVelocity.Z)
                            / Math.Max(.001f, Length(motion) * Length(cell.MotionVelocity));
                        motionAligned &= cosine > .985f; motionSamples++;
                        // Published campaign velocity is actual SI motion, independent
                        // of the private source's accelerated simulation clock.
                        finiteMotion &= Distance(visible.Velocity, cell.MotionVelocity) < .001f;
                    }
                    if (cell.Atmosphere.CapeJkg < cell.BirthAtmosphere.CapeJkg * .8f
                        && cell.Atmosphere.InitiationPotential01 < cell.BirthAtmosphere.InitiationPotential01 * .9f)
                    {
                        deterioratingSamples++;
                        if (visible.UpdraftRadiusMeters < source.UpdraftRadiusMeters * .98f) supportedCells++;
                        if (campaign.Hour is >= 1 and < 6 && visible.UpdraftRadiusMeters < source.UpdraftRadiusMeters * .98f)
                            lateNightWeakeningSamples++;
                        var sourceTornado = source.Tornadoes.FirstOrDefault(); var visibleTornado = visible.Tornadoes.FirstOrDefault();
                        if (sourceTornado is not null && visibleTornado is not null
                            && (sourceTornado.SmoothedWindSpeed ?? sourceTornado.WindSpeed) > 10
                            && (visibleTornado.SmoothedWindSpeed ?? visibleTornado.WindSpeed)
                                < (sourceTornado.SmoothedWindSpeed ?? sourceTornado.WindSpeed) * .95f) scaledWindSamples++;
                    }
                    if (cell.Simulation.TouchdownCount > 1 && recycledSeen.Add(id)) recycledParents++;
                    if (source.Tornadoes.Any(t => t.TouchdownAmount >= .95f))
                    {
                        sourceTouchdowns++;
                        if (captureSeed == 0) { captureSeed = seed; captureSeconds = campaign.ElapsedSeconds; }
                        if (campaign.Hour is >= .25f and < 6 && nightSeen.Add(id + "/" + cell.Simulation.TornadoEpisodeNumber))
                            nightTouchdowns.Add(new { seed, campaign.DayNumber, campaign.Hour, campaign.ElapsedSeconds, id,
                                episode = cell.Simulation.TornadoEpisodeNumber, source = cell.Simulation.BehaviorName,
                                cell.Atmosphere.CapeJkg, cell.Atmosphere.CinJkg, cell.Atmosphere.Srh01KmM2S2 });
                    }
                }
            }
            if (StormChaseCampaignWeather.CreateForecast(seed, 1).Opportunities > 0 && dayOneBirths == 0) weakBirthsRejected++;
            if (seed % 16 == 0) Console.WriteLine($"Atmosphere population {seed}/96: {births.Count} births, {nightTouchdowns.Count} night episodes.");
        }
        populationTimer.Stop();
        Check("low-energy/capped opportunity days can fail to initiate", weakBirthsRejected > 0);
        Check("every actual birth has usable integrated energy and cap support", birthsSupported && births.Count > 20);
        Check("non-tornadic failed tornadic and cyclic parent behavior all occur", behaviors.SetEquals([
            "Non-tornadic thunderstorm", "Interrupted mesocyclone", "Tornadic supercell", "Cyclic supercell"]));
        Check("storm morphology varies among all four ensembles", forms.Count == 4);
        Check("moist environments favor broad precipitation morphology", moistBirths > 10 && moistBroad > moistBirths * .4);
        Check("multiple independent supported storms coexist", maxCells >= 2 && maxCells <= 4);
        Check("regional cloud layer survives naturally developing storms", backgroundPresent);
        Check("actual parent cloud and tornado height follows diagnosed LCL", basesFollowLcl);
        Check("diagnosed cloud heights preserve positive depth and attached funnels", positiveDepth);
        Check("offset integration moves complete parent and funnel together", sourceCentersMatch);
        Check("storm physical velocity is finite and in playable SI bounds", finiteMotion);
        Check("storm motion aligns with diagnosed Bunkers flow", motionAligned && motionSamples > 100);
        Check("environmental steering changes smoothly", smoothMotion);
        Check("atmospheric deterioration reduces actual updraft geometry", deterioratingSamples > 100 && supportedCells > 100);
        Check("atmospheric deterioration reduces actual tornado wind", scaledWindSamples > 0);
        Check("parent storms can recycle into another touchdown", recycledParents > 0);
        Check("naturally supported post-midnight tornadoes remain possible", nightTouchdowns.Count > 0);
        Check("late-night atmospheric deterioration weakens actual surviving parent updrafts", lateNightWeakeningSamples > 0);

        double target = captureSeconds + 24.5;
        var bulk = Make(captureSeed); bulk.Update(target);
        var split = Make(captureSeed);
        while (split.ElapsedSeconds + .05 <= target + 1e-8) split.Update(.05);
        if (split.ElapsedSeconds < target - 1e-8) split.Update(target - split.ElapsedSeconds);
        var resumed = Make(captureSeed, target);
        static bool Equivalent(StormChaseCampaignWeather left, StormChaseCampaignWeather right)
        {
            if (left.ActiveStormCount != right.ActiveStormCount || left.SpawnHistory.Count != right.SpawnHistory.Count) return false;
            foreach (var first in left.ActiveCells)
            {
                var second = right.ActiveCells.FirstOrDefault(cell => StormId(cell) == StormId(first));
                if (second is null || Distance(first.MotionOffset, second.MotionOffset) > .003f
                    || Distance(first.MotionVelocity, second.MotionVelocity) > .003f
                    || first.Atmosphere != second.Atmosphere || first.Simulation.CurrentPhase != second.Simulation.CurrentPhase
                    || Math.Abs(first.Simulation.SimulationSeconds - second.Simulation.SimulationSeconds) > 1e-5) return false;
            }
            return JsonSerializer.Serialize(left.Current, ProtocolCodec.JsonOptions) == JsonSerializer.Serialize(right.Current, ProtocolCodec.JsonOptions);
        }
        Check("20Hz simulation is independent of render update partition", Equivalent(bulk, split));
        Check("resume reconstructs storm motion offsets and complete environment", Equivalent(bulk, resumed));
        var pauseControls = new LiveStormController(["--demo"]);
        var paused = new StormChaseCampaignWeather(pauseControls, ["--campaign-seed", captureSeed.ToString(CultureInfo.InvariantCulture)], target);
        string beforePause = JsonSerializer.Serialize(paused.Current, ProtocolCodec.JsonOptions);
        pauseControls.HandleKey(0x50); paused.Update(3);
        Check("paused chase preserves atmosphere clock and physical storm position", paused.ElapsedSeconds == target
            && beforePause == JsonSerializer.Serialize(paused.Current, ProtocolCodec.JsonOptions));

        if (wireEnvironment is null || wireAtmosphere is null) throw new InvalidOperationException("Population produced no wire capture.");
        var states = wireEnvironment.ActiveStorms.Select((storm, index) => new StormState(index + 1,
            new(storm.Position.X, storm.Position.Y, storm.Position.Z), "active", 0, storm.Intensity,
            storm.AnvilRadiusMeters, 0, Length(storm.Velocity), null)).ToArray();
        var snapshot = new WorldSnapshot(wireAtmosphere.ServerTick, wireAtmosphere.CampaignSeconds, 73019,
            [], [], states, [], [], Environment: wireEnvironment, Atmosphere: wireAtmosphere);
        byte[] raw = JsonSerializer.SerializeToUtf8Bytes(snapshot, ProtocolCodec.JsonOptions);
        byte[] encoded = ProtocolCodec.Encode(MessageKind.WorldSnapshot, 42, snapshot);
        bool decoded = ProtocolCodec.TryDecode(encoded, out var message);
        var result = decoded ? message!.Get<WorldSnapshot>() : null;
        Check("authoritative atmosphere and visible environment round trip together", result?.Atmosphere?.Diagnostics == wireAtmosphere.Diagnostics
            && result.Atmosphere.Sounding.SequenceEqual(wireAtmosphere.Sounding)
            && JsonSerializer.Serialize(result.Environment, ProtocolCodec.JsonOptions) == JsonSerializer.Serialize(wireEnvironment, ProtocolCodec.JsonOptions));
        Check("97-level sounding plus actual concurrent storms fit wire bounds", raw.Length < ProtocolCodec.MaxPayloadBytes && encoded.Length < 32 * 1024);
        bool Rejected(WorldSnapshot invalid)
        { try { ProtocolCodec.Encode(MessageKind.WorldSnapshot, 43, invalid); return false; } catch (ArgumentException) { return true; } }
        Check("protocol rejects nonfinite atmosphere diagnostics", Rejected(snapshot with
            { Atmosphere = wireAtmosphere with { Diagnostics = wireAtmosphere.Diagnostics with { CapeJkg = float.NaN } } }));
        Check("protocol rejects oversized sounding collections", Rejected(snapshot with
            { Atmosphere = wireAtmosphere with { Sounding = Enumerable.Repeat(wireAtmosphere.Sounding[0], 129).ToImmutableArray() } }));
        Check("protocol rejects out-of-range diurnal metadata", Rejected(snapshot with
            { Atmosphere = wireAtmosphere with { Hour = 24 } }));
        var costs = new List<double>();
        for (int warmup = 0; warmup < 12; warmup++) StormChaseAtmosphere.Sample(73019, 2, 14, birthplace);
        for (int i = 0; i < 192; i++)
        {
            var timer = Stopwatch.StartNew(); StormChaseAtmosphere.Sample(i + 1, 2, i % 24, birthplace); timer.Stop();
            costs.Add(timer.Elapsed.TotalMilliseconds);
        }
        costs.Sort(); double p95 = costs[(int)(costs.Count * .95)];
        Check("coarse sample p95 leaves ample room within 50ms physics tick", p95 < 5);
        File.WriteAllText(Path.Combine(output, "atmosphere-integration-check.json"), JsonSerializer.Serialize(new
        {
            allChecksPassed = failures == 0, checkCount = checks.Count, checks,
            population = new { seeds = 96, wallSecondsPerSeed = 2400, elapsedMilliseconds = populationTimer.Elapsed.TotalMilliseconds,
                births = births.Count, behaviors, forms, maxCells, sourceTouchdowns, recycledParents, weakBirthsRejected,
                moistBirths, moistBroad, dryCompact, deterioratingSamples, supportedCells, scaledWindSamples, lateNightWeakeningSamples,
                nightTouchdowns, maximumMotionChange },
            motionReplay = new { seed = captureSeed, targetSeconds = target, activeCells = bulk.ActiveStormCount },
            network = new { rawBytes = raw.Length, encodedBytes = encoded.Length, concurrentStorms = wireEnvironment.ActiveStorms.Count,
                profileLevels = wireAtmosphere.Sounding.Length, sampleP95Milliseconds = p95 }, births
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Atmosphere integration: {checks.Count - failures}/{checks.Count}; {births.Count} natural births; {nightTouchdowns.Count} post-midnight tornado episodes; {encoded.Length} bytes largest-cell wire capture.");
        return failures == 0 ? 0 : 1;
    }
}
