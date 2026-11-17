using Shared.Contracts.Rendering;

internal sealed partial class StormChaseWorld
{
    private readonly ChaseSpatialGrid<VisibleModule> visibleModules=new(512);
    private sealed record VisibleModule(float X,float Z,float Range,RenderItem Item);

    /// <summary>Client presentation hooks (StormChaseWorldBuildingLod.cs); compiled away on the server.</summary>
    partial void RegisterBuildingPart(RenderItem item,float range,ref bool handled);
    partial void AddBuildings(List<RenderItem> items,float x,float z,HashSet<string>? damagedBuildings);

    private void BuildRenderSpatialIndex()
    {
        foreach(var placed in roadSections)visibleModules.Add(placed.X,placed.Z,new(placed.X,placed.Z,6000,placed.Item));
        foreach(var placed in scenery)visibleModules.Add(placed.X,placed.Z,new(placed.X,placed.Z,placed.Range,placed.Item));
        foreach(var placed in streetProps)visibleModules.Add(placed.X,placed.Z,new(placed.X,placed.Z,placed.Range,placed.Item));
        foreach(var item in destructible)
        {
            // Fence posts/rails are sub-pixel beyond ~1.2 km; utility poles stay visible further.
            float range=item.MaterialId=="chase/wire"?700:item.MaterialId=="chase/wood"?(item.Scale.Y>5?3300:1200):4200;
            if(Models.TryGetPart(item,out var part))range=part.Category switch
            {"trim"=>1000,"window"=>1800,"door"=>1800,"pump"=>2200,"interior"=>240,_=>range};
            bool handled=false;
            RegisterBuildingPart(item,range,ref handled);   // client: building LOD owns its parts
            if(!handled)visibleModules.Add(item.Position.X,item.Position.Z,new(item.Position.X,item.Position.Z,range,item));
        }
    }

    // World construction registers immutable source modules once. A frame
    // touches nearby bins, while PhysX substitutes the current debris poses.
    private sealed class ChaseSpatialGrid<T>(float cellSize)
    {
        private readonly Dictionary<(int X,int Z),List<T>> cells=[];
        private int Cell(float coordinate)=>(int)MathF.Floor(coordinate/cellSize);
        public void Add(float x,float z,T value)
        {
            var cell=(Cell(x),Cell(z));
            if(!cells.TryGetValue(cell,out var bucket))cells.Add(cell,bucket=[]);
            bucket.Add(value);
        }
        public IEnumerable<T> Near(float x,float z,float radius)
        {
            for(int iz=Cell(z-radius);iz<=Cell(z+radius);iz++)for(int ix=Cell(x-radius);ix<=Cell(x+radius);ix++)
                if(cells.TryGetValue((ix,iz),out var bucket))foreach(var value in bucket)yield return value;
        }
    }
}
