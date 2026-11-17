using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Engine.Environment;
using Engine.Rendering.Assets;
using Engine.Rendering.Backend;
using Engine.Rendering.Configuration;
using Engine.Rendering.Pipeline;
using Engine.Rendering.Scene;
using Engine.UniStorm;
using Shared.Contracts.Rendering;
using Shared.Contracts.World;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

internal static partial class Showcase
{
    /// <summary>Records actual seeded source histories; no weather snapshots are manufactured by this probe.</summary>
    private static int VerifyStormVariation(string output)
    {
        var watch = Stopwatch.StartNew();
        var checks = new List<string>();
        void Check(bool valid, string description)
        {
            if (!valid) throw new InvalidOperationException(description);
            checks.Add(description);
        }
        var histories = Enumerable.Range(1, 32).Select(seed => TraceStormVariation(
            LiveStormController.CreateChaseOptions(3, seed, variedLifecycle: true))).ToArray();
        Check(histories.All(h => h.Complete), "All 32 seeded parent storms complete their actual source lifecycle.");
        Check(histories.All(h => h.ValidFiniteState), "Every sampled source wind, width, position and smoothing value is finite and bounded.");
        Check(histories.All(h => h.OneParent && h.MaximumSimultaneousTornadoes <= 1),
            "Every lifecycle retains one parent storm and has at most one active tornado at a time.");
        Check(histories.All(h => h.AttachedChildren), "Episode offsets, velocities and parent IDs remain attached through the native source state.");
        Check(histories.Any(h => h.Episodes.Count == 0 && h.Touchdowns == 0), "The seeded ensemble includes a thunderstorm that never forms a tornado.");
        Check(histories.Any(h => h.Episodes.Count > 0 && h.Touchdowns == 0), "The ensemble includes an interrupted funnel without a touchdown.");
        Check(histories.Any(h => h.Touchdowns == 1), "The ensemble includes a single-tornado parent storm.");
        Check(histories.Any(h => h.Touchdowns >= 2 && h.RecyclingCloudSamples > 0),
            "The ensemble includes multiple genuine tornado touchdowns separated by a cloud-only recycling gap.");
        Check(histories.Where(h => h.RecyclingSamples > 0).All(h => h.RecyclingSamples == h.RecyclingCloudSamples),
            "Every recycling sample preserves parent cloud density and precipitation while containing no tornado.");
        Check(histories.Select(h => h.DurationSeconds).Distinct().Count() > 20,
            "Seeded parent storms have varied durations rather than one shared timetable.");
        Check(histories.Any(h => h.Events.Any(e => e.Phase == "Surging")) && histories.Any(h => h.Events.Any(e => e.Phase == "Recovering")),
            "Actual histories include wind surges and parent-storm recovery.");
        var cyclic = histories.Single(h => h.Seed == 13);
        Check(cyclic.Touchdowns >= 2 && cyclic.CaptureStates.Count == 4,
            "Seed13 yields first mature tornado, recycling clouds, second mature tornado and post-tornado thunderstorm from the same parent.");
        Check(cyclic.Episodes.Select(e => e.MaximumWindMph).Distinct().Count() > 1
            && cyclic.Episodes.Select(e => e.MaximumWidthMeters).Distinct().Count() > 1
            && cyclic.Episodes.Select(e => e.LocalOffset).Distinct().Count() > 1,
            "Successive seed13 funnels have different actual winds, widths and source attachment offsets.");
        Check(cyclic.Episodes.Select(e=>e.Id).Distinct().Count()==cyclic.Episodes.Count,
            "Recycled tornado episodes receive separate identities under one persistent parent ID.");

        var builder = new DefaultRenderFrameBuilder();
        var camera = new CameraState(new(-300, 5, 160), new(0, .1f, 1), 58, .1f, 16000);
        foreach (var shot in cyclic.CaptureStates)
        {
            var frame = builder.Build(shot.Tick, DateTimeOffset.UnixEpoch.AddSeconds(shot.Seconds), new(1280,720),
                new RenderScene { Camera = camera, Environment = shot.State, AnimationTimeSeconds = 42 });
            var source = shot.State.ActiveStorms.Single();
            var mapped = frame.Weather!.ActiveStorms.Single();
            Check(mapped.Id == source.Id && mapped.Position == source.Position && mapped.SmoothedStage == source.SmoothedStage
                && mapped.SmoothedWindSpeed == source.SmoothedWindSpeed && mapped.Tornadoes.Count == source.Tornadoes.Count,
                "The native frame builder preserves the actual " + shot.Name + " parent, smoothing and tornado count.");
        }
        var gap = cyclic.CaptureStates.Single(s => s.Name == "variation-02-recycling-clouds");
        Check(gap.State.ActiveStorms.Single().Tornadoes.Count == 0 && gap.State.Precipitation > .05f,
            "Recycling submits a real rainy parent storm with an empty child list, so no old funnel is synthesized.");

        var exactOptions = LiveStormController.CreateChaseOptions(3,13,variedLifecycle:true);
        var oneAdvance = new PmWeatherSupercellSimulation(exactOptions);
        var chunked = new PmWeatherSupercellSimulation(exactOptions);
        oneAdvance.Advance(1900);
        for (int i = 0; i < 3800; i++) chunked.Advance(.5);
        var a = oneAdvance.Current.ActiveStorms.FirstOrDefault();
        var b = chunked.Current.ActiveStorms.FirstOrDefault();
        Check(oneAdvance.TickCount == chunked.TickCount && oneAdvance.CurrentPhase == chunked.CurrentPhase
            && oneAdvance.TornadoEpisodeNumber == chunked.TornadoEpisodeNumber && oneAdvance.TouchdownCount == chunked.TouchdownCount
            && a?.Position == b?.Position && a?.WindSpeed == b?.WindSpeed && a?.WidthMeters == b?.WidthMeters
            && a?.SmoothedStage == b?.SmoothedStage && a?.SmoothedWindSpeed == b?.SmoothedWindSpeed
            && a?.SmoothedWidthMeters == b?.SmoothedWidthMeters && a?.SpinRadians == b?.SpinRadians,
            "One large advance and 3800 partitioned advances produce identical native ticks, episodes and source smoothing.");

        var live = new LiveStormController(["--demo", "--storm-seed", "13"]);
        Check(live.VariedLifecycleEnabled && live.Simulation.IsVariedLifecycle,
            "The normal chase demo enables varied source lifecycle behavior by default.");
        var originalSimulation = live.Simulation;
        long worldVersion = live.WorldResetVersion;
        for (int phase = 0; phase < 50 && live.Phase != "Recycling" && !live.Simulation.IsComplete; phase++)
            live.HandleKey(0x47);
        Check(live.Phase == "Recycling" && ReferenceEquals(originalSimulation,live.Simulation)
            && live.WorldResetVersion == worldVersion && live.Simulation.Current.ActiveStorms.Count == 1,
            "G advances the same live parent into recycling without restarting the simulation or world.");
        string recyclingParent = live.Simulation.Current.ActiveStorms.Single().Id;
        live.HandleKey(0x47);
        Check(ReferenceEquals(originalSimulation,live.Simulation) && live.WorldResetVersion == worldVersion
            && live.Simulation.Current.ActiveStorms.Single().Id == recyclingParent && live.Phase == live.Simulation.CurrentPhase,
            "Leaving a recycling phase keeps the same source parent and forwards its diagnostic phase.");
        live.HandleKey(0x50);
        int pausedTick = live.Simulation.TickCount;
        live.Update(2);
        Check(live.Paused && live.Simulation.TickCount == pausedTick, "P freezes source ticks during a varied lifecycle.");
        live.HandleKey(0x50);live.HandleKey(0xDB);
        Check(!live.Paused && live.PlaybackSpeed == 10, "Pause resume and [ retain normal varied-lifecycle playback controls.");
        live.HandleKey(0xDD);
        Check(live.PlaybackSpeed == 20, "] restores20x source playback without resetting the parent.");
        var previousOrigin = live.Simulation.Current.ActiveStorms.Single().Position;
        live.HandleKey(0x52);
        Check(!ReferenceEquals(originalSimulation,live.Simulation) && live.WorldResetVersion == worldVersion + 1
            && live.Simulation.TickCount == 0 && live.Simulation.Current.ActiveStorms.Single().Id != recyclingParent
            && live.Simulation.Current.ActiveStorms.Single().Position != previousOrigin,
            "R deliberately chooses a fresh seeded parent/route and restores the world; recycling does neither.");
        worldVersion = live.WorldResetVersion;
        string previousParent = live.Simulation.Current.ActiveStorms.Single().Id;
        live.Restart(newSeed:true);
        Check(live.WorldResetVersion == worldVersion && live.Simulation.Current.ActiveStorms.Single().Id != previousParent,
            "Scheduling another seeded storm preserves damage and deployed instruments.");

        Directory.CreateDirectory(output);
        string reportPath = Path.Combine(output,"storm-variation-cpu.json");
        File.WriteAllText(reportPath,JsonSerializer.Serialize(new
        {
            Schema = 1, Passed = checks.Count, Checks = checks, SourceTickRate = 20, SamplingSeconds = .25,
            EnsembleSeedCount = histories.Length, ElapsedMilliseconds = watch.Elapsed.TotalMilliseconds,
            Outcomes = histories.GroupBy(h=>h.BehaviorName).Select(g=>new { Behavior=g.Key,Count=g.Count() }),
            Timelines = histories.Select(VariationTimelineReport)
        },JsonOptions));
        Console.WriteLine($"Varied storm CPU checks passed: {checks.Count}; {histories.Length} seeded histories; seed13 {cyclic.Touchdowns} touchdowns; {reportPath}");
        return 0;
    }

    private static VariationHistory TraceStormVariation(PmWeatherSupercellOptions options)
    {
        var simulation = new PmWeatherSupercellSimulation(options);
        var history = new VariationHistory { Seed=options.Seed, ParentId=options.Id, BehaviorName=simulation.BehaviorName,
            PlannedEpisodes=simulation.PlannedEpisodeCount, FirstActivitySeconds=simulation.FirstActivityTimeSeconds };
        var episodes = new Dictionary<int,VariationEpisode>();
        var matureStates = new Dictionary<int,VariationCaptureState>();
        VariationCaptureState? gap = null, post = null;
        string previousPhase = "";
        double phaseStart = 0;
        for (int sample=0;sample<=60_000;sample++)
        {
            if (sample>0) simulation.Advance(.25);
            var state=simulation.Current;
            var storm=state.ActiveStorms.FirstOrDefault();
            string phase=simulation.CurrentPhase;
            if (phase!=previousPhase)
            {
                phaseStart=simulation.SimulationSeconds;
                history.Events.Add(new(simulation.SimulationSeconds,simulation.TickCount,phase,
                    simulation.TornadoEpisodeNumber,simulation.TouchdownCount,storm?.WindSpeed??0,
                    storm?.SmoothedWidthMeters??0,storm?.Tornadoes.Count??0,storm?.SmoothedStage??0));
                previousPhase=phase;
            }
            if (storm is not null)
            {
                history.OneParent &= state.ActiveStorms.Count==1 && storm.Id==options.Id;
                history.MaximumSimultaneousTornadoes=Math.Max(history.MaximumSimultaneousTornadoes,storm.Tornadoes.Count);
                history.ValidFiniteState &= float.IsFinite(storm.Position.X) && float.IsFinite(storm.Position.Y)
                    && float.IsFinite(storm.Position.Z) && float.IsFinite(storm.SmoothedStage??0)
                    && float.IsFinite(storm.SmoothedWindSpeed??0) && float.IsFinite(storm.SmoothedWidthMeters??0)
                    && storm.WindSpeed is >=0 and <=400 && storm.WidthMeters is >=0 and <=1800;
                foreach(var tornado in storm.Tornadoes)
                {
                    history.AttachedChildren &= tornado.ParentStormId==storm.Id
                        && tornado.Position==storm.Position+tornado.LocalOffset && tornado.Velocity==storm.Velocity;
                    int number=simulation.TornadoEpisodeNumber;
                    if(!episodes.TryGetValue(number,out var episode))
                    {
                        episode=new VariationEpisode { Number=number,Id=tornado.Id,LocalOffset=tornado.LocalOffset,
                            FirstSeconds=simulation.SimulationSeconds,LastSeconds=simulation.SimulationSeconds };
                        episodes.Add(number,episode);
                    }
                    episode.LastSeconds=simulation.SimulationSeconds;
                    episode.MaximumWindMph=Math.Max(episode.MaximumWindMph,tornado.SmoothedWindSpeed??tornado.WindSpeed);
                    episode.MaximumWidthMeters=Math.Max(episode.MaximumWidthMeters,tornado.SmoothedWidthMeters??tornado.WidthMeters);
                    episode.MaximumTouchdownAmount=Math.Max(episode.MaximumTouchdownAmount,tornado.TouchdownAmount);
                    episode.MaximumCondensation=Math.Max(episode.MaximumCondensation,tornado.CondensationAmount);
                    if(phase=="Mature" && tornado.TouchdownAmount>=.999f
                        && (!matureStates.TryGetValue(number,out var previous)
                            || (previous.State.ActiveStorms.Single().Tornadoes.Single().SmoothedWindSpeed??0)<(tornado.SmoothedWindSpeed??0)))
                        matureStates[number]=new("",state,simulation.SimulationSeconds,simulation.TickCount,phase,number,simulation.TouchdownCount);
                }
                if(phase=="Recycling")
                {
                    history.RecyclingSamples++;
                    if(storm.Tornadoes.Count==0 && state.Precipitation>.05f && state.Clouds.VolumetricDensity>.01f && storm.Intensity>.05f)
                        history.RecyclingCloudSamples++;
                    if(gap is null && simulation.SimulationSeconds-phaseStart>=30 && storm.Tornadoes.Count==0)
                        gap=new("variation-02-recycling-clouds",state,simulation.SimulationSeconds,simulation.TickCount,
                            phase,simulation.TornadoEpisodeNumber,simulation.TouchdownCount);
                }
                if(post is null && phase=="Thunderstorm" && simulation.TornadoEpisodeNumber==simulation.PlannedEpisodeCount
                    && simulation.PlannedEpisodeCount>0 && simulation.SimulationSeconds-phaseStart>=30 && storm.Tornadoes.Count==0)
                    post=new("variation-04-post-tornado-thunderstorm",state,simulation.SimulationSeconds,simulation.TickCount,
                        phase,simulation.TornadoEpisodeNumber,simulation.TouchdownCount);
            }
            if(simulation.IsComplete)break;
        }
        history.Complete=simulation.IsComplete;
        history.DurationSeconds=simulation.SimulationSeconds;
        history.TickCount=simulation.TickCount;
        history.Touchdowns=simulation.TouchdownCount;
        history.Episodes.AddRange(episodes.Values.OrderBy(e=>e.Number));
        var mature=matureStates.OrderBy(pair=>pair.Key).Take(2).ToArray();
        if(mature.Length>0)history.CaptureStates.Add(mature[0].Value with {Name="variation-01-first-mature"});
        if(gap is not null)history.CaptureStates.Add(gap);
        if(mature.Length>1)history.CaptureStates.Add(mature[1].Value with {Name="variation-03-second-mature"});
        if(post is not null)history.CaptureStates.Add(post);
        history.CaptureStates.Sort((a,b)=>a.Seconds.CompareTo(b.Seconds));
        return history;
    }

    private static object VariationTimelineReport(VariationHistory history)=>new
    {
        history.Seed,history.ParentId,history.BehaviorName,history.PlannedEpisodes,history.FirstActivitySeconds,
        history.Complete,history.DurationSeconds,history.TickCount,history.Touchdowns,history.MaximumSimultaneousTornadoes,
        RecyclingCloudSeconds=history.RecyclingCloudSamples*.25,history.Events,history.Episodes,
        CapturePoints=history.CaptureStates.Select(s=>new{s.Name,s.Seconds,s.Tick,s.Phase,s.Episode,s.Touchdowns,
            Parent=s.State.ActiveStorms.Single().Id,ChildIds=s.State.ActiveStorms.Single().Tornadoes.Select(t=>t.Id)})
    };

    /// <summary>Four real source snapshots of a persistent parent, including its empty-child recycling interval.</summary>
    private static async Task<int> RunStormVariationCaptureAsync(RendererOptions options,NativeSurface surface,
        MutableRenderAssetCatalog catalog,CaptureLogger logger,RenderGpuDebugValidation validation,
        bool particles,string qualityName,string output,string staging,int width,int height,string[] args)
    {
        var sourceOptions=LiveStormController.CreateChaseOptions(3,13,variedLifecycle:true);
        var trace=TraceStormVariation(sourceOptions);
        if(!trace.Complete||trace.Touchdowns<2||trace.CaptureStates.Count!=4)
            throw new InvalidOperationException("The actual seed13 varied source did not yield the four cyclic-storm capture states.");
        using var session=new StormChaseSession(catalog,logger,OptionalValue(args,"--avatar-catalog"));
        await using var backend=new Dx12RenderBackend(options,surface,catalog,logger);
        await backend.InitializeAsync(CancellationToken.None);
        validation.CollectErrors(backend,"varied storm capture initialization");
        long number=0,historyVersion=40000;
        var reports=new List<object>();
        var images=new Dictionary<string,byte[]>();
        foreach(var shot in trace.CaptureStates)
        {
            var source=session.GroundStorms(shot.State);
            var parent=source.ActiveStorms.Single();
            if(parent.Id!=sourceOptions.Id)throw new InvalidOperationException("A cyclic capture replaced its parent storm.");
            var target=parent.Tornadoes.FirstOrDefault()?.Position??parent.Position;
            var observer=new Vector3f(target.X-360,session.World.HeightAt(target.X-360,target.Z-820)+28,target.Z-820);
            var camera=ChaseCaptureCamera(observer,new(target.X,parent.Position.Y+260,target.Z));
            var local=new UniStormSpatialWeatherController(UniStormWeatherCatalog.LoadDefault(),13,
                new UniStormOptions{AutomaticWeatherEnabled=false,AdvanceInternalClock=false,TransitionSeconds=2});
            EnvironmentSceneState environment=source;
            for(int tick=0;tick<200;tick++)environment=local.Update(new(.05f,42,2026,150,15,22,observer),source);
            if(!ReferenceEquals(environment.Storms,source.Storms)||!ReferenceEquals(environment.Clouds,source.Clouds))
                throw new InvalidOperationException("Observer weather replaced the varied parent cloud or child states.");
            var frame=session.BuildFrame(number,42,0,environment,camera,width,height,particles,historyVersion,
                snapshot:local.Snapshot,advancePhysics:false);
            var mapped=frame.Weather!.ActiveStorms.Single();
            if(mapped.Id!=parent.Id||mapped.Tornadoes.Count!=parent.Tornadoes.Count)
                throw new InvalidOperationException("Actual render mapping lost the cyclic parent or tornado count.");
            if(shot.Name.Contains("clouds",StringComparison.Ordinal)||shot.Name.Contains("thunderstorm",StringComparison.Ordinal))
            {
                if(mapped.Tornadoes.Count!=0||frame.Weather.TornadoFunnelAmount>.001f||parent.PrecipitationCoreAmount<=.05f)
                    throw new InvalidOperationException("Cloud-only source phase submitted a funnel or lost its precipitation.");
            }
            int residentFrames=0,warmups=0;
            for(;warmups<128;warmups++)
            {
                surface.PumpMessages();
                await backend.RenderAsync(frame with{FrameNumber=number++},CancellationToken.None);
                residentFrames=backend.LastFrameStatistics.RequiredResourcesResident?residentFrames+1:0;
                if(warmups>=23&&residentFrames>=16)break;
            }
            // Align each exported frame to the same TAA jitter phase as well as resetting cloud history.
            while(number%8!=7)
            {
                surface.PumpMessages();
                await backend.RenderAsync(frame with{FrameNumber=number++},CancellationToken.None);
            }
            ExportCompleted.SetValue(backend,false);
            var watch=Stopwatch.StartNew();
            var captured=frame with{FrameNumber=number++};
            await backend.RenderAsync(captured,CancellationToken.None);
            validation.CollectErrors(backend,shot.Name,waitForCompletion:validation.Enabled);
            var stats=backend.LastFrameStatistics;
            if(!(bool)ExportCompleted.GetValue(backend)!||!stats.RequiredResourcesResident
                ||stats.MissingCatalogResourceCount!=0||stats.InvalidSkinPoseCount!=0||stats.OpaqueBatchCount<=0)
                throw new InvalidOperationException("Incomplete native varied-storm capture "+shot.Name+": "+stats);
            string path=Path.Combine(output,shot.Name+".png");
            File.Copy(staging,path,true);
            using var image=Image.Load<Rgba32>(path);
            var pixels=new byte[width*height*4];image.CopyPixelDataTo(pixels);
            double mean=MeanLuminance(pixels),variation=LuminanceVariation(pixels,mean);
            if(image.Width!=width||image.Height!=height||mean<.001||variation<.000001)
                throw new InvalidDataException("Blank or invalid native varied-storm capture: "+shot.Name);
            images[shot.Name]=pixels;
            reports.Add(new{shot.Name,Path=path,SourceSeconds=shot.Seconds,SourceTick=shot.Tick,shot.Phase,shot.Episode,
                shot.Touchdowns,ParentId=parent.Id,TornadoIds=parent.Tornadoes.Select(t=>t.Id),Camera=camera,
                captured.FrameNumber,captured.AnimationTimeSeconds,WarmupFrames=warmups+1,
                CaptureMilliseconds=watch.Elapsed.TotalMilliseconds,Weather=captured.Weather,Sky=captured.Sky,
                MeanLuminance=mean,LuminanceVariation=variation,Statistics=stats,GpuTiming=backend.LastGpuTiming,
                LocalWeather=local.LocalWeatherName,local.StormInfluence,
                Sha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))});
            Console.WriteLine($"Varied native capture {shot.Name}: parent={parent.Id}, episode={shot.Episode}, children={parent.Tornadoes.Count}; {path}");
            historyVersion++;
        }
        var differences=trace.CaptureStates.Zip(trace.CaptureStates.Skip(1),(a,b)=>new
        {From=a.Name,To=b.Name,MeanAbsoluteRgbDifference=PixelDifference(images[a.Name],images[b.Name])}).ToArray();
        if(differences.Any(d=>d.MeanAbsoluteRgbDifference<=.00001))
            throw new InvalidDataException("Consecutive real cyclic-storm source states rendered identically.");
        validation.CollectErrors(backend,"varied storm final drain",waitForCompletion:validation.Enabled);
        string manifestPath=Path.Combine(output,"storm-variation-native.json");
        File.WriteAllText(manifestPath,JsonSerializer.Serialize(new
        {
            Schema=1,NativeGpu=true,Backend=backend.Info,Hardware=Hardware(backend),Width=width,Height=height,
            WeatherQualityName=qualityName,options.WeatherRenderScale,options.WeatherQuality,
            EngineAssemblySha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(Dx12RenderBackend).Assembly.Location))),
            SourceOptions=sourceOptions,SourceTimeline=VariationTimelineReport(trace),Captures=reports,
            ConsecutiveDifferences=differences,Warnings=logger.Warnings,DebugValidation=validation.Snapshot
        },JsonOptions));
        Console.WriteLine("Four actual cyclic-parent native captures passed: "+manifestPath);
        return 0;
    }

    private sealed class VariationHistory
    {
        public int Seed {get;init;}
        public string ParentId {get;init;}="";
        public string BehaviorName {get;init;}="";
        public int PlannedEpisodes {get;init;}
        public double FirstActivitySeconds {get;init;}
        public bool Complete {get;set;}
        public double DurationSeconds {get;set;}
        public int TickCount {get;set;}
        public int Touchdowns {get;set;}
        public bool OneParent {get;set;}=true;
        public bool AttachedChildren {get;set;}=true;
        public bool ValidFiniteState {get;set;}=true;
        public int MaximumSimultaneousTornadoes {get;set;}
        public int RecyclingSamples {get;set;}
        public int RecyclingCloudSamples {get;set;}
        public List<VariationEvent> Events {get;}=[];
        public List<VariationEpisode> Episodes {get;}=[];
        public List<VariationCaptureState> CaptureStates {get;}=[];
    }
    private sealed class VariationEpisode
    {
        public int Number {get;init;}
        public string Id {get;init;}="";
        public Vector3f LocalOffset {get;init;}
        public double FirstSeconds {get;init;}
        public double LastSeconds {get;set;}
        public float MaximumWindMph {get;set;}
        public float MaximumWidthMeters {get;set;}
        public float MaximumTouchdownAmount {get;set;}
        public float MaximumCondensation {get;set;}
    }
    private sealed record VariationEvent(double Seconds,int Tick,string Phase,int Episode,int Touchdowns,
        int WindMph,float WidthMeters,int ActiveTornadoes,float SmoothedStage);
    private sealed record VariationCaptureState(string Name,EnvironmentSceneState State,double Seconds,
        int Tick,string Phase,int Episode,int Touchdowns);
}
