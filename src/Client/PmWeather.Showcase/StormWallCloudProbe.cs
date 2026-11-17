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
    private static List<WallCloudSourcePoint> FindWallCloudSourcePoints()
    {
        var simulation=new PmWeatherSupercellSimulation(LiveStormController.CreateChaseOptions(3,13,variedLifecycle:true));
        var points=new Dictionary<string,WallCloudSourcePoint>();
        double recycleStarted=-1;
        void Save(string name)
        {
            if(points.ContainsKey(name))return;
            points.Add(name,new(name,simulation.Current,simulation.SimulationSeconds,simulation.TickCount,
                simulation.CurrentPhase,simulation.TornadoEpisodeNumber,simulation.TouchdownCount));
        }
        for(int tick=0;tick<200_000&&!simulation.IsComplete;tick++)
        {
            var parent=simulation.Current.ActiveStorms.Single();
            var tornado=parent.Tornadoes.FirstOrDefault();
            if(simulation.TornadoEpisodeNumber==0 && simulation.CurrentPhase=="WallCloud"
                && parent.SmoothedStage>=2.8f && tornado is null)
                Save("wall-01-before-funnel");
            if(simulation.TornadoEpisodeNumber==1)
            {
                if(simulation.CurrentPhase=="Forming" && tornado is {SmoothedWindSpeed:>=15})
                    Save("wall-02-first-visible-funnel");
                if(simulation.CurrentPhase=="Forming" && tornado is {TouchdownAmount:>=.8f and <.9f})
                    Save("wall-03-before-touchdown");
                if(simulation.CurrentPhase=="Mature" && tornado is {TouchdownAmount:>=.999f})
                    Save("wall-04-mature-touchdown");
                if(simulation.CurrentPhase=="Rope" && tornado is {SmoothedWindSpeed:>=4 and <=20})
                    Save("wall-05-rope");
                if(simulation.CurrentPhase=="Recycling")
                {
                    if(recycleStarted<0)recycleStarted=simulation.SimulationSeconds;
                    if(simulation.SimulationSeconds-recycleStarted>=10 && tornado is null)
                        Save("wall-06-recycling");
                }
            }
            if(points.Count==6)break;
            simulation.Advance(.05);
        }
        if(points.Count!=6)
            throw new InvalidOperationException("The actual varied seed13 source did not reach all six wall-cloud phases. Found: "+string.Join(", ",points.Keys));
        return points.Values.OrderBy(p=>p.Seconds).ToList();
    }

    private static int VerifyWallCloudSource(string output)
    {
        var points=FindWallCloudSourcePoints();
        var checks=new List<string>();
        void Check(bool valid,string message)
        {
            if(!valid)throw new InvalidOperationException(message);
            checks.Add(message);
        }
        Check(points.Count==6 && points.Select(p=>p.Name).Distinct().Count()==6,"Six genuine source phases were found in chronological order.");
        string id=points[0].State.ActiveStorms.Single().Id;
        Check(points.All(p=>p.State.ActiveStorms.Count==1&&p.State.ActiveStorms.Single().Id==id),
            "One persistent parent storm supplies every wall-cloud comparison.");
        Check(points.All(p=>p.State.ActiveStorms.Single().WallCloudAmount>.5f
            && p.State.Clouds.VolumetricDensity>.01f),
            "The source keeps its wall-cloud amount and parent cloud density throughout funnel formation, maturity, rope and recycling.");
        Check(points[0].State.ActiveStorms.Single().Tornadoes.Count==0
            && points[1].State.ActiveStorms.Single().Tornadoes.Count==1
            && points[^1].State.ActiveStorms.Single().Tornadoes.Count==0,
            "The source genuinely changes empty child list to a funnel and back to empty during recycling.");
        Check(points.Skip(1).Take(4).All(p=>p.State.ActiveStorms.Single().Tornadoes.Single().ParentStormId==id),
            "The four funnel phases retain the same source tornado parent.");
        var builder=new DefaultRenderFrameBuilder();
        foreach(var point in points)
        {
            var parent=point.State.ActiveStorms.Single();
            var camera=ChaseCaptureCamera(parent.Position+new Vector3f(-450,35,-1600),parent.Position+new Vector3f(0,600,0));
            var frame=builder.Build(point.Tick,DateTimeOffset.UnixEpoch.AddSeconds(point.Seconds),new(1280,720),
                new RenderScene{Camera=camera,Environment=point.State,AnimationTimeSeconds=42});
            var mapped=frame.Weather!.ActiveStorms.Single();
            Check(mapped.Id==id && mapped.Tornadoes.Count==parent.Tornadoes.Count
                && mapped.SmoothedStage==parent.SmoothedStage && mapped.WallCloudAmount==parent.WallCloudAmount,
                "The native frame builder preserves source wall-cloud/child state for "+point.Name+".");
        }
        Directory.CreateDirectory(output);
        string path=Path.Combine(output,"wall-cloud-source.json");
        File.WriteAllText(path,JsonSerializer.Serialize(new{Schema=1,Passed=checks.Count,Checks=checks,
            SourceSeed=13,VariedLifecycle=true,SourceTickRate=20,Points=points.Select(WallCloudPointReport)},JsonOptions));
        Console.WriteLine($"Wall-cloud source checks passed: {checks.Count}; {path}");
        return 0;
    }

    private static object WallCloudPointReport(WallCloudSourcePoint point)
    {
        var parent=point.State.ActiveStorms.Single();
        var tornado=parent.Tornadoes.FirstOrDefault();
        return new{point.Name,point.Seconds,point.Tick,point.Phase,point.Episode,point.Touchdowns,
            ParentId=parent.Id,ParentPosition=parent.Position,parent.SmoothedStage,parent.WallCloudAmount,
            parent.CoreRadiusMeters,parent.UpdraftRadiusMeters,parent.AnvilRadiusMeters,parent.CloudBaseMeters,
            parent.WidthMeters,parent.SmoothedWidthMeters,parent.WindSpeed,parent.SmoothedWindSpeed,
            CloudDensity=point.State.Clouds.VolumetricDensity,TornadoCount=parent.Tornadoes.Count,
            Tornado=tornado};
    }

    /// <summary>Matched source snapshots for diagnosing a wall cloud disappearing when its first child is submitted.</summary>
    private static async Task<int> RunWallCloudCaptureAsync(RendererOptions options,NativeSurface surface,
        MutableRenderAssetCatalog catalog,CaptureLogger logger,RenderGpuDebugValidation validation,
        bool particles,string qualityName,string output,string staging,int width,int height,string[] args)
    {
        var points=FindWallCloudSourcePoints();
        using var session=new StormChaseSession(catalog,logger,OptionalValue(args,"--avatar-catalog"));
        await using var backend=new Dx12RenderBackend(options,surface,catalog,logger);
        await backend.InitializeAsync(CancellationToken.None);
        validation.CollectErrors(backend,"wall-cloud capture initialization");
        long frameNumber=0,history=50000;
        var reports=new List<object>();
        var images=new Dictionary<string,byte[]>();
        foreach(var point in points)
        {
            var source=session.GroundStorms(point.State);
            var parent=source.ActiveStorms.Single();
            // Keep the same view direction, elevation and distance from the
            // actual moving parent in both baseline and repaired captures.
            var camera=ChaseCaptureCamera(parent.Position+new Vector3f(-450,35,-1600),
                parent.Position+new Vector3f(0,600,0));
            var local=new UniStormSpatialWeatherController(UniStormWeatherCatalog.LoadDefault(),13,
                new UniStormOptions{AutomaticWeatherEnabled=false,AdvanceInternalClock=false,TransitionSeconds=2});
            EnvironmentSceneState environment=source;
            for(int tick=0;tick<200;tick++)environment=local.Update(new(.05f,42,2026,150,15,22,camera.Position),source);
            if(!ReferenceEquals(environment.Storms,source.Storms)||!ReferenceEquals(environment.Clouds,source.Clouds))
                throw new InvalidOperationException("Local atmosphere replaced the actual parent cloud state: "+point.Name);
            var frame=session.BuildFrame(frameNumber,42,0,environment,camera,width,height,particles,history,
                snapshot:local.Snapshot,advancePhysics:false);
            var mapped=frame.Weather!.ActiveStorms.Single();
            if(mapped.Id!=parent.Id||mapped.Tornadoes.Count!=parent.Tornadoes.Count)
                throw new InvalidOperationException("Wall-cloud capture altered source parent/child mapping: "+point.Name);
            int warmups=0,resident=0;
            for(;warmups<128;warmups++)
            {
                surface.PumpMessages();
                await backend.RenderAsync(frame with{FrameNumber=frameNumber++},CancellationToken.None);
                resident=backend.LastFrameStatistics.RequiredResourcesResident?resident+1:0;
                if(warmups>=23&&resident>=16)break;
            }
            while(frameNumber%8!=7)
            {
                surface.PumpMessages();
                await backend.RenderAsync(frame with{FrameNumber=frameNumber++},CancellationToken.None);
            }
            ExportCompleted.SetValue(backend,false);
            var watch=Stopwatch.StartNew();
            var captured=frame with{FrameNumber=frameNumber++};
            await backend.RenderAsync(captured,CancellationToken.None);
            validation.CollectErrors(backend,point.Name,waitForCompletion:validation.Enabled);
            var stats=backend.LastFrameStatistics;
            if(!(bool)ExportCompleted.GetValue(backend)!||!stats.RequiredResourcesResident
                ||stats.MissingCatalogResourceCount!=0||stats.InvalidSkinPoseCount!=0||stats.OpaqueBatchCount<=0)
                throw new InvalidOperationException("Incomplete native wall-cloud capture "+point.Name+": "+stats);
            string path=Path.Combine(output,point.Name+".png");
            File.Copy(staging,path,true);
            using var image=Image.Load<Rgba32>(path);
            var pixels=new byte[width*height*4];image.CopyPixelDataTo(pixels);
            double mean=MeanLuminance(pixels),variation=LuminanceVariation(pixels,mean);
            if(image.Width!=width||image.Height!=height||mean<.001||variation<.000001)
                throw new InvalidDataException("Blank native wall-cloud frame: "+point.Name);
            images[point.Name]=pixels;
            reports.Add(new{point.Name,Path=path,Source=WallCloudPointReport(point),Camera=camera,
                CameraOffset=new Vector3f(-450,35,-1600),CameraTargetOffset=new Vector3f(0,600,0),
                captured.FrameNumber,captured.AnimationTimeSeconds,WarmupFrames=warmups+1,
                CaptureMilliseconds=watch.Elapsed.TotalMilliseconds,MeanLuminance=mean,LuminanceVariation=variation,
                Statistics=stats,GpuTiming=backend.LastGpuTiming,Weather=captured.Weather,Sky=captured.Sky,
                LocalWeather=local.LocalWeatherName,local.StormInfluence,
                Sha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))});
            Console.WriteLine($"Wall-cloud native capture {point.Name}: stage={parent.SmoothedStage:F3}, wind={parent.SmoothedWindSpeed:F2}, children={parent.Tornadoes.Count}; {path}");
            history++;
        }
        validation.CollectErrors(backend,"wall-cloud final drain",waitForCompletion:validation.Enabled);
        string manifest=Path.Combine(output,"wall-cloud-native.json");
        File.WriteAllText(manifest,JsonSerializer.Serialize(new
        {
            Schema=1,NativeGpu=true,SourceSeed=13,VariedLifecycle=true,MatchedTextureSeconds=42,
            Backend=backend.Info,Hardware=Hardware(backend),Width=width,Height=height,
            WeatherQualityName=qualityName,options.WeatherQuality,options.WeatherRenderScale,
            EngineAssemblySha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(Dx12RenderBackend).Assembly.Location))),
            Captures=reports,ConsecutiveDifferences=points.Zip(points.Skip(1),(a,b)=>new
            {From=a.Name,To=b.Name,MeanAbsoluteRgbDifference=PixelDifference(images[a.Name],images[b.Name])}),
            Warnings=logger.Warnings,DebugValidation=validation.Snapshot
        },JsonOptions));
        Console.WriteLine("Six matched genuine-source wall-cloud captures passed: "+manifest);
        return 0;
    }

    private sealed record WallCloudSourcePoint(string Name,EnvironmentSceneState State,double Seconds,
        int Tick,string Phase,int Episode,int Touchdowns);
}
