# Phase 0: Foundations

Status, 2026-11-17: the server side is done and its gate probe passes 18/18. The real DX12 client has not yet been
pointed at the Gateway.

## What Phase 0 delivers

Every server role is its own process. All of them run on one machine today. Each can move to its own machine by
changing an address, not code.

| Process | Project | Public? | Role |
|---|---|---|---|
| Gateway | `src/Servers/LifeCare.Gateway` | Yes, the only one | One HTTP port, one encrypted UDP game port |
| Accounts | `src/Servers/StormChase.Server --accounts-only` | No | Logins, characters, careers, server directory |
| District host | `src/Servers/StormChase.Server --relay-port` | No | Physics, vehicles, players, world damage |
| Climate | `src/Servers/LifeCare.Climate` | No | Campaign clock and weather model; pushes frames to hosts |

Start them all with `tools/Start-LifeCareServers.ps1`. Then point the client at the Gateway with
`MMOI_STORM_CHASE_ACCOUNTS_API=http://127.0.0.1:5000`.

## How a request travels

- **HTTP.**
  - `/s/{serverId}/…` goes to that district host's API.
  - Everything else goes to accounts.
  - `/api/internal` is never reachable from outside.
  - The Gateway rewrites server addresses in `/api/servers`, `/api/server` and `/api/matches/join` responses to its own
    addresses, so clients never learn an internal address.
- **Game traffic.**
  - The client connects to the Gateway's game port and sends `JoinRequest`.
  - The Gateway saw that ticket when it proxied `/api/matches/join`, so it already knows the district host. Admission
    needs no extra lookup.
  - Frames are forwarded unchanged over one TCP relay link per district host.
  - The top bit of each frame's sequence number marks reliable delivery, so the Gateway forwards each frame on the
    channel its sender chose without decoding it.
- **Hop budget.**
  - Input goes client → Gateway → district host: one internal hop.
  - Measured on one machine, the Gateway adds about 4 ms to the median round trip and under 1 ms at p95, against a
    player connected straight to a host. That is one probe run of 71 pings each.
  - The host's 50 ms tick dominates both paths.
- **Weather.**
  - The climate process pushes one frame per 50 ms tick.
  - Hosts read their local copy and never wait on the climate process during a tick.
  - The 97-level atmosphere is resent only when its day changes.
  - A slow host skips straight to the newest frame.

## Gate

Run the probe with `dotnet tests/LifeCare.Phase0Probe/bin/Release/net10.0/LifeCare.Phase0Probe.dll <repo>`. It writes
`artifacts/phase0-probe/phase0-gate.json` and one log per process.

Covered:
- Register, create a character, list servers and join, all through the Gateway.
- The host has no public game port.
- Internal routes are blocked.
- The weather clock comes from the climate process.
- Snapshots stream back.
- Sessions close cleanly.

Not yet covered:
- The real client through the Gateway.
- Single-player, which still runs every role in-process and is unchanged by this phase.

## Limits to know

- **One trust domain per process.** The game transport library allows one certificate identity per process. That is
  why the Gateway talks to hosts over TCP rather than a second encrypted connection. The link is plain TCP, bound to
  loopback by default. Off loopback it requires `--link-key-env`. Add TLS before links cross an untrusted network.
- **Native lock.** The relay holds one GNS host, and all of the shim's native calls share one lock. That is fine for
  one Gateway in front of tens of players. Larger loads need more Gateways.
- **Direct-mode hosts.** A host without `--relay-port` (for example "Host a server" from the client) is listed by the
  Gateway as not ready, because the Gateway cannot relay to it.
- **World constants.** The climate process copies four world constants from `StormChaseWorldMap.cs` in
  `LifeCare.Climate/WorldConstants.cs`. Change both together.
- **Art.** Art is not in git. Builds read it through `StormChaseAssetsRoot`, by default `%USERPROFILE%\Tornadoes\Assets`.
