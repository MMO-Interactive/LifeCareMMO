using System.Numerics;
using Engine.GUI;
using LifeCare.Construction;
using Shared.Contracts.Rendering;
using Shared.Contracts.World;

/// <summary>
/// The build camera, as in Tiny Clinic and Life Beyond: it leaves the avatar where it stands and flies freely over the lot.
/// WASD moves over the ground, Space/Ctrl rise and sink, Q/E turn, Shift hurries, the wheel dollies in and out and the
/// right mouse button (dragged) looks around. The cursor stays free for aiming.
/// </summary>
internal sealed class StormChaseBuildCamera
{
    public const float FieldOfViewDegrees = 58;
    public Vector3 Position;
    /// <summary>Yaw 0 looks along +Z; pitch below zero looks down.</summary>
    public float Yaw, Pitch = -.75f;

    public Vector3 Forward => new(MathF.Sin(Yaw) * MathF.Cos(Pitch), MathF.Sin(Pitch), MathF.Cos(Yaw) * MathF.Cos(Pitch));
    private Vector3 Right => Vector3.Normalize(Vector3.Cross(Vector3.UnitY, Forward));

    /// <summary>Looks down at <paramref name="target"/> from the direction the player was facing.</summary>
    public void Frame(Vector3 target, float distance, Vector3 facing)
    {
        Yaw = MathF.Atan2(facing.X, facing.Z); Pitch = -.78f;
        Position = target - Forward * distance;
    }

    public void Update(float dt, Func<int, bool> held, float wheel, Vector2 look, float ground)
    {
        Yaw += look.X * .0045f;
        Pitch = Math.Clamp(Pitch - look.Y * .0045f, -1.5f, .35f);
        Yaw += ((held(0x45) ? 1 : 0) - (held(0x51) ? 1 : 0)) * 1.7f * dt;
        var flat = Vector3.Normalize(new(Forward.X, 0, Forward.Z));
        var right = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, flat));
        float height = MathF.Max(1.5f, Position.Y - ground);
        float speed = Math.Clamp(height * 1.1f, 5, 80) * (held(0x10) ? 3 : 1);
        var move = flat * ((held(0x57) ? 1 : 0) - (held(0x53) ? 1 : 0)) + right * ((held(0x44) ? 1 : 0) - (held(0x41) ? 1 : 0))
            + Vector3.UnitY * ((held(0x20) ? 1 : 0) - (held(0x11) ? 1 : 0));
        if (move.LengthSquared() > 0) Position += Vector3.Normalize(move) * speed * MathF.Min(dt, .1f);
        if (wheel != 0) Position += Forward * wheel * MathF.Max(1.2f, height * .22f);
        Position.Y = MathF.Max(Position.Y, ground + .6f);
    }

    public CameraState State => new(new(Position.X, Position.Y, Position.Z), new(Forward.X, Forward.Y, Forward.Z), FieldOfViewDegrees, .2f, 16000);

    /// <summary>The world ray under a screen pixel.</summary>
    public (Vector3 Origin, Vector3 Direction) Ray(float x, float y, int width, int height)
    {
        float tan = MathF.Tan(FieldOfViewDegrees * MathF.PI / 360), aspect = width / (float)Math.Max(1, height);
        float nx = (2 * x / width - 1) * tan * aspect, ny = (1 - 2 * y / height) * tan;
        var f = Forward; var r = Right; var u = Vector3.Cross(f, r);
        return (Position, Vector3.Normalize(f + r * nx + u * ny));
    }
}

/// <summary>Sims-style build mode for the owner's lot: the build camera, mouse aiming and the build catalog.</summary>
internal sealed partial class StormChaseLotPlay
{
    private readonly StormChaseBuildCamera buildCamera = new();
    private CameraState? lastCamera;
    private float buildWheel;
    private Vector2? lastRight;
    private float rightTravel;
    private bool worldGesture;
    private string catalogCategory = "structure";
    private int catalogPage;
    private (float X, float Z)? hoverPoint;
    private readonly List<GuiRect> buildUi = [];

    public CameraState BuildCameraState => buildCamera.State;
    public void AddBuildWheel(float notches) => buildWheel += notches;

    private void EnterBuildCamera()
    {
        if (build is null) return;
        var d = build.Session.Lot.Lot;
        float baseY = geometry.TryGetValue(d.LotId, out var g) ? g.Base : lastFeet.Y;
        float x0 = d.MinimumTileX * BuildGrid.TileMeters, x1 = (d.MaximumTileX + 1) * BuildGrid.TileMeters;
        float z0 = d.MinimumTileZ * BuildGrid.TileMeters, z1 = (d.MaximumTileZ + 1) * BuildGrid.TileMeters;
        var facing = lastCamera is { } c ? new Vector3(c.Forward.X, 0, c.Forward.Z) : Vector3.UnitZ;
        if (facing.LengthSquared() < 1e-4f) facing = Vector3.UnitZ;
        buildCamera.Frame(new((x0 + x1) / 2, baseY, (z0 + z1) / 2), MathF.Max(x1 - x0, z1 - z0) * .9f + 12, Vector3.Normalize(facing));
        catalogPage = 0;
    }

    /// <summary>Build-mode keys. Every key is consumed so gameplay shortcuts (map, radar, mute) stay quiet while building.</summary>
    public bool HandleBuildKey(int key)
    {
        if (!BuildMode || build is null) return false;
        switch (key)
        {
            case 0x31: Pick(LotBuildTool.Room); break;
            case 0x32: Pick(LotBuildTool.Wall); break;
            case 0x33: Pick(LotBuildTool.CurvedWall); break;
            case 0x34: Pick(LotBuildTool.Fitting); break;
            case 0x35: Pick(LotBuildTool.Finish); break;
            case 0x36: Pick(LotBuildTool.PaintWall); break;
            case 0x37: Pick(LotBuildTool.Floor); break;
            case 0x38: Pick(LotBuildTool.Roof); break;
            case 0x39 or 0x2E: Pick(LotBuildTool.Delete); break;
            case 0x21: build.ChangeLevel(1); break;
            case 0x22: build.ChangeLevel(-1); break;
            case 0x48: build.FinishCurve(false); break;
            case 0x46: build.FinishCurve(true); break;
            case 0x24: EnterBuildCamera(); break;
        }
        return true;

        void Pick(LotBuildTool tool)
        {
            build.SelectTool(tool);
            catalogCategory = tool switch
            {
                LotBuildTool.CurvedWall => "curved", LotBuildTool.Fitting => "doors", LotBuildTool.Finish => "styles", LotBuildTool.PaintWall => "paint",
                LotBuildTool.Floor => "floors", LotBuildTool.Roof => "roofs", LotBuildTool.Delete => "edit", _ => "structure",
            };
            catalogPage = 0;
        }
    }

    /// <summary>Called each frame in build mode after the HUD has seen the pointer: camera, aiming and world clicks.</summary>
    public void UpdateBuild(GuiInputState input, string? hoveredElement, NativeSurface surface, float dt)
    {
        if (!BuildMode || build is null) { buildWheel = 0; lastRight = null; return; }
        var pointer = input.Pointer;
        var (rightDown, rightPressed, rightReleased) = surface.TakeSecondaryButton();
        var look = Vector2.Zero;
        if (rightPressed) rightTravel = 0;
        if (rightDown && pointer.IsPresent)
        {
            var at = new Vector2(pointer.X, pointer.Y);
            if (lastRight is { } previous) { look = at - previous; rightTravel += look.Length(); }
            lastRight = at;
        }
        else lastRight = null;
        if (rightReleased && rightTravel < 6) build.CancelGesture();   // a right click cancels; a right drag looks

        float baseY = geometry.TryGetValue(build.Session.Lot.Lot.LotId, out var g) ? g.Base : lastFeet.Y;
        buildCamera.Update(dt, surface.IsKeyHeld, buildWheel, look, baseY); buildWheel = 0;
        build.PaintWholeRoom = surface.IsKeyHeld(0x10);

        bool overUi = hoveredElement is not null || pointer.IsPresent && buildUi.Any(r => pointer.X >= r.X && pointer.X <= r.Right && pointer.Y >= r.Y && pointer.Y <= r.Bottom);
        if (pointer.IsPresent && !overUi)
        {
            var (origin, direction) = buildCamera.Ray(pointer.X, pointer.Y, surface.Width, surface.Height);
            float levelBase = LotGeometry.LevelElevation(baseY, build.Level), plane = levelBase + LotGeometry.FloorThickness;
            if (direction.Y < -1e-4f)
            {
                float t = (plane - origin.Y) / direction.Y;
                if (t is > 0 and < 2000)
                {
                    var hit = origin + direction * t;
                    build.SetRay(origin, direction, hit.X, hit.Z, levelBase);
                    hoverPoint = (hit.X, hit.Z);
                }
            }
        }
        if (pointer.IsPrimaryButtonPressed && !overUi) { build.BeginGesture(); worldGesture = true; }
        if (pointer.IsPrimaryButtonReleased && (worldGesture || build.CurveExtending)) { build.CompleteGesture(); worldGesture = false; }
    }

    private bool HandleBuildAction(string action)
    {
        if (!action.StartsWith("lot/build/", StringComparison.Ordinal) || build is null) return false;
        var parts = action.Split('/');
        switch (parts.ElementAtOrDefault(2))
        {
            case "cat": catalogCategory = parts[3]; catalogPage = 0; break;
            case "page": catalogPage = Math.Max(0, catalogPage + int.Parse(parts[3])); break;
            case "item" when int.TryParse(parts[3], out int index) && Catalog(catalogCategory).ElementAtOrDefault(index) is { } item:
                build.Choose(item.Tool, item.Apply); selectedItem = item; break;
            case "level": build.ChangeLevel(int.Parse(parts[3])); break;
            case "camera": EnterBuildCamera(); break;
            case "exit": HandleKey(0x59); break;
        }
        return true;
    }

    private CatalogItem? selectedItem;

    private sealed record CatalogItem(string Name, string Detail, LotBuildTool Tool, Func<LotBuildSelection, LotBuildSelection> Apply,
        Func<LotBuildController, bool> Selected, string? Swatch = null);

    private static readonly (string Id, string Label)[] Categories =
    [
        ("structure", "STRUCTURE"), ("curved", "CURVED WALLS"), ("doors", "DOORS & WINDOWS"), ("styles", "WALL STYLES"), ("paint", "WALL PAINT"),
        ("floors", "FLOORS & CARPET"), ("roofs", "ROOFS"), ("edit", "EDIT & REMOVE"),
    ];

    private static IReadOnlyList<CatalogItem> Catalog(string category)
    {
        static Func<LotBuildController, bool> Is(LotBuildTool tool, Func<LotBuildSelection, bool>? also = null) => b => b.Tool == tool && (also?.Invoke(b.Selection) ?? true);
        switch (category)
        {
            case "structure":
                return
                [
                    new("Room", "Drag a rectangle: floor and four walls in the selected style.", LotBuildTool.Room, s => s, Is(LotBuildTool.Room)),
                    new("Wall", "Drag along tile edges to build full-height walls.", LotBuildTool.Wall, s => s with { WallKind = ModuleCatalog.NoOpening },
                        Is(LotBuildTool.Wall, s => s.WallKind == ModuleCatalog.NoOpening)),
                    new("Half wall", "Waist-high walls that divide space without enclosing a room.", LotBuildTool.Wall, s => s with { WallKind = "half" },
                        Is(LotBuildTool.Wall, s => s.WallKind == "half")),
                    new("Railing", "Protective railings for porches, landings and open edges.", LotBuildTool.Wall, s => s with { WallKind = "railing" },
                        Is(LotBuildTool.Wall, s => s.WallKind == "railing")),
                    new("Floor", "Drag a rectangle to lay floor.", LotBuildTool.Floor, s => s, Is(LotBuildTool.Floor)),
                    new("Roof", "Drag over a building; gable and hip roofs span 2 tiles.", LotBuildTool.Roof, s => s, Is(LotBuildTool.Roof)),
                ];
            case "curved":
                return CurvedWalls.Profiles.Select(p => new CatalogItem(LotBuildController.ProfileName(p), p switch
                {
                    "glazed" => "A curved wall of continuous glass.",
                    "half" => "A curved waist-high wall.",
                    "counter" => "A curved counter with a worktop inside: a nurses' station or reception.",
                    _ => "A full-height curved wall: round rooms and curved fronts.",
                } + " Press on the starting corner, drag to the half point, then carry it round.", LotBuildTool.CurvedWall,
                    s => s with { CurvedProfile = p }, Is(LotBuildTool.CurvedWall, s => s.CurvedProfile == p))).ToList();
            case "doors":
                return ModuleCatalog.Fittings.Select(o => new CatalogItem(o.Name, o.Glazed && !o.Passable ? $"{o.WidthMeters:0.#} × {o.HeightMeters:0.#} m window. Click a wall to fit it."
                    : $"{o.WidthMeters:0.#} m door. Click a wall to fit it; click an opening on a curved wall to remove it.", LotBuildTool.Fitting,
                    s => s with { Fitting = o.Id }, Is(LotBuildTool.Fitting, s => s.Fitting == o.Id)))
                    .Append(new("Open passage", "A 1.2 m opening in a curved wall or counter.", LotBuildTool.Fitting, s => s with { Fitting = "passage" },
                        Is(LotBuildTool.Fitting, s => s.Fitting == "passage")))
                    .Append(new("Remove fitting", "Click a wall to make it solid again.", LotBuildTool.Fitting, s => s with { Fitting = ModuleCatalog.NoOpening },
                        Is(LotBuildTool.Fitting, s => s.Fitting == ModuleCatalog.NoOpening))).ToList();
            case "styles":
                return ModuleCatalog.WallStyles.Select(w => new CatalogItem(w.Name, $"Click or drag along walls to change their finish. Fire rating {w.FireRatingMinutes} min. New walls use it too.",
                    LotBuildTool.Finish, s => s with { WallStyle = w.Id }, Is(LotBuildTool.Finish, s => s.WallStyle == w.Id), Swatch: SidingSwatch(w))).ToList();
            case "paint":
                return ModuleCatalog.WallPaints.Select(p => new CatalogItem(p.Name, "Click a wall face to paint it; hold Shift to paint the whole room.",
                    LotBuildTool.PaintWall, s => s with { WallPaint = p.Id }, Is(LotBuildTool.PaintWall, s => s.WallPaint == p.Id), p.Hex))
                    .Append(new("Remove paint", "Click a face to return it to its finish.", LotBuildTool.PaintWall, s => s with { WallPaint = BuildingEditor.NoPaint },
                        Is(LotBuildTool.PaintWall, s => s.WallPaint == BuildingEditor.NoPaint))).ToList();
            case "floors":
                var floors = new List<CatalogItem>();
                void Floor(string name, string finish, string? paint, string detail, string swatch) => floors.Add(new(name, detail, LotBuildTool.Floor,
                    s => s with { FloorFinish = finish, FloorPaint = paint }, Is(LotBuildTool.Floor, s => s.FloorFinish == finish && s.FloorPaint == paint), swatch));
                Floor("Wood floor", "wood", null, "Plank floor.", "#A0703F");
                foreach (var p in ModuleCatalog.Paints.Where(p => p.Family == "wood")) Floor(p.Name, "wood", p.Id, "Plank floor.", p.Hex);
                Floor("White tile", "tile", null, "Wipe-clean tile for clinical rooms.", "#DCD6C8");
                foreach (var p in ModuleCatalog.Paints.Where(p => p.Family == "tile")) Floor(p.Name, "tile", p.Id, "Wipe-clean tile.", p.Hex);
                foreach (var p in ModuleCatalog.Paints.Where(p => p.Family == "carpet")) Floor(p.Name, "carpet", p.Id, "Soft carpet for homes, offices and play rooms.", p.Hex);
                foreach (var p in ModuleCatalog.Paints.Where(p => p.Family == "vinyl")) Floor(p.Name, "vinyl", p.Id, "Seamless vinyl for exam and treatment rooms.", p.Hex);
                Floor("Concrete slab", "concrete", null, "Garages, workshops and service rooms.", "#9E9C96");
                Floor("Raised wood floor", "raised_wood", null, "Wood floor on piers, for a lot lifted off the ground.", "#8B6A45");
                return floors;
            case "roofs":
                var roofs = new List<CatalogItem> { new("Flat roof", "Covers any rectangle.", LotBuildTool.Roof, s => s with { RoofShape = "flat" }, Is(LotBuildTool.Roof, s => s.RoofShape == "flat"), "#6F7275") };
                foreach (var shape in new[] { "gable", "hip" })
                    foreach (var (id, name) in ModuleCatalog.RoofFinishes)
                        roofs.Add(new($"{char.ToUpperInvariant(shape[0])}{shape[1..]} roof, {name.ToLowerInvariant()}", $"Spans 2 tiles (8 m) across; the ridge runs along the longer side.",
                            LotBuildTool.Roof, s => s with { RoofShape = shape, RoofFinish = id }, Is(LotBuildTool.Roof, s => s.RoofShape == shape && s.RoofFinish == id),
                            id switch { "metal_green" => "#3F6047", "metal_galv" => "#A3A7AA", _ => "#46484B" }));
                return roofs;
            default:
                return [new("Delete", "Click a wall, curved wall, roof or floor tile; drag to clear floor.", LotBuildTool.Delete, s => s, Is(LotBuildTool.Delete))];
        }
    }

    private static string SidingSwatch(WallStyleSpec style) => style.Siding switch
    {
        "siding_white" => "#E4E2D6", "siding_natural" => "#9C7550", "stucco" => "#CDBB9B", "batten_green" => "#4E6B4A", "batten_red" => "#8E2A22", "stone" => "#8A857C", _ => "#D9D3C7",
    };

    private static GuiColor Hex(string hex)
    {
        string h = hex.TrimStart('#');
        return new((byte)Convert.ToInt32(h[..2], 16), (byte)Convert.ToInt32(h[2..4], 16), (byte)Convert.ToInt32(h[4..6], 16), 255);
    }

    /// <summary>The build-mode screen: lot information (left), controls (right), the catalog (bottom) and the status line (top).</summary>
    private IEnumerable<GuiElement> BuildOverlay(int w, int h)
    {
        var items = new List<GuiElement>();
        buildUi.Clear();
        if (build is null) return items;
        var t = (Glass: StormChaseHud.Theme.Glass, Edge: StormChaseHud.Theme.GlassEdge, Tile: StormChaseHud.Theme.Tile, TileEdge: StormChaseHud.Theme.TileEdge,
            Accent: StormChaseHud.Theme.Accent, Ink: StormChaseHud.Theme.Ink, Muted: StormChaseHud.Theme.Muted, Faint: StormChaseHud.Theme.Faint);
        void Panel(GuiRect r, string title, string? subtitle = null)
        {
            buildUi.Add(r);
            items.Add(new GuiOverlayPanel { Translation = new(r.X, r.Y), Width = GuiLength.Pixels(r.Width), Height = GuiLength.Pixels(r.Height),
                BackgroundColor = t.Glass, BorderColor = t.Edge, BorderThickness = 1, CornerRadius = 10 });
            if (title.Length > 0) items.Add(BuildText(title, r.X + 14, r.Y + 10, r.Width - 28, 11.5f, t.Ink, true));
            if (subtitle is not null) items.Add(BuildText(subtitle, r.X + 14, r.Y + 30, r.Width - 28, 8, t.Muted, false));
        }
        void Row(GuiRect r, ref float y, string label, string value)
        {
            items.Add(BuildText(label, r.X + 14, y, 140, 8.5f, t.Muted, false));
            items.Add(BuildText(value, r.X + 150, y, r.Width - 164, 8.5f, t.Ink, true, GuiTextAlignment.Right));
            y += 20;
        }
        GuiButton Button(string id, string text, GuiRect r, bool selected = false, float size = 8.5f) => new()
        {
            Id = id, Text = text, Translation = new(r.X, r.Y), Width = GuiLength.Pixels(r.Width), Height = GuiLength.Pixels(r.Height),
            TextStyle = new(StormChaseHud.Theme.Bold, size, 1.1f, t.Ink), TextAlignment = GuiTextAlignment.Center, Padding = new(6, (r.Height - size * 1.3f) / 2),
            CornerRadius = 7, BackgroundColor = selected ? new(26, 92, 140, 250) : t.Tile, HoverBackgroundColor = new(34, 104, 156, 250),
            PressedBackgroundColor = new(10, 40, 70, 255), BorderColor = selected ? t.Accent : t.TileEdge, BorderThickness = selected ? 2 : 1,
        };

        // Status line (top centre).
        var status = new GuiRect(w / 2f - 330, 14, 660, 58);
        Panel(status, "");
        items.Add(BuildText($"BUILD · {LotBuildController.ToolName(build.Tool).ToUpperInvariant()} · {(build.Level == 0 ? "GROUND" : "FLOOR " + (build.Level + 1))}",
            status.X + 14, status.Y + 6, 300, 8, t.Accent, true));
        items.Add(BuildText(build.Preview.Status, status.X + 14, status.Y + 20, status.Width - 28, 9, build.Preview.IsValid || build.Preview.Edit is null ? t.Ink : StormChaseHud.Theme.Warn, false));
        if (build.Status is { Length: > 0 } message) items.Add(BuildText(message, status.X + 14, status.Y + 38, status.Width - 28, 8, t.Faint, false));

        // Lot information (left).
        var lot = build.Session.Lot; var plan = lot.ToPlan();
        var info = new GuiRect(16, 14, 300, 300);
        Panel(info, "LOT INFORMATION", "Current authoritative build context");
        float y = info.Y + 56;
        Row(info, ref y, "Lot", lot.Lot.LotId);
        Row(info, ref y, "Size", $"{lot.Lot.MaximumTileX - lot.Lot.MinimumTileX + 1} × {lot.Lot.MaximumTileZ - lot.Lot.MinimumTileZ + 1} tiles");
        Row(info, ref y, "Revision", lot.Revision.ToString());
        if (hoverPoint is { } hp) { var tile = LotBuildController.TileAt(hp.X, hp.Z); Row(info, ref y, "Pointer tile", $"{tile.X}, {tile.Z}  ·  Level {build.Level}"); }
        Row(info, ref y, "Floor tiles", lot.Floors.Count(f => f.Level == build.Level).ToString());
        Row(info, ref y, "Walls", $"{lot.Walls.Count(wl => wl.Level == build.Level)} straight · {lot.CurvedWalls.Count(c => c.Level == build.Level)} curved");
        Row(info, ref y, "Rooms", $"{RoomFinder.Find(plan, build.Level).Count} · {RoundRoomFinder.Find(plan, build.Level).Count} round");
        items.Add(BuildText("BUILDING FLOORS", info.X + 14, y + 6, 200, 8, t.Accent, true));
        items.Add(Button("lot/build/level/-1", "<", new(info.X + 14, y + 26, 36, 30)));
        items.Add(Button("lot/build/level/0", build.Level == 0 ? "GROUND FLOOR" : $"FLOOR {build.Level + 1}", new(info.X + 56, y + 26, info.Width - 112, 30)));
        items.Add(Button("lot/build/level/1", ">", new(info.Right - 50, y + 26, 36, 30)));

        // Controls (right).
        var controls = new GuiRect(w - 316, 14, 300, 330);
        Panel(controls, "BUILD MODE", "Free build camera; the avatar stays where it is");
        y = controls.Y + 56;
        foreach (var (key, what) in new[]
        {
            ("WASD", "Fly over the lot"), ("Space / Ctrl", "Rise / sink"), ("Q / E", "Turn"), ("Right drag", "Look around"), ("Wheel", "Zoom"),
            ("LMB", "Place, or drag to build"), ("RMB", "Cancel the gesture"), ("Shift", "Paint the whole room"), ("H / F", "Finish a curve half / full"),
            ("1–9", "Tools  ·  PgUp/PgDn floors"),
        })
        {
            items.Add(new GuiOverlayPanel { Translation = new(controls.X + 14, y - 2), Width = GuiLength.Pixels(92), Height = GuiLength.Pixels(18),
                BackgroundColor = StormChaseHud.Theme.KeyCap, BorderColor = t.TileEdge, BorderThickness = 1, CornerRadius = 4 });
            items.Add(BuildText(key, controls.X + 14, y, 92, 8, t.Ink, true, GuiTextAlignment.Center));
            items.Add(BuildText(what, controls.X + 116, y, controls.Width - 130, 8.5f, t.Muted, false));
            y += 22;
        }
        items.Add(Button("lot/build/camera", "RESET CAMERA", new(controls.X + 14, controls.Bottom - 40, 130, 28)));
        items.Add(Button("lot/build/exit", "EXIT BUILD MODE", new(controls.Right - 158, controls.Bottom - 40, 144, 28)));

        // Catalog (bottom): categories, items, and the selected item.
        var catalog = new GuiRect(16, h - 262, w - 32, 248);
        Panel(catalog, "");
        items.Add(BuildText("BUILD CATALOG", catalog.X + 14, catalog.Y + 10, 200, 11.5f, t.Ink, true));
        for (int i = 0; i < Categories.Length; i++)
            items.Add(Button("lot/build/cat/" + Categories[i].Id, Categories[i].Label, new(catalog.X + 12, catalog.Y + 36 + i * 25.5f, 190, 22),
                Categories[i].Id == catalogCategory, 7.5f));
        var selectedPanel = new GuiRect(catalog.Right - 290, catalog.Y + 10, 278, catalog.Height - 20);
        var grid = new GuiRect(catalog.X + 214, catalog.Y + 36, selectedPanel.X - catalog.X - 226, catalog.Height - 48);
        var list = Catalog(catalogCategory);
        int columns = Math.Max(1, (int)(grid.Width / 214)), rows = 3, perPage = columns * rows;
        int pages = Math.Max(1, (list.Count + perPage - 1) / perPage);
        catalogPage = Math.Clamp(catalogPage, 0, pages - 1);
        items.Add(BuildText(Categories.First(c => c.Id == catalogCategory).Label, grid.X, catalog.Y + 12, 300, 9, t.Accent, true));
        if (pages > 1)
        {
            items.Add(Button("lot/build/page/-1", "‹", new(grid.Right - 116, catalog.Y + 8, 30, 22)));
            items.Add(BuildText($"{catalogPage + 1} / {pages}", grid.Right - 82, catalog.Y + 12, 50, 8.5f, t.Muted, false, GuiTextAlignment.Center));
            items.Add(Button("lot/build/page/1", "›", new(grid.Right - 30, catalog.Y + 8, 30, 22)));
        }
        float cardW = (grid.Width - (columns - 1) * 8) / columns, cardH = (grid.Height - (rows - 1) * 8) / rows;
        for (int i = 0; i < perPage && catalogPage * perPage + i < list.Count; i++)
        {
            int index = catalogPage * perPage + i; var item = list[index];
            var r = new GuiRect(grid.X + i % columns * (cardW + 8), grid.Y + i / columns * (cardH + 8), cardW, cardH);
            bool selected = item.Selected(build);
            var card = Button("lot/build/item/" + index, "", r, selected);
            items.Add(card);
            float textX = r.X + 10;
            if (item.Swatch is { } swatch)
            {
                items.Add(new GuiOverlayPanel { Translation = new(r.X + 8, r.Y + 8), Width = GuiLength.Pixels(cardH - 16), Height = GuiLength.Pixels(cardH - 16),
                    BackgroundColor = Hex(swatch), BorderColor = t.TileEdge, BorderThickness = 1, CornerRadius = 5 });
                textX = r.X + cardH;
            }
            items.Add(BuildText(item.Name, textX, r.Y + 8, r.Right - textX - 8, 9, t.Ink, true));
            items.Add(BuildText(item.Detail, textX, r.Y + 24, r.Right - textX - 8, 7.5f, t.Muted, false, wrap: true, height: cardH - 28));
        }
        var chosen = list.FirstOrDefault(i => i.Selected(build)) ?? (selectedItem is { } si && si.Selected(build) ? si : null);
        items.Add(new GuiOverlayPanel { Translation = new(selectedPanel.X, selectedPanel.Y), Width = GuiLength.Pixels(selectedPanel.Width), Height = GuiLength.Pixels(selectedPanel.Height),
            BackgroundColor = new(12, 30, 48, 240), BorderColor = t.Edge, BorderThickness = 1, CornerRadius = 8 });
        items.Add(BuildText("SELECTED", selectedPanel.X + 12, selectedPanel.Y + 10, 200, 8, t.Accent, true));
        items.Add(BuildText(chosen?.Name ?? LotBuildController.ToolName(build.Tool), selectedPanel.X + 12, selectedPanel.Y + 26, selectedPanel.Width - 24, 13, t.Ink, true));
        items.Add(BuildText(chosen?.Detail ?? LotBuildController.ToolHelp(build.Tool), selectedPanel.X + 12, selectedPanel.Y + 56, selectedPanel.Width - 24, 8.5f, t.Muted, false, wrap: true, height: 70));
        float sy = selectedPanel.Y + 130;
        void Option(string label, string value) { items.Add(BuildText(label, selectedPanel.X + 12, sy, 100, 8, t.Faint, false)); items.Add(BuildText(value, selectedPanel.X + 100, sy, selectedPanel.Width - 112, 8, t.Ink, true)); sy += 17; }
        var sel = build.Selection;
        Option("Wall style", ModuleCatalog.Style(sel.WallStyle)?.Name ?? sel.WallStyle);
        Option("Paint", ModuleCatalog.Paint(sel.WallPaint)?.Name ?? "None");
        Option("Floor", (ModuleCatalog.FloorFinishes.FirstOrDefault(f => f.Id == sel.FloorFinish).Name ?? sel.FloorFinish) + (ModuleCatalog.Paint(sel.FloorPaint) is { } fp ? " · " + fp.Name : ""));
        Option("Roof", $"{sel.RoofShape} · {ModuleCatalog.RoofFinishes.FirstOrDefault(f => f.Id == sel.RoofFinish).Name}");
        return items;
    }

    private static GuiLabel BuildText(string text, float x, float y, float width, float size, GuiColor color, bool bold, GuiTextAlignment align = GuiTextAlignment.Left,
        bool wrap = false, float height = 0) => new()
    {
        Text = text, Translation = new(x, y), Width = GuiLength.Pixels(width), Height = GuiLength.Pixels(height > 0 ? height : size * 1.9f),
        WrapMode = wrap ? GuiTextWrapMode.Wrap : GuiTextWrapMode.NoWrap, TextAlignment = align,
        Style = new(bold ? StormChaseHud.Theme.Bold : StormChaseHud.Theme.Font, size, 1.15f, color),
    };
}
