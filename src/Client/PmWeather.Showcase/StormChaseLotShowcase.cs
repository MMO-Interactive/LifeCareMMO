using Engine.Rendering.Assets;
using LifeCare.Construction;
using Shared.Contracts.Rendering;
using Shared.Contracts.World;

/// <summary>
/// A capture-only lot built through the real construction rules (every edit goes through <see cref="BuildingEditor"/>)
/// and drawn through the same <see cref="StormChaseLotRenderer"/> path as online lots: a module-built house with a gable
/// roof, a hip-roofed cottage, and a round clinic room (a true curved ring with a door, windows and a passage) around a
/// 270° nurses' station counter, plus a glazed curved screen and a curved low wall.
/// </summary>
internal static class StormChaseLotShowcase
{
    public const int TileX = -82, TileZ = 160;

    public static (IReadOnlyList<RenderItem> Items, Vector3f RoundRoomCentre, Vector3f HouseCentre, float Base) Create(StormChaseWorld world, MutableRenderAssetCatalog catalog)
    {
        _ = new StormChaseLotRenderer(catalog);
        var deed = new LotDeed("showcase", "builder", TileX, TileZ, TileX + 12, TileZ + 9);
        var plan = BuildingPlan.Empty(deed);
        void Do(BuildEdit edit)
        {
            var result = BuildingEditor.Apply(plan, "builder", edit);
            if (!result.Accepted) throw new InvalidOperationException($"Showcase edit {edit.Kind} was refused: {result.Message}");
            plan = result.Plan;
        }
        List<WallSegment> Box(int x0, int z0, int w, int d)
        {
            var walls = new List<WallSegment>();
            for (int x = x0; x < x0 + w; x++) { walls.Add(new(x, z0, WallAxis.AlongX, 0)); walls.Add(new(x, z0 + d, WallAxis.AlongX, 0)); }
            for (int z = z0; z < z0 + d; z++) { walls.Add(new(x0, z, WallAxis.AlongZ, 0)); walls.Add(new(x0 + w, z, WallAxis.AlongZ, 0)); }
            return walls;
        }

        // A 3x2 painted-siding house: front door between shutter windows, gable roof with matching infill.
        int hx = TileX + 1, hz = TileZ + 2;
        Do(new(BuildEditKind.AddFloor, 0, hx, hz, hx + 2, hz + 1, Finish: "wood"));
        Do(new(BuildEditKind.AddWall, 0, Walls: Box(hx, hz, 3, 2), Style: "lap_white"));
        Do(new(BuildEditKind.SetWallFinish, 0, Walls: [new(hx + 1, hz, WallAxis.AlongX, 0)], Opening: "door_wood"));
        Do(new(BuildEditKind.SetWallFinish, 0, Walls: [new(hx, hz, WallAxis.AlongX, 0), new(hx + 2, hz, WallAxis.AlongX, 0)], Opening: "win_shutter"));
        Do(new(BuildEditKind.SetWallFinish, 0, Walls: [new(hx, hz, WallAxis.AlongZ, 0), new(hx + 3, hz + 1, WallAxis.AlongZ, 0)], Opening: "win_large"));
        Do(new(BuildEditKind.AddRoof, 0, hx, hz, hx + 2, hz + 1, RoofId: "house", Shape: "gable", Finish: "shingle"));

        // A 2x2 red board-and-batten garage with a hip roof.
        int gx = TileX + 1, gz = TileZ + 6;
        Do(new(BuildEditKind.AddFloor, 0, gx, gz, gx + 1, gz + 1, Finish: "concrete"));
        Do(new(BuildEditKind.AddWall, 0, Walls: Box(gx, gz, 2, 2), Style: "batten_red"));
        Do(new(BuildEditKind.SetWallFinish, 0, Walls: [new(gx, gz, WallAxis.AlongX, 0)], Opening: "door_garage"));
        Do(new(BuildEditKind.SetWallFinish, 0, Walls: [new(gx + 1, gz, WallAxis.AlongX, 0)], Opening: "door_halfglass"));
        Do(new(BuildEditKind.AddRoof, 0, gx, gz, gx + 1, gz + 1, RoofId: "garage", Shape: "hip", Finish: "metal_galv"));

        // The round clinic room: a 6 m stucco ring with its door facing -Z, two windows and a staff passage,
        // around a 270° nurses' station counter whose open side faces the door.
        int rx = TileX + 6, rz = TileZ + 1;
        Do(new(BuildEditKind.AddFloor, 0, rx, rz, rx + 3, rz + 3, Finish: "tile"));
        int cx = (rx + 2) * 4000, cz = (rz + 2) * 4000;
        Do(new(BuildEditKind.AddCurvedWall, 0, Curve: new("round-room", 0, cx, cz, 6_000, 180_000, CurvedWall.FullCircleMilliDegrees, "stucco", "full", true,
            [new("door_halfglass", 90_000), new("win_large", 30_000), new("win_large", 150_000), new("win_large", 230_000), new("passage", 320_000)])));
        Do(new(BuildEditKind.AddCurvedWall, 0, Curve: new("nurses-station", 0, cx, cz, 2_500, 315_000, 270_000, "plaster", "counter")));
        // Outside: a glazed half-round screen and a curved stone garden wall.
        Do(new(BuildEditKind.AddCurvedWall, 0, Curve: new("glazed-screen", 0, (TileX + 5) * 4000, (TileZ + 8) * 4000, 4_000, 180_000, 180_000, "plaster", "glazed")));
        Do(new(BuildEditKind.AddCurvedWall, 0, Curve: new("garden-wall", 0, (TileX + 10) * 4000, (TileZ + 8) * 4000, 3_500, 200_000, 140_000, "stone", "half")));

        var document = LotDocument.From(plan, 1);
        float baseY = LotGeometry.BaseElevation(deed, world.HeightAt);
        var pieces = LotGeometry.Build(document, baseY);
        // Creating the items now registers every mesh before the device initialises.
        var items = pieces.Select(p => StormChaseLotRenderer.Item(p)).ToArray();
        int modular = items.Count(i => i.MaterialId == "lot/module");
        Console.WriteLine($"Lot showcase: {pieces.Count} pieces, {modular} drawn from modules or curves, {RoundRoomFinder.Find(plan, 0).Count} round room(s).");
        if (modular != pieces.Count) throw new InvalidOperationException($"{pieces.Count - modular} lot pieces fell back to boxes; is the rural module pack present?");
        return (items, new(cx / 1000f, baseY, cz / 1000f), new((hx + 1.5f) * 4, baseY, (hz + 1) * 4), baseY);
    }
}
