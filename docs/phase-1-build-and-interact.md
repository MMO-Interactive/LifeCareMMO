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

- **Y** toggles build mode on your lot. **1** floor, **2** wall, **3** roof, **4** delete. **PgUp/PgDn** change level.
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
- **Furniture and lot geometry are plain boxes** with built-in materials. No art is involved, and none goes in git.
- **Access tokens are not refreshed** by the lot client. A session longer than the token's life loses lot access until
  the player rejoins.
- **The client polls every lot.** Fine for Phase 1. Many lots will need the list filtered by area.
