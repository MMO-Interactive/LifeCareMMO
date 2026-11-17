using System.Diagnostics;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using Engine.Environment;
using Engine.Physics.PhysX;
using Engine.Rendering.Assets;
using Engine.Rendering.Pipeline;
using Engine.Rendering.Scene;
using Shared.Contracts.Rendering;
using Shared.Contracts.World;
using StormChase.Networking;
using StormChase.Protocol;

namespace StormChase.Server;

/// <summary>One headless authoritative weather and native PhysX world. Clients send inputs, never final debris poses.</summary>
public sealed partial class GameAuthority:BackgroundService
{
    private const float TickDelta=.05f;
    private readonly ServerOptions options;
    private readonly CareerStore store;
    private readonly object gate=new();
    private readonly string bootId=Guid.NewGuid().ToString("N");
    private readonly Dictionary<string,Admission> admissions=[];
    private readonly Dictionary<uint,Player> players=[];
    private readonly Dictionary<uint,DateTimeOffset> awaitingJoin=[];
    private readonly Dictionary<string,int> stablePartIds=new(StringComparer.Ordinal);
    private readonly Dictionary<string,DebrisBodyState> publishedParts=new(StringComparer.Ordinal);
    private readonly List<Probe> probes=[];
    private readonly DefaultRenderFrameBuilder frameBuilder=new();
    private readonly IClimateFeed weather;
    private StormChaseWorld? world;
    private StormChaseDestruction? destruction;
    private StormChaseContracts? contracts;
    private static readonly JsonSerializerOptions ContractJson=new(JsonSerializerDefaults.Web);
    private IGameTransport? transport;
    private TransportIdentity? transportIdentity;
    private EnvironmentSceneState environment;
    private RenderWeatherState? renderWeather;
    private EnvironmentSceneState? proofEnvironment;
    private PmWeatherSupercellSimulation? fixtureWeather;
    private AtmosphereSnapshot? fractionalDayProfile;
    private long tick,baselineRevision,eventNumber;
    private uint nextPlayerId;
    private int relayPort;
    private int nextProbeId;
    private DateTimeOffset lastPersist=DateTimeOffset.UtcNow;
    private ServerInfoDto publishedInfo=null!;
    private string[] publishedCharacters=[];
    private readonly Queue<double> tickMilliseconds=[];
    private long performanceWindowStart=Stopwatch.GetTimestamp();
    public bool Ready {get;private set;}
    public string ServerId {get;}

    public GameAuthority(ServerOptions options,CareerStore store)
    {
        this.options=options;this.store=store;ServerId=store.GetOrCreateServerId();
        var args=new[]{"--demo","--campaign-seed",options.CampaignSeed.ToString(),"--campaign-start-hour",options.CampaignStartHour.ToString(System.Globalization.CultureInfo.InvariantCulture)};
        weather=CreateClimate(options,args,store);environment=weather.Current;PublishInfo();
    }
    /// <summary>A separate climate process when --climate host:port is given (accounts-only never needs weather); otherwise the model runs here.</summary>
    private static IClimateFeed CreateClimate(ServerOptions options,string[] args,CareerStore store)
    {
        if(options.ClimateAddress is null||options.AccountsOnly)return new LocalClimate(new(new(args),args,store.LoadCampaignSeconds()));
        int colon=options.ClimateAddress.LastIndexOf(':');
        if(colon<1||!int.TryParse(options.ClimateAddress[(colon+1)..],out int port))throw new ArgumentException("--climate must be host:port.");
        var feed=new ClimateLinkClient(options.ClimateAddress[..colon],port,options.LinkKeyEnvironment is null?null:Environment.GetEnvironmentVariable(options.LinkKeyEnvironment),
            options.CampaignSeed,TimeSpan.FromSeconds(30));
        Console.WriteLine($"Weather from climate process {options.ClimateAddress}: day {feed.DayNumber} {feed.Hour:00.00}h.");
        return feed;
    }
    public ServerInfoDto ServerInfo
    {
        get=>Volatile.Read(ref publishedInfo);
    }
    private void PublishInfo()
    {
        Volatile.Write(ref publishedInfo,new(ServerId,options.ServerName,"Self hosted",players.Count,options.MaxPlayers,options.AccountsOnly?"Accounts service":"Cooperative storm chase","1",options.AdvertiseHost,options.HttpPort,options.GamePort,true,options.CampaignSeed,
            options.AccountsOnly?0:StormChaseWorld.PlayableExtentMeters/1000,weather.ElapsedSeconds,weather.Hour,weather.DayNumber,weather.ForecastName,environment.ActiveStorms.Count,Ready,world?.ContentFingerprint??"",transportIdentity?.Identity??"",transportIdentity?.AuthorityCertificateBase64??"",options.ApiAddress??(options.CertificatePath is null?"http":"https")+"://"+options.AdvertiseHost+":"+options.HttpPort,RelayPort:relayPort));
        Volatile.Write(ref publishedCharacters,players.Values.Select(p=>p.Character.CharacterId).ToArray());
    }
    public JoinTicketDto? IssueAdmission(string accountId,string characterId)
    {
        var character=store.Character(accountId,characterId);if(character is null)return null;
        lock(gate)
        {
            if(!Ready)throw new CareerConflictException("Server world is loading. Try again shortly.");
            PruneAdmissions();
            if(players.Count>=options.MaxPlayers)throw new CareerConflictException("Server is full.");
            if(players.Values.Any(p=>p.Character.CharacterId==characterId))throw new CareerConflictException("Character is already connected.");
            if(admissions.Count>=128)throw new CareerConflictException("Too many pending admissions. Try again shortly.");
            foreach(var previous in admissions.Where(a=>a.Value.Character.CharacterId==characterId).Select(a=>a.Key).ToArray())admissions.Remove(previous);
            string ticket=Convert.ToHexString(RandomNumberGenerator.GetBytes(32));var expires=DateTimeOffset.UtcNow.AddSeconds(30);
            admissions[ticket]=new(character,expires);
            return new(ticket,expires,options.AdvertiseHost,options.GamePort,ServerId,characterId);
        }
    }
    public bool TryConsumeAdmission(string ticket,out AuthenticatedCharacter? character)
    {
        if(store is RemoteCareerStore remote){character=remote.Redeem(ticket);return character is not null;}
        lock(gate)
        {
            character=null;PruneAdmissions();
            if(ticket is null||ticket.Length!=64||players.Count>=options.MaxPlayers||!admissions.Remove(ticket,out var admission)||admission.ExpiresUtc<=DateTimeOffset.UtcNow)return false;
            if(players.Values.Any(p=>p.Character.CharacterId==admission.Character.CharacterId))return false;
            character=admission.Character;return true;
        }
    }
    private void PruneAdmissions(){foreach(var key in admissions.Where(p=>p.Value.ExpiresUtc<=DateTimeOffset.UtcNow).Select(p=>p.Key).ToArray())admissions.Remove(key);}
    public bool CanUseServices(string characterId)
    {
        lock(gate)
        {
            var player=players.Values.FirstOrDefault(p=>p.Character.CharacterId==characterId);
            return player is null||player.Vehicle.SpeedMps<.5f&&player.Career.AtServiceStation(player.Vehicle.Position);
        }
    }
    public PurchaseDto Purchase(string characterId,PurchaseRequest request)
    {
        CareerStore.ValidateOperation(request.OperationId);
        lock(gate)
        {
            if(!CanUseServices(characterId))throw new CareerConflictException("Park at a working service station before purchasing.");
            var player=players.Values.FirstOrDefault(p=>p.Character.CharacterId==characterId);
            player?.Career.PersistVehicle();
            var result=store.Purchase(characterId,request with{OperationId="client:"+Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(request.OperationId)))});
            if(player is not null)
            {
                player.Career.Refresh(false);
                var station=player.Career.NearestStation(player.Vehicle.Position)!;
                player.Vehicle.SwitchIfChanged(station.ParkingPosition,result.Purchased&&request.Sku is "suv" or "interceptor" or "chase-truck");
            }
            return result;
        }
    }
    public CareerDto SelectVehicle(string characterId,string vehicleId)
    {
        lock(gate)
        {
            if(!CanUseServices(characterId))throw new CareerConflictException("Park at a working service station before changing vehicles.");
            var player=players.Values.FirstOrDefault(p=>p.Character.CharacterId==characterId);
            player?.Career.PersistVehicle();var result=store.SelectVehicle(characterId,vehicleId);
            if(player is not null){player.Career.Refresh(false);player.Vehicle.SwitchIfChanged(player.Career.NearestStation(player.Vehicle.Position)!.ParkingPosition,true);}
            return result;
        }
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if(!options.SelfTest&&!options.AccountsOnly)
        {
            world=new(new MutableRenderAssetCatalog(new BuiltInRenderAssetCatalog()));destruction=new(world);
            contracts=new(world,options.CampaignSeed){Debris=()=>destruction.GetReleasedPartStates(),CollectDebris=destruction.TryCollectSettledPart,BuildingDestroyed=destruction.IsBuildingDestroyed,
                Storms=()=>renderWeather?.ActiveStorms.Where(s=>!s.VisualOnly).Select(s=>new ChaseJobStorm(s.Position,s.Velocity,s.Tornadoes.Any(t=>t.TouchdownAmount>=.5f))).ToArray()??[]};
            InitHousing(world);
            if(destruction.NativeProvider.IsReducedFallback||destruction.NativeProvider.ProviderName!="PhysX")throw new InvalidOperationException("The dedicated world requires the actual native PhysX provider.");
            destruction.AdditionalPhysicsAnchors=()=>players.Values.SelectMany(p=>new[]{p.Feet,p.Vehicle.Position}).Concat(furniturePhysics?.PhysicsAnchors??[]).ToArray();
            int id=0;foreach(var item in world.DestructibleItems.OrderBy(i=>i.MotionVectorId,StringComparer.Ordinal))stablePartIds.Add(item.MotionVectorId!,++id);
            _=PollLotsAsync(stoppingToken);
            if(options.RelayPort>0)
            {
                // Gateway mode: no public game port. Players reach this district host only through a Gateway link.
                var relay=new RelayLinkServer(System.Net.IPAddress.Parse(options.RelayListen=="localhost"?"127.0.0.1":options.RelayListen),options.RelayPort,
                    options.LinkKeyEnvironment is null?null:Environment.GetEnvironmentVariable(options.LinkKeyEnvironment));
                transport=relay;relayPort=relay.Port;
                Console.WriteLine($"Native PhysX world ready: {world.BuildingCount} buildings, {world.DestructibleItems.Count} parts; Gateway relay link {relay.Port}.");
            }
            else
            {
                transportIdentity=TransportIdentity.LoadOrCreate(Path.Combine(options.DataRoot,"transport-identity.json"));
                transport=StormTransportHost.CreateServer((ushort)options.GamePort,options.MaxPlayers+8,transportIdentity.ServerSecurity);
                Console.WriteLine($"Native PhysX world ready: {world.BuildingCount} buildings, {world.DestructibleItems.Count} parts; encrypted game transport {options.GamePort}.");
            }
            if(options.NativeFixture)
            {
                BeginNativeProofStorm();environment=GroundStorms(proofEnvironment!);
                var fixtureWeather=frameBuilder.Build(0,DateTimeOffset.UnixEpoch,new(1,1),new RenderScene{Environment=environment}).Weather;
                for(int i=0;i<180;i++)destruction.Update(1f/60,fixtureWeather,new Vector3f(5820,0,6860));
                SettleNativeProofStorm();
                StartFixtureVisualStorm();
                Console.WriteLine($"Native fixture ready: {destruction.GetReleasedPartStates().Count} actual PhysX fragments at commercial site 5820 / 6860; full settled baseline available.");
            }
        }
        Ready=true;PublishInfo();
        using var timer=new PeriodicTimer(TimeSpan.FromSeconds(TickDelta));
        try
        {
            while(await timer.WaitForNextTickAsync(stoppingToken))
            {
                long started=Stopwatch.GetTimestamp();
                lock(gate)
                {
                    ProcessNetwork();weather.Update(TickDelta);AdvanceLotTestStorm();
                    if(fixtureWeather is not null)
                    {
                        fixtureWeather.Advance(TickDelta);
                        if(fixtureWeather.Current.ActiveStorms.Count==0)StartFixtureVisualStorm();
                        proofEnvironment=fixtureWeather.Current;
                    }
                    environment=GroundStorms(proofEnvironment??weather.Current);tick++;
                    if(destruction is not null&&world is not null)
                    {
                        renderWeather=frameBuilder.Build(tick,DateTimeOffset.UnixEpoch.AddSeconds(weather.ElapsedSeconds),new(1,1),new RenderScene{Environment=environment}).Weather;
                        AdvancePlayers();ApplyLotUpdates();
                        var observer=players.Values.FirstOrDefault()?.Feet??world.GasStations[0].ParkingPosition;
                        destruction.Update(TickDelta,renderWeather,observer,delta=>{foreach(var p in players.Values)p.Vehicle.StepForces(delta);});
                        HousingWorldUpdate();
                        foreach(var p in players.Values)
                        {
                            p.Vehicle.AfterPhysics(weather.ElapsedSeconds);
                            if(p.Vehicle.Driving){p.Feet=p.Vehicle.Position;p.Camera.SetPosition(Native(p.Feet)+Vector3.UnitY*1.72f);}
                            else if(p.Controller is not null&&destruction.Physics.TryGetCharacterFootPosition(p.Controller,out var feet))p.Feet=Render(feet);
                            contracts!.Update(p.Contracts,TickDelta,p.Feet,p.Vehicle.Position,p.Vehicle.Driving,p.Vehicle.SpeedMps,
                                StormChaseDestruction.SampleWindSpeedMph(p.Feet,renderWeather),reward=>CommitContract(p,reward));
                            HousingUpdate(p);
                        }
                        AdvanceProbes();PublishDebrisReleases();
                        if(tick%2==0){var shared=Snapshot(false);foreach(var p in players.Values)transport!.Send(p.Connection,MessageKind.WorldSnapshot,Personalize(shared,p),false);}
                        // The daily baseline/outlook is static network data. Live
                        // UI diagnostics use the shared model and server clock.
                        if(players.Values.Any(p=>p.AtmosphereDay!=weather.DayNumber))
                        {
                            var profile=DailyAtmosphere();
                            foreach(var p in players.Values)
                                if(p.AtmosphereDay!=profile.DayNumber&&transport!.Send(p.Connection,MessageKind.AtmosphereSnapshot,profile))
                                    p.AtmosphereDay=profile.DayNumber;
                        }
                        if(tick%2==0)PublishDebrisMotion();
                    }
                    if(DateTimeOffset.UtcNow-lastPersist>=TimeSpan.FromSeconds(1))PersistProgress();
                    PublishInfo();tickMilliseconds.Enqueue(Stopwatch.GetElapsedTime(started).TotalMilliseconds);if(tickMilliseconds.Count>1200)tickMilliseconds.Dequeue();
                }
            }
        }
        catch(OperationCanceledException)when(stoppingToken.IsCancellationRequested){}
        finally
        {
            lock(gate)
            {
                Ready=false;PublishInfo();PersistProgress();DisposeHousing();
                foreach(var connection in players.Keys.ToArray())RemovePlayer(connection);
                if(store is RemoteCareerStore remote)remote.Post<bool>("/api/internal/servers/stop",new{});
                players.Clear();transport?.Dispose();transport=null;destruction?.Dispose();destruction=null;weather.Dispose();
            }
        }
    }
    private EnvironmentSceneState GroundStorms(EnvironmentSceneState source)
    {
        if(world is null)return source;
        return source with {Storms=source.ActiveStorms.Select(storm=>
        {
            float ground=world.HeightAt(storm.Position.X,storm.Position.Z);
            float rise=storm.Id.StartsWith("campaign-",StringComparison.Ordinal)?ground:0;
            return storm with
            {
                Position=storm.Position with{Y=ground},CloudBaseMeters=storm.CloudBaseMeters+rise,CloudTopMeters=storm.CloudTopMeters+rise,
                Vorticies=storm.ActiveVorticies.Select(vortex=>vortex with{Position=vortex.Position with{Y=world.HeightAt(vortex.Position.X,vortex.Position.Z)}}).ToArray(),
                Tornadoes=storm.Tornadoes.Select(tornado=>tornado with
                { Position=tornado.Position with{Y=world.HeightAt(tornado.Position.X,tornado.Position.Z)},HeightMeters=Math.Max(1,storm.CloudBaseMeters+rise-world.HeightAt(tornado.Position.X,tornado.Position.Z)) }).ToArray()
            };
        }).ToArray()};
    }
    // This trusted seam exists only inside the native proof executable mode.
    // No HTTP/native gameplay request can select weather or final body poses.
    internal void BeginNativeProofStorm()
    {
        if(!options.NativeSelfTest&&!options.NativeFixture)throw new InvalidOperationException("Native validation mode is required.");
        lock(gate)
        {
            var target=new Vector3f(5820,0,6860);float ground=world!.HeightAt(target.X+260,target.Z);
            var sim=new PmWeatherSupercellSimulation(new(){Seed=13,PeakWindSpeedMph=400,MaximumWidthMeters=700,TouchdownWindSpeedMph=90,
                Origin=new(target.X+260,ground,target.Z),CloudBaseMeters=ground+760,CloudTopMeters=ground+3260,
                InitialStage=3,InitialEnergy=100,InitialGrowthSpeed=3,TornadoGrowthSpeed=2,DecayGrowthSpeed=3});
            sim.Advance(40);proofEnvironment=sim.Current;
        }
    }
    private void StartFixtureVisualStorm()
    {
        if(!options.NativeFixture)throw new InvalidOperationException("Native fixture mode is required.");
        // A second real source cell stays away from the settled destruction site.
        // It advances normally on the authoritative tick, including its lifecycle.
        float ground=world!.HeightAt(7600,6860);
        fixtureWeather=new(new(){Seed=23,Id="native-fixture-visual-cell",PeakWindSpeedMph=100,MaximumWidthMeters=90,TouchdownWindSpeedMph=70,
            Origin=new(7600,ground,6860),Velocity=new(.5f,0,.12f),CloudBaseMeters=ground+760,CloudTopMeters=ground+3260,
            InitialStage=3,InitialEnergy=100,InitialGrowthSpeed=3,TornadoGrowthSpeed=2,DecayGrowthSpeed=60});
        fixtureWeather.Advance(40);proofEnvironment=fixtureWeather.Current;
    }
    internal void SettleNativeProofStorm()
    {
        if(!options.NativeSelfTest&&!options.NativeFixture)throw new InvalidOperationException("Native validation mode is required.");
        lock(gate)
        {
            proofEnvironment=null;environment=GroundStorms(weather.Current);renderWeather=null;
            var target=new Vector3f(5820,0,6860);
            for(int i=0;i<2400;i++)destruction!.Update(1f/60,null,target);
            PublishDebrisReleases();foreach(var p in players.Values)SendFullDebrisBaseline(p.Connection);
        }
    }
    internal DebrisBodyState[] NativeProofBodies()
    {
        if(!options.NativeSelfTest)throw new InvalidOperationException("Native validation mode is required.");
        lock(gate)return destruction!.GetReleasedPartStates().Select(ConvertPart).ToArray();
    }
    internal ChaseContractOffer[] NativeProofOffers()
    {
        if(!options.NativeSelfTest)throw new InvalidOperationException("Native validation mode is required.");
        lock(gate)return contracts!.Offers(weather.DayNumber).ToArray();
    }
    internal void PlaceNativeProofAvatar(string character,Vector3f feet)
    {
        if(!options.NativeSelfTest)throw new InvalidOperationException("Native validation mode is required.");
        lock(gate)
        {
            var p=players.Values.Single(p=>p.Character.CharacterId==character);
            if(p.Vehicle.Driving)throw new InvalidOperationException("Exit vehicle before validation movement.");
            destruction!.EnsurePhysicsAround(feet);destruction.Physics.SetCharacterFootPosition(p.Controller!,Native(feet));p.Feet=feet;p.VerticalVelocity=0;
            p.Camera.SetPosition(Native(feet)+Vector3.UnitY*1.72f);
        }
    }
    internal void PlaceNativeProofVehicle(string character,Vector3f feet)
    {
        if(!options.NativeSelfTest)throw new InvalidOperationException("Native validation mode is required.");
        lock(gate)
        {
            var p=players.Values.Single(p=>p.Character.CharacterId==character);p.Vehicle.ExitForCameraMode();p.Vehicle.Spawn(feet);
            var avatar=feet+new Vector3f(-3,0,0);avatar=avatar with{Y=world!.HeightAt(avatar.X,avatar.Z)};
            p.Controller??=destruction!.Physics.CreateCapsuleCharacter("online/avatar/"+character,Native(avatar));
            PlaceNativeProofAvatar(character,avatar);
        }
    }
    private void ProcessNetwork()
    {
        if(transport is null)return;
        foreach(var packet in transport.Poll())
        {
            if(packet.Kind==TransportEventKind.Connected){awaitingJoin[packet.ConnectionId]=DateTimeOffset.UtcNow;continue;}
            if(packet.Kind==TransportEventKind.Disconnected){RemovePlayer(packet.ConnectionId);awaitingJoin.Remove(packet.ConnectionId);continue;}
            if(packet.Kind!=TransportEventKind.Message||packet.Message is not { } message)continue;
            if(message.Kind==MessageKind.JoinRequest)
            {
                if(players.ContainsKey(packet.ConnectionId))continue;
                var join=message.Get<JoinRequest>();
                if(world is not null&&join.WorldContentFingerprint!=world.ContentFingerprint)
                {transport.Send(packet.ConnectionId,MessageKind.JoinRejected,new JoinRejected("World assets differ from the server. Install the same game version."));transport.Close(packet.ConnectionId,"World content mismatch");continue;}
                if(!TryConsumeAdmission(join.Ticket,out var character)||character is null)
                {transport.Send(packet.ConnectionId,MessageKind.JoinRejected,new JoinRejected("Admission ticket is invalid, expired or already used."));transport.Close(packet.ConnectionId,"Admission rejected");continue;}
                try{AddPlayer(packet.ConnectionId,character);}
                catch(CareerConflictException error)
                {
                    if(store is RemoteCareerStore remote)remote.Release(character.CharacterId);
                    transport.Send(packet.ConnectionId,MessageKind.JoinRejected,new JoinRejected(error.Message));
                    transport.Close(packet.ConnectionId,"Admission parking blocked");awaitingJoin.Remove(packet.ConnectionId);
                }
                continue;
            }
            if(!players.TryGetValue(packet.ConnectionId,out var player)){transport.Close(packet.ConnectionId,"Authenticate before gameplay");continue;}
            if(player.MessagesThisTick++>12){transport.Close(packet.ConnectionId,"Input rate exceeded");continue;}
            switch(message.Kind)
            {
                case MessageKind.ClientInput:
                    var input=message.Get<ClientInput>();
                    if(input.ClientTick<=player.LastInputTick||!input.Position.IsFinite||!float.IsFinite(input.Yaw)||!float.IsFinite(input.Pitch)||
                       !float.IsFinite(input.Forward)||!float.IsFinite(input.Strafe)||!float.IsFinite(input.Turn)||!Enum.IsDefined(input.Mode))break;
                    player.LastInputTick=input.ClientTick;player.Input=input with{Forward=Math.Clamp(input.Forward,-1,1),Strafe=Math.Clamp(input.Strafe,-1,1),Turn=Math.Clamp(input.Turn,-1,1),Pitch=Math.Clamp(input.Pitch,-1.48f,1.48f)};
                    player.LastInputUtc=DateTimeOffset.UtcNow;break;
                case MessageKind.GameplayAction:HandleAction(player,message.Get<GameplayAction>());break;
                case MessageKind.Ping:transport.Send(player.Connection,MessageKind.Pong,message.Get<Ping>(),false);break;
                case MessageKind.ChatSend:HandleChat(player,message.Get<ChatSend>());break;
            }
        }
        foreach(var p in players.Values)p.MessagesThisTick=0;
        foreach(var connection in awaitingJoin.Where(p=>DateTimeOffset.UtcNow-p.Value>TimeSpan.FromSeconds(10)).Select(p=>p.Key).ToArray())
        {transport.Close(connection,"Admission timeout");awaitingJoin.Remove(connection);}
    }
    private void AddPlayer(uint connection,AuthenticatedCharacter character)
    {
        var parking=AdmissionParking();
        var contractState=string.IsNullOrEmpty(character.Career.ContractState)?new():JsonSerializer.Deserialize<ChaseContractProgress>(character.Career.ContractState,ContractJson)??new();
        // A player who left on duty returns on duty, in their department vehicle.
        var career=new StormChaseCareer(world!,store,character.CharacterId){DestroyedBuildings=()=>destruction!.DestroyedBuildingIds,
            DutyVehicle=StormChaseJobs.Get(contractState.Job).DutyVehicle};
        uint id=++nextPlayerId;var vehicle=new StormChaseVehicles(world!,destruction!,career,"online/vehicle/"+character.CharacterId);
        vehicle.Spawn(parking);
        var feet=vehicle.Position+new Vector3f(-3,0,0);feet=feet with{Y=world!.HeightAt(feet.X,feet.Z)};
        var camera=new FlyCamera{Grounded=true};camera.SetPose(Native(feet)+Vector3.UnitY*1.72f,Native(feet)+new Vector3(0,1.72f,1));
        var player=new Player(connection,id,character,career,vehicle,camera,feet);
        player.Contracts=contractState;
        player.Controller=destruction!.Physics.CreateCapsuleCharacter("online/avatar/"+character.CharacterId,Native(feet));
        players.Add(connection,player);awaitingJoin.Remove(connection);
        var welcome=new JoinAccepted(id,Guid.Parse(character.CharacterId),Progress(player),Personalize(Snapshot(true),player));
        if(!transport!.Send(connection,MessageKind.JoinAccepted,welcome))
        {RemovePlayer(connection);transport.Close(connection,"Admission queue unavailable");return;}
        player.AtmosphereDay=welcome.Snapshot.Atmosphere!.DayNumber;
        SendFullDebrisBaseline(connection);
        Console.WriteLine("Player admitted: "+id+" / "+character.Name);
        AnnounceToDistrict(character.Name+" arrived in the district.");
    }
    private Vector3f AdmissionParking()
    {
        // Identities remain monotonic; admission locations are a reusable bounded
        // search near real stations, never an ever-growing offset into buildings.
        var wreckage=destruction!.GetReleasedPartStates().Where(p=>!p.Collected).ToArray();
        foreach(var station in world!.GasStations)
        {
            for(int ring=0;ring<=6;ring++)for(int spoke=0;spoke<(ring==0?1:16);spoke++)
            {
                float angle=spoke*MathF.Tau/16;
                var point=station.ParkingPosition+new Vector3f(MathF.Cos(angle)*ring*10,0,MathF.Sin(angle)*ring*10);
                if(world.Buildings.Any(b=>point.X+3.5f>b.BoundsMinimum.X&&point.X-4.5f<b.BoundsMaximum.X
                    &&point.Z+4>b.BoundsMinimum.Z&&point.Z-4<b.BoundsMaximum.Z))continue;
                if(players.Values.Any(p=>HorizontalDistance(point,p.Vehicle.Position)<9||HorizontalDistance(point,p.Feet)<7))continue;
                if(wreckage.Any(p=>HorizontalDistance(point,p.Position)<12))continue;
                float ground=world.HeightAt(point.X,point.Z);
                if(!float.IsFinite(ground)||MathF.Abs(ground-world.HeightAt(point.X-3,point.Z))>1)continue;
                return point with{Y=ground+.25f};
            }
        }
        throw new CareerConflictException("All nearby admission parking is occupied or blocked by storm debris. Try again shortly.");
    }
    private static float HorizontalDistance(Vector3f a,Vector3f b)
    {float x=a.X-b.X,z=a.Z-b.Z;return MathF.Sqrt(x*x+z*z);}
    private void RemovePlayer(uint connection)
    {
        if(!players.Remove(connection,out var p))return;
        PersistPlayer(p);
        PersistPlayerHousing(p);
        if(store is RemoteCareerStore remote)remote.Release(p.Character.CharacterId);
        if(p.Controller is not null)destruction!.Physics.RemoveCharacter(p.Controller);
        p.Vehicle.Dispose();
        foreach(var probe in probes.Where(probe=>probe.OwnerId==p.Id)){probe.Detached=true;probe.Active=false;}
        Console.WriteLine("Player disconnected: "+p.Id);
        chatBudget.Remove(connection);AnnounceToDistrict(p.Character.Name+" left the district.");
    }
    public string[] ActiveCharacterIds()=>Volatile.Read(ref publishedCharacters);
    internal object NativeProofPerformance()
    {
        if(!options.NativeSelfTest)throw new InvalidOperationException("Native validation mode is required.");
        lock(gate)
        {
            var sorted=tickMilliseconds.Order().ToArray();double elapsed=Stopwatch.GetElapsedTime(performanceWindowStart).TotalSeconds;
            return new{Samples=sorted.Length,MeasurementSeconds=elapsed,MeasuredTickRate=sorted.Length/Math.Max(.001,elapsed),TickRateTarget=20,ActivePlayers=players.Count,NativeDynamicFragments=destruction?.DynamicBodyCount??0,MeanMilliseconds=sorted.Length==0?0:sorted.Average(),P95Milliseconds=sorted.Length==0?0:sorted[(int)((sorted.Length-1)*.95)],MaximumMilliseconds=sorted.Length==0?0:sorted[^1],Over50Milliseconds=sorted.Count(v=>v>50)};
        }
    }
    internal (int LargestEncodedSnapshotBytes,string[] ConnectionStatuses) NativeProofNetwork()
    {
        if(!options.NativeSelfTest)throw new InvalidOperationException("Native validation mode is required.");
        lock(gate)
        {
            var shared=Snapshot(false);
            return(players.Values.Select(p=>ProtocolCodec.Encode(MessageKind.WorldSnapshot,0,Personalize(shared,p)).Length).DefaultIfEmpty(0).Max(),
                players.Values.Select(p=>transport!.GetConnectionStatus(p.Connection)).ToArray());
        }
    }
    internal void ResetNativeProofPerformance()
    {
        if(!options.NativeSelfTest)throw new InvalidOperationException("Native validation mode is required.");
        lock(gate){tickMilliseconds.Clear();performanceWindowStart=Stopwatch.GetTimestamp();}
    }
    private void AdvancePlayers()
    {
        foreach(var p in players.Values)
        {
            var previousFeet=p.Feet;
            var input=DateTimeOffset.UtcNow-p.LastInputUtc<TimeSpan.FromSeconds(1)?p.Input:null;
            float yaw=input?.Yaw??p.Yaw,pitch=input?.Pitch??p.Pitch;p.Yaw=yaw;p.Pitch=pitch;
            var direction=new Vector3(MathF.Sin(yaw)*MathF.Cos(pitch),MathF.Sin(pitch),MathF.Cos(yaw)*MathF.Cos(pitch));
            p.Camera.SetPose(Native(p.Feet)+Vector3.UnitY*1.72f,Native(p.Feet)+Vector3.UnitY*1.72f+direction);
            p.Vehicle.SetInput(input?.Forward??0,input?.Turn??0,input?.Brake??false,renderWeather,TickDelta);
            if(p.Vehicle.Driving){p.Velocity=p.Vehicle.NativeBody.Velocity;continue;}
            if(p.Controller is null)p.Controller=destruction!.Physics.CreateCapsuleCharacter("online/avatar/"+p.Character.CharacterId,Native(p.Feet));
            var forward=new Vector3(MathF.Sin(yaw),0,MathF.Cos(yaw));var right=new Vector3(forward.Z,0,-forward.X);
            var movement=forward*(input?.Forward??0)+right*(input?.Strafe??0);if(movement.LengthSquared()>1)movement=Vector3.Normalize(movement);
            movement*=((input?.Sprint??false)?12:6)*TickDelta;
            var wind=StormChaseDestruction.SampleWindVelocity(p.Feet,renderWeather);
            bool sheltered=world!.FindShelter(p.Feet,destruction!.IsPartDestroyed) is not null;
            var horizontalWind=new Vector3(wind.X,0,wind.Z);float windMph=horizontalWind.Length()/.44704f;
            if(windMph>35&&horizontalWind.LengthSquared()>.01f)
            {
                float driftSpeed=Math.Clamp((windMph-35)*.038f,0,6);
                movement+=Vector3.Normalize(horizontalWind)*driftSpeed*TickDelta*(sheltered?.08f:1);
            }
            if(input?.Jump==true&&!p.JumpHeld&&p.Feet.Y<=world!.HeightAt(p.Feet.X,p.Feet.Z)+.25f)p.VerticalVelocity=5;
            p.JumpHeld=input?.Jump??false;p.VerticalVelocity=Math.Max(-25,p.VerticalVelocity-9.81f*TickDelta);
            movement.Y=p.VerticalVelocity*TickDelta;
            destruction!.Physics.MoveCharacter(p.Controller,movement,TickDelta,out _);
            if(destruction.Physics.TryGetCharacterFootPosition(p.Controller,out var position))
            {
                if(!world!.ContainsPlayablePosition(position.X,position.Z))destruction.Physics.SetCharacterFootPosition(p.Controller,Native(p.Feet));
                else p.Feet=Render(position);
                if(p.Feet.Y<=world.HeightAt(p.Feet.X,p.Feet.Z)+.1f)p.VerticalVelocity=0;
            }
            p.Velocity=Native(p.Feet-previousFeet)/TickDelta;
        }
    }
    private void HandleAction(Player p,GameplayAction action)
    {
        if(action.RequestId<=0||!Enum.IsDefined(action.Kind)||!action.Position.IsFinite)return;
        if(p.Receipts.TryGetValue(action.RequestId,out var old)){transport!.Send(p.Connection,MessageKind.ActionResult,old);return;}
        if(action.RequestId<=p.HighestAction){transport!.Send(p.Connection,MessageKind.ActionResult,new ActionResult(action.RequestId,false,"Old action rejected."));return;}
        p.HighestAction=action.RequestId;bool success=false;string message="Action unavailable.";
        var now=DateTimeOffset.UtcNow;p.ActionTokens=Math.Min(4,p.ActionTokens+(float)(now-p.ActionRefill).TotalSeconds*2);p.ActionRefill=now;
        if(p.ActionTokens<1){transport!.Send(p.Connection,MessageKind.ActionResult,new ActionResult(action.RequestId,false,"Wait briefly before another action."));return;}p.ActionTokens--;
        if(HousingAction(p,action,out var housingResult))
        {p.Receipts[action.RequestId]=housingResult;if(p.Receipts.Count>128)p.Receipts.Remove(p.Receipts.Keys.Min());transport!.Send(p.Connection,MessageKind.ActionResult,housingResult);return;}
        switch(action.Kind)
        {
            case GameplayActionKind.AcceptContract:
                success=contracts!.Accept(action.TargetId,weather.DayNumber,p.Contracts);message=contracts.Message;SaveContracts(p);break;
            case GameplayActionKind.SelectJob:
                if(!CanUseServices(p.Character.CharacterId)){message="Change jobs at a working service station.";break;}
                if(action.TargetId<0||action.TargetId>=StormChaseJobs.All.Length){message="Unknown job.";break;}
                var job=StormChaseJobs.All[action.TargetId];
                p.Career.PersistVehicle();
                success=contracts!.SelectJob(job.Id,p.Contracts);message=contracts.Message;
                if(success&&p.Career.DutyVehicle!=job.DutyVehicle)
                {
                    p.Career.DutyVehicle=job.DutyVehicle;p.Career.ServiceDutyVehicle();
                    p.Vehicle.SwitchIfChanged(p.Career.NearestStation(p.Vehicle.Position)!.ParkingPosition,true);
                }
                SaveContracts(p);break;
            case GameplayActionKind.CancelContract:
                contracts!.Cancel(p.Contracts);success=true;message=contracts.Message;SaveContracts(p);break;
            case GameplayActionKind.ContractInteract:
            case GameplayActionKind.Salvage:
                success=contracts!.Interact(p.Contracts,p.Feet,p.Vehicle.Position,p.Vehicle.Driving,p.Vehicle.SpeedMps,StormChaseDestruction.SampleWindSpeedMph(p.Feet,renderWeather));message=contracts.Message;SaveContracts(p);break;
            case GameplayActionKind.EnterVehicle:
            case GameplayActionKind.ExitVehicle:
                success=p.Vehicle.Interact(p.Camera,weather.ElapsedSeconds);message=p.Career.Message;
                if(success&&p.Vehicle.Driving&&p.Controller is not null){destruction!.Physics.RemoveCharacter(p.Controller);p.Controller=null;}
                if(success&&!p.Vehicle.Driving){var camera=p.Camera.ToCameraState().Position;p.Feet=camera-new Vector3f(0,1.72f,0);}
                break;
            case GameplayActionKind.DeployProbe:
                var state=store.GetCareer(p.Character.CharacterId);int capacity=Math.Min(p.Career.SelectedVehicle.ProbeCapacity,state.ProbeLevel>0?6:2);
                if(probes.Count(probe=>probe.OwnerId==p.Id&&probe.Active)>=capacity){message="Probe rack is empty. Retrieve a probe or upgrade equipment.";break;}
                var point=p.Feet+new Vector3f(MathF.Sin(p.Yaw)*2,0,MathF.Cos(p.Yaw)*2);point=point with{Y=world!.HeightAt(point.X,point.Z)+.35f};
                probes.Add(new(++nextProbeId,p.Id,p.Character.CharacterId,point,state.ProbeLevel));success=true;message="Research probe deployed.";break;
            case GameplayActionKind.RecoverProbe:
                var nearby=probes.Where(probe=>probe.OwnerId==p.Id&&probe.Active&&StormChaseCareer.HorizontalDistance(probe.Position,p.Feet)<15).OrderBy(probe=>StormChaseCareer.HorizontalDistance(probe.Position,p.Feet)).FirstOrDefault();
                if(nearby is not null){nearby.Active=false;success=true;message="Probe recovered.";}else message="Move within15m of your probe.";break;
            case GameplayActionKind.TakePhoto:
                (success,message)=EvaluatePhoto(p,action.RequestId);break;
            case GameplayActionKind.RepairVehicle:
            case GameplayActionKind.BuyProbe:
            case GameplayActionKind.Purchase:
                if(!CanUseServices(p.Character.CharacterId)){message="Park at a working service station.";break;}
                int offerIndex=action.Kind==GameplayActionKind.RepairVehicle?5:action.Kind==GameplayActionKind.BuyProbe?0:action.TargetId;
                if(offerIndex<0||offerIndex>=CareerStore.Offers.Length){message="Unknown offer.";break;}
                p.Career.PersistVehicle();
                var purchase=store.Purchase(p.Character.CharacterId,new(CareerStore.Offers[offerIndex].Sku,bootId+":"+p.Id+":"+action.RequestId));
                p.Career.Refresh(false);p.Vehicle.SwitchIfChanged(world!.GasStations.OrderBy(s=>StormChaseCareer.HorizontalDistance(s.Position,p.Feet)).First().ParkingPosition,purchase.Purchased&&CareerStore.Offers[offerIndex].Sku is "suv" or "interceptor" or "chase-truck");
                success=purchase.Purchased||purchase.Duplicate;message=purchase.Message;break;
            case GameplayActionKind.SelectVehicle:
                if(!CanUseServices(p.Character.CharacterId)){message="Park at a working service station.";break;}
                string selected=action.TargetId switch{0=>"pickup",1=>"suv",2=>"interceptor",3=>"chase-truck",_=>""};
                try{p.Career.PersistVehicle();store.SelectVehicle(p.Character.CharacterId,selected);p.Career.Refresh(false);p.Vehicle.SwitchIfChanged(p.Career.NearestStation(p.Vehicle.Position)!.ParkingPosition,true);success=true;message="Vehicle selected.";}catch(ArgumentException e){message=e.Message;}break;
            case GameplayActionKind.Refuel:p.Vehicle.ToggleRefuel(weather.ElapsedSeconds);success=p.Career.Refueling;message=p.Career.Message;break;
            case GameplayActionKind.ToggleAnchors:p.Vehicle.ToggleAnchors(weather.ElapsedSeconds);success=true;message=p.Career.Message;break;
            case GameplayActionKind.Respawn:
            case GameplayActionKind.RecoverVehicle:
                p.Vehicle.Recover(p.Camera,weather.ElapsedSeconds);var recovered=p.Camera.ToCameraState().Position;p.Feet=recovered-new Vector3f(0,1.72f,0);
                if(p.Controller is not null)destruction!.Physics.SetCharacterFootPosition(p.Controller,Native(p.Feet));success=true;message=p.Career.Message;break;
        }
        if(success)p.Career.Refresh();
        var result=new ActionResult(action.RequestId,success,message,Progress(p));p.Receipts[action.RequestId]=result;
        if(p.Receipts.Count>128)p.Receipts.Remove(p.Receipts.Keys.Min());transport!.Send(p.Connection,MessageKind.ActionResult,result);
    }
    private (bool,string) EvaluatePhoto(Player p,long requestId)
    {
        if(weather.ElapsedSeconds-p.LastPhoto<12)return(false,"Photo reward cooldown is12seconds.");
        p.Photos++;decimal best=0;var career=store.GetCareer(p.Character.CharacterId);var camera=p.Camera.ToCameraState();
        foreach(var tornado in renderWeather?.ActiveStorms.SelectMany(s=>s.Tornadoes)??[])
        {
            float wind=tornado.SmoothedWindSpeed??tornado.WindSpeed;if(tornado.CondensationAmount<.15f||wind<15)continue;
            var aim=tornado.Position with{Y=tornado.Position.Y+tornado.HeightMeters*.4f};var delta=Native(aim-camera.Position);float range=delta.Length(),maximum=career.CameraLevel>0?8000:4500;
            if(range<80||range>maximum)continue;float dot=Vector3.Dot(Vector3.Normalize(delta),Native(camera.Forward));
            if(dot<MathF.Cos(camera.VerticalFieldOfViewDegrees*MathF.PI/360*.85f)||!world!.IsLineOfSightClear(camera.Position,aim,destruction!.DestroyedBuildingIds))continue;
            decimal reward=decimal.Round((decimal)((60+wind*2.2f)*Math.Clamp((dot-.8f)/.2f,.2f,1)*Math.Clamp(1-range/maximum,.15f,1)*(career.CameraLevel>0?1.5f:1)*(1+Math.Min(career.Popularity,200)/100f)),2);best=Math.Max(best,reward);
        }
        p.LastPhoto=weather.ElapsedSeconds;
        if(best<=0){store.ApplyReward(p.Character.CharacterId,bootId+":photo:"+p.Id+":"+requestId,0,0,photos:1);return(true,"Photo recorded. Frame a visible tornado to earn research income.");}
        p.ParticipatedDay=weather.DayNumber;
        best=Math.Min(10000,best);store.ApplyReward(p.Character.CharacterId,bootId+":photo:"+p.Id+":"+requestId,best,5,photos:1);p.LastPhoto=weather.ElapsedSeconds;
        return(true,$"Tornado photograph accepted: +${best:0.00}.");
    }
    private void AdvanceProbes()
    {
        foreach(var probe in probes.Where(p=>p.Active&&!p.Detached))
        {
            probe.Wind=StormChaseDestruction.SampleWindSpeedMph(probe.Position,renderWeather);
            if(probe.Wind>=40)probe.PendingCash+=(decimal)(TickDelta*(probe.Wind-25)*.55f*(probe.Level>0?1.6f:1));
            if(probe.Wind>(probe.Level>0?180:115))probe.Overload+=TickDelta;else probe.Overload=Math.Max(0,probe.Overload-TickDelta);
            if(probe.Overload>=4)probe.Active=false;
        }
    }
    private void PersistProgress()
    {
        foreach(var p in players.Values)PersistPlayer(p);
        foreach(var probe in probes)
        {
            decimal reward=decimal.Round(probe.PendingCash,2);if(reward<=0)continue;
            probe.PendingCash-=reward;store.ApplyReward(probe.CharacterId,bootId+":probe:"+probe.Id+":"+tick,Math.Min(1000,reward),1,researchSamples:1);
            var observer=players.Values.FirstOrDefault(p=>p.Character.CharacterId==probe.CharacterId);if(observer is not null){observer.ParticipatedDay=weather.DayNumber;observer.Career.Refresh();}
        }
        store.SaveCampaignSeconds(weather.ElapsedSeconds);lastPersist=DateTimeOffset.UtcNow;
        if(probes.Count>1024)probes.RemoveAll(p=>!p.Active||p.Detached);
    }
    private void PersistPlayer(Player p)
    {
        var reward=p.Career.DrainRewards();if(reward>0)store.ApplyReward(p.Character.CharacterId,bootId+":drive:"+p.Id+":"+tick,Math.Min(1000,reward),1);
        if(p.ParticipatedDay>0&&p.ParticipatedDay<weather.DayNumber)
        {store.ApplyReward(p.Character.CharacterId,ServerId+":chase-day:"+p.ParticipatedDay,0,10,completedChases:1);p.ParticipatedDay=0;}
        p.Career.PersistVehicle();p.Career.Refresh();
        if(contracts is not null)SaveContracts(p);
    }
    private void SaveContracts(Player p)
    {
        p.Contracts.Completed.RemoveWhere(id=>id/100<weather.DayNumber-2);
        string document=JsonSerializer.Serialize(p.Contracts,ContractJson);if(document==p.LastContractDocument)return;
        store.SaveContracts(p.Character.CharacterId,document);p.LastContractDocument=document;
    }
    private bool CommitContract(Player p,ChaseContractReward reward)
    {
        var completed=JsonSerializer.Deserialize<ChaseContractProgress>(JsonSerializer.Serialize(p.Contracts,ContractJson),ContractJson)!;
        completed.Completed.Add(reward.ContractId);completed.TotalCompleted++;completed.Active=null;
        string document=JsonSerializer.Serialize(completed,ContractJson);
        store.CommitContract(p.Character.CharacterId,reward.ReceiptId,reward.ContractId,reward.CashCents/100m,reward.Reputation,reward.Popularity,document);
        p.LastContractDocument=document;p.Career.Refresh();return true;
    }
    private WorldSnapshot Personalize(WorldSnapshot snapshot,Player p)
    {
        var offer=contracts!.ActiveOffer(p.Contracts);var target=contracts.Target(p.Contracts);var run=p.Contracts.Active;
        MissionState? mission=offer is not null&&run is not null?new(offer.Id,offer.Title,offer.Kind.ToString(),run.Step,offer.Stops.Count,target?.Instruction??"Saving completion",Net(target?.Position??p.Feet),run.Step>=offer.Stops.Count,run.WorkSeconds,target?.WorkSeconds??0):null;
        var activities=contracts.Offers(weather.DayNumber,p.Contracts.Job).Select(o=>new ActivityState(o.Id,o.Title,o.Kind.ToString(),Net(o.Stops[0].Position),o.Stops.Count,o.CashCents,o.Reputation,o.Available&&!p.Contracts.Completed.Contains(o.Id),o.Description)).ToArray();
        return HousingPersonalize(snapshot with{Mission=mission,Activities=activities,Career=Progress(p)},p);
    }
    private CareerProgress Progress(Player p)
    {
        var state=p.Career.State;return new(checked((long)(state.Cash*100)),(int)Math.Min(int.MaxValue,state.Reputation),probes.Count(probe=>probe.OwnerId==p.Id&&probe.Active),state.Photos,state.CompletedChases,
            state.Reputation>=1000?"Veteran Chaser":state.Reputation>=100?"Research Chaser":"Rookie Chaser",state.Popularity);
    }
    private DebrisBodyState ConvertPart(StormChaseDebrisState part)
    {
        int id=stablePartIds[part.PartId];return new(id,id,Net(part.Position),new(part.Rotation.X,part.Rotation.Y,part.Rotation.Z,part.Rotation.W),Net(part.LinearVelocity),Net(part.AngularVelocity),part.Sleeping,part.CollisionEnabled,part.PartId,part.Archived,part.Collected);
    }
    private void PublishDebrisReleases()
    {
        var states=destruction!.GetReleasedPartStates();var flashes=destruction.DrainBreakEvents().Where(e=>e.IsUtilityPole).Select(e=>e.Id).ToHashSet(StringComparer.Ordinal);
        foreach(var state in states)
        {
            var body=ConvertPart(state);if(!publishedParts.ContainsKey(state.PartId))
            {
                baselineRevision++;var release=new DebrisEvent(++eventNumber,baselineRevision,DebrisEventKind.Released,body);
                foreach(var p in players.Values)transport!.Send(p.Connection,MessageKind.DebrisEvent,release);
            }
            else if(body.Collected&&!publishedParts[state.PartId].Collected)
            {
                baselineRevision++;var collected=new DebrisEvent(++eventNumber,baselineRevision,DebrisEventKind.Removed,body);
                foreach(var p in players.Values)transport!.Send(p.Connection,MessageKind.DebrisEvent,collected);
            }
            else if(publishedParts[state.PartId] is var previous && (body.Archived!=previous.Archived||(body.Sleeping&&!previous.Sleeping)||body.CollisionEnabled!=previous.CollisionEnabled))
            {
                // A reliable final pose keeps settled collision in the same place on every client.
                foreach(var p in players.Values)transport!.Send(p.Connection,MessageKind.DebrisSnapshot,new DebrisSnapshot(tick,baselineRevision,false,[body]));
            }
            publishedParts[state.PartId]=body;
        }
        // Release events carry physical state; power flashes are a shared visual event.
        if(flashes.Count>0)
        {
            var changes=states.Where(s=>flashes.Contains(s.PartId)).Select(s=>new DestructionDelta(++eventNumber,stablePartIds[s.PartId],Net(s.Position),Net(s.LinearVelocity),true,true)).ToArray();
            foreach(var p in players.Values)transport!.Send(p.Connection,MessageKind.WorldSnapshot,Snapshot(false) with{Destruction=changes});
        }
    }
    private void PublishDebrisMotion()
    {
        var moving=publishedParts.Values.Where(b=>!b.Archived).ToArray();
        // Baselines carry the stable string catalogue. Small binary motion packets
        // fit one datagram and survive ordinary loss without whole-frame fragmentation.
        foreach(var chunk in moving.Select(body=>body with{StablePartId=""}).Chunk(12))
        {
            var snapshot=new DebrisSnapshot(tick,baselineRevision,false,chunk);
            foreach(var p in players.Values)transport!.Send(p.Connection,MessageKind.DebrisSnapshot,snapshot,false);
        }
        if(tick%100==0)foreach(var p in players.Values)SendFullDebrisBaseline(p.Connection);
    }
    private void SendFullDebrisBaseline(uint connection)
    {
        var states=destruction!.GetReleasedPartStates().Select(ConvertPart).ToArray();var chunks=states.Length==0?new[]{Array.Empty<DebrisBodyState>()}:states.Chunk(200).ToArray();
        for(int i=0;i<chunks.Length;i++)transport!.Send(connection,MessageKind.DebrisSnapshot,new DebrisSnapshot(tick,baselineRevision,true,chunks[i],i,chunks.Length));
    }
    private WorldSnapshot Snapshot(bool baseline)
    {
        var playerStates=players.Values.Select(p=>new PlayerState(p.Id,p.Character.Name,Net(p.Feet),p.Yaw,p.Vehicle.Driving?PlayerMovementMode.Driving:PlayerMovementMode.OnFoot,(int)p.Id,p.LastInputTick,p.Pitch,Net(p.Velocity))).ToArray();
        var vehicles=players.Values.Select(p=>
        {
            var body=p.Vehicle.NativeBody;destruction!.Physics.TryGetBodyPose(body,out var pose);destruction.Physics.TryGetBodyDynamics(body,out var linear,out var angular,out _);
            var rotation=Quaternion.CreateFromRotationMatrix(pose);
            return new VehicleState((int)p.Id,Net(p.Vehicle.Position),p.Vehicle.YawRadians,Net(linear),p.Career.Condition.Health,p.Vehicle.Driving?p.Id:0,
                new(rotation.X,rotation.Y,rotation.Z,rotation.W),Net(angular),p.Career.Condition.FuelLiters,p.Vehicle.Anchored,p.Career.SelectedVehicle.Id switch{"suv"=>1,"interceptor"=>2,"chase-truck"=>3,"police-cruiser"=>4,"fire-engine"=>5,"ambulance"=>6,_=>0});
        }).ToArray();
        var storms=environment.ActiveStorms.Select((s,index)=>new StormState(index+1,Net(s.Position),s.Stage.ToString(),s.TickCount/20f,s.Intensity,s.AnvilRadiusMeters,MathF.Atan2(s.Velocity.X,s.Velocity.Z),MathF.Sqrt(s.Velocity.X*s.Velocity.X+s.Velocity.Z*s.Velocity.Z),s.Tornadoes.FirstOrDefault() is{ }t?new TornadoState(1,t.Stage.ToString(),Net(t.Position),t.Intensity,t.SmoothedWindSpeed??t.WindSpeed,t.SmoothedWidthMeters??t.WidthMeters,t.FunnelLoweringAmount,s.TornadoOnGroundTicks/20f):null)).ToArray();
        var probeStates=probes.Select(p=>new ProbeState(p.Id,p.OwnerId,Net(p.Position),p.Wind,1013-p.Wind*.25f,p.Active)).ToArray();
        return new(tick,weather.ElapsedSeconds,options.CampaignSeed,playerStates,vehicles,storms,[],probeStates,null,environment,
            Atmosphere:baseline?DailyAtmosphere():null);
    }
    private AtmosphereSnapshot DailyAtmosphere()
    {
        var profile=weather.Atmosphere;
        // Midnight can fall between whole-second cache updates with a custom
        // start hour. Admission/day publication must already describe the new day.
        if(profile.DayNumber!=weather.DayNumber)
        {
            if(fractionalDayProfile?.DayNumber!=weather.DayNumber)
                fractionalDayProfile=StormChaseAtmosphere.OutlookSnapshot(options.CampaignSeed,weather.DayNumber,weather.Hour,
                    weather.ElapsedSeconds,tick) with
                {CampaignStartHour=weather.StartHour,CampaignDayLengthSeconds=StormChaseCampaignWeather.DefaultDayLengthSeconds};
            profile=fractionalDayProfile!;
        }
        return profile with{ServerTick=tick};
    }
    private static Vector3 Native(Vector3f vector)=>new(vector.X,vector.Y,vector.Z);
    private static Vector3f Render(Vector3 vector)=>new(vector.X,vector.Y,vector.Z);
    private static NetVector3 Net(Vector3f vector)=>new(vector.X,vector.Y,vector.Z);
    private static NetVector3 Net(Vector3 vector)=>new(vector.X,vector.Y,vector.Z);
    private sealed record Admission(AuthenticatedCharacter Character,DateTimeOffset ExpiresUtc);
    private sealed class Player(uint connection,uint id,AuthenticatedCharacter character,StormChaseCareer career,StormChaseVehicles vehicle,FlyCamera camera,Vector3f feet)
    {
        public uint Connection=connection,Id=id;public AuthenticatedCharacter Character=character;public StormChaseCareer Career=career;public StormChaseVehicles Vehicle=vehicle;public FlyCamera Camera=camera;
        public Vector3f Feet=feet;public Vector3 Velocity;public PhysXCharacterController? Controller;public ClientInput? Input;public long LastInputTick,HighestAction;public float Yaw,Pitch,VerticalVelocity;public bool JumpHeld;
        public DateTimeOffset LastInputUtc=DateTimeOffset.UtcNow;public int MessagesThisTick,Photos;public double LastPhoto=-100;public Dictionary<long,ActionResult> Receipts=[];
        public DateTimeOffset ActionRefill=DateTimeOffset.UtcNow;public float ActionTokens=4;public int ParticipatedDay,AtmosphereDay;
        public ChaseContractProgress Contracts=new();public string LastContractDocument="";
    }
    private sealed class Probe(int id,uint ownerId,string characterId,Vector3f position,int level)
    {
        public int Id=id,Level=level;public uint OwnerId=ownerId;public string CharacterId=characterId;public Vector3f Position=position;public bool Active=true,Detached;public float Wind,Overload;public decimal PendingCash;
    }
}
