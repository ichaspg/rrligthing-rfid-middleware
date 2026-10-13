using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using RfidMiddleware.Core;

namespace RfidMiddleware.Server;

public sealed class HttpManifestClient(HttpClient httpClient, ServerOptions options, IClock clock) : IManifestClient
{
    public async Task<ManifestFetchResult> GetCurrentAsync(string? etag, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"api/v1/middleware/{Uri.EscapeDataString(options.MiddlewareId)}/manifest");
        if (!string.IsNullOrWhiteSpace(etag)) request.Headers.IfNoneMatch.Add(new EntityTagHeaderValue(etag));
        AddAuthentication(request);
        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotModified)
            return new(ManifestFetchDirective.Unchanged, null, null, etag);
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<ManifestPayload>(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("Manifest response was empty.");
        var responseEtag = response.Headers.ETag?.Tag;
        if (string.Equals(payload.Status, "COMPLETED", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(payload.ManifestId) || payload.Version < 1)
                throw new InvalidDataException("Completed manifest directive is invalid.");
            return new(ManifestFetchDirective.Deactivate, null,
                new ManifestCompletion(payload.ManifestId, payload.Version), responseEtag);
        }
        var expires = payload.ExpiresAt ?? clock.UtcNow.AddMinutes(30);
        var items = payload.Items.Select(x => new ManifestItem(x.InventoryId, x.Epc)).ToArray();
        var document = new ManifestDocument(payload.ManifestId, payload.TransportId, payload.Version, payload.Status,
            payload.ItemCount, payload.Checksum, responseEtag, clock.UtcNow, expires, items);
        ManifestValidator.Validate(document);
        return new(ManifestFetchDirective.Activate, document, null, responseEtag);
    }

    private void AddAuthentication(HttpRequestMessage request)
    {
        var token = Environment.GetEnvironmentVariable(options.TokenEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(token)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    private sealed record ManifestPayload(string ManifestId, string TransportId, long Version, string Status,
        int ItemCount, string? Checksum, DateTimeOffset? ExpiresAt, IReadOnlyList<ManifestItemPayload> Items);
    private sealed record ManifestItemPayload(string InventoryId, string Epc);
}

