using System.Collections.Concurrent;
using System.Net.Http.Json;
using Engine.Environment;
using LifeCare.Construction;
using Shared.Contracts.World;

namespace StormChase.Server;

/// <summary>
/// LifeCare lots on the district host. The host reads every lot from the Construction service (internal route, service
/// key) and registers each piece as a PhysX destructible part, so tornadoes tear roofs and walls off player-built rooms
/// with the same release, flight and replication as authored buildings: every player in the district sees the same debris.
/// </summary>
public sealed record CharacterPositionDto(float X, float Y, float Z, float? LotBase);

public sealed partial class GameAuthority
{
    private readonly ConcurrentQueue<LotDocument> lotUpdates = new();
    private readonly Dictionary<string, long> lotRevisions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, float> lotBases = new(StringComparer.Ordinal);
    private int nextStablePartId;
    private PmWeatherSupercellSimulation? testStorm;
    private double testStormSecondsLeft;

    /// <summary>Polls the Construction service for lot revisions; runs beside the tick loop and never blocks it.</summary>
    private async Task PollLotsAsync(CancellationToken stop)
    {
        if (options.ConstructionUrl is null) return;
        string key = Environment.GetEnvironmentVariable(options.ConstructionKeyEnvironment ?? options.MasterKeyEnvironment ?? "LIFECARE_MASTER_KEY") ?? "";
        using var http = new HttpClient { BaseAddress = new(options.ConstructionUrl + "/"), Timeout = TimeSpan.FromSeconds(10) };
        http.DefaultRequestHeaders.Add("X-LifeCare-Service", key);
        var seen = new Dictionary<string, long>(StringComparer.Ordinal);
        bool reported = false;
        Console.WriteLine("Lots from the Construction service at " + options.ConstructionUrl + ".");
        while (!stop.IsCancellationRequested)
        {
            try
            {
                var lots = await http.GetFromJsonAsync<LotSummary[]>("api/internal/lots", ConstructionClient.Json, stop) ?? [];
                foreach (var summary in lots)
                {
                    if (seen.TryGetValue(summary.Lot.LotId, out long revision) && revision >= summary.Revision) continue;
                    var document = await http.GetFromJsonAsync<LotDocument>("api/internal/lots/" + Uri.EscapeDataString(summary.Lot.LotId), ConstructionClient.Json, stop);
                    if (document is null) continue;
                    seen[document.Lot.LotId] = document.Revision; lotUpdates.Enqueue(document);
                }
                reported = false;
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
            catch (Exception error) { if (!reported) Console.WriteLine("Construction service unavailable: " + error.Message); reported = true; }
            try { await Task.Delay(TimeSpan.FromSeconds(options.LotPollSeconds), stop); } catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>On the tick, under the gate: turns new lot revisions into destructible parts.</summary>
    private void ApplyLotUpdates()
    {
        if (world is null || destruction is null) return;
        if (nextStablePartId == 0) nextStablePartId = stablePartIds.Count;
        while (lotUpdates.TryDequeue(out var document))
        {
            string lotId = document.Lot.LotId;
            if (lotRevisions.TryGetValue(lotId, out long known) && known >= document.Revision) continue;
            // The base is fixed by the deed and the world's terrain, so the host and every client agree on it.
            if (!lotBases.TryGetValue(lotId, out float baseY)) lotBases[lotId] = baseY = LotGeometry.BaseElevation(document.Lot, world.HeightAt);
            var pieces = LotGeometry.Build(document, baseY);
            foreach (var piece in pieces) if (!stablePartIds.ContainsKey(piece.PieceId)) stablePartIds.Add(piece.PieceId, ++nextStablePartId);
            destruction.SetExternalParts("lot/" + lotId, pieces.Select(StormChaseLotParts.Destructible));
            lotRevisions[lotId] = document.Revision; RememberLotCenter(document);
            Console.WriteLine($"Lot {lotId} revision {document.Revision}: {pieces.Count} pieces in the district's physics.");
        }
    }

    /// <summary>A connected character's feet, and the base height of the lot asked about (if this host has it).</summary>
    public CharacterPositionDto? CharacterPosition(string characterId, string? lotId)
    {
        lock (gate)
        {
            var player = players.Values.FirstOrDefault(p => p.Character.CharacterId == characterId);
            if (player is null) return null;
            float? lotBase = lotId is not null && lotBases.TryGetValue(lotId, out float b) ? b : null;
            return new(player.Feet.X, player.Feet.Y, player.Feet.Z, lotBase);
        }
    }

    /// <summary>The lots this host has in its physics, with the revision each reflects.</summary>
    public IReadOnlyDictionary<string, long> LotRevisions() { lock (gate) return new Dictionary<string, long>(lotRevisions); }

    /// <summary>
    /// Trusted test seam behind --storm-test-routes: holds a strong tornado over a lot for a few seconds. The same storm
    /// the native fixture uses, offset the same way, so it crosses the target. Never reachable by players.
    /// </summary>
    public object? StartLotTestStorm(string lotId, double seconds, float peakMph, float widthMeters)
    {
        if (!options.StormTestRoutes) throw new InvalidOperationException("Storm test routes are disabled.");
        lock (gate)
        {
            if (world is null || !lotCenters.TryGetValue(lotId, out var deedCenter)) return null;
            float ground = world.HeightAt(deedCenter.X, deedCenter.Z);
            testStorm = new PmWeatherSupercellSimulation(new() { Seed = 13, Id = "lifecare-lot-test-cell", PeakWindSpeedMph = (int)Math.Clamp(peakMph, 100, 400),
                MaximumWidthMeters = (int)Math.Clamp(widthMeters, 40, 700), TouchdownWindSpeedMph = 90,
                Origin = new(deedCenter.X, ground, deedCenter.Z), CloudBaseMeters = ground + 760, CloudTopMeters = ground + 3260,
                InitialStage = 3, InitialEnergy = 100, InitialGrowthSpeed = 3, TornadoGrowthSpeed = 2, DecayGrowthSpeed = 3 });
            testStorm.Advance(40);
            // Hold the funnel beside the lot so the lot sits in its eyewall, the radius of strongest wind (the axis
            // itself is calm). A real tornado sweeps across; a held test one has to be placed there.
            var funnelNow = testStorm.Current.ActiveStorms.SelectMany(s => s.Tornadoes).FirstOrDefault();
            float eyewall = funnelNow is null ? 0 : MathF.Max(funnelNow.BaseRadiusMeters, funnelNow.WidthMeters / 2.5f);
            proofEnvironment = CenterTornadoOn(testStorm.Current, deedCenter with { X = deedCenter.X - eyewall }); testStormSecondsLeft = Math.Clamp(seconds, 1, 60);
            var held = GroundStorms(proofEnvironment);
            var weatherNow = frameBuilder.Build(tick, DateTimeOffset.UnixEpoch, new(1, 1), new Engine.Rendering.Scene.RenderScene { Environment = held }).Weather;
            float wind = StormChaseDestruction.SampleWindSpeedMph(new(deedCenter.X, world.HeightAt(deedCenter.X, deedCenter.Z) + 2, deedCenter.Z), weatherNow);
            var tornado = held.ActiveStorms.SelectMany(s => s.Tornadoes).FirstOrDefault();
            Console.WriteLine($"Test tornado held over lot {lotId} for {testStormSecondsLeft:0} s: {wind:0} mph at the lot, funnel {(tornado is null ? "absent" : $"{tornado.WidthMeters:0} m wide")}.");
            var cell = weatherNow?.ActiveStorms.FirstOrDefault(); var rendered = cell?.Tornadoes.FirstOrDefault();
            return new { started = true, windMphAtLot = wind, tornado = tornado is not null, widthMeters = tornado?.WidthMeters ?? 0,
                render = cell is null ? null : new { cell.Stage, cell.SmoothedStage, cell.Energy, cell.VisualOnly, tornadoes = cell.Tornadoes.Count,
                    funnel = rendered is null ? null : new { rendered.WindSpeed, rendered.SmoothedWindSpeed, rendered.TouchdownAmount, rendered.TouchdownSpeed, rendered.VisualOnly,
                        x = rendered.Position.X, y = rendered.Position.Y, z = rendered.Position.Z, rendered.HeightMeters, rendered.BaseRadiusMeters } },
                lot = new { deedCenter.X, deedCenter.Z } };
        }
    }

    private static EnvironmentSceneState CenterTornadoOn(EnvironmentSceneState environment, Vector3f target)
    {
        var funnel = environment.ActiveStorms.SelectMany(s => s.Tornadoes).FirstOrDefault();
        if (funnel is null) return environment;
        float dx = target.X - funnel.Position.X, dz = target.Z - funnel.Position.Z;
        Vector3f Move(Vector3f p) => new(p.X + dx, p.Y, p.Z + dz);
        return environment with
        {
            Storms = environment.ActiveStorms.Select(s => s with
            {
                Position = Move(s.Position), Tornadoes = s.Tornadoes.Select(t => t with { Position = Move(t.Position) }).ToArray(),
                Vorticies = s.ActiveVorticies.Select(v => v with { Position = Move(v.Position) }).ToArray(),
            }).ToArray(),
        };
    }

    /// <summary>Test seam: a playable point with no authored destructible part within <paramref name="clearMeters"/>, nearest the map's middle.</summary>
    public object? FindClearTestSite(float clearMeters)
    {
        if (!options.StormTestRoutes) throw new InvalidOperationException("Storm test routes are disabled.");
        if (world is null) return null;
        const float cell = 250;
        var occupied = world.DestructibleItems.Select(i => ((int)MathF.Floor(i.Position.X / cell), (int)MathF.Floor(i.Position.Z / cell))).ToHashSet();
        int reach = (int)MathF.Ceiling(clearMeters / cell);
        float extent = StormChaseWorld.PlayableExtentMeters / 2;
        var best = default((float X, float Z)?); float bestDistance = float.MaxValue;
        for (float z = -extent; z <= extent; z += cell)
            for (float x = -extent; x <= extent; x += cell)
            {
                if (!world.ContainsPlayablePosition(x, z) || !world.ContainsPlayablePosition(x + clearMeters, z + clearMeters) || !world.ContainsPlayablePosition(x - clearMeters, z - clearMeters)) continue;
                int cx = (int)MathF.Floor(x / cell), cz = (int)MathF.Floor(z / cell); bool clear = true;
                for (int oz = -reach; oz <= reach && clear; oz++) for (int ox = -reach; ox <= reach && clear; ox++) clear = !occupied.Contains((cx + ox, cz + oz));
                if (!clear) continue;
                float d = x * x + z * z; if (d < bestDistance) { bestDistance = d; best = (x, z); }
            }
        return best is { } site ? new { x = site.X, z = site.Z, tileX = (int)MathF.Floor(site.X / BuildGrid.TileMeters), tileZ = (int)MathF.Floor(site.Z / BuildGrid.TileMeters) } : null;
    }

    private readonly Dictionary<string, Vector3f> lotCenters = new(StringComparer.Ordinal);

    private void RememberLotCenter(LotDocument document)
    {
        var d = document.Lot;
        lotCenters[d.LotId] = new((d.MinimumTileX + d.MaximumTileX + 1) * BuildGrid.TileMeters / 2, 0, (d.MinimumTileZ + d.MaximumTileZ + 1) * BuildGrid.TileMeters / 2);
    }

    /// <summary>On the tick: counts the test storm down and returns the sky to the climate's when it ends.</summary>
    private void AdvanceLotTestStorm()
    {
        if (testStorm is null) return;
        testStormSecondsLeft -= TickDelta;
        if (testStormSecondsLeft > 0) return;
        testStorm = null; proofEnvironment = null;
        int lotDebris = destruction?.ReleasedPartIds.Count(id => id.StartsWith("lot/", StringComparison.Ordinal)) ?? 0;
        Console.WriteLine($"Test tornado lifted: {lotDebris} lot pieces and {destruction?.DestroyedPartCount - lotDebris} world parts torn loose.");
    }
}
