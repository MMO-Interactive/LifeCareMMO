# StormChase native transport

This project uses Valve GameNetworkingSockets 1.6.1, pinned to commit `61d5da24fc933ab104cc8e5ab2beb133550f9570`. It provides real encrypted UDP connections, reliable ordered messages, unreliable motion messages, native congestion handling and fragmentation. Steam is not required. The Windows x64 runtime statically links protobuf 36.2, Abseil 20250512.1 and the C++ runtime; its remaining imports are Windows system libraries. It uses Windows BCrypt AES-GCM and upstream Curve25519/Ed25519.

Build the native runtime with `powershell -File native/build-windows.ps1`. Downloads are pinned and SHA-256 checked in `native/dependencies.json`. The build uses a local LLVM MinGW toolchain, CMake and Ninja without modifying Visual Studio. The resulting DLL is copied to `runtimes/win-x64/native`; project references copy it to build and publish output. Run the integration harness with the pinned SDK:

```
C:/Users/reviv/.cache/mmoi-dotnet/10.0.401/dotnet.exe run --project ../StormChase.Networking.Tests -c Release
```

Create the dedicated server identity before opening transport hosts:

```csharp
var identity = TransportIdentity.LoadOrCreate("private/server-transport.json");
using var server = StormTransportHost.CreateServer(29001, 32, identity.ServerSecurity);
// HTTPS discovery exposes ONLY identity.Identity and identity.AuthorityCertificateBase64.
// identity.ServerCertificateBase64 contains the private signing key. Keep it private.

var security = new TransportSecurity(discovery.TransportAuthority,
    ExpectedPeerIdentity: discovery.TransportIdentity);
using var client = StormTransportHost.CreateClient(security);
var connection = client.Connect(discovery.Host, discovery.Port);
// Wait for Connected before sending JoinRequest with the HTTPS admission ticket.
client.Send(connection, MessageKind.JoinRequest, new JoinRequest(ticket, worldFingerprint));
```

The native certificate is checked against the HTTPS-discovered authority and exact server identity **before** the game receives Connected or can send application data. Encryption cannot be disabled through this adapter. Remote clients require pinned discovery credentials; unauthenticated native identity is permitted only for loopback development connections. A process accepts one active authority domain. Disposing all hosts clears native state; a subsequent host can switch to a different server authority. The certificate provisioning follows upstream's native certificate format and signing routines; the shim accesses the pinned upstream certificate store because it has no public trust-store setter. No custom encryption protocol is added.

Certificates expire: the generated server certificate lasts one year and its root ten years. Rotate the private identity file deliberately before expiry and advertise the new public identity through trusted HTTPS. Protect the private file with the server account's filesystem permissions. Server-generated certificates authenticate the server, while HTTP passwords, access tokens, single-use admission, account bans and persistent progression remain application responsibilities.

Every host owns a background network pump. `Poll()` only drains game-facing events, and `Send` does not block rendering. Outgoing traffic and incoming events each have an 8 MiB bound; queue counts are bounded. A false Send result means backpressure, so critical callers retry without granting rewards twice. A saturated native reliable send buffer retains messages in order and retries; obsolete unreliable snapshots can be dropped. Malformed/version-mismatched/nonfinite/oversized game messages close the connection.

`StormChase.Protocol` preserves full authoritative Engine.Environment state and stable physical part IDs. Destruction release/remove events and baseline chunks use reliable delivery. Motion uses small unreliable chunks: twelve DebrisBodyState values with an empty StablePartId (already supplied by baseline) occupy about 799 protocol bytes and fit a 1200-byte native packet. Full baselines are reliable chunks and become collision-active only after all chunks for their revision arrive. Sleeping/archived collision bodies remain part of the baseline. Collected parts remain permanent hidden tombstones with collision disabled, so cleanup cannot respawn the original wall for a late join. `AuthoritativeDebrisReplica` atomically assembles matching baseline chunks, overlays newer per-body motion, preserves stable mappings and refuses stale or invalid poses. It keeps old published arrays immutable. Sequence numbers/ticks let gameplay ignore stale poses; the transport never runs independent debris physics.

Wire version 2 compresses complete WorldSnapshot and JoinAccepted JSON with lossless Brotli. Every render and gameplay value survives unchanged; compression removes repeated field/catalog text before native transport sees the message. Both encoded payload and advertised decompressed length are bounded to 256 KiB, and decoders reject malformed, truncated or trailing streams. All peers must ship the same protocol version. Fragment motion remains compact binary and does not depend on snapshot compression.

The console integration harness verifies malformed envelopes, real server/two-client connections, encryption, 600 matching fragment poses, native reliable congestion retry, loss/lag ordering, late-join settled state, disconnect, separate-process trusted certificates, wrong name/authority rejection and changing authority after disposal. Evidence is saved by the enclosing task to `artifacts/gns-loopback-tests.log`. Windows x64 was built and tested. Linux native builds and the engine's Linux physics packaging still require verification before claiming Linux dedicated-server support.

Upstream documentation: https://github.com/ValveSoftware/GameNetworkingSockets and its pinned BUILDING.md / include/steam/isteamnetworkingsockets.h. Third-party notices are distributed with this project and copied to output.
