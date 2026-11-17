# NPC population and distributed simulation — design (draft, 2026-11-17)

> **Status (phase 1, 2026-11-17).**
> - **Genetic appearance:** looks come from genetics, not presets. Each resident has a BiologyLib 23-pair genome:
>   its own pigmentation, hair texture and height loci plus synthetic additive morphology loci, one per UMA DNA
>   slider. Children are conceived from both parents' genomes.
> - **Avatar system:** builds every person's own avatar, with skin, hair and eye colours deferred to material tints
>   (an opt-in Tools.Avatar.UMA feature) so people share composited textures.
> - **Families and days:** households are singles, couples, families and single parents. Children attend school on
>   weekdays (each town's civic hall stands in until school buildings exist), play out after school (8+), and young
>   children go out with a parent.
> - **Toggle:** single-player NPCs are opt-in via `MMOI_STORM_CHASE_NPCS=1`.

Goal: a large simulated population (thousands of residents) using BiologyLib bodies and MMOI avatars,
run by a server backend split into processes that start on one PC and can later move to other machines.
Single-player runs the same simulation in-process at a smaller scale.

## What exists today
- **Accounts master** (`--accounts-only`, :5080): logins, characters, careers, housing.
- **Game host** (:5180, UDP 7777): one process owns everything else — world, PhysX destruction, weather,
  players and vehicles. It ticks at 20 Hz.
- **Avatars:** the player avatar uses `Tools.Avatar.UMA` + `Engine.Avatar.Generation` from the Old Engine.
  This is the same code as the MMOIEngine.Avatar repo. Each avatar is a full skinned UMA build (expensive),
  and only one is built per client today.
- **BiologyLib** (C:\Users\reviv\BiologyLib, net8.0, no dependencies), measured here:

  | Measure | Cost |
  |---|---|
  | One body advance | ~0.13–0.22 ms, roughly independent of step length (1 s – 10 min) |
  | Snapshot | ~0.09 ms |
  | Saved state | ~9 KiB per body |

  One core therefore steps roughly 5,000 bodies per second.

## Process layout
| Process | Owns |
|---|---|
| Accounts master (exists) | Accounts, careers, housing |
| Game host (exists) | Physics, weather, players, vehicles, world damage. **Authoritative for anything physical.** It replicates NPCs near players to clients. |
| NPC coordinator (new, runs inside the game host at first) | Population registry and persistence (SQLite in the host data root), shard assignment, NPC hand-off between shards, clock distribution |
| NPC workers ×N (new processes) | One shard each, cut on settlement/region boundaries. Each worker runs its residents' BiologyLib bodies, needs and daily schedules, routing on the town road/lot graph, and storm awareness. |

- **Clock:** the host's campaign clock is the only clock. Workers advance on clock messages, so runs are
  deterministic and resume-replayable, like the weather today.
- **Authority:** workers decide intent and own biology. The host resolves movement and collisions for nearby
  NPCs, and reports hazards (wind at the NPC, debris strikes, building collapse) back as events. Workers turn
  those events into BiologyLib injuries.
- **Internal transport:** framed binary messages over loopback TCP between host and workers. Shard
  messages carry no machine-local assumptions, so workers can move to other machines later; TLS and
  discovery are only needed at that point.

## Simulation level of detail
| Tier | Who | Behaviour | Biology |
|---|---|---|---|
| Background | Everyone | Schedule state machine (home / work / errand / shelter) stepped per game-quarter-hour, no positions | Advanced every 30 game-min |
| Active | Within ~2 km of a player | Real positions and path following on roads and sidewalks at 2–5 Hz | Every game-minute |
| Near | Within ~150 m | 10–20 Hz kinematic state replicated to clients. The host's PhysX applies wind knockdown and debris. | Every game-minute plus event-driven injuries |

At the measured cost, 20,000 background bodies advanced every 30 game-minutes (every 25 wall-seconds at
1,200 s per day) is under 0.2 core.

## Population
- Generated deterministically from the world seed:
  - **Homes:** the 329 houses, plus apartments above multi-level downtown stores.
  - **Workplaces:** stores, warehouses, civic halls, farms, fuel stations.
- The Engine.Avatar recipe (appearance) is derived from each resident's BiologyLib phenotype (sex, age,
  build) through `Engine.Avatar.Biology`, so the body and the avatar agree.
- **Persisted:** biology state, schedule, home, job and injuries.

## Client avatars
- A full UMA avatar is costly in build time, memory and render items. The client keeps a pool of at most
  about 16–24 full avatars for the nearest NPCs.
- Further Active-tier NPCs need a cheap form: instanced low-detail figures, merged like the street furniture.
- Recipes are cached per resident.
- Any renderer or skinning changes to Old Engine needed for many avatars will be raised before editing,
  because other projects share it.

## Single-player
The same `StormChase.Npc` library runs in-process behind the same worker interface, as a single local shard.
The population is scaled down (for example the nearest two towns at full detail, the rest in the background
tier).

## Code layout (proposed)
- `multiplayer/StormChase.Npc`: simulation library — population, schedules, BiologyLib adapter, tiers,
  injuries. It references BiologyLib by path; BiologyLib is not modified.
- `multiplayer/StormChase.Npc.Worker`: worker process host.
- Coordinator plus replication code in StormChase.Server, with protocol messages for NPC snapshots.
- Client: NPC avatar pool, low-detail figures and HUD hooks.

## Phases
1. **Library and single-player.**
   - Build `StormChase.Npc` with population generation, the background and active tiers, and BiologyLib bodies.
   - Run it in-process in single-player.
   - Render the nearest residents as UMA avatars walking their routes.
   - Add a probe for determinism, save/restore and per-tick cost.
2. **Distributed.**
   - Worker process, coordinator inside the host, and shard assignment and hand-off.
   - Host replication of NPCs to clients.
   - Multi-worker probe on one PC.
3. **Storms.**
   - Warnings lead to sheltering, with civic halls and storm shelters as refuges.
   - Wind and debris cause BiologyLib injuries, recovery and mortality.
   - Hooks for jobs and contracts (rescue, transport).
4. **Scale.**
   - Low-detail figures and avatar pooling.
   - 10k–20k population profile on one PC; FPS, frame items and tick budgets reported.
