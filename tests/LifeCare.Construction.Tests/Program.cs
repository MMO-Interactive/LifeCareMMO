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
