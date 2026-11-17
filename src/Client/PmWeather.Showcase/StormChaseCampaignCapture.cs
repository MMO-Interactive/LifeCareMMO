using System.Diagnostics;
using System.Globalization;
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
    private static async Task<int> RunCampaignCaptureAsync(RendererOptions options,NativeSurface surface,
        MutableRenderAssetCatalog catalog,CaptureLogger logger,RenderGpuDebugValidation validation,
        bool particles,string qualityName,string output,string staging,int width,int height,string[] args)
    {
        Directory.CreateDirectory(output);
        // All model, terrain, Crest, wardrobe and native-physics resources exist
        // before device initialization. This isolated save never touches a career.
        using var session=new StormChaseSession(catalog,logger,OptionalValue(args,"--avatar-catalog"));
        string fixtureSave=Path.Combine(output,"career-fixture","native-photo-"+Guid.NewGuid().ToString("N")+".json");
        var career=new StormChaseCareer(session.World,fixtureSave);
        using var vehicles=new StormChaseVehicles(session.World,session.Destruction,career);
        session.Career=career;session.Vehicles=vehicles;
        career.DestroyedBuildings=()=>session.Destruction.DestroyedBuildingIds;
        session.Destruction.PersistentPhysicsAnchor=()=>vehicles.Position;
        if(session.Destruction.NativeProvider.IsReducedFallback)
            throw new InvalidOperationException("Campaign captures require the actual native PhysX provider.");
        var world=session.World;
        var cityFuel=world.GasStations[0];
        var maturePoint=FindWallCloudSourcePoints().Single(point=>point.Name=="wall-04-mature-touchdown");
        var mature=session.GroundStorms(maturePoint.State);
        string filter=Value(args,"--campaign-scene","all").ToLowerInvariant();
        string[] names=["campaign-city-afternoon","campaign-farm-town","campaign-industrial-town","campaign-city-night",
            "campaign-farm-tornado-photo","campaign-four-storms","campaign-city-destruction"];
        if(filter!="all"&&!names.Contains(filter,StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException("Unknown --campaign-scene: "+filter+". Use "+string.Join(", ",names)+" or all.");
        var four=filter is "all" or "campaign-four-storms"?FindCampaignFourStormCapturePoint():null;
        var clearService=new ProceduralEnvironmentService();
        EnvironmentSceneState Clear(float hour)=>clearService.Evaluate(BuiltInEnvironmentProfiles.TemperateOutdoor with
        { BaseFogDensity=.000012f,BaseFogStartDistance=4000,CloudCoverage=.12f,CloudDensity=.09f },hour,
            BuiltInWeatherProfiles.Clear,generateStorms:false) with
        { Weather=WeatherState.Clear,WeatherEffects=WeatherEffectsState.None,Storms=Array.Empty<WeatherStormCellState>(),Precipitation=0 };
        float Ground(float x,float z)=>world.HeightAt(x,z);

        await using var backend=new Dx12RenderBackend(options,surface,catalog,logger);
        await backend.InitializeAsync(CancellationToken.None);
        validation.CollectErrors(backend,"campaign capture initialization");
        long number=0,history=70000;
        var reports=new List<object>();var rewards=new List<object>();
        foreach(string name in names)
        {
            if(filter!="all"&&!name.Equals(filter,StringComparison.OrdinalIgnoreCase))continue;
            session.Reset();session.ThirdPerson=false;session.Flashlight=false;vehicles.ExitForCameraMode();
            float hour=name=="campaign-city-night"?22.5f:name=="campaign-four-storms"?four!.Hour:15;
            session.CampaignHour=hour;session.CampaignWallSeconds=42;
            EnvironmentSceneState source=Clear(hour);
            object provenance=new{Kind="Clear settlement fixture",Hour=hour};
            var camera=name switch
            {
                "campaign-city-afternoon" or "campaign-city-night"=>ChaseCaptureCamera(
                    new(cityFuel.Position.X-48,Ground(cityFuel.Position.X-48,cityFuel.Position.Z-45)+5,cityFuel.Position.Z-45),
                    cityFuel.Position+new Vector3f(7,5,22)),
                "campaign-farm-town"=>ChaseCaptureCamera(new(-7220,Ground(-7220,1900)+95,1900),new(-6500,34,2600)),
                "campaign-industrial-town"=>ChaseCaptureCamera(new(2800,Ground(2800,-6300)+95,-6300),new(3830,42,-5460)),
                "campaign-farm-tornado-photo"=>ChaseCaptureCamera(new(-300,Ground(-300,160)+4,160),
                    mature.ActiveStorms.Single().Tornadoes.Single().Position+new Vector3f(0,304,0)),
                "campaign-city-destruction"=>ChaseCaptureCamera(new(5920,Ground(5920,6530)+24,6530),new(5730,230,6850)),
                _=>CampaignWideCamera(four!.Source,world,width,height)
            };
            Vector3f? avatarFeet=null;
            if(name is "campaign-city-afternoon" or "campaign-city-night")
            {
                vehicles.Spawn(cityFuel.ParkingPosition);
                if(name=="campaign-city-night")
                {
                    // Enter the actual native vehicle instead of assigning a
                    // headlight state or placing a torch far from its avatar.
                    var driver=new FlyCamera();
                    driver.SetPose(new(vehicles.Position.X-3,vehicles.Position.Y+1.72f,vehicles.Position.Z),
                        new(vehicles.Position.X-3,vehicles.Position.Y+1.72f,vehicles.Position.Z+10));
                    if(!vehicles.Interact(driver,42))
                        throw new InvalidOperationException("Night capture could not enter its actual station vehicle.");
                    float x=cityFuel.Position.X-vehicles.Position.X,z=cityFuel.Position.Z+9-vehicles.Position.Z;
                    float heading=MathF.Atan2(x,z);
                    float pitch=MathF.Atan2(cityFuel.Position.Y+4-vehicles.Position.Y-2.2f,MathF.Sqrt(x*x+z*z));
                    driver.Look(heading/.0025f,-pitch/.0025f);
                    camera=vehicles.Camera(driver);
                    provenance=new{Kind="Night service station with genuinely entered native vehicle and actual headlights",Hour=hour};
                }
                else
                {
                    session.ThirdPerson=true;
                    avatarFeet=cityFuel.ParkingPosition+new Vector3f(-3,0,0);
                }
            }
            else if(name=="campaign-farm-tornado-photo")
            {
                source=mature;
                provenance=new{Kind="Genuine varied source lifecycle",Source=WallCloudPointReport(maturePoint)};
                vehicles.Spawn(new(-286,Ground(-286,245)+.1f,245));
                career.DeployProbe(new(-260,Ground(-260,265),265),42);
                career.DeployProbe(new(55,Ground(55,995),995),42);
            }
            else if(name=="campaign-four-storms")
            {
                source=session.GroundStorms(four!.Source);
                provenance=new{Kind="Genuine concurrent seeded campaign cells",four.Seed,four.Seconds,four.Hour,
                    four.Forecast,Cells=four.Cells,Spawns=four.Spawns};
                if(source.ActiveStorms.Count!=4||source.ActiveStorms.Select(storm=>storm.Id).Distinct().Count()!=4)
                    throw new InvalidOperationException("Campaign capture did not retain four independent source parents.");
            }
            else if(name=="campaign-city-destruction")
            {
                var simulation=new PmWeatherSupercellSimulation(LiveStormController.CreateOptions(5,13) with
                {
                    Id="campaign-capture-city-source",Origin=new(5650,Ground(5650,6850),6850),Velocity=Vector3f.Zero,
                    CloudBaseMeters=760,CloudTopMeters=3260,InitialStage=3,InitialEnergy=0,
                    InitialGrowthSpeed=3,TornadoGrowthSpeed=2,DecayGrowthSpeed=3,
                    PeakWindSpeedMph=230,MaximumWidthMeters=420,TouchdownWindSpeedMph=90
                });
                simulation.Advance(40);
                source=session.GroundStorms(simulation.Current);
                if(source.ActiveStorms.Single().Tornadoes.Count!=1)
                    throw new InvalidOperationException("The city destruction fixture did not develop its actual source funnel.");
                provenance=new{Kind="Genuine explicitly positioned source lifecycle and native PhysX destruction",
                    simulation.TickCount,simulation.SimulationSeconds,simulation.CurrentPhase,Origin=new Vector3f(5650,Ground(5650,6850),6850)};
            }

            var local=new UniStormSpatialWeatherController(UniStormWeatherCatalog.LoadDefault(),13,
                new UniStormOptions{AutomaticWeatherEnabled=false,AdvanceInternalClock=false,TransitionSeconds=2});
            EnvironmentSceneState environment=source;
            for(int tick=0;tick<200;tick++)environment=local.Update(new(.05f,42,2026,150,hour,22,camera.Position),source);
            var frame=session.BuildFrame(number,42,0,environment,camera,width,height,particles,history,
                avatarFeet:avatarFeet,snapshot:local.Snapshot,advancePhysics:false);
            if(name=="campaign-city-night"&&((frame.Lights?.Count(light=>light.Id.StartsWith("vehicle-headlight-",StringComparison.Ordinal))??0)!=2
                ||!(frame.Lights?.Any(light=>light.Id.EndsWith("/canopy-lamp",StringComparison.Ordinal))??false)))
                throw new InvalidOperationException("Night service capture requires both actual vehicle headlights and an intact station canopy lamp.");
            if(name=="campaign-city-destruction")
            {
                vehicles.SetInput(0,0,true,frame.Weather,1f/60);
                for(int tick=0;tick<240;tick++)session.Destruction.Update(1f/60,frame.Weather,camera.Position,vehicles.StepForces);
                if(session.Destruction.DestroyedPartCount==0)
                    throw new InvalidOperationException("The city source produced no actual native building failures.");
                frame=session.BuildFrame(number,42,0,environment,camera,width,height,particles,history,
                    snapshot:local.Snapshot,advancePhysics:false);
            }
            // Submit a frozen physical snapshot, leaving only temporal jitter
            // and actual residency/fence completion to advance during warmup.
            int warmups=0,resident=0;
            for(;warmups<256;warmups++)
            {
                surface.PumpMessages();
                await backend.RenderAsync(frame with{FrameNumber=number++},CancellationToken.None);
                resident=backend.LastFrameStatistics.RequiredResourcesResident?resident+1:0;
                if(warmups>=23&&resident>=16)break;
            }
            if(resident<16)throw new InvalidOperationException("Campaign resources did not become consecutively resident: "+name);
            while(number%8!=7)
            {
                surface.PumpMessages();
                await backend.RenderAsync(frame with{FrameNumber=number++},CancellationToken.None);
            }
            ExportCompleted.SetValue(backend,false);
            var watch=Stopwatch.StartNew();var captured=frame with{FrameNumber=number++};
            await backend.RenderAsync(captured,CancellationToken.None);
            validation.CollectErrors(backend,name,waitForCompletion:validation.Enabled);
            var stats=backend.LastFrameStatistics;
            if(!(bool)ExportCompleted.GetValue(backend)!||!stats.RequiredResourcesResident
                ||stats.MissingCatalogResourceCount!=0||stats.InvalidSkinPoseCount!=0||stats.OpaqueBatchCount<=0)
                throw new InvalidOperationException("Incomplete native campaign capture "+name+": "+stats);
            string path=Path.Combine(output,name+".png");File.Copy(staging,path,true);
            using var image=Image.Load<Rgba32>(path);
            var pixels=new byte[width*height*4];image.CopyPixelDataTo(pixels);
            double mean=MeanLuminance(pixels),variation=LuminanceVariation(pixels,mean);
            if(image.Width!=width||image.Height!=height||mean<.001||variation<.000001)
                throw new InvalidDataException("Blank native campaign image: "+name);
            string sha=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
            if(name=="campaign-farm-tornado-photo")
            {
                decimal cashBefore=career.Save.Cash,photoBefore=career.Save.PhotoEarnings;
                int paidBefore=career.Save.PaidPhotos;
                // This receipt references only the completed native readback,
                // after dimensions, pixels, residency and source checks passed.
                career.QueuePhoto(new(path,captured));career.Update(43,0,captured.Weather);
                string receipt=Path.ChangeExtension(path,".reward.json");
                if(career.Save.PaidPhotos!=paidBefore+1||career.Save.PhotoEarnings<=photoBefore||career.Save.Cash<=cashBefore||!File.Exists(receipt))
                    throw new InvalidOperationException("A completed native tornado photograph did not earn its actual gameplay reward: "+career.Message);
                rewards.Add(new{NativePng=path,NativePngSha256=sha,Receipt=receipt,CashBefore=cashBefore,CashAfter=career.Save.Cash,
                    PhotoEarningsBefore=photoBefore,PhotoEarningsAfter=career.Save.PhotoEarnings,PaidPhotosBefore=paidBefore,PaidPhotosAfter=career.Save.PaidPhotos});
            }
            reports.Add(new{Name=name,Path=path,Sha256=sha,Source=provenance,Camera=camera,Hour=hour,
                captured.FrameNumber,captured.AnimationTimeSeconds,WarmupFrames=warmups+1,ConsecutiveResidentFrames=resident,
                CaptureMilliseconds=watch.Elapsed.TotalMilliseconds,MeanLuminance=mean,LuminanceVariation=variation,
                Statistics=stats,GpuTiming=backend.LastGpuTiming,Weather=captured.Weather,Sky=captured.Sky,Lights=captured.Lights,
                SourceStormCount=source.ActiveStorms.Count,RenderedStormCount=captured.Weather?.ActiveStorms.Count??0,
                SourceParents=source.ActiveStorms.Select(storm=>new{storm.Id,storm.Position,storm.Velocity,storm.SmoothedStage,
                    storm.WindSpeed,storm.SmoothedWindSpeed,Tornadoes=storm.Tornadoes.Select(tornado=>new{tornado.Id,tornado.ParentStormId,
                        tornado.Position,tornado.SmoothedWindSpeed,tornado.TouchdownAmount}).ToArray()}).ToArray(),
                NativePhysicsSteps=session.Destruction.PhysicsStepCount,DestroyedParts=session.Destruction.DestroyedPartCount,
                DynamicDebrisBodies=session.Destruction.DynamicBodyCount,ProbeCount=career.ActiveProbeCount,
                VehiclePosition=vehicles.Position,VehicleModel=career.SelectedVehicle.Id,VehicleDriving=vehicles.Driving,
                CameraToSourceCentersMeters=source.ActiveStorms.Select(storm=>new{storm.Id,
                    Distance=MathF.Sqrt((storm.Position.X-camera.Position.X)*(storm.Position.X-camera.Position.X)
                        +(storm.Position.Z-camera.Position.Z)*(storm.Position.Z-camera.Position.Z))}).ToArray(),
                LocalWeather=local.LocalWeatherName,local.StormInfluence});
            Console.WriteLine($"Campaign native capture {name}: {source.ActiveStorms.Count} genuine cells, {stats.OpaqueBatchCount} opaque batches; {path}");
            history++;
        }
        validation.CollectErrors(backend,"campaign final drain",waitForCompletion:validation.Enabled);
        career.Flush();
        string manifest=Path.Combine(output,"campaign-native.json");
        File.WriteAllText(manifest,JsonSerializer.Serialize(new{Schema=1,NativeGpu=true,Backend=backend.Info,Hardware=Hardware(backend),
            Width=width,Height=height,WeatherQualityName=qualityName,options.WeatherQuality,options.WeatherRenderScale,
            EngineAssemblySha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(Dx12RenderBackend).Assembly.Location))),
            World=new{PlayableExtentMeters=StormChaseWorld.PlayableExtentMeters,world.TerrainPageCount,world.BuildingCount,
                DestructibleParts=world.DestructibleItems.Count,world.RoadCount,world.RiverPatchCount,Settlements=world.Settlements,GasStations=world.GasStations},
            IsolatedCareerSave=fixtureSave,Captures=reports,ActualNativePhotoRewards=rewards,Warnings=logger.Warnings,DebugValidation=validation.Snapshot},JsonOptions));
        Console.WriteLine("Native campaign captures passed: "+manifest);
        return 0;
    }

    private static CampaignFourStormCapturePoint FindCampaignFourStormCapturePoint()
    {
        foreach(int seed in Enumerable.Range(1,256).Where(seed=>StormChaseCampaignWeather.CreateForecast(seed,1) is {Opportunities:4,Risk:>=.65f}))
        {
            var director=new StormChaseCampaignWeather(new LiveStormController(["--demo"]),
                ["--campaign-seed",seed.ToString(CultureInfo.InvariantCulture)]);
            for(int seconds=0;seconds<700;seconds++)
            {
                director.Update(1);
                if(director.ActiveStormCount!=4||director.Current.ActiveStorms.Any(storm=>(storm.SmoothedStage??storm.Stage)<2.15f))continue;
                return new(seed,director.ElapsedSeconds,director.Hour,director.Current,director.Day,
                    director.ActiveCells.Select(cell=>new{cell.Number,cell.Region,cell.Seed,cell.PlaybackSpeed,
                        cell.Simulation.BehaviorName,cell.Simulation.CurrentPhase,cell.Simulation.TickCount,
                        cell.Simulation.SimulationSeconds,cell.Simulation.TornadoEpisodeNumber,cell.Simulation.TouchdownCount}).Cast<object>().ToArray(),
                    director.SpawnHistory.ToArray());
            }
        }
        throw new InvalidOperationException("No genuine seeded campaign day produced four concurrently visible source clouds.");
    }

    private static CameraState CampaignWideCamera(EnvironmentSceneState source,StormChaseWorld world,int width,int height)
    {
        var storms=source.ActiveStorms;
        // A low hill west of the western source keeps all four genuine cloud
        // footprints against a horizon, rather than looking down from 9 km.
        float x=storms.Min(storm=>storm.Position.X)-2100,z=storms.Average(storm=>storm.Position.Z);
        var position=new Vector3f(x,world.HeightAt(x,z)+70,z);
        float low=float.PositiveInfinity,high=float.NegativeInfinity;
        foreach(var storm in storms)
        {
            float dx=storm.Position.X-x,dz=storm.Position.Z-z;
            float distance=MathF.Sqrt(dx*dx+dz*dz);
            float bearing=MathF.Atan2(dz,dx);
            float radius=MathF.Asin(Math.Clamp(storm.AnvilRadiusMeters/distance,0,.95f));
            low=Math.Min(low,bearing-radius);high=Math.Max(high,bearing+radius);
        }
        float heading=(low+high)*.5f;
        float span=high-low+6*MathF.PI/180;
        float aspect=(float)width/height;
        float fieldOfView=2*MathF.Atan(MathF.Tan(span*.5f)/aspect)*180/MathF.PI;
        if(fieldOfView>90)
            throw new InvalidOperationException("Four source footprints need a different horizon observer for this capture aspect ratio.");
        float cloudMiddle=storms.Average(storm=>(storm.CloudBaseMeters+storm.CloudTopMeters)*.5f);
        var target=new Vector3f(x+MathF.Cos(heading)*10000,cloudMiddle,z+MathF.Sin(heading)*10000);
        return ChaseCaptureCamera(position,target) with {VerticalFieldOfViewDegrees=Math.Max(58,fieldOfView),FarPlane=36000};
    }

    private sealed record CampaignFourStormCapturePoint(int Seed,double Seconds,float Hour,EnvironmentSceneState Source,
        StormChaseDayForecast Forecast,IReadOnlyList<object> Cells,IReadOnlyList<StormChaseCampaignSpawn> Spawns);
}
