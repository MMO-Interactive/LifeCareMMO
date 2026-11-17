using Shared.Contracts.Rendering;
using Shared.Contracts.World;

internal sealed record StormChaseBuildingInterior(string BuildingId,string Name,string Kind,
    Vector3f ExteriorFeet,Vector3f EntranceFeet,Vector3f InteriorFeet,float ClearDoorWidthMeters,float ClearDoorHeightMeters,
    Vector3f WallOutsideFeet,Vector3f WallInsideFeet);
internal sealed record StormChaseShelter(string Id,string BuildingId,string Name,Vector3f Position,
    Vector3f LocalMinimum,Vector3f LocalMaximum,IReadOnlyList<string> StructuralPartIds);

internal sealed partial class StormChaseWorld
{
    private readonly List<StormChaseBuildingInterior> interiors=[];
    private readonly List<StormChaseShelter> shelters=[];
    public IReadOnlyList<StormChaseBuildingInterior> BuildingInteriors=>interiors;
    public IReadOnlyList<StormChaseShelter> Shelters=>shelters;

    private static Vector3f FromBuildingLocal(StormChaseBuilding building,Vector3f point)
    {
        float angle=building.YawDegrees*MathF.PI/180,c=MathF.Cos(angle),s=MathF.Sin(angle);
        point*=building.Scale;
        return building.Position+new Vector3f(point.X*c+point.Z*s,point.Y,-point.X*s+point.Z*c);
    }
    private static Vector3f ToBuildingLocal(StormChaseBuilding building,Vector3f point)
    {
        float angle=building.YawDegrees*MathF.PI/180,c=MathF.Cos(angle),s=MathF.Sin(angle);
        point=(point-building.Position)*(1/building.Scale);
        return new(point.X*c-point.Z*s,point.Y,point.X*s+point.Z*c);
    }

    private void RegisterInterior(StormChaseBuilding building)
    {
        var layout=StormChaseModelLibrary.Layout(building.Kind);
        if(layout.Width<=0)return;
        float approach=building.Kind=="house"?10.8f:building.Kind=="cityhall"?14.5f:layout.FrontZ+2;
        // Stock stays beside the aisle; a side wall test location is deliberately
        // offset from the real entrance to verify physical wall collision.
        float wallX=building.Kind=="house"?5:building.Kind=="gas-station"?7:building.Kind=="cityhall"?10:building.Kind=="barn"?7:building.Kind=="warehouse"?14:8;
        interiors.Add(new(building.Id,building.DisplayName,building.Kind,
            FromBuildingLocal(building,new(0,0,approach)),FromBuildingLocal(building,new(0,layout.FloorY,layout.FrontZ)),
            FromBuildingLocal(building,new(0,layout.FloorY,layout.FrontZ-2)),layout.Width*building.Scale,layout.Height*building.Scale,
            FromBuildingLocal(building,new(wallX,layout.FloorY,layout.FrontZ+1.2f)),
            FromBuildingLocal(building,new(wallX,layout.FloorY,layout.FrontZ-1.2f))));
        if(building.Kind=="cityhall")
        {
            string[] names=["rear-wall","side-wall","left-wall","front-left","front-right","roof"];
            shelters.Add(new(building.Id+"/shelter",building.Id,building.DisplayName+" storm refuge",
                FromBuildingLocal(building,new(5,layout.FloorY,-4.7f)),new(1.65f,.32f,-7.1f),new(8.35f,3.5f,-2.3f),
                names.Select(name=>building.Id+"/level0/interior-shelter-"+name).ToArray()));
        }
    }

    public StormChaseShelter? FindShelter(Vector3f feet,Func<string,bool>? isPartDestroyed=null)
    {
        foreach(var shelter in shelters)
        {
            if(isPartDestroyed is not null&&shelter.StructuralPartIds.Any(isPartDestroyed))continue;
            var building=buildings.First(b=>b.Id==shelter.BuildingId);
            var local=ToBuildingLocal(building,feet);var low=shelter.LocalMinimum;var high=shelter.LocalMaximum;
            if(local.X>=low.X&&local.X<=high.X&&local.Z>=low.Z&&local.Z<=high.Z&&local.Y>=low.Y-.15f&&local.Y<high.Y)return shelter;
        }
        return null;
    }

    private readonly record struct ExtendedTown(string Id,string Name,string Kind,float X,float Z,bool City);
    private static readonly ExtendedTown[] ExtendedTownDefinitions=
    [
        new("dunfield","Dunfield","wheat and ranch town",-15500,13500,false),
        new("northcrest","Northcrest","county city",15100,15500,true),
        new("willowbend","Willowbend","agricultural town",-15000,-14200,false),
        new("fairview","Fairview","rail and manufacturing town",16200,-13800,false)
    ];
    private void BuildExtendedSettlements()
    {
        foreach(var town in ExtendedTownDefinitions)
        {
            settlements.Add(new(town.Id,town.Name,town.Kind,new(town.X,HeightAt(town.X,town.Z),town.Z),town.City?1600:1250));
            int rows=town.City?8:5,columns=town.City?8:6;
            for(int row=0;row<rows;row++)for(int col=0;col<columns;col++)
                PlaceBuilding("house",town.Id,town.X-680+col*155,town.Z-640+row*145,col%2*180,.92f+(row%3)*.05f,town.Name+" residential home");
            int shops=town.City?16:8;
            for(int i=0;i<shops;i++)PlaceBuilding("store",town.Id,town.X-590+(i%8)*165,town.Z+460+(i/8)*130,180,1.05f,town.Name+" main street shop",1+(town.City?i%3:0));
            for(int row=0;row<2;row++)for(int col=0;col<4;col++)
                PlaceBuilding("warehouse",town.Id,town.X+580+col*105,town.Z-540+row*185,90,1,town.Name+" freight warehouse");
            PlaceBuilding("cityhall",town.Id,town.X-240,town.Z+700,180,1.1f,town.Name+" civic hall and storm shelter");
            for(int farm=0;farm<3;farm++)PlaceBuilding("barn",town.Id,town.X-700+farm*240,town.Z+950,0,1.1f,town.Name+" farm cooperative");
            Station(town.Id,town.Name+" Fuel & Service",town.X+650,town.Z+60,90);
        }
        // Reproducible farms occupy the wider region between the connected
        // highways. Each is a separate physical house, barn and grain silo.
        for(int farm=0;farm<32;farm++)
        {
            float x=-19000+(farm%8)*5200,z=farm<16?10600+(farm/8)*750:-10200-(farm/8-2)*850;
            PlaceBuilding("barn","rural",x,z,(farm%5-2)*7,1.05f,"Regional livestock and grain barn");
            PlaceBuilding("house","rural",x-85,z-85,180,1,"Regional farmhouse");
            PlaceBuilding("silo","rural",x+60,z+35,0,1,"Regional grain storage");
        }
        foreach(var p in new[]{new Vector3f(-10800,0,7200),new(-11200,0,-6400),new(11200,0,11600),new(10200,0,-10100)})
            PlaceBuilding("cityhall","rural",p.X+85,p.Z+90,180,.8f,"Highway storm shelter");
    }
}
