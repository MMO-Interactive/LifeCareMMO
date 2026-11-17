# LifeCare MMO

An emergency life simulation. Players choose a career in Medical, EMT, Police or Fire. They live through
disasters and everyday life in a shared world.

The game brings together pieces of four earlier projects:

| Source | What comes from it |
|---|---|
| Storm Chase (Tornadoes) | The world, weather and storms, NPC population, per-person genetic avatars |
| Life Beyond v2 (Virtual_World) | Sims-style grid building and the Adaptive District Runtime server design |
| BiologicalSimulator / BiologyLib | Genetics and body simulation |
| Arcade Clinic (PediatricClinic) | Clinic play, widened to family practice; the Pedizon shop; pie-wheel interactions; clinic management |

The integration plan is the design reference:
[Storm Chase → Emergency Life Simulation: Integration Plan](https://claude.ai/artifact/ULfjrNKNmfBucf2vYQg63H).

## Server layout

The setup follows Life Beyond's servers. Every role is its own process. All of them run on one server at first
and move to their own machines as load and funds allow.

- **Gateway:** the only way in for clients.
- **Orleans domain services:** Accounts, Population, Medical, Dispatch, Commerce, Construction.
- **DistrictRuntime workers:** run the simulation cells, under a DistrictAuthority.
- **Climate simulation:** its own process, which can later spread over several servers.

## Repository layout

| Folder | Contents |
|---|---|
| `src/` | Game client, servers and shared libraries |
| `tests/` | Unit tests and probes |
| `docs/` | Design notes |
| `tools/` | Build and content scripts |
| `assets/` | Source art and audio, stored in Git LFS |

## Building

Requirements:

- .NET SDK 10.0.401 or later (see `global.json`).
- Git LFS (`git lfs install`).
- These checkouts beside this repo:
  - the MMOI Old Engine (default `%USERPROFILE%\youradventure\Old Engine`)
  - BiologyLib (default `%USERPROFILE%\BiologyLib`)

To use other locations, set `EngineRoot` or `BiologyLibRoot` in a `Directory.Build.local.props`, which git
ignores.
