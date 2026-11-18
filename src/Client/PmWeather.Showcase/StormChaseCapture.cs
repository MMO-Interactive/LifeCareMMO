using System.Diagnostics;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using Engine.Environment;
using Engine.Rendering.Assets;
using Engine.Rendering.Backend;
using Engine.Rendering.Configuration;
using Engine.UniStorm;
using Shared.Contracts.Rendering;
using Shared.Contracts.World;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

internal static partial class Showcase
{
    /// <summary>Three-quarter view of a Cedar Junction home's porch side (model front is local +Z).</summary>
    private static CameraState BuildingCamera(StormChaseSession session)
    {
        var house=session.World.Buildings.First(b=>b.SettlementId=="cedar"&&b.Kind=="house"&&MathF.Abs(b.YawDegrees)<1);
        var p=house.Position;
        return ChaseCaptureCamera(new(p.X+13,p.Y+3.2f,p.Z+20),new(p.X,p.Y+3,p.Z+2));
    }

    /// <summary>
    /// Residents at 17:30 on a Wednesday (day 3) in Meridian: picks the busiest sidewalk spot, builds those people's
    /// own genetic avatars first (synchronously), and looks at them from 22 m away.
    /// </summary>
    private static CameraState NpcStreetCamera(StormChaseSession session)
    {
        System.Environment.SetEnvironmentVariable("MMOI_STORM_CHASE_NPCS","1");
        session.CampaignDay=3;session.CampaignHour=17.5f;
        session.EnableNpcs(73019,3,17.5f,null);
        var npcs=session.Npcs??throw new InvalidOperationException("NPCs were not enabled for the capture.");
        var now=new StormChase.Npc.NpcTime(3,17.5);
        var best=(Count:0,Center:Vector2.Zero);
        for(float x=5000;x<=7400;x+=40)for(float z=6200;z<=8200;z+=40)
        {
            int count=npcs.Simulation.AgentsNear(new(x,z),35,now,40).Count;
            if(count>best.Count)best=(count,new(x,z));
        }
        var agents=npcs.Simulation.AgentsNear(best.Center,35,now,StormChaseNpcLayer.MaximumAvatars);
        Console.WriteLine($"NPC capture: {agents.Count} residents near {best.Center}; building their avatars.");
        npcs.BuildNow(agents.Select(a=>a.ResidentId));
        var centroid=agents.Aggregate(Vector2.Zero,(sum,a)=>sum+a.Position)/Math.Max(1,agents.Count);
        Vector3f At(Vector2 p,float lift)=>new(p.X,session.World.HeightAt(p.X,p.Y)+lift,p.Y);
        // The eye position (ring around the group) that sees the most residents past the buildings.
        var eye=centroid+new Vector2(0,-14);int seen=-1;
        for(int ring=0;ring<3;ring++)for(int k=0;k<16;k++)
        {
            float angle=k*MathF.Tau/16,distance=10+ring*6;
            var candidate=centroid+new Vector2(MathF.Cos(angle),MathF.Sin(angle))*distance;
            if(!session.World.IsLineOfSightClear(At(candidate,1.75f),At(candidate,.2f)))continue;   // not inside a building
            int visible=agents.Count(a=>session.World.IsLineOfSightClear(At(candidate,1.75f),At(a.Position,1.2f)));
            if(visible>seen){seen=visible;eye=candidate;}
        }
        Console.WriteLine($"NPC capture: camera sees {seen}/{agents.Count} residents.");
        return ChaseCaptureCamera(At(eye,1.75f),At(centroid,1.0f));
    }

    private static CameraState CoastCamera(StormChaseSession session)
    {
        var (ex,ez)=StormChaseCoast.Point(-3060,-250);var (tx,tz)=StormChaseCoast.Point(-3260,180);
        return ChaseCaptureCamera(new(ex,session.World.HeightAt(ex,ez)+7,ez),new(tx,StormChaseCoast.SeaLevel+2,tz));
    }

    private static async Task<int> RunStormChaseCaptureAsync(RendererOptions options, NativeSurface surface,
        MutableRenderAssetCatalog catalog, CaptureLogger logger, RenderGpuDebugValidation validation,
        bool particles, string qualityName, string output, string staging, int width, int height, string[] args)
    {
        // Register every world, Crest, UMA and particle asset before device/pipeline initialization.
        using var session = new StormChaseSession(catalog, logger, OptionalValue(args,"--avatar-catalog"));
        using var hud = new StormChaseHud(surface.WindowHandle);
        var simulation = new PmWeatherSupercellSimulation(LiveStormController.CreateOptions(3,13));
        for(int tick=0;tick<100_000;tick++)
        {
            if(simulation.Current.ActiveStorms.FirstOrDefault() is {Stage:3,IsDying:true,TicksSinceDying:>=600})break;
            simulation.Advance(.05);
        }
        if(simulation.Current.ActiveStorms.FirstOrDefault() is not {Stage:3,IsDying:true,WindSpeed:155})
            throw new InvalidOperationException("The genuine severe source lifecycle did not reach its peak hold.");
        var mature = session.GroundStorms(simulation.Current);
        double matureSimulationSeconds=simulation.SimulationSeconds;
        int matureTickCount=simulation.TickCount;
        for(int tick=0;tick<100_000&&!simulation.IsComplete;tick++)simulation.Advance(.05);
        if(!simulation.IsComplete)throw new InvalidOperationException("The native source lifecycle did not finish its clear state.");
        var clear = simulation.Current;
        if(session.Destruction.NativeProvider.IsReducedFallback)
            throw new InvalidOperationException("Combined chase captures require the real native PhysX provider.");
        var movingSimulation = ChasePeakSimulation(3,13);
        var movingA = session.GroundStorms(movingSimulation.Current);
        movingSimulation.Advance(50);
        var movingB = session.GroundStorms(movingSimulation.Current);
        var electrical = session.GroundStorms(ChasePeakSimulation(3,14).Current);
        string chaseFilter=Value(args,"--chase-scene","all");
        var lotShowcase = chaseFilter is "all" or "lot-modules" or "lot-round-room" ? StormChaseLotShowcase.Create(session.World,catalog) : default;

        await using var backend = new Dx12RenderBackend(options,surface,catalog,logger);
        await backend.InitializeAsync(CancellationToken.None);
        validation.CollectErrors(backend,"combined chase initialization");
        long number=0,history=20000;
        var reports=new List<object>();
        var pixelsByName=new Dictionary<string,byte[]>();
        string filter=Value(args,"--chase-scene","all");
        string[] names=["clear-landscape","severe-road","crest-shore","crest-underwater","dressed-avatar",
            "structures-intact","physx-airborne","physx-settled-clear","sunset-storm","lightning-storm","night-torch",
            "moving-storm-a","moving-storm-b","power-flash","core-punch","probe-lineup","chase-truck","emergency-lineup",
            "gulf-coast","town-street","town-street-night","building-closeup","road-junction","highway-edge","npc-street","lot-modules","lot-round-room"];
        if(filter!="all"&&!names.Contains(filter,StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException("Unknown --chase-scene: "+filter+". Use "+string.Join(", ",names)+" or all.");
        foreach(string name in names)
        {
            if(filter!="all"&&!string.Equals(filter,name,StringComparison.OrdinalIgnoreCase))continue;
            session.Reset();session.SetLighting(name is "sunset-storm" or "emergency-lineup"?1:name is "night-torch" or "power-flash" or "town-street-night"?2:0);
            session.Flashlight=name=="night-torch";
            session.ThirdPerson=false;
            bool stormView=name is "severe-road" or "physx-airborne" or "sunset-storm" or "lightning-storm"
                or "moving-storm-a" or "moving-storm-b" or "power-flash" or "core-punch" or "chase-truck";
            var source=name switch { "moving-storm-a"=>movingA, "moving-storm-b"=>movingB, "power-flash"=>electrical, _=>stormView?mature:clear };
            var parent=mature.ActiveStorms.Single();
            CameraState? npcCamera=name=="npc-street"?NpcStreetCamera(session):null;
            var camera=npcCamera is not null?npcCamera:name switch
            {
                "gulf-coast"=>CoastCamera(session),
                // The capture-only showcase lot: modules, a gable and a hip roof, and a true round room with a nurses' station.
                "lot-modules"=>ChaseCaptureCamera(lotShowcase.HouseCentre+new Vector3f(-6,14,-30),lotShowcase.HouseCentre+new Vector3f(12,1,8)),
                "lot-round-room"=>ChaseCaptureCamera(lotShowcase.RoundRoomCentre+new Vector3f(-3.5f,8.5f,-9.5f),lotShowcase.RoundRoomCentre+new Vector3f(.5f,.6f,1)),
                // Meridian downtown: an intersection with stop signs, street-name blades and street lights.
                "town-street" or "town-street-night"=>ChaseCaptureCamera(new(5784,session.World.HeightAt(5784,6915)+1.8f,6915),new(5792,session.World.HeightAt(5792,6960)+2.6f,6960)),
                "building-closeup"=>BuildingCamera(session),
                // Overhead of a Meridian junction (patch, stop bars, signs) and a low view along the Cedar highway's edge lines.
                "road-junction"=>ChaseCaptureCamera(new(5768,session.World.HeightAt(5768,6935)+15,6935),new(5781,session.World.HeightAt(5781,6955),6955)),
                "highway-edge"=>ChaseCaptureCamera(new(-2380,session.World.HeightAt(-2380,2306)+2.4f,2306),new(-2700,session.World.HeightAt(-2700,2318)+1,2318)),
                "clear-landscape"=>ChaseCaptureCamera(new(-820,session.World.HeightAt(-820,-80)+62,-80),new(450,35,1180)),
                "crest-shore"=>ChaseCaptureCamera(new(980,session.World.HeightAt(980,55)+3.4f,55),new(1210,9,480)),
                "crest-underwater"=>ChaseCaptureCamera(new(1050,1,400),new(1250,2,550)),
                "dressed-avatar"=>ChaseCaptureCamera(new(-335.5f,session.World.HeightAt(-336,80)+1.72f,73),new(-336,session.World.HeightAt(-336,80)+1.05f,80)),
                "power-flash"=>ChaseCaptureCamera(new(-330,session.World.HeightAt(-330,970)+3.5f,970),new(-235,session.World.HeightAt(-235,1100)+13,1100)),
                "physx-airborne"=>ChaseCaptureCamera(new(-300,session.World.HeightAt(-300,420)+48,420),new(parent.Position.X,250,parent.Position.Z)),
                "structures-intact" or "physx-settled-clear"=>
                    ChaseCaptureCamera(new(195,session.World.HeightAt(195,930)+26,930),new(82,session.World.HeightAt(80,1040)+6,1040)),
                // The ten Blender field-probe types in a row on open ground (see AddProbeLineup).
                "emergency-lineup"=>ChaseCaptureCamera(new(-300,session.World.HeightAt(-300,600)+3.2f,600),new(-300,session.World.HeightAt(-300,614)+1.2f,614)),
                "probe-lineup"=>ChaseCaptureCamera(new(-300,session.World.HeightAt(-300,601)+4.2f,601),new(-300,session.World.HeightAt(-300,608)+.5f,608)),
                // Inside the forward-flank precipitation/hail core, looking back toward the updraft.
                "core-punch"=>ChaseCaptureCamera(new(parent.Position.X+parent.ForwardFlankCoreOffset.X,
                    session.World.HeightAt(parent.Position.X+parent.ForwardFlankCoreOffset.X,parent.Position.Z+parent.ForwardFlankCoreOffset.Z)+2.2f,
                    parent.Position.Z+parent.ForwardFlankCoreOffset.Z),new(parent.Position.X,160,parent.Position.Z)),
                _=>ChaseCaptureCamera(new(-300,session.World.HeightAt(-300,160)+3.4f,160),new(parent.Position.X,220,parent.Position.Z)),
            };
            var local = new UniStormSpatialWeatherController(UniStormWeatherCatalog.LoadDefault(),13,
                new UniStormOptions{AutomaticWeatherEnabled=false,AdvanceInternalClock=false,TransitionSeconds=2});
            EnvironmentSceneState environment=source;
            for(int tick=0;tick<200;tick++)
                environment=local.Update(new(.05f,42,1,40,session.Hour,22,camera.Position),source);
            float visualSeconds=42;
            RenderFrame Build(float delta=0)
            {
                var built=session.BuildFrame(number,visualSeconds,delta,environment,camera,width,height,
                    particles,history, snapshot:local.Snapshot,advancePhysics:false);
                return name=="probe-lineup"?AddProbeLineup(built,session):name=="chase-truck"?AddChaseTruck(built,session)
                    :name=="emergency-lineup"?AddEmergencyLineup(built,session,visualSeconds)
                    :name is "lot-modules" or "lot-round-room"?built with{Items=built.Items.Concat(lotShowcase.Items).ToList()}:built;
            }
            var frame=Build();
            if(name=="power-flash")
            {
                for(int tick=0;tick<240&&session.PowerFlashes.AcceptedEventCount==0;tick++)
                {
                    session.Destruction.Update(1f/60,frame.Weather);
                    visualSeconds=42+tick/60f;
                    frame=Build();
                }
                if(session.PowerFlashes.AcceptedEventCount==0||session.PowerFlashes.AddedLightCount==0||session.PowerFlashes.SparkInstanceCount==0)
                    throw new InvalidOperationException("A native highway utility failure did not emit its local electrical flash.");
            }
            if(name is "physx-airborne" or "physx-settled-clear")
            {
                // Feed the actual mapped source tornado to the native 60Hz body simulation.
                var forceLocal = new UniStormSpatialWeatherController(UniStormWeatherCatalog.LoadDefault(),13,
                    new UniStormOptions{AutomaticWeatherEnabled=false,AdvanceInternalClock=false});
                var forceEnvironment=forceLocal.Update(new(0,42,1,40,15,22,camera.Position),mature);
                var forceFrame=session.BuildFrame(number,42,0,forceEnvironment,camera,width,height,false,history,advancePhysics:false);
                for(int tick=0;tick<480;tick++)session.Destruction.Update(1f/60,forceFrame.Weather);
                if(session.Destruction.DestroyedPartCount==0||session.Destruction.MaximumDebrisSpeed<.5f)
                    throw new InvalidOperationException("Genuine source winds did not detach and accelerate native PhysX parts.");
                if(name=="physx-settled-clear")
                {
                    for(int tick=0;tick<3600;tick++)session.Destruction.Update(1f/60,null);
                    if(session.Destruction.MaximumDebrisSpeed>1)
                        throw new InvalidOperationException("Native debris did not settle after the source storm was removed.");
                }
                frame=Build();
            }
            if(name=="crest-underwater")
            {
                for(int tick=0;tick<30;tick++)frame=Build(1f/60);
                if(!session.Water.Underwater.IsSubmerged||session.Water.Underwater.Blend<.99f)
                    throw new InvalidOperationException("The real Crest underwater treatment did not activate.");
            }
            if(name=="core-punch")
            {
                // Let gusts, rain and hail particles spin up, then require the core to actually be felt.
                for(int tick=0;tick<90;tick++){visualSeconds=42+tick/60f;frame=Build(1f/60);}
                var here=session.Effects.LocalConditions;
                var wrappedCell=frame.Weather?.ActiveStorms.FirstOrDefault(cell=>cell.Tornadoes.Count>0);
                Console.WriteLine($"Core punch: {here.Summary(session.Effects.Gust)}; region {here.Region}; storm occlusion {wrappedCell?.Occlusion:0.00}, funnel condensation {wrappedCell?.Tornadoes[0].CondensationAmount:0.00}, fog {frame.Sky?.FogDensity:0.0000}.");
                if(here.Rain01<.5f||here.Hail01<.1f||here.Visibility01>.5f||frame.Sky is null||frame.Sky.FogDensity<.0005f)
                    throw new InvalidOperationException($"The forward-flank core was not felt at the camera: {here}, fog {frame.Sky?.FogDensity}.");
            }
            if(name=="dressed-avatar"&&(frame.SkinPoses is not {Count:>0}||!frame.Items.Any(item=>item.SkinnedPoseId is not null)))
                throw new InvalidOperationException("The dressed UMA actor did not submit actual GPU skin palettes.");
            if(name=="night-torch"&&frame.Lights?.Any(light=>light.Id=="chaser-torch")!=true)
                throw new InvalidOperationException("Native night spotlight was not submitted.");
            if(name=="lightning-storm")
            {
                // Advance the real imported particle/lightning presenter until its
                // scheduled source strike; do not replace the storm with a visual fixture.
                session.Effects.Reset();
                for(int tick=0;tick<240;tick++)
                {
                    visualSeconds=42+tick/60f;frame=Build(1f/60);
                    if(session.Effects.LightningFlash>.08f&&session.Effects.LastStrike is not null)break;
                }
                if(session.Effects.LastStrike is null||session.Effects.LightningFlash<=.08f)
                    throw new InvalidOperationException("The native lightning scheduler did not emit its bolt/light frame.");
            }
            if(args.Contains("--chase-hud",StringComparer.OrdinalIgnoreCase))
            {
                // Review mode: the real GPU HUD over the scene, with campaign telemetry built
                // from the live session. Scenes exercise different HUD states.
                var hudTelemetry=CaptureHudTelemetry(session,frame,name);
                hud.InvalidateRadar();
                hud.Update(surface,hudTelemetry,frame);
                frame=frame with{Ui=hud.Ui};
            }
            int warmups=0, residentFrames=0;
            // Streaming uploads are time-sliced; allow a slower or shared GPU to
            // finish residency rather than failing a capture of a valid frame.
            for(;warmups<720;warmups++)
            {
                surface.PumpMessages();
                await backend.RenderAsync(frame with{FrameNumber=number++},CancellationToken.None);
                residentFrames=backend.LastFrameStatistics.RequiredResourcesResident?residentFrames+1:0;
                if(warmups>=23&&residentFrames>=16)break;
            }
            ExportCompleted.SetValue(backend,false);
            var watch=Stopwatch.StartNew();
            var captured=frame with{FrameNumber=number++};
            await backend.RenderAsync(captured,CancellationToken.None);
            // The renderer now exports only after several consecutive fully
            // resident frames; keep presenting the identical frame until it does.
            for(int settle=0;settle<32&&!(bool)ExportCompleted.GetValue(backend)!;settle++)
            {
                surface.PumpMessages();
                captured=frame with{FrameNumber=number++};
                await backend.RenderAsync(captured,CancellationToken.None);
            }
            validation.CollectErrors(backend,name,waitForCompletion:validation.Enabled);
            var stats=backend.LastFrameStatistics;
            if(!(bool)ExportCompleted.GetValue(backend)!||!stats.RequiredResourcesResident||stats.OpaqueBatchCount<=0)
                throw new InvalidOperationException("Incomplete combined native frame "+name+": "+stats);
            if(stats.InstancedInstanceCount<=0)throw new InvalidOperationException("Native chase scenery did not use its instance buffers: "+name);
            if(name.StartsWith("crest-",StringComparison.Ordinal)&&stats.TranslucentBatchCount<=0)
                throw new InvalidOperationException("Native Crest water did not submit a translucent surface: "+name);
            string path=Path.Combine(output,name+".png");File.Copy(staging,path,true);
            using var image=Image.Load<Rgba32>(path);
            byte[] pixels=new byte[width*height*4];image.CopyPixelDataTo(pixels);
            double mean=MeanLuminance(pixels),variation=LuminanceVariation(pixels,mean);
            if(image.Width!=width||image.Height!=height||mean<.001||variation<.000001)
                throw new InvalidDataException("Blank or invalid combined native capture: "+name);
            pixelsByName[name]=pixels;
            // CPU profile: the game's frame construction plus the renderer's CPU phases.
            const int profileFrames=40;
            var phases=new Dictionary<string,double>(StringComparer.Ordinal);
            backend.FrameCpuTimingSink=(phase,ms)=>{lock(phases)phases[phase]=phases.GetValueOrDefault(phase)+ms;};
            session.FrameTimingSink=(phase,ms)=>{lock(phases)phases["Game."+phase]=phases.GetValueOrDefault("Game."+phase)+ms;};
            double buildMs=0,renderMs=0;int profiledItems=0;
            for(int profile=0;profile<profileFrames;profile++)
            {
                surface.PumpMessages();
                long started=Stopwatch.GetTimestamp();
                var profiled=Build();
                buildMs+=Stopwatch.GetElapsedTime(started).TotalMilliseconds;profiledItems=profiled.Items.Count;
                started=Stopwatch.GetTimestamp();
                await backend.RenderAsync(profiled with{FrameNumber=number++},CancellationToken.None);
                renderMs+=Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            }
            backend.FrameCpuTimingSink=null;session.FrameTimingSink=null;
            var lastProfiled=Build();
            var cpuProfile=new{BuildFrameMs=buildMs/profileFrames,RenderCallMs=renderMs/profileFrames,FrameItems=profiledItems,
                ItemsByMaterial=lastProfiled.Items.GroupBy(i=>i.MaterialId+(i.CastsShadow?" [shadow]":"")).OrderByDescending(g=>g.Count()).Take(14).ToDictionary(g=>g.Key,g=>g.Count()),
                Phases=phases.ToDictionary(p=>p.Key,p=>p.Value/profileFrames)};
            Console.WriteLine($"  CPU {name}: build {cpuProfile.BuildFrameMs:F2} ms, render call {cpuProfile.RenderCallMs:F2} ms, items {profiledItems}; "
                +string.Join(", ",cpuProfile.Phases.OrderByDescending(p=>p.Value).Take(4).Select(p=>$"{p.Key} {p.Value:F2}")));
            var renderedStorm=captured.Weather?.ActiveStorms.FirstOrDefault();
            var telemetry=new StormChaseTelemetry(stormView?"Mature":"Clear",renderedStorm?.WindSpeed??0,
                renderedStorm?.SmoothedWidthMeters??0,renderedStorm is null?0:ChaseCaptureDistance(camera.Position,renderedStorm.Position),
                local.LocalWeatherName,local.StormInfluence,0,backend.LastGpuTiming?.TotalMilliseconds??0,"NATIVE CAPTURE",
                session.LightingName,session.Destruction.DestroyedPartCount,session.ProbeCount,0,true);
            string hudPath=Path.Combine(output,name+"-instruments.png");hud.SaveOverlay(hudPath,width,height,telemetry,captured);
            reports.Add(new{Name=name,Path=path,OverlayPath=hudPath,captured.FrameNumber,captured.AnimationTimeSeconds,
                WarmupFrames=Math.Min(warmups+1,64),CaptureMilliseconds=watch.Elapsed.TotalMilliseconds,Camera=camera,
                SourceSimulationSeconds=stormView?matureSimulationSeconds:simulation.SimulationSeconds,
                Weather=captured.Weather,Sky=captured.Sky,MeanLuminance=mean,LuminanceVariation=variation,
                Statistics=stats,GpuTiming=backend.LastGpuTiming,CpuProfile=cpuProfile,NativeSkinPaletteCount=captured.SkinPoses?.Count??0,
                NativePhysicsSteps=session.Destruction.PhysicsStepCount,DestroyedParts=session.Destruction.DestroyedPartCount,
                MovingParts=session.Destruction.ActiveDebrisCount,MaximumDebrisSpeed=session.Destruction.MaximumDebrisSpeed,
                UnderwaterBlend=session.Water.Underwater.Blend,LightningStrike=session.Effects.LastStrike,
                LightningFlash=session.Effects.LightningFlash,SimulatedParticleCount=session.Effects.SimulatedParticleCount,
                ElectricalFailure=new {session.PowerFlashes.AcceptedEventCount,session.PowerFlashes.LastEventId,
                    session.PowerFlashes.LastPowerFlashPosition,session.PowerFlashes.AddedLightCount,session.PowerFlashes.SparkInstanceCount,
                    session.PowerFlashes.ArcInstanceCount,session.PowerFlashes.FlashIntensity},
                Sha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))});
            Console.WriteLine($"Chase native capture {name}: resident={stats.RequiredResourcesResident}, opaque={stats.OpaqueBatchCount}, instances={stats.InstancedInstanceCount}, PhysX broken={session.Destruction.DestroyedPartCount}; {path}");
            history++;
        }
        double? settledDifference=pixelsByName.TryGetValue("structures-intact",out var intact)&&pixelsByName.TryGetValue("physx-settled-clear",out var settled)
            ?PixelDifference(intact,settled):null;
        if(settledDifference is <=.00001)throw new InvalidDataException("Native breakage and intact structures are visually identical.");
        double? movingDifference=pixelsByName.TryGetValue("moving-storm-a",out var firstMotion)&&pixelsByName.TryGetValue("moving-storm-b",out var secondMotion)
            ?PixelDifference(firstMotion,secondMotion):null;
        if(movingDifference is <=.001)throw new InvalidDataException("The traveling native supercell captures did not change visibly.");
        validation.CollectErrors(backend,"combined chase final drain",waitForCompletion:validation.Enabled);
        string reportPath=Path.Combine(output,"storm-chase-manifest.json");
        File.WriteAllText(reportPath,JsonSerializer.Serialize(new
        {
            Schema=1,NativeGpu=true,Backend=backend.Info,Hardware=Hardware(backend),Width=width,Height=height,
            WeatherQualityName=qualityName,options.WeatherRenderScale,options.WeatherQuality,
            EngineAssemblySha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(Dx12RenderBackend).Assembly.Location))),
            SourceLifecycle=new{Preset=LiveStormController.Presets[3],MatureSeconds=matureSimulationSeconds,MatureTick=matureTickCount,CompleteSeconds=simulation.SimulationSeconds},
            World=new{session.World.TerrainPageCount,session.World.TreeCount,session.World.GrassClusterCount,session.World.RoadCount,
                DestructibleParts=session.World.DestructibleItems.Count,StaticSolids=session.World.StaticCollisionItems.Count},
            Crest=new{session.Water.PatchCount,Capabilities=StormChaseWater.Capabilities,session.Water.Registration},
            Avatar=new{session.Avatar.PartCount,session.Avatar.HeightMeters,session.Avatar.WardrobeNames,session.Avatar.AnimationSource,session.Avatar.CatalogPath},
            Physics=session.Destruction.NativeProvider,IntactToSettledMeanAbsoluteRgbDifference=settledDifference,
            Motion=new {First=movingA.ActiveStorms.Single().Position,Second=movingB.ActiveStorms.Single().Position,
                SourceSeconds=50,WallSecondsAt20x=2.5,MeanAbsoluteRgbDifference=movingDifference},
            Captures=reports,Warnings=logger.Warnings,DebugValidation=validation.Snapshot,
        },JsonOptions));
        Console.WriteLine($"{reports.Count} combined native storm chase captures passed: {reportPath}");
        return 0;
    }

    private static CameraState ChaseCaptureCamera(Vector3f position,Vector3f target)
    {
        var forward=Vector3.Normalize(new(target.X-position.X,target.Y-position.Y,target.Z-position.Z));
        return new(position,new(forward.X,forward.Y,forward.Z),58,.1f,16000);
    }
    private static PmWeatherSupercellSimulation ChasePeakSimulation(int preset,int seed)
    {
        var native=new PmWeatherSupercellSimulation(LiveStormController.CreateChaseOptions(preset,seed));
        for(int tick=0;tick<100_000;tick++)
        {
            if(native.Current.ActiveStorms.FirstOrDefault() is {Stage:3,IsDying:true})return native;
            native.Advance(.05);
        }
        throw new InvalidOperationException("The traveling source storm did not reach its native peak.");
    }
    private static float ChaseCaptureDistance(Vector3f a,Vector3f b)=>MathF.Sqrt((a.X-b.X)*(a.X-b.X)+(a.Z-b.Z)*(a.Z-b.Z));

    /// <summary>Police cruiser, fire engine and ambulance parked in a fan 11-16 m ahead, beacons flashing.</summary>
    private static RenderFrame AddEmergencyLineup(RenderFrame frame,StormChaseSession session,float seconds)
    {
        var items=frame.Items.ToList();var lights=(frame.Lights??[]).ToList();
        foreach(var (id,x,z,yaw) in new[]{("fire-engine",-306.5f,617f,2.6f),("ambulance",-300f,613f,3.4f),("police-cruiser",-294f,611f,3.9f)})
        {
            var origin=new Vector3f(x,session.World.HeightAt(x,z),z);
            items.AddRange(session.World.Models.CreateItems(id,origin,yaw,"showcase/"+id));
            float c=MathF.Cos(yaw),s=MathF.Sin(yaw);
            // Same yaw convention as StormChaseModelLibrary.CreateItems.
            lights.AddRange(StormChaseEmergencyLights.Create(session.World.Models,id,
                local=>origin+new Vector3f(local.X*c+local.Z*s,local.Y,-local.X*s+local.Z*c),seconds+(id.Length*.13f)));
        }
        return frame with{Items=items,Lights=lights};
    }

    /// <summary>The Blender classic chase pickup parked 17 m ahead of the camera, three-quarter view, storm behind it.</summary>
    private static RenderFrame AddChaseTruck(RenderFrame frame,StormChaseSession session)
    {
        var c=frame.Camera; float heading=MathF.Atan2(c.Forward.X,c.Forward.Z);
        float fx=MathF.Sin(heading),fz=MathF.Cos(heading);
        float x=c.Position.X+fx*17-fz*3.5f, z=c.Position.Z+fz*17+fx*3.5f;
        var items=frame.Items.ToList();
        // Nose turned toward the camera's left so the front, side and roof mesonet all show.
        items.AddRange(session.World.Models.CreateItems("chase-truck",new(x,session.World.HeightAt(x,z),z),heading+2.3f,"showcase/chase-truck"));
        return frame with{Items=items};
    }

    /// <summary>Two rows of five probes, 5-8 m in front of the probe-lineup camera, each on the terrain.</summary>
    private static RenderFrame AddProbeLineup(RenderFrame frame,StormChaseSession session)
    {
        var items=frame.Items.ToList();
        var ids=StormChaseModelLibrary.ProbeModelIds;
        for(int i=0;i<ids.Count;i++)
        {
            float x=-300+(i%5-2)*2.5f, z=606+(i/5)*3.2f;
            items.AddRange(session.World.Models.CreateItems(ids[i],new(x,session.World.HeightAt(x,z),z),MathF.PI,"lineup/"+ids[i]));
        }
        return frame with{Items=items};
    }

    private static StormChaseTelemetry CaptureHudTelemetry(StormChaseSession session,RenderFrame frame,string scene)
    {
        var camera=frame.Camera.Position;
        var storm=frame.Weather?.ActiveStorms.OrderBy(cell=>(cell.Position.X-camera.X)*(cell.Position.X-camera.X)+(cell.Position.Z-camera.Z)*(cell.Position.Z-camera.Z)).FirstOrDefault();
        float range=storm is null?0:MathF.Sqrt((storm.Position.X-camera.X)*(storm.Position.X-camera.X)+(storm.Position.Z-camera.Z)*(storm.Position.Z-camera.Z));
        bool quiet=scene is "clear-landscape" or "crest-shore" or "crest-underwater" or "structures-intact" or "physx-settled-clear" or "night-torch";
        bool driving=scene is "severe-road" or "moving-storm-a" or "moving-storm-b" or "sunset-storm" or "core-punch";
        // The in-game HUD shows the day's outlook sounding (the boundary target), not the camera's own column.
        var atmosphere=StormChaseAtmosphere.OutlookSnapshot(73019,2,session.Hour,1500,30000);
        var world=session.World;
        var probe=new ChaseFieldProbe(1,new(camera.X+600,camera.Y,camera.Z+900),1){WindMph=quiet?0:84,PeakMph=quiet?0:112};
        var mission=quiet?new StormChase.Protocol.MissionState(7,"Route reconnaissance","survey",1,3,
            "Drive to the marked shelterbelt on the Cedar highway and photograph the windbreak.",
            new(camera.X+420,camera.Y,camera.Z-260),false,12,30):null;
        var telemetry=new StormChaseTelemetry(quiet?"Quiet day":storm?.WindSpeed>0?"Tornadic":"Thunderstorm",
            quiet?0:storm?.WindSpeed??0,quiet?0:storm?.SmoothedWidthMeters??0,quiet?0:range,
            quiet?"Clear":"Thunderstorm",quiet?.04f:.86f,61,7.4,driving?"DRIVING":"ON FOOT",session.LightingName,
            quiet?0:76,1,3,false,"POD 1: 84 MPH",FunnelEpisode:quiet?0:2,Touchdowns:quiet?0:2,HasFunnel:!quiet,
            LocalStorm:session.Effects.LocalConditions,Gust:session.Effects.Gust);
        if(scene=="crest-underwater")return telemetry with{CameraMode="FLY CAMERA"};   // demo-mode HUD (no campaign)
        return telemetry with{Campaign=new(2,session.Hour,"Enhanced risk","",.61f,quiet?0:(frame.Weather?.ActiveStorms.Count??0),
            925.40m,"Prairie pickup",driving?54:0,52.3f,75,86,false,scene=="physx-settled-clear",1,
            quiet?"Quiet skies. Take a dispatch job (O) or prepare your equipment (U).":"Plan your intercept. Deploy B probes ahead of the track; F takes a photograph.",
            false,world.Settlements,world.GasStations,[probe],camera,world.RoadRoutes,Popularity:14,Mission:mission,Atmosphere:atmosphere,Boundary:StormChaseAtmosphere.ActiveBoundary(73019,2,session.Hour))};
    }
}
