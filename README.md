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

## Client and server repos

This repo is the game client. The servers live in the separate **LifeCareMMO-Server** repo, checked out beside
this one (default `%USERPROFILE%\LifeCareMMO-Server`). Every game runs through the server; there is no single
player. The client connects to the LifeCare Gateway, by default `http://127.0.0.1:5000`.

This repo owns the code both sides run, and the server repo compiles it from here:

- `src/Shared`: `StormChase.Protocol`, `StormChase.Networking` and `LifeCare.Construction`.
- The world, terrain, weather, destruction and vehicle sources in `src/Client/PmWeather.Showcase` that the
  server projects list as linked files.

Client and server must build from matching commits of this code. If they differ, the world content fingerprint
won't match and players can't join. When you change any of it, rebuild and restart the servers too.

The server setup follows Life Beyond's servers. Every role is its own process. All of them run on one server at
first and move to their own machines as load and funds allow.

- **Gateway:** the only way in for clients.
- **Orleans domain services:** Accounts, Population, Medical, Dispatch, Commerce, Construction.
- **DistrictRuntime workers:** run the simulation cells, under a DistrictAuthority.
- **Climate simulation:** its own process, which can later spread over several servers.

## Repository layout

| Folder | Contents |
|---|---|
| `src/Client` | The DX12 game client |
| `src/Shared` | Libraries shared with the server repo, plus the client-side NPC population |
| `tests/` | Unit tests for the shared libraries |
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

To play locally, start the servers from the LifeCareMMO-Server repo (`tools\Start-LifeCareServers.ps1 -Build`), then
run the client with `--game`.
