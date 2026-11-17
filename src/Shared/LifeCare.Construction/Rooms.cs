namespace LifeCare.Construction;

/// <summary>A fully enclosed area on one story: floor tiles bounded by walls on every open side.</summary>
public sealed record Room(int Level, IReadOnlyList<TileCell> Tiles)
{
    public float AreaSquareMeters => Tiles.Count * BuildGrid.TileMeters * BuildGrid.TileMeters;
}

/// <summary>Finds rooms the way build mode shows them; clinics will use these to tell an exam room from a hallway.</summary>
public static class RoomFinder
{
    public static IReadOnlyList<Room> Find(BuildingPlan plan, int level)
    {
        var floor = plan.Floors.Where(f => f.Level == level).Select(f => f.Tile).ToHashSet();
        var walls = plan.Walls.Where(w => w.Level == level).ToHashSet();
        var seen = new HashSet<TileCell>(); var rooms = new List<Room>();
        foreach (var start in floor.OrderBy(t => t.Z).ThenBy(t => t.X))
        {
            if (!seen.Add(start)) continue;
            var region = new List<TileCell>(); var pending = new Queue<TileCell>(); pending.Enqueue(start); bool enclosed = true;
            while (pending.TryDequeue(out var tile))
            {
                region.Add(tile);
                foreach (var next in new TileCell[] { new(tile.X - 1, tile.Z), new(tile.X + 1, tile.Z), new(tile.X, tile.Z - 1), new(tile.X, tile.Z + 1) })
                {
                    if (walls.Contains(WallSegment.Between(tile, next, level))) continue;
                    if (!floor.Contains(next)) { enclosed = false; continue; }   // an open edge to no floor: outdoors
                    if (seen.Add(next)) pending.Enqueue(next);
                }
            }
            if (enclosed) rooms.Add(new(level, region.OrderBy(t => t.Z).ThenBy(t => t.X).ToArray()));
        }
        return rooms;
    }
}
