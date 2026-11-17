using System.Numerics;
using System.Text.Json;
using Engine.Environment;
using Engine.Rendering.Assets;
using Engine.Rendering.Pipeline;
using Engine.Rendering.Scene;
using Shared.Contracts.Rendering;
using Shared.Contracts.World;
using StormChase.Protocol;

internal static class StormChaseHousingProbe
{
    public static int Run(string output)
    {
        Directory.CreateDirectory(output);var checks=new List<string>();var errors=new List<string>();
        void Check(bool pass,string message){if(pass)checks.Add(message);else errors.Add(message);}
        var catalog=new MutableRenderAssetCatalog(new BuiltInRenderAssetCatalog());var world=new StormChaseWorld(catalog);string fingerprint=world.ContentFingerprint;
        var housing=new StormChaseHousing(world,catalog);Check(world.ContentFingerprint==fingerprint,"Purchasable model registration preserves the base world fingerprint.");
        Check(housing.Homes.Count==world.BuildingInteriors.Count(i=>i.Kind=="house")&&housing.Homes.Count>300&&housing.Homes.All(h=>world.BuildingInteriors.Any(i=>i.BuildingId==h.BuildingId&&i.Kind=="house")),"Every purchasable home references an existing actual enterable house.");
        var results=new List<object>();
        foreach(var h in new[]{housing.Homes.First(h=>h.BuildingId.Contains("/rural/")),housing.Homes.First(h=>h.BuildingId.Contains("/meridian/"))})
        {
            foreach(var model in StormChaseHousing.Catalogue)
            {
                int valid=-1;NetVector3 placedPosition=default;NetQuaternion q=default;
                for(int slot=0;slot<12;slot++)if(housing.TryPlacement(h.HomeId,model.Offer.Sku,slot,0,[],0,out placedPosition,out q,out _)){valid=slot;break;}
                Check(valid>=0,$"{model.Offer.Sku} has an unobstructed interior slot in {h.BuildingId}.");
                if(valid>=0)
                {
                    var item=new FurnitureState(1,h.HomeId,"probe-owner",model.Offer.Sku,placedPosition,q,default,default,true,true,true,true);
                    Check(!housing.TryPlacement(h.HomeId,model.Offer.Sku,valid,0,[item],2,out _,out _,out _),"Overlapping same-slot "+model.Offer.Sku+" is rejected.");
                    Check(housing.CreateRenderItems([item],new(placedPosition.X,placedPosition.Y,placedPosition.Z)).Count==model.Pieces.Count&&model.Pieces.Count>=3,"Placed "+model.Offer.Sku+" contains its real visible meshes.");
                    results.Add(new{h.HomeId,h.BuildingId,model.Offer.Sku,Slot=valid,Position=placedPosition,Rotation=q});
                }
            }
            Check(housing.IsInsideHome(h.HomeId,new(h.Interior.X,h.Interior.Y,h.Interior.Z)),"Home metadata is inside its real rotated native interior.");
            Check(!housing.IsInsideHome(h.HomeId,new(h.Entrance.X,h.Entrance.Y,h.Entrance.Z)),"Standing outside the front door cannot place furniture.");
        }
        var home=housing.Homes.First(h=>h.BuildingId.Contains("/meridian/"));var entry=world.BuildingInteriors.First(i=>i.BuildingId==home.BuildingId);
        int lampSlot=Enumerable.Range(0,12).First(s=>housing.TryPlacement(home.HomeId,"floor-lamp",s,0,[],0,out _,out _,out _));
        housing.TryPlacement(home.HomeId,"floor-lamp",lampSlot,0,[],0,out var lampPosition,out var lampRotation,out _);
        var lamp=new FurnitureState(1,home.HomeId,"probe-owner","floor-lamp",lampPosition,lampRotation,default,default,true,true,false,true);
        Check(housing.CreateLights([lamp],new(lampPosition.X,lampPosition.Y,lampPosition.Z)).Single().LightType==RenderLightType.Point,"Paid lamps contribute an actual native point light.");
        using var destruction=new StormChaseDestruction(world);using var furniture=new StormChaseFurniturePhysics(world,destruction,[lamp]);
        destruction.AdditionalPhysicsAnchors=()=>furniture.PhysicsAnchors;
        var observer=new Vector3f(home.Interior.X,home.Interior.Y,home.Interior.Z);furniture.Update(1f/60,null,[observer],new HashSet<string>{"probe-owner"});
        Check(!destruction.NativeProvider.IsReducedFallback&&furniture.ResidentCount==1&&furniture.States.Single().CollisionEnabled,"Placed furniture owns a real native PhysX collider.");
        Vector3f Walk(Vector3f start,Vector3f target)
        {
            destruction.ResetAvatarMovement();destruction.EnsurePhysicsAround(start);var current=start;var delta=target-start;float length=MathF.Sqrt(delta.X*delta.X+delta.Z*delta.Z);var direction=new Vector3f(delta.X,0,delta.Z)*(1/Math.Max(.001f,length));
            for(int i=0;i<(int)MathF.Ceiling(length/.05f)+12;i++)current=destruction.ResolveAvatarMovement(current,current+direction*.05f,1f/60);return current;
        }
        var reached=Walk(entry.ExteriorFeet,entry.InteriorFeet);Check(Distance(reached,entry.InteriorFeet)<.9f,"A native player still walks through the furnished front doorway.");
        var p=new Vector3f(lampPosition.X,lampPosition.Y,lampPosition.Z);float by=housing.Building(home.HomeId).YawDegrees*MathF.PI/180;var side=new Vector3f(MathF.Cos(by),0,-MathF.Sin(by));
        var blocked=Walk(p-side*1.2f,p+side*1.2f);Check(Distance(blocked,p+side*1.2f)>.5f,"Native capsule movement collides with the purchased lamp instead of walking through it.");
        string soloPath=Path.Combine(output,"isolated-housing-career-"+Guid.NewGuid().ToString("N")+".json");
        var save=new StormChaseCareer(world,soloPath);save.Save.Cash=8000;save.HousingOwnerName="Housing probe";
        using var localFurniture=new StormChaseFurniturePhysics(world,destruction,[]);
        Check(save.HousingAction(housing,localFurniture,GameplayActionKind.BuyHouse,home.HomeId,-1,0,new(home.Entrance.X,home.Entrance.Y,home.Entrance.Z),false,null,0,"offline-house"),"Offline home purchase debits earned career currency and persists.");
        Check(save.Save.Cash==8000-home.PriceCents/100m,"Offline home price is an exact debit.");
        Check(save.HousingAction(housing,localFurniture,GameplayActionKind.BuyFurniture,4,-1,0,observer,false,null,0,"offline-lamp"),"Offline furniture is paid inventory.");
        var owned=save.SavedFurniture.Single();
        Check(save.HousingAction(housing,localFurniture,GameplayActionKind.PlaceFurniture,owned.FurnitureId,lampSlot,0,observer,false,null,0,"offline-place"),"Offline placement is durable and owns a native pose.");
        var reloaded=new StormChaseCareer(world,soloPath);Check(reloaded.Save.OwnedHomeId==home.HomeId&&reloaded.SavedFurniture.Single().Placed,"Restart restores paid home and placed lamp.");
        // Remove the second local test actor before wind proof; IDs deliberately
        // match a separate solo save, rather than coexisting in one match.
        localFurniture.Dispose();
        var simulation=new PmWeatherSupercellSimulation(new(){Seed=13,PeakWindSpeedMph=400,MaximumWidthMeters=700,TouchdownWindSpeedMph=90,
            Origin=new(p.X+260,world.HeightAt(p.X+260,p.Z),p.Z),CloudBaseMeters=p.Y+760,CloudTopMeters=p.Y+3260,InitialStage=3,InitialEnergy=100,InitialGrowthSpeed=3,TornadoGrowthSpeed=2,DecayGrowthSpeed=3});
        simulation.Advance(40);var weather=new DefaultRenderFrameBuilder().Build(0,DateTimeOffset.UnixEpoch,new(1,1),new RenderScene{Environment=simulation.Current}).Weather;
        float mph=StormChaseDestruction.SampleWindSpeedMph(p,weather),maxRotation=0,maxMotion=0;
        for(int i=0;i<240;i++)
        {
            destruction.Update(1f/60,weather,p);furniture.Update(1f/60,weather,[p],new HashSet<string>{"probe-owner"});var s=furniture.States.Single();maxMotion=Math.Max(maxMotion,Distance(Render(s.Position),p));maxRotation=Math.Max(maxRotation,MathF.Sqrt(s.AngularVelocity.X*s.AngularVelocity.X+s.AngularVelocity.Y*s.AngularVelocity.Y+s.AngularVelocity.Z*s.AngularVelocity.Z));
        }
        var authoritative=furniture.States.Single();Check(mph>85&&maxMotion>.1f&&maxRotation>.05f,"Tornado wind translates and spins the purchased furnishing in the actual shared native scene.");
        var replicaA=housing.CreateRenderItems([authoritative],Render(authoritative.Position));var replicaB=housing.CreateRenderItems([authoritative],Render(authoritative.Position));
        Check(replicaA.SequenceEqual(replicaB)&&replicaA.Count>0,"Two late replicas produce identical visible meshes from the authoritative settled/moving pose.");
        File.WriteAllText(Path.Combine(output,"housing-native-proof.json"),JsonSerializer.Serialize(new{passed=errors.Count==0,checks=checks.Count,items=checks,failures=errors,HomeCount=housing.Homes.Count,fingerprint,results,WindMph=mph,MaximumMotion=maxMotion,MaximumAngularSpeed=maxRotation,authoritative},new JsonSerializerOptions{WriteIndented=true}));
        if(errors.Count>0)throw new InvalidOperationException(string.Join(Environment.NewLine,errors));Console.WriteLine($"Housing native proof: {checks.Count} checks passed.");return 0;
    }
    private static float Distance(Vector3f a,Vector3f b)=>MathF.Sqrt((a.X-b.X)*(a.X-b.X)+(a.Z-b.Z)*(a.Z-b.Z));
    private static Vector3f Render(NetVector3 p)=>new(p.X,p.Y,p.Z);
}
