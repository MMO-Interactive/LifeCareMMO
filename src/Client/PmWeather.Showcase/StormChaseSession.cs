using Engine.Core.Diagnostics;
using Engine.ECS.Components;
using Engine.ECS.World;
using Engine.Environment;
using Engine.Rendering.Assets;
using Engine.Rendering.Pipeline;
using Engine.Rendering.Scene;
using Engine.UniStorm;
using Shared.Contracts.Rendering;
using Shared.Contracts.World;

internal readonly record struct ChaseProbeComponent(float WindMph, float PeakMph, int Samples) : IComponent;

/// <summary>Assembles the engine modules around one persistent chase world.</summary>
internal sealed class StormChaseSession : IDisposable
{
    private readonly DefaultRenderFrameBuilder builder = new();
    private readonly WorldState instruments = new(new ZoneId("mmoi-storm-chase"));
    private readonly Queue<EntityId> probeOrder = new();
    private float nextProbeSample;
    private bool avatarHistoryReset = true;
    private readonly StormChaseDestruction? localDestruction;
    private readonly MutableRenderAssetCatalog assetCatalog;
    private readonly List<StormChaseAvatar> onlineAvatarPool = [];
    private readonly Dictionary<uint, StormChaseAvatar> onlineAvatars = [];
    private Dictionary<string, RenderItem>? onlineParts;
    private static readonly float[] Hours = [15, 17.5f, 22.5f];
    private int lightingIndex;

    public StormChaseSession(MutableRenderAssetCatalog catalog, IRuntimeLogger logger, string? avatarCatalog = null,
        bool simulateLocally = true)
    {
        assetCatalog = catalog;
        World = new(catalog);
        if (Environment.GetEnvironmentVariable("MMOI_STORM_CHASE_NO_VEGETATION") != "1")
            World.EnableVegetation(new StormChaseVegetationLibrary(catalog));
        World.EnableOcean();
        Housing = new(World, catalog);
        Water = new(catalog);
        try
        {
        StormChaseEffects.Register(catalog);
        PowerFlashes = new(catalog);
        Effects = new(logger, (x, z) => World.HeightAt(x, z));
        Avatar = new(catalog, avatarCatalog);
        if (simulateLocally) localDestruction = new(World);
        instruments.RegisterStore<ChaseProbeComponent>();
        catalog.UpsertMaterial(new()
        {
            Handle = new("material", "chase/probe"),
            ShaderProgramId = BuiltInShaderLibrary.PbrLit.Id,
            Tint = new(0.08f, 0.6f, 0.54f, 1), RoughnessStrength = 0.26f, MetalnessStrength = 0.75f,
            EmissiveColor = new(0.05f, 0.8f, 0.65f, 1), EmissiveIntensity = 0.35f
        });
        }
        catch
        {
            Avatar?.Dispose();
            Water.Dispose();
            throw;
        }
    }

    public StormChaseWorld World { get; }
    public StormChaseHousing Housing { get; }
    public StormChaseFurniturePhysics? FurniturePhysics { get; private set; }
    public StormChase.Protocol.HousingState? HousingState { get; private set; }
    public void ConfigureLocalHousing(StormChaseCareer career)
    {
        if(localDestruction is null)throw new InvalidOperationException("Online furniture is server authoritative.");
        FurniturePhysics?.Dispose();FurniturePhysics=new(World,localDestruction,career.SavedFurniture);
        var previousAnchors=localDestruction.AdditionalPhysicsAnchors;
        localDestruction.AdditionalPhysicsAnchors=()=> (previousAnchors?.Invoke()??[])
            .Concat(FurniturePhysics?.PhysicsAnchors??[]).ToArray();
    }
    public StormChaseWater Water { get; }
    /// <summary>LifeCare lot geometry and ghosts for online frames, given the observer and the released (debris) ids.</summary>
    public Func<Vector3f, Func<string, bool>, IEnumerable<RenderItem>>? LotItems { get; set; }
    /// <summary>The render item of a lot piece the server has released as debris, by its stable id.</summary>
    public Func<string, RenderItem?>? LotDebrisPart { get; set; }
    public StormChaseEffects Effects { get; }
    public StormChasePowerFlashes PowerFlashes { get; }
    public StormChaseAvatar Avatar { get; }
    public StormChaseDestruction Destruction => localDestruction ?? throw new InvalidOperationException("Online clients do not own a local physics authority.");
    public StormChaseCareer? Career { get; set; }
    // Opt-in finite profiling; normal play performs no timestamp reads here.
    internal Action<string,double>? FrameTimingSink { get; set; }
    public StormChaseVehicles? Vehicles { get; set; }
    public bool ThirdPerson { get; set; }
    public bool Flashlight { get; set; }
    public int ProbeCount => Career?.ActiveProbeCount ?? probeOrder.Count;
    public float? CampaignHour { get; set; }
    public int? CampaignDay { get; set; }
    /// <summary>Single-player NPC population and avatars; null online (server shards in phase 2) and in captures.</summary>
    public StormChaseNpcLayer? Npcs { get; private set; }
    public string? NearbyResident { get; private set; }
    public void EnableNpcs(int seed, int day, float hour, string? savePath)
    {
        // Opt-in while NPC appearance moves from preset looks to per-person genetics (see docs/npc-distributed-design.md).
        if (localDestruction is null || Npcs is not null || System.Environment.GetEnvironmentVariable("MMOI_STORM_CHASE_NPCS") != "1") return;
        Npcs = new(World, assetCatalog, Avatar.CatalogPath, seed, new(day, hour), savePath);
    }
    public float Hour => CampaignHour ?? Hours[lightingIndex];
    public string LightingName => lightingIndex switch { 1 => "GOLDEN HOUR", 2 => "MOONLIT CHASE", _ => "AFTERNOON" };
    public string ProbeReading
    {
        get
        {
            if (Career is not null) return Career.ProbeReading;
            if (probeOrder.Count == 0) return "";
            instruments.GetStore<ChaseProbeComponent>().TryGet(probeOrder.Last(), out var sample);
            return $"PROBE {sample.WindMph:0} MPH  /  PEAK {sample.PeakMph:0} MPH";
        }
    }

    public void CycleLighting() => lightingIndex = (lightingIndex + 1) % Hours.Length;
    public void SetLighting(int index) => lightingIndex = Math.Clamp(index, 0, Hours.Length - 1);

    public void Reset()
    {
        Destruction.Reset();
        Effects.Reset();
        PowerFlashes.Reset();
        Water.ResetUnderwater();
        foreach (var entity in probeOrder) instruments.DestroyEntity(entity);
        probeOrder.Clear();
        nextProbeSample = 0;
        avatarHistoryReset = true;
    }

    public void DeployProbe(Vector3f observer)
    {
        if (Career is not null) { Career.DeployProbe(observer, CampaignWallSeconds); return; }
        if (probeOrder.Count >= 8) instruments.DestroyEntity(probeOrder.Dequeue());
        var feet = new Vector3f(observer.X, World.HeightAt(observer.X, observer.Z) + 0.35f, observer.Z + 2);
        var id = instruments.SpawnEntity(feet, default);
        instruments.SetComponent(id, new ChaseProbeComponent(0, 0, 0));
        probeOrder.Enqueue(id);
    }
    public double CampaignWallSeconds { get; set; }

    public EnvironmentSceneState GroundStorms(EnvironmentSceneState state) => state with
    {
        Storms = state.ActiveStorms.Select(storm =>
        {
            float ground = World.HeightAt(storm.Position.X, storm.Position.Z);
            float rise=storm.Id.StartsWith("campaign-",StringComparison.Ordinal)?ground:0;
            return storm with
            {
                Position = storm.Position with { Y = ground },
                CloudBaseMeters=storm.CloudBaseMeters+rise,CloudTopMeters=storm.CloudTopMeters+rise,
                Vorticies = storm.ActiveVorticies.Select(vortex => vortex with
                { Position = vortex.Position with { Y = World.HeightAt(vortex.Position.X, vortex.Position.Z) } }).ToArray(),
                Tornadoes = storm.Tornadoes.Select(tornado => tornado with
                {
                    Position = tornado.Position with { Y = World.HeightAt(tornado.Position.X, tornado.Position.Z) },
                    HeightMeters = Math.Max(1, storm.CloudBaseMeters+rise - World.HeightAt(tornado.Position.X, tornado.Position.Z))
                }).ToArray()
            };
        }).ToArray()
    };

    public RenderFrame BuildFrame(long number, float seconds, float wallDelta, EnvironmentSceneState environment,
        CameraState camera, int width, int height, bool precipitation, long history,
        Vector3f? avatarFeet = null, bool walking = false, UniStormSnapshot? snapshot = null, bool advancePhysics = true)
    {
        long phaseStarted=FrameTimingSink is null?0:System.Diagnostics.Stopwatch.GetTimestamp();
        void Mark(string phase)
        {
            if(FrameTimingSink is not { } sink)return;
            sink(phase,System.Diagnostics.Stopwatch.GetElapsedTime(phaseStarted).TotalMilliseconds);
            phaseStarted=System.Diagnostics.Stopwatch.GetTimestamp();
        }
        var preliminary = builder.Build(number, DateTimeOffset.UnixEpoch.AddSeconds(seconds), new(width, height),
            new RenderScene { Camera = camera, Environment = environment, AnimationTimeSeconds = seconds });
        Mark("EnvironmentMapping");
        if (advancePhysics)
        {
            Destruction.Update(wallDelta, preliminary.Weather, camera.Position,
                Vehicles is null ? null : Vehicles.StepForces);
            Vehicles?.AfterPhysics(CampaignWallSeconds);
            if (FurniturePhysics is { } furniture && Career is { } owner)
            {
                furniture.Update(wallDelta,preliminary.Weather,[camera.Position,Vehicles?.Position??camera.Position],
                    new HashSet<string>(StringComparer.Ordinal){owner.HousingOwnerId});
                owner.SaveFurniturePoses(furniture.States);
            }
        }
        Mark("PhysicsAndFurniture");
        SampleProbes(seconds, preliminary.Weather);
        var items = Destruction.Apply(World.CreateScene(camera, seconds, preliminary.Weather, Destruction.ReleasedPartIds).Items).ToList();
        Mark("WorldAndDebris");
        if (Career is not null) items.AddRange(Career.CreateProbeItems());
        if (Vehicles is not null) items.AddRange(Vehicles.CreateRenderItems());
        if (FurniturePhysics is { } localFurniture && Career is { } housingCareer)
        {
            HousingState=housingCareer.HousingSnapshot(Housing,camera.Position,localFurniture.States);
            items.AddRange(Housing.CreateRenderItems(localFurniture.States,camera.Position));
        }
        bool showPlayer = ThirdPerson && avatarFeet.HasValue;
        var actorPosition = showPlayer ? avatarFeet!.Value :
            new Vector3f(-336, World.HeightAt(-336, 80), 80);
        float yaw = showPlayer ? MathF.Atan2(camera.Forward.X, camera.Forward.Z) * 180 / MathF.PI : 165;
        items.AddRange(Avatar.CreateRenderItems(actorPosition, yaw, seconds, walking && showPlayer));
        var npcPoses = new List<RenderSkinPose>();
        if (Npcs is { } npcs && CampaignDay is int npcDay && CampaignHour is float npcHour)
        {
            var now = new StormChase.Npc.NpcTime(npcDay, npcHour);
            npcs.AddToFrame(items, npcPoses, camera.Position, seconds, now);
            NearbyResident = npcs.Nearby(camera.Position, now);
        }
        foreach (var instrument in instruments.Query<TransformComponent>())
            items.Add(new("chase/probe", new("mesh", "chase/box"), new("material", "chase/probe"),
                instrument.Component.Position, new(0.42f, 0.6f, 0.42f), 0, true,
                MotionVectorId: "probe/" + instrument.EntityId.Value));
        var lights = new List<RenderLight>();
        lights.AddRange(World.CreateServiceLights(camera, Hour, Destruction.IsPartDestroyed));
        if (Vehicles is not null) lights.AddRange(Vehicles.CreateHeadlights());
        if (FurniturePhysics is { } litFurniture) lights.AddRange(Housing.CreateLights(litFurniture.States,camera.Position));
        if (Flashlight)
            lights.Add(new("chaser-torch", RenderLightType.Spot, camera.Position, camera.Forward,
                new(1, .92f, .76f, 1), 9, 120, 15, 27));
        Mark("ActorsAndLights");
        var scene = new RenderScene
        {
            Camera = camera, Environment = environment, ClearColor = environment.ClearColor,
            Items = items, Lights = lights, WaterBodies = Water.CreateBodies(preliminary.Weather, camera, seconds)
                .Concat(World.CreateRiverBodies(preliminary.Weather, camera, seconds))
                .Concat(World.CreateOceanBodies(preliminary.Weather, camera, seconds)).ToArray(),
            ReflectionProbes = [new("chase-lake-reflection", StormChaseWater.LakeCenter, new(900, 200, 600), .65f)],
            SkinPoses = Avatar.CreateSkinPoses(avatarHistoryReset).Concat(npcPoses).ToArray(),
            TemporalHistoryVersion = history, AnimationTimeSeconds = seconds,
            PostProcessSettings = new(RenderToneMapper.Filmic, Exposure: 1.15f, Contrast: 1.02f, Saturation: .97f,
                VignetteIntensity: .075f, BloomIntensity: .06f, AmbientOcclusionIntensity: .45f,
                AntiAliasing: RenderAntiAliasingMode.Taa),
            QualityHints = new(ShadowCascadeCount: 2, PostProcessQuality: 2)
        };
        avatarHistoryReset = false;
        scene = Water.ApplyUnderwater(scene, wallDelta, seconds);
        var frame = builder.Build(number, preliminary.UtcTimestamp, preliminary.Viewport, scene);
        Mark("FrameMappingAndWater");
        frame = frame with { Particles = precipitation ? Showcase.CreateChaseDebris(seconds, frame.Weather!, camera.Position, frame.Sky) : null };
        frame = Effects.AddToFrame(frame, seconds, snapshot, precipitation);
        frame=PowerFlashes.AddToFrame(frame, seconds, Destruction.DrainBreakEvents());
        Mark("ParticlesAndEffects");
        return frame;
    }

    /// <summary>Load remote rig resources before requesting a 30-second admission ticket.</summary>
    public void PrepareOnlinePlayers(int maximumPlayers)
    {
        if (localDestruction is not null) throw new InvalidOperationException("An online session must disable local authority.");
        maximumPlayers = Math.Clamp(maximumPlayers, 1, 64);
        while (onlineAvatarPool.Count < maximumPlayers)
            onlineAvatarPool.Add(new StormChaseAvatar(assetCatalog, Avatar.CatalogPath));
        onlineParts ??= World.DestructibleItems.ToDictionary(item => item.MotionVectorId!, StringComparer.Ordinal);
    }

    /// <summary>Render exact server poses. No client debris gravity, wind, damage or autonomous storm stepping occurs here.</summary>
    public RenderFrame BuildOnlineFrame(long number, float seconds, float wallDelta, OnlineChaseState state,
        CameraState camera, int width, int height, bool precipitation, long history,
        IReadOnlyList<StormChase.Protocol.DestructionDelta>? powerEvents = null,
        EnvironmentSceneState? renderEnvironment = null)
    {
        if (localDestruction is not null || !state.BaselineReady || state.World.Environment is null)
            throw new InvalidOperationException("A coherent admitted server world is required before rendering online play.");
        long phaseStarted=FrameTimingSink is null?0:System.Diagnostics.Stopwatch.GetTimestamp();
        void Mark(string phase)
        {
            if(FrameTimingSink is not { } sink)return;
            sink(phase,System.Diagnostics.Stopwatch.GetElapsedTime(phaseStarted).TotalMilliseconds);
            phaseStarted=System.Diagnostics.Stopwatch.GetTimestamp();
        }
        onlineParts ??= World.DestructibleItems.ToDictionary(item => item.MotionVectorId!, StringComparer.Ordinal);
        // Observer-local UniStorm atmosphere may vary visually; authoritative storm cells and poses are retained verbatim.
        var source = renderEnvironment is null ? state.World.Environment
            : renderEnvironment with { Storms = state.World.Environment.Storms };
        CampaignHour = source.TimeOfDayHours;
        var preliminary = builder.Build(number, DateTimeOffset.UnixEpoch.AddSeconds(seconds), new(width, height),
            new RenderScene { Camera = camera, Environment = source, AnimationTimeSeconds = seconds });
        Mark("EnvironmentMapping");
        var released = state.Debris.Select(body => body.StablePartId).ToHashSet(StringComparer.Ordinal);
        var items = World.CreateScene(camera, seconds, preliminary.Weather, released).Items
            .Where(item => item.MotionVectorId is null || !released.Contains(item.MotionVectorId)).ToList();
        foreach (var body in state.Debris)
        {
            if (body.Collected) continue;
            RenderItem? original = onlineParts.TryGetValue(body.StablePartId, out var authored) ? authored : LotDebrisPart?.Invoke(body.StablePartId);
            if (original is null) continue;
            float dx = body.Position.X-camera.Position.X, dz = body.Position.Z-camera.Position.Z;
            if (dx*dx+dz*dz > 5000*5000) continue; // visual distance culling; complete bodies remain in network state
            var rotation = OnlineRotation(body.Rotation);
            items.Add(original with { Position = new(body.Position.X,body.Position.Y,body.Position.Z),
                RotationEulerDegrees = OnlineEuler(rotation), RotationYawDegrees = 0,
                MotionVectorId = "online/debris/"+body.BodyId, AllowStaticInstancing = false, WritesMotionVectors = true });
        }
        if (LotItems is { } lotItems) items.AddRange(lotItems(camera.Position, released.Contains));
        Mark("WorldAndDebris");
        var poses = new List<RenderSkinPose>();
        var presentPlayers=state.World.Players.Select(player=>player.PlayerId).ToHashSet();
        foreach(var absent in onlineAvatars.Keys.Where(id=>!presentPlayers.Contains(id)).ToArray())onlineAvatars.Remove(absent);
        foreach (var player in state.World.Players)
        {
            if (player.Mode == StormChase.Protocol.PlayerMovementMode.Driving) continue;
            if (player.PlayerId == state.PlayerId && !ThirdPerson) continue;
            float dx=player.Position.X-camera.Position.X,dz=player.Position.Z-camera.Position.Z;
            if(dx*dx+dz*dz>1500*1500)continue;
            if (!onlineAvatars.TryGetValue(player.PlayerId, out var rig))
            {
                if (onlineAvatars.Count >= onlineAvatarPool.Count) continue;
                rig=onlineAvatarPool.First(candidate=>!onlineAvatars.Values.Contains(candidate));onlineAvatars.Add(player.PlayerId,rig);
                avatarHistoryReset=true;
            }
            bool walking = player.Velocity.X*player.Velocity.X+player.Velocity.Z*player.Velocity.Z>.04f;
            items.AddRange(rig.CreateRenderItems(new(player.Position.X,player.Position.Y,player.Position.Z),player.Yaw*180/MathF.PI,seconds,walking));
            poses.AddRange(rig.CreateSkinPoses(avatarHistoryReset));
        }
        var lights = new List<RenderLight>();
        HousingState=state.World.Housing;
        if(HousingState is { } homeState)
        {
            items.AddRange(Housing.CreateRenderItems(homeState.Furniture,camera.Position));
            lights.AddRange(Housing.CreateLights(homeState.Furniture,camera.Position));
        }
        lights.AddRange(World.CreateServiceLights(camera, Hour, released.Contains));
        foreach (var vehicle in state.World.Vehicles)
        {
            var position=new Vector3f(vehicle.Position.X,vehicle.Position.Y,vehicle.Position.Z);
            float dx=position.X-camera.Position.X,dz=position.Z-camera.Position.Z;
            if(dx*dx+dz*dz>4200*4200)continue;
            string model=vehicle.VehicleType switch {1=>"suv",2=>"interceptor",3=>"chase-truck",4=>"police-cruiser",5=>"fire-engine",6=>"ambulance",_=>"pickup"};
            var rotation=OnlineRotation(vehicle.Rotation);var parent=System.Numerics.Matrix4x4.CreateFromQuaternion(rotation);
            foreach(var item in World.Models.CreateItems(model,position,0,"online/vehicle/"+vehicle.VehicleId))
            {
                var local=new System.Numerics.Vector3(item.Position.X-position.X,item.Position.Y-position.Y,item.Position.Z-position.Z);
                local=System.Numerics.Vector3.Transform(local,parent);
                var euler=item.RotationEulerDegrees??new Vector3f(0,item.RotationYawDegrees,0);
                var part=System.Numerics.Matrix4x4.CreateRotationX(euler.X*MathF.PI/180)
                    *System.Numerics.Matrix4x4.CreateRotationY(euler.Y*MathF.PI/180)
                    *System.Numerics.Matrix4x4.CreateRotationZ(euler.Z*MathF.PI/180)*parent;
                items.Add(item with {Position=position+new Vector3f(local.X,local.Y,local.Z),
                    RotationEulerDegrees=OnlineEuler(System.Numerics.Quaternion.CreateFromRotationMatrix(part)),
                    RotationYawDegrees=0,AllowStaticInstancing=false,WritesMotionVectors=true});
            }
            // Other players' department vehicles flash their beacons too.
            lights.AddRange(StormChaseEmergencyLights.Create(World.Models,model,local=>
            {
                var turned=System.Numerics.Vector3.Transform(new System.Numerics.Vector3(local.X,local.Y,local.Z),parent);
                return position+new Vector3f(turned.X,turned.Y,turned.Z);
            },seconds));
            if(vehicle.DriverId!=0)
            {
                var direction=System.Numerics.Vector3.Transform(System.Numerics.Vector3.UnitZ,rotation);
                foreach(float side in new[]{-.7f,.7f})
                {
                    var origin=System.Numerics.Vector3.Transform(new System.Numerics.Vector3(side,1,2.3f),rotation);
                    lights.Add(new("online/vehicle/"+vehicle.VehicleId+"/headlight/"+side,RenderLightType.Spot,
                        position+new Vector3f(origin.X,origin.Y,origin.Z),new(direction.X,direction.Y,direction.Z),
                        new(1,.93f,.79f,1),1800,150,15,27,CastsShadow:false));
                }
            }
        }
        foreach(var probe in state.World.Probes.Where(probe=>probe.Active))
            items.Add(new("online/probe",new("mesh","chase/box"),new("material","chase/probe"),
                new(probe.Position.X,probe.Position.Y+.3f,probe.Position.Z),new(.42f,.6f,.42f),0,true,
                MotionVectorId:"online/probe/"+probe.ProbeId));
        if(Flashlight)lights.Add(new("online/chaser-torch",RenderLightType.Spot,camera.Position,camera.Forward,new(1,.92f,.76f,1),9,120,15,27));
        Mark("ActorsAndLights");
        var scene=new RenderScene { Camera=camera,Environment=source,ClearColor=source.ClearColor,Items=items,Lights=lights,
            WaterBodies=Water.CreateBodies(preliminary.Weather,camera,seconds).Concat(World.CreateRiverBodies(preliminary.Weather,camera,seconds)).Concat(World.CreateOceanBodies(preliminary.Weather,camera,seconds)).ToArray(),
            SkinPoses=poses,TemporalHistoryVersion=history,AnimationTimeSeconds=seconds,
            PostProcessSettings=new(RenderToneMapper.Filmic,Exposure:1.15f,Contrast:1.02f,Saturation:.97f,
                VignetteIntensity:.075f,BloomIntensity:.06f,AmbientOcclusionIntensity:.45f,AntiAliasing:RenderAntiAliasingMode.Taa),
            QualityHints=new(ShadowCascadeCount:2,PostProcessQuality:2) };
        avatarHistoryReset=false;scene=Water.ApplyUnderwater(scene,wallDelta,seconds);
        var frame=builder.Build(number,preliminary.UtcTimestamp,preliminary.Viewport,scene);
        Mark("FrameMappingAndWater");
        frame=frame with {Particles=precipitation?Showcase.CreateChaseDebris(seconds,frame.Weather!,camera.Position,frame.Sky):null};
        frame=Effects.AddToFrame(frame,seconds,null,precipitation);
        var events=(powerEvents??[]).Select(item=>new StormChaseBreakEvent("server/pole/"+item.ObjectId,
            new(item.Position.X,item.Position.Y,item.Position.Z),true)).ToArray();
        frame=PowerFlashes.AddToFrame(frame,seconds,events);
        Mark("ParticlesAndEffects");
        return frame;
    }

    private static System.Numerics.Quaternion OnlineRotation(StormChase.Protocol.NetQuaternion value)
    {
        var q=new System.Numerics.Quaternion(value.X,value.Y,value.Z,value.W);
        return q.LengthSquared()<.0001f?System.Numerics.Quaternion.Identity:System.Numerics.Quaternion.Normalize(q);
    }
    private static Vector3f OnlineEuler(System.Numerics.Quaternion rotation)
    {
        var m=System.Numerics.Matrix4x4.CreateFromQuaternion(rotation);
        float sinY=Math.Clamp(-m.M13,-1,1),yaw=MathF.Asin(sinY),pitch,roll;
        if(MathF.Abs(MathF.Cos(yaw))>.00001f){pitch=MathF.Atan2(m.M23,m.M33);roll=MathF.Atan2(m.M12,m.M11);}
        else{pitch=MathF.Atan2(MathF.Sign(sinY)*m.M21,m.M22);roll=0;}
        return new(pitch*180/MathF.PI,yaw*180/MathF.PI,roll*180/MathF.PI);
    }

    private void SampleProbes(float seconds, RenderWeatherState? weather)
    {
        if (Career is not null) return;
        if (seconds < nextProbeSample) return;
        nextProbeSample = seconds + .2f;
        var store = instruments.GetStore<ChaseProbeComponent>();
        foreach (var probe in instruments.Query<TransformComponent>())
        {
            float mph = StormChaseDestruction.SampleWindSpeedMph(probe.Component.Position, weather);
            store.TryGet(probe.EntityId, out var previous);
            store.Set(probe.EntityId, new(mph, Math.Max(previous.PeakMph, mph), previous.Samples + 1));
        }
    }

    public static float EstimateWind(Vector3f point, RenderStormCellState? storm)
    {
        if (storm is null) return 0;
        var tornado = storm.Tornadoes.FirstOrDefault();
        float radius = Math.Max(25, (tornado?.SmoothedWidthMeters ?? storm.WidthMeters) * .5f);
        float dx = point.X - storm.Position.X, dz = point.Z - storm.Position.Z;
        float distance = MathF.Sqrt(dx * dx + dz * dz);
        float fraction = distance < radius ? distance / radius : radius / Math.Max(radius, distance);
        float mph = (tornado?.SmoothedWindSpeed ?? storm.WindSpeed) * fraction;
        return Math.Max(0, mph * Math.Clamp(1 - distance / Math.Max(900, radius * 7), 0, 1));
    }

    public void Dispose()
    {
        Npcs?.Dispose();
        FurniturePhysics?.Dispose();
        localDestruction?.Dispose();
        foreach (var remote in onlineAvatarPool) remote.Dispose();
        Avatar.Dispose();
        Water.Dispose();
    }
}
