using LifeCare.Construction;

int failed = 0, passed = 0;
void Check(bool ok, string name) { if (ok) passed++; else failed++; Console.WriteLine((ok ? "PASS " : "FAIL ") + name); }

// ---- Life Beyond's documented capacity examples
Check(StructuralCapacity.MaximumStoriesForContiguousFootprint(4, 2, 2) == 3, "2x2 base supports three stories");
Check(StructuralCapacity.MaximumStoriesForContiguousFootprint(16, 4, 4) == 6, "4x4 base supports six stories");
Check(StructuralCapacity.MaximumStoriesForContiguousFootprint(196, 14, 14) == 51, "14x14 base supports fifty-one stories");
Check(StructuralCapacity.MaximumStoriesForContiguousFootprint(20, 20, 1) == 2, "A 20x1 strip is limited by its short span");
Check(StructuralCapacity.MaximumStoriesForContiguousFootprint(0, 0, 0) == 1, "No foundation supports only the ground story");

var lot = new LotDeed("lot-1", "owner", 0, 0, 9, 9);
var plan = BuildingPlan.Empty(lot);
BuildingPlan Must(BuildResult r, string name) { Check(r.Accepted, name + (r.Accepted ? "" : " â€” " + r.Message)); return r.Plan; }

// ---- lot rules
Check(!BuildingEditor.Apply(plan, "stranger", new(BuildEditKind.AddFloor, 0, 0, 0, 1, 1)).Accepted, "Only the owner can build");
Check(BuildingEditor.Apply(plan, "owner", new(BuildEditKind.AddFloor, 0, 8, 8, 10, 10)).ErrorCode == "outside_lot", "Floors must stay on the lot");

// ---- a 3x3 room with walls on all sides
plan = Must(BuildingEditor.Apply(plan, "owner", new(BuildEditKind.AddFloor, 0, 2, 2, 4, 4)), "Lay a 3x3 ground floor");
var ring = new List<WallSegment>();
for (int i = 2; i <= 4; i++)
{
    ring.Add(new(i, 2, WallAxis.AlongX, 0)); ring.Add(new(i, 5, WallAxis.AlongX, 0));   // south and north edges
    ring.Add(new(2, i, WallAxis.AlongZ, 0)); ring.Add(new(5, i, WallAxis.AlongZ, 0));   // west and east edges
}
Check(RoomFinder.Find(plan, 0).Count == 0, "A floor without walls is not a room");
plan = Must(BuildingEditor.Apply(plan, "owner", new(BuildEditKind.AddWall, 0, Walls: ring)), "Wall the floor in");
var rooms = RoomFinder.Find(plan, 0);
Check(rooms.Count == 1 && rooms[0].Tiles.Count == 9 && rooms[0].AreaSquareMeters == 144, "Enclosed 3x3 floor is one 144 mÂ² room");

// An interior wall line splits it into a 1x3 and a 2x3 room.
var divider = Enumerable.Range(2, 3).Select(z => new WallSegment(3, z, WallAxis.AlongZ, 0)).ToList();
plan = Must(BuildingEditor.Apply(plan, "owner", new(BuildEditKind.AddWall, 0, Walls: divider)), "Add an interior wall");
rooms = RoomFinder.Find(plan, 0);
Check(rooms.Count == 2 && rooms.Select(r => r.Tiles.Count).Order().SequenceEqual([3, 6]), "Interior wall makes two rooms of 3 and 6 tiles");
var withDoorway = BuildingEditor.Apply(plan, "owner", new(BuildEditKind.RemoveWall, 0, Walls: [divider[1]])).Plan;
Check(RoomFinder.Find(withDoorway, 0).Count == 1, "Opening one wall edge joins the two rooms");
var openSide = BuildingEditor.Apply(plan, "owner", new(BuildEditKind.RemoveWall, 0, Walls: [ring[0]])).Plan;
Check(RoomFinder.Find(openSide, 0).Count == 1, "Opening an outside wall leaves only the closed room");

// ---- structural support: a 3x3 base (effective area 9) supports 2 + 9/4 = 4 stories, i.e. levels 0..3
plan = Must(BuildingEditor.Apply(plan, "owner", new(BuildEditKind.AddFloor, 1, 2, 2, 4, 4)), "Second story on a 3x3 base");
plan = Must(BuildingEditor.Apply(plan, "owner", new(BuildEditKind.AddFloor, 3, 2, 2, 4, 4)), "Fourth story on a 3x3 base");
var tooHigh = BuildingEditor.Apply(plan, "owner", new(BuildEditKind.AddFloor, 4, 2, 2, 4, 4));
Check(tooHigh.ErrorCode == "insufficient_structural_support" && tooHigh.Plan == plan, "Fifth story on a 3x3 base is refused and changes nothing");
var floating = BuildingEditor.Apply(plan, "owner", new(BuildEditKind.AddFloor, 1, 7, 7, 7, 7));
Check(floating.ErrorCode == "insufficient_structural_support" && floating.Message.Contains("no connected ground-level floor"), "An upper floor over bare ground is refused with a clear reason");

// Removing the base under upper stories is refused; an unrelated edit next to a grandfathered violation is allowed.
var undermine = BuildingEditor.Apply(plan, "owner", new(BuildEditKind.RemoveFloor, 0, 2, 2, 3, 4));
Check(!undermine.Accepted, "Shrinking a foundation under four stories is refused");
var legacy = plan with { Floors = new HashSet<FloorCell>(plan.Floors) { new(8, 8, 3) } };
Check(BuildingEditor.Apply(legacy, "owner", new(BuildEditKind.AddFloor, 0, 0, 0, 0, 0)).Accepted, "An existing violation does not block unrelated edits");

// ---- roofs
// A roof counts on the story it covers: the top story here is level 3.
plan = Must(BuildingEditor.Apply(plan, "owner", new(BuildEditKind.AddRoof, 3, 2, 2, 4, 4, RoofId: "roof-a")), "Roof over the fourth story is supported");
Check(!BuildingEditor.Apply(plan, "owner", new(BuildEditKind.AddRoof, 4, 2, 2, 4, 4, RoofId: "roof-b")).Accepted, "A roof above the supported height is refused");
Check(BuildingEditor.Apply(plan, "owner", new(BuildEditKind.RemoveRoof, 3, RoofId: "roof-a")).Accepted, "A roof can be removed");

// ---- Tiny Clinic-style furniture: 0.5 m sub-cells, tile + sub-cell anchor, quarter turns, ghost preview
var furnished = BuildingPlan.Empty(lot);
furnished = Must(BuildingEditor.Apply(furnished, "owner", new(BuildEditKind.AddFloor, 0, 0, 0, 1, 0)), "Lay two floor tiles for furniture");
var bed = new FurnishingDefinition("exam-bed", "Exam bed", 4, 2);   // 2 m x 1 m
var chair = new FurnishingDefinition("chair", "Chair", 1, 1);
var placed = PlacedFurnishing.Of("bed-1", bed, 0, 0, 0, 2, 3, 0);
Check(placed.SubCells().Count() == 8 && placed.MinimumX == 2 && placed.MinimumZ == 3, "A 4x2 sub-cell bed covers eight 0.5 m cells from its tile + sub-cell anchor");
Check(PlacedFurnishing.Of("t", bed, 0, 0, 0, 0, 0, 1) is { SpanX: 2, SpanZ: 4 }, "A quarter turn swaps width and depth");
Check(FurnishingEditor.Preview(furnished, "owner", placed) is { IsValid: true, Status: "Place here." }, "Ghost preview says the bed may be set down");
furnished = Must(FurnishingEditor.Place(furnished, "owner", placed), "Set the bed down");
Check(FurnishingEditor.Preview(furnished, "owner", PlacedFurnishing.Of("chair-1", chair, 0, 0, 0, 3, 4, 0)).ErrorCode == "furnishing_overlap", "Ghost preview flags an overlap");
Check(FurnishingEditor.Preview(furnished, "owner", PlacedFurnishing.Of("chair-1", chair, 0, 0, 1, 0, 0, 0)).ErrorCode == "furnishing_requires_floor", "Ghost preview flags missing floor");
Check(FurnishingEditor.Preview(furnished, "stranger", PlacedFurnishing.Of("chair-1", chair, 0, 1, 0, 0, 0, 0)).ErrorCode == "not_owner", "Only the owner may furnish");
var straddle = PlacedFurnishing.Of("bed-2", bed, 0, 0, 0, 6, 5, 0);   // runs from tile 0 into tile 1
Check(FurnishingEditor.Preview(furnished, "owner", straddle).IsValid, "A piece may span two tiles with no wall between");
var walled = BuildingEditor.Apply(furnished, "owner", new(BuildEditKind.AddWall, 0, Walls: [new WallSegment(1, 0, WallAxis.AlongZ, 0)])).Plan;
Check(FurnishingEditor.Preview(walled, "owner", straddle).ErrorCode == "furnishing_crosses_wall", "A piece cannot straddle a wall");
var moved = placed with { TileX = 1, SubCellX = 0, YawQuarterTurns = 1 };
furnished = Must(FurnishingEditor.Move(furnished, "owner", moved), "Pick the bed up, turn it and set it on the other tile");
Check(!FurnishingEditor.Move(furnished, "owner", moved with { FootprintWidthSubCells = 1 }).Accepted, "A move cannot change the furnishing's size");
var undercut = BuildingEditor.Apply(furnished, "owner", new(BuildEditKind.RemoveFloor, 0, 1, 0, 1, 0));
Check(undercut.ErrorCode == "furnishing_in_the_way", "Removing the floor under furniture is refused");
var splitting = BuildingEditor.Apply(furnished with { Furnishings = [straddle] }, "owner", new(BuildEditKind.AddWall, 0, Walls: [new WallSegment(1, 0, WallAxis.AlongZ, 0)]));
Check(splitting.ErrorCode == "furnishing_in_the_way", "A wall through furniture is refused");
furnished = Must(FurnishingEditor.PickUp(furnished, "owner", "bed-1"), "Pick the bed back up into its package");
Check(furnished.Furnishings.Count == 0, "The lot is empty again");

// ---- client build mode: gestures go through a session to the authority, one edit at a time
{
    var authority = new InMemoryLotAuthority();
    var session = new LotSession(authority, "owner", authority.Deed(new LotDeed("lot-b", "owner", 10, 10, 19, 19)));
    var build = new LotBuildController(session);
    async Task Drag(LotBuildTool tool, float x0, float z0, float x1, float z1)
    { build.SelectTool(tool); build.SetPointer(x0, z0); build.BeginGesture(); build.SetPointer(x1, z1); build.CompleteGesture(); await session.SettleAsync(); }
    Check(!build.SelectFurnishing("chair") && build.Status.Contains("package"), "Build mode refuses furniture and points to the package");
    build.SetPointer(-30, -30); Check(!build.Preview.IsValid && build.Preview.Edit is not null, "The ghost is red off the lot");
    await Drag(LotBuildTool.Floor, 42, 42, 50, 50);   // tiles 10..12
    Check(session.Lot.Revision == 1 && session.Lot.Floors.Length == 9, "Dragging a floor lays a 3x3 rectangle (revision 1)");
    await Drag(LotBuildTool.Wall, 40, 40, 52, 40); await Drag(LotBuildTool.Wall, 40, 52, 52, 52);
    await Drag(LotBuildTool.Wall, 40, 40, 40, 52); await Drag(LotBuildTool.Wall, 52, 40, 52, 52);
    Check(session.Lot.Walls.Length == 12 && RoomFinder.Find(session.Lot.ToPlan(), 0).Count == 1, "Four wall drags along grid lines enclose one room");
    build.SelectTool(LotBuildTool.Wall); build.SetPointer(45, 43.9f); build.BeginGesture(); build.CompleteGesture(); await session.SettleAsync();
    Check(session.Lot.Walls.Contains(new WallSegment(11, 11, WallAxis.AlongX, 0)), "A wall click takes the nearest tile edge");
    await Drag(LotBuildTool.Roof, 41, 41, 51, 51);
    Check(session.Lot.Roofs.Length == 1 && session.Lot.Roofs[0] is { MinimumTileX: 10, MaximumTileX: 12 }, "Dragging a roof covers the room");
    build.SelectTool(LotBuildTool.Delete); build.SetPointer(45, 44.1f); build.BeginGesture(); build.CompleteGesture(); await session.SettleAsync();
    Check(!session.Lot.Walls.Contains(new WallSegment(11, 11, WallAxis.AlongX, 0)), "Delete takes the wall under the pointer first");
    long before = session.Lot.Revision;
    build.SelectTool(LotBuildTool.Floor); build.SetPointer(42, 42); build.BeginGesture(); build.CompleteGesture();
    build.BeginGesture(); Check(build.Status.Contains("Waiting"), "A second edit waits for the first to resolve");
    await session.SettleAsync(); Check(session.Lot.Revision == before + 1, "Re-laying an existing floor is still one accepted edit");
    var visitor = new LotSession(authority, "visitor", session.Lot); var visitorBuild = new LotBuildController(visitor);
    visitorBuild.BeginGesture(); Check(!visitorBuild.IsDragging, "A visitor cannot start a build gesture");
    var stale = new LotSession(authority, "owner", (await authority.GetAsync("lot-b"))! with { Revision = 1 });
    var staleBuild = new LotBuildController(stale); staleBuild.SelectTool(LotBuildTool.Floor); staleBuild.SetPointer(70, 70); staleBuild.BeginGesture(); staleBuild.CompleteGesture(); await stale.SettleAsync();
    Check(stale.Lot.Revision == session.Lot.Revision && stale.Status.Contains("changed"), "An edit from a stale view is refused and the view catches up");

    // ---- furniture: carried package, ghost, set down, move, turn, pick up
    var furnish = new LotFurnishingController(session);
    var examBed = FurnishingCatalog.Find("exam-bed")!.Definition;
    Check(furnish.BeginCarry(new("pkg-bed-1", examBed)), "Pick up the exam-bed package");
    furnish.SetPointer(-20, -20); Check(furnish.Preview is { IsValid: false }, "The ghost is red off the lot");
    furnish.SetPointer(46, 46); Check(furnish.Preview is { IsValid: true }, "The ghost is green on the floor");
    Check(furnish.Preview!.Candidate.MinimumX * .5f + furnish.Preview.Candidate.SpanX * .25f == 46, "The ghost is centred on the pointer on 0.5 m sub-cells");
    Check(furnish.SetDown(), "Set the bed down"); await session.SettleAsync();
    Check(session.Lot.Furnishings.Any(f => f.InstanceId == "pkg-bed-1"), "The package's id is the placed piece's id");
    Check(furnish.HitTest(46, 46, 0)?.InstanceId == "pkg-bed-1" && furnish.HitTest(30, 30, 0) is null, "Hit-testing finds the piece under the pointer");
    Check(furnish.BeginMove("pkg-bed-1"), "Start moving the bed"); furnish.Rotate(); furnish.SetPointer(49, 49);
    Check(furnish.Preview is { IsValid: true, Candidate.YawQuarterTurns: 1 }, "The moving ghost turns");
    furnish.SetDown(); await session.SettleAsync();
    Check(session.Lot.Furnishings.Single().YawQuarterTurns == 1, "The bed is moved and turned");
    furnish.RotateInPlace("pkg-bed-1"); await session.SettleAsync(); Check(session.Lot.Furnishings.Single().YawQuarterTurns == 2, "Rotate turns the bed in place");
    furnish.PickUp("pkg-bed-1"); await session.SettleAsync(); Check(session.Lot.Furnishings.Length == 0, "Pick Up packs the bed away");

    // ---- geometry every client and the host build alike
    var pieces = LotGeometry.Build(session.Lot, 100);
    Check(pieces.Count(p => p.Kind == LotPieceKind.Roof) == 9 && pieces.Select(p => p.PieceId).Distinct().Count() == pieces.Count, "Geometry: one piece per roof tile, every piece id unique");
    Check(pieces.Where(p => p.Kind == LotPieceKind.Roof).All(p => p.ReleaseWindMph < pieces.Where(w => w.Kind == LotPieceKind.Wall).Min(w => w.ReleaseWindMph)), "Roofs tear off before walls");
    Check(pieces.Where(p => p.Kind == LotPieceKind.Floor).All(p => float.IsPositiveInfinity(p.ReleaseWindMph) && MathF.Abs(p.CenterY - 100.1f) < .001f), "Ground floors sit on the lot and do not blow away");
    var wall = pieces.First(p => p.PieceId == "lot/lot-b/wall/0/x/10/10");
    Check(wall.CenterX == 42 && wall.CenterZ == 40 && wall.SizeX > 4, "An along-X wall spans its 4 m edge");
    Check(LotGeometry.Build(session.Lot, 100).SequenceEqual(pieces), "Geometry is deterministic for a revision");
}

// ---- the rural module pack: every module the construction system can ask for exists
{
    string? pack = ModuleMeshLibrary.Locate(AppContext.BaseDirectory);
    Check(pack is not null, "The rural module pack is found from the build output");
    var library = ModuleMeshLibrary.Load(pack!);
    var wanted = ModuleCatalog.WallStyles.SelectMany(s => ModuleCatalog.StraightOpenings.Select(o => ModuleCatalog.WallModule(s.Id, o.Id))
            .Append(ModuleCatalog.OuterCornerModule(s.Id)).Append(ModuleCatalog.GableInfillModule(s.Id)))
        .Concat(ModuleCatalog.FloorFinishes.Select(f => f.ModuleId))
        .Concat(ModuleCatalog.RoofFinishes.SelectMany(f => new[] { "roof_gable_mid_", "roof_gable_end_", "roof_hip_end_", "roof_hip_2x2_" }.Select(p => p + f.Id)))
        .Append("roof_flat_1x1").ToList();
    var missing = wanted.Where(id => !library.Has(id)).ToList();
    Check(missing.Count == 0, "Every catalog module is in the pack" + (missing.Count == 0 ? "" : ": missing " + string.Join(", ", missing.Take(5))));
    var door = library.Module("wall_lap_white_door_wood")!;
    Check(door.VertexCount > 100 && door.Positions.Min(p => p.Y) >= -.001f && MathF.Abs(door.Positions.Max(p => p.Y) - 3.2f) < .01f, "A wall module stands from the level base to the 3.2 m story top");
    Check(library.Module("wall_lap_white_win_shutter")!.Colors.Select(c => (c.X, c.Y, c.Z)).Distinct().Count() >= 4, "Module materials become vertex colours");
    var turned = library.Compose([new("wall_lap_white_full", 0, 0, 0, 1)])!;
    Check(turned.Positions.Max(p => p.Z) > 1.9f && turned.Positions.Max(p => MathF.Abs(p.X)) < .2f, "A quarter turn lays a wall along Z");
}

// ---- straight walls pick modules, face outward and get corner trims
{
    var deed = new LotDeed("lot-m", "owner", 0, 0, 9, 9);
    var p = Must(BuildingEditor.Apply(BuildingPlan.Empty(deed), "owner", new(BuildEditKind.AddFloor, 0, 1, 1, 2, 2, Finish: "tile")), "Lay a tiled 2x2 floor");
    var box = new List<WallSegment>();
    for (int i = 1; i <= 2; i++) { box.Add(new(i, 1, WallAxis.AlongX, 0)); box.Add(new(i, 3, WallAxis.AlongX, 0)); box.Add(new(1, i, WallAxis.AlongZ, 0)); box.Add(new(3, i, WallAxis.AlongZ, 0)); }
    box.Add(new(2, 1, WallAxis.AlongZ, 0));
    p = Must(BuildingEditor.Apply(p, "owner", new(BuildEditKind.AddWall, 0, Walls: box, Style: "stone")), "Wall it in with stone");
    p = Must(BuildingEditor.Apply(p, "owner", new(BuildEditKind.SetWallFinish, 0, Walls: [new(1, 1, WallAxis.AlongX, 0)], Opening: "door_wood")), "Put a door in the south wall");
    Check(BuildingEditor.Apply(p, "owner", new(BuildEditKind.SetWallFinish, 0, Walls: [new(1, 1, WallAxis.AlongX, 0)], Opening: "passage")).ErrorCode == "invalid_opening", "Straight walls refuse curved-only openings");
    Check(BuildingEditor.Apply(p, "owner", new(BuildEditKind.SetWallFinish, 0, Walls: [new(5, 5, WallAxis.AlongX, 0)], Opening: "door_wood")).ErrorCode == "unknown_wall", "Finishing a wall that is not there is refused");
    var doc = LotDocument.From(p, 3);
    var geo = LotGeometry.Build(doc, 0);
    LotPiece W(string id) => geo.First(x => x.PieceId == "lot/lot-m/wall/0/" + id);
    Check(W("x/1/1/left").Modules[0] is { ModuleId: "wall_stone_door_wood", QuarterTurns: 2 }, "The south wall's door module faces south (out of the room)");
    Check(W("x/1/3").Modules[0].QuarterTurns == 0 && W("z/1/1").Modules[0].QuarterTurns == 3 && W("z/3/1").Modules[0].QuarterTurns == 1, "North, west and east walls face out of the room");
    Check(W("z/2/1").Modules[0].ModuleId == "wall_stone_full", "A finished interior wall keeps its finish");
    Check(geo.SelectMany(x => x.Modules).Count(m => m.ModuleId == "corner_stone_outer") == 4, "A rectangular building gets four outer corner trims");
    Check(geo.Where(x => x.Kind == LotPieceKind.Floor).All(x => x.Modules[0].ModuleId == "floor_tile"), "Floors draw their finish");
    var bare = LotGeometry.Build(LotDocument.From(BuildingEditor.Apply(p, "owner", new(BuildEditKind.AddWall, 0, Walls: [new(1, 2, WallAxis.AlongX, 0)])).Plan, 4), 0);
    Check(bare.First(x => x.PieceId == "lot/lot-m/wall/0/x/1/2").Modules[0].ModuleId == "wall_plaster_full", "An unfinished wall between two rooms is interior plaster");
    var cleared = BuildingEditor.Apply(p, "owner", new(BuildEditKind.RemoveWall, 0, Walls: [new(1, 1, WallAxis.AlongX, 0)])).Plan;
    Check(cleared.WallFinishes.All(f => f.Wall != new WallSegment(1, 1, WallAxis.AlongX, 0)), "Removing a wall drops its finish");

    // Roofs: gable ends with infill matching the wall below, hip 2x2, and the 2-tile span rule.
    p = Must(BuildingEditor.Apply(p, "owner", new(BuildEditKind.AddRoof, 0, 1, 1, 2, 2, RoofId: "g", Shape: "gable", Finish: "metal_green")), "Gable roof over the 2x2");
    var roofs = LotGeometry.Build(LotDocument.From(p, 5), 0).Where(x => x.Kind == LotPieceKind.Roof).ToList();
    Check(roofs.Count == 2 && roofs.All(r => r.Modules[0].ModuleId == "roof_gable_end_metal_green") && roofs.All(r => r.Modules.Any(m => m.ModuleId == "gable_infill_stone")),
        "A 2x2 gable is two end pieces, each with stone gable infill");
    Check(roofs.Select(r => r.Modules[0].QuarterTurns).Order().SequenceEqual([0, 2]), "The two gable ends face opposite ways");
    var hip = BuildingEditor.Apply(p with { Roofs = [] }, "owner", new(BuildEditKind.AddRoof, 0, 1, 1, 2, 2, RoofId: "h", Shape: "hip")).Plan;
    Check(LotGeometry.Build(LotDocument.From(hip, 6), 0).Single(x => x.Kind == LotPieceKind.Roof).Modules[0].ModuleId == "roof_hip_2x2_shingle", "A 2x2 hip roof is one hip module");
    Check(BuildingEditor.Apply(BuildingPlan.Empty(deed), "owner", new(BuildEditKind.AddRoof, 0, 0, 0, 2, 2, RoofId: "x", Shape: "gable")).ErrorCode == "pitched_roof_span", "Pitched roofs must span exactly two tiles");
    var longGable = BuildingEditor.Apply(BuildingPlan.Empty(deed), "owner", new(BuildEditKind.AddRoof, 0, 0, 0, 1, 3, RoofId: "l", Shape: "gable")).Plan;
    var longPieces = LotGeometry.Build(LotDocument.From(longGable, 1), 0).ToList();
    Check(longPieces.Count == 4 && longPieces.Count(x => x.Modules[0].ModuleId.StartsWith("roof_gable_mid")) == 2 && longPieces.All(x => x.SizeZ < 5),
        "A gable 2 wide and 4 long runs its ridge along Z: two ends and two middles");
}

// ---- true curved walls: full rings, arcs and curved counters
{
    var deed = new LotDeed("lot-c", "owner", 0, 0, 7, 7);
    var p = Must(BuildingEditor.Apply(BuildingPlan.Empty(deed), "owner", new(BuildEditKind.AddFloor, 0, 2, 2, 5, 5)), "Lay a 4x4 floor for a round room");
    var roundWall = new CurvedWall("ring", 0, 16_000, 16_000, 5_000, 180_000, CurvedWall.FullCircleMilliDegrees, "lap_white", "full", true,
        [new("door_wood", 90_000), new("win_large", 270_000), new("passage", 180_000)]);   // door due -Z, window due +Z, passage due +X; seam at -X
    p = Must(BuildingEditor.Apply(p, "owner", new(BuildEditKind.AddCurvedWall, 0, Curve: roundWall)), "Build a 5 m round room with a door and a window");
    var roundRooms = RoundRoomFinder.Find(p, 0);
    Check(roundRooms.Count == 1 && roundRooms[0].Floored && roundRooms[0].Entrances == 2 && MathF.Abs(roundRooms[0].AreaSquareMeters - MathF.PI * 4.9f * 4.9f) < .01f,
        "A full ring with floor inside is a round room of π·r² with its two entrances");
    var pieces = LotGeometry.Build(LotDocument.From(p, 2), 10).Where(x => x.Curve is not null).ToList();
    Check(pieces.Count >= 14 && pieces.All(x => x.Kind == LotPieceKind.Wall) && pieces.Select(x => x.PieceId).Distinct().Count() == pieces.Count, "The ring breaks into curved pieces with unique ids");
    float arc = pieces.Sum(x => (float)((x.Curve!.To - x.Curve.From) * roundWall.Radius));
    Check(MathF.Abs(arc - 2 * MathF.PI * roundWall.Radius) < .001f, "The pieces cover the whole circumference with no gaps");

    // The mesh is a true curve: outer-face vertices lie on the circle and carry radial (smooth) normals.
    var segment = pieces[0].Curve!;
    var world = CurvedWallMesher.Build(roundWall, segment.From, segment.To, 10);
    float outer = roundWall.Radius + CurvedWalls.Thickness / 2;
    var onFace = Enumerable.Range(0, world.VertexCount).Where(i =>
    {
        var v = world.Positions[i]; float d = MathF.Sqrt((v.X - 16) * (v.X - 16) + (v.Z - 16) * (v.Z - 16));
        return MathF.Abs(d - (roundWall.Radius - CurvedWalls.Thickness / 2)) < .001f;
    }).ToList();
    System.Numerics.Vector3 Out(int i) { var v = world.Positions[i]; return System.Numerics.Vector3.Normalize(new(v.X - 16, 0, v.Z - 16)); }
    var sideways = onFace.Where(i => MathF.Abs(System.Numerics.Vector3.Dot(world.Normals[i], Out(i))) > .5f).ToList();   // the face itself, not caps or rings
    Check(sideways.Count > 20 && sideways.All(i => System.Numerics.Vector3.Dot(world.Normals[i], -Out(i)) > .999f),
        "Inner-face vertices sit exactly on the arc with radial normals (no flat panels)");
    var angles = onFace.Select(i => Math.Round(((Math.Atan2(world.Positions[i].Z - 16, world.Positions[i].X - 16) * 180 / Math.PI - roundWall.StartMilliDegrees / 1000.0) % 360 + 360) % 360, 3)).Distinct().Order().ToList();
    Check(angles.Zip(angles.Skip(1), (a, b) => b - a).All(step => step <= 3.0001), "The arc is sampled at least every 3°");
    Check(world.Indices.Count % 3 == 0 && world.Indices.All(i => i >= 0 && i < world.VertexCount), "The curved mesh is valid triangles");
    var local = CurvedWallMesher.ForPiece(pieces[0]);
    var back = System.Numerics.Matrix4x4.CreateRotationY(pieces[0].YawDegrees * MathF.PI / 180);
    var p0 = System.Numerics.Vector3.Transform(local.Positions[0], back) + new System.Numerics.Vector3(pieces[0].CenterX, pieces[0].CenterY, pieces[0].CenterZ);
    Check(System.Numerics.Vector3.Distance(p0, world.Positions[0]) < .001f, "A piece's local mesh, turned by its yaw, lands back on the arc");
    var withDoor = pieces.Single(x => { var r = CurvedWalls.Range(roundWall, roundWall.HostedOpenings[0]); return x.Curve!.From <= r.From && x.Curve.To >= r.To; });
    Check(CurvedWallMesher.ForPiece(withDoor).VertexCount > 0, "The door sits whole inside one piece");

    // Furniture cannot straddle the curved wall, but can stand inside the room or in the doorway.
    var chairDef = FurnishingCatalog.Find("chair")!.Definition;
    Check(!FurnishingEditor.Place(p, "owner", PlacedFurnishing.Of("c1", chairDef, 0, 2, 3, 6, 7, 0)).Accepted, "A chair on the ring's wall line is refused");
    Check(FurnishingEditor.Place(p, "owner", PlacedFurnishing.Of("c2", chairDef, 0, 4, 4, 0, 0, 0)).Accepted, "A chair in the round room is fine");
    Check(FurnishingEditor.Place(p, "owner", PlacedFurnishing.Of("c3", chairDef, 0, 5, 3, 1, 7, 0)).Accepted, "A chair in the open passage clears the ring");
    var withChair = Must(FurnishingEditor.Place(p, "owner", PlacedFurnishing.Of("c4", chairDef, 0, 4, 4, 0, 0, 0)), "Set a chair inside");
    Check(BuildingEditor.Apply(withChair, "owner", new(BuildEditKind.SetCurvedWall, 0, Curve: roundWall with { RadiusMillimeters = 1_500 })).ErrorCode is "furnishing_in_the_way" or "opening_too_curved", "Shrinking the ring onto furniture is refused");

    // Rules.
    CurvedWall C(string id, int r, int sweep, string profile = "full", CurvedOpening[]? o = null) => new(id, 0, 16_000, 16_000, r, 0, sweep, "stucco", profile, true, o);
    Check(CurvedWalls.Problem(C("a", 1_000, 360_000), deed)?.Code == "invalid_radius", "Radius below 1.5 m is refused");
    Check(CurvedWalls.Problem(C("a", 20_000, 360_000), deed)?.Code == "outside_lot", "A ring reaching off the lot is refused");
    Check(CurvedWalls.Problem(C("a", 3_025, 360_000), deed) is null, "Curves are stored to the millimetre, so they can start exactly on a corner");
    Check(CurvedWalls.Problem(C("a", 3_000, 10_000), deed)?.Code == "invalid_sweep", "Arcs sweep at least 15°");
    Check(CurvedWalls.Problem(C("a", 1_500, 360_000, o: [new("win_wide", 90_000)]), deed)?.Code == "opening_too_curved", "A wide window is refused on a tight curve");
    Check(CurvedWalls.Problem(C("a", 3_000, 360_000, o: [new("door_wood", 90_000), new("win_small", 100_000)]), deed)?.Code == "openings_overlap", "Overlapping openings are refused");
    Check(CurvedWalls.Problem(C("a", 3_000, 180_000, o: [new("door_wood", 2_000)]), deed)?.Code == "opening_off_wall", "An opening hanging off an arc's end is refused");

    // Nurses' stations: a 270° counter (the gap is the staff entrance) or a full counter roundWall with a passage.
    var station = C("station", 2_500, 270_000, "counter");
    Check(CurvedWalls.Problem(station, deed) is null, "A 270° horseshoe nurses' station counter is valid");
    Check(CurvedWalls.Problem(station with { Openings = [new("door_wood", 135_000)] }, deed)?.Code == "invalid_opening", "Counters take passages, not doors");
    Check(CurvedWalls.Problem(C("ring-counter", 2_500, 360_000, "counter", [new("passage", 180_000)]), deed) is null, "A full counter ring with a staff passage is valid");
    var counterMesh = CurvedWallMesher.Build(station, 0, station.SweepRadians, 0);
    Check(MathF.Abs(counterMesh.Positions.Max(v => v.Y) - (CurvedWalls.ProfileTop("counter") + .04f)) < .001f, "The counter's worktop sits at counter height");
    Check(counterMesh.Positions.Min(v => MathF.Sqrt((v.X - 16) * (v.X - 16) + (v.Z - 16) * (v.Z - 16))) < 2.5f - .5f, "The worktop reaches into the staff side");
    Check(CurvedWalls.Problem(C("glass", 3_000, 180_000, "glazed"), deed) is null && CurvedWallMesher.Build(C("glass", 3_000, 180_000, "glazed"), 0, Math.PI, 0)
        .Colors.Any(c => MathF.Abs(c.X - ModuleCatalog.Color("glass").R) < .001f), "A glazed curved wall carries curved glass");
    Check(BuildingEditor.Apply(p, "owner", new(BuildEditKind.AddCurvedWall, 0, Curve: roundWall)).ErrorCode == "invalid_curved_wall", "Curved wall ids are unique");
    Check(BuildingEditor.Apply(p, "owner", new(BuildEditKind.AddCurvedWall, 1, Curve: station with { Level = 1, CenterXMillimeters = 4_000, CenterZMillimeters = 4_000 })).ErrorCode == "insufficient_structural_support",
        "An upper-level curved wall needs a foundation");
    var removed = Must(BuildingEditor.Apply(p, "owner", new(BuildEditKind.RemoveCurvedWall, 0, TargetId: "ring")), "Remove the ring");
    Check(removed.CurvedWalls.Count == 0 && RoundRoomFinder.Find(removed, 0).Count == 0, "Removing the ring removes the round room");

    // Save and reload: the arc round-trips exactly, and lot files from before curved walls still load.
    var options = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
    var saved = System.Text.Json.JsonSerializer.Serialize(LotDocument.From(p, 9), options);
    var loaded = System.Text.Json.JsonSerializer.Deserialize<LotDocument>(saved, options)!;
    Check(loaded.CurvedWalls.Single().Fingerprint == roundWall.Fingerprint && LotGeometry.Build(loaded, 0).SequenceEqual(LotGeometry.Build(LotDocument.From(p, 9), 0)),
        "A saved round room reloads with identical arc and geometry");
    var legacyJson = """{"lot":{"lotId":"old","ownerCharacterId":"o","minimumTileX":0,"minimumTileZ":0,"maximumTileX":3,"maximumTileZ":3},"revision":4,"floors":[{"tileX":0,"tileZ":0,"level":0}],"walls":[],"roofs":[{"roofId":"r","minimumTileX":0,"minimumTileZ":0,"maximumTileX":0,"maximumTileZ":0,"level":0}],"furnishings":[]}""";
    var legacyDoc = System.Text.Json.JsonSerializer.Deserialize<LotDocument>(legacyJson, options)!;
    Check(legacyDoc.CurvedWalls.Length == 0 && legacyDoc.ToPlan().Roofs[0].Shape == "flat" && LotGeometry.Build(legacyDoc, 0).Count == 2, "A lot saved before modules and curves loads with defaults");
}

// ---- Sims-style build mode: room tool, paints, carpets, half walls and the corner-drag curve
{
    var authority = new InMemoryLotAuthority();
    var session = new LotSession(authority, "owner", authority.Deed(new LotDeed("lot-s", "owner", 0, 0, 9, 9)));
    var build = new LotBuildController(session);
    async Task Settle() { while (session.HasPendingEdit) await session.SettleAsync(); }
    async Task Gesture(float x0, float z0, float x1, float z1) { build.SetPointer(x0, z0); build.BeginGesture(); build.SetPointer(x1, z1); build.CompleteGesture(); await Settle(); }

    // Room: one drag lays the floor and walls it in, as two edits sent in turn.
    build.Choose(LotBuildTool.Room, s => s with { WallStyle = "stucco", FloorFinish = "carpet", FloorPaint = "carpet_blue" });
    await Gesture(5, 5, 11, 11);   // tiles 1..2
    var roomLot = session.Lot;
    Check(roomLot.Floors.Length == 4 && roomLot.Walls.Length == 8 && RoomFinder.Find(roomLot.ToPlan(), 0).Count == 1, "The room tool lays a 2x2 floor and its 8 walls");
    Check(roomLot.FloorFinishes.All(f => f is { Finish: "carpet", Paint: "carpet_blue" }) && roomLot.WallFinishes.All(f => f.Style == "stucco"), "The room uses the chosen carpet and wall style");
    var library = ModuleMeshLibrary.Load(ModuleMeshLibrary.Locate(AppContext.BaseDirectory)!);
    var floorPiece = LotGeometry.Build(roomLot, 0).First(p => p.Kind == LotPieceKind.Floor);
    var carpet = library.Compose(floorPiece.Modules)!; var blue = ModuleCatalog.PaintColor("carpet_blue")!.Value;
    Check(floorPiece.Modules[0].ModuleId == "floor_concrete" && carpet.Colors.Any(c => MathF.Abs(c.X - blue.R) < 1e-4f && MathF.Abs(c.Z - blue.B) < 1e-4f), "Carpet draws as a blue floor surface");

    // Paint: clicking from inside paints the inside face only; Shift paints every wall of the room.
    build.Choose(LotBuildTool.PaintWall, s => s with { WallPaint = "mint" });
    await Gesture(6, 4.3f, 6, 4.3f);   // just inside the south wall (z = 4)
    var south = session.Lot.WallFinishes.Single(f => f.Wall == new WallSegment(1, 1, WallAxis.AlongX, 0));
    Check(south is { InsidePaint: "mint", OutsidePaint: null }, "Clicking a wall from inside the room paints its inside face");
    build.PaintWholeRoom = true;
    build.Choose(LotBuildTool.PaintWall, s => s with { WallPaint = "lavender" });
    await Gesture(11.7f, 6, 11.7f, 6);   // inside, next to the east wall
    build.PaintWholeRoom = false;
    Check(session.Lot.WallFinishes.Count(f => f.InsidePaint == "lavender") == 8 && session.Lot.WallFinishes.All(f => f.OutsidePaint is null), "Shift paints the inside of every wall of the room");
    await Gesture(6, 3.7f, 6, 3.7f);   // just outside the south wall, still lavender
    Check(session.Lot.WallFinishes.Single(f => f.Wall == new WallSegment(1, 1, WallAxis.AlongX, 0)) is { InsidePaint: "lavender", OutsidePaint: "lavender" }, "Clicking from outside paints the outside face");
    var southPiece = LotGeometry.Build(session.Lot, 0).First(p => p.PieceId == "lot/lot-s/wall/0/x/1/1");
    var painted = library.Compose(southPiece.Modules)!; var lavender = ModuleCatalog.PaintColor("lavender")!.Value;
    Check(painted.Colors.Count(c => MathF.Abs(c.X - lavender.R) < 1e-4f) > painted.VertexCount / 4, "The painted wall module carries the paint on its faces");

    // Doors and windows go into existing walls; half walls stand without closing a room.
    build.Choose(LotBuildTool.Fitting, s => s with { Fitting = "door_halfglass" });
    await Gesture(6, 4.3f, 6, 4.3f);
    Check(session.Lot.WallFinishes.Single(f => f.Wall == new WallSegment(1, 1, WallAxis.AlongX, 0)).Opening == "door_halfglass", "The fitting tool sets a door into the wall");
    build.Choose(LotBuildTool.Wall, s => s with { WallKind = "half" });
    await Gesture(12, 4, 12, 12);   // a half wall run beside the room
    Check(session.Lot.WallFinishes.Count(f => f.Opening == "half") == 2, "Half walls are built from the half-wall modules");
    var openPlan = BuildingEditor.Apply(session.Lot.ToPlan(), "owner", new(BuildEditKind.SetWallFinish, 0, Walls: [new(3, 1, WallAxis.AlongZ, 0)], Opening: "half")).Plan;
    Check(RoomFinder.Find(openPlan, 0).Count == 0, "Turning a room's wall into a half wall opens the room");

    // Curved wall: press on the corner, drag to the half point, release, carry it round, then finish.
    build.Choose(LotBuildTool.CurvedWall, s => s with { CurvedProfile = "counter" });
    build.SetPointer(20.2f, 20.3f); build.BeginGesture();          // snaps to the corner (20, 20)
    build.SetPointer(26, 20);
    Check(build.Preview.Edit?.Curve is { SweepMilliDegrees: 180_000 } half && MathF.Abs(half.Radius - 3) < 1e-3f && MathF.Abs(half.CenterX - 23) < 1e-3f,
        "Dragging from the corner previews a half circle through the half point");
    build.CompleteGesture();
    Check(build.CurveExtending && !session.HasPendingEdit, "Releasing locks the half circle and waits for the sweep");
    build.SetPointer(23, 17.5f);                                     // a quarter further round
    var arc = build.Preview.Edit!.Curve!;
    Check(arc.SweepMilliDegrees == 270_000, "Moving round from the half point carries the curve to 270°");
    var (sx, sz) = CurvedWalls.Point(arc, arc.StartRadians, arc.Radius); var (ex, ez) = CurvedWalls.Point(arc, arc.StartRadians + arc.SweepRadians, arc.Radius);
    Check(new[] { (sx, sz), (ex, ez) }.Any(p => MathF.Abs(p.Item1 - 20) < .01f && MathF.Abs(p.Item2 - 20) < .01f), "The curve still starts exactly on the corner");
    build.BeginGesture(); build.CompleteGesture(); await Settle();
    Check(session.Lot.CurvedWalls.Single() is { Profile: "counter", SweepMilliDegrees: 270_000 }, "A click finishes a 270° nurses' station counter");
    build.SetPointer(30, 20); build.BeginGesture(); build.SetPointer(30, 28); build.CompleteGesture();
    Check(build.FinishCurve(true), "F closes the curve into a full circle");
    await Settle();
    Check(session.Lot.CurvedWalls.Any(c => c.IsFullCircle && MathF.Abs(c.Radius - 4) < 1e-3f), "The full circle has the dragged diameter");
    build.SetPointer(10, 30); build.BeginGesture(); build.SetPointer(10, 31); build.CompleteGesture();
    Check(!build.CurveExtending && build.Status.Contains("3 m"), "A curve shorter than the minimum diameter is refused with the reason");
}

// ---- doors: server-held state, a clear doorway, and leaves that swing, lift or slide out of the way
{
    var deed = new LotDeed("lot-d", "owner", 0, 0, 9, 9);
    var p = Must(BuildingEditor.Apply(BuildingPlan.Empty(deed), "owner", new(BuildEditKind.AddFloor, 0, 1, 1, 2, 2)), "Floor for the door tests");
    var box = new List<WallSegment>();
    for (int i = 1; i <= 2; i++) { box.Add(new(i, 1, WallAxis.AlongX, 0)); box.Add(new(i, 3, WallAxis.AlongX, 0)); box.Add(new(1, i, WallAxis.AlongZ, 0)); box.Add(new(3, i, WallAxis.AlongZ, 0)); }
    p = Must(BuildingEditor.Apply(p, "owner", new(BuildEditKind.AddWall, 0, Walls: box)), "Walls for the door tests");
    var south = new WallSegment(1, 1, WallAxis.AlongX, 0); var east = new WallSegment(3, 1, WallAxis.AlongZ, 0); var north = new WallSegment(2, 3, WallAxis.AlongX, 0);
    p = Must(BuildingEditor.Apply(p, "owner", new(BuildEditKind.SetWallFinish, 0, Walls: [south], Opening: "door_wood")), "Fit a front door");
    p = Must(BuildingEditor.Apply(p, "owner", new(BuildEditKind.SetWallFinish, 0, Walls: [east], Opening: "door_garage")), "Fit a garage door");
    p = Must(BuildingEditor.Apply(p, "owner", new(BuildEditKind.SetWallFinish, 0, Walls: [north], Opening: "door_barn")), "Fit a barn door");
    var doors = LotDoors.All(p).ToList();
    Check(doors.Count == 3 && doors.Single(d => d.DoorId == LotDoors.StraightId(south)) is { CenterX: 6, CenterZ: 4, WidthMeters: .9f }, "The lot lists its three doors where they are");

    bool Blocks(IEnumerable<LotPiece> pieces, float x, float y, float z) => pieces.Any(q => MathF.Abs(x - q.CenterX) < q.SizeX / 2 && MathF.Abs(y - q.CenterY) < q.SizeY / 2 && MathF.Abs(z - q.CenterZ) < q.SizeZ / 2);
    var closed = LotGeometry.Build(LotDocument.From(p, 1), 0);
    Check(closed.Count(q => q.PieceId.StartsWith("lot/lot-d/wall/0/x/1/1/", StringComparison.Ordinal)) == 4, "A door wall is two jambs, a lintel and a leaf");
    Check(Blocks(closed, 6, 1.2f, 3.96f) && !Blocks(closed.Where(q => q.Door is null), 6, 1.2f, 3.96f), "Closed, only the leaf fills the doorway");
    Check(Blocks(closed, 6, 3.0f, 4) && Blocks(closed, 7.5f, 1.2f, 4), "The lintel and the wall beside the door stay solid");

    // Server-held state: anyone signed in may set it; the state is explicit, so repeating it changes nothing.
    var opened = LotDoors.SetOpen(p, "visitor", LotDoors.StraightId(south), true);
    Check(opened.Accepted && opened.Plan.OpenDoors.SequenceEqual([LotDoors.StraightId(south)]), "A visitor can open the door (it is an interaction, not an edit)");
    Check(LotDoors.SetOpen(opened.Plan, "visitor", LotDoors.StraightId(south), true).Plan.OpenDoors.Count == 1, "Opening an open door changes nothing");
    Check(LotDoors.SetOpen(p, "visitor", "wall/0/x/9/9", true).ErrorCode == "door_not_found", "A door that is not there cannot be opened");
    p = LotDoors.SetOpen(LotDoors.SetOpen(opened.Plan, "owner", LotDoors.StraightId(east), true).Plan, "owner", LotDoors.StraightId(north), true).Plan;
    var open = LotGeometry.Build(LotDocument.From(p, 2), 0);
    Check(!Blocks(open, 6, 1.2f, 4), "Open, the front doorway is clear to walk through");
    var frontLeaf = open.Single(q => q.PieceId == "lot/lot-d/wall/0/x/1/1/leaf0");
    Check(frontLeaf.CenterZ > 4.1f && frontLeaf.SizeZ > .8f && frontLeaf.Modules[0].QuarterTurns == 3, "The front door swings a quarter turn into the room");
    Check(!Blocks(open, 12, 1.2f, 6) && open.Single(q => q.PieceId.EndsWith("z/3/1/leaf0", StringComparison.Ordinal)) is { CenterY: > 2.5f, Modules: [{ ModuleId: "door_leaf_garage_open" }] },
        "The garage door rolls up overhead, clearing the opening");
    Check(!Blocks(open, 10, 1.2f, 12) && open.Where(q => q.PieceId.Contains("x/2/3/leaf", StringComparison.Ordinal)).All(q => MathF.Abs(q.CenterX - 10) > 1.5f), "The barn doors slide apart");
    var removedDoor = Must(BuildingEditor.Apply(p, "owner", new(BuildEditKind.SetWallFinish, 0, Walls: [south], Opening: ModuleCatalog.NoOpening)), "Wall the front door up");
    Check(!removedDoor.OpenDoors.Contains(LotDoors.StraightId(south)) && removedDoor.OpenDoors.Count == 2, "A removed door is no longer open");

    // The jamb pieces draw only their own slice of the wall module.
    var doorLibrary = ModuleMeshLibrary.Load(ModuleMeshLibrary.Locate(AppContext.BaseDirectory)!);
    var leftJamb = open.Single(q => q.PieceId == "lot/lot-d/wall/0/x/1/1/left");
    var jambMesh = doorLibrary.Compose(leftJamb.Modules)!;
    var wholeWall = doorLibrary.Compose([leftJamb.Modules[0] with { ClipFrom = float.NegativeInfinity, ClipTo = float.PositiveInfinity }])!;
    Check(jambMesh.Indices.Count < wholeWall.Indices.Count * .7 && jambMesh.Indices.Count > 0, "A jamb piece draws its slice of the wall, not the whole wall");
    Check(doorLibrary.Compose(frontLeaf.Modules) is { VertexCount: > 50 }, "Door leaves come from the pack's leaf modules");

    // Interaction reach and aiming, as in Life Beyond: within one tile, the door the crosshair is on.
    var front = LotDoors.All(p).Single(d => d.DoorId == LotDoors.StraightId(south));
    Check(LotDoors.InReach(front, new(6, 0, 2.5f), 0) && !LotDoors.InReach(front, new(6, 0, -1), 0) && !LotDoors.InReach(front, new(6, 6, 2.5f), 0), "Doors are used from within one tile, on their own floor");
    Check(LotDoors.Hit(LotDoors.All(p), 0, new(6, 1.7f, 1.5f), System.Numerics.Vector3.UnitZ)?.Door.DoorId == front.DoorId, "Aiming at the doorway picks that door");
    Check(LotDoors.Hit(LotDoors.All(p), 0, new(6, 1.7f, 1.5f), -System.Numerics.Vector3.UnitZ) is null, "Looking away picks no door");
    var doorWheel = InteractionPieMenu.ForDoor("lot-d|" + front.DoorId, front.Name, isOpen: true, 640, 360, 1280, 720);
    Check(doorWheel.Actions.Select(a => a.Label).SequenceEqual(["Close", "Inspect"]), "An open door's wheel offers Close and Inspect");

    // A door in a curved wall: the doorway clear, and a straight leaf that opens into the round room.
    var doorRing = new CurvedWall("ring", 0, 16_000, 16_000, 5_000, 180_000, CurvedWall.FullCircleMilliDegrees, "stucco", "full", true, [new("door_wood", 90_000)]);
    var rp = Must(BuildingEditor.Apply(BuildingPlan.Empty(deed), "owner", new(BuildEditKind.AddCurvedWall, 0, Curve: doorRing)), "A round room with a door");
    string ringDoor = LotDoors.All(rp).Single().DoorId;
    var shut = LotGeometry.Build(LotDocument.From(rp, 1), 0);
    var shutLeaf = shut.Single(q => q.Door is not null);
    Check(Blocks(shut, 16, 1.2f, 11) && !Blocks(shut.Where(q => q.Door is null), 16, 1.2f, 11), "The curved doorway is clear except for its closed leaf");
    var ajar = LotGeometry.Build(LotDocument.From(LotDoors.SetOpen(rp, "visitor", ringDoor, true).Plan, 2), 0);
    var ajarLeaf = ajar.Single(q => q.Door is not null);
    Check(!Blocks(ajar, 16, 1.2f, 11) && MathF.Sqrt((ajarLeaf.CenterX - 16) * (ajarLeaf.CenterX - 16) + (ajarLeaf.CenterZ - 16) * (ajarLeaf.CenterZ - 16)) < 4.8f,
        "Opened, the curved door's leaf swings into the round room and the doorway clears");
    Check(DoorLeafMesher.ForPiece(ajarLeaf).VertexCount > 0 && shutLeaf.MeshKey != ajarLeaf.MeshKey, "The curved leaf has its own mesh in each state");
}

// ---- the construction client stays signed in: a 401 refreshes the 15-minute access token and retries once
{
    var seen = new List<(string? Token, string Body)>();
    var lotDoc = LotDocument.From(BuildingPlan.Empty(new LotDeed("lot-t", "owner", 0, 0, 3, 3)), 1);
    var handler = new StubHandler(async request =>
    {
        string body = request.Content is null ? "" : await request.Content.ReadAsStringAsync();
        seen.Add((request.Headers.Authorization?.Parameter, body));
        if (request.Headers.Authorization?.Parameter != "fresh") return new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.Unauthorized);
        return new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
            { Content = System.Net.Http.Json.JsonContent.Create(new ChangeResult(true, lotDoc), options: ConstructionClient.Json) };
    });
    int refreshes = 0;
    using var client = new ConstructionClient("http://gateway", "expired", handler, async (rejected, _) => { refreshes++; await Task.Yield(); return rejected == "expired" ? "fresh" : null; });
    var reply = await client.BuildAsync("lot-t", new("owner", new(BuildEditKind.AddFloor, 0, 0, 0, 0, 0)));
    Check(reply.Accepted && refreshes == 1 && seen.Count == 2 && seen[0].Token == "expired" && seen[1].Token == "fresh" && seen[0].Body == seen[1].Body && seen[1].Body.Contains("AddFloor"),
        "An expired token is refreshed once and the same edit is retried");
    await client.BuildAsync("lot-t", new("owner", new(BuildEditKind.AddFloor, 0, 0, 0, 0, 0)));
    Check(refreshes == 1 && seen[^1].Token == "fresh", "Later requests keep the fresh token");
}

// ---- the pie wheel (Tiny Clinic's furnishing menu)
{
    var piece = PlacedFurnishing.Of("chair-1", FurnishingCatalog.Find("chair")!.Definition, 0, 0, 0, 0, 0, 0);
    var menu = InteractionPieMenu.ForFurnishing(piece, "Chair", true, 5, 5, 1280, 720);
    Check(menu.X == 8 && menu.Y == 8, "The wheel stays on screen near the corner");
    Check(menu.Actions.Select(a => a.Label).SequenceEqual(["Arrange", "Inspect"]), "The owner's wheel offers Arrange and Inspect");
    var arrange = menu.Slot(0);
    Check(menu.Click(arrange.X + 5, arrange.Y + 5, out bool closed) is null && !closed && menu.Category == "ARRANGE" && menu.CanNavigateBack, "A branch opens in place");
    var pick = menu.Click(menu.Slot(2).X + 5, menu.Slot(2).Y + 5, out closed);
    Check(pick?.ActionId == InteractionPieMenu.PickUpAction && pick.TargetId == "chair-1" && closed, "Choosing Pick Up returns its invocation and closes");
    var again = InteractionPieMenu.ForFurnishing(piece, "Chair", true, 640, 360, 1280, 720); again.SelectAction(0);
    again.Click(again.Center.X + 50, again.Center.Y + 50, out closed); Check(!closed && !again.CanNavigateBack, "The centre goes back from a branch");
    again.Click(again.Center.X + 50, again.Center.Y + 50, out closed); Check(closed, "The centre closes at the root");
    Check(InteractionPieMenu.ForFurnishing(piece, "Chair", false, 640, 360, 1280, 720).Actions.Single().Label == "Inspect", "Visitors only inspect");
    var many = InteractionPieMenu.Normalize(Enumerable.Range(1, 13).Select(i => PieMenuAction.Leaf("A" + i, "", "a" + i)));
    Check(many.Count == 6 && many[5].Label == "More Actions" && many[5].Children.Count == 6 && many[5].Children[5].Children.Count == 3, "Long lists page five at a time behind More Actions");
}

Console.WriteLine($"{passed}/{passed + failed} construction checks passed");
return failed == 0 ? 0 : 1;

sealed class StubHandler(Func<System.Net.Http.HttpRequestMessage, Task<System.Net.Http.HttpResponseMessage>> reply) : System.Net.Http.HttpMessageHandler
{
    protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, CancellationToken cancellationToken) => reply(request);
}
