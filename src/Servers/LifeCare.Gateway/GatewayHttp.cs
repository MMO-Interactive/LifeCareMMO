using System.Net;
using System.Text.Json.Nodes;

namespace LifeCare.Gateway;

/// <summary>
/// Forwards client HTTP to the service that owns it: <c>/s/{serverId}/…</c> to that district host's API, everything else to accounts.
/// Internal routes are never reachable from outside, and server addresses in responses are rewritten to the Gateway's.
/// </summary>
public static class GatewayHttp
{
    private const long MaxBody = 8192;
    private static readonly HashSet<string> HopHeaders = new(StringComparer.OrdinalIgnoreCase)
    { "Connection", "Keep-Alive", "Transfer-Encoding", "Upgrade", "Proxy-Connection", "TE", "Trailer", "Host", "Content-Length" };

    public static void Map(WebApplication app)
    {
        app.MapGet("/gateway/health", (GatewayDirectory directory, GatewayRelay relay) =>
            Results.Ok(new { status = relay.Ready ? "ready" : "starting", players = relay.PlayerCount, links = relay.LinkCount }));
        app.Map("/{**path}", Forward);
    }

    private static async Task Forward(HttpContext context, GatewayOptions options, GatewayDirectory directory, IHttpClientFactory clients)
    {
        string path = context.Request.Path.Value ?? "/";
        if (path.StartsWith("/api/internal", StringComparison.OrdinalIgnoreCase) || path.Contains("/api/internal", StringComparison.OrdinalIgnoreCase))
        { context.Response.StatusCode = 404; return; }

        HttpClient client; string target; string? serverId = null;
        if (path.StartsWith("/s/", StringComparison.Ordinal))
        {
            int slash = path.IndexOf('/', 3);
            serverId = slash < 0 ? path[3..] : path[3..slash];
            if (directory.Route(serverId) is not { } route) { context.Response.StatusCode = 404; return; }
            client = clients.CreateClient("hosts");
            target = route.ApiAddress.TrimEnd('/') + (slash < 0 ? "/" : path[slash..]);
        }
        else if (path.StartsWith("/construction/", StringComparison.Ordinal))
        {
            if (options.ConstructionUrl is null) { context.Response.StatusCode = 404; return; }
            client = clients.CreateClient("hosts"); target = options.ConstructionUrl + path["/construction".Length..];
        }
        else { client = clients.CreateClient("accounts"); target = options.AccountsUrl + path; }
        target += context.Request.QueryString.Value;

        if (context.Request.ContentLength > MaxBody) { context.Response.StatusCode = 413; return; }
        using var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), target);
        if (context.Request.ContentLength > 0 || context.Request.Headers.TransferEncoding.Count > 0)
        {
            var body = new MemoryStream();
            await context.Request.Body.CopyToAsync(body, context.RequestAborted);
            if (body.Length > MaxBody) { context.Response.StatusCode = 413; return; }
            body.Position = 0; request.Content = new StreamContent(body);
        }
        foreach (var header in context.Request.Headers)
        {
            if (HopHeaders.Contains(header.Key) || header.Key.StartsWith("X-StormChase", StringComparison.OrdinalIgnoreCase)) continue;
            if (!request.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray())) request.Content?.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
        }
        request.Headers.TryAddWithoutValidation("X-Forwarded-For", context.Connection.RemoteIpAddress?.ToString());

        HttpResponseMessage response;
        try { response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted); }
        catch (HttpRequestException) { context.Response.StatusCode = (int)HttpStatusCode.BadGateway; return; }
        using (response)
        {
            context.Response.StatusCode = (int)response.StatusCode;
            foreach (var header in response.Headers.Concat(response.Content.Headers))
                if (!HopHeaders.Contains(header.Key)) context.Response.Headers[header.Key] = header.Value.ToArray();

            string route = serverId is null ? path : path[(3 + serverId.Length)..];
            bool rewrite = response.IsSuccessStatusCode && context.Request.Method is "GET" or "POST" &&
                route is "/api/servers" or "/api/server" or "/api/matches/join";
            if (!rewrite) { await response.Content.CopyToAsync(context.Response.Body, context.RequestAborted); return; }

            var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(context.RequestAborted));
            JsonNode? output = json;
            if (json is JsonArray list)
            {
                directory.Learn(list);
                output = new JsonArray(list.OfType<JsonObject>().Select(s => (JsonNode)directory.Publish(s)).ToArray());
            }
            else if (json is JsonObject item && route == "/api/matches/join")
            {
                string id = item["serverId"]?.GetValue<string>() ?? "";
                if (item["ticket"]?.GetValue<string>() is { } ticket)
                    directory.RememberTicket(ticket, id, item["expiresUtc"]?.GetValue<DateTimeOffset>() ?? DateTimeOffset.UtcNow.AddSeconds(30));
                item["host"] = options.AdvertiseHost; item["gamePort"] = options.GamePort; item["apiAddress"] = options.PublicBase + "/s/" + id;
            }
            else if (json is JsonObject server) output = directory.Publish(server);
            context.Response.Headers.ContentLength = null;
            await context.Response.WriteAsync(output?.ToJsonString() ?? "null", context.RequestAborted);
        }
    }
}
