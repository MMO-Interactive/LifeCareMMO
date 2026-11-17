using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace LifeCare.Gateway;

/// <summary>
/// What the Gateway knows about the district hosts behind it: their real (internal) addresses from the accounts directory,
/// and which host each admission ticket belongs to. Clients only ever see the Gateway's addresses.
/// </summary>
public sealed class GatewayDirectory(GatewayOptions options, IHttpClientFactory clients, ILogger<GatewayDirectory> logger) : BackgroundService
{
    private readonly ConcurrentDictionary<string, JsonObject> servers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (string ServerId, DateTimeOffset Expires)> tickets = new(StringComparer.Ordinal);
    public string? TransportIdentity { get; set; }
    public string? TransportAuthority { get; set; }

    protected override async Task ExecuteAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try { Learn(JsonNode.Parse(await clients.CreateClient("accounts").GetStringAsync("/api/servers", token)) as JsonArray); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception error) { logger.LogWarning("Accounts directory poll failed: {Reason}", error.Message); }
            foreach (var expired in tickets.Where(t => t.Value.Expires <= DateTimeOffset.UtcNow).Select(t => t.Key).ToArray()) tickets.TryRemove(expired, out _);
            await Task.Delay(TimeSpan.FromSeconds(2), token);
        }
    }

    /// <summary>Records real server entries, which may arrive from the poll or from a proxied response.</summary>
    public void Learn(JsonArray? list)
    {
        if (list is null) return;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in list.OfType<JsonObject>())
            if (item["serverId"]?.GetValue<string>() is { Length: > 0 } id) { servers[id] = (JsonObject)item.DeepClone(); seen.Add(id); }
        foreach (var gone in servers.Keys.Where(k => !seen.Contains(k)).ToArray()) servers.TryRemove(gone, out _);
    }

    public bool TryServer(string serverId, out JsonObject server) => servers.TryGetValue(serverId, out server!);

    /// <summary>The district host's internal relay endpoint, or null when it is not running in Gateway mode.</summary>
    public (string Host, int Port, string ApiAddress)? Route(string serverId) =>
        servers.TryGetValue(serverId, out var s) && s["relayPort"]?.GetValue<int>() is > 0 and var port
            ? (s["host"]?.GetValue<string>() ?? "127.0.0.1", port, s["apiAddress"]?.GetValue<string>() ?? "") : null;

    public void RememberTicket(string ticket, string serverId, DateTimeOffset expires) => tickets[ticket] = (serverId, expires.AddSeconds(5));
    public string? ServerForTicket(string ticket) => tickets.TryRemove(ticket, out var t) && t.Expires > DateTimeOffset.UtcNow ? t.ServerId : null;

    /// <summary>Replaces every address in a server entry with the Gateway's own. Hosts that cannot be relayed are reported as not ready.</summary>
    public JsonObject Publish(JsonObject real)
    {
        var copy = (JsonObject)real.DeepClone();
        string id = copy["serverId"]?.GetValue<string>() ?? "";
        bool relayed = copy["relayPort"]?.GetValue<int>() is > 0;
        copy["host"] = options.AdvertiseHost;
        copy["gamePort"] = options.GamePort;
        copy["httpPort"] = options.HttpPort;
        copy["apiAddress"] = options.PublicBase + "/s/" + id;
        copy["transportIdentity"] = TransportIdentity ?? "";
        copy["transportAuthority"] = TransportAuthority ?? "";
        copy["relayPort"] = 0;
        if (!relayed) copy["ready"] = false;
        return copy;
    }
}
