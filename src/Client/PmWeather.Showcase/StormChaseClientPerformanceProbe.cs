using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Engine.Environment;
using Engine.Rendering.Assets;
using Engine.Rendering.Backend;
using Engine.Rendering.Configuration;
using Engine.UniStorm;
using Shared.Contracts.Rendering;
using Shared.Contracts.World;

internal static partial class Showcase
{
    // Finite diagnostic using fresh, explicitly isolated careers/accounts. The
    // online case freezes a genuinely admitted authority snapshot, never a clone.
    private static async Task<int> VerifyClientPerformanceAsync(RendererOptions options,NativeSurface surface,
        MutableRenderAssetCatalog catalog,CaptureLogger logger,RenderGpuDebugValidation validation,
        string output,string staging,string[] args)
    {
        using var tokenSource=new CancellationTokenSource(TimeSpan.FromMinutes(6));var token=tokenSource.Token;
        using var local=new StormChaseSession(catalog,logger,OptionalValue(args,"--avatar-catalog"));
        var career=new StormChaseCareer(local.World,Path.Combine(output,"isolated-performance-career.json"));
        using var vehicle=new StormChaseVehicles(local.World,local.Destruction,career);
        local.Career=career;local.Vehicles=vehicle;local.ConfigureLocalHousing(career);
        local.Destruction.PersistentPhysicsAnchor=()=>vehicle.Position;
        career.DestroyedBuildings=()=>local.Destruction.DestroyedBuildingIds;
        career.VehiclePosition=()=>vehicle.Position;career.BindContractPhysics(local.Destruction);
        var station=local.World.GasStations[0];
        var eye=station.ParkingPosition+new Vector3f(-3,1.72f,0);
        var camera=ChaseCaptureCamera(eye,eye+new Vector3f(35,1,65));
        var clear=new PmWeatherSupercellSimulation(LiveStormController.CreateChaseOptions(3,14));
        var clearSource=clear.Current with{Storms=[],TimeOfDayHours=15};
        var maturePoint=FindWallCloudSourcePoints().Single(point=>point.Name=="wall-04-mature-touchdown");
        // Translation is an explicit placement fixture; wind/lifecycle/profile
        // are the unchanged real source state, and are grounded through Session.
        var shift=station.Position+new Vector3f(900,0,950)-maturePoint.State.ActiveStorms.Single().Position;
        var matureSource=local.GroundStorms(maturePoint.State with{Storms=maturePoint.State.ActiveStorms.Select(storm=>storm with
        {Position=storm.Position+shift,Tornadoes=storm.Tornadoes.Select(child=>child with{Position=child.Position+shift}).ToArray()}).ToArray()});
        EnvironmentSceneState Observe(EnvironmentSceneState source)
        {
            var weather=new UniStormSpatialWeatherController(UniStormWeatherCatalog.LoadDefault(),13,new(){AutomaticWeatherEnabled=false,AdvanceInternalClock=false,TransitionSeconds=2});
            for(int tick=0;tick<160;tick++)source=weather.Update(new(.05f,42,2026,150,15,22,camera.Position),source);
            return source;
        }
        var clearEnvironment=Observe(clearSource);var matureEnvironment=Observe(matureSource);

        using var onlineSession=new StormChaseSession(catalog,logger,OptionalValue(args,"--avatar-catalog"),simulateLocally:false);
        onlineSession.PrepareOnlinePlayers(2);
        using var account=new ChaseApiClient();account.Configure(Value(args,"--accounts-url","http://127.0.0.1:5080"));
        using var gameApi=new ChaseApiClient();gameApi.Configure(Value(args,"--server-url","http://127.0.0.1:5180"));
        var metadata=await gameApi.ServerAsync(token);if(!metadata.Ready)throw new InvalidOperationException("The externally owned proof server must already be ready.");
        string suffix=Guid.NewGuid().ToString("N")[..12];
        await account.SignInAsync("perf_"+suffix,Convert.ToHexString(RandomNumberGenerator.GetBytes(24)),true,token);
        var character=await account.CreateCharacterAsync("Steady client performance",token);
        using var client=new StormChaseOnlineClient(metadata,await account.JoinAsync(character.Id,metadata.ServerId,token),onlineSession.World.ContentFingerprint);
        await client.Admission.WaitAsync(token);var online=client.Current!;
        var onlineEnvironment=Observe(online.World.Environment!);onlineSession.CampaignHour=15;local.CampaignHour=15;
        using var hud=new StormChaseHud(surface.WindowHandle);
        var telemetry=new StormChaseTelemetry("Steady diagnostic",0,0,0,"Clear",0,0,0,"ON FOOT","AFTERNOON",0,0,0,true,
            Campaign:new(1,15,"SOURCE FIXTURE","Finite steady native performance measurement",0,0,career.Save.Cash,
                career.SelectedVehicle.Name,0,career.Condition.FuelLiters,career.SelectedVehicle.FuelCapacityLiters,career.Condition.Health,
                false,false,0,"",false,local.World.Settlements,local.World.GasStations,[],vehicle.Position,local.World.RoadRoutes));

        await using var backend=new Dx12RenderBackend(options,surface,catalog,logger);await backend.InitializeAsync(token);
        ExportCompleted.SetValue(backend,true);long number=0;var reports=new List<object>();
        var cases=new[]{("clear-meridian",local,clearEnvironment), ("mature-meridian",local,matureEnvironment), ("online-static-authority",onlineSession,onlineEnvironment)};
        foreach(var scenario in cases)
        {
            var phases=new Dictionary<string,double>();scenario.Item2.FrameTimingSink=(name,ms)=>phases[name]=ms;
            RenderFrame Create()
            {
                phases.Clear();
                var built=scenario.Item2==onlineSession
                    ?onlineSession.BuildOnlineFrame(number++,42,1f/60,online,camera,surface.Width,surface.Height,true,70000,renderEnvironment:scenario.Item3)
                    :local.BuildFrame(number++,42,1f/60,scenario.Item3,camera,surface.Width,surface.Height,true,70000,
                        avatarFeet:eye-new Vector3f(0,1.72f,0));
                if(scenario.Item2==local)
                {
                    var started=Stopwatch.GetTimestamp();
                    career.Update(42,0,built.Weather);
                    career.UpdateContracts(1f/60,1,eye-new Vector3f(0,1.72f,0),vehicle.Position,false,0,built.Weather,42);
                    phases["CareerAndContracts"]=Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                }
                return built;
            }
            void UpdateHud(RenderFrame built)
            {
                var preparedTelemetry=telemetry with{Campaign=telemetry.Campaign! with
                {StormCount=built.Weather?.ActiveStorms.Count??0,
                    Mission=scenario.Item2==local?career.DispatchMission:online.World.Mission,
                    Activities=scenario.Item2==local?career.DispatchActivities:online.World.Activities}};
                hud.Housing=scenario.Item2.HousingState;
                hud.Update(surface,preparedTelemetry,built);
            }
            int resident=0;RenderFrame frame=Create();
            for(int warm=0;warm<384;warm++)
            {
                frame=Create();UpdateHud(frame);frame=frame with{Ui=hud.Ui};
                await backend.RenderAsync(frame,token);resident=backend.LastFrameStatistics.RequiredResourcesResident?resident+1:0;
                if(warm>=31&&resident>=16)break;
            }
            if(resident<16)throw new InvalidOperationException("Steady performance requires fully resident native resources: "+scenario.Item1);
            var builds=new List<double>();var buildCpu=new List<double>();var physicsPhases=new Dictionary<string,List<double>>();
            var hudWall=new List<double>();var hudCpu=new List<double>();var renderWall=new List<double>();var renderCpu=new List<double>();
            var gpu=new List<double>();var seenGpu=new HashSet<long>();var waits=new List<double>();
            var rendererPhases=new Dictionary<string,List<double>>();
            backend.FrameCpuTimingSink=(name,ms)=>
            {if(!rendererPhases.TryGetValue(name,out var times))rendererPhases[name]=times=[];times.Add(ms);};
            long allocated=GC.GetAllocatedBytesForCurrentThread(),cpuThread=GetCurrentThreadId();
            // Build-only samples distinguish UI work from any GPU wait. The
            // exact same Session/PhysX/HUD path is also exercised during draws.
            for(int sample=0;sample<96;sample++)
            {
                var start=Stopwatch.GetTimestamp();var cpu=ThreadCpuMilliseconds();frame=Create();
                builds.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);buildCpu.Add(ThreadCpuMilliseconds()-cpu);
                foreach(var phase in phases){if(!physicsPhases.TryGetValue(phase.Key,out var times))physicsPhases[phase.Key]=times=[];times.Add(phase.Value);}
                start=Stopwatch.GetTimestamp();cpu=ThreadCpuMilliseconds();UpdateHud(frame);
                hudWall.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);hudCpu.Add(ThreadCpuMilliseconds()-cpu);
            }
            long allocatedBuild=GC.GetAllocatedBytesForCurrentThread()-allocated;
            for(int sample=0;sample<80;sample++)
            {
                frame=Create();UpdateHud(frame);frame=frame with{Ui=hud.Ui};
                var started=Stopwatch.GetTimestamp();var cpu=ThreadCpuMilliseconds();await backend.RenderAsync(frame,token);
                renderWall.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);renderCpu.Add(ThreadCpuMilliseconds()-cpu);
                if(backend.LastGpuTiming is { } timing&&seenGpu.Add(timing.FrameNumber))gpu.Add(timing.TotalMilliseconds);
            }
            backend.FrameCpuTimingSink=null;
            if(GetCurrentThreadId()!=cpuThread)throw new InvalidOperationException("Renderer CPU samples crossed a native thread.");
            if(gpu.Count<48||!backend.LastFrameStatistics.RequiredResourcesResident
                ||backend.LastFrameStatistics.MissingCatalogResourceCount!=0||backend.LastFrameStatistics.InvalidSkinPoseCount!=0)
                throw new InvalidOperationException("Incomplete resident native timing samples: "+scenario.Item1);
            for(int sample=0;sample<24;sample++)
            {surface.PumpMessages();var started=Stopwatch.GetTimestamp();surface.WaitForMessages(8);waits.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);}
            ExportCompleted.SetValue(backend,false);for(int sample=0;sample<24&&!(bool)ExportCompleted.GetValue(backend)!;sample++)
                await backend.RenderAsync(frame with{FrameNumber=number++},token);
            if(!(bool)ExportCompleted.GetValue(backend)!)throw new InvalidOperationException("Native performance image export failed.");
            var imagePath=Path.Combine(output,scenario.Item1+".png");File.Copy(staging,imagePath,true);ExportCompleted.SetValue(backend,true);
            var summary=new{Scenario=scenario.Item1,FrameConstructionWall=PerfMetric(builds),FrameConstructionCpu=PerfMetric(buildCpu),
                Phases=physicsPhases.ToDictionary(pair=>pair.Key,pair=>PerfMetric(pair.Value)),HudWall=PerfMetric(hudWall),HudCpu=PerfMetric(hudCpu),
                RendererCallWallIncludingGpuWait=PerfMetric(renderWall),RendererThreadCpu=PerfMetric(renderCpu),Gpu=PerfMetric(gpu),
                RendererPhases=rendererPhases.ToDictionary(pair=>pair.Key,pair=>PerfMetric(pair.Value)),
                UiWaitRequested8Milliseconds=PerfMetric(waits),AllocatedBuildAndHudBytesPerFrame=allocatedBuild/96d,
                frame.Camera,frame.Viewport,SceneItems=frame.Items.Count,Statistics=backend.LastFrameStatistics,
                PhysicsSteps=scenario.Item2==local?local.Destruction.PhysicsStepCount:0,
                BodyCount=scenario.Item2==local?local.Destruction.DynamicBodyCount:online.Debris.Count,
                SourceStorms=scenario.Item3.ActiveStorms.Select(storm=>new{storm.Id,storm.Position,storm.Stage}).ToArray(),
                Capture=imagePath,Sha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(imagePath)))};
            reports.Add(summary);Console.WriteLine($"Steady {scenario.Item1}: frame {PerfMetric(builds).MedianMilliseconds:F2}ms; HUD {PerfMetric(hudWall).MedianMilliseconds:F2}ms; renderer CPU {PerfMetric(renderCpu).MedianMilliseconds:F2}ms; GPU {PerfMetric(gpu).MedianMilliseconds:F2}ms; wait8 {PerfMetric(waits).MedianMilliseconds:F2}ms.");
            scenario.Item2.FrameTimingSink=null;
            backend.FrameCpuTimingSink=null;
        }
        validation.CollectErrors(backend,"steady native client performance",validation.Enabled);
        string path=Path.Combine(output,"steady-client-performance.json");File.WriteAllText(path,JsonSerializer.Serialize(new
        {Passed=true,NativeDx12=true,Scope="Steady fully resident 49km-world Meridian client work; online snapshot admitted by independent native server, no local online physics",
            options.WeatherQuality,options.WeatherRenderScale,options.SynchronizeFrameResources,Quality="High unchanged: .25 scale, four light samples, 800 view steps",
            WorldFingerprint=local.World.ContentFingerprint,BuildingCount=local.World.Buildings.Count,metadata.ServerId,
            DebugValidation=validation.Enabled,Cases=reports,Measurement="Renderer thread CPU excludes waits using Win32 GetThreadTimes; renderer-call wall includes GPU synchronization. No PNG readbacks inside timing sets."},JsonOptions));
        Console.WriteLine("Steady client performance passed: "+path);return 0;
    }

    private sealed record ClientPerfMetric(double MedianMilliseconds,double P90Milliseconds,double MaximumMilliseconds,int Samples,double MeanMilliseconds);
    private static ClientPerfMetric PerfMetric(IEnumerable<double> values)
    {var sorted=values.Order().ToArray();return new(sorted[sorted.Length/2],sorted[(int)Math.Ceiling(sorted.Length*.9)-1],sorted[^1],sorted.Length,sorted.Average());}
    private static double ThreadCpuMilliseconds()
    {
        if(!GetThreadTimes(GetCurrentThread(),out _,out _,out var kernel,out var user))throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        return (kernel+user)/10_000d;
    }
    [DllImport("kernel32.dll")] private static extern nint GetCurrentThread();
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetThreadTimes(nint thread,out long creation,out long exit,out long kernel,out long user);
}
