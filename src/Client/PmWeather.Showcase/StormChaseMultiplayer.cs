using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Engine.Environment;
using Engine.Core.Diagnostics;
using Engine.Rendering.Assets;
using Engine.UniStorm;
using Shared.Contracts.Rendering;
using Shared.Contracts.World;
using StormChase.Protocol;

/// <summary>One play session selected by an authenticated frontend; online authority never falls back to local simulation.</summary>
internal sealed class StormChasePlaySession : IDisposable
{
    private readonly UniStormSpatialWeatherController localWeather;
    private readonly StormChaseCampaignWeather? campaign;
    private readonly StormChaseCareer? career;
    private readonly StormChaseVehicles? vehicles;
    private readonly StormChaseOnlineClient? client;
    private RenderWeatherState? previousWeather;
    private Vector3 previousPosition;
    private float localSeconds;
    private string message = "Ready to chase.";
    private int shopSelection;
    private long history = 90000;
    private float? previousVehicleYaw;
    public StormChaseSession Session { get; }
    public bool Online => client is not null;
    public bool ShopOpen { get; set; }
    public bool DispatchOpen { get; set; }
    public bool HousingOpen { get; set; }
    public long HousingCashCents=>client?.Current?.Career.CashCents??(long)((career?.Save.Cash??0)*100);
    public string PlayerName { get; }
    public string Message => client?.Current?.Message ?? career?.Message ?? message;
    public FlyCamera Camera { get; } = new();
    public OnlineChaseState? ServerState => client?.Current;
    public StormChasePhotos Photos { get; }
    /// <summary>LifeCare lots, build mode and world furniture; online through the Gateway only.</summary>
    public StormChaseLotPlay? Lots { get; private set; }
    /// <summary>Chat lines the district delivered; single player has no one to talk to.</summary>
    public IReadOnlyList<ChatMessage> DrainChat() => client?.DrainChat() ?? [];
    public bool SendChat(ChatChannel channel, string text) => client?.SendChat(channel, text) ?? false;

    /// <summary>What the avatar has on them, from authoritative state only. Containers in the world are not included.</summary>
    public HudInventory Inventory()
    {
        var state = client?.Current; var items = new List<HudItem>();
        items.Add(new("phone", "Phone", "Weather, jobs, home and more  ·  P", "phone"));
        string vehicleName = state?.Vehicle is { } v ? StormChaseCareer.Vehicles[Math.Clamp(v.VehicleType, 0, StormChaseCareer.Vehicles.Length - 1)].Name : career?.SelectedVehicle.Name ?? "Vehicle";
        items.Add(new("keys", "Keys", $"{vehicleName}  ·  E to enter", "equipment"));
        int probes = state?.Career.Probes ?? career?.ProbeCapacity ?? 0;
        if (probes > 0) items.Add(new("probes", $"Field probes ×{probes}", "Deploy with B", "equipment"));
        if (Lots?.CarriedItem is { } carried) items.Insert(0, carried);
        return new(Lots?.Carrying ?? "Empty hands", items);
    }
    public void SuspendInput()
    {var facing=Camera.Forward;client?.SetInput(MathF.Atan2(facing.X,facing.Z),MathF.Asin(Math.Clamp(facing.Y,-1,1)),0,0,0,false,true,false);}
    public StormChasePlaySession(StormChaseSession session, StormChaseCareer localCareer,
        StormChaseVehicles localVehicles, StormChaseCampaignWeather localCampaign, string name, string output, string staging)
    {
        Session=session;career=localCareer;vehicles=localVehicles;campaign=localCampaign;PlayerName=name;
        localWeather=new(UniStormWeatherCatalog.LoadDefault(),13,new(){AutomaticWeatherEnabled=false,AdvanceInternalClock=false,TransitionSeconds=2});
        Camera.Grounded=true;Camera.SetSpeed(6);
        Camera.SetPose(new(vehicles.Position.X-3,vehicles.Position.Y+1.72f,vehicles.Position.Z),new(vehicles.Position.X-3,vehicles.Position.Y+1.72f,vehicles.Position.Z+1));
        previousPosition=Camera.Position;
        Photos=new(output,staging,career.QueuePhoto);
    }
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
        StormChaseSession? session=null;StormChaseOnlineClient? online=null;StormChaseVehicles? vehicle=null;
        try
        {
            session=await Task.Run(()=>new StormChaseSession(catalog,logger,Showcase.OptionalGameValue(args,"--avatar-catalog"),simulateLocally:selection.Offline),cancellation);
            if(selection.Offline)
            {
                string identity=selection.Character?.Id??Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes((selection.AccountAddress??"")+"/"+name))).ToLowerInvariant();
                string path=Showcase.OptionalGameValue(args,"--save-path")??Path.Combine(ChaseClientPreferences.DataRoot,"Career","SinglePlayer",identity+".json");
                var career=new StormChaseCareer(session.World,path);
                vehicle=new(session.World,session.Destruction,career);
                session.Career=career;session.Vehicles=vehicle;
                session.Destruction.PersistentPhysicsAnchor=()=>vehicle.Position;
                career.DestroyedBuildings=()=>session.Destruction.DestroyedBuildingIds;career.VehiclePosition=()=>vehicle.Position;
                string[] campaignArgs=["--campaign-seed",career.Save.CampaignSeed.ToString(),"--campaign-start-hour",career.Save.CampaignStartHour.ToString(System.Globalization.CultureInfo.InvariantCulture)];
                var controller=new LiveStormController(campaignArgs);
                var campaign=new StormChaseCampaignWeather(controller,campaignArgs,career.Save.CampaignSeconds);
                career.Save.CampaignSeed=campaign.Seed;career.Save.CampaignStartHour=campaign.StartHour;
                career.BindContractPhysics(session.Destruction);
                career.HousingOwnerName=name;session.ConfigureLocalHousing(career);
                session.CampaignDay=campaign.DayNumber;session.CampaignHour=campaign.Hour;
                await Task.Run(()=>session.EnableNpcs(campaign.Seed,campaign.DayNumber,campaign.Hour,career.SavePath+".npc.gz"),cancellation);
                return new(session,career,vehicle,campaign,name,output,staging);
            }
            if(selection.Server is null||selection.Character is null||selection.ApiAddress is null||selection.AccountAddress is null||selection.AccessToken is null)
                throw new InvalidOperationException("Select an authenticated character and chase server first.");
            var uri=new Uri(selection.ApiAddress);
            if(uri.Scheme!="https"&&!uri.IsLoopback)throw new InvalidOperationException("Remote native trust must be obtained from verified HTTPS.");
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
        catch { online?.Dispose();vehicle?.Dispose();session?.Dispose();throw; }
    }

    public void HandleKey(int key)
    {
        if(Lots?.HandleKey(key)==true)return;
        if(key==0x55){ShopOpen=!ShopOpen;DispatchOpen=false;HousingOpen=false;if(career is not null)career.ShopOpen=ShopOpen;return;}
        if(key==0x4F){DispatchOpen=!DispatchOpen;ShopOpen=false;HousingOpen=false;if(career is not null)career.ShopOpen=false;return;}
        if(key==0x50){HousingOpen=!HousingOpen;ShopOpen=false;DispatchOpen=false;return;}
        if(key==0x26&&ShopOpen){shopSelection=(shopSelection+StormChaseCareer.Offers.Length-1)%StormChaseCareer.Offers.Length;return;}
        if(key==0x28&&ShopOpen){shopSelection=(shopSelection+1)%StormChaseCareer.Offers.Length;return;}
        if(key==0x0D&&ShopOpen){Purchase(shopSelection);return;}
        if(key==0x46){Photos.Request();return;}
        if(key==0x4C){Session.Flashlight=!Session.Flashlight;return;}
        if(key==0x56){Session.ThirdPerson=!Session.ThirdPerson;history++;return;}
        if(key==0x47&&client is null&&career is not null){career.CycleProbeType(localSeconds);return;}
        if(client is not null)
        {
            var state=client.Current!;
            GameplayActionKind? action=key switch {
                0x45=>state.Player?.Mode==PlayerMovementMode.Driving?GameplayActionKind.ExitVehicle:GameplayActionKind.EnterVehicle,
                0x52=>GameplayActionKind.Refuel,0x42=>GameplayActionKind.DeployProbe,0x4A=>GameplayActionKind.RecoverProbe,
                0x58=>GameplayActionKind.RecoverVehicle,0x4B=>GameplayActionKind.ToggleAnchors,0x48=>GameplayActionKind.ContractInteract,_=>null};
            if(action.HasValue)client.SendAction(action.Value);return;
        }
        switch(key)
        {
            case 0x45:vehicles!.Interact(Camera,localSeconds);history++;break;
            case 0x52:vehicles!.ToggleRefuel(localSeconds);break;
            case 0x42:Session.DeployProbe(vehicles!.Driving?vehicles.Position:Camera.ToCameraState().Position);break;
            case 0x4A:career!.RetrieveProbe(Camera.ToCameraState().Position,localSeconds);break;
            case 0x58:vehicles!.Recover(Camera,localSeconds);history++;break;
            case 0x4B:vehicles!.ToggleAnchors(localSeconds);break;
            case 0x48:career!.WorkContract(new(Camera.Position.X,Camera.Position.Y-1.72f,Camera.Position.Z),vehicles!.Position,vehicles.Driving,vehicles.SpeedMph,previousWeather,localSeconds);break;
        }
    }
    public void HandleGuiAction(string action)
    {
        if(action=="contract/close"){DispatchOpen=false;return;}
        if(client is null)
        {
            if(action=="contract/interact")HandleKey(0x48);
            else if(action=="contract/cancel")career!.CancelContract(localSeconds);
            else if(action.StartsWith("contract/accept/",StringComparison.Ordinal)&&int.TryParse(action.AsSpan(16),out int target))career!.AcceptContract(target,localSeconds);
            else if(action.StartsWith("job/",StringComparison.Ordinal))
            {
                var where=vehicles!.Driving?vehicles.Position:Camera.ToCameraState().Position;
                // A new job swaps in its department vehicle (or the player's own) at the station.
                if(career!.SelectJob(action[4..],where,localSeconds))vehicles.SwitchIfChanged(career.NearestStation(where)?.ParkingPosition);
            }
            else if(action.StartsWith("shop/offer/",StringComparison.Ordinal)&&int.TryParse(action.AsSpan(11),out int index))shopSelection=Math.Clamp(index,0,StormChaseCareer.Offers.Length-1);
            else if(action=="shop/buy")Purchase(shopSelection);
            else if(action=="shop/previous")HandleKey(0x26);
            else if(action=="shop/next")HandleKey(0x28);
            return;
        }
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
        if(client is not null)
        {
            if(StormChaseCareer.Offers[index].Id=="pickup")client.SendAction(GameplayActionKind.SelectVehicle,0);
            else client.SendAction(GameplayActionKind.Purchase,index);
        }
        else
        {
            career!.Purchase(StormChaseCareer.Offers[index].Id,vehicles!.Driving?vehicles.Position:Camera.ToCameraState().Position,localSeconds);
            vehicles.SwitchIfChanged(career.NearestStation(Camera.ToCameraState().Position)?.ParkingPosition, true);
            message=career.Message;
        }
    }
    public void HousingAction(GameplayActionKind kind,int target,int slot=-1,float yaw=0)
    {
        if(client is not null){client.SendAction(kind,target,slot,yaw);return;}
        if(Session.FurniturePhysics is not { } furniture)return;
        career!.HousingAction(Session.Housing,furniture,kind,target,slot,yaw,
            new(Camera.Position.X,Camera.Position.Y-1.72f,Camera.Position.Z),vehicles!.Driving,previousWeather,localSeconds,Guid.NewGuid().ToString("N"));
    }

    public RenderFrame Step(NativeSurface surface,long number,float delta,bool paused,bool particles,bool weatherPanelOpen=false)
    {
        bool blocked=paused||ShopOpen||DispatchOpen||HousingOpen||weatherPanelOpen;
        if(client is not null)
        {
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
        float step=paused?0:delta;localSeconds+=step;campaign!.Update(step);Session.CampaignHour=campaign.Hour;Session.CampaignDay=campaign.DayNumber;Session.CampaignWallSeconds=localSeconds;
        var start=Camera.Position;surface.UpdateFlyCamera(Camera,delta,!blocked&&vehicles?.Driving!=true);
        var input=surface.DrivingInput();vehicles!.SetInput(blocked?0:input.Throttle,blocked?0:input.Steering,blocked||input.Brake,previousWeather,step);
        if(vehicles.Driving)Camera.SetPosition(new(vehicles.Position.X,vehicles.Position.Y+2,vehicles.Position.Z));
        else
        {
            float ground=Session.World.HeightAt(Camera.Position.X,Camera.Position.Z);
            var feet=Session.Destruction.ResolveAvatarMovement(new(start.X,start.Y-1.72f,start.Z),new(Camera.Position.X,ground,Camera.Position.Z),step,previousWeather);
            Camera.SetPosition(new(feet.X,feet.Y+1.72f,feet.Z));
        }
        var renderCamera=vehicles.Driving?vehicles.Camera(Camera):Camera.ToCameraState();
        var source=Session.GroundStorms(campaign.Current);
        var environment=localWeather.Update(new(step,localSeconds,2026,150,campaign.Hour,campaign.Atmosphere.Diagnostics.SurfaceTemperatureCelsius,renderCamera.Position),source);
        bool moving=Vector3.DistanceSquared(previousPosition,Camera.Position)>.0004f;previousPosition=Camera.Position;
        var frame=Session.BuildFrame(number,localSeconds,step,environment,renderCamera,surface.Width,surface.Height,particles,history,
            new(Camera.Position.X,Camera.Position.Y-1.72f,Camera.Position.Z),moving,localWeather.Snapshot);
        previousWeather=frame.Weather;career!.Update(localSeconds,campaign.ElapsedSeconds,frame.Weather);
        career.UpdateContracts(step,campaign.DayNumber,new(Camera.Position.X,Camera.Position.Y-1.72f,Camera.Position.Z),vehicles.Position,vehicles.Driving,vehicles.SpeedMph,frame.Weather,localSeconds);return frame;
    }

    public StormChaseTelemetry Telemetry(double fps,double gpu,bool muted)
    {
        var state=client?.Current;var environment=state?.World.Environment??campaign!.Current;
        var storm=environment.ActiveStorms.OrderBy(cell=>Vector2.Distance(new(cell.Position.X,cell.Position.Z),new(Camera.Position.X,Camera.Position.Z))).FirstOrDefault();
        var tornado=storm?.Tornadoes.FirstOrDefault();
        float range=storm is null?0:Vector2.Distance(new(storm.Position.X,storm.Position.Z),new(Camera.Position.X,Camera.Position.Z));
        var vehicle=state?.Vehicle;var probes=state?.World.Probes.Where(probe=>probe.OwnerId==state.PlayerId&&probe.Active)
            .Select(probe=>new ChaseFieldProbe(probe.ProbeId,new(probe.Position.X,probe.Position.Y,probe.Position.Z),0){WindMph=probe.WindMph,PeakMph=probe.WindMph}).ToArray()??career?.Probes.ToArray()??[];
        var telemetry=new StormChaseTelemetry(storm is null?"Quiet day":((storm.SmoothedStage??storm.Stage)>=3?"Tornadic":"Thunderstorm"),
            tornado?.SmoothedWindSpeed??tornado?.WindSpeed??0,tornado?.SmoothedWidthMeters??tornado?.WidthMeters??0,range,
            environment.Weather?.Name??"Clear",0,fps,gpu,state?.Player?.Mode==PlayerMovementMode.Driving||vehicles?.Driving==true?"DRIVING":"ON FOOT",
            Session.LightingName,state?.Debris.Count??Session.Destruction.DestroyedPartCount,probes.Length,state?.Career.Photos??Photos.Count,muted,
            state is null&&career is not null?career.ProbeReading:probes.Length==0?"NO FIELD PROBES":"POD "+probes[^1].Sequence+": "+probes[^1].WindMph.ToString("0")+" MPH",HasFunnel:tornado is not null,
            LocalStorm:Session.Effects.LocalConditions,Gust:Session.Effects.Gust);
        int kind=vehicle?.VehicleType??(career?.SelectedVehicle.Id switch{"suv"=>1,"interceptor"=>2,"chase-truck"=>3,"police-cruiser"=>4,"fire-engine"=>5,"ambulance"=>6,_=>0});
        var spec=StormChaseCareer.Vehicles[kind];
        var position=vehicle is null?vehicles!.Position:new Vector3f(vehicle.Position.X,vehicle.Position.Y,vehicle.Position.Z);
        var atmosphere=state?.World.Atmosphere??campaign?.Atmosphere;
        string forecast=atmosphere?.ThreeDayForecast.FirstOrDefault()?.Name??(Online?"SERVER FORECAST":campaign!.ForecastName);
        string forecastSummary=atmosphere is{} observed?$"CAPE {observed.Diagnostics.CapeJkg:0} J/KG  CIN {observed.Diagnostics.CinJkg:0}  SRH {observed.Diagnostics.Srh01KmM2S2:0}  STP {observed.Diagnostics.Stp:0.0}":"Forecast unavailable";
        return telemetry with{Campaign=new(atmosphere?.DayNumber??campaign?.DayNumber??1,environment.TimeOfDayHours,forecast,
            forecastSummary,atmosphere?.Diagnostics.TornadoPotential01??0,environment.ActiveStorms.Count,
            state is null?career!.Save.Cash:state.Career.CashCents/100m,spec.Name,vehicle is null?vehicles!.SpeedMph:new Vector2(vehicle.Velocity.X,vehicle.Velocity.Z).Length()/.44704f,
            vehicle?.FuelLiters??career!.Condition.FuelLiters,spec.FuelCapacityLiters,vehicle?.Health??career!.Condition.Health,vehicle?.Anchored??vehicles!.Anchored,
            ShopOpen,shopSelection,Message,career?.Refueling??false,Session.World.Settlements,Session.World.GasStations,probes,position,Session.World.RoadRoutes,
            Popularity:state?.Career.Popularity??career?.Save.Popularity??0,Mission:state?.World.Mission??career?.DispatchMission,Activities:state?.World.Activities??career?.DispatchActivities,DispatchOpen:DispatchOpen,Atmosphere:atmosphere,
            Boundary:(state?.World.Seed??campaign?.Seed) is int boundarySeed&&(atmosphere?.DayNumber??campaign?.DayNumber) is int boundaryDay
                ?StormChaseAtmosphere.ActiveBoundary(boundarySeed,boundaryDay,Math.Clamp(environment.TimeOfDayHours,0,23.999f)):null,
            // Online, the server-issued department vehicle (types 4-6) identifies the job.
            Seed:state?.World.Seed??campaign?.Seed,
            Job:state is null?career?.Save.Contracts.Job??StormChaseJobs.Chaser:vehicle?.VehicleType switch{4=>StormChaseJobs.Police,5=>StormChaseJobs.Fire,6=>StormChaseJobs.Ems,_=>StormChaseJobs.Chaser})};
    }
    public void Dispose(){Lots?.Dispose();client?.Dispose();if(career is not null&&Session.FurniturePhysics is { } furniture)career.SaveFurniturePoses(furniture.States);career?.Flush();vehicles?.Dispose();Session.Dispose();}
}
