using Engine.GUI;
using Engine.Rendering.Assets;
using LifeCare.Construction;
using Shared.Contracts.Content;
using Shared.Contracts.Rendering;
using Shared.Contracts.World;

/// <summary>Mouse buttons pressed while mouse look is captured: world interactions aimed with the crosshair.</summary>
internal enum WorldClick { PrimaryDown, PrimaryUp, SecondaryDown }

/// <summary>Draws lots from their authoritative revisions, plus build and furniture ghosts. Shared by every client.</summary>
internal sealed class StormChaseLotRenderer
{
    private static readonly AssetHandle Cube = StormChaseLotParts.Cube;
    private static readonly (string Name, ColorRgba Tint, float Emissive)[] Materials =
    [
        ("floor", new(.55f, .47f, .36f, 1), 0), ("wall", new(.82f, .8f, .74f, 1), 0), ("wall-interior", new(.9f, .88f, .82f, 1), 0),
        ("roof", new(.32f, .2f, .16f, 1), 0), ("wood", new(.36f, .22f, .12f, 1), 0), ("fabric", new(.2f, .38f, .44f, 1), 0),
        ("metal", new(.42f, .45f, .48f, 1), 0), ("lamp", new(.95f, .85f, .62f, 1), 1.4f),
        ("ghost-valid", new(.25f, .95f, .55f, 1), 1.1f), ("ghost-invalid", new(1f, .28f, .25f, 1), 1.1f),
    ];

    public StormChaseLotRenderer(MutableRenderAssetCatalog catalog)
    {
        catalog.UpsertMesh(StormChaseTerrainGeometry.Box(Cube));
        foreach (var (name, tint, emissive) in Materials)
            catalog.UpsertMaterial(new()
            {
                Handle = new("material", "lot/" + name), ShaderProgramId = BuiltInShaderLibrary.PbrLit.Id,
                LightingProfile = RenderMaterialLightingProfile.MetallicRoughness, Tint = tint, RoughnessStrength = .8f,
                MetalnessStrength = name == "metal" ? .5f : 0, EmissiveColor = emissive > 0 ? tint : ColorRgba.Black, EmissiveIntensity = emissive,
            });
    }

    /// <summary>The render item for a piece: its stable id doubles as the motion-vector id the host's debris names.</summary>
    public static RenderItem Item(LotPiece piece, string? material = null) => StormChaseLotParts.Item(piece, material);

    public static IEnumerable<RenderItem> Items(IEnumerable<LotPiece> pieces, Vector3f observer, Func<string, bool> released, float range = 900)
    {
        foreach (var piece in pieces)
        {
            if (released(piece.PieceId)) continue;
            float dx = piece.CenterX - observer.X, dz = piece.CenterZ - observer.Z;
            if (dx * dx + dz * dz <= range * range) yield return Item(piece);
        }
    }

    public static IEnumerable<RenderItem> Ghost(IEnumerable<LotPiece> pieces, bool valid) =>
        pieces.Select(p => Item(p with { PieceId = "ghost/" + p.PieceId, SizeX = p.SizeX + .04f, SizeY = p.SizeY + .04f, SizeZ = p.SizeZ + .04f }, valid ? "ghost-valid" : "ghost-invalid"));
}

/// <summary>
/// LifeCare lots in online play. Lots come from the Construction service through the Gateway and are drawn from their
/// revisions. The owner's lot gets Tiny Clinic-style build mode (Y: floors, walls, roofs only) and world furniture
/// handling: packages are unpacked (I) and set down, placed pieces open the pie wheel (right click) to move, turn or
/// pick up. Everything is aimed with the crosshair and every edit goes to the authority.
/// </summary>
internal sealed class StormChaseLotPlay : IDisposable
{
    private readonly ConstructionClient client;
    private readonly string characterId;
    private readonly StormChaseWorld world;
    private readonly Dictionary<string, LotSession> lots = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (long Revision, IReadOnlyList<LotPiece> Pieces, float Base)> geometry = new(StringComparer.Ordinal);
    private Task<IReadOnlyList<LotDocument>>? refresh;
    private double nextRefresh;
    private LotBuildController? build;
    private LotFurnishingController? furnish;
    private int packageCursor;
    private string? unavailable;

    public StormChaseLotPlay(string gatewayUrl, string accessToken, string characterId, StormChaseWorld world)
    {
        client = new(gatewayUrl, accessToken); this.characterId = characterId; this.world = world;
    }

    public bool BuildMode { get; private set; }
    public InteractionPieMenu? Pie { get; private set; }
    public string Status => unavailable ?? build?.Status ?? (lots.Count == 0 ? "No lots nearby." : "");
    public LotSession? OwnLot => build?.Session;

    /// <summary>Every piece currently built, for drawing and for naming debris the host releases.</summary>
    public IEnumerable<LotPiece> Pieces => geometry.Values.SelectMany(g => g.Pieces);
    public LotPiece? Piece(string pieceId) => pieceId.StartsWith("lot/", StringComparison.Ordinal) ? Pieces.FirstOrDefault(p => p.PieceId == pieceId) : null;

    private Task<LotDocument?>? claim;
    private Vector3f lastFeet;

    public void Tick(double seconds)
    {
        foreach (var session in lots.Values) session.Tick();
        if (claim is { IsCompleted: true } claimed)
        {
            claim = null;
            unavailable = claimed.IsFaulted ? claimed.Exception!.GetBaseException().Message
                : claimed.Result is null ? "No lot to build on: deeds come from buying a home." : "Starter lot claimed just ahead of you. Press Y to build.";
            nextRefresh = 0;
        }
        if (refresh is { IsCompleted: true } done)
        {
            refresh = null;
            if (done.IsFaulted) unavailable = "Lots are unavailable: " + done.Exception!.GetBaseException().Message;
            else
            {
                unavailable = null;
                foreach (var document in done.Result)
                {
                    if (lots.TryGetValue(document.Lot.LotId, out var session)) session.Accept(document);
                    else
                    {
                        lots[document.Lot.LotId] = session = new LotSession(client, characterId, document);
                        if (build is null && session.IsOwner) { build = new(session); furnish = new(session); }
                    }
                }
            }
        }
        if (refresh is null && seconds >= nextRefresh) { nextRefresh = seconds + 2; refresh = Refresh(); }
        foreach (var (id, session) in lots)
            if (!geometry.TryGetValue(id, out var g) || g.Revision != session.Lot.Revision)
            {
                float baseY = g.Pieces is null ? LotGeometry.BaseElevation(session.Lot.Lot, world.HeightAt) : g.Base;
                geometry[id] = (session.Lot.Revision, LotGeometry.Build(session.Lot, baseY), baseY);
            }
    }

    private async Task<IReadOnlyList<LotDocument>> Refresh()
    {
        var known = lots.ToDictionary(p => p.Key, p => p.Value.Lot.Revision);
        var result = new List<LotDocument>();
        foreach (var summary in await client.ListAsync())
            if (!known.TryGetValue(summary.Lot.LotId, out long revision) || revision < summary.Revision)
                if (await client.GetAsync(summary.Lot.LotId) is { } document) result.Add(document);
        return result;
    }

    /// <summary>Keys this layer uses; returns true when it consumed the key.</summary>
    public bool HandleKey(int key)
    {
        if (key == 0x59)
        {
            if (build is null)
            {
                // Play-testing: ask for a dev starter lot just ahead of where you stand (only if the service offers them).
                if (claim is null || claim.IsCompleted) claim = client.ClaimStarterAsync(new(characterId, (int)MathF.Floor(lastFeet.X / BuildGrid.TileMeters), (int)MathF.Floor(lastFeet.Z / BuildGrid.TileMeters)));
                unavailable = "Claiming a starter lot…"; nextRefresh = 0;
                return true;
            }
            BuildMode = !BuildMode; build.CancelGesture(); furnish?.Cancel(); Pie = null;
            build.Session.Status = BuildMode ? "Build mode: 1 floor · 2 wall · 3 roof · 4 delete · PgUp/PgDn level · click or drag to build." : "Left build mode.";
            return true;
        }
        if (BuildMode && build is not null)
        {
            switch (key)
            {
                case 0x31: build.SelectTool(LotBuildTool.Floor); return true;
                case 0x32: build.SelectTool(LotBuildTool.Wall); return true;
                case 0x33: build.SelectTool(LotBuildTool.Roof); return true;
                case 0x34: build.SelectTool(LotBuildTool.Delete); return true;
                case 0x21: build.ChangeLevel(1); return true;
                case 0x22: build.ChangeLevel(-1); return true;
            }
        }
        if (furnish is null) return false;
        if (key == 0x49 && !BuildMode && furnish.Gesture == FurnishingGesture.None)
        {
            // Stand-in for Pedizon delivery until the shop ships packages: unpack the next catalogue item.
            var entry = FurnishingCatalog.Entries[packageCursor++ % FurnishingCatalog.Entries.Count];
            string prefix = $"pkg-{characterId[..Math.Min(8, characterId.Length)]}-{entry.Definition.DefinitionId}-";
            int n = 1; while (furnish.Session.Lot.Furnishings.Any(f => f.InstanceId == prefix + n)) n++;
            furnish.BeginCarry(new(prefix + n, entry.Definition), ActiveLevel);
            return true;
        }
        if (key == 0x52 && furnish.Gesture != FurnishingGesture.None) { furnish.Rotate(); return true; }
        return false;
    }

    /// <summary>Esc: closes the wheel, then cancels a drag, a move or a carry. Returns false if there was nothing to cancel.</summary>
    public bool Cancel()
    {
        if (Pie is not null) { Pie = null; return true; }
        if (build?.CancelGesture() == true) return true;
        if (furnish?.Cancel() == true) return true;
        if (BuildMode) { BuildMode = false; return true; }
        return false;
    }

    private int playerLevel;
    private int ActiveLevel => BuildMode && build is not null ? build.Level : playerLevel;

    /// <summary>Aims at the build plane with the crosshair and applies world clicks.</summary>
    public void Update(CameraState camera, Vector3f feet, IEnumerable<WorldClick> clicks, int viewportWidth, int viewportHeight)
    {
        lastFeet = feet;
        if (build is null || furnish is null) { foreach (var _ in clicks) { } return; }
        float baseY = geometry.TryGetValue(build.Session.Lot.Lot.LotId, out var g) ? g.Base : feet.Y;
        playerLevel = Math.Clamp((int)MathF.Round((feet.Y - baseY) / LotGeometry.StoryMeters), 0, LotBuildController.MaximumLevels - 1);
        float plane = LotGeometry.LevelElevation(baseY, ActiveLevel) + LotGeometry.FloorThickness;
        (float X, float Z)? aim = null;
        if (camera.Forward.Y < -.02f)
        {
            float t = (plane - camera.Position.Y) / camera.Forward.Y;
            if (t is > 0 and < 80) aim = (camera.Position.X + camera.Forward.X * t, camera.Position.Z + camera.Forward.Z * t);
        }
        if (aim is { } p) { if (BuildMode) build.SetPointer(p.X, p.Z); else furnish.SetPointer(p.X, p.Z); }
        foreach (var click in clicks)
        {
            if (Pie is not null) continue;
            if (BuildMode)
            {
                if (click == WorldClick.PrimaryDown) build.BeginGesture();
                else if (click == WorldClick.PrimaryUp) build.CompleteGesture();
                continue;
            }
            if (click == WorldClick.PrimaryDown && furnish.Gesture != FurnishingGesture.None) furnish.SetDown();
            else if (click == WorldClick.SecondaryDown && furnish.Gesture == FurnishingGesture.None && aim is { } at)
            {
                var session = lots.Values.FirstOrDefault(s => LotFurnishingController.HitTest(s.Lot, at.X, at.Z, playerLevel) is not null);
                if (session is null) continue;
                var piece = LotFurnishingController.HitTest(session.Lot, at.X, at.Z, playerLevel)!;
                string name = FurnishingCatalog.Find(piece.DefinitionId)?.Definition.Name ?? piece.DefinitionId;
                Pie = InteractionPieMenu.ForFurnishing(piece, name, session.IsOwner && session == furnish.Session, viewportWidth / 2, viewportHeight / 2, viewportWidth, viewportHeight);
            }
        }
    }

    /// <summary>HUD button ids beginning with lot/.</summary>
    public void HandleGuiAction(string action)
    {
        if (Pie is not { } menu || furnish is null) return;
        if (action == "lot/pie/outside") { Pie = null; return; }
        if (action == "lot/pie/center") { if (!menu.NavigateBack()) Pie = null; return; }
        if (!action.StartsWith("lot/pie/", StringComparison.Ordinal) || !int.TryParse(action.AsSpan(8), out int slot)) return;
        if (menu.SelectAction(slot) is not { } invocation) return;
        Pie = null;
        switch (invocation.ActionId)
        {
            case InteractionPieMenu.MoveAction: furnish.BeginMove(invocation.TargetId); break;
            case InteractionPieMenu.RotateAction: furnish.RotateInPlace(invocation.TargetId); break;
            case InteractionPieMenu.PickUpAction: furnish.PickUp(invocation.TargetId); break;
        }
    }

    public IEnumerable<RenderItem> RenderItems(Vector3f observer, Func<string, bool> released)
    {
        foreach (var item in StormChaseLotRenderer.Items(Pieces, observer, released)) yield return item;
        if (build is null || !geometry.TryGetValue(build.Session.Lot.Lot.LotId, out var g)) yield break;
        if (BuildMode && build.Preview.Edit is not null)
            foreach (var item in StormChaseLotRenderer.Ghost(LotGeometry.Ghost(build.Session.Lot.Lot.LotId, build.Preview, g.Base), build.Preview.IsValid)) yield return item;
        if (!BuildMode && furnish?.Preview is { } ghost)
            foreach (var item in StormChaseLotRenderer.Ghost([LotGeometry.Furnishing(build.Session.Lot.Lot.LotId, ghost.Candidate, g.Base)], ghost.IsValid)) yield return item;
    }

    /// <summary>The status-pill line for the HUD: what build mode or the carried piece is doing, else the lot layer's last message.</summary>
    public string? StatusLine => BuildMode && build is not null
        ? $"BUILD · {build.Tool.ToString().ToUpperInvariant()} · {(build.Level == 0 ? "GROUND" : "FLOOR " + (build.Level + 1))}  —  {build.Preview.Status}"
        : furnish?.Preview is { } ghost ? $"{(furnish.Gesture == FurnishingGesture.CarryingPackage ? "Carrying " + furnish.Carried!.Definition.Name : "Moving")}  —  {ghost.Status}  ·  R turn, click set down"
        : unavailable;

    /// <summary>Hotbar contents: build tools in build mode, the carried piece in hand, otherwise null (empty hands).</summary>
    public IReadOnlyList<HudSlot>? Hotbar()
    {
        if (BuildMode && build is not null)
            return [new("Floor", build.Tool == LotBuildTool.Floor), new("Wall", build.Tool == LotBuildTool.Wall), new("Roof", build.Tool == LotBuildTool.Roof), new("Delete", build.Tool == LotBuildTool.Delete)];
        if (furnish?.Gesture == FurnishingGesture.CarryingPackage) return [new(furnish.Carried!.Definition.Name, true)];
        if (furnish?.Gesture == FurnishingGesture.MovingPiece) return [new("Moving", true)];
        return null;
    }

    /// <summary>Lots for the map.</summary>
    public IReadOnlyList<MapLot> MapLots() => lots.Values.Select(s =>
    {
        var d = s.Lot.Lot;
        return new MapLot(d.LotId, d.MinimumTileX * BuildGrid.TileMeters, d.MinimumTileZ * BuildGrid.TileMeters, (d.MaximumTileX + 1) * BuildGrid.TileMeters,
            (d.MaximumTileZ + 1) * BuildGrid.TileMeters, s.IsOwner, s.Lot.Floors.Length + s.Lot.Walls.Length + s.Lot.Furnishings.Length);
    }).ToArray();

    /// <summary>The interaction wheel. It behaves like Tiny Clinic's pie menu but is drawn with the HUD's own glass, tiles and type.</summary>
    public IEnumerable<GuiElement> Overlay(int width, int height) => Pie is { } menu ? PieOverlay(menu, width, height) : [];

    public static IEnumerable<GuiElement> PieOverlay(InteractionPieMenu menu, int width, int height)
    {
        var theme = (Glass: StormChaseHud.Theme.Glass, Edge: StormChaseHud.Theme.GlassEdge, Tile: StormChaseHud.Theme.Tile, TileEdge: StormChaseHud.Theme.TileEdge,
            Header: StormChaseHud.Theme.Header, Accent: StormChaseHud.Theme.Accent, Ink: StormChaseHud.Theme.Ink, Muted: StormChaseHud.Theme.Muted, Faint: StormChaseHud.Theme.Faint);
        float size = InteractionPieMenu.WheelSize;
        yield return new GuiButton { Id = "lot/pie/outside", Text = "", Width = GuiLength.Pixels(width), Height = GuiLength.Pixels(height), BackgroundColor = new(2, 8, 14, 90),
            HoverBackgroundColor = new(2, 8, 14, 90), PressedBackgroundColor = new(2, 8, 14, 110) };
        yield return new GuiOverlayPanel { Translation = new(menu.X, menu.Y), Width = GuiLength.Pixels(size), Height = GuiLength.Pixels(size),
            BackgroundColor = theme.Glass, BorderColor = theme.Edge, BorderThickness = 1, CornerRadius = size / 2 };
        yield return new GuiOverlayPanel { Translation = new(menu.X + 60, menu.Y + 60), Width = GuiLength.Pixels(size - 120), Height = GuiLength.Pixels(size - 120),
            BorderColor = theme.Edge.WithAlpha(50), BorderThickness = 1, CornerRadius = (size - 120) / 2 };
        for (int i = 0; i < Math.Min(menu.Actions.Count, InteractionPieMenu.VisibleSlotCount); i++)
        {
            var slot = menu.Slot(i); var action = menu.Actions[i];
            yield return new GuiButton { Id = "lot/pie/" + i, Text = action.HasChildren ? action.Label + "  ›" : action.Label, Translation = new(slot.X, slot.Y),
                Width = GuiLength.Pixels(slot.Width), Height = GuiLength.Pixels(slot.Height), TextStyle = new(StormChaseHud.Theme.Bold, 9.5f, 1, theme.Ink),
                TextAlignment = GuiTextAlignment.Center, Padding = new(6, (slot.Height - 9.5f * 1.35f) / 2), CornerRadius = 9,
                BackgroundColor = theme.Tile, HoverBackgroundColor = new(34, 104, 156, 250), PressedBackgroundColor = new(10, 40, 70, 255), BorderColor = theme.TileEdge, BorderThickness = 1 };
        }
        var center = menu.Center;
        yield return new GuiButton { Id = "lot/pie/center", Text = "", Translation = new(center.X, center.Y), Width = GuiLength.Pixels(center.Width), Height = GuiLength.Pixels(center.Height),
            CornerRadius = center.Width / 2, BackgroundColor = theme.Header, HoverBackgroundColor = new(20, 44, 66, 250), PressedBackgroundColor = new(10, 26, 40, 255),
            BorderColor = theme.Accent, BorderThickness = 2 };
        yield return Text(menu.Category, center.X, center.Y + 26, center.Width, 7.5f, theme.Faint, true, GuiTextAlignment.Center);
        yield return Text(menu.DisplayName, center.X + 4, center.Y + 42, center.Width - 8, 10, theme.Ink, true, GuiTextAlignment.Center);
        yield return Text(menu.CanNavigateBack ? "‹ BACK" : "CLOSE", center.X, center.Y + 64, center.Width, 7.5f, theme.Accent, true, GuiTextAlignment.Center);
        if (menu.Feedback is { Length: > 0 } feedback)
        {
            yield return new GuiOverlayPanel { Translation = new(menu.X + 10, menu.Y + size + 10), Width = GuiLength.Pixels(size - 20), Height = GuiLength.Pixels(54),
                BackgroundColor = theme.Glass, BorderColor = theme.Edge, BorderThickness = 1, CornerRadius = 12 };
            yield return new GuiLabel { Text = feedback, Translation = new(menu.X + 24, menu.Y + size + 18), Width = GuiLength.Pixels(size - 48), Height = GuiLength.Pixels(42),
                WrapMode = GuiTextWrapMode.Wrap, Style = new(StormChaseHud.Theme.Font, 9, 1.2f, theme.Muted) };
        }
    }

    private static GuiLabel Text(string text, float x, float y, float width, float size, GuiColor color, bool bold, GuiTextAlignment align = GuiTextAlignment.Left) => new()
    {
        Text = text, Translation = new(x, y), Width = GuiLength.Pixels(width), Height = GuiLength.Pixels(size * 1.9f), WrapMode = GuiTextWrapMode.NoWrap, TextAlignment = align,
        Style = new(bold ? StormChaseHud.Theme.Bold : StormChaseHud.Theme.Font, size, 1.1f, color),
    };

    public void Dispose() => client.Dispose();
}
