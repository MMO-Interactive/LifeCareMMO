using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LifeCare.Construction;

/// <summary>
/// The player's link to the Construction service, always through the Gateway at <c>/construction/</c>. Every build and
/// furnishing edit goes through here; the reply carries the authoritative lot document the client then renders.
/// </summary>
public sealed class ConstructionClient : ILotAuthority, IDisposable
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private readonly HttpClient http;
    private readonly bool ownsHttp;

    /// <param name="gatewayUrl">The Gateway's HTTP address, for example http://127.0.0.1:5000.</param>
    /// <param name="refreshAccess">
    /// Called with the rejected access token when the service answers 401 (access tokens last 15 minutes); returns a fresh
    /// token, and the request is sent once more with it. Without it, an expired token fails every request.
    /// </param>
    public ConstructionClient(string gatewayUrl, string accessToken, HttpMessageHandler? handler = null,
        Func<string, CancellationToken, Task<string?>>? refreshAccess = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gatewayUrl);
        HttpMessageHandler inner = handler ?? new HttpClientHandler();
        http = refreshAccess is null ? new HttpClient(inner, handler is null)
            : new HttpClient(new RefreshingHandler(refreshAccess) { InnerHandler = inner }, true);
        ownsHttp = true;
        http.BaseAddress = new Uri(gatewayUrl.TrimEnd('/') + "/construction/");
        http.Timeout = TimeSpan.FromSeconds(10);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
    }

    public async Task<IReadOnlyList<LotSummary>> ListAsync(CancellationToken cancel = default) =>
        await http.GetFromJsonAsync<LotSummary[]>("api/lots", Json, cancel) ?? [];

    public async Task<LotDocument?> GetAsync(string lotId, CancellationToken cancel = default)
    {
        using var reply = await http.GetAsync("api/lots/" + Uri.EscapeDataString(lotId), cancel);
        if (reply.StatusCode == HttpStatusCode.NotFound) return null;
        reply.EnsureSuccessStatusCode();
        return await reply.Content.ReadFromJsonAsync<LotDocument>(Json, cancel);
    }

    /// <summary>Claims a dev starter lot; null when the service does not offer them. Throws with the service's reason on a conflict.</summary>
    public async Task<LotDocument?> ClaimStarterAsync(StarterLotRequest request, CancellationToken cancel = default)
    {
        using var reply = await http.PostAsJsonAsync("api/lots/claim-starter", request, Json, cancel);
        if (reply.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed) return null;
        if (reply.StatusCode == HttpStatusCode.Conflict) throw new InvalidOperationException((await reply.Content.ReadFromJsonAsync<JsonElement>(Json, cancel)).GetProperty("error").GetString());
        reply.EnsureSuccessStatusCode();
        return await reply.Content.ReadFromJsonAsync<LotDocument>(Json, cancel);
    }

    public Task<ChangeResult> BuildAsync(string lotId, BuildRequest request, CancellationToken cancel = default) => Change(lotId, "build", request, cancel);
    public Task<ChangeResult> PlaceAsync(string lotId, FurnishingRequest request, CancellationToken cancel = default) => Change(lotId, "furnishings/place", request, cancel);
    public Task<ChangeResult> MoveAsync(string lotId, FurnishingRequest request, CancellationToken cancel = default) => Change(lotId, "furnishings/move", request, cancel);
    public Task<ChangeResult> PickUpAsync(string lotId, FurnishingRequest request, CancellationToken cancel = default) => Change(lotId, "furnishings/pickup", request, cancel);
    public Task<ChangeResult> SetDoorAsync(string lotId, DoorRequest request, CancellationToken cancel = default) => Change(lotId, "doors", request, cancel);

    private async Task<ChangeResult> Change(string lotId, string route, object request, CancellationToken cancel)
    {
        using var reply = await http.PostAsJsonAsync($"api/lots/{Uri.EscapeDataString(lotId)}/{route}", request, Json, cancel);
        // 200 accepted, 409 stale, 422 rejected by the rules: all carry a ChangeResult with the current lot.
        if (reply.StatusCode is HttpStatusCode.OK or HttpStatusCode.Conflict or HttpStatusCode.UnprocessableEntity)
            return await reply.Content.ReadFromJsonAsync<ChangeResult>(Json, cancel) ?? throw new InvalidDataException("The Construction service sent an empty reply.");
        throw new HttpRequestException(reply.StatusCode == HttpStatusCode.Forbidden ? "That character is not yours." : $"The Construction service answered {(int)reply.StatusCode}.", null, reply.StatusCode);
    }

    public void Dispose() { if (ownsHttp) http.Dispose(); }

    /// <summary>Retries a request once with a refreshed bearer token after a 401, and keeps that token for later requests.</summary>
    private sealed class RefreshingHandler(Func<string, CancellationToken, Task<string?>> refresh) : DelegatingHandler
    {
        private readonly SemaphoreSlim gate = new(1, 1);
        private string? current;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
        {
            if (current is not null) request.Headers.Authorization = new("Bearer", current);
            byte[]? body = request.Content is null ? null : await request.Content.ReadAsByteArrayAsync(cancel);
            var contentType = request.Content?.Headers.ContentType;
            if (body is not null) { request.Content = new ByteArrayContent(body); request.Content.Headers.ContentType = contentType; }
            var response = await base.SendAsync(request, cancel);
            if (response.StatusCode != HttpStatusCode.Unauthorized || request.Headers.Authorization?.Parameter is not { } rejected) return response;
            string? fresh;
            await gate.WaitAsync(cancel);
            try { fresh = current is not null && current != rejected ? current : await refresh(rejected, cancel); if (fresh is not null) current = fresh; }
            catch { return response; }
            finally { gate.Release(); }
            if (fresh is null || fresh == rejected) return response;
            response.Dispose();
            var retry = new HttpRequestMessage(request.Method, request.RequestUri) { Version = request.Version };
            foreach (var header in request.Headers) retry.Headers.TryAddWithoutValidation(header.Key, header.Value);
            retry.Headers.Authorization = new("Bearer", fresh);
            if (body is not null) { retry.Content = new ByteArrayContent(body); retry.Content.Headers.ContentType = contentType; }
            return await base.SendAsync(retry, cancel);
        }

        protected override void Dispose(bool disposing) { if (disposing) gate.Dispose(); base.Dispose(disposing); }
    }
}
