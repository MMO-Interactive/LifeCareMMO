using System.Numerics;
using System.Text.Json;
using System.Globalization;
using Engine.Rendering.Assets;
using Shared.Contracts.Rendering;
using Shared.Contracts.World;

/// <summary>CPU audit of complete authored lots and the actual curved road/shoulder meshes.</summary>
internal static class StormChaseLotProbe
{
    public static int Run(string output)
    {
        Directory.CreateDirectory(output);
        var checks=new List<string>();
        var failures=new List<string>();
        void Check(bool condition,string message){if(!condition)failures.Add(message);else checks.Add(message);}
        var catalog=new MutableRenderAssetCatalog(new BuiltInRenderAssetCatalog());
        var world=new StormChaseWorld(catalog);
        var triangles=StormChaseLotGeometry.RoadTriangles(world.RoadSurfaceItems,catalog).ToArray();
        var lots=world.Buildings.Select(building=>(Building:building,Shape:StormChaseLotGeometry.FromItems(world.BuildingGeometryItems(building),catalog))).ToArray();
        Check(lots.Length==world.BuildingCount&&lots.Length>=200,"Every city, town, rural farm and pier has a complete authored footprint.");
        Check(triangles.Any(t=>t.RoadMesh.Contains("-shoulder/",StringComparison.Ordinal))&&triangles.Any(t=>!t.RoadMesh.Contains("-shoulder/",StringComparison.Ordinal)),
            "The audit reads both the actual paved/gravel surfaces and the generated shoulder triangles.");
        var results=new List<object>();
        float minimumRoadClearance=float.PositiveInfinity,minimumBuildingClearance=float.PositiveInfinity;
        foreach(var lot in lots)
        {
            float nearest=float.PositiveInfinity;string road="";
            foreach(var triangle in triangles)
            {
                if(BoundsDistance(lot.Shape,triangle)>=nearest)continue;
                float distance=StormChaseLotGeometry.Distance(lot.Shape.Polygon,triangle.Polygon);
                if(distance<nearest){nearest=distance;road=triangle.RoadMesh;}
            }
            minimumRoadClearance=Math.Min(minimumRoadClearance,nearest);
            Check(nearest>=StormChaseWorld.BuildingRoadClearanceMeters-.01f,"Complete building footprint clears road shoulders: "+lot.Building.Id);
            results.Add(new{lot.Building.Id,lot.Building.Kind,lot.Building.Position,Footprint=lot.Shape.Polygon.Select(p=>new Vector3f(p.X,0,p.Y)),
                RoadClearanceMeters=nearest,NearestRoadMesh=road,BoundsMinimum=lot.Shape.Minimum,BoundsMaximum=lot.Shape.Maximum});
        }
        for(int i=0;i<lots.Length;i++)for(int j=i+1;j<lots.Length;j++)
        {
            if(!StormChaseLotGeometry.BoundsNear(lots[i].Shape,lots[j].Shape,Math.Max(StormChaseWorld.BuildingLotClearanceMeters,minimumBuildingClearance)))continue;
            float distance=StormChaseLotGeometry.Distance(lots[i].Shape.Polygon,lots[j].Shape.Polygon);
            minimumBuildingClearance=Math.Min(minimumBuildingClearance,distance);
            if(distance<StormChaseWorld.BuildingLotClearanceMeters-.01f)failures.Add("Building footprints conflict: "+lots[i].Building.Id+" / "+lots[j].Building.Id);
        }
        Check(minimumBuildingClearance>=StormChaseWorld.BuildingLotClearanceMeters-.01f,"All building footprints maintain the explicit three-metre separation.");
        foreach(var station in world.GasStations)
        {
            var point=new Vector2(station.ParkingPosition.X,station.ParkingPosition.Z);
            var parking=StormChaseLotGeometry.Rectangle(point,1.5f,3);
            Check(Vector2.Distance(new(station.Position.X,station.Position.Z),new(station.ParkingPosition.X,station.ParkingPosition.Z))<70,
                "Parking stays within the service interaction range: "+station.Id);
            Check(lots.All(lot=>StormChaseLotGeometry.Distance(parking.Polygon,lot.Shape.Polygon)>=.99f),
                "The complete parked vehicle footprint clears every building: "+station.Id);
            Check(triangles.All(triangle=>BoundsDistance(parking,triangle)>.5f||StormChaseLotGeometry.Distance(parking.Polygon,triangle.Polygon)>=.49f),
                "Service parking clears the actual road shoulders: "+station.Id);
            Check(station.Position==world.Buildings.Single(b=>b.Id==station.BuildingId).Position,
                "Station metadata follows the final physically placed building: "+station.Id);
            var nearest=triangles.Select(t=>StormChaseLotGeometry.NearestPoint(point,t.Polygon)).MinBy(p=>Vector2.DistanceSquared(point,p));
            var corridor=StormChaseLotGeometry.Corridor(point,nearest,1.75f);
            Check(Vector2.Distance(point,nearest)<100&&lots.All(lot=>StormChaseLotGeometry.Distance(corridor.Polygon,lot.Shape.Polygon)>=.49f),
                "A complete vehicle-width route reaches the nearest road without crossing a building: "+station.Id);
        }

        // Regression geometry: a clear centre cannot hide a porch/roof crossing,
        // touching edges and triangle winding must not evade the footprint test.
        var roadTriangle=new[]{new Vector2(0,0),new(20,0),new(0,20)};
        var crossing=StormChaseLotGeometry.Rectangle(new(13,13),4,4);
        Check(!StormChaseLotGeometry.Contains(roadTriangle,new(13,13))&&StormChaseLotGeometry.Distance(crossing.Polygon,roadTriangle)==0,
            "A building whose centre is outside the road still fails when its footprint crosses the road triangle.");
        var shoulder=StormChaseLotGeometry.Rectangle(new(0,0),7,40);
        var roadside=StormChaseLotGeometry.Rectangle(new(10,0),1,4);
        Check(Math.Abs(StormChaseLotGeometry.Distance(shoulder.Polygon,roadside.Polygon)-2)<.001f,
            "The exact shoulder setback catches a two-metre gap even when pavement and centres are clear.");
        Check(StormChaseLotGeometry.Distance(crossing.Polygon,roadTriangle.Reverse().ToArray())==0,
            "Road triangle winding does not change overlap detection.");
        var originalFarm=lots.Single(lot=>lot.Building.Kind=="barn"&&lot.Building.DestructiblePartIds.Any(id=>id.StartsWith("building/rural/barn/1/",StringComparison.Ordinal)));
        Check(Vector2.Distance(new(originalFarm.Building.Position.X,originalFarm.Building.Position.Z),new(80,1040))<50,
            "The original chase farm stays near its authored location.");
        var previousCulture=CultureInfo.CurrentCulture;
        StormChaseWorld secondWorld;
        try
        {
            CultureInfo.CurrentCulture=CultureInfo.GetCultureInfo("ar-SA");
            secondWorld=new StormChaseWorld(new MutableRenderAssetCatalog(new BuiltInRenderAssetCatalog()));
        }
        finally{CultureInfo.CurrentCulture=previousCulture;}
        Check(world.Buildings.Select(b=>(b.Id,b.Position)).SequenceEqual(secondWorld.Buildings.Select(b=>(b.Id,b.Position)))
            &&world.GasStations.SequenceEqual(secondWorld.GasStations),"Repeated world construction selects identical lots, station positions and parking.");
        Check(world.ContentFingerprint==secondWorld.ContentFingerprint,"World content fingerprints and physical IDs remain identical under a different numeric culture for network admission.");

        string report=Path.Combine(output,"campaign-road-building-lots.json");
        File.WriteAllText(report,JsonSerializer.Serialize(new{Passed=checks.Count,Checks=checks,Failures=failures,BuildingCount=lots.Length,
            world.ContentFingerprint,ActualRoadTriangleCount=triangles.Length,ShoulderSetbackMeters=StormChaseWorld.BuildingRoadClearanceMeters,
            MinimumRoadClearanceMeters=minimumRoadClearance,MinimumBuildingClearanceMeters=minimumBuildingClearance,
            Relocations=world.LotRelocations,Stations=world.GasStations,Buildings=results},new JsonSerializerOptions{WriteIndented=true}));
        if(failures.Count>0)throw new InvalidOperationException(string.Join(Environment.NewLine,failures)+Environment.NewLine+report);
        Console.WriteLine($"Road/building footprint checks passed: {checks.Count}; {lots.Length} buildings, {triangles.Length} actual triangles; {report}");
        return 0;
    }

    private static float BoundsDistance(StormChaseLotShape shape,StormChaseRoadTriangle triangle)
    {
        float x=Math.Max(0,Math.Max(shape.Minimum.X-triangle.Maximum.X,triangle.Minimum.X-shape.Maximum.X));
        float z=Math.Max(0,Math.Max(shape.Minimum.Z-triangle.Maximum.Y,triangle.Minimum.Y-shape.Maximum.Z));
        return MathF.Sqrt(x*x+z*z);
    }
}
