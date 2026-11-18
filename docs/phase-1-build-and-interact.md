# Phase 1: Build and interact

Status, 2026-11-17: the gate probe passes 25/25. Nobody has played build mode in the real DX12 client yet. It builds
and is wired in, but the probe drives the same controllers headlessly.

## What Phase 1 delivers

| Piece | Where | What it does |
|---|---|---|
| Building grid | `src/Shared/LifeCare.Construction` | Life Beyond's 4 m tiles: edge walls, floors, roofs, deeds, structural support, rooms |
| Furniture rules | `FurnishingEditor` | Tiny Clinic's 0.5 m sub-cells. Pieces stand on a floor, never cross a wall and never overlap |
| Construction service | `src/Servers/LifeCare.Construction.Service` | The lot authority behind the Gateway at `/construction/`. Revisioned, persisted, owner-only |
| Build mode | `LotBuildController` | Floors, walls and roofs only, ghost-checked with the service's own rules |
| Furniture in the world | `LotFurnishingController` | Packages are carried and set down; placed pieces are moved, turned or picked up |
| Pie wheel | `InteractionPieMenu` | Port of Tiny Clinic's `ClinicFurnishingPieMenu`: six slots, More Actions paging, Back/Close |
| Lot geometry | `LotGeometry` | Boxes per revision with stable piece ids, release wind and mass |
| Client | `src/Client/PmWeather.Showcase/StormChaseLots.cs` | Draws lots, takes build and furniture input, shows the wheel |
| Storm damage | `GameAuthority.Lots.cs`, `StormChaseDestruction.External.cs` | Lot pieces are PhysX destructible parts on the district host |

## Playing it (online only)

Start the servers with `tools/Start-LifeCareServers.ps1` and sign in through the Gateway. A lot needs a deed: a trusted
service posts it to the Construction service's `/api/internal/lots` with the service key. The client polls the
Gateway's `/construction/api/lots` every 2 s.

- **Y** toggles build mode on your lot. **1** floor, **2** wall, **3** roof, **4** delete, **5** curved wall, **6** finish.
  **PgUp/PgDn** change level.
- **B** steps the tool's finish: wall siding (walls, curved walls, finish tool), floor finish (floor) or roof finish (roof).
- **N** steps the second option: the opening a wall hosts (walls, finish tool), the roof shape (flat, gable, hip) or the
  curved wall's profile (full, glazed, half wall, counter).
- **C** (curved wall tool) steps the sweep: full circle, 270°, 180°, 90°. Press at the centre and drag out the radius; the
  arc's middle points where you release, so a 270° counter's open side faces away from the drag.
- The finish tool restyles the wall you click and sets its opening. On a curved wall it adds the selected opening where you
  click, or removes the one you click.
- In build mode, aim with the crosshair and click or drag. A click with the wall tool takes the nearest tile edge.
- **I** unpacks a furniture package. **R** turns it, a click sets it down, **Esc** puts it away.
- **Right click** on a piece opens the pie wheel: Arrange (Move, Rotate, Pick Up) for the owner, Inspect for anyone.

## How storm damage works

- The district host polls the Construction service's internal `/api/internal/lots` (2 s by default, `--lot-poll-seconds`).
- Each revision is turned into boxes with `LotGeometry` and registered as external PhysX destructible parts.
- From there the path is the same as for authored buildings:
  - A piece tears loose when its local tornado wind reaches its release speed.
  - Loose pieces fly as native rigid bodies.
  - They reach every player as ordinary debris bodies named by piece id.
- Each client renders those bodies from its own copy of the same lot revision.
- Release wind by piece:
  - Roof tiles: 90 mph.
  - Outside walls: 110 mph.
  - Walls between two floored tiles: 135 mph.
  - Upper floors: 150 mph.
  - Furniture: 95 mph.
  - Ground floors never blow away.

## Gate

Run `dotnet tests/LifeCare.Phase1Probe/bin/Release/net10.0/LifeCare.Phase1Probe.dll <repo>`. It writes
`artifacts/phase1-probe/phase1-gate.json` and one log per process.

The probe covers:
- Climate, accounts, construction, district host and Gateway, each as its own process.
- Two players who use only the Gateway.
- The builder builds a roofed 3x3 room and sets down a bed, using the client's own controllers.
- Both players read the identical lot document. The neighbour cannot edit it.
- The district host loads that revision into its physics.
- Both players join the district.
- A held test tornado tears pieces off the room. In the passing run it tore off 22 pieces.
- Both players' connections report exactly the same pieces, with the same server bodies. The roof goes and the ground
  floors stay.

The test tornado comes from `--storm-test-routes`, which needs keyed internal routes (`--master-url`).
- `/api/internal/test/clear-site` names a spot away from authored buildings. Otherwise the 256-body debris cap fills
  with town debris first.
- `/api/internal/test/lot-tornado` holds a funnel so the lot sits in its eyewall. The axis itself is calm.

## Limits to know

- **Damage lasts for the host's session only.** A restarted host shows lots whole again. Damage should be written back
  to the Construction service, so a lot can be "damaged until repaired".
- **Packages are a stand-in.** `I` unpacks the next catalogue item until the shop delivers real packages.
- **Furniture is still plain boxes.** Lot walls, floors and roofs are drawn from the rural module pack
  (`assets/modules/rural`, see [Building modules and curved walls](#building-modules-and-curved-walls)); without the pack
  they fall back to boxes.

## Building modules and curved walls

- **Finishes are data.** Lot documents store a style and an opening per straight wall (`WallFinishes`), a finish per floor
  tile (`FloorFinishes`) and a shape and finish per roof. Lot files saved before these existed load with defaults.
- **Modules come from the finish.** `LotGeometry` picks each piece's pack modules (`LotPiece.Modules`). A wall turns so its
  exterior faces away from the floored side; a wall with floor on both sides is interior plaster unless finished otherwise.
  Outer corners get corner trims, and gable ends get gable infill in the style of the wall below.
- **Pitched roofs** (gable, hip) span exactly two tiles, the span of the pack's roof modules, with the ridge along the
  longer side. Flat roofs fit any rectangle.
- **Curved walls are true arcs** (`CurvedWall`, ported from Arcade Clinic's arc wall paths): centre, radius, start and sweep
  in fixed-point millimetres and millidegrees, from a 15° arc to a full ring. Openings sit on the arc by angle; doors keep a
  straight leaf across the chord, and the authority refuses openings that are too wide for the curve.
- **The mesh is generated from the arc** (`CurvedWallMesher`): faces sampled at 3° or 12 cm at most, with radial normals and
  shared vertices, so a ring reads as one surface. Siding courses, battens, stones and window glass follow the curve.
- **Pieces split along the arc.** Each wall is cut into pieces of at most 2.4 m of arc, each with its own box for storm
  damage. Each opening stays whole inside one piece, and a torn-off piece flies as a curved panel.
- **Profiles:** full (3 m), glazed (a continuous curved window band), half (1 m low wall) and counter (1.05 m with a
  worktop on the staff side).
  - A 270° counter is a horseshoe nurses' station whose gap is the staff entrance.
  - A full counter ring takes a passage instead.
- **Round rooms:** a full ring with floor inside is a room (`RoundRoomFinder`) with area π·r² and its entrances counted.
  Furniture cannot straddle a curved wall or counter.
- **Capture:** `PmWeather.Showcase --chase-capture --chase-scene lot-modules` (or `lot-round-room`) builds a showcase lot
  through `BuildingEditor` and renders it. The lot holds the module house, a hip-roofed garage, the round room with its
  nurses' station, a glazed screen and a curved low wall.
- **Access tokens are not refreshed** by the lot client. A session longer than the token's life loses lot access until
  the player rejoins.
- **The client polls every lot.** Fine for Phase 1. Many lots will need the list filtered by area.
