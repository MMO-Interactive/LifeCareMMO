using Engine.GUI;
using Shared.Contracts.World;

/// <summary>A LifeCare lot as the map shows it.</summary>
internal readonly record struct MapLot(string LotId, float MinimumX, float MinimumZ, float MaximumX, float MaximumZ, bool Own, int Pieces);

/// <summary>What the map is asked to show besides the terrain.</summary>
internal sealed record MapMarkers(Vector3f Player, Vector3f Facing, Vector3f? Job, IReadOnlyList<MapLot> Lots);

/// <summary>
/// Life Beyond v2's map, drawn as GPU geometry: the terrain is sampled once into a grid of coloured cells (water by depth,
/// sand, grass shaded by height) and only resampled when the view moves or zooms; chunk lines, roads with their
/// sidewalk edging, buildings as ringed dots, LifeCare lots, the job marker and the player go on top.
/// </summary>
internal sealed class StormChaseMapView(StormChaseWorld world)
{
    private const float ChunkMeters = 64;
    private GuiColor[] cells = [];
    private (float X, float Z, float Meters, int Columns, int Rows) cached = (float.NaN, 0, 0, 0, 0);

    public static readonly GuiColor Road = new(58, 65, 68), Sidewalk = new(157, 157, 145), BuildingFill = new(214, 168, 92), BuildingRing = new(28, 26, 22);
    public static readonly GuiColor PlayerBlue = new(64, 168, 238), JobAmber = new(255, 198, 74), LotTeal = new(84, 226, 198);

    /// <summary>Draws the area of <paramref name="metersAcross"/> metres around (centerX, centerZ) into the pixel rectangle.</summary>
    public void Draw(GuiDx12MeshBuilder mesh, GuiRect pixels, float centerX, float centerZ, float metersAcross, MapMarkers markers,
        bool roads = true, bool buildings = true, bool lots = true, float s = 1, int cellPixels = 4)
    {
        float metersPerPixel = metersAcross / pixels.Width, metersDown = pixels.Height * metersPerPixel;
        int columns = Math.Max(8, (int)(pixels.Width / cellPixels)), rows = Math.Max(8, (int)(pixels.Height / cellPixels));
        float cellMeters = metersAcross / columns;
        // Snap the sampled grid to whole cells so panning does not shimmer; resample only when it moved a cell.
        float snappedX = MathF.Round(centerX / cellMeters) * cellMeters, snappedZ = MathF.Round(centerZ / cellMeters) * cellMeters;
        if (cached.X != snappedX || cached.Z != snappedZ || cached.Meters != metersAcross || cached.Columns != columns || cached.Rows != rows)
        {
            cells = new GuiColor[columns * rows];
            float x0 = snappedX - metersAcross / 2, z1 = snappedZ + metersDown / 2;
            for (int row = 0; row < rows; row++)
                for (int column = 0; column < columns; column++)
                    cells[row * columns + column] = Sample(x0 + (column + .5f) * cellMeters, z1 - (row + .5f) * metersDown / rows);
            cached = (snappedX, snappedZ, metersAcross, columns, rows);
        }
        // One quad per run of same-coloured cells keeps the map well inside the renderer's UI vertex budget.
        float cw = pixels.Width / columns, ch = pixels.Height / rows;
        for (int row = 0; row < rows; row++)
            for (int column = 0; column < columns;)
            {
                var color = cells[row * columns + column]; int end = column + 1;
                while (end < columns && cells[row * columns + end] == color) end++;
                mesh.FillRect(pixels.X + column * cw, pixels.Y + row * ch, (end - column) * cw + .6f, ch + .6f, color);
                column = end;
            }

        float left = centerX - metersAcross / 2, top = centerZ + metersDown / 2;
        (float X, float Y) P(float x, float z) => (pixels.X + (x - left) / metersPerPixel, pixels.Y + (top - z) / metersPerPixel);
        bool Inside(float px, float py, float pad = 0) => px >= pixels.X - pad && px <= pixels.X + pixels.Width + pad && py >= pixels.Y - pad && py <= pixels.Y + pixels.Height + pad;

        // Chunk grid, as Life Beyond draws its district chunks.
        var chunk = new GuiColor(225, 238, 239, 40);
        for (float x = MathF.Ceiling(left / ChunkMeters) * ChunkMeters; x < left + metersAcross; x += ChunkMeters)
        { var p = P(x, 0); if (metersPerPixel < 6) mesh.Line(p.X, pixels.Y, p.X, pixels.Y + pixels.Height, chunk, 1); }
        for (float z = MathF.Floor(top / ChunkMeters) * ChunkMeters; z > top - metersDown; z -= ChunkMeters)
        { var p = P(0, z); if (metersPerPixel < 6) mesh.Line(pixels.X, p.Y, pixels.X + pixels.Width, p.Y, chunk, 1); }

        if (roads)
        {
            float width = Math.Clamp(9 / metersPerPixel, 1.5f, 12) * Math.Max(1, s * .5f);
            foreach (var pass in new[] { (Sidewalk, width + Math.Max(1.5f, width * .45f)), (Road, width) })
                foreach (var route in world.RoadRoutes)
                    for (int i = 1; i < route.Points.Count; i++)
                    {
                        var a = P(route.Points[i - 1].X, route.Points[i - 1].Z); var b = P(route.Points[i].X, route.Points[i].Z);
                        if (!Clip(ref a, ref b, pixels)) continue;
                        mesh.Line(a.X, a.Y, b.X, b.Y, pass.Item1, pass.Item2);
                    }
        }
        if (lots)
            foreach (var lot in markers.Lots)
            {
                var a = P(lot.MinimumX, lot.MaximumZ); var b = P(lot.MaximumX, lot.MinimumZ);
                if (b.X < pixels.X || a.X > pixels.X + pixels.Width || b.Y < pixels.Y || a.Y > pixels.Y + pixels.Height) continue;
                var r = Intersect(new(a.X, a.Y, MathF.Max(3, b.X - a.X), MathF.Max(3, b.Y - a.Y)), pixels);
                var color = lot.Own ? LotTeal : new GuiColor(180, 200, 210);
                mesh.FillRect(r, color.WithAlpha(lot.Pieces > 0 ? (byte)90 : (byte)40), 2 * s);
                mesh.BorderRect(r, color, Math.Max(1, 1.5f * s), 2 * s);
            }
        if (buildings)
        {
            float radius = Math.Clamp(5 / metersPerPixel, 2.5f, 7) * Math.Max(1, s * .6f);
            foreach (var building in world.Buildings)
            {
                var p = P(building.Position.X, building.Position.Z);
                if (!Inside(p.X, p.Y, -radius) || mesh.VertexCount > 200_000) continue;
                mesh.Dot(p.X, p.Y, radius + Math.Max(1, s), BuildingRing);
                mesh.Dot(p.X, p.Y, radius, building.Kind is "station" or "gas" ? new(120, 200, 255) : BuildingFill);
            }
        }
        if (markers.Job is { } job)
        {
            var p = P(job.X, job.Z);
            if (Inside(p.X, p.Y))
            {
                float d = 7 * s;
                mesh.Line(p.X, p.Y - d, p.X + d, p.Y, JobAmber, 2.5f * s); mesh.Line(p.X + d, p.Y, p.X, p.Y + d, JobAmber, 2.5f * s);
                mesh.Line(p.X, p.Y + d, p.X - d, p.Y, JobAmber, 2.5f * s); mesh.Line(p.X - d, p.Y, p.X, p.Y - d, JobAmber, 2.5f * s);
            }
        }
        {
            // The player: Life Beyond's blue disc with a white rim, plus a heading wedge.
            var p = P(markers.Player.X, markers.Player.Z);
            p = (Math.Clamp(p.X, pixels.X + 8 * s, pixels.X + pixels.Width - 8 * s), Math.Clamp(p.Y, pixels.Y + 8 * s, pixels.Y + pixels.Height - 8 * s));
            float length = MathF.Max(.001f, MathF.Sqrt(markers.Facing.X * markers.Facing.X + markers.Facing.Z * markers.Facing.Z));
            float fx = markers.Facing.X / length, fy = -markers.Facing.Z / length, r = 7 * s;
            mesh.Line(p.X + fx * r, p.Y + fy * r, p.X + fx * (r + 8 * s) , p.Y + fy * (r + 8 * s), new(255, 255, 255), 3 * s);
            mesh.Disc(p.X, p.Y, r + 1.6f * s, new(255, 255, 255));
            mesh.Disc(p.X, p.Y, r, PlayerBlue);
            mesh.Disc(p.X, p.Y - r * .3f, r * .26f, new(255, 255, 255));
            mesh.FillRect(new(p.X - r * .3f, p.Y, r * .6f, r * .5f), new(255, 255, 255), r * .2f);
        }
        mesh.BorderRect(pixels, new(101, 157, 178, 180), Math.Max(1.5f, 2 * s * .7f));
    }

    /// <summary>Life Beyond's terrain colours: water by depth, sand, then grass shaded by height and steepness.</summary>
    private GuiColor Sample(float x, float z)
    {
        if (!world.ContainsPlayablePosition(x, z)) return ((int)MathF.Floor(x / 20) + (int)MathF.Floor(z / 20)) % 2 == 0 ? new(10, 25, 30) : new(8, 20, 25);
        if (StormChaseCoast.IsSea(x, z)) return Blend(new(47, 128, 169), new(8, 48, 83), Math.Clamp(StormChaseCoast.SeawardDistance(x, z) / 900, 0, 1));
        if (StormChaseWater.ContainsWater(x, z)) return new(43, 118, 160);
        if (StormChaseCoast.IsBeach(x, z)) return new(198, 179, 119);
        float h = world.HeightAt(x, z), slope = MathF.Abs(world.HeightAt(x + 6, z) - world.HeightAt(x - 6, z)) + MathF.Abs(world.HeightAt(x, z + 6) - world.HeightAt(x, z - 6));
        float inland = StormChaseCoast.InlandDistance(x, z);
        // Grassland greens near the coast drying toward the western plains, as Life Beyond's biome tints.
        var grass = Blend(new(66, 132, 72), new(137, 137, 72), Math.Clamp((inland - 6000) / 30000, 0, .55f));
        float shade = .82f + Math.Clamp(h / 220, 0, 1) * .28f;
        if (slope > 4) shade *= .86f;
        return new((byte)Math.Clamp(grass.R * shade, 0, 255), (byte)Math.Clamp(grass.G * shade, 0, 255), (byte)Math.Clamp(grass.B * shade, 0, 255));
    }

    private static GuiColor Blend(GuiColor a, GuiColor b, float t) =>
        new((byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    private static GuiRect Intersect(GuiRect a, GuiRect b)
    {
        float x0 = MathF.Max(a.X, b.X), y0 = MathF.Max(a.Y, b.Y), x1 = MathF.Min(a.X + a.Width, b.X + b.Width), y1 = MathF.Min(a.Y + a.Height, b.Y + b.Height);
        return new(x0, y0, MathF.Max(0, x1 - x0), MathF.Max(0, y1 - y0));
    }

    /// <summary>Liang–Barsky: clips a segment to the rectangle; false when it lies wholly outside.</summary>
    private static bool Clip(ref (float X, float Y) a, ref (float X, float Y) b, GuiRect r)
    {
        float dx = b.X - a.X, dy = b.Y - a.Y, t0 = 0, t1 = 1;
        foreach (var (p, q) in new[] { (-dx, a.X - r.X), (dx, r.X + r.Width - a.X), (-dy, a.Y - r.Y), (dy, r.Y + r.Height - a.Y) })
        {
            if (p == 0) { if (q < 0) return false; continue; }
            float t = q / p;
            if (p < 0) { if (t > t1) return false; if (t > t0) t0 = t; }
            else { if (t < t0) return false; if (t < t1) t1 = t; }
        }
        var start = (a.X + t0 * dx, a.Y + t0 * dy); b = (a.X + t1 * dx, a.Y + t1 * dy); a = start;
        return true;
    }
}
