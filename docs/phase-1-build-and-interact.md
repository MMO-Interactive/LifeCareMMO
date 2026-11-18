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

- **Y** toggles build mode on your lot. The camera leaves the avatar and flies freely: **WASD** move, **Space/Ctrl** rise
  and sink, **Q/E** turn, **Shift** hurries, the **wheel** zooms and a **right-drag** looks around. **Home** resets the view.
  The play HUD gives way to the build screen: lot information (left), controls (right) and the build catalog (bottom).
- The catalog's categories are structure, curved walls, doors and windows, wall styles, wall paint, floors and carpet,
  roofs, and edit. Picking an item selects its tool. Keys **1–9** pick the tools directly: room, wall, curved wall, doors
  and windows, wall style, wall paint, floor, roof, delete.
  - **Room:** drag a rectangle; the tool lays the floor and walls it in.
  - **Wall:** drag along tile edges. It builds full walls, half walls or railings, all from the module pack.
  - **Doors & windows:** click a wall, or drag along several. On a curved wall the fitting goes where you click, and
    clicking an opening removes it.
  - **Wall style:** changes a wall's siding.
  - **Wall paint:** paints the face the mouse is on. Hold **Shift** to paint every wall around the room.
  - **Floor:** wood, tile, concrete, carpet and vinyl, each in several colours.
- **Curved walls:** press on the corner where the wall starts and drag to the point halfway round. Releasing locks the
  half circle; then move the mouse to carry the curve further round, and click to finish. **H** stops at the half circle
  and **F** closes the full circle. A right click or **Esc** cancels.
- The mouse aims from the build camera. Wall tools act on the face the cursor ray hits first; a click with the wall
  tool takes the nearest tile edge.
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

## Doors

Doors work as in Life Beyond v2 and Tiny Clinic:
- **Interaction:** right-click a door to open its wheel: **Open** or **Close**, and **Inspect**. Anyone within one tile
  (4 m) of the door can use it, on their own lot or a neighbour's.
- **Server state:** whether a door is open is part of the lot document (`OpenDoors`). The Construction service sets it at
  `POST /construction/api/lots/{lot}/doors` with an explicit state and an operation id, so a retried request is applied
  once. It asks the district host where the character really stands (`/api/internal/characters/{id}/position`) and
  refuses a request from further away.
- **Doors block movement:** a door wall is two jambs and a lintel, each drawing its slice of the wall module, so the
  doorway itself is open. The leaf is a separate physics piece: closed it fills the doorway; open it stands clear of it.
  - Wood, half-glass and screen doors swing into the room.
  - The garage door rolls up under the ceiling.
  - The barn doors slide apart.
  - A door in a curved wall is a straight leaf across the opening's chord, swinging into the round room.
- **Timing:** clients animate the leaf, which starts moving as soon as you choose Open or Close. The district host picks
  up the new state within a second; other players see it on their next lot refresh, within two seconds.

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
- **Lot access stays signed in.** On a 401 the lot client refreshes its 15-minute access token through the Gateway and
  retries the request once.
- **The client polls every lot.** Fine for Phase 1. Many lots will need the list filtered by area.
