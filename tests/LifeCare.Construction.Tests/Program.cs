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
    Check(door.Colors.Select(c => (c.X, c.Y, c.Z)).Distinct().Count() >= 4, "Module materials become vertex colours");
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
    Check(W("x/1/1").Modules[0] is { ModuleId: "wall_stone_door_wood", QuarterTurns: 2 }, "The south wall's door module faces south (out of the room)");
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
    Check(CurvedWalls.Problem(C("a", 3_025, 360_000), deed)?.Code == "unquantized_geometry", "Curves snap to 5 cm");
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
