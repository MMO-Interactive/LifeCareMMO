using System.Numerics;
using Engine.CrestWater.Content;
using Engine.Paths;
using Engine.Paths.Terrain;
using Engine.Rendering.Assets;
using Engine.Rendering.Scene;
using Engine.Splines;
using Shared.Contracts.Content;
using Shared.Contracts.Rendering;
using Shared.Contracts.World;

internal sealed record StormChaseSettlement(string Id, string Name, string Kind, Vector3f Position, float RadiusMeters);
internal sealed record StormChaseGasStation(string Id, string Name, Vector3f Position, string SettlementId,
    string BuildingId, Vector3f ParkingPosition);
internal sealed record StormChaseBuilding(string Id, string SettlementId, string Kind, Vector3f Position,
    Vector3f SizeMeters, IReadOnlyList<string> DestructiblePartIds)
{
    public Vector3f BoundsMinimum { get; init; }
    public Vector3f BoundsMaximum { get; init; }
    public float YawDegrees {get;init;}
    public float Scale {get;init;}=1;
    public string DisplayName {get;init;}="";
}

internal sealed record StormChaseLotRelocation(string BuildingId,Vector3f RequestedPosition,Vector3f PlacedPosition);
internal sealed record StormChaseRoadTriangle(string RoadMesh,Vector2[] Polygon,Vector2 Minimum,Vector2 Maximum);
internal sealed record StormChaseLotShape(Vector2[] Polygon,Vector3f Minimum,Vector3f Maximum)
{
    public StormChaseLotShape Translate(Vector2 offset)=>new(Polygon.Select(p=>p+offset).ToArray(),
        Minimum+new Vector3f(offset.X,0,offset.Y),Maximum+new Vector3f(offset.X,0,offset.Y));
}

/// <summary>Complete transformed authored bounds against the actual spline road triangles, including shoulders.</summary>
internal static class StormChaseLotGeometry
{
    public static StormChaseLotShape FromItems(IEnumerable<RenderItem> items,IRenderAssetCatalog catalog)
    {
        var points=new List<Vector2>();
        var low=new Vector3(float.PositiveInfinity);var high=new Vector3(float.NegativeInfinity);
        foreach(var item in items)
        {
            if(!catalog.TryGetMesh(item.Mesh,out var mesh)||mesh is null)throw new InvalidOperationException("Missing lot mesh: "+item.Mesh);
            var localLow=new Vector3(float.PositiveInfinity);var localHigh=new Vector3(float.NegativeInfinity);
            foreach(var vertex in mesh.Vertices)
            {
                var p=Native(vertex.Position);localLow=Vector3.Min(localLow,p);localHigh=Vector3.Max(localHigh,p);
            }
            var transform=Transform(item);
            for(int corner=0;corner<8;corner++)
            {
                var p=Vector3.Transform(new((corner&1)==0?localLow.X:localHigh.X,(corner&2)==0?localLow.Y:localHigh.Y,
                    (corner&4)==0?localLow.Z:localHigh.Z),transform);
                low=Vector3.Min(low,p);high=Vector3.Max(high,p);points.Add(new(p.X,p.Z));
            }
        }
        if(points.Count==0)throw new InvalidOperationException("Empty building lot.");
        return new(Hull(points),new(low.X,low.Y,low.Z),new(high.X,high.Y,high.Z));
    }

    public static IEnumerable<StormChaseRoadTriangle> RoadTriangles(IEnumerable<RenderItem> roads,IRenderAssetCatalog catalog)
    {
        foreach(var road in roads)
        {
            if(!catalog.TryGetMesh(road.Mesh,out var mesh)||mesh is null)throw new InvalidOperationException("Missing road mesh: "+road.Mesh);
            var transform=Transform(road);
            var vertices=mesh.Vertices.Select(vertex=>Vector3.Transform(Native(vertex.Position),transform)).Select(p=>new Vector2(p.X,p.Z)).ToArray();
            var indices=mesh.WideIndices.Count>0?mesh.WideIndices:mesh.Indices.Select(i=>(uint)i).ToArray();
            for(int i=0;i<indices.Count;i+=3)
            {
                var polygon=new[]{vertices[indices[i]],vertices[indices[i+1]],vertices[indices[i+2]]};
                if(Math.Abs(Cross(polygon[1]-polygon[0],polygon[2]-polygon[0]))<.0001)continue;
                yield return new(road.Mesh.Id,polygon,Vector2.Min(polygon[0],Vector2.Min(polygon[1],polygon[2])),
                    Vector2.Max(polygon[0],Vector2.Max(polygon[1],polygon[2])));
            }
        }
    }

    public static StormChaseLotShape Rectangle(Vector2 centre,float halfWidth,float halfLength,float yaw=0)
    {
        float c=MathF.Cos(yaw),s=MathF.Sin(yaw);
        var polygon=new[]{new Vector2(-halfWidth,-halfLength),new(halfWidth,-halfLength),new(halfWidth,halfLength),new(-halfWidth,halfLength)}
            .Select(p=>centre+new Vector2(p.X*c+p.Y*s,-p.X*s+p.Y*c)).ToArray();
        return new(polygon,new(polygon.Min(p=>p.X),0,polygon.Min(p=>p.Y)),new(polygon.Max(p=>p.X),0,polygon.Max(p=>p.Y)));
    }

    public static Vector2 NearestPoint(Vector2 point,IReadOnlyList<Vector2> polygon)
    {
        if(Contains(polygon,point))return point;
        var nearest=polygon[0];float minimum=float.PositiveInfinity;
        for(int i=0;i<polygon.Count;i++)
        {
            var a=polygon[i];var d=polygon[(i+1)%polygon.Count]-a;
            var p=a+d*Math.Clamp(Vector2.Dot(point-a,d)/Math.Max(.000001f,d.LengthSquared()),0,1);
            float distance=Vector2.DistanceSquared(p,point);
            if(distance<minimum){minimum=distance;nearest=p;}
        }
        return nearest;
    }

    public static StormChaseLotShape Corridor(Vector2 from,Vector2 to,float halfWidth)
    {
        var delta=to-from;
        if(delta.LengthSquared()<.01f)return Rectangle(from,halfWidth,halfWidth);
        var side=new Vector2(-delta.Y,delta.X)*(halfWidth/delta.Length());
        var polygon=Hull(new[]{from-side,from+side,to-side,to+side});
        return new(polygon,new(polygon.Min(p=>p.X),0,polygon.Min(p=>p.Y)),new(polygon.Max(p=>p.X),0,polygon.Max(p=>p.Y)));
    }

    public static bool BoundsNear(StormChaseLotShape a,StormChaseLotShape b,float distance)=>
        a.Maximum.X+distance>=b.Minimum.X&&b.Maximum.X+distance>=a.Minimum.X
        &&a.Maximum.Z+distance>=b.Minimum.Z&&b.Maximum.Z+distance>=a.Minimum.Z;

    public static float Distance(IReadOnlyList<Vector2> a,IReadOnlyList<Vector2> b)
    {
        if(Contains(a,b[0])||Contains(b,a[0]))return 0;
        float minimum=float.PositiveInfinity;
        for(int i=0;i<a.Count;i++)for(int j=0;j<b.Count;j++)
        {
            var a0=a[i];var a1=a[(i+1)%a.Count];var b0=b[j];var b1=b[(j+1)%b.Count];
            double x0=Cross(a1-a0,b0-a0),x1=Cross(a1-a0,b1-a0),y0=Cross(b1-b0,a0-b0),y1=Cross(b1-b0,a1-b0);
            if(x0*x1<=0&&y0*y1<=0&&Math.Max(a0.X,a1.X)>=Math.Min(b0.X,b1.X)&&Math.Max(b0.X,b1.X)>=Math.Min(a0.X,a1.X)
                &&Math.Max(a0.Y,a1.Y)>=Math.Min(b0.Y,b1.Y)&&Math.Max(b0.Y,b1.Y)>=Math.Min(a0.Y,a1.Y))return 0;
            minimum=Math.Min(minimum,Math.Min(Math.Min(PointDistance(a0,b0,b1),PointDistance(a1,b0,b1)),
                Math.Min(PointDistance(b0,a0,a1),PointDistance(b1,a0,a1))));
        }
        return minimum;
    }

    public static bool Contains(IReadOnlyList<Vector2> polygon,Vector2 point)
    {
        bool positive=false,negative=false;
        for(int i=0;i<polygon.Count;i++)
        {
            double cross=Cross(polygon[(i+1)%polygon.Count]-polygon[i],point-polygon[i]);
            positive|=cross>.00001;negative|=cross<-.00001;
            if(positive&&negative)return false;
        }
        return true;
    }

    public static Vector2[] Hull(IEnumerable<Vector2> source)
    {
        var points=source.Distinct().OrderBy(p=>p.X).ThenBy(p=>p.Y).ToArray();
        if(points.Length<3)throw new InvalidOperationException("Degenerate lot footprint.");
        var lower=new List<Vector2>();var upper=new List<Vector2>();
        foreach(var p in points)
        {
            while(lower.Count>1&&Cross(lower[^1]-lower[^2],p-lower[^1])<=0)lower.RemoveAt(lower.Count-1);
            lower.Add(p);
        }
        foreach(var p in points.Reverse())
        {
            while(upper.Count>1&&Cross(upper[^1]-upper[^2],p-upper[^1])<=0)upper.RemoveAt(upper.Count-1);
            upper.Add(p);
        }
        return lower.Take(lower.Count-1).Concat(upper.Take(upper.Count-1)).ToArray();
    }

    private static float PointDistance(Vector2 p,Vector2 a,Vector2 b)
    {
        var d=b-a;float length=d.LengthSquared();
        return Vector2.Distance(p,a+d*(length>.000001f?Math.Clamp(Vector2.Dot(p-a,d)/length,0,1):0));
    }
    private static double Cross(Vector2 a,Vector2 b)=>(double)a.X*b.Y-(double)a.Y*b.X;
    internal static Matrix4x4 Transform(RenderItem item)
    {
        var e=item.RotationEulerDegrees??new Vector3f(0,item.RotationYawDegrees,0);
        return Matrix4x4.CreateScale(Native(item.Scale))*Matrix4x4.CreateRotationX(e.X*MathF.PI/180)
            *Matrix4x4.CreateRotationY(e.Y*MathF.PI/180)*Matrix4x4.CreateRotationZ(e.Z*MathF.PI/180)
            *Matrix4x4.CreateTranslation(Native(item.Position));
    }
    private static Vector3 Native(Vector3f p)=>new(p.X,p.Y,p.Z);
}
internal readonly record struct StormChasePartDamage(string BuildingId, float ReleaseWindMph, float MassKilograms);
internal sealed record StormChaseRoadRoute(string Name, IReadOnlyList<Vector3f> Points);

/// <summary>Connected Midwest settlements and actual Blender-authored, independently breakable buildings.</summary>
internal sealed partial class StormChaseWorld
{
    public const float PlayableExtentMeters = 49152;
    public const float PlayableCenterZ = 1000;
    private static readonly (float X,float Z,float Height,float Radius)[] SettlementPads =
        [(6000,7000,18,1800),(-6500,2500,14,1050),(3500,-5500,20,1150),
         (-15500,13500,24,1250),(15100,15500,31,1600),(-15000,-14200,20,1250),(16200,-13800,26,1450),
         (GullHarborX,GullHarborZ,6,420)];
    /// <summary>Small Gulf fishing town just behind the dunes (see StormChaseCoast).</summary>
    public const float GullHarborX = 13800, GullHarborZ = -18340;
    private readonly List<StormChaseSettlement> settlements = [];
    private readonly List<StormChaseGasStation> gasStations = [];
    private readonly List<StormChaseBuilding> buildings = [];
    private readonly Dictionary<string, StormChasePartDamage> partDamage = new(StringComparer.Ordinal);
    private readonly List<Placed> roadSections = [];
    private readonly List<StormChaseRoadRoute> roadRoutes = [];
    private readonly List<RenderItem> serviceCanopies = [];
    private readonly List<RenderWaterBody> riverBodies = [];
    private int buildingNumber;
    public const float BuildingRoadClearanceMeters = 2.5f;
    public const float BuildingLotClearanceMeters = 3;
    private readonly Dictionary<(int X,int Z),List<StormChaseRoadTriangle>> roadLotGrid = [];
    private readonly Dictionary<string,StormChaseLotShape> buildingLots = new(StringComparer.Ordinal);
    private readonly List<StormChaseLotRelocation> lotRelocations = [];
    private bool roadLotsInitialized;

    public StormChaseModelLibrary Models { get; private set; } = null!;
    public IReadOnlyList<StormChaseSettlement> Settlements => settlements;
    public IReadOnlyList<StormChaseGasStation> GasStations => gasStations;
    public IReadOnlyList<StormChaseBuilding> Buildings => buildings;
    public IReadOnlyDictionary<string, StormChasePartDamage> PartDamage => partDamage;
    public int BuildingCount => buildings.Count;
    public int RiverPatchCount => riverBodies.Count;
    public IReadOnlyList<StormChaseRoadRoute> RoadRoutes => roadRoutes;
    internal IReadOnlyList<RenderItem> RoadSurfaceItems => roads;
    internal IReadOnlyList<StormChaseLotRelocation> LotRelocations => lotRelocations;
    internal IReadOnlyList<RenderItem> BuildingGeometryItems(StormChaseBuilding building) =>
        destructible.Where(item=>building.DestructiblePartIds.Contains(item.MotionVectorId!))
            .Concat(staticCollision.Where(item=>item.MotionVectorId?.StartsWith(building.Id+"/",StringComparison.Ordinal)==true)).ToArray();

    public IReadOnlyList<RenderLight> CreateServiceLights(CameraState camera, float hour, Func<string, bool> isDestroyed)
    {
        var lights=new List<RenderLight>();
        if(hour<7||hour>18.5f)lights.AddRange(serviceCanopies.Where(item => !isDestroyed(item.MotionVectorId!) &&
            StormChaseCareer.HorizontalDistance(item.Position, camera.Position) < 250).Select(item =>
            new RenderLight(item.MotionVectorId + "/canopy-lamp", RenderLightType.Point,
                item.Position - new Vector3f(0, .45f, 0), new(0, -1, 0), new(1, .90f, .72f, 1),
                600, 45, CastsShadow: false)));
        if(hour<7||hour>18.5f)AddStreetLampLights(lights,camera.Position);
        foreach(var building in buildings)
        {
            if(camera.Position.X<building.BoundsMinimum.X||camera.Position.X>building.BoundsMaximum.X
                ||camera.Position.Z<building.BoundsMinimum.Z||camera.Position.Z>building.BoundsMaximum.Z
                ||camera.Position.Y<building.Position.Y||camera.Position.Y>building.BoundsMaximum.Y)continue;
            var layout=StormChaseModelLibrary.Layout(building.Kind);if(layout.Width<=0)continue;
            string id=building.Id+"/level0/interior-ceiling-lamp";if(isDestroyed(id))continue;
            lights.Add(new(id,RenderLightType.Point,FromBuildingLocal(building,new(0,layout.Height-.5f,layout.CenterZ)),
                new(0,-1,0),new(1,.9f,.74f,1),90,20,CastsShadow:false));
        }
        return lights;
    }

    /// <summary>The nearest working street lamps around the viewer (bounded: at most 14, within 220 m).</summary>
    private void AddStreetLampLights(List<RenderLight> lights,Vector3f viewer)
    {
        if(streetLamps.Count==0)return;
        const float radius=220;
        var near=new List<(float Distance,Vector3f Lamp)>();
        for(int z=(int)MathF.Floor((viewer.Z-radius)/256);z<=(int)MathF.Floor((viewer.Z+radius)/256);z++)
            for(int x=(int)MathF.Floor((viewer.X-radius)/256);x<=(int)MathF.Floor((viewer.X+radius)/256);x++)
                if(streetLamps.TryGetValue((x,z),out var bucket))foreach(var lamp in bucket)
                {
                    float d=DistanceSquared(viewer.X,viewer.Z,lamp.X,lamp.Z);
                    if(d<radius*radius)near.Add((d,lamp));
                }
        foreach(var (_,lamp) in near.OrderBy(entry=>entry.Distance).Take(14))
            lights.Add(new(FormattableString.Invariant($"street-lamp/{lamp.X:0.0}/{lamp.Z:0.0}"),RenderLightType.Spot,lamp,new(0,-1,0),
                new(1,.82f,.58f,1),3200,34,50,80,CastsShadow:false));
    }

    public bool ContainsPlayablePosition(float x, float z) => MathF.Abs(x) <= PlayableExtentMeters / 2
        && MathF.Abs(z - PlayableCenterZ) <= PlayableExtentMeters / 2;

    public static float RiverZ(float x) => 460 + 160 * MathF.Sin((x - 1700) * .0009f);

    public IReadOnlyList<RenderWaterBody> CreateRiverBodies(RenderWeatherState? weather, CameraState camera, float seconds)
    {
        float wind = Math.Clamp(weather?.WindStrength ?? .25f, 0, 2);
        return riverBodies.Where(body => DistanceSquared(camera.Position.X, camera.Position.Z, body.Position.X, body.Position.Z) < 5000 * 5000)
            .Select(body => body with { WaveStrength = .16f + wind * .18f, FoamAmount = .10f + wind * .08f }).ToArray();
    }

    /// <summary>Gameplay visibility query against complete building footprints, with destroyed buildings optionally excluded.</summary>
    public bool IsLineOfSightClear(Vector3f from, Vector3f to, IReadOnlySet<string>? ignoredBuildingIds = null)
    {
        foreach (var building in buildings)
        {
            if (ignoredBuildingIds?.Contains(building.Id) == true) continue;
            var low = building.BoundsMinimum;
            var high = building.BoundsMaximum;
            float begin = 0, end = 1;
            if (Clip(from.X,to.X,low.X,high.X,ref begin,ref end) && Clip(from.Y,to.Y,low.Y,high.Y,ref begin,ref end)
                && Clip(from.Z,to.Z,low.Z,high.Z,ref begin,ref end) && end > .001f && begin < .999f) return false;
        }
        return true;
    }

    private static bool Clip(float from,float to,float low,float high,ref float begin,ref float end)
    {
        float delta=to-from;
        if(MathF.Abs(delta)<.00001f)return from>=low&&from<=high;
        float a=(low-from)/delta,b=(high-from)/delta;
        if(a>b)(a,b)=(b,a);
        begin=MathF.Max(begin,a);end=MathF.Min(end,b);
        return begin<=end;
    }

    private void BuildSettlements()
    {
        settlements.Add(new("meridian","Meridian City","city",new(6000,HeightAt(6000,7000),7000),1800));
        settlements.Add(new("cedar","Cedar Junction","farm town",new(-6500,HeightAt(-6500,2500),2500),1050));
        settlements.Add(new("redbank","Redbank","rail and industrial town",new(3500,HeightAt(3500,-5500),-5500),1150));

        // Meridian's dense downtown, commercial avenues, civic square,
        // industrial yards and residential blocks are separate visible districts.
        for(int row=0;row<5;row++)for(int col=0;col<5;col++)
            PlaceBuilding("store","meridian",5760+col*92,6740+row*105,col%2*180,1.05f,"Downtown",levels:1+(col+row)%4);
        PlaceBuilding("cityhall","meridian",6000,7380,180,1.8f,"City hall");
        PlaceBuilding("cityhall","meridian",6220,7380,180,1.25f,"County courthouse");
        PlaceBuilding("warehouse","meridian",5750,7380,180,.85f,"Fire and ambulance station");
        for(int i=0;i<18;i++)
            PlaceBuilding("store","meridian",5360+(i%6)*185,6330+(i/6)*100,i%2*180,1.2f,"Commercial avenue");
        for(int row=0;row<3;row++)for(int col=0;col<5;col++)
            PlaceBuilding("warehouse","meridian",6740+col*108,6770+row*170,90,1.15f,"Industrial park");
        for(int row=0;row<7;row++)for(int col=0;col<8;col++)
            PlaceBuilding("house","meridian",5150+col*92,7520+row*85,col%2*180,.92f+(col%3)*.07f,"Residential block");
        // Parking is south-west of the canopy/shop. Root's station+(-14,-20)
        // spawn and the explicit parking point both remain unobstructed.
        Station("meridian","Meridian Fuel & Service",5650,6150,0);

        for(int row=0;row<4;row++)for(int col=0;col<6;col++)
            PlaceBuilding("house","cedar",-6930+col*122,2200+row*118,col%2*180,1,"Cedar homes");
        for(int i=0;i<7;i++)PlaceBuilding("store","cedar",-6860+i*108,2680,180,1,"Cedar main street");
        PlaceBuilding("cityhall","cedar",-6520,2880,180,1.05f,"Town hall and volunteer fire station");
        for(int i=0;i<4;i++)PlaceBuilding("barn","cedar",-7080+i*230,3040,0,1.2f,"Farm cooperative");
        Station("cedar","Cedar Co-op Fuel",-6200,2520,90);

        for(int row=0;row<4;row++)for(int col=0;col<5;col++)
            PlaceBuilding("house","redbank",3080+col*115,-5840+row*105,180,1,"Redbank homes");
        for(int i=0;i<6;i++)PlaceBuilding("store","redbank",3210+i*103,-5260,0,1.05f,"Redbank main street");
        for(int row=0;row<2;row++)for(int col=0;col<5;col++)
            PlaceBuilding("warehouse","redbank",3850+col*130,-5700+row*190,90,1.3f,"Rail freight and light industry");
        PlaceBuilding("cityhall","redbank",3480,-5060,0,1,"Redbank civic hall");
        Station("redbank","Redbank Travel Stop",2920,-5350,0);

        for(int i=0;i<14;i++)
        {
            float x=-5100+i*720,z=i%2==0?4300:-3350;
            PlaceBuilding("barn","rural",x,z,i%3*12,1.1f,"Regional farm");
            PlaceBuilding("house","rural",x-70,z-65,180,1,"Farmhouse");
            PlaceBuilding("silo","rural",x+45,z+20,0,1,"Grain storage");
            Fence(x-140,z-120,x+180,z-120,18,true);
        }
        BuildExtendedSettlements();
        BuildRiver();
        // Appended last so every existing building keeps its id and placement (career saves own houses by id).
        BuildGullHarbor();
    }

    private void Station(string settlement,string name,float x,float z,float yaw)
    {
        string id=PlaceBuilding("gas-station",settlement,x,z,yaw,1,name);
        var position=buildings.Single(building=>building.Id==id).Position;
        var parking=FindStationParking(position,yaw);
        gasStations.Add(new(settlement+"/fuel",name,position,settlement,id,parking));
    }

    private string PlaceBuilding(string modelId,string settlement,float x,float z,float yaw,float scale,string name,int levels=1)
    {
        string id=$"building/{settlement}/{modelId}/{++buildingNumber}";
        var model=Models.GetModel(modelId);
        if(StormChaseCoast.SeawardDistance(x,z)>-StormChaseCoast.BeachInlandMeters)
            throw new InvalidOperationException("Building requested on the beach or in the sea: "+id);
        var requested=new Vector3f(x,HeightAt(x,z),z);
        var lot=StormChaseLotGeometry.FromItems(Models.CreateItems(modelId,new(x,0,z),yaw*MathF.PI/180,id,scale),catalog);
        var placed=FindBuildingLot(lot,new(x,z));
        x=placed.X; z=placed.Y;
        if(Vector2.DistanceSquared(placed,new(requested.X,requested.Z))>.01f)
            lotRelocations.Add(new(id,requested,new(x,HeightAt(x,z),z)));
        float ground=HeightAt(x,z);
        int first=destructible.Count;
        for(int level=0;level<levels;level++)
        {
            var basePosition=new Vector3f(x,ground+level*model.FootprintMeters.Y*scale,z);
            string prefix=id+"/level"+level;
            foreach(var item in Models.CreateItems(modelId,basePosition,yaw*MathF.PI/180,prefix,scale))
            {
                string partName=item.MotionVectorId![(prefix.Length+1)..];
                var definition=model.Parts.Single(part=>part.Name==partName);
                bool foundation=definition.Category.Equals("foundation",StringComparison.OrdinalIgnoreCase)
                    && level==0;
                if(foundation)
                {
                    scenery.Add(new(item.Position.X,item.Position.Z,4200,item with {AllowStaticInstancing=true,WritesMotionVectors=false}));
                    staticCollision.Add(item);
                    continue;
                }
                var piece=item with { AllowStaticInstancing=true,WritesMotionVectors=false };
                destructible.Add(piece);
                if (modelId == "gas-station" && partName.StartsWith("canopy-roof", StringComparison.Ordinal)) serviceCanopies.Add(piece);
                float volume=piece.Scale.X*piece.Scale.Y*piece.Scale.Z;
                float mass=Math.Clamp(definition.Category=="roof"?piece.Scale.X*piece.Scale.Z*4.5f:volume*160,3,1500);
                partDamage.Add(piece.MotionVectorId!,new(id,Math.Max(35,definition.ReleaseWindMph),mass));
            }
        }
        var dimensions=model.FootprintMeters*scale;
        // A yawed AABB conservatively supports gameplay LOS and world probes.
        float angle=yaw*MathF.PI/180;
        var bounds=new Vector3f(MathF.Abs(MathF.Cos(angle))*dimensions.X+MathF.Abs(MathF.Sin(angle))*dimensions.Z,
            dimensions.Y*levels,MathF.Abs(MathF.Sin(angle))*dimensions.X+MathF.Abs(MathF.Cos(angle))*dimensions.Z);
        RecordBuilding(id,settlement,modelId,new(x,ground,z),bounds,first);
        buildings[^1]=buildings[^1] with{YawDegrees=yaw,Scale=scale,DisplayName=name};
        RegisterInterior(buildings[^1]);
        return id;
    }

    private void RecordBuilding(string id,string settlement,string kind,Vector3f position,Vector3f size,int firstPart)
    {
        string[] ids=destructible.Skip(firstPart).Select(item=>item.MotionVectorId!).ToArray();
        if(ids.Length==0)throw new InvalidOperationException("Building has no physically destructible parts: "+id);
        var pieces=destructible.Skip(firstPart).Concat(staticCollision.Where(item=>item.MotionVectorId?.StartsWith(id+"/",StringComparison.Ordinal)==true));
        var shape=StormChaseLotGeometry.FromItems(pieces,catalog);
        if(!LotIsClear(shape,BuildingRoadClearanceMeters,BuildingLotClearanceMeters))
            throw new InvalidOperationException("Building footprint conflicts with a road shoulder or another lot: "+id);
        buildingLots.Add(id,shape);
        buildings.Add(new(id,settlement,kind,position,shape.Maximum-shape.Minimum,ids)
            {BoundsMinimum=shape.Minimum,BoundsMaximum=shape.Maximum});
    }

    private Vector2 FindBuildingLot(StormChaseLotShape original,Vector2 requested)
    {
        if(LotIsClear(original,BuildingRoadClearanceMeters,BuildingLotClearanceMeters))return requested;
        // Stable nearby lot search preserves each district and authored yaw;
        // no road is rerouted through a building to conceal a placement error.
        const float spacing=6;
        for(int ring=1;ring<=40;ring++)
        {
            var offsets=new List<Vector2>();
            for(int x=-ring;x<=ring;x++)for(int z=-ring;z<=ring;z++)
                if(Math.Abs(x)==ring||Math.Abs(z)==ring)offsets.Add(new(x*spacing,z*spacing));
            foreach(var offset in offsets.OrderBy(p=>p.LengthSquared()).ThenBy(p=>p.Y).ThenBy(p=>p.X))
            {
                var candidate=requested+offset;
                if(!ContainsPlayablePosition(candidate.X,candidate.Y)||HeightAt(candidate.X,candidate.Y)<LakeWaterLevel+1
                    ||StormChaseCoast.SeawardDistance(candidate.X,candidate.Y)>-StormChaseCoast.BeachInlandMeters)continue;
                if(LotIsClear(original.Translate(offset),BuildingRoadClearanceMeters,BuildingLotClearanceMeters))return candidate;
            }
        }
        throw new InvalidOperationException("No clear building lot near "+requested);
    }

    private Vector3f FindStationParking(Vector3f position,float yawDegrees)
    {
        float angle=yawDegrees*MathF.PI/180,c=MathF.Cos(angle),s=MathF.Sin(angle);
        // Parking occupies a complete pickup-sized space outside the shop,
        // canopy and road shoulder, and stays within the service interaction range.
        foreach(var local in new[]{new Vector2(-14,-20),new(14,-20),new(0,-25),new(-20,0),new(20,0),new(-14,20),new(14,20)})
        {
            var point=new Vector2(position.X+local.X*c+local.Y*s,position.Z-local.X*s+local.Y*c);
            var footprint=StormChaseLotGeometry.Rectangle(point,1.5f,3);
            if(!LotIsClear(footprint,.5f,1))continue;
            var nearest=NearestRoadPoint(point);
            var access=StormChaseLotGeometry.Corridor(point,nearest,1.75f);
            if(Vector2.Distance(point,nearest)<100&&buildingLots.Values.All(lot=>
                StormChaseLotGeometry.Distance(access.Polygon,lot.Polygon)>=.5f))
                return new(point.X,HeightAt(point.X,point.Y)+.25f,point.Y);
        }
        throw new InvalidOperationException("Service station has no accessible parking: "+position);
    }

    private Vector2 NearestRoadPoint(Vector2 point)
    {
        var search=StormChaseLotGeometry.Rectangle(point,150,150);
        float minimum=float.PositiveInfinity;var nearest=point;
        foreach(var triangle in NearbyRoadTriangles(search,0))
        {
            var candidate=StormChaseLotGeometry.NearestPoint(point,triangle.Polygon);
            float distance=Vector2.DistanceSquared(point,candidate);
            if(distance<minimum){minimum=distance;nearest=candidate;}
        }
        if(!float.IsFinite(minimum))throw new InvalidOperationException("Service station is too far from a road: "+point);
        return nearest;
    }

    private bool LotIsClear(StormChaseLotShape shape,float roadClearance,float buildingClearance)
    {
        EnsureRoadLotGrid();
        foreach(var triangle in NearbyRoadTriangles(shape,roadClearance))
            if(StormChaseLotGeometry.Distance(shape.Polygon,triangle.Polygon)<roadClearance-.002f)return false;
        foreach(var other in buildingLots.Values)
            if(StormChaseLotGeometry.BoundsNear(shape,other,buildingClearance)
                &&StormChaseLotGeometry.Distance(shape.Polygon,other.Polygon)<buildingClearance-.002f)return false;
        return true;
    }

    private void EnsureRoadLotGrid()
    {
        if(roadLotsInitialized)return;
        foreach(var triangle in StormChaseLotGeometry.RoadTriangles(roads,catalog))
        {
            for(int z=LotCell(triangle.Minimum.Y);z<=LotCell(triangle.Maximum.Y);z++)
                for(int x=LotCell(triangle.Minimum.X);x<=LotCell(triangle.Maximum.X);x++)
                {
                    if(!roadLotGrid.TryGetValue((x,z),out var bucket))roadLotGrid.Add((x,z),bucket=[]);
                    bucket.Add(triangle);
                }
        }
        roadLotsInitialized=true;
    }

    private IEnumerable<StormChaseRoadTriangle> NearbyRoadTriangles(StormChaseLotShape shape,float clearance)
    {
        var seen=new HashSet<StormChaseRoadTriangle>();
        for(int z=LotCell(shape.Minimum.Z-clearance);z<=LotCell(shape.Maximum.Z+clearance);z++)
            for(int x=LotCell(shape.Minimum.X-clearance);x<=LotCell(shape.Maximum.X+clearance);x++)
                if(roadLotGrid.TryGetValue((x,z),out var bucket))foreach(var triangle in bucket)
                    if(seen.Add(triangle)&&triangle.Maximum.X>=shape.Minimum.X-clearance&&triangle.Minimum.X<=shape.Maximum.X+clearance
                        &&triangle.Maximum.Y>=shape.Minimum.Z-clearance&&triangle.Minimum.Y<=shape.Maximum.Z+clearance)yield return triangle;
    }
    private static int LotCell(float coordinate)=>(int)MathF.Floor(coordinate/64);

    private void BuildRegionalRoads(ISplineSurface surface)
    {
        void Route(string name,float width,bool gravel,params Vector3f[] points)
        {
            // The map polyline stops at the beach where a route was authored past the shoreline;
            // the ribbon itself is clipped at the same place in AddRibbon.
            roadRoutes.Add(new(name, points.Select(ClipToShore).ToArray()));
            var path=new TerrainPath(name,SplineCurveMode.CatmullRom,defaultWidth:width);
            foreach(var point in points)path.AddPoint(point);
            AddRoad(path,surface,name,width,gravel);
        }
        Route("Meridian highway",13,false,new(-320,0,4000),new(1500,0,4500),new(3500,0,5600),new(5650,0,6150),new(6000,0,7000));
        Route("Cedar highway",11,false,new(-320,0,2500),new(-2500,0,2300),new(-4300,0,2600),new(-6200,0,2520),new(-7200,0,2520));
        Route("Redbank highway",12,false,new(-310,0,-2200),new(900,0,-3150),new(1900,0,-4400),new(2920,0,-5350),new(4400,0,-5350));
        Route("Eastern regional loop",10,false,new(4400,0,-5350),new(6650,0,-2500),new(6950,0,1900),new(6500,0,5000),new(6000,0,7000));
        Route("Western farm road",7,true,new(-6500,0,2500),new(-5100,0,4300),new(-2000,0,4250),new(-320,0,4000));
        Route("Southern farm road",7,true,new(-6500,0,2200),new(-6000,0,-1500),new(-2600,0,-3350),new(900,0,-3150));
        for(int i=0;i<9;i++)Route("Meridian east-west "+i,9,false,new(5020,0,6250+i*235),new(6000,0,6250+i*235),new(7300,0,6250+i*235));
        for(int i=0;i<10;i++)Route("Meridian north-south "+i,9,false,new(5030+i*250,0,6200),new(5030+i*250,0,7050),new(5030+i*250,0,8150));
        foreach(var tuple in new[]{(-6500f,2500f,"Cedar"),(3500f,-5500f,"Redbank")})
        {
            for(int i=-2;i<=2;i++)
            {
                Route(FormattableString.Invariant($"{tuple.Item3} cross street {i}"),8,false,new(tuple.Item1-680,0,tuple.Item2+i*185),new(tuple.Item1+800,0,tuple.Item2+i*185));
                Route(FormattableString.Invariant($"{tuple.Item3} side street {i}"),8,false,new(tuple.Item1+i*210,0,tuple.Item2-620),new(tuple.Item1+i*210,0,tuple.Item2+640));
            }
        }
        Route("Northwestern interstate",13,false,new(-6500,0,2500),new(-10800,0,7200),new(-15500,0,13500),new(-20500,0,17000));
        Route("Northern storm belt",12,false,new(-15500,0,13500),new(-6000,0,15800),new(5000,0,15900),new(15100,0,15500),new(21300,0,17800));
        Route("Northeastern interstate",13,false,new(6000,0,7000),new(11200,0,11600),new(15100,0,15500));
        Route("Southwestern chase route",11,false,new(-6500,0,2200),new(-11200,0,-6400),new(-15000,0,-14200),new(-20900,0,-18800));
        Route("Southern storm belt",12,false,new(-15000,0,-14200),new(-6500,0,-15900),new(4000,0,-15300),new(16200,0,-13800),new(21500,0,-18200));
        Route("Southeastern interstate",13,false,new(3500,0,-5500),new(10200,0,-10100),new(16200,0,-13800));
        foreach(var town in ExtendedTownDefinitions)
            for(int street=-2;street<=2;street++)
            {
                Route(FormattableString.Invariant($"{town.Name} avenue {street}"),8,false,new(town.X-750,0,town.Z+street*200),new(town.X+750,0,town.Z+street*200));
                Route(FormattableString.Invariant($"{town.Name} street {street}"),8,false,new(town.X+street*230,0,town.Z-700),new(town.X+street*230,0,town.Z+700));
            }
        // Gulf coast: Fairview's south street continues to Gull Harbor, whose streets run parallel to the beach.
        // Shore-parallel streets stay >= 600 m inland so neither ribbon nor shoulder reaches the sand.
        Vector3f Coast(float along,float seaward){var (px,pz)=StormChaseCoast.Point(along,seaward);return new(px,0,pz);}
        Route("Gulf coast road",10,false,new(15740,0,-14500),new(15500,0,-16000),new(14600,0,-17500),Coast(-3000,-1180));
        Route("Gull Harbor front street",8,false,Coast(-4300,-650),Coast(-3700,-650),Coast(-3200,-650),Coast(-2700,-650),Coast(-2200,-650));
        Route("Gull Harbor back street",8,false,Coast(-4200,-1180),Coast(-3600,-1180),Coast(-3000,-1180),Coast(-2400,-1180));
        foreach(float along in new[]{-3900f,-3200f,-2500f})
            Route(FormattableString.Invariant($"Gull Harbor cross street {along:0}"),7,false,Coast(along,-1420),Coast(along,-920),Coast(along,-560));
    }

    private static Vector3f ClipToShore(Vector3f point)
    {
        const float keepInland=150;
        float seaward=StormChaseCoast.SeawardDistance(point.X,point.Z);
        if(seaward<=-keepInland)return point;
        var (x,z)=StormChaseCoast.Point(StormChaseCoast.Along(point.X,point.Z),-keepInland);
        return new(x,point.Y,z);
    }

    private void BuildGullHarbor()
    {
        settlements.Add(new("gullharbor","Gull Harbor","Gulf fishing town",new(GullHarborX,HeightAt(GullHarborX,GullHarborZ),GullHarborZ),420));
        // Buildings face the beach: model fronts are authored toward local -Z, the sea lies to the south-east.
        const float shoreYaw=135;
        void At(string model,float along,float seaward,float yaw,float scale,string name,int levels=1)
        {
            var (x,z)=StormChaseCoast.Point(along,seaward);
            PlaceBuilding(model,"gullharbor",x,z,yaw,scale,name,levels);
        }
        for(int i=0;i<6;i++)At("store",-4050+i*260+(i>=3?120:0),-725,shoreYaw+180,1,"Gull Harbor waterfront shop",1+i%2);
        for(int i=0;i<10;i++)At("house",-4100+(i%5)*300+(i/5)*150,i<5?-1085:-1290,shoreYaw+(i/5)*180,.95f,"Gull Harbor beach cottage");
        At("warehouse",-2350,-1290,shoreYaw+90,.9f,"Gull Harbor bait, ice and boat storage");
        {
            // Like the other stations it is requested on its road's control point and moved to the nearest clear lot.
            Station("gullharbor","Gull Harbor Bait & Fuel",14600,-17500,0);
        }
        // Timber fishing pier from the dunes out over the surf.
        var (bx,bz)=StormChaseCoast.Point(-3200,0);
        const float seawardX=.70710678f,seawardZ=-.70710678f,pierYaw=135;
        float deck=StormChaseCoast.SeaLevel+2.6f;
        int first=destructible.Count;
        for(int i=0;i<17;i++)
        {
            float along=-40+i*8;
            Add("wood","box",new(bx+seawardX*along,deck,bz+seawardZ*along),new(5,.24f,8.1f),pierYaw,2500,damage:true);
        }
        for(int i=0;i<9;i++)foreach(float side in new[]{-2.2f,2.2f})
        {
            float along=-24+i*16;
            float px=bx+seawardX*along+seawardZ*side,pz=bz+seawardZ*along-seawardX*side;
            float floor=HeightAt(px,pz);
            Add("wood","cylinder",new(px,(floor+deck)*.5f,pz),new(.36f,deck-floor+.6f,.36f),0,2200,damage:true);
        }
        RecordBuilding("coast/pier","gullharbor","pier",new(bx+seawardX*24,deck,bz+seawardZ*24),new(8,6,140),first);
    }

    private void BuildRiver()
    {
        for(float x=1700;x<6300;x+=200)
        {
            float middle=x+100,centerZ=RiverZ(middle);
            float yaw=-MathF.Atan2(RiverZ(x+200)-RiverZ(x),200)*180/MathF.PI;
            var handle=Mesh("river/"+(int)x);
            const int nx=24,nz=12;
            var vertices=new List<RenderMeshVertex>();var indices=new List<ushort>();
            for(int z=0;z<=nz;z++)for(int u=0;u<=nx;u++)
                vertices.Add(new(new(u/(float)nx-.5f,0,z/(float)nz-.5f),new(1,1,1,1),u/(float)nx,z/(float)nz,new(0,1,0),new(1,0,0)));
            for(int z=0;z<nz;z++)for(int u=0;u<nx;u++)
            {
                ushort a=(ushort)(z*(nx+1)+u),b=(ushort)(a+1),c=(ushort)(a+nx+1),d=(ushort)(c+1);
                indices.AddRange([a,c,b,b,c,d]);
            }
            catalog.UpsertMesh(new(){Handle=handle,Vertices=vertices,Indices=indices});
            riverBodies.Add(new("chase-river-"+(int)x,new(middle,3,centerZ),new(204,1,100),new(1,0,.1f),
                Material:CrestWaterAssetIds.InlandMaterial,WaveStrength:.2f,FoamAmount:.1f,FlowSpeedMetersPerSecond:.7f,
                RotationYawDegrees:yaw,Mesh:handle));
        }
    }
}
