using System.Numerics;
using Engine.Rendering.Assets;
using Shared.Contracts.Content;
using Shared.Contracts.Rendering;
using Shared.Contracts.World;
using StormChase.Protocol;

internal sealed record ChaseFurnitureDefinition(FurnitureOfferState Offer,Vector3f Size,float Mass,IReadOnlyList<ChaseFurniturePiece> Pieces);
internal sealed record ChaseFurniturePiece(string Name,Vector3f Position,Vector3f Size,string Material);

/// <summary>Shared housing catalogue and visible furniture; no client physics integration occurs here.</summary>
internal sealed class StormChaseHousing
{
    public const int MaximumFurniturePerHome=24;
    private readonly StormChaseWorld world;
    private readonly Dictionary<int,IReadOnlyList<RenderItem>> interiorGeometry=[];
    public IReadOnlyList<HomeSiteState> Homes {get;}
    public static readonly ChaseFurnitureDefinition[] Catalogue=CreateCatalogue();
    private static readonly AssetHandle Cube=new("mesh","housing/furniture-cube");
    public StormChaseHousing(StormChaseWorld world,MutableRenderAssetCatalog? catalog=null)
    {
        this.world=world;
        Homes=world.BuildingInteriors.Where(i=>i.Kind=="house").OrderBy(i=>StormChaseWorld.IsAppendedHome(i.BuildingId)?1:0)
            .ThenBy(i=>i.BuildingId,StringComparer.Ordinal).Select((i,index)=>
        {
            var b=world.Buildings.First(b=>b.Id==i.BuildingId);
            string town=world.Settlements.FirstOrDefault(t=>t.Id==b.SettlementId)?.Name??"Prairie farms";
            return new HomeSiteState(index+1,b.Id,town+" home "+(index+1),Net(i.EntranceFeet),Net(i.InteriorFeet),PriceFor(b.SettlementId),"","",false,false);
        }).ToArray();
        if(catalog is not null)RegisterFurniture(catalog);
    }
    public static long PriceFor(string region)=>region switch{"rural"=>180000,"meridian" or "northcrest"=>420000,_=>260000};
    public HomeSiteState? Home(int id)=>Homes.FirstOrDefault(h=>h.HomeId==id);
    public StormChaseBuilding Building(int id)=>world.Buildings.First(b=>b.Id==Home(id)?.BuildingId);
    public bool IsInsideHome(int id,Vector3f feet)
    {
        if(Home(id) is null)return false;var b=Building(id);var p=ToLocal(b,feet);
        return p.X is >-6.55f and <6.55f&&p.Z is >-5.05f and <5.05f&&p.Y is >.1f and <2;
    }
    public static ChaseFurnitureDefinition? Furniture(string sku)=>Catalogue.FirstOrDefault(c=>c.Offer.Sku==sku);
    public static ChaseFurnitureDefinition? Furniture(int id)=>Catalogue.FirstOrDefault(c=>c.Offer.Id==id);
    public void RegisterFurniture(MutableRenderAssetCatalog catalog)
    {
        catalog.UpsertMesh(StormChaseTerrainGeometry.Box(Cube));
        foreach(var (name,tint) in new (string,ColorRgba)[]{("wood",new(.28f,.16f,.08f,1)),("fabric",new(.15f,.3f,.36f,1)),
            ("metal",new(.19f,.22f,.24f,1)),("cream",new(.83f,.78f,.65f,1)),("screen",new(.08f,.54f,.38f,1)),("lamp",new(.95f,.83f,.6f,1))})
            catalog.UpsertMaterial(new(){Handle=new("material","housing/"+name),ShaderProgramId=BuiltInShaderLibrary.PbrLit.Id,
                LightingProfile=RenderMaterialLightingProfile.MetallicRoughness,Tint=tint,RoughnessStrength=.78f,
                MetalnessStrength=name=="metal"?.55f:0,EmissiveColor=name is "screen" or "lamp"?tint:ColorRgba.Black,
                EmissiveIntensity=name=="lamp"?1.8f:name=="screen"?.4f:0});
    }
    public IReadOnlyList<RenderItem> CreateRenderItems(IEnumerable<FurnitureState> furniture,Vector3f observer)
    {
        var items=new List<RenderItem>();
        foreach(var state in furniture)
        {
            if(!state.Placed||Furniture(state.Sku) is not { } model||Distance(Render(state.Position),observer)>280)continue;
            var rotation=Quaternion.Normalize(new(state.Rotation.X,state.Rotation.Y,state.Rotation.Z,state.Rotation.W));
            var matrix=Matrix4x4.CreateFromQuaternion(rotation);var euler=Euler(matrix);
            foreach(var piece in model.Pieces)
            {
                var p=Vector3.Transform(new(piece.Position.X,piece.Position.Y,piece.Position.Z),rotation);
                items.Add(new("housing/"+piece.Material,Cube,new AssetHandle("material","housing/"+piece.Material),
                    Render(state.Position)+new Vector3f(p.X,p.Y,p.Z),piece.Size,0,true,
                    WritesMotionVectors:!state.Archived,RotationEulerDegrees:euler,
                    MotionVectorId:"housing/furniture/"+state.FurnitureId+"/"+piece.Name,AllowStaticInstancing:state.Archived));
            }
        }
        return items;
    }
    public IReadOnlyList<RenderLight> CreateLights(IEnumerable<FurnitureState> furniture,Vector3f observer)=>furniture
        .Where(f=>f.Placed&&Furniture(f.Sku)?.Offer.Lamp==true&&Distance(Render(f.Position),observer)<28)
        .Select(f=>
        {
            var q=Quaternion.Normalize(new(f.Rotation.X,f.Rotation.Y,f.Rotation.Z,f.Rotation.W));var offset=Vector3.Transform(new(0,1.6f,0),q);
            return new RenderLight("housing/furniture/"+f.FurnitureId+"/light",RenderLightType.Point,
                Render(f.Position)+new Vector3f(offset.X,offset.Y,offset.Z),new(0,-1,0),new(1,.87f,.67f,1),55,14,CastsShadow:false);
        }).Take(8).ToArray();

    // Fixed slots avoid arbitrary client coordinates, native doorway occlusion,
    // intersections with authored furniture and out-of-lot/road placement.
    public bool TryPlacement(int homeId,string sku,int slot,float extraYaw,IEnumerable<FurnitureState> existing,int movingId,
        out NetVector3 position,out NetQuaternion rotation,out string reason)
    {
        position=default;rotation=NetQuaternion.Identity;reason="Choose a valid furnishing slot.";
        if(Home(homeId) is null||Furniture(sku) is not { } model||slot is <0 or >=12||!float.IsFinite(extraYaw))return false;
        var building=Building(homeId);
        (float X,float Z,float Yaw)[] places=[(2.45f,2.8f,0),(4.4f,2.4f,MathF.PI*.5f),(4.4f,.1f,MathF.PI*.5f),
            (2.6f,-.9f,MathF.PI*.5f),(.3f,-1.2f,0),(.6f,-3.2f,0),(-3.9f,3.2f,MathF.PI*.5f),
            (-5.45f,3.15f,0),(5.75f,-3.1f,MathF.PI*.5f),(-5.7f,-3.2f,MathF.PI*.5f),(2.2f,1.0f,0),(.3f,1.3f,0)];
        var place=places[slot];float yaw=place.Yaw+extraYaw;float c=MathF.Cos(yaw),s=MathF.Sin(yaw);
        var shape=StormChaseLotGeometry.Rectangle(new(place.X,place.Z),model.Size.X*.5f/building.Scale,model.Size.Z*.5f/building.Scale,yaw);
        if(shape.Minimum.X<-6.55f||shape.Maximum.X>6.55f||shape.Minimum.Z<-5.05f||shape.Maximum.Z>5.05f
            ||(shape.Minimum.X<.8f&&shape.Maximum.X>-.8f&&shape.Maximum.Z>2.5f))
        {reason="That item would block the entrance or cross a wall. Choose another slot.";return false;}
        float by=building.YawDegrees*MathF.PI/180,bc=MathF.Cos(by),bs=MathF.Sin(by);
        var local=new Vector3f(place.X*building.Scale,.32f*building.Scale,place.Z*building.Scale);
        var worldPoint=building.Position+new Vector3f(local.X*bc+local.Z*bs,local.Y,-local.X*bs+local.Z*bc);
        var footprint=StormChaseLotGeometry.Rectangle(new(worldPoint.X,worldPoint.Z),model.Size.X*.5f,model.Size.Z*.5f,by+yaw);
        foreach(var item in existing.Where(f=>f.Placed&&f.HomeId==homeId&&f.FurnitureId!=movingId))
        {
            if(Furniture(item.Sku) is not { } other)continue;
            var q=item.Rotation;float angle=MathF.Atan2(2*(q.W*q.Y+q.X*q.Z),1-2*(q.Y*q.Y+q.Z*q.Z));
            var occupied=StormChaseLotGeometry.Rectangle(new(item.Position.X,item.Position.Z),other.Size.X*.5f,other.Size.Z*.5f,angle);
            if(StormChaseLotGeometry.Distance(footprint.Polygon,occupied.Polygon)<.12f){reason="Another furnishing occupies that slot.";return false;}
        }
        // Authored interior modules are real colliders too. Reject their complete
        // transformed footprints rather than hiding them under purchased props.
        if(!interiorGeometry.TryGetValue(homeId,out var authored))interiorGeometry[homeId]=authored=world.BuildingGeometryItems(building);
        foreach(var piece in authored)
        {
            if(!world.Models.TryGetPart(piece,out var part)||part.Category!="interior"||part.Name=="interior-ceiling-lamp")continue;
            var center=ToLocal(building,piece.Position);
            if(center.Y>2.4f)continue;
            var fixedBox=StormChaseLotGeometry.Rectangle(new(piece.Position.X,piece.Position.Z),piece.Scale.X*.5f,piece.Scale.Z*.5f,(piece.RotationEulerDegrees?.Y??building.YawDegrees)*MathF.PI/180);
            if(StormChaseLotGeometry.Distance(footprint.Polygon,fixedBox.Polygon)<.12f){reason="Leave room around the home's existing furniture.";return false;}
        }
        position=Net(worldPoint);var quaternion=Quaternion.CreateFromAxisAngle(Vector3.UnitY,by+yaw);
        rotation=new(quaternion.X,quaternion.Y,quaternion.Z,quaternion.W);reason="Furnishing placed.";return true;
    }
    private static Vector3f ToLocal(StormChaseBuilding b,Vector3f world)
    {var p=(world-b.Position)*(1/b.Scale);float y=b.YawDegrees*MathF.PI/180,c=MathF.Cos(y),s=MathF.Sin(y);return new(p.X*c-p.Z*s,p.Y,p.X*s+p.Z*c);}
    private static float Distance(Vector3f a,Vector3f b)=>MathF.Sqrt((a.X-b.X)*(a.X-b.X)+(a.Z-b.Z)*(a.Z-b.Z));
    private static NetVector3 Net(Vector3f p)=>new(p.X,p.Y,p.Z);
    private static Vector3f Render(NetVector3 p)=>new(p.X,p.Y,p.Z);
    private static Vector3f Euler(Matrix4x4 m)
    {
        float sinY=Math.Clamp(-m.M13,-1,1),y=MathF.Asin(sinY),x,z;
        if(MathF.Abs(MathF.Cos(y))>.00001f){x=MathF.Atan2(m.M23,m.M33);z=MathF.Atan2(m.M12,m.M11);}
        else{x=MathF.Atan2(MathF.Sign(sinY)*m.M21,m.M22);z=0;}
        return new(x*180/MathF.PI,y*180/MathF.PI,z*180/MathF.PI);
    }
    private static ChaseFurnitureDefinition[] CreateCatalogue()
    {
        ChaseFurniturePiece P(string name,float x,float y,float z,float sx,float sy,float sz,string mat)=>new(name,new(x,y,z),new(sx,sy,sz),mat);
        return [
            new(new(1,"chase-sofa","Chaser sofa",14000,"Comfortable seating for friends after a long chase.",false),new(2.4f,1.15f,1),48,
                [P("seat",0,.48f,0,2.4f,.4f,1,"fabric"),P("back",0,.9f,-.38f,2.4f,.5f,.24f,"fabric"),P("left",-1.08f,.75f,0,.22f,.8f,1,"fabric"),P("right",1.08f,.75f,0,.22f,.8f,1,"fabric"),
                 P("foot-fl",-.95f,.14f,.3f,.12f,.28f,.12f,"wood"),P("foot-fr",.95f,.14f,.3f,.12f,.28f,.12f,"wood"),P("foot-bl",-.95f,.14f,-.3f,.12f,.28f,.12f,"wood"),P("foot-br",.95f,.14f,-.3f,.12f,.28f,.12f,"wood")]),
            new(new(2,"coffee-table","Coffee table",6500,"A solid wood table for the living room.",false),new(1.25f,.58f,.75f),15,
                [P("top",0,.53f,0,1.25f,.1f,.75f,"wood"),P("leg-left",-.5f,.25f,0,.1f,.5f,.6f,"wood"),P("leg-right",.5f,.25f,0,.1f,.5f,.6f,"wood")]),
            new(new(3,"reading-chair","Reading chair",5500,"A compact chair for guests.",false),new(.82f,1.1f,.82f),12,
                [P("seat",0,.45f,0,.82f,.16f,.82f,"fabric"),P("back",0,.83f,-.32f,.82f,.54f,.18f,"fabric"),P("stand",0,.2f,0,.62f,.4f,.62f,"wood")]),
            new(new(4,"floor-lamp","Warm floor lamp",8500,"A real local light for evening hangouts.",true),new(.5f,1.8f,.5f),8,
                [P("base",0,.06f,0,.5f,.12f,.5f,"metal"),P("post",0,.8f,0,.08f,1.5f,.08f,"metal"),P("shade",0,1.62f,0,.5f,.36f,.5f,"lamp")]),
            new(new(5,"storm-desk","Storm analysis desk",21000,"A workstation with a glowing weather monitor.",false),new(1.8f,1.65f,.85f),32,
                [P("desk",0,.78f,0,1.8f,.16f,.85f,"wood"),P("left",-.72f,.37f,0,.16f,.74f,.7f,"wood"),P("right",.72f,.37f,0,.16f,.74f,.7f,"wood"),P("monitor-stand",0,.95f,-.25f,.12f,.23f,.12f,"metal"),P("monitor",0,1.35f,-.25f,.8f,.6f,.12f,"screen")]),
            new(new(6,"bookcase","Research bookcase",11500,"Shelves for the storm chaser's library.",false),new(1.2f,1.85f,.38f),24,
                [P("back",0,.93f,-.13f,1.2f,1.85f,.12f,"wood"),P("side-left",-.55f,.93f,0,.1f,1.85f,.38f,"wood"),P("side-right",.55f,.93f,0,.1f,1.85f,.38f,"wood"),P("shelf1",0,.3f,0,1.2f,.08f,.38f,"wood"),P("shelf2",0,.9f,0,1.2f,.08f,.38f,"wood"),P("shelf3",0,1.5f,0,1.2f,.08f,.38f,"wood")]),
            new(new(7,"guest-bed","Guest bed",26000,"A furnished spare room for visiting chasers.",false),new(1.45f,.84f,2.1f),42,
                [P("frame",0,.22f,0,1.45f,.44f,2.1f,"wood"),P("mattress",0,.57f,0,1.42f,.3f,2.05f,"cream"),P("pillow",0,.78f,-.72f,.95f,.12f,.35f,"fabric")])
        ];
    }
}
