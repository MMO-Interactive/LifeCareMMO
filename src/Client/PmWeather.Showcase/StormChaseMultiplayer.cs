using System.Numerics;
using Engine.Environment;
using Engine.Core.Diagnostics;
using Engine.Rendering.Assets;
using Engine.UniStorm;
using Shared.Contracts.Rendering;
using Shared.Contracts.World;
using StormChase.Protocol;

/// <summary>One play session selected by an authenticated frontend. Every chase is server-authoritative:
/// weather, career, vehicles, debris and jobs come from the district server and nothing is simulated locally.</summary>
internal sealed class StormChasePlaySession : IDisposable
{
    private readonly UniStormSpatialWeatherController localWeather;
    private readonly StormChaseOnlineClient client;
    private float localSeconds;
    private int shopSelection;
    private long history = 90000;
    private float? previousVehicleYaw;
    public StormChaseSession Session { get; }
    public bool ShopOpen { get; set; }
    public bool DispatchOpen { get; set; }
    public bool HousingOpen { get; set; }
    public long HousingCashCents=>client.Current?.Career.CashCents??0;
    public string PlayerName { get; }
    public string Message => client.Current?.Message ?? "Ready to chase.";
    public FlyCamera Camera { get; } = new();
    public OnlineChaseState? ServerState => client.Current;
    public StormChasePhotos Photos { get; }
    /// <summary>LifeCare lots, build mode and world furniture; through the Gateway only.</summary>
    public StormChaseLotPlay? Lots { get; private set; }
    /// <summary>Chat lines the district delivered.</summary>
    public IReadOnlyList<ChatMessage> DrainChat() => client.DrainChat();
    public bool SendChat(ChatChannel channel, string text) => client.SendChat(channel, text);

    /// <summary>What the avatar has on them, from authoritative state only. Containers in the world are not included.</summary>
    public HudInventory Inventory()
    {
        var state = client.Current; var items = new List<HudItem>();
        items.Add(new("phone", "Phone", "Weather, jobs, home and more  ·  P", "phone"));
        string vehicleName = state?.Vehicle is { } v ? StormChaseCareer.Vehicles[Math.Clamp(v.VehicleType, 0, StormChaseCareer.Vehicles.Length - 1)].Name : "Vehicle";
        items.Add(new("keys", "Keys", $"{vehicleName}  ·  E to enter", "equipment"));
        int probes = state?.Career.Probes ?? 0;
        if (probes > 0) items.Add(new("probes", $"Field probes ×{probes}", "Deploy with B", "equipment"));
        if (Lots?.CarriedItem is { } carried) items.Insert(0, carried);
        return new(Lots?.Carrying ?? "Empty hands", items);
    }
    public void SuspendInput()
    {var facing=Camera.Forward;client.SetInput(MathF.Atan2(facing.X,facing.Z),MathF.Asin(Math.Clamp(facing.Y,-1,1)),0,0,0,false,true,false);}
    private StormChasePlaySession(StormChaseSession session,StormChaseOnlineClient online,string name,string output,string staging)
    {
        Session=session;client=online;PlayerName=name;
        localWeather=new(UniStormWeatherCatalog.LoadDefault(),13,new(){AutomaticWeatherEnabled=false,AdvanceInternalClock=false,TransitionSeconds=2});
        Camera.SetSpeed(6);
        var player=online.Current!.Player??throw new InvalidDataException("The admitted player was absent from the server world.");
        var feet=player.Position;Camera.SetPose(new(feet.X,feet.Y+1.72f,feet.Z),new(feet.X+MathF.Sin(player.Yaw),feet.Y+1.72f,feet.Z+MathF.Cos(player.Yaw)));
        // Only a completed native renderer export may request a server-authoritative photo reward.
        Photos=new(output,staging,_=>online.SendAction(GameplayActionKind.TakePhoto));
    }

    public static async Task<StormChasePlaySession> LoadAsync(MutableRenderAssetCatalog catalog,IRuntimeLogger logger,
        ChaseFrontendSelection selection,string name,string output,string staging,string[] args,CancellationToken cancellation)
    {
        if(selection.Server is null||selection.Character is null||selection.ApiAddress is null||selection.AccountAddress is null||selection.AccessToken is null)
            throw new InvalidOperationException("Select an authenticated character and chase server first.");
        var uri=new Uri(selection.ApiAddress);
        if(uri.Scheme!="https"&&!uri.IsLoopback)throw new InvalidOperationException("Remote native trust must be obtained from verified HTTPS.");
        StormChaseSession? session=null;StormChaseOnlineClient? online=null;
        try
        {
            // No local destruction, weather, career or vehicles: the server owns all of them.
            session=await Task.Run(()=>new StormChaseSession(catalog,logger,Showcase.OptionalGameValue(args,"--avatar-catalog"),simulateLocally:false),cancellation);
            await Task.Run(()=>session.PrepareOnlinePlayers(selection.Server.MaxPlayers),cancellation);
            using var gameApi=new ChaseApiClient();gameApi.Configure(selection.ApiAddress);
            var server=await gameApi.ServerAsync(cancellation);
            if(server.ServerId!=selection.Server.ServerId)throw new InvalidOperationException("The selected server identity changed. Return to the browser.");
            using var accounts=new ChaseApiClient();accounts.ResumeLogin(selection.AccountAddress,selection.AccessToken,selection.RefreshToken);
            // All expensive assets are ready before this short-lived, single-use, server-bound ticket is issued.
            var ticket=await accounts.JoinAsync(selection.Character.Id,server.ServerId,cancellation);
            online=new(server,ticket,session.World.ContentFingerprint);
            await online.Admission.WaitAsync(TimeSpan.FromSeconds(25),cancellation);
            _=new StormChaseLotRenderer(catalog);
            var lots=new StormChaseLotPlay(selection.AccountAddress,selection.AccessToken,selection.Character.Id,session.World,selection.RefreshToken);
            session.LotItems=lots.RenderItems;session.LotDebrisPart=id=>lots.Piece(id) is { } piece?StormChaseLotRenderer.Item(piece):null;
            return new(session,online,name,output,staging){Lots=lots};
        }
        catch { online?.Dispose();session?.Dispose();throw; }
    }

    public void HandleKey(int key)
    {
        if(Lots?.HandleKey(key)==true)return;
        if(key==0x55){ShopOpen=!ShopOpen;DispatchOpen=false;HousingOpen=false;return;}
        if(key==0x4F){DispatchOpen=!DispatchOpen;ShopOpen=false;HousingOpen=false;return;}
        if(key==0x50){HousingOpen=!HousingOpen;ShopOpen=false;DispatchOpen=false;return;}
        if(key==0x26&&ShopOpen){shopSelection=(shopSelection+StormChaseCareer.Offers.Length-1)%StormChaseCareer.Offers.Length;return;}
        if(key==0x28&&ShopOpen){shopSelection=(shopSelection+1)%StormChaseCareer.Offers.Length;return;}
        if(key==0x0D&&ShopOpen){Purchase(shopSelection);return;}
        if(key==0x46){Photos.Request();return;}
        if(key==0x4C){Session.Flashlight=!Session.Flashlight;return;}
        if(key==0x56){Session.ThirdPerson=!Session.ThirdPerson;history++;return;}
        var state=client.Current!;
        GameplayActionKind? action=key switch {
            0x45=>state.Player?.Mode==PlayerMovementMode.Driving?GameplayActionKind.ExitVehicle:GameplayActionKind.EnterVehicle,
            0x52=>GameplayActionKind.Refuel,0x42=>GameplayActionKind.DeployProbe,0x4A=>GameplayActionKind.RecoverProbe,
            0x58=>GameplayActionKind.RecoverVehicle,0x4B=>GameplayActionKind.ToggleAnchors,0x48=>GameplayActionKind.ContractInteract,_=>null};
        if(action.HasValue)client.SendAction(action.Value);
    }
    public void HandleGuiAction(string action)
    {
        if(action=="contract/close"){DispatchOpen=false;return;}
        if(action.StartsWith("shop/offer/",StringComparison.Ordinal)&&int.TryParse(action.AsSpan(11),out int offer)){shopSelection=Math.Clamp(offer,0,StormChaseCareer.Offers.Length-1);return;}
        if(action=="shop/buy"){Purchase(shopSelection);return;}
        if(action=="shop/previous"){HandleKey(0x26);return;}if(action=="shop/next"){HandleKey(0x28);return;}
        if(action=="contract/interact")client.SendAction(GameplayActionKind.ContractInteract);
        else if(action=="contract/cancel")client.SendAction(GameplayActionKind.CancelContract);
        else if(action.StartsWith("contract/accept/",StringComparison.Ordinal)&&int.TryParse(action.AsSpan(16),out int target))
            client.SendAction(GameplayActionKind.AcceptContract,target);
        else if(action.StartsWith("job/",StringComparison.Ordinal)&&StormChaseJobs.WireIndex(action[4..]) is int job and >=0)
            client.SendAction(GameplayActionKind.SelectJob,job);
    }
    public void Purchase(int index)
    {
        if(StormChaseCareer.Offers[index].Id=="pickup")client.SendAction(GameplayActionKind.SelectVehicle,0);
        else client.SendAction(GameplayActionKind.Purchase,index);
    }
    public void HousingAction(GameplayActionKind kind,int target,int slot=-1,float yaw=0)=>client.SendAction(kind,target,slot,yaw);

    public RenderFrame Step(NativeSurface surface,long number,float delta,bool paused,bool particles,bool weatherPanelOpen=false)
    {
        bool blocked=paused||ShopOpen||DispatchOpen||HousingOpen||weatherPanelOpen;
        client.ThrowIfFaulted();var state=client.Current??throw new IOException("Server world is unavailable.");
        var player=state.Player??throw new IOException("Server removed the admitted player.");
        bool building=Lots?.BuildMode==true;
        if(building)Lots!.AddBuildWheel(surface.TakeWheelNotches());   // the build camera zooms with the wheel
        surface.UpdateFlyCamera(Camera,delta,false);
        var axes=surface.ChaseInput();
        if(building)blocked=true;                                       // the avatar stays put while the build camera flies
        if(player.Mode==PlayerMovementMode.Driving&&state.Vehicle is { } headingVehicle)
        {
            if(previousVehicleYaw is { } previousYaw&&!blocked)
                Camera.Look(MathF.IEEERemainder(headingVehicle.Yaw-previousYaw,MathF.Tau)/.0025f,0);
            previousVehicleYaw=headingVehicle.Yaw;
        }
        else previousVehicleYaw=null;
        var facing=Camera.Forward;
        client.SetInput(MathF.Atan2(facing.X,facing.Z),MathF.Asin(Math.Clamp(facing.Y,-1,1)),blocked?0:axes.Forward,
            blocked?0:axes.Strafe,blocked?0:axes.Strafe,!blocked&&axes.Sprint,blocked||axes.Brake,!blocked&&axes.Jump);
        Camera.SetPosition(new(player.Position.X,player.Position.Y+1.72f,player.Position.Z));
        var camera=Camera.ToCameraState();
        if(player.Mode==PlayerMovementMode.Driving&&state.Vehicle is { } vehicle)
        {
            var pivot=new Vector3(vehicle.Position.X,vehicle.Position.Y+2.2f,vehicle.Position.Z);
            var position=pivot-Camera.Forward*7;position.Y=Math.Max(position.Y,Session.World.HeightAt(position.X,position.Z)+1);
            camera=camera with{Position=new(position.X,position.Y,position.Z),VerticalFieldOfViewDegrees=66};
        }
        if(building)camera=Lots!.BuildCameraState;
        if(Lots is { } lots)
        {
            lots.Tick(localSeconds+=delta);
            var clicks=surface.DrainWorldClicks();
            lots.Update(camera,new(player.Position.X,player.Position.Y,player.Position.Z),blocked?Array.Empty<WorldClick>():clicks,surface.Width,surface.Height);
        }
        foreach(var result in client.DrainReceipts())Console.WriteLine("Online action "+result.RequestId+": "+(result.Success?"confirmed":"rejected")+"; "+result.Message);
        var onlineSource=state.World.Environment!;
        var onlineEnvironment=localWeather.Update(new(delta,(float)state.World.SimulationSeconds,2026,150,onlineSource.TimeOfDayHours,
            state.World.Atmosphere?.Diagnostics.SurfaceTemperatureCelsius??22,camera.Position),onlineSource);
        return Session.BuildOnlineFrame(number,(float)state.World.SimulationSeconds,delta,state,camera,surface.Width,surface.Height,particles,history,client.DrainPowerFlashes(),onlineEnvironment);
    }

    public StormChaseTelemetry Telemetry(double fps,double gpu,bool muted)
    {
        var state=client.Current??throw new IOException("Server world is unavailable.");
        var environment=state.World.Environment??throw new IOException("Server environment is unavailable.");
        var storm=environment.ActiveStorms.OrderBy(cell=>Vector2.Distance(new(cell.Position.X,cell.Position.Z),new(Camera.Position.X,Camera.Position.Z))).FirstOrDefault();
        var tornado=storm?.Tornadoes.FirstOrDefault();
        float range=storm is null?0:Vector2.Distance(new(storm.Position.X,storm.Position.Z),new(Camera.Position.X,Camera.Position.Z));
        var vehicle=state?.Vehicle;var probes=state?.World.Probes.Where(probe=>probe.OwnerId==state.PlayerId&&probe.Active)
            .Select(probe=>new ChaseFieldProbe(probe.ProbeId,new(probe.Position.X,probe.Position.Y,probe.Position.Z),0){WindMph=probe.WindMph,PeakMph=probe.WindMph}).ToArray()??[];
        var telemetry=new StormChaseTelemetry(storm is null?"Quiet day":((storm.SmoothedStage??storm.Stage)>=3?"Tornadic":"Thunderstorm"),
            tornado?.SmoothedWindSpeed??tornado?.WindSpeed??0,tornado?.SmoothedWidthMeters??tornado?.WidthMeters??0,range,
            environment.Weather?.Name??"Clear",0,fps,gpu,state?.Player?.Mode==PlayerMovementMode.Driving?"DRIVING":"ON FOOT",
            Session.LightingName,state?.Debris.Count??0,probes.Length,state?.Career.Photos??Photos.Count,muted,
            probes.Length==0?"NO FIELD PROBES":"POD "+probes[^1].Sequence+": "+probes[^1].WindMph.ToString("0")+" MPH",HasFunnel:tornado is not null,
            LocalStorm:Session.Effects.LocalConditions,Gust:Session.Effects.Gust);
        int kind=Math.Clamp(vehicle?.VehicleType??0,0,StormChaseCareer.Vehicles.Length-1);
        var spec=StormChaseCareer.Vehicles[kind];
        var feet=state?.Player?.Position;
        var position=vehicle is not null?new Vector3f(vehicle.Position.X,vehicle.Position.Y,vehicle.Position.Z)
            :feet is { } p?new Vector3f(p.X,p.Y,p.Z):new Vector3f(Camera.Position.X,Camera.Position.Y-1.72f,Camera.Position.Z);
        var atmosphere=state?.World.Atmosphere;
        string forecast=atmosphere?.ThreeDayForecast.FirstOrDefault()?.Name??"SERVER FORECAST";
        string forecastSummary=atmosphere is{} observed?$"CAPE {observed.Diagnostics.CapeJkg:0} J/KG  CIN {observed.Diagnostics.CinJkg:0}  SRH {observed.Diagnostics.Srh01KmM2S2:0}  STP {observed.Diagnostics.Stp:0.0}":"Forecast unavailable";
        return telemetry with{Campaign=new(atmosphere?.DayNumber??1,environment.TimeOfDayHours,forecast,
            forecastSummary,atmosphere?.Diagnostics.TornadoPotential01??0,environment.ActiveStorms.Count,
            (state?.Career.CashCents??0)/100m,spec.Name,vehicle is null?0:new Vector2(vehicle.Velocity.X,vehicle.Velocity.Z).Length()/.44704f,
            vehicle?.FuelLiters??0,spec.FuelCapacityLiters,vehicle?.Health??0,vehicle?.Anchored??false,
            ShopOpen,shopSelection,Message,false,Session.World.Settlements,Session.World.GasStations,probes,position,Session.World.RoadRoutes,
            Popularity:state?.Career.Popularity??0,Mission:state?.World.Mission,Activities:state?.World.Activities,DispatchOpen:DispatchOpen,Atmosphere:atmosphere,
            Boundary:state?.World.Seed is int boundarySeed&&atmosphere?.DayNumber is int boundaryDay
                ?StormChaseAtmosphere.ActiveBoundary(boundarySeed,boundaryDay,Math.Clamp(environment.TimeOfDayHours,0,23.999f)):null,
            Seed:state?.World.Seed,
            // The server-issued department vehicle (types 4-6) identifies the job.
            Job:vehicle?.VehicleType switch{4=>StormChaseJobs.Police,5=>StormChaseJobs.Fire,6=>StormChaseJobs.Ems,_=>StormChaseJobs.Chaser})};
    }
    public void Dispose(){Lots?.Dispose();client.Dispose();Session.Dispose();}
}
