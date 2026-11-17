using System.Diagnostics;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using Engine.Rendering.Assets;
using Engine.Rendering.Backend;
using Engine.Rendering.Configuration;
using Shared.Contracts.Rendering;
using StormChase.Protocol;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

internal static partial class Showcase
{
    /// <summary>Joins a separately owned, explicit isolated native fixture; creates no local online physics authority.</summary>
    private static async Task<int> VerifyStormChaseOnlineAsync(RendererOptions options,NativeSurface surface,
        MutableRenderAssetCatalog catalog,CaptureLogger logger,RenderGpuDebugValidation validation,string output,string staging,string[] args)
    {
        string accountAddress=Value(args,"--accounts-url","http://127.0.0.1:5080"),serverAddress=Value(args,"--server-url","http://127.0.0.1:5180");
        using var timeout=new CancellationTokenSource(TimeSpan.FromMinutes(4));var token=timeout.Token;
        var checks=new List<object>();void Check(bool value,string name)
        {checks.Add(new{Name=name,Passed=value});if(!value)throw new InvalidOperationException("Online proof failed: "+name);}
        using var gameApi=new ChaseApiClient();gameApi.Configure(serverAddress);var metadata=await gameApi.ServerAsync(token);
        var startup=Stopwatch.StartNew();
        while(!metadata.Ready&&startup.Elapsed.TotalSeconds<45)
        {await Task.Delay(200,token);metadata=await gameApi.ServerAsync(token);}
        Check(metadata.Ready,"Real game metadata is ready with native trust");
        // Complete the same heavy content stage before requesting either thirty-second admission ticket.
        using var session=new StormChaseSession(catalog,logger,OptionalValue(args,"--avatar-catalog"),simulateLocally:false);
        session.PrepareOnlinePlayers(2);Check(metadata.WorldContentFingerprint==session.World.ContentFingerprint,"Exact authored world fingerprint matches authority");
        using var accountA=new ChaseApiClient();using var accountB=new ChaseApiClient();
        accountA.Configure(accountAddress);accountB.Configure(accountAddress);
        string suffix=Guid.NewGuid().ToString("N")[..12],password=Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        await accountA.SignInAsync("proofa_"+suffix,password,true,token);await accountB.SignInAsync("proofb_"+suffix,password,true,token);
        var characterA=await accountA.CreateCharacterAsync("Native chaser A",token);var characterB=await accountB.CreateCharacterAsync("Native chaser B",token);
        string refresh=accountA.Login!.RefreshToken;
        accountA.ResumeLogin(accountAddress,"expired-access-token",refresh);
        var recovered=await accountA.CharactersAsync(token);
        Check(recovered.Any(character=>character.Id==characterA.Id)&&accountA.Login!.AccessToken!="expired-access-token",
            "Actual 401 refresh restores the authenticated character without logging credentials");
        using var first=new StormChaseOnlineClient(metadata,await accountA.JoinAsync(characterA.Id,metadata.ServerId,token),session.World.ContentFingerprint);
        using var second=new StormChaseOnlineClient(metadata,await accountB.JoinAsync(characterB.Id,metadata.ServerId,token),session.World.ContentFingerprint);
        await Task.WhenAll(first.Admission,second.Admission).WaitAsync(token);
        await Wait(()=>first.Current!.World.Players.Length>=2&&second.Current!.World.Players.Length>=2);
        Check(first.Current!.PlayerId!=second.Current!.PlayerId,"Two independent accounts own distinct admitted native players");
        Check(first.Current.BaselineReady&&second.Current.BaselineReady,"Both clients wait for complete reliable debris baselines");
        await Wait(()=>first.Current!.World.ServerTick==second.Current!.World.ServerTick);
        var beforeA=first.Current!;var beforeB=second.Current!;
        Check(beforeA.World.Environment is not null&&beforeB.World.Environment is not null,"Both receive full authoritative engine environments");
        Check(beforeA.World.Environment!.ActiveStorms.Count>0,"Explicit fixture supplies a genuine active engine source storm");
        Check(beforeA.World.Environment!.ActiveStorms.Select(cell=>cell.Id).SequenceEqual(beforeB.World.Environment!.ActiveStorms.Select(cell=>cell.Id)),"Both receive the same independently simulated storm identities");
        Check(beforeA.Debris.Count>0&&beforeA.Debris.Count==beforeB.Debris.Count,"Explicit fixture serves nonempty actual native PhysX debris to both clients");
        var bodiesB=beforeB.Debris.ToDictionary(body=>body.BodyId);
        Check(beforeA.Debris.All(body=>!string.IsNullOrWhiteSpace(body.StablePartId)&&bodiesB.TryGetValue(body.BodyId,out var other)&&body.StablePartId==other.StablePartId),"Stable physical body mappings match on both native clients");
        Check(beforeA.Debris.Where(body=>body.Sleeping||body.Archived).All(body=>body.Position==bodiesB[body.BodyId].Position&&body.Rotation==bodiesB[body.BodyId].Rotation),"Actual settled fragment positions and native quaternions match");
        var origin=first.Current.Player!.Position;first.SetInput(0,0,1,0,0,false,false,false);
        await Task.Delay(1200,token);first.SetInput(0,0,0,0,0,false,true,false);
        await Wait(()=>Distance(origin,first.Current!.Player!.Position)>.8f);
        Check(Distance(origin,first.Current!.Player!.Position)>.8f,"Server capsule moves from raw axes rather than client positions");
        await Wait(()=>second.Current!.World.Players.Any(player=>player.PlayerId==first.Current!.PlayerId&&Distance(player.Position,first.Current.Player!.Position)<.6f));
        Check(true,"Second native client observes the confirmed first-player movement");
        // The independently spawned second player remains beside its real native pickup.
        long enter=second.SendAction(GameplayActionKind.EnterVehicle);var enterReceipt=await Receipt(second,enter);
        File.WriteAllText(Path.Combine(output,"online-enter-state.json"),JsonSerializer.Serialize(new{Receipt=enterReceipt,second.Current!.Player,second.Current.Vehicle},JsonOptions));
        Check(enterReceipt.Success,"Actual native enter-vehicle action is confirmed: "+enterReceipt.Message);
        await Wait(()=>second.Current!.Player!.Mode==PlayerMovementMode.Driving);
        Check(first.Current!.World.Vehicles.Any(vehicle=>vehicle.DriverId==second.Current!.PlayerId),"Vehicle occupancy is visible to the other client");
        long exit=second.SendAction(GameplayActionKind.ExitVehicle);Check((await Receipt(second,exit)).Success,"Actual native exit-vehicle action is confirmed");
        await Wait(()=>second.Current!.Player!.Mode==PlayerMovementMode.OnFoot);
        long deploy=second.SendAction(GameplayActionKind.DeployProbe);Check((await Receipt(second,deploy)).Success,"Actual server-owned field probe deploys");
        await Wait(()=>first.Current!.World.Probes.Any(probe=>probe.OwnerId==second.Current!.PlayerId&&probe.Active));
        Check(true,"Probe state is shared with the other native client");

        var captures=new List<object>();await using var backend=new Dx12RenderBackend(options,surface,catalog,logger);await backend.InitializeAsync(token);
        long number=0;
        foreach(var pair in new[]{("native-client-a",first), ("native-client-b",second)})
        {
            var snapshot=pair.Item2.Current!;var feet=snapshot.Player!.Position;
            var target=new Vector3(feet.X,feet.Y+1.6f,feet.Z);var eye=target+new Vector3(-12,6,-18);var direction=Vector3.Normalize(target-eye);
            var camera=new CameraState(new(eye.X,eye.Y,eye.Z),new(direction.X,direction.Y,direction.Z),58,.5f,16000);
            session.ThirdPerson=true;var frame=session.BuildOnlineFrame(number,(float)snapshot.World.SimulationSeconds,0,snapshot,camera,surface.Width,surface.Height,true,100000);
            int resident=0,warm=0;for(;warm<256;warm++)
            {surface.PumpMessages();await backend.RenderAsync(frame with{FrameNumber=number++},token);resident=backend.LastFrameStatistics.RequiredResourcesResident?resident+1:0;if(warm>=23&&resident>=16)break;}
            Check(resident>=16,pair.Item1+" native textures, meshes and skins are consecutively resident");
            while(number%8!=7)await backend.RenderAsync(frame with{FrameNumber=number++},token);
            ExportCompleted.SetValue(backend,false);await backend.RenderAsync(frame with{FrameNumber=number++},token);
            var stats=backend.LastFrameStatistics;
            Check((bool)ExportCompleted.GetValue(backend)!&&stats.MissingCatalogResourceCount==0&&stats.InvalidSkinPoseCount==0&&stats.OpaqueBatchCount>0,pair.Item1+" actual DX12 readback completed without missing resources");
            string path=Path.Combine(output,pair.Item1+".png");File.Copy(staging,path,true);using var image=Image.Load<Rgba32>(path);
            var pixels=new byte[image.Width*image.Height*4];image.CopyPixelDataTo(pixels);double mean=MeanLuminance(pixels);
            Check(mean>.001&&LuminanceVariation(pixels,mean)>.000001,pair.Item1+" native image contains visible scene pixels");
            captures.Add(new{Path=path,Sha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))),NativeDx12=true,
                snapshot.World.ServerTick,snapshot.World.SimulationSeconds,PlayerId=snapshot.PlayerId,PlayerCount=snapshot.World.Players.Length,
                BodyCount=snapshot.Debris.Count,snapshot.BaselineRevision,Storms=snapshot.World.Environment!.ActiveStorms.Select(cell=>new{cell.Id,cell.Position,cell.Stage}).ToArray(),
                frame.Camera,frame.Viewport,Statistics=stats,GpuTiming=backend.LastGpuTiming});
        }
        validation.CollectErrors(backend,"native two-client world proof",true);
        string report=Path.Combine(output,"native-online-proof.json");File.WriteAllText(report,JsonSerializer.Serialize(new{Passed=true,
            Scope="Two actual authenticated native clients against externally owned isolated fixture; zero local client physics/weather authority",
            metadata.ServerId,metadata.WorldContentFingerprint,NativeTrustIdentity=metadata.TransportIdentity,
            Checks=checks,Captures=captures,DebugValidation=validation.Enabled},JsonOptions));
        Console.WriteLine("Native online proof passed: "+checks.Count+" checks; "+report);return 0;

        async Task Wait(Func<bool> condition)
        {var watch=Stopwatch.StartNew();while(!condition()){first.ThrowIfFaulted();second.ThrowIfFaulted();token.ThrowIfCancellationRequested();if(watch.Elapsed.TotalSeconds>12)throw new TimeoutException("Native proof state did not arrive.");await Task.Delay(10,token);}}
        async Task<ActionResult> Receipt(StormChaseOnlineClient client,long id)
        {ActionResult? found=null;await Wait(()=>{foreach(var receipt in client.DrainReceipts())if(receipt.RequestId==id)found=receipt;return found is not null;});return found!;}
        static float Distance(NetVector3 a,NetVector3 b)=>Vector3.Distance(new(a.X,a.Y,a.Z),new(b.X,b.Y,b.Z));
    }
}
