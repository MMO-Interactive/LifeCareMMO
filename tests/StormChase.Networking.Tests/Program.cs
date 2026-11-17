using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using StormChase.Networking;
using StormChase.Protocol;
using Engine.Environment;
using Shared.Contracts.World;

if (args is ["--certificate-server", var identityFile, var securePortText, var markerFile])
{
    var id = TransportIdentity.LoadOrCreate(identityFile);
    using var certificateServer = StormTransportHost.CreateServer(ushort.Parse(securePortText), security: id.ServerSecurity);
    File.WriteAllText(markerFile + ".ready", "ready");
    var duration = Stopwatch.StartNew();
    while (!File.Exists(markerFile + ".stop") && duration.Elapsed < TimeSpan.FromSeconds(30))
    {
        foreach (var e in certificateServer.Poll()) if (e.Message is { Kind: MessageKind.Ping } ping) certificateServer.Send(e.ConnectionId, MessageKind.Pong, ping.Get<Ping>());
        Thread.Sleep(5);
    }
    return;
}

var passed = new List<string>();
void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException("FAILED: " + name); passed.Add(name); Console.WriteLine("PASS " + name); }
void Wait(Func<bool> complete, Action tick, string name, int timeoutMs = 10000)
{
    var watch = Stopwatch.StartNew();
    while (!complete() && watch.ElapsedMilliseconds < timeoutMs) { tick(); Thread.Sleep(5); }
    Check(complete(), name);
}

var original = ProtocolCodec.Encode(MessageKind.JoinRequest, 123, new JoinRequest("loopback-ticket"));
Check(ProtocolCodec.TryDecode(original, out var decoded) && decoded!.Sequence == 123 && decoded.Get<JoinRequest>().Ticket == "loopback-ticket", "versioned envelope roundtrip");
var corrupt = original.ToArray(); corrupt[4] = 255;
Check(!ProtocolCodec.TryDecode(corrupt, out _), "unsupported version rejected");
corrupt = original.ToArray(); BinaryPrimitives.WriteInt32LittleEndian(corrupt.AsSpan(16), 1);
Check(!ProtocolCodec.TryDecode(corrupt, out _), "inconsistent payload length rejected");
Check(!ProtocolCodec.TryDecode(new byte[ProtocolCodec.MaxPayloadBytes + ProtocolCodec.HeaderBytes + 1], out _), "oversized input rejected");
var invalidJson = original.ToArray(); invalidJson[ProtocolCodec.HeaderBytes] = 0;
Check(!ProtocolCodec.TryDecode(invalidJson, out _), "malformed JSON rejected");
try { ProtocolCodec.Encode(MessageKind.ClientInput, 1, new ClientInput(1, new(float.NaN, 0, 0), 0, 0, PlayerMovementMode.OnFoot, 0, false)); Check(false, "finite vectors enforced"); }
catch (ArgumentException) { Check(true, "finite vectors enforced"); }
var environment = new Engine.Environment.ProceduralEnvironmentService().Evaluate(Engine.Environment.BuiltInEnvironmentProfiles.TemperateOutdoor, 18, Engine.Environment.BuiltInWeatherProfiles.Thunderstorm, simulationSeconds: 157);
var fullWeather = new WorldSnapshot(157, 157, 42, [], [], [], [], [], Environment: environment);
var weatherBytes = ProtocolCodec.Encode(MessageKind.WorldSnapshot, 1, fullWeather);
Check(ProtocolCodec.TryDecode(weatherBytes, out var weatherDecoded) && weatherDecoded!.Get<WorldSnapshot>().Environment!.Clouds == environment.Clouds && weatherDecoded.Get<WorldSnapshot>().Environment!.Sun == environment.Sun && weatherDecoded.Get<WorldSnapshot>().Environment!.ActiveStorms.Count == environment.ActiveStorms.Count, "full authoritative environment preserves render parameters and vectors");
Check(JsonSerializer.Serialize(fullWeather,ProtocolCodec.JsonOptions)==JsonSerializer.Serialize(weatherDecoded!.Get<WorldSnapshot>(),ProtocolCodec.JsonOptions),"lossless compressed snapshot preserves every authoritative field exactly");
Check(weatherBytes.Length<JsonSerializer.SerializeToUtf8Bytes(fullWeather,ProtocolCodec.JsonOptions).Length,"complete weather compression reduces repeated snapshot wire bytes");
var bomb=weatherBytes.ToArray();BinaryPrimitives.WriteInt32LittleEndian(bomb.AsSpan(ProtocolCodec.HeaderBytes),ProtocolCodec.MaxPayloadBytes+1);
Check(!ProtocolCodec.TryDecode(bomb,out _),"decompressed size limit is enforced before allocation");
var wrongRawSize=weatherBytes.ToArray();BinaryPrimitives.WriteInt32LittleEndian(wrongRawSize.AsSpan(ProtocolCodec.HeaderBytes),1);
Check(!ProtocolCodec.TryDecode(wrongRawSize,out _),"compressed content cannot exceed its advertised output size");
var truncatedWeather=weatherBytes[..^1];BinaryPrimitives.WriteInt32LittleEndian(truncatedWeather.AsSpan(16),truncatedWeather.Length-ProtocolCodec.HeaderBytes);
Check(!ProtocolCodec.TryDecode(truncatedWeather,out _),"truncated compressed snapshot is rejected");
var trailingWeather=weatherBytes.Concat(new byte[]{0}).ToArray();BinaryPrimitives.WriteInt32LittleEndian(trailingWeather.AsSpan(16),trailingWeather.Length-ProtocolCodec.HeaderBytes);
Check(!ProtocolCodec.TryDecode(trailingWeather,out _),"compressed snapshot trailing bytes are rejected");
var invalidWeather=weatherBytes.ToArray();Array.Fill(invalidWeather,(byte)255,ProtocolCodec.HeaderBytes+4,invalidWeather.Length-ProtocolCodec.HeaderBytes-4);
Check(!ProtocolCodec.TryDecode(invalidWeather,out _),"malformed compressed snapshot is rejected");
var acceptedWeather=new JoinAccepted(1,Guid.NewGuid(),new(90000,0,0,0,0,"Rookie"),fullWeather);
var acceptedBytes=ProtocolCodec.Encode(MessageKind.JoinAccepted,77,acceptedWeather);
Check(ProtocolCodec.TryDecode(acceptedBytes,out var acceptedDecoded)&&JsonSerializer.Serialize(acceptedWeather,ProtocolCodec.JsonOptions)==JsonSerializer.Serialize(acceptedDecoded!.Get<JoinAccepted>(),ProtocolCodec.JsonOptions),"compressed reliable admission roundtrips complete initial state");

// Known immutable wire fixtures exercise transport fidelity independently of
// the client's atmosphere solver and forecast-generation implementation.
var diagnostic = new AtmosphereDiagnostics(3500, -75, 400, 850, 1100, 11500, 13, 30, 240, 360,
    new(15, 0, 8), 5.6f, 15.6f, 29, 22, 6.5f, .72f, .84f);
var mutableLevels = Enumerable.Range(0, 97).Select(i =>
{
    float height = i * 125;
    return new AtmosphereLevel(height, 1000 * MathF.Exp(-height / 8500), 29 - height * .0065f,
        22 - height * .0063f, 4 + height * .003f, 6 + 7 * MathF.Sin(height / 1500), 29 - height * .0055f);
}).ToArray();
var sounding = ImmutableArray.CreateRange(mutableLevels);
var outlook = ImmutableArray.Create(
    new AtmosphereDayForecast(1, "Moderate", .72f, diagnostic, diagnostic with { CapeJkg = 1200, CinJkg = -130, Stp = 2.1f, VtpGame = 3.8f }),
    new AtmosphereDayForecast(2, "Slight", .33f, diagnostic with { Stp = 1.3f, VtpGame = 2.5f }, diagnostic with { Stp = .4f, VtpGame = .8f }),
    new AtmosphereDayForecast(3, "Quiet / capped", .09f, diagnostic with { CinJkg = -250, Stp = .1f, VtpGame = .2f }, diagnostic with { Stp = 0, VtpGame = 0 }));
var atmosphere = new AtmosphereSnapshot(157, 157, 1, 15.25f, new(456, 0, 678), sounding, diagnostic, outlook);
var parentStorm = environment.ActiveStorms.First();
var tornadoId = parentStorm.Id + "/wire-tornado";
var physicalTornado = new WeatherTornadoState(tornadoId, parentStorm.Id, default, new(100, 0, 200), default,
    100, 220, 800, 1, 1, 1, WindSpeed: 220, TouchdownSpeed: 90, WidthMeters: 250, Stage: 3, Energy: 70,
    TouchdownAmount: 1, FunnelLoweringAmount: 1, WindfieldRadiusMeters: 900, SmoothedWindSpeed: 220, SmoothedWidthMeters: 250);
var physicalFields = new[]
{
    new WeatherSubVortexState(tornadoId + "/physical-sub/0", new(180, 0, 200), .5f, 30, 80, .12f, .32f, .3f, .35f, 1000, 55),
    new WeatherSubVortexState(tornadoId + "/physical-sub/1", new(20, 0, 200), 3.64f, 28, 80, .112f, .32f, .2f, .3f, 1200, 75),
};
var atmosphericWorld = fullWeather with
{
    Environment = environment with { Storms = [parentStorm with { Tornadoes = [physicalTornado], Vorticies = physicalFields, Stage = 3, SmoothedStage = 3.7f }] },
    Atmosphere = atmosphere,
};
mutableLevels[0] = mutableLevels[0] with { TemperatureCelsius = -100 };
Check(atmosphere.Sounding[0].TemperatureCelsius == 29, "immutable authoritative profile cannot change through its original mutable array");
var atmosphereBytes = ProtocolCodec.Encode(MessageKind.WorldSnapshot, 78, atmosphericWorld);
Check(ProtocolCodec.TryDecode(atmosphereBytes, out var atmosphereDecoded) &&
    JsonSerializer.Serialize(atmosphericWorld, ProtocolCodec.JsonOptions) == JsonSerializer.Serialize(atmosphereDecoded!.Get<WorldSnapshot>(), ProtocolCodec.JsonOptions),
    "complete 97-level profile, diagnostics, three-day outlook and physical subvortices roundtrip losslessly");
var decodedAtmosphere = atmosphereDecoded!.Get<WorldSnapshot>().Atmosphere!;
var changedProfile = decodedAtmosphere.Sounding.SetItem(0, decodedAtmosphere.Sounding[0] with { TemperatureCelsius = 30 });
Check(decodedAtmosphere.Sounding[0].TemperatureCelsius == 29 && changedProfile[0].TemperatureCelsius == 30 &&
    decodedAtmosphere.ThreeDayForecast.SequenceEqual(outlook), "decoded profile edits create a new immutable value and retain all three daily forecasts");
var atmosphericAdmission = acceptedWeather with { Snapshot = atmosphericWorld };
var atmosphericAdmissionBytes = ProtocolCodec.Encode(MessageKind.JoinAccepted, 79, atmosphericAdmission);
Check(ProtocolCodec.TryDecode(atmosphericAdmissionBytes, out var atmosphericAdmissionDecoded) &&
    JsonSerializer.Serialize(atmosphericAdmission, ProtocolCodec.JsonOptions) == JsonSerializer.Serialize(atmosphericAdmissionDecoded!.Get<JoinAccepted>(), ProtocolCodec.JsonOptions),
    "reliable admission carries the same complete immutable atmosphere and physical field baseline");
int atmosphericRawBytes = JsonSerializer.SerializeToUtf8Bytes(atmosphericWorld, ProtocolCodec.JsonOptions).Length;
Check(atmosphereBytes.Length < atmosphericRawBytes && atmosphereBytes.Length < ProtocolCodec.MaxPayloadBytes,
    "complete sounding and outlook stay within bounded compressed snapshot wire budget");
byte[] UnvalidatedWeatherJson(string json, MessageKind kind = MessageKind.WorldSnapshot)
{
    var raw = System.Text.Encoding.UTF8.GetBytes(json);
    var buffer = new byte[ProtocolCodec.HeaderBytes + 4 + BrotliEncoder.GetMaxCompressedLength(raw.Length)];
    BinaryPrimitives.WriteUInt32LittleEndian(buffer, ProtocolCodec.Magic);
    BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(4), ProtocolCodec.Version);
    BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(6), (ushort)kind);
    BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(8), 99);
    BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(ProtocolCodec.HeaderBytes), raw.Length);
    if (!BrotliEncoder.TryCompress(raw, buffer.AsSpan(ProtocolCodec.HeaderBytes + 4), out int length, quality: 1, window: 22))
        throw new InvalidOperationException("Invalid-fixture compression failed.");
    BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(16), length + 4);
    return buffer[..(ProtocolCodec.HeaderBytes + 4 + length)];
}
void RejectAtmosphere(AtmosphereSnapshot invalid, string name, bool incoming = true)
{
    bool rejected = false;
    try { ProtocolCodec.Encode(MessageKind.WorldSnapshot, 99, atmosphericWorld with { Atmosphere = invalid }); }
    catch (ArgumentException) { rejected = true; }
    Check(rejected, "outbound atmosphere rejects " + name);
    if (incoming)
        Check(!ProtocolCodec.TryDecode(UnvalidatedWeatherJson(JsonSerializer.Serialize(atmosphericWorld with { Atmosphere = invalid }, ProtocolCodec.JsonOptions)), out _),
            "inbound compressed atmosphere rejects " + name);
}
RejectAtmosphere(atmosphere with { Diagnostics = diagnostic with { CapeJkg = float.NaN } }, "nonfinite parcel energy", false);
RejectAtmosphere(atmosphere with { Sounding = sounding.SetItem(7, sounding[7] with { WindEastMetersPerSecond = float.PositiveInfinity }) }, "nonfinite sounding wind", false);
RejectAtmosphere(atmosphere with { Diagnostics = diagnostic with { Stp = 1e30f } }, "unbounded positive STP");
RejectAtmosphere(atmosphere with { Diagnostics = diagnostic with { VtpGame = 1e30f } }, "unbounded positive gameplay VTP");
RejectAtmosphere(atmosphere with { Diagnostics = diagnostic with { Stp = -1 } }, "negative STP");
RejectAtmosphere(atmosphere with { Diagnostics = diagnostic with { TornadoPotential01 = 1.01f } }, "out-of-range tornado potential");
RejectAtmosphere(atmosphere with { ThreeDayForecast = outlook.SetItem(1, outlook[1] with { Risk = -.1f }) }, "negative forecast risk");
RejectAtmosphere(atmosphere with { ThreeDayForecast = outlook.SetItem(1, outlook[1] with { Risk = 1.1f }) }, "forecast risk above one");
RejectAtmosphere(atmosphere with { ThreeDayForecast = outlook.SetItem(2, outlook[2] with { Peak = diagnostic with { VtpGame = 1e20f } }) }, "unbounded nested forecast composite");
RejectAtmosphere(atmosphere with { Sounding = sounding.SetItem(8, sounding[8] with { HeightMeters = sounding[7].HeightMeters }) }, "duplicate profile heights");
RejectAtmosphere(atmosphere with { Sounding = sounding.SetItem(8, sounding[8] with { PressureHpa = sounding[7].PressureHpa + 1 }) }, "pressure increasing with height");
RejectAtmosphere(atmosphere with { Sounding = sounding.SetItem(0, sounding[0] with { HeightMeters = 1 }) }, "profile not starting at ground level");
RejectAtmosphere(atmosphere with { Sounding = sounding.Take(40).ToImmutableArray() }, "profile missing the six-kilometer hodograph layer");
RejectAtmosphere(atmosphere with { Sounding = Enumerable.Range(0, 129).Select(i => sounding[Math.Min(i, sounding.Length - 1)]).ToImmutableArray() }, "oversized vertical profile");
RejectAtmosphere(atmosphere with { Sounding = sounding.SetItem(8, sounding[8] with { DewPointCelsius = sounding[8].TemperatureCelsius + 1 }) }, "supersaturated environmental temperature");
RejectAtmosphere(atmosphere with { Sounding = sounding.SetItem(8, sounding[8] with { WindNorthMetersPerSecond = 1e30f }) }, "unbounded finite profile wind");
RejectAtmosphere(atmosphere with { Sounding = sounding.SetItem(8, null!) }, "null vertical level");
RejectAtmosphere(atmosphere with { ThreeDayForecast = outlook.Add(outlook[2] with { DayNumber = 4 }) }, "more than three forecast days");
RejectAtmosphere(atmosphere with { ThreeDayForecast = outlook.SetItem(2, outlook[2] with { DayNumber = 2 }) }, "duplicated or unordered outlook days");
RejectAtmosphere(atmosphere with { Sounding = default }, "uninitialized immutable profile", false);
RejectAtmosphere(atmosphere with { ThreeDayForecast = default }, "uninitialized immutable outlook", false);
RejectAtmosphere(atmosphere with { ModelVersion = 2 }, "unsupported deterministic atmosphere model");
RejectAtmosphere(atmosphere with { CampaignStartHour = 24 }, "invalid reconstruction clock origin");
RejectAtmosphere(atmosphere with { CampaignDayLengthSeconds = 0 }, "invalid reconstruction clock rate");
var overflowJson = JsonSerializer.Serialize(atmosphericWorld, ProtocolCodec.JsonOptions).Replace("\"capeJkg\":3500", "\"capeJkg\":1e999", StringComparison.Ordinal);
Check(!ProtocolCodec.TryDecode(UnvalidatedWeatherJson(overflowJson), out _), "inbound floating-point overflow cannot bypass finite atmosphere validation");
var legacyWeatherJson = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(fullWeather, ProtocolCodec.JsonOptions))!;
legacyWeatherJson.AsObject().Remove("atmosphere");
Check(ProtocolCodec.TryDecode(UnvalidatedWeatherJson(legacyWeatherJson.ToJsonString()), out var legacyWeather) && legacyWeather!.Get<WorldSnapshot>().Atmosphere is null,
    "protocol version three accepts lean snapshots that omit the optional atmosphere extension");
var noOutlookBytes = ProtocolCodec.Encode(MessageKind.WorldSnapshot, 99, atmosphericWorld with { Atmosphere = atmosphere with { ThreeDayForecast = [] } });
Check(ProtocolCodec.TryDecode(noOutlookBytes, out var noOutlookDecoded) && noOutlookDecoded!.Get<WorldSnapshot>().Atmosphere!.ThreeDayForecast.IsEmpty,
    "an intentionally omitted cached outlook remains a valid atmosphere update");
var profileLaneBytes = ProtocolCodec.Encode(MessageKind.AtmosphereSnapshot, 100, atmosphere);
Check(ProtocolCodec.TryDecode(profileLaneBytes, out var profileLaneDecoded) &&
    JsonSerializer.Serialize(profileLaneDecoded!.Get<AtmosphereSnapshot>(), ProtocolCodec.JsonOptions) == JsonSerializer.Serialize(atmosphere, ProtocolCodec.JsonOptions),
    "slow reliable atmosphere lane preserves all 97 levels and three forecast days losslessly");
Check(!ProtocolCodec.TryDecode(UnvalidatedWeatherJson(JsonSerializer.Serialize(atmosphere with { Diagnostics = diagnostic with { Stp = 1e30f } }, ProtocolCodec.JsonOptions), MessageKind.AtmosphereSnapshot), out _),
    "dedicated atmosphere lane enforces the same diagnostic bounds before publishing a profile");
var profileV2 = profileLaneBytes.ToArray(); BinaryPrimitives.WriteUInt16LittleEndian(profileV2.AsSpan(4), 2);
Check(!ProtocolCodec.TryDecode(profileV2, out _), "version two cannot silently accept the new mandatory atmosphere lane");
var fastWorld = atmosphericWorld with { Atmosphere = null };
int leanWorldWireBytes = ProtocolCodec.Encode(MessageKind.WorldSnapshot, 100, fastWorld).Length;
int oldRepeatedWeatherBytesPerSecond = atmosphereBytes.Length * 10;
double slowLaneWeatherBytesPerSecond = leanWorldWireBytes * 10 + profileLaneBytes.Length / atmosphere.CampaignDayLengthSeconds;
Check(slowLaneWeatherBytesPerSecond < oldRepeatedWeatherBytesPerSecond * .5,
    "one full reliable profile per day reduces steady wire bandwidth while preserving the full sounding");

ushort port;
using (var reserve = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0))) port = (ushort)((IPEndPoint)reserve.Client.LocalEndPoint!).Port;
using var server = StormTransportHost.CreateServer(port);
using var clientA = StormTransportHost.CreateClient();
using var clientB = StormTransportHost.CreateClient();
var a = clientA.Connect("127.0.0.1", port);
var b = clientB.Connect("127.0.0.1", port);
var serverConnections = new List<uint>();
var receivedA = new List<ProtocolMessage>();
var receivedB = new List<ProtocolMessage>();
var receivedServer = new List<ProtocolMessage>();
void Poll()
{
    foreach (var e in server.Poll()) { if (e.Kind == TransportEventKind.Connected) serverConnections.Add(e.ConnectionId); if (e.Message is not null) receivedServer.Add(e.Message); if (e.Kind == TransportEventKind.Fault) throw new Exception(e.Reason); }
    foreach (var e in clientA.Poll()) { if (e.Message is not null) receivedA.Add(e.Message); if (e.Kind == TransportEventKind.Fault) throw new Exception(e.Reason); }
    foreach (var e in clientB.Poll()) { if (e.Message is not null) receivedB.Add(e.Message); if (e.Kind == TransportEventKind.Fault) throw new Exception(e.Reason); }
}
Wait(() => serverConnections.Count == 2 && clientA.ConnectionCount == 1 && clientB.ConnectionCount == 1, Poll, "real IP server + two clients connect");
var status = clientA.GetConnectionStatus(a);
Console.WriteLine(status);
Check(status.Contains("Encrypted: true", StringComparison.Ordinal), "native encryption required and active");
Check(clientA.Send(a, MessageKind.JoinRequest, new JoinRequest("admission-A")) && clientB.Send(b, MessageKind.JoinRequest, new JoinRequest("admission-B")), "reliable admission queued");
Wait(() => receivedServer.Count(x => x.Kind == MessageKind.JoinRequest) == 2, Poll, "reliable admission received independently");
foreach (var connection in serverConnections) Check(server.Send(connection, MessageKind.JoinAccepted, atmosphericAdmission), "complete atmospheric admission queued over native encrypted transport");
Wait(() => receivedA.Any(x => x.Kind == MessageKind.JoinAccepted) && receivedB.Any(x => x.Kind == MessageKind.JoinAccepted), Poll,
    "both native clients receive complete atmospheric admission");
var nativeAtmosphereA = receivedA.Last(x => x.Kind == MessageKind.JoinAccepted).Get<JoinAccepted>().Snapshot;
var nativeAtmosphereB = receivedB.Last(x => x.Kind == MessageKind.JoinAccepted).Get<JoinAccepted>().Snapshot;
Check(JsonSerializer.Serialize(nativeAtmosphereA, ProtocolCodec.JsonOptions) == JsonSerializer.Serialize(atmosphericWorld, ProtocolCodec.JsonOptions) &&
    JsonSerializer.Serialize(nativeAtmosphereA, ProtocolCodec.JsonOptions) == JsonSerializer.Serialize(nativeAtmosphereB, ProtocolCodec.JsonOptions),
    "two native clients see identical complete server profile, diagnostics, environment and physical subvortices");
var atmosphereReplicaA = new AuthoritativeAtmosphereReplica(); var atmosphereReplicaB = new AuthoritativeAtmosphereReplica();
atmosphereReplicaA.Retain(nativeAtmosphereA); atmosphereReplicaB.Retain(nativeAtmosphereB);
var updatedAtmosphericWorld = fastWorld with { ServerTick = 158 };
foreach (var connection in serverConnections) Check(server.Send(connection, MessageKind.WorldSnapshot, updatedAtmosphericWorld, reliable: false),
    "lean world update queued on native unreliable snapshot channel");
Wait(() => receivedA.Any(x => x.Kind == MessageKind.WorldSnapshot) && receivedB.Any(x => x.Kind == MessageKind.WorldSnapshot), Poll,
    "native unreliable world updates omit the slowly changing profile for both clients");
var fastNativeA = receivedA.Last(x => x.Kind == MessageKind.WorldSnapshot).Get<WorldSnapshot>();
var fastNativeB = receivedB.Last(x => x.Kind == MessageKind.WorldSnapshot).Get<WorldSnapshot>();
Check(fastNativeA.Atmosphere is null && fastNativeB.Atmosphere is null &&
    JsonSerializer.Serialize(atmosphereReplicaA.Retain(fastNativeA), ProtocolCodec.JsonOptions) ==
    JsonSerializer.Serialize(atmosphereReplicaB.Retain(fastNativeB), ProtocolCodec.JsonOptions),
    "first lean native snapshot retains the identical authoritative admission profile and physical windfield");
updatedAtmosphericWorld = updatedAtmosphericWorld with { ServerTick = 159 };
foreach (var connection in serverConnections) Check(server.Send(connection, MessageKind.WorldSnapshot, updatedAtmosphericWorld, reliable: false),
    "second lean native world snapshot is queued without duplicating the sounding");
Wait(() => receivedA.Count(x => x.Kind == MessageKind.WorldSnapshot) >= 2 && receivedB.Count(x => x.Kind == MessageKind.WorldSnapshot) >= 2, Poll,
    "two consecutive lean world updates reach each native client");
Check(atmosphereReplicaA.Retain(receivedA.Last(x => x.Kind == MessageKind.WorldSnapshot).Get<WorldSnapshot>()).Atmosphere!.Sounding.SequenceEqual(sounding) &&
    atmosphereReplicaB.Retain(receivedB.Last(x => x.Kind == MessageKind.WorldSnapshot).Get<WorldSnapshot>()).Atmosphere!.ThreeDayForecast.SequenceEqual(outlook),
    "two null-profile native snapshots preserve the cached full sounding and outlook without local regeneration");
var updatedAtmosphere = atmosphere with { ServerTick = 160, Hour = 15.3f };
StormTransportHost.ConfigureNetworkSimulation(5, 40);
foreach (var connection in serverConnections) Check(server.Send(connection, MessageKind.AtmosphereSnapshot, updatedAtmosphere),
    "daily complete atmosphere baseline is queued reliably under five percent packet loss");
Wait(() => receivedA.Any(x => x.Kind == MessageKind.AtmosphereSnapshot) && receivedB.Any(x => x.Kind == MessageKind.AtmosphereSnapshot), Poll,
    "slow reliable full profiles reach both native clients under simulated loss and lag", 20000);
Check(atmosphereReplicaA.Apply(receivedA.Last(x => x.Kind == MessageKind.AtmosphereSnapshot).Get<AtmosphereSnapshot>()) &&
    atmosphereReplicaB.Apply(receivedB.Last(x => x.Kind == MessageKind.AtmosphereSnapshot).Get<AtmosphereSnapshot>()) &&
    JsonSerializer.Serialize(atmosphereReplicaA.Current, ProtocolCodec.JsonOptions) == JsonSerializer.Serialize(atmosphereReplicaB.Current, ProtocolCodec.JsonOptions),
    "both clients adopt the identical newer reliable server profile");
foreach (var connection in serverConnections) Check(server.Send(connection, MessageKind.AtmosphereSnapshot, atmosphere with { ServerTick = 150, Hour = 14 }),
    "delayed stale atmosphere fixture is delivered over the real reliable lane");
Wait(() => receivedA.Count(x => x.Kind == MessageKind.AtmosphereSnapshot) >= 2 && receivedB.Count(x => x.Kind == MessageKind.AtmosphereSnapshot) >= 2, Poll,
    "out-of-order profile timestamps reach both native replicas", 20000);
Check(!atmosphereReplicaA.Apply(receivedA.Last(x => x.Kind == MessageKind.AtmosphereSnapshot).Get<AtmosphereSnapshot>()) &&
    !atmosphereReplicaB.Apply(receivedB.Last(x => x.Kind == MessageKind.AtmosphereSnapshot).Get<AtmosphereSnapshot>()) &&
    atmosphereReplicaA.Current!.ServerTick == 160 && atmosphereReplicaB.Current!.Hour == 15.3f,
    "stale reliable profiles cannot roll back the cached authoritative weather");
Check(!atmosphereReplicaA.Apply(updatedAtmosphere with { Hour = 14 }) &&
    atmosphereReplicaA.Retain(updatedAtmosphericWorld with { Atmosphere = atmosphere }).Atmosphere!.ServerTick == 160,
    "duplicate or old embedded profiles cannot replace the newer reliable cache");
StormTransportHost.ConfigureNetworkSimulation();
updatedAtmosphericWorld = updatedAtmosphericWorld with { ServerTick = 160, Atmosphere = updatedAtmosphere };

var bodies = Enumerable.Range(1, 600).Select(i => new DebrisBodyState(i, i / 10, new(i, 5, -i), NetQuaternion.Identity, new(4, 6, 8), new(0, 0.5f, 0), i % 2 == 0, true)).ToArray();
var replicaA = new AuthoritativeDebrisReplica(); var replicaB = new AuthoritativeDebrisReplica();
var mappedBodies = bodies[..4].Select(x => x with { StablePartId = "part/" + x.BodyId }).ToArray();
var chunk0 = new DebrisSnapshot(10, 1, true, mappedBodies[..2], 0, 2); var chunk1 = new DebrisSnapshot(10, 1, true, mappedBodies[2..], 1, 2);
Check(replicaA.Apply(chunk1) == DebrisApplyResult.Buffered && !replicaA.CompletedBaseline && replicaA.Bodies.Count == 0, "partial baseline never exposes collision catalogue");
var newerMotion = new DebrisSnapshot(20, 1, false, [mappedBodies[0] with { Position = new(50, 0, 80), StablePartId = "" }]);
replicaA.Apply(newerMotion); replicaB.Apply(newerMotion); replicaB.Apply(chunk0);
Check(replicaA.Apply(chunk0) == DebrisApplyResult.BaselineCommitted && replicaB.Apply(chunk1) == DebrisApplyResult.BaselineCommitted, "reordered chunks publish coherent baselines");
Check(replicaA.Bodies.SequenceEqual(replicaB.Bodies) && replicaA.Bodies.Single(x => x.BodyId == 1).Position == new NetVector3(50, 0, 80) && replicaA.StableMappings[1] == "part/1", "clients overlay newer buffered motion without losing stable identity");
var frozenView = replicaA.Bodies;
Check(replicaA.Apply(new DebrisSnapshot(15, 1, false, [mappedBodies[0]])) == DebrisApplyResult.Buffered && replicaA.Bodies.Single(x => x.BodyId == 1).Position.X == 50, "old per-body motion cannot roll back position");
var collected = mappedBodies[0] with { Position = new(50, 0, 80), LinearVelocity = default, AngularVelocity = default, Sleeping = true, Archived = true, CollisionEnabled = false, Collected = true };
var tombstone = new DebrisEvent(1, 2, DebrisEventKind.Removed, collected);
Check(replicaA.Apply(tombstone) == DebrisApplyResult.Applied && replicaA.Bodies.Single(x => x.BodyId == 1).Collected && frozenView.Single(x => x.BodyId == 1).Collected == false, "collected tombstone hides physical part and previous snapshot remains immutable");
replicaB.Apply(tombstone); replicaA.Apply(new DebrisSnapshot(30, 2, false, [mappedBodies[0] with { StablePartId = "" }]));
Check(replicaA.Bodies.Single(x => x.BodyId == 1).Collected && replicaA.StableMappings[1] == "part/1", "later motion cannot resurrect collected wall");
var lateReplica = new AuthoritativeDebrisReplica(); var collectedBaseline = new DebrisSnapshot(30, 2, true, [collected, .. mappedBodies[1..]]);
Check(lateReplica.Apply(collectedBaseline) == DebrisApplyResult.BaselineCommitted && lateReplica.Bodies.Single(x => x.BodyId == 1).Collected && !lateReplica.Bodies.Single(x => x.BodyId == 1).CollisionEnabled, "late join preserves permanent collected part tombstone");
var tombstoneWire = ProtocolCodec.Encode(MessageKind.DebrisSnapshot, 1, collectedBaseline);
Check(ProtocolCodec.TryDecode(tombstoneWire, out var decodedTombstone) && decodedTombstone!.Get<DebrisSnapshot>().Bodies[0].Collected, "compact binary collected tombstone roundtrip");
Check(lateReplica.Apply(new DebrisSnapshot(31, 2, false, [collected with { CollisionEnabled = true }])) == DebrisApplyResult.Invalid, "collected collision invariant enforced");
var immutableBeforeInvalid = lateReplica.Bodies;
Check(lateReplica.Apply(new DebrisSnapshot(32, 2, false, [mappedBodies[1] with { Position = new(99, 0, 0) }, mappedBodies[2] with { StablePartId = "wrong/part" }])) == DebrisApplyResult.Invalid && lateReplica.Bodies.SequenceEqual(immutableBeforeInvalid), "invalid mixed motion batch is rejected atomically");
lateReplica.Reset(); Check(!lateReplica.CompletedBaseline && lateReplica.Bodies.Count == 0, "replica reset gates collision activation again");
var baseline = new DebrisSnapshot(10, 1, true, bodies);
var motionPacket = ProtocolCodec.Encode(MessageKind.DebrisSnapshot, 1, new DebrisSnapshot(10, 1, false, bodies[..12]));
Check(motionPacket.Length < 1100, "compact 12-body motion packet fits one native datagram");
Check(ProtocolCodec.TryDecode(motionPacket, out var decodedMotion) && decodedMotion!.Get<DebrisSnapshot>().Bodies.SequenceEqual(bodies[..12]), "compact body pose codec roundtrip");
var corruptMotion = motionPacket[..^1]; BinaryPrimitives.WriteInt32LittleEndian(corruptMotion.AsSpan(16), corruptMotion.Length - ProtocolCodec.HeaderBytes);
Check(!ProtocolCodec.TryDecode(corruptMotion, out _), "truncated binary body pose rejected");
var invalidFloatMotion = motionPacket.ToArray(); BinaryPrimitives.WriteSingleLittleEndian(invalidFloatMotion.AsSpan(ProtocolCodec.HeaderBytes + 23 + 8), float.NaN);
Check(!ProtocolCodec.TryDecode(invalidFloatMotion, out _), "nonfinite binary body position rejected");
foreach (var connection in serverConnections) Check(server.Send(connection, MessageKind.DebrisSnapshot, baseline), "reliable large baseline queued");
Wait(() => receivedA.Any(x => x.Kind == MessageKind.DebrisSnapshot) && receivedB.Any(x => x.Kind == MessageKind.DebrisSnapshot), Poll, "fragmented debris baseline reaches both clients");
var baselineA = receivedA.Last(x => x.Kind == MessageKind.DebrisSnapshot).Get<DebrisSnapshot>();
var baselineB = receivedB.Last(x => x.Kind == MessageKind.DebrisSnapshot).Get<DebrisSnapshot>();
Check(baselineA.Bodies.SequenceEqual(baselineB.Bodies) && baselineA.Bodies.SequenceEqual(bodies), "600 physical body poses / velocities / sleeping states identical");
for (var i = 0; i < 48; i++) Check(server.Send(serverConnections[0], MessageKind.DebrisSnapshot, baseline with { ServerTick = 20 + i }), "reliable congestion burst queued " + i);
Wait(() => receivedA.Count(x => x.Kind == MessageKind.DebrisSnapshot && x.Get<DebrisSnapshot>().ServerTick >= 20) == 48, Poll, "bounded native send buffer retries reliable backlog without loss", 20000);

StormTransportHost.ConfigureNetworkSimulation(5, 40);
for (var i = 0; i < 64; i++) foreach (var connection in serverConnections) Check(server.Send(connection, MessageKind.DebrisEvent, new DebrisEvent(i, 2, DebrisEventKind.Released, bodies[i])), "reliable release queued " + i);
Wait(() => receivedA.Count(x => x.Kind == MessageKind.DebrisEvent) == 64 && receivedB.Count(x => x.Kind == MessageKind.DebrisEvent) == 64, Poll, "reliable destruction converges under loss and lag", 20000);
Check(receivedA.Where(x => x.Kind == MessageKind.DebrisEvent).Select(x => x.Get<DebrisEvent>().EventId).SequenceEqual(Enumerable.Range(0, 64).Select(x => (long)x)), "reliable release order preserved");
Check(receivedB.Where(x => x.Kind == MessageKind.DebrisEvent).Select(x => x.Get<DebrisEvent>()).SequenceEqual(receivedA.Where(x => x.Kind == MessageKind.DebrisEvent).Select(x => x.Get<DebrisEvent>())), "both clients receive identical authoritative release events");
StormTransportHost.ConfigureNetworkSimulation();

var settled = bodies.Select(x => x with { Position = new(x.BodyId, 0, -x.BodyId), LinearVelocity = default, AngularVelocity = default, Sleeping = true }).ToArray();
var motion = new DebrisSnapshot(100, 2, false, settled[..8]);
foreach (var connection in serverConnections) Check(server.Send(connection, MessageKind.DebrisSnapshot, motion, reliable: false), "unreliable motion queued");
Wait(() => receivedA.Any(x => x.Kind == MessageKind.DebrisSnapshot && x.Get<DebrisSnapshot>().ServerTick == 100) && receivedB.Any(x => x.Kind == MessageKind.DebrisSnapshot && x.Get<DebrisSnapshot>().ServerTick == 100), Poll, "unreliable body motion delivered");
Check(receivedA.Last(x => x.Kind == MessageKind.DebrisSnapshot).Get<DebrisSnapshot>().Bodies.SequenceEqual(receivedB.Last(x => x.Kind == MessageKind.DebrisSnapshot).Get<DebrisSnapshot>().Bodies), "settled authoritative poses identical");
using var lateClient = StormTransportHost.CreateClient();
var lateConnection = lateClient.Connect("127.0.0.1", port);
Wait(() => serverConnections.Count == 3 && lateClient.ConnectionCount == 1, Poll, "late client connects");
Check(server.Send(serverConnections[^1], MessageKind.DebrisSnapshot, new DebrisSnapshot(100, 2, true, settled)), "late join settled baseline queued");
DebrisSnapshot? lateBaseline = null;
Check(server.Send(serverConnections[^1], MessageKind.JoinAccepted, atmosphericAdmission with { PlayerId = 3, Snapshot = updatedAtmosphericWorld }),
    "late native client receives current atmospheric admission rather than a locally regenerated forecast");
WorldSnapshot? lateAtmosphericWorld = null;
Wait(() => lateBaseline is not null && lateAtmosphericWorld is not null, () =>
{
    Poll();
    foreach (var e in lateClient.Poll())
    {
        if (e.Message?.Kind == MessageKind.DebrisSnapshot) lateBaseline = e.Message.Get<DebrisSnapshot>();
        if (e.Message?.Kind == MessageKind.JoinAccepted) lateAtmosphericWorld = e.Message.Get<JoinAccepted>().Snapshot;
    }
}, "late join physical and atmospheric baselines delivered");
Check(lateBaseline!.FullBaseline && lateBaseline.BaselineRevision == 2 && lateBaseline.Bodies.SequenceEqual(settled), "late join collision world agrees on all settled fragments");
Check(JsonSerializer.Serialize(lateAtmosphericWorld, ProtocolCodec.JsonOptions) == JsonSerializer.Serialize(updatedAtmosphericWorld, ProtocolCodec.JsonOptions),
    "late native join preserves the exact latest server sounding, three-day outlook and physical subvortices");
clientA.Close(a, "loopback done");
Wait(() => clientA.ConnectionCount == 0 && server.ConnectionCount == 2, Poll, "clean peer disconnect lifecycle");
lateClient.Dispose(); clientA.Dispose(); clientB.Dispose(); server.Dispose();

var certificateIdentity = TransportIdentity.Create();
var differentAuthority = TransportIdentity.Create();
var identityPath = Path.Combine(Path.GetTempPath(), "stormchase-native-test-" + Guid.NewGuid().ToString("N") + ".json");
var markerPath = identityPath + ".process";
File.WriteAllText(identityPath, JsonSerializer.Serialize(certificateIdentity));
using (var reserve = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0))) port = (ushort)((IPEndPoint)reserve.Client.LocalEndPoint!).Port;
var start = new ProcessStartInfo("C:/Users/reviv/.cache/mmoi-dotnet/10.0.401/dotnet.exe") { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true };
start.ArgumentList.Add(typeof(TransportEvent).Assembly.Location.Replace("StormChase.Networking.dll", "StormChase.Networking.Tests.dll"));
start.ArgumentList.Add("--certificate-server"); start.ArgumentList.Add(identityPath); start.ArgumentList.Add(port.ToString()); start.ArgumentList.Add(markerPath);
using var secureServer = Process.Start(start) ?? throw new InvalidOperationException("Cannot start certificate server test.");
var secureStdout = secureServer.StandardOutput.ReadToEndAsync(); var secureStderr = secureServer.StandardError.ReadToEndAsync();
try
{
    Wait(() => File.Exists(markerPath + ".ready"), () => { if (secureServer.HasExited) throw new Exception(secureStderr.Result); }, "separate process certificate server starts");
    using (var secureClient = StormTransportHost.CreateClient(certificateIdentity.ClientSecurity))
    {
        var secureConnection = secureClient.Connect("127.0.0.1", port);
        var connected = false; var pong = false;
        Wait(() => connected, () => { foreach (var e in secureClient.Poll()) { if (e.Kind == TransportEventKind.Connected) connected = true; if (e.Kind is TransportEventKind.Fault or TransportEventKind.Disconnected) throw new Exception(e.Reason); } }, "trusted certificate native handshake accepts exact server identity", 15000);
        var secureStatus = secureClient.GetConnectionStatus(secureConnection);
        Console.WriteLine(secureStatus);
        Check(secureStatus.Contains("Peer certificate authenticated: true", StringComparison.Ordinal), "native server certificate authenticated by pinned authority");
        try { using var crossDomain = StormTransportHost.CreateClient(differentAuthority.ClientSecurity); Check(false, "simultaneous cross authority trust prohibited"); }
        catch (InvalidOperationException) { Check(true, "simultaneous cross authority trust prohibited"); }
        Check(secureClient.Send(secureConnection, MessageKind.Ping, new Ping(999)), "authenticated application packet queued");
        Wait(() => pong, () => { foreach (var e in secureClient.Poll()) if (e.Message?.Kind == MessageKind.Pong) pong = e.Message.Get<Ping>().Timestamp == 999; }, "authenticated reliable roundtrip");
    }
    using (var wrongName = StormTransportHost.CreateClient(certificateIdentity.ClientSecurity with { ExpectedPeerIdentity = "stormchase/wrong-name" }))
    {
        wrongName.Connect("127.0.0.1", port); var rejected = false; var accepted = false;
        Wait(() => rejected, () => { foreach (var e in wrongName.Poll()) { accepted |= e.Kind == TransportEventKind.Connected; rejected |= e.Kind is TransportEventKind.Fault or TransportEventKind.Disconnected; } }, "wrong native server identity rejected");
        Check(!accepted, "identity mismatch never exposes Connected to game");
    }
    using (var wrongRoot = StormTransportHost.CreateClient(differentAuthority.ClientSecurity with { ExpectedPeerIdentity = certificateIdentity.Identity }))
    {
        wrongRoot.Connect("127.0.0.1", port); var rejected = false; var accepted = false;
        Wait(() => rejected, () => { foreach (var e in wrongRoot.Poll()) { accepted |= e.Kind == TransportEventKind.Connected; rejected |= e.Kind is TransportEventKind.Fault or TransportEventKind.Disconnected; } }, "untrusted native certificate authority rejected");
        Check(!accepted, "untrusted authority never exposes Connected to game");
    }
}
finally
{
    File.WriteAllText(markerPath + ".stop", "stop");
    if (!secureServer.WaitForExit(5000)) secureServer.Kill(entireProcessTree: true);
    Console.WriteLine("Certificate test server diagnostics: " + secureStderr.GetAwaiter().GetResult());
    foreach (var path in new[] { identityPath, markerPath + ".ready", markerPath + ".stop" }) if (File.Exists(path)) File.Delete(path);
}
File.WriteAllText(identityPath, JsonSerializer.Serialize(differentAuthority));
using var switchedServer = Process.Start(start) ?? throw new InvalidOperationException("Cannot start switched server test.");
var switchedStderr = switchedServer.StandardError.ReadToEndAsync(); var switchedStdout = switchedServer.StandardOutput.ReadToEndAsync();
try
{
    Wait(() => File.Exists(markerPath + ".ready"), () => { if (switchedServer.HasExited) throw new Exception(switchedStderr.Result); }, "second dedicated server certificate starts");
    using var switchedClient = StormTransportHost.CreateClient(differentAuthority.ClientSecurity);
    var switchedConnection = switchedClient.Connect("127.0.0.1", port); var connected = false;
    Wait(() => connected, () => { foreach (var e in switchedClient.Poll()) { if (e.Kind == TransportEventKind.Connected) connected = true; if (e.Kind is TransportEventKind.Fault or TransportEventKind.Disconnected) throw new Exception(e.Reason); } }, "zero-host trust reset allows switching to new server authority");
    Check(switchedClient.GetConnectionStatus(switchedConnection).Contains("Peer certificate authenticated: true", StringComparison.Ordinal), "switched server identity remains authenticated");
}
finally
{
    File.WriteAllText(markerPath + ".stop", "stop"); if (!switchedServer.WaitForExit(5000)) switchedServer.Kill(entireProcessTree: true);
    foreach (var path in new[] { identityPath, markerPath + ".ready", markerPath + ".stop" }) if (File.Exists(path)) File.Delete(path);
}
var report = JsonSerializer.Serialize(new { checks = passed.Count, passed = true, native = "GameNetworkingSockets v1.6.1", encrypted = true,
    physicalBodies = 600, clients = 3, packetLossPercent = 5, simulatedOneWayLagMs = 40, atmosphereLevels = sounding.Length,
    forecastDays = outlook.Length, weatherWireBytes = weatherBytes.Length, atmosphericWorldRawBytes = atmosphericRawBytes,
    atmosphericWorldWireBytes = atmosphereBytes.Length, atmosphericAdmissionWireBytes = atmosphericAdmissionBytes.Length,
    protocolVersion = ProtocolCodec.Version, leanWorldWireBytes, profileLaneWireBytes = profileLaneBytes.Length,
    oldRepeatedWeatherBytesPerSecond, slowLaneWeatherBytesPerSecond,
    atmosphereNativeClientsIdentical = true, atmosphereLateJoinIdentical = true, CheckNames = passed }, new JsonSerializerOptions { WriteIndented = true });
Console.WriteLine(report);
int outputArgument = Array.IndexOf(args, "--output");
if (outputArgument >= 0 && outputArgument + 1 < args.Length)
{
    string output = Path.GetFullPath(args[outputArgument + 1]); Directory.CreateDirectory(output);
    File.WriteAllText(Path.Combine(output, "network-protocol-proof.json"), report);
}
