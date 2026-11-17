# Storm Chase dedicated server

This is the headless .NET 10 host used by the native game. It runs the same generated world, PMWeather campaign and native PhysX destruction/vehicles as the client. The server accepts movement axes and gameplay actions; client positions, photo locations and final debris poses never grant authority. Every collision-bearing fragment has a stable authored part ID, actual native quaternion, linear/angular velocity, sleeping state and collision residency. Reliable release/final-state events and complete late-join baselines preserve road-blocking debris across clients.

The portable Windows package is `Game/Server`. Open a terminal in that folder and use `./StormChase.Server.exe` in the examples below; it includes its runtime and native libraries, so building is optional. The accounts and game services are separate processes. For remote clients, allow/forward the accounts TCP port (5080 by default), game API TCP port (5180) and game UDP port (7777) to their respective machines. Advertised hostnames must resolve to addresses reachable by those clients.

Build from the repository root with the .NET 10 SDK:

```powershell
dotnet build multiplayer/StormChase.Server/StormChase.Server.csproj -c Release
```

The engine location is selected by `-p:EngineRoot="C:\path\to\Old Engine"`. The engine physics runtime must include `PhysXPhysicsWorld.TryGetBodyDynamics`. An isolated staged engine build may supply `-p:EnginePhysicsRuntime="C:\path\to\Engine.Physics.PhysX.dll"`; this copies that exact verified runtime into build/publish output. The executable folder contains the actual PhysX and GameNetworkingSockets native DLLs and authored world assets. The verified native server platform is Windows x64.

Start the independent accounts service first. It loads no game world, so players can log in and select characters before choosing Single Player, Host or Join:

```powershell
.\multiplayer\StormChase.Server\bin\Release\net10.0\StormChase.Server.exe `
  --accounts-only --http-port 5080 --data-root .\accounts-data
```

Start a game host registered with that accounts service. The private operator registration key is generated in the protected accounts data directory; pass it through an environment variable:

```powershell
$env:CHASE_MASTER_KEY = (Get-Content .\accounts-data\master-server.key -Raw).Trim()
.\multiplayer\StormChase.Server\bin\Release\net10.0\StormChase.Server.exe `
  --http-port 5180 --game-port 7777 --data-root .\game-data `
  --master-url http://127.0.0.1:5080 --master-key-env CHASE_MASTER_KEY `
  --server-name "My chase server" --max-players 16 --world-seed 73019
```

Defaults: accounts API listens on `127.0.0.1:5080`; game API on `127.0.0.1:5180`, encrypted game transport on UDP 7777, 16 players, seed 73019, campaign starts at 08:00. `/health` returns `starting` until its service is ready, then `ready`. Game `/api/server.ready` waits for the actual world/native transport. The generated world fingerprint must match before native admission is accepted. Keep the same game/world asset version on server and clients. Omitting `--master-url` retains an isolated standalone diagnostic host; the game's global login flow uses the independent accounts service.

The campaign favors afternoon development while allowing occasional evening and post-midnight storms when seeded moisture and shear support them. New clouds develop from thin cumulus into organized storms over 30–80 wall seconds. Independent seeded compact towering, classic, broad precipitation and low sprawling forms vary parent dimensions, cloud bases, rain, rotation and funnel proportions within the same intensity potential. Tornado episodes retain their independent source lifecycle and can recycle into another form. Surface energy decreases continuously overnight; existing cells lose future growth/recycling and fade over several game hours when their birth environment loses support. Sunset does not force a tornado to disappear. Each 24-hour campaign day takes 1,200 wall seconds. Restart reconstruction includes the previous night so active cells can carry across midnight. Joining players use reusable clear parking near actual service stations; monotonically increasing player identities do not move later arrivals into buildings.

For an external dedicated server, supply a trusted HTTPS certificate (PFX/PKCS12). Pass its password through an environment variable, never a command-line password:

```powershell
$env:CHASE_CERT_PASSWORD = 'your certificate password'
.\StormChase.Server.exe --listen 0.0.0.0 --advertise-host chase.example.com `
  --certificate C:\certs\chase.pfx --certificate-password-env CHASE_CERT_PASSWORD `
  --http-port 5180 --game-port 7777 --data-root C:\StormChaseData `
  --master-url https://accounts.example.com:5080 --master-key-env CHASE_MASTER_KEY
```

Players log in to `https://accounts.example.com:5080`; its `/api/servers` lists registered game hosts, including `https://chase.example.com:5180`. Hosts heartbeat every five seconds; stale entries expire after 35 seconds and graceful shutdown removes them immediately. Entries are flat server metadata with `apiAddress`. Native transport pins the public authority certificate and server identity received over the game's verified HTTPS connection. A public URL with a certificate validation error must be fixed before joining. `--allow-insecure-lan` is an explicit trusted LAN development option; it does not protect account credentials or initial trust against interception. No cloud directory deployment is required.

For an externally accessible accounts service, start a separate process with a certificate covering its accounts hostname:

```powershell
$env:CHASE_ACCOUNTS_CERT_PASSWORD = 'your accounts certificate password'
.\StormChase.Server.exe --accounts-only --listen 0.0.0.0 --advertise-host accounts.example.com `
  --certificate C:\certs\accounts.pfx --certificate-password-env CHASE_ACCOUNTS_CERT_PASSWORD `
  --http-port 5080 --data-root C:\StormChaseAccounts
```

Provide the generated `C:\StormChaseAccounts\master-server.key` privately to the trusted game host operator and assign it to that host's `CHASE_MASTER_KEY` environment variable. Clients enter the accounts HTTPS address on Login. Both game and accounts processes need to remain running; closing an interactive terminal stops its process unless it is managed by an OS service runner.

Accounts, characters and careers belong to the independent accounts service and follow players between its registered game hosts. A single login token works throughout that directory. Authentication uses ASP.NET Core's framework password hasher and protected opaque bearer/refresh tokens: access lasts 15 minutes, refresh lasts seven days. These are proprietary Identity bearer tokens, **not JWTs or a general OAuth/OIDC identity provider**. There is no email delivery, account recovery or external identity federation in this local host. Native admission comes from the accounts service: a separate cryptographically random, character-and-server-bound, single-use ticket expiring after 30 seconds. Request it after loading the game assets. The selected game redeems it over the private authenticated accounts API and holds a character lease; another host cannot admit or credit that character simultaneously. API credential/action rates, body sizes, character counts, action frequency and native queues are bounded.

Game hosts sharing the private registration key are trusted operators: they run the authoritative simulation and may commit bounded progression only for their leased players. Do not distribute that key to an untrusted public community host. The public API never exposes it, public callers cannot reach private host mutation routes, and native clients never supply wallet/reward authority. External master URLs require HTTPS. The accounts service must stay available for host admission and durable progression.

Accounts `career.sqlite` stores hashed accounts, owned characters, integer-cent wallets, gear/vehicle ownership, actual fuel/health, reputation, photograph/research counts, popularity, completed jobs and active assignment state. SQLite transactions commit purchases/rewards with durable idempotency receipts; no public credit endpoint exists. Mission completion atomically grants pay/reputation/popularity, marks completion and clears the active assignment. Native actions validate the actual server camera/probe/station/vehicle/work time and real wreckage. An active character must park at an intact service station for HTTP purchases, repair or vehicle selection; the accounts service delegates that command to the leased game's physics authority. Out-of-match equipment/vehicle purchases are available through the menu. Each game keeps campaign time/server identity separately. World damage is session state; restarting a host rebuilds its world while retaining central careers and campaign time.

Housing also belongs to the accounts service. A deed identifies a real generated house within an exact world content fingerprint; one character may own one house per world, and two characters cannot buy the same house. Home and furniture purchases charge integer cents in the same transaction as the ownership change. Each house supports at most 24 furnishings. Only the leased game host may place furniture after checking the character is inside its owned, intact home. The host simulates furnishing collisions, publishes authoritative poses, and persists their exact position/quaternion. Restarted hosts reconstruct them from those saved poses rather than retaining a stale collision residency flag. Owners can enable or disable visits; only the owner can change furnishings or privacy.

Back up the entire data directory while the host is stopped, including SQLite and its WAL files, `keys` and `transport-identity.json`. Data Protection keys retain token validity across restarts. The transport identity file contains the private native certificate and must remain private. The host restricts the directory to its Windows account, SYSTEM and administrators; Unix directories use owner-only permissions. Never publish the data directory or private certificate. Switching the OS account requires an intentional key/certificate migration; Windows Data Protection keys are protected for the running account.

Ctrl+C/OS host shutdown flushes progression and disconnects native clients. The desktop launcher can additionally start the host with a 32-byte hexadecimal `--control-key`; `POST /api/admin/shutdown` then requires a loopback source and `X-StormChase-Control` with that key. Without the option the route is absent.

The public HTTP contract uses camel-case JSON:

| Route | Purpose |
| --- | --- |
| `GET /api/server`, `/api/servers`, `/api/catalog` | Metadata, self entry, authoritative prices |
| `POST /api/auth/register`, `/api/auth/login` | `{username,password}`; register then obtain framework tokens |
| `POST /api/auth/refresh` | `{refreshToken}` |
| `GET /api/me`, `/api/characters` | Authenticated account and owned characters |
| `POST /api/characters` | `{name}`; up to four characters |
| `POST /api/characters/{id}/select` | Select/obtain owned career |
| `GET /api/characters/{id}/career` | Server career; HTTP cash in dollars, native cash in integer cents |
| `POST /api/characters/{id}/purchases` | `{sku,operationId}`; retries charge once |
| `POST /api/characters/{id}/vehicle` | `{vehicleId}`; owned pickup/suv/interceptor |
| `POST /api/matches/join` at accounts | `{characterId,serverId}`; short-lived game-bound native admission |

Offer indices used by native gameplay: 0 reinforced probe ($1,600), 1 telephoto camera ($1,400), 2 mobile mesonet ($2,400), 3 SUV ($8,000), 4 interceptor ($24,000), 5 repair ($250). `SelectVehicle` uses target 0 pickup, 1 SUV, 2 interceptor. All prices come from `/api/catalog`.

Run meaningful isolated verification:

```powershell
.\StormChase.Server.exe --self-test --test-output C:\proof\http
.\StormChase.Server.exe --native-self-test --test-output C:\proof\native
.\StormChase.Server.exe --master-self-test --test-output C:\proof\accounts
```

The HTTP test uses real loopback Kestrel, waits for actual 30-second admission expiry, restarts the host, and verifies durable ownership/currency/equipment/condition/token behavior. It also writes `housing-persistence-proof.json` for atomic deed/furnishing purchases, receipt retries, ownership, saved rotations, privacy and capacity. The accounts test proves one login/character across two independent HTTP game adapters, server-bound redemption, lease authority, central idempotent progression and independent persistence. The native test starts the independent accounts service plus the actual native game world and signed GameNetworkingSockets clients under five percent packet loss and 40 ms lag. It exercises capsule/vehicle inputs, waits for real timed mission work, generates actual PhysX tornado destruction from authored buildings, compares settled poses on two clients, and checks a third late join. It also collects actual settled fragments and verifies durable cleanup rewards and collision-disabled removal tombstones. Its JSON includes measured 20 Hz server tick timing during two connected clients, live debris and concurrent career reads; this is a finite local measurement, not a capacity guarantee. Its controlled storm/placement/fast settle helpers are callable only inside the explicit native validation executable mode; gameplay endpoints cannot invoke them. Tests create fresh isolated data folders and JSON evidence rather than altering an existing career.

The CPU-only campaign regression can be run from the repository root with `dotnet run --project tools/StormChase.Weather.Tests -c Release -- artifacts/campaign-weather-development`. It checks 96 seeded nights, naturally developed nighttime touchdowns, source weakening, gradual births, appearance diversity, recycled funnels, fixed update cadence and deterministic resume. Its `campaign-night-capture.json` and `campaign-appearance-captures.json` contain complete genuine seeded source environments for renderer verification, rather than manually constructed storms or tornadoes.

For an independently rendered client integration proof, add `--native-fixture` to an isolated game host using fresh data. Startup runs an actual tornado against authored buildings and settles the native PhysX fragments, then prints the debris count and site coordinates. The host continues accepting normal authenticated clients with that complete baseline. This explicit validation option does not appear in the ordinary host UI. Launch test helpers from a separate copied executable folder if another agent needs to rebuild the server.
