using System.Diagnostics;
using System.Numerics;
using System.Text.Json;
using Engine.Rendering.Assets;
using Engine.Rendering.Pipeline;
using Engine.Rendering.Scene;
using Engine.Environment;
using Shared.Contracts.Rendering;
using Shared.Contracts.World;

internal static class StormChaseInteriorProbe
{
    public static int Run(string output)
    {
        Directory.CreateDirectory(output);var checks=new List<string>();var failures=new List<string>();
        void Check(bool value,string text){if(value)checks.Add(text);else failures.Add(text);}
        var timer=Stopwatch.StartNew();var catalog=new MutableRenderAssetCatalog(new BuiltInRenderAssetCatalog());
        var world=new StormChaseWorld(catalog);double construction=timer.Elapsed.TotalSeconds;
        Check(StormChaseWorld.PlayableExtentMeters>=48000,"The native map spans more than 48 km.");
        Check(world.Settlements.Count>=7&&world.GasStations.Count>=7,"The expanded map has seven connected settlements and working station metadata.");
        Check(world.Buildings.Count>=600,"The wider region contains at least six hundred physical buildings.");
        Check(world.BuildingInteriors.Count==world.Buildings.Count(b=>StormChaseModelLibrary.Layout(b.Kind).Width>0),"Every appropriate authored building has a real ground-floor entrance and floor.");
        Check(world.Shelters.Count>=10,"Town and roadside civic rooms expose structurally breakable storm refuge metadata.");
        Check(world.BuildingInteriors.All(i=>i.ClearDoorWidthMeters>1.3f&&i.ClearDoorHeightMeters>2),"All doorway apertures exceed the native capsule width and height.");
        var sampleKinds=new[]{"house","store","gas-station","barn","warehouse","cityhall"};
        var results=new List<object>();using var destruction=new StormChaseDestruction(world);
        Check(!destruction.NativeProvider.IsReducedFallback,"Interior movement uses the actual native PhysX provider.");
        Vector3f Walk(Vector3f start,Vector3f target)
        {
            destruction.ResetAvatarMovement();destruction.EnsurePhysicsAround(start);
            var actual=start;var delta=new Vector3f(target.X-start.X,0,target.Z-start.Z);float length=Horizontal(start,target);
            var direction=delta*(1/Math.Max(.001f,length));
            int steps=(int)MathF.Ceiling(length/.065f)+8;
            for(int step=0;step<steps;step++)
                actual=destruction.ResolveAvatarMovement(actual,actual+direction*.065f,1f/60);
            return actual;
        }
        foreach(var kind in sampleKinds)
        {
            // Flat settlement lots make the doorway test independent of the
            // rolling farm slope; a separate original farmhouse checks stairs.
            var entrance=world.BuildingInteriors.First(i=>i.Kind==kind&&(kind=="barn"?i.BuildingId.Contains("/cedar/"):i.BuildingId.Contains("/meridian/")));
            var exterior=entrance.ExteriorFeet with{Y=world.HeightAt(entrance.ExteriorFeet.X,entrance.ExteriorFeet.Z)+.04f};
            var reached=Walk(exterior,entrance.InteriorFeet);
            float insideDistance=Horizontal(reached,entrance.InteriorFeet);
            Check(insideDistance<.9f,"Native capsule traverses the physical "+kind+" doorway.");
            Check(Math.Abs(reached.Y-entrance.InteriorFeet.Y)<.16f,"The "+kind+" interior foundation supports the capsule.");
            var outside=entrance.WallOutsideFeet with{Y=entrance.WallOutsideFeet.Y+.08f};
            var blocked=Walk(outside,entrance.WallInsideFeet);
            Check(Horizontal(blocked,entrance.WallInsideFeet)>.9f,"The "+kind+" wall remains a native solid collider beside the doorway.");
            results.Add(new{kind,entrance.BuildingId,entrance.ExteriorFeet,entrance.EntranceFeet,entrance.InteriorFeet,reached,insideDistance,blocked});
        }
        foreach(var entrance in new[]{world.BuildingInteriors.First(i=>i.BuildingId=="building/rural/house/4"),
            world.BuildingInteriors.First(i=>i.Kind=="house"&&i.BuildingId.Contains("/dunfield/")),
            world.BuildingInteriors.First(i=>i.Name=="Highway storm shelter")})
        {
            var exterior=entrance.ExteriorFeet with{Y=world.HeightAt(entrance.ExteriorFeet.X,entrance.ExteriorFeet.Z)+.04f};
            var reached=Walk(exterior,entrance.InteriorFeet);
            Check(Horizontal(reached,entrance.InteriorFeet)<.95f,"Native entry remains accessible for "+entrance.Name+" / "+entrance.BuildingId);
            Check(Math.Abs(reached.Y-entrance.InteriorFeet.Y)<.2f,"The rural/remote interior supports native feet: "+entrance.BuildingId);
            results.Add(new{kind=entrance.Kind,entrance.BuildingId,entrance.ExteriorFeet,entrance.InteriorFeet,reached});
        }
        var shelter=world.Shelters.First(s=>s.BuildingId.Contains("/meridian/"));
        var shelterBuilding=world.Buildings.First(b=>b.Id==shelter.BuildingId);
        Vector3f ShelterLocal(Vector3f point)
        {
            float angle=shelterBuilding.YawDegrees*MathF.PI/180,c=MathF.Cos(angle),s=MathF.Sin(angle);point*=shelterBuilding.Scale;
            return shelterBuilding.Position+new Vector3f(point.X*c+point.Z*s,point.Y,-point.X*s+point.Z*c);
        }
        var refugeEntry=Walk(ShelterLocal(new(5,.32f,-.6f)),shelter.Position);
        Check(Horizontal(refugeEntry,shelter.Position)<.9f,"A native capsule walks through the concrete refuge doorway from the civic interior.");
        var refugeWall=Walk(ShelterLocal(new(.3f,.32f,-4.7f)),ShelterLocal(new(2.5f,.32f,-4.7f)));
        Check(Horizontal(refugeWall,ShelterLocal(new(2.5f,.32f,-4.7f)))>.9f,"The refuge's reinforced side walls remain actual native colliders.");
        Check(world.FindShelter(shelter.Position,destruction.IsPartDestroyed)?.Id==shelter.Id,"An intact concrete refuge protects a player inside its actual footprint.");
        Check(world.FindShelter(shelter.Position,id=>id==shelter.StructuralPartIds[0]) is null,"A destroyed refuge wall removes wind protection.");
        Check(world.FindShelter(shelter.Position+new Vector3f(100,0,0)) is null,"Being outside the shelter grants no refuge protection.");
        var lampInterior=world.BuildingInteriors.First(i=>i.Kind=="gas-station");
        var lampCamera=new CameraState(lampInterior.InteriorFeet+new Vector3f(0,1.7f,0),new(0,0,1),70,.1f,16000);
        Check(world.CreateServiceLights(lampCamera,12,_=>false).Any(light=>light.Id==lampInterior.BuildingId+"/level0/interior-ceiling-lamp"),
            "Entered interiors receive a real native local light during daylight.");
        Check(!world.CreateServiceLights(lampCamera,12,id=>id.EndsWith("/interior-ceiling-lamp",StringComparison.Ordinal)).Any(),
            "Destroyed interior lamp fixtures stop contributing local light.");
        destruction.ResetAvatarMovement();destruction.EnsurePhysicsAround(shelter.Position);
        var ceiling=shelter.Position;
        for(int i=0;i<100;i++)ceiling=destruction.ResolveAvatarMovement(ceiling,ceiling+new Vector3f(0,.12f,0),1f/60);
        float roofHeight=world.Buildings.First(b=>b.Id==shelter.BuildingId).Scale*3.625f+world.Buildings.First(b=>b.Id==shelter.BuildingId).Position.Y;
        Check(ceiling.Y<roofHeight-1.7f&&ceiling.Y>shelter.Position.Y+.1f,"The concrete refuge ceiling blocks upward native capsule movement.");

        timer.Restart();int itemCount=0;
        foreach(var town in world.Settlements)
        {
            var p=town.Position+new Vector3f(0,2,0);
            var scene=world.CreateScene(new(p,new Vector3f(0,0,1),70,.1f,16000),0,null);
            itemCount=Math.Max(itemCount,scene.Items.Count);
            Check(scene.Items.Any(i=>i.MotionVectorId?.Contains("/"+town.Id+"/",StringComparison.Ordinal)==true),"Spatial rendering selects actual nearby buildings in "+town.Name+".");
        }
        double sceneSeconds=timer.Elapsed.TotalSeconds;
        Check(destruction.StaticPartBodyCount<=2048,"Native building collision remains within the streamed actor budget.");
        destruction.AdditionalPhysicsAnchors=()=>new[]{world.GasStations[1].ParkingPosition,world.GasStations[3].ParkingPosition};
        destruction.EnsurePhysicsAround(world.GasStations[0].ParkingPosition);
        Check(destruction.TerrainTileCount>=27,"Shared native terrain collision covers three widely separated player/car interests.");
        Check(destruction.StaticPartBodyCount<=2048*3,"Shared static part collision is bounded per independent interest.");
        destruction.AdditionalPhysicsAnchors=null;

        var target=new Vector3f(5820,0,6860);float ground=world.HeightAt(target.X+260,target.Z);
        var sim=new PmWeatherSupercellSimulation(new(){Seed=13,PeakWindSpeedMph=400,MaximumWidthMeters=700,TouchdownWindSpeedMph=90,
            Origin=new(target.X+260,ground,target.Z),CloudBaseMeters=ground+760,CloudTopMeters=ground+3260,
            InitialStage=3,InitialEnergy=100,InitialGrowthSpeed=3,TornadoGrowthSpeed=2,DecayGrowthSpeed=3});
        sim.Advance(40);
        var weather=new DefaultRenderFrameBuilder().Build(1,DateTimeOffset.UnixEpoch,new(1280,720),new RenderScene{
            Camera=new(target+new Vector3f(0,30,0),new(0,0,1),70,.1f,16000),Environment=sim.Current}).Weather;
        for(int i=0;i<180;i++)destruction.Update(1f/60,weather,target);
        var states=destruction.GetReleasedPartStates();
        Check(states.Count>0&&states.Any(s=>s.AngularVelocity.X*s.AngularVelocity.X+s.AngularVelocity.Y*s.AngularVelocity.Y+s.AngularVelocity.Z*s.AngularVelocity.Z>.01f),
            "Authoritative debris snapshots read actual collision/wind rotation and angular velocity.");
        Check(states.Select(s=>s.PartId).SequenceEqual(states.Select(s=>s.PartId).OrderBy(id=>id,StringComparer.Ordinal)),"Released snapshots use sorted stable world part IDs.");
        using(var replica=new StormChaseDestruction(world))
        {
            replica.ApplyReplicaStates(states,true);
            Check(replica.IsReplicaMode&&replica.GetReleasedPartStates().SequenceEqual(states),"Client replica receives exactly the authoritative rigid-body poses and velocities.");
            for(int i=0;i<120;i++)replica.Update(1f/60,weather,target);
            Check(replica.DynamicBodyCount==0&&replica.GetReleasedPartStates().SequenceEqual(states),"Native client steps and tornado weather cannot choose new debris trajectories or landing sites.");
            for(int i=0;i<2400;i++)destruction.Update(1f/60,null,target);
            var landed=destruction.GetReleasedPartStates();
            Check(landed.Any(s=>s.Archived&&s.Sleeping&&s.LinearVelocity==default&&s.AngularVelocity==default),"Settled authoritative debris retains its actual final pose in the baseline.");
            replica.ApplyReplicaStates(landed,true);
            Check(replica.GetReleasedPartStates().SequenceEqual(landed),"Joining/resynchronizing replicas retain every authoritative settled destination exactly.");
            var disabled=landed.First() with{CollisionEnabled=false};
            replica.ApplyReplicaStates(new[]{disabled});replica.EnsurePhysicsAround(disabled.Position);
            for(int i=0;i<30;i++)replica.Update(1f/60,null,disabled.Position);
            Check(!replica.IsPartCollisionResident(disabled.PartId),"Client streaming cannot recreate a debris collider disabled by the server state.");
            replica.ApplyReplicaStates(new[]{disabled with{CollisionEnabled=true}});replica.EnsurePhysicsAround(disabled.Position);
            Check(replica.IsPartCollisionResident(disabled.PartId),"A coherent authoritative collision-enable update restores the server-pose collider.");
        }
        var report=new{Passed=checks.Count,Checks=checks,Failures=failures,ConstructionSeconds=construction,
            world.ContentFingerprint,PlayableExtentMeters=StormChaseWorld.PlayableExtentMeters,world.TerrainPageCount,world.BuildingCount,Parts=world.DestructibleItems.Count,
            InteriorCount=world.BuildingInteriors.Count,ShelterCount=world.Shelters.Count,MaximumSampledVisibleItems=itemCount,
            SevenSceneAssemblySeconds=sceneSeconds,world.Settlements,world.GasStations,world.Shelters,NativeSamples=results};
        string path=Path.Combine(output,"campaign-interior-navigation.json");
        File.WriteAllText(path,JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true}));
        if(failures.Count>0)throw new InvalidOperationException(string.Join(Environment.NewLine,failures));
        Console.WriteLine($"Interior/native navigation: {checks.Count} checks passed; {world.BuildingCount} buildings, {world.BuildingInteriors.Count} interiors. {path}");
        return 0;
    }
    private static float Horizontal(Vector3f a,Vector3f b)=>MathF.Sqrt((a.X-b.X)*(a.X-b.X)+(a.Z-b.Z)*(a.Z-b.Z));
}
