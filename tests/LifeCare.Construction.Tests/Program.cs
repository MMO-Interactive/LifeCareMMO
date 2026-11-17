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
BuildingPlan Must(BuildResult r, string name) { Check(r.Accepted, name + (r.Accepted ? "" : " — " + r.Message)); return r.Plan; }

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
Check(rooms.Count == 1 && rooms[0].Tiles.Count == 9 && rooms[0].AreaSquareMeters == 144, "Enclosed 3x3 floor is one 144 m² room");

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

Console.WriteLine($"{passed}/{passed + failed} construction checks passed");
return failed == 0 ? 0 : 1;
