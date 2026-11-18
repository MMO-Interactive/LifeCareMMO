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

    /// <summary>The white, vertex-coloured material module and curved meshes draw with: their colours are in the vertices.</summary>
    private const string ModuleMaterial = "module";
    private static StormChaseLotRenderer? active;
    private readonly MutableRenderAssetCatalog catalog;
    private readonly ModuleMeshLibrary? modules;
    private readonly Dictionary<string, AssetHandle?> meshes = new(StringComparer.Ordinal);

    public StormChaseLotRenderer(MutableRenderAssetCatalog catalog, string? modulePack = null)
    {
        this.catalog = catalog;
        catalog.UpsertMesh(StormChaseTerrainGeometry.Box(Cube));
        foreach (var (name, tint, emissive) in Materials.Append((ModuleMaterial, new(1, 1, 1, 1), 0)))
            catalog.UpsertMaterial(new()
            {
                Handle = new("material", "lot/" + name), ShaderProgramId = BuiltInShaderLibrary.PbrLit.Id,
                LightingProfile = RenderMaterialLightingProfile.MetallicRoughness, Tint = tint, RoughnessStrength = .8f,
                MetalnessStrength = name == "metal" ? .5f : 0, EmissiveColor = emissive > 0 ? tint : ColorRgba.Black, EmissiveIntensity = emissive,
            });
        try
        {
            if ((modulePack ?? ModuleMeshLibrary.Locate(AppContext.BaseDirectory)) is { } pack)
            {
                modules = ModuleMeshLibrary.Load(pack);
                Console.WriteLine($"Lot modules: {modules.Count} from {pack}");
            }
            else Console.WriteLine("Lot modules: rural module pack not found; lots draw as boxes.");
        }
        catch (Exception ex) { Console.WriteLine("Lot modules failed to load; lots draw as boxes: " + ex.Message); }
        active = this;
    }

    public int ModuleCount => modules?.Count ?? 0;

    /// <summary>
    /// The render item for a piece: its stable id doubles as the motion-vector id the host's debris names. Pieces with pack
    /// modules or a curved arc draw their real mesh at the piece's centre and yaw (scale 1), so the same item also flies
    /// correctly as debris with the body's rotation. Anything else, or a missing pack, draws the physics box.
    /// </summary>
    public static RenderItem Item(LotPiece piece, string? material = null)
    {
        if (active?.MeshFor(piece) is { } mesh)
        {
            string id = material ?? ModuleMaterial;
            return new("lot/" + id, mesh, new AssetHandle("material", "lot/" + id), new(piece.CenterX, piece.CenterY, piece.CenterZ), new(1, 1, 1),
                piece.YawDegrees, material is null, MotionVectorId: piece.PieceId, AllowStaticInstancing: material is null);
        }
        return StormChaseLotParts.Item(piece, material);
    }

    private AssetHandle? MeshFor(LotPiece piece)
    {
        if (piece.MeshKey is not { } key) return null;
        if (meshes.TryGetValue(key, out var cached)) return cached;
        MeshBuffer? data = piece.Door is { CurvedKind: not null } ? DoorLeafMesher.ForPiece(piece)
            : piece.Curve is not null ? CurvedWallMesher.ForPiece(piece) : modules?.Compose(piece.Modules);
        AssetHandle? handle = null;
        if (data is { VertexCount: > 0 })
        {
            handle = new AssetHandle("mesh", "lot/mesh/" + meshes.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
            catalog.UpsertMesh(ToRender(handle.Value, data));
        }
        if (meshes.Count > 20_000) meshes.Clear();
        meshes[key] = handle;
        return handle;
    }

    private static RenderMeshData ToRender(AssetHandle handle, MeshBuffer data)
    {
        var vertices = new RenderMeshVertex[data.VertexCount];
        for (int i = 0; i < vertices.Length; i++)
        {
            var p = data.Positions[i]; var n = data.Normals[i]; var c = data.Colors[i]; var uv = data.Uvs[i];
            vertices[i] = new(new(p.X, p.Y, p.Z), new(c.X, c.Y, c.Z, c.W), uv.X, uv.Y, new(n.X, n.Y, n.Z), new(1, 0, 0), 1);
        }
        return new RenderMeshData { Handle = handle, Vertices = vertices, WideIndices = data.Indices.Select(i => (uint)i).ToArray() };
    }

    public static IEnumerable<RenderItem> Items(IEnumerable<LotPiece> pieces, Vector3f observer, Func<string, bool> released, float range = 900)
    {
        active?.Tick();
        foreach (var piece in pieces)
        {
            if (released(piece.PieceId)) continue;
            float dx = piece.CenterX - observer.X, dz = piece.CenterZ - observer.Z;
            if (dx * dx + dz * dz > range * range) continue;
            yield return piece.Door is { } door && active?.AnimatedLeaf(piece, door) is { } leaf ? leaf : Item(piece);
        }
    }

    // ---------------------------------------------------------------- doors: leaves swing, slide or lift between the server's states
    private readonly Dictionary<string, float> doorFraction = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (bool Open, double Until)> doorRequested = new(StringComparer.Ordinal);
    private readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
    private double lastTick;
    private float tickDelta;
    private const float DoorSecondsToOpen = .6f;

    private void Tick() { double now = clock.Elapsed.TotalSeconds; tickDelta = (float)Math.Clamp(now - lastTick, 0, .1); lastTick = now; }

    /// <summary>Shows a door moving the moment the player asks; the server's state takes over when it arrives or after a few seconds.</summary>
    public static void RequestDoor(string doorId, bool open) { if (active is { } r) r.doorRequested[doorId] = (open, r.clock.Elapsed.TotalSeconds + 4); }

    /// <summary>Whether a door looks open to this player: a pending request, else the server's state.</summary>
    public static bool? DoorShownOpen(string doorId) => active is { } r && r.doorRequested.TryGetValue(doorId, out var q) && q.Until > r.clock.Elapsed.TotalSeconds ? q.Open : null;

    private RenderItem? AnimatedLeaf(LotPiece piece, DoorLeaf door)
    {
        bool target = door.Open;
        if (doorRequested.TryGetValue(door.DoorId, out var request))
        {
            if (request.Until < clock.Elapsed.TotalSeconds || request.Open == door.Open) doorRequested.Remove(door.DoorId);
            else target = request.Open;
        }
        float f = doorFraction.TryGetValue(door.DoorId + piece.PieceId, out float known) ? known : door.Open ? 1 : 0;
        f = Math.Clamp(f + (target ? 1 : -1) * tickDelta / DoorSecondsToOpen, 0, 1);
        doorFraction[door.DoorId + piece.PieceId] = f;
        float eased = f * f * (3 - 2 * f);
        string? module = door.Motion == "overhead" && f >= .5f ? door.OpenModule : door.ClosedModule;
        string key = door.CurvedKind is { } kind ? $"hingecurve:{kind}:{door.CurvedColour}:{door.CurvedWidth:R}" : "hinge:" + module;
        if (!meshes.TryGetValue(key, out var mesh))
        {
            MeshBuffer? data = door.CurvedKind is { } k ? DoorLeafMesher.Build(k, door.CurvedColour ?? "wood", door.CurvedWidth)
                : module is null ? null : modules?.Compose([new(module, 0, 0, 0, 0)]);
            mesh = null;
            if (data is { VertexCount: > 0 })
            {
                mesh = new AssetHandle("mesh", "lot/mesh/" + meshes.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
                catalog.UpsertMesh(ToRender(mesh.Value, data));
            }
            meshes[key] = mesh;
        }
        if (mesh is null) return null;
        float yaw = door.ClosedYawDegrees + (door.Motion == "swing" ? 90 * eased : 0);
        float slide = door.Motion == "slide" ? eased : 0;
        return new("lot/" + ModuleMaterial, mesh.Value, new AssetHandle("material", "lot/" + ModuleMaterial),
            new(door.PivotX + door.SlideX * slide, door.PivotY, door.PivotZ + door.SlideZ * slide), new(1, 1, 1), yaw, true,
            MotionVectorId: piece.PieceId, AllowStaticInstancing: false, WritesMotionVectors: f is > 0 and < 1);
    }

    /// <summary>Ghosts draw as tinted boxes, except curved walls, which preview their true curved mesh.</summary>
    public static IEnumerable<RenderItem> Ghost(IEnumerable<LotPiece> pieces, bool valid) =>
        pieces.Select(p => Item(p.Curve is not null ? p with { PieceId = "ghost/" + p.PieceId }
            : p with { PieceId = "ghost/" + p.PieceId, SizeX = p.SizeX + .04f, SizeY = p.SizeY + .04f, SizeZ = p.SizeZ + .04f, Modules = [] }, valid ? "ghost-valid" : "ghost-invalid"));
}

/// <summary>
/// LifeCare lots in online play. Lots come from the Construction service through the Gateway and are drawn from their
/// revisions. The owner's lot gets Tiny Clinic-style build mode (Y: floors, walls, roofs only) and world furniture
/// handling: packages are unpacked (I) and set down, placed pieces open the pie wheel (right click) to move, turn or
/// pick up. Everything is aimed with the crosshair and every edit goes to the authority.
/// </summary>
internal sealed partial class StormChaseLotPlay : IDisposable
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

    /// <param name="refreshToken">Keeps the lot client signed in past the 15-minute access token.</param>
    public StormChaseLotPlay(string gatewayUrl, string accessToken, string characterId, StormChaseWorld world, string? refreshToken = null)
    {
        accounts = new ChaseApiClient(); accounts.ResumeLogin(gatewayUrl, accessToken, refreshToken);
        client = new(gatewayUrl, accessToken, refreshAccess: string.IsNullOrWhiteSpace(refreshToken) ? null : async (rejected, cancel) =>
        {
            if (accounts.Login?.AccessToken == rejected) await accounts.RefreshLoginAsync(cancel);
            return accounts.Login?.AccessToken;
        });
        this.characterId = characterId; this.world = world;
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

    private string? lotMessage;
    private double lotMessageUntil, clockSeconds;

    /// <summary>The door under the crosshair within reach, on any lot (yours or a neighbour's).</summary>
    private (LotSession Session, LotDoor Door)? DoorAt(CameraState camera)
    {
        (LotSession, LotDoor, float)? best = null;
        var origin = new System.Numerics.Vector3(camera.Position.X, camera.Position.Y, camera.Position.Z);
        var direction = System.Numerics.Vector3.Normalize(new(camera.Forward.X, camera.Forward.Y, camera.Forward.Z));
        foreach (var (id, session) in lots)
        {
            if (!geometry.TryGetValue(id, out var g)) continue;
            if (LotDoors.Hit(LotDoors.All(session.Lot.ToPlan()), g.Base, origin, direction, LotDoors.InteractionRangeMeters + 1.5f) is { } hit
                && (best is null || hit.Distance < best.Value.Item3)) best = (session, hit.Door, hit.Distance);
        }
        return best is { } b ? (b.Item1, b.Item2) : null;
    }

    private (LotSession Session, LotDoor Door)? FindDoor(string target)
    {
        var parts = target.Split('|', 2);
        if (parts.Length != 2 || !lots.TryGetValue(parts[0], out var session)) return null;
        return LotDoors.All(session.Lot.ToPlan()).FirstOrDefault(d => d.DoorId == parts[1]) is { } door ? (session, door) : null;
    }

    /// <summary>Asks the lot authority to set the door; the leaf starts moving at once and the server's state settles it.</summary>
    private void SetDoor(string target, bool open)
    {
        if (FindDoor(target) is not { } found) { lotMessage = "That door is no longer there."; lotMessageUntil = clockSeconds + 4; return; }
        var (session, door) = found;
        string lotId = session.Lot.Lot.LotId, operation = Guid.NewGuid().ToString("N");
        if (session.Submit((authority, _) => authority.SetDoorAsync(lotId, new(characterId, door.DoorId, open, operation)), open ? "open the door" : "close the door"))
            StormChaseLotRenderer.RequestDoor(door.DoorId, open);
    }

    private void InspectDoor(string target)
    {
        if (FindDoor(target) is not { } found) return;
        var (session, door) = found;
        bool open = session.Lot.OpenDoors.Contains(door.DoorId);
        string whose = session.IsOwner ? "your lot" : "a neighbour's lot";
        lotMessage = $"{door.Name}, {door.WidthMeters:0.0#} m wide · {(open ? "open" : "closed")} · on {whose}. Anyone nearby can open or close it.";
        lotMessageUntil = clockSeconds + 6;
    }

    public void Tick(double seconds)
    {
        clockSeconds = seconds;
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
                        session.EditResolved += (_, message) => { lotMessage = message; lotMessageUntil = clockSeconds + 5; };
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
            if (BuildMode) EnterBuildCamera();
            build.Session.Status = BuildMode ? "Build mode: pick a tool from the catalog, or 1 room · 2 wall · 3 curved · 4 doors · 5 style · 6 paint · 7 floor · 8 roof · 9 delete." : "Left build mode.";
            return true;
        }
        if (furnish is null) return false;
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
        lastFeet = feet; lastCamera = camera;
        if (build is null || furnish is null || BuildMode)
        {
            // Build mode aims with the mouse from the build camera (UpdateBuild), not with the crosshair.
            // Without a lot of your own you can still use the doors on other lots.
            foreach (var click in clicks)
                if (!BuildMode && Pie is null && click == WorldClick.SecondaryDown && DoorAt(camera) is { } target)
                    Pie = InteractionPieMenu.ForDoor(target.Session.Lot.Lot.LotId + "|" + target.Door.DoorId, target.Door.Name,
                        StormChaseLotRenderer.DoorShownOpen(target.Door.DoorId) ?? target.Session.Lot.OpenDoors.Contains(target.Door.DoorId),
                        viewportWidth / 2, viewportHeight / 2, viewportWidth, viewportHeight);
            if (build is not null && geometry.TryGetValue(build.Session.Lot.Lot.LotId, out var g0))
                playerLevel = Math.Clamp((int)MathF.Round((feet.Y - g0.Base) / LotGeometry.StoryMeters), 0, LotBuildController.MaximumLevels - 1);
            return;
        }
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
            else if (click == WorldClick.SecondaryDown && furnish.Gesture == FurnishingGesture.None && DoorAt(camera) is { } target)
            {
                // Life Beyond's door wheel: Open or Close, and Inspect. Anyone near the door can use it; the server checks reach.
                bool isOpen = StormChaseLotRenderer.DoorShownOpen(target.Door.DoorId) ?? target.Session.Lot.OpenDoors.Contains(target.Door.DoorId);
                Pie = InteractionPieMenu.ForDoor(target.Session.Lot.Lot.LotId + "|" + target.Door.DoorId, target.Door.Name, isOpen,
                    viewportWidth / 2, viewportHeight / 2, viewportWidth, viewportHeight);
            }
            else if (click == WorldClick.SecondaryDown && furnish.Gesture == FurnishingGesture.None && aim is { } at)
            {
                var session = lots.Values.FirstOrDefault(s => LotFurnishingController.HitTest(s.Lot, at.X, at.Z, playerLevel) is not null);
                if (session is null)
                {
                    // Your own lot's ground: its wheel opens the delivery crate (an interim spot until the crate is a placed object) or build mode.
                    var tile = LotBuildController.TileAt(at.X, at.Z);
                    if (furnish.Session.IsOwner && furnish.Session.Lot.Lot.Contains(tile))
                        Pie = InteractionPieMenu.Create(DeliveryCrateId, "lot", "Your lot",
                            [PieMenuAction.Leaf("Open crate", "Open the delivery crate: packages delivered to your lot wait here.", OpenCrateAction),
                             PieMenuAction.Leaf("Build", "Edit floors, walls and roofs.", BuildModeAction)],
                            viewportWidth / 2, viewportHeight / 2, viewportWidth, viewportHeight);
                    continue;
                }
                var piece = LotFurnishingController.HitTest(session.Lot, at.X, at.Z, playerLevel)!;
                string name = FurnishingCatalog.Find(piece.DefinitionId)?.Definition.Name ?? piece.DefinitionId;
                Pie = InteractionPieMenu.ForFurnishing(piece, name, session.IsOwner && session == furnish.Session, viewportWidth / 2, viewportHeight / 2, viewportWidth, viewportHeight);
            }
        }
    }

    /// <summary>HUD button ids beginning with lot/.</summary>
    public void HandleGuiAction(string action)
    {
        if (HandleBuildAction(action)) return;
        if (Pie is not { } menu) return;
        if (action == "lot/pie/outside") { Pie = null; return; }
        if (action == "lot/pie/center") { if (!menu.NavigateBack()) Pie = null; return; }
        if (!action.StartsWith("lot/pie/", StringComparison.Ordinal) || !int.TryParse(action.AsSpan(8), out int slot)) return;
        if (menu.SelectAction(slot) is not { } invocation) return;
        Pie = null;
        switch (invocation.ActionId)
        {
            case InteractionPieMenu.MoveAction: furnish?.BeginMove(invocation.TargetId); break;
            case InteractionPieMenu.RotateAction: furnish?.RotateInPlace(invocation.TargetId); break;
            case InteractionPieMenu.PickUpAction: furnish?.PickUp(invocation.TargetId); break;
            case LotDoors.OpenAction or LotDoors.CloseAction: SetDoor(invocation.TargetId, invocation.ActionId == LotDoors.OpenAction); break;
            case InteractionPieMenu.InspectAction when invocation.TargetKind == "door": InspectDoor(invocation.TargetId); break;
            case OpenCrateAction: ContainerRequest = DeliveryCrateId; break;
            case BuildModeAction: HandleKey(0x59); break;
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
        ? $"BUILD · {LotBuildController.ToolName(build.Tool).ToUpperInvariant()} · {(build.Level == 0 ? "GROUND" : "FLOOR " + (build.Level + 1))}  —  {build.Preview.Status}"
        : furnish?.Preview is { } ghost ? $"{(furnish.Gesture == FurnishingGesture.CarryingPackage ? "Carrying " + furnish.Carried!.Definition.Name : "Moving")}  —  {ghost.Status}  ·  R turn, click set down"
        : unavailable ?? (clockSeconds < lotMessageUntil ? lotMessage : null);

    /// <summary>Hotbar contents: build tools in build mode, the carried piece in hand, otherwise null (empty hands).</summary>
    public IReadOnlyList<HudSlot>? Hotbar()
    {
        if (BuildMode && build is not null) return null;
        if (furnish?.Gesture == FurnishingGesture.CarryingPackage) return [new(furnish.Carried!.Definition.Name, true)];
        if (furnish?.Gesture == FurnishingGesture.MovingPiece) return [new("Moving", true)];
        return null;
    }

    /// <summary>
    /// Packages waiting to be unpacked. Until the shop delivers real orders this is a stand-in: one of each catalogue
    /// piece, available once you own a lot.
    /// </summary>
    public IReadOnlyList<HudItem> Deliveries() => furnish is null ? [] : FurnishingCatalog.Entries
        .Select(e => new HudItem(e.Definition.DefinitionId, e.Definition.Name, $"{e.Definition.FootprintWidthSubCells * BuildGrid.SubCellMeters:0.#} × {e.Definition.FootprintDepthSubCells * BuildGrid.SubCellMeters:0.#} m  ·  set it down on your lot's floor", "delivery")).ToArray();

    /// <summary>Unpacks a delivered piece into your hands, ready to set down.</summary>
    public bool Unpack(string definitionId)
    {
        if (furnish is null || build is null || FurnishingCatalog.Find(definitionId) is not { } entry) return false;
        if (BuildMode) { BuildMode = false; build.CancelGesture(); }
        furnish.Cancel();
        string prefix = $"pkg-{characterId[..Math.Min(8, characterId.Length)]}-{entry.Definition.DefinitionId}-";
        int n = 1; while (furnish.Session.Lot.Furnishings.Any(f => f.InstanceId == prefix + n)) n++;
        return furnish.BeginCarry(new(prefix + n, entry.Definition), ActiveLevel);
    }

    public const string DeliveryCrateId = "delivery-crate";
    public const string OpenCrateAction = "lifecare.lot.open-crate", BuildModeAction = "lifecare.lot.build";

    /// <summary>A container the player asked to open this frame (from the interaction wheel); the game opens its window.</summary>
    public string? ContainerRequest { get; set; }

    /// <summary>The delivery crate on your lot as a container window.</summary>
    public HudContainer DeliveryCrate() => new(DeliveryCrateId, "Delivery crate", "Packages delivered to your lot. Take one to carry it and set it down.", Deliveries(), "TAKE");

    /// <summary>The carried package as a Bag item, if any.</summary>
    public HudItem? CarriedItem => furnish?.Gesture == FurnishingGesture.CarryingPackage ? new("carried", furnish.Carried!.Definition.Name, "In your hands  ·  click to set it down, R to turn, Esc to put it back", "carried") : null;

    /// <summary>What is in the avatar's hands.</summary>
    public string Carrying => furnish?.Gesture switch
    {
        FurnishingGesture.CarryingPackage => furnish.Carried!.Definition.Name + " (package)",
        FurnishingGesture.MovingPiece => "Furniture being moved",
        _ => "Empty hands",
    };

    /// <summary>Lots for the map.</summary>
    public IReadOnlyList<MapLot> MapLots() => lots.Values.Select(s =>
    {
        var d = s.Lot.Lot;
        return new MapLot(d.LotId, d.MinimumTileX * BuildGrid.TileMeters, d.MinimumTileZ * BuildGrid.TileMeters, (d.MaximumTileX + 1) * BuildGrid.TileMeters,
            (d.MaximumTileZ + 1) * BuildGrid.TileMeters, s.IsOwner, s.Lot.Floors.Length + s.Lot.Walls.Length + s.Lot.Furnishings.Length);
    }).ToArray();

    /// <summary>The interaction wheel. It behaves like Tiny Clinic's pie menu but is drawn with the HUD's own glass, tiles and type.</summary>
    public IEnumerable<GuiElement> Overlay(int width, int height) => BuildMode ? BuildOverlay(width, height) : Pie is { } menu ? PieOverlay(menu, width, height) : [];

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

    private readonly ChaseApiClient accounts;
    public void Dispose() { client.Dispose(); accounts.Dispose(); }
}
