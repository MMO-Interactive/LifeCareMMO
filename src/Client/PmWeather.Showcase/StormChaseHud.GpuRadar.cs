using Engine.GUI;
using Shared.Contracts.Rendering;
using Shared.Contracts.World;
using StormChase.Protocol;

internal sealed partial class StormChaseHud
{
    /// <summary>Circular radar scope at (x, y) with diameter <paramref name="size"/> pixels; <paramref name="s"/> scales strokes and type.</summary>
    private void DrawGpuRadar(GuiDx12MeshBuilder mesh, int x, int y, int size, RenderFrame frame,
        StormChaseCampaignTelemetry? career, float s = 1)
    {
        var sweep = GetRadarSweep(frame, career?.Atmosphere, career?.Boundary);
        float range = sweep.RangeMeters, centerX = sweep.CenterX, centerZ = sweep.CenterZ;
        float center = size / 2f, cx = x + center, cy = y + center;
        mesh.Disc(cx, cy, center + 2 * s, new(3, 9, 13, 235));
        for (int row = 0; row < 48; row++)
        for (int column = 0; column < 48; column++)
        {
            float dx = (column / 47f - .5f) * range * 2, dz = (.5f - row / 47f) * range * 2;
            if (dx * dx + dz * dz > range * range) continue;
            float value = sweep.Values[row * 48 + column];
            if (sweep.Product == StormChaseRadarProduct.Reflectivity ? value < 10 : MathF.Abs(value) < 1) continue;
            var color = RadarColor(value, sweep.Product);
            int left = x + column * size / 48, top = y + row * size / 48;
            mesh.FillRect(left, top, x + (column + 1) * size / 48 + 1 - left, y + (row + 1) * size / 48 + 1 - top, color);
        }
        var grid = Theme.Accent.WithAlpha(46);
        for (int ring = 1; ring <= 2; ring++) mesh.Circle(cx, cy, center * ring / 3f, grid, Math.Max(1, s));
        mesh.Line(x + 6 * s, cy, x + size - 6 * s, cy, grid, Math.Max(1, s));
        mesh.Line(cx, y + 6 * s, cx, y + size - 6 * s, grid, Math.Max(1, s));
        mesh.Circle(cx, cy, center, Theme.Ink.WithAlpha(70), 1.5f * s);
        for (int tick = 0; tick < 36; tick++)
        {
            float a = tick * MathF.Tau / 36;
            float inner = center - (tick % 9 == 0 ? 9 : 5) * s;
            mesh.Line(cx + MathF.Sin(a) * inner, cy - MathF.Cos(a) * inner, cx + MathF.Sin(a) * center, cy - MathF.Cos(a) * center,
                Theme.Ink.WithAlpha(tick % 9 == 0 ? (byte)150 : (byte)60), Math.Max(1, s));
        }
        mesh.Text("N", cx - 10 * s, y + 9 * s, 20 * s, 16 * s, 8.5f * s, Theme.Accent, Theme.Bold, GuiTextAlignment.Center);
        bool MapPoint(Vector3f position, out float mx, out float my)
        {
            mx = cx + (position.X - centerX) / range * center;
            my = cy - (position.Z - centerZ) / range * center;
            float nx = position.X - centerX, nz = position.Z - centerZ;
            return nx * nx + nz * nz <= range * range * .98f;
        }
        if (career is not null)
        {
            foreach (var route in career.Roads ?? [])
                for (int i = 1; i < route.Points.Count; i++)
                    if (MapPoint(route.Points[i - 1], out float ax, out float ay) && MapPoint(route.Points[i], out float bx, out float by))
                        mesh.Line(ax, ay, bx, by, new(150, 170, 178, 110), Math.Max(1, s));
            foreach (var settlement in career.Settlements)
                if (MapPoint(settlement.Position, out float townX, out float townY))
                {
                    mesh.Disc(townX, townY, 3 * s, Theme.Muted);
                    float left = Math.Clamp(townX - 50 * s, x + 4 * s, x + size - 104 * s);
                    mesh.Text(settlement.Name, left, townY - 19 * s, 100 * s, 16 * s, 7.5f * s, Theme.Ink, Theme.Font, GuiTextAlignment.Center);
                }
            if (career.Boundary is { } boundary) DrawBoundary(mesh, boundary, cx, cy, center, centerX, centerZ, range, s);
            foreach (var station in career.Stations)
                if (MapPoint(station.Position, out float sx, out float sy)) mesh.Disc(sx, sy, 3.5f * s, Theme.Warn);
            foreach (var probe in career.Probes)
                if (MapPoint(probe.Position, out float px, out float py))
                {
                    mesh.Disc(px, py, 3 * s, probe.Destroyed ? new(120, 120, 120) : new(70, 220, 255));
                    mesh.Circle(px, py, 6 * s, (probe.Destroyed ? new GuiColor(120, 120, 120) : new GuiColor(70, 220, 255)).WithAlpha(120), Math.Max(1, s));
                }
            if (career.Mission is { } mission && MapPoint(new(mission.TargetPosition.X, mission.TargetPosition.Y, mission.TargetPosition.Z), out float jobX, out float jobY))
            {
                mesh.Disc(jobX, jobY, 4 * s, Theme.Warn);
                mesh.Circle(jobX, jobY, 9 * s, Theme.Warn, 1.5f * s);
            }
            if (MapPoint(career.VehiclePosition, out float vx, out float vy)) mesh.FillRect(new(vx - 3 * s, vy - 3 * s, 6 * s, 6 * s), Theme.Ink, 1.5f * s);
        }
        foreach (var signal in sweep.Signals)
        {
            if (!MapPoint(signal.Position, out float sx, out float sy)) continue;
            var color = RotationColor(signal.EstimatedWindMph);
            if (signal.EstimatedWindMph < 15) mesh.Circle(sx, sy, 6 * s, color, 2 * s);
            else
            {
                mesh.Line(sx - 6 * s, sy - 5 * s, sx + 6 * s, sy - 5 * s, color, 2 * s);
                mesh.Line(sx + 6 * s, sy - 5 * s, sx, sy + 6 * s, color, 2 * s);
                mesh.Line(sx, sy + 6 * s, sx - 6 * s, sy - 5 * s, color, 2 * s);
                if (signal.SurfaceConfirmed) mesh.Disc(sx, sy, 2 * s, new(255, 255, 255));
            }
        }
        if (MapPoint(frame.Camera.Position, out float playerX, out float playerY))
        {
            var f = frame.Camera.Forward;
            float length = MathF.Max(.001f, MathF.Sqrt(f.X * f.X + f.Z * f.Z));
            mesh.Line(playerX, playerY, playerX + f.X / length * 16 * s, playerY - f.Z / length * 16 * s, Theme.Accent, 2 * s);
            mesh.Disc(playerX, playerY, 4.5f * s, Theme.Accent);
            mesh.Disc(playerX, playerY, 2 * s, new(8, 20, 24));
        }

        // Legend under the scope.
        float ly = y + size + 12 * s, lw = size;
        if (sweep.Product == StormChaseRadarProduct.Reflectivity)
        {
            float[] stops = [15, 30, 40, 50, 60, 70];
            float sw = lw / stops.Length;
            for (int i = 0; i < stops.Length; i++)
            {
                mesh.FillRect(new(x + i * sw + 1, ly, sw - 2, 6 * s), RadarColor(stops[i], sweep.Product), 2 * s);
                mesh.Text($"{stops[i] - 5:0}", x + i * sw, ly + 8 * s, sw, 14 * s, 7 * s, Theme.Muted, Theme.Font, GuiTextAlignment.Center);
            }
            mesh.Text("dBZ", x + lw - 30 * s, ly - 15 * s, 30 * s, 14 * s, 7 * s, Theme.Faint, Theme.Font, GuiTextAlignment.Right);
        }
        else
        {
            int steps = 24;
            for (int i = 0; i < steps; i++)
            {
                float v = -45 + 90f * (i + .5f) / steps;
                mesh.FillRect(new(x + i * lw / steps, ly, lw / steps + .5f, 6 * s), RadarColor(v, sweep.Product));
            }
            mesh.Text("TOWARD", x, ly + 8 * s, 80 * s, 14 * s, 7 * s, Theme.Muted, Theme.Font);
            mesh.Text("AWAY", x + lw - 80 * s, ly + 8 * s, 80 * s, 14 * s, 7 * s, Theme.Muted, Theme.Font, GuiTextAlignment.Right);
            mesh.Text("m/s", x + lw / 2 - 20 * s, ly + 8 * s, 40 * s, 14 * s, 7 * s, Theme.Faint, Theme.Font, GuiTextAlignment.Center);
        }
    }

    /// <summary>
    /// Dashed boundary lines clipped to the scope: the (curved) main boundary and, on triple-point days, the
    /// warm front. Each is drawn only where it edges the warm sector. Ticks point into the warm/moist side.
    /// </summary>
    private static void DrawBoundary(GuiDx12MeshBuilder mesh, StormChaseBoundary boundary, float cx, float cy, float radius,
        float centerX, float centerZ, float range, float s)
    {
        float limit = range * .97f;
        var color = BoundaryColor.WithAlpha(220);
        (float X, float Y) Screen(float x, float z) => (cx + (x - centerX) / range * radius, cy - (z - centerZ) / range * radius);
        bool Inside(float x, float z) => (x - centerX) * (x - centerX) + (z - centerZ) * (z - centerZ) <= limit * limit;
        bool hasTriple = boundary.TryTriplePoint(out float tripleX, out float tripleZ);
        void Curve(Func<float, (float X, float Z)> point, Func<float, float, float> distance, Func<float, float, bool> keep,
            float alongCenter, string label)
        {
            const int Steps = 120;
            float start = alongCenter - range * 1.4f, step = range * 2.8f / Steps;
            int visible = 0, labelled = -1; float labelScore = float.NegativeInfinity;
            var previous = point(start);
            bool previousOk = Inside(previous.X, previous.Z) && keep(previous.X, previous.Z);
            for (int i = 1; i <= Steps; i++)
            {
                var current = point(start + i * step);
                bool ok = Inside(current.X, current.Z) && keep(current.X, current.Z);
                if (ok && previousOk)
                {
                    var a = Screen(previous.X, previous.Z); var b = Screen(current.X, current.Z);
                    if (visible % 5 < 3) mesh.Line(a.X, a.Y, b.X, b.Y, color, 2 * s);
                    if (visible % 30 == 15)
                    {
                        // Tick toward the warm/moist side, along the local distance gradient.
                        float gx = distance(current.X + 30, current.Z) - distance(current.X - 30, current.Z);
                        float gz = distance(current.X, current.Z + 30) - distance(current.X, current.Z - 30);
                        float length = MathF.Max(1e-4f, MathF.Sqrt(gx * gx + gz * gz));
                        mesh.Line(b.X, b.Y, b.X + gx / length * 6 * s, b.Y - gz / length * 6 * s, color, 1.5f * s);
                    }
                    // Label away from the triple point (whose own label sits there) and away from the scope edge.
                    float fromCenter = MathF.Sqrt((current.X - centerX) * (current.X - centerX) + (current.Z - centerZ) * (current.Z - centerZ));
                    float score = fromCenter > limit * .75f ? float.MinValue
                        : hasTriple ? MathF.Min(MathF.Sqrt((current.X - tripleX) * (current.X - tripleX) + (current.Z - tripleZ) * (current.Z - tripleZ)), range * .5f) - fromCenter * .01f
                        : -MathF.Abs(visible - 24);
                    if (score > labelScore) { labelScore = score; labelled = i; }
                    visible++;
                }
                previous = current; previousOk = ok;
            }
            if (labelled < 0) return;
            var at = point(start + labelled * step); var p = Screen(at.X, at.Z);
            float lx = Math.Clamp(p.X + 6 * s, cx - radius + 6 * s, cx + radius - 96 * s);
            mesh.Text(label, lx, p.Y - 7 * s, 90 * s, 14 * s, 7 * s, BoundaryColor, Theme.Bold);
        }
        Curve(along => boundary.Point(along, 0), boundary.PrimaryDistance, (x, z) => boundary.SecondaryDistance(x, z) >= 0,
            -centerX * boundary.NormalZ + centerZ * boundary.NormalX, boundary.Kind.ToUpperInvariant());
        if (!boundary.HasSecondary) return;
        Curve(boundary.SecondaryPoint, boundary.SecondaryDistance, (x, z) => boundary.PrimaryDistance(x, z) >= 0,
            -centerX * boundary.SecondaryNormalZ + centerZ * boundary.SecondaryNormalX, StormChaseBoundary.SecondaryKind.ToUpperInvariant());
        if (hasTriple && Inside(tripleX, tripleZ))
        {
            var p = Screen(tripleX, tripleZ);
            mesh.Circle(p.X, p.Y, 5 * s, BoundaryColor, 1.5f * s);
            mesh.Disc(p.X, p.Y, 2 * s, BoundaryColor);
            float lx = Math.Clamp(p.X - 35 * s, cx - radius + 6 * s, cx + radius - 76 * s);
            mesh.Text("TRIPLE PT", lx, p.Y - 20 * s, 70 * s, 14 * s, 7 * s, BoundaryColor, Theme.Bold, GuiTextAlignment.Center);
        }
    }

    public void InvalidateRadar() => radarCache.Invalidate();

    private StormChaseRadarSweep GetRadarSweep(RenderFrame frame, AtmosphereSnapshot? atmosphere = null, StormChaseBoundary? boundary = null) => radarCache.Get(radarClock.Elapsed.TotalSeconds,
        RegionalMap ? 0 : frame.Camera.Position.X, RegionalMap ? StormChaseWorld.PlayableCenterZ : frame.Camera.Position.Z,
        RegionalMap ? StormChaseWorld.PlayableExtentMeters * .5f : 3000, RadarProduct, frame.Weather, atmosphere, boundary);

    private static GuiColor RadarColor(float value, StormChaseRadarProduct product)
    {
        if (product == StormChaseRadarProduct.Reflectivity)
            return value < 10 ? new(17, 36, 41) : value < 25 ? new(30, 120, 92) : value < 35 ? new(70, 170, 70)
                : value < 45 ? new(230, 200, 60) : value < 55 ? new(240, 120, 44) : value < 65 ? new(226, 54, 74) : new(226, 150, 246);
        float speed = MathF.Abs(value);
        if (speed < 1) return new(24, 38, 45);
        float amount = Math.Clamp(speed / 45, .15f, 1);
        byte channel = (byte)(80 + amount * 175), low = (byte)(62 - amount * 35);
        return value < 0 ? new(low, channel, (byte)(68 + amount * 85)) : new(channel, low, (byte)(58 + amount * 40));
    }

    private static GuiColor RotationColor(float mph) => mph < 65 ? new(85, 215, 129) : mph < 95 ? new(75, 231, 90)
        : mph < 110 ? new(251, 220, 65) : mph < 130 ? new(253, 155, 51) : mph < 145 ? new(252, 68, 80) : new(255, 117, 222);
}
