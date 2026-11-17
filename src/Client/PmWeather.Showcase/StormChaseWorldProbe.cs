using System.Numerics;
using System.Text.Json;
using Engine.Environment;
using Engine.Physics;
using Engine.Physics.Collision;
using Engine.Rendering.Assets;
using Engine.Rendering.Pipeline;
using Engine.Rendering.Scene;
using Shared.Contracts.Rendering;
using Shared.Contracts.World;

/// <summary>CPU/native-PhysX checks for the actual large world, without initializing a renderer.</summary>
internal static class StormChaseWorldProbe
{
    public static int Run(string output,bool destructionStress=false)
    {
        Directory.CreateDirectory(output);
        var checks=new List<string>();
        void Check(bool condition,string message)
        {
            if(!condition)throw new InvalidOperationException(message);
            checks.Add(message);
        }
        var catalog=new MutableRenderAssetCatalog(new BuiltInRenderAssetCatalog());
        var world=new StormChaseWorld(catalog);
        Check(world.Settlements.Count>=7&&world.Settlements.Any(s=>s.Kind=="city")&&world.Settlements.Any(s=>s.Kind=="county city"),"The map contains two city districts and five distinct towns.");
        Check(world.GasStations.Count==world.Settlements.Count&&world.GasStations.Select(s=>s.SettlementId).Distinct().Count()==world.Settlements.Count,"Every settlement has one fuel station.");
        Check(StormChaseWorld.PlayableExtentMeters>=24000&&world.TerrainPageCount>=250,"Native terrain covers a substantially larger playable map.");
        Check(world.BuildingCount>=200&&world.Buildings.Count(b=>b.SettlementId=="meridian")>=100,"The city and towns contain actual populated building districts.");
        var ids=world.DestructibleItems.Select(item=>item.MotionVectorId!).ToHashSet(StringComparer.Ordinal);
        Check(ids.Count==world.DestructibleItems.Count,"Every physically breakable part has a unique persistent identity.");
        Check(world.Buildings.All(building=>building.DestructiblePartIds.Count>0&&building.DestructiblePartIds.All(ids.Contains)),
            "Every building, including the original houses, farms, silos and pier, has real destruction parts.");
        Check(world.Buildings.Any(b=>b.Kind=="pier")&&world.Buildings.Any(b=>b.Kind=="barn"&&Vector3fDistance(b.Position,new(80,0,1040))<50),
            "The original farm interception and breakable lake pier remain in the expanded world.");
        Check(world.GasStations.All(station=>world.Buildings.Any(b=>b.Id==station.BuildingId&&b.Kind=="gas-station")),
            "Refueling metadata points to the actual destructible station buildings.");
        Check(world.Buildings.All(building=>world.ContainsPlayablePosition(building.Position.X,building.Position.Z)),"All authored buildings lie inside the playable extent.");
        Check(world.DestructibleItems.All(item=>item.AllowStaticInstancing&&!item.WritesMotionVectors),"Intact building modules use native static instancing until released.");
        Check(world.RiverPatchCount>=20&&world.RoadCount>40,"Connected native spline roads, river patches and the original lake are present.");
        Check(world.Models.GetModel("house").Parts.All(p=>p.Destructible||p.Category=="foundation"),"Blender house walls, roofs, doors and windows are individually breakable.");
        var camera=new CameraState(new(6000,100,6700),new(0,0,1),58,.5f,16000);
        var city=world.CreateScene(camera,0,null);
        var distant=world.CreateScene(camera with {Position=new(-10000,100,-8000)},0,null);
        Check(city.Items.Count>300&&distant.Items.Count<city.Items.Count,"Distance culling bounds scenery submissions outside populated districts.");
        Console.WriteLine($"Street furniture: {world.StopSignCount} stop signs, {world.StreetNameSignCount} street-name signs, {world.StreetLightCount} street lights, "
            +$"{world.DistributionPoleCount} distribution poles merged into {world.StreetPropItemCount} area meshes ({world.StreetPropVertexCount} vertices).");
        Check(world.StopSignCount>=200&&world.StreetNameSignCount>=150&&world.StreetLightCount>=800&&world.DistributionPoleCount>=800
            &&world.StreetPropItemCount<1600&&world.StreetPropVertexCount<1_900_000,
            "Towns carry stop signs, street-name signs and street lights, regional roads carry pole lines, all merged per area.");
        Console.WriteLine($"Road surface detail: {world.JunctionPatchCount} junction patches, {world.EdgeLineMeters/1000:0.0} km of edge lines.");
        Check(world.JunctionPatchCount>=200&&world.EdgeLineMeters>50_000,"Town junctions are patched and paved regional roads carry edge lines.");
        foreach(var station in world.GasStations)
        {
            Check(float.IsFinite(world.HeightAt(station.ParkingPosition.X,station.ParkingPosition.Z)),"Fuel parking has finite terrain: "+station.Id);
            Check(world.IsLineOfSightClear(station.ParkingPosition+new Vector3f(0,1,0),station.ParkingPosition+new Vector3f(0,3,0)),
                "Fuel parking is outside building footprints: "+station.Id);
        }

        using var destruction=new StormChaseDestruction(world);
        Check(destruction.DynamicBodyCount==0&&destruction.StaticPartBodyCount<=2048&&destruction.TerrainTileCount<=16,
            "Startup creates bounded nearby native collision actors rather than every building actor.");
        var parking=world.GasStations[0].ParkingPosition;
        destruction.EnsurePhysicsAround(parking);
        var vehicle=destruction.Physics.CreateBody(new("probe/external-vehicle",new(parking.X,parking.Y+3,parking.Z),Vector3.Zero,1200,
            new BoxColliderShape(new(1,.6f,2)),PhysicsBodyType.Dynamic));
        destruction.PersistentPhysicsAnchor=()=>new(vehicle.Position.X,vehicle.Position.Y,vehicle.Position.Z);
        int callbacks=0;
        for(int i=0;i<180;i++)destruction.Update(1f/60,null,new(-6500,25,2500),_=>callbacks++);
        Check(callbacks==180,"External chassis force callbacks run exactly once for each native fixed physics step.");
        Check(destruction.Physics.TryGetBodyPose(vehicle,out var vehiclePose)&&float.IsFinite(vehiclePose.M42)
            &&vehiclePose.M42>=world.HeightAt(parking.X,parking.Z),"A parked external native body retains terrain while the observer leaves the city.");
        Check(destruction.DynamicBodyCount<=StormChaseDestruction.MaximumDynamicBodyCount&&destruction.StaticPartBodyCount<=4096,
            "Distance activation preserves finite native actor budgets.");
        destruction.Reset();
        Check(destruction.Physics.TryGetBodyPose(vehicle,out _),"Reset and collision streaming preserve externally owned vehicle actors.");
        destruction.Physics.RemoveBody(vehicle);
        destruction.PersistentPhysicsAnchor=null;

        var waves=new List<object>();
        if(destructionStress)
        {
            var builder=new DefaultRenderFrameBuilder();
            foreach(var target in new[]{new Vector3f(5820,0,6860),new(5520,0,7690),new(6920,0,7040),new(5860,0,8050),
                new(-6640,0,2380),new(3470,0,-5700),new(4400,0,-5510),new(5630,0,6430),new(6280,0,7330),new(5330,0,7860),new(-6480,0,2820),new(3740,0,-5140)})
            {
                float ground=world.HeightAt(target.X+260,target.Z);
                var sim=new PmWeatherSupercellSimulation(new(){Seed=13,PeakWindSpeedMph=400,MaximumWidthMeters=700,TouchdownWindSpeedMph=90,
                    Origin=new(target.X+260,ground,target.Z),CloudBaseMeters=ground+760,CloudTopMeters=ground+3260,
                    InitialStage=3,InitialEnergy=100,InitialGrowthSpeed=3,TornadoGrowthSpeed=2,DecayGrowthSpeed=3});
                sim.Advance(40);
                var weather=builder.Build(1,DateTimeOffset.UnixEpoch,new(1280,720),new RenderScene{Camera=camera,Environment=sim.Current}).Weather;
                int before=destruction.DestroyedPartCount;
                for(int i=0;i<360;i++)destruction.Update(1f/60,weather,target);
                Check(destruction.DynamicBodyCount<=256,"Active destruction respects the 256-body cap at "+target);
                for(int i=0;i<4200;i++)destruction.Update(1f/60,null,target);
                waves.Add(new{Target=target,Released=destruction.DestroyedPartCount-before,Total=destruction.DestroyedPartCount,
                    DynamicAfterSettling=destruction.DynamicBodyCount,NativeSteps=destruction.PhysicsStepCount});
                if(destruction.DestroyedPartCount>2000)break;
            }
            Check(destruction.DestroyedPartCount>2000,"Archived persistent damage permits more than 2,000 actual native releases across successive storm encounters.");
            Check(destruction.DestroyedBuildingIds.Count>0,"Actual part damage marks destroyed buildings for gameplay visibility and fueling checks.");
            destruction.Reset();
            Check(destruction.DestroyedPartCount==0&&destruction.DynamicBodyCount==0&&destruction.DrainBreakEvents().Count==0,
                "Explicit world reset restores intact buildings and removes stale destruction events.");
        }
        string report=Path.Combine(output,"campaign-world-cpu.json");
        File.WriteAllText(report,JsonSerializer.Serialize(new{Schema=1,Passed=checks.Count,Checks=checks,DestructionStress=destructionStress,
            PlayableExtentMeters=StormChaseWorld.PlayableExtentMeters,world.TerrainPageCount,world.BuildingCount,Parts=world.DestructibleItems.Count,
            world.RoadCount,world.RiverPatchCount,Settlements=world.Settlements,GasStations=world.GasStations,
            CityVisibleItems=city.Items.Count,DistantVisibleItems=distant.Items.Count,MaximumDynamicBodies=256,Waves=waves},new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine($"Campaign world: {checks.Count} checks passed; {world.BuildingCount} buildings, {world.DestructibleItems.Count} native breakable parts. {report}");
        return 0;
    }

    private static float Vector3fDistance(Vector3f a,Vector3f b)=>MathF.Sqrt((a.X-b.X)*(a.X-b.X)+(a.Z-b.Z)*(a.Z-b.Z));
}
