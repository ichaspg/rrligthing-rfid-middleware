using System.Net;
using System.Text;
using RfidMiddleware.Core;
using RfidMiddleware.Server;
using Xunit;

namespace RfidMiddleware.IntegrationTests;

public sealed class HttpClientIntegrationTests
{
    [Fact]
    public async Task ManifestClientValidatesPayloadAndUsesEtag()
    {
        const string epc = "E28011700000000000000001";
        var checksum = ManifestValidator.ComputeChecksum([epc]);
        var calls = 0;
        using var http = new HttpClient(new DelegateHandler(request =>
        {
            calls++;
            Assert.Equal("Bearer dev-token", request.Headers.Authorization?.ToString());
            if (request.Headers.IfNoneMatch.Count > 0)
                return new HttpResponseMessage(HttpStatusCode.NotModified);

            var json = $$"""
                {"manifestId":"M-1","transportId":"T-1","version":17,"status":"ACTIVE",
                 "itemCount":1,"checksum":"{{checksum}}","expiresAt":"2099-01-01T00:00:00Z",
                 "items":[{"inventoryId":"I-1","epc":"{{epc}}"}]}
                """;
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"M-1-17\"");
            return response;
        })) { BaseAddress = new Uri("http://test/") };

        const string tokenName = "RFID_TEST_INTEGRATION_TOKEN";
        Environment.SetEnvironmentVariable(tokenName, "dev-token");
        try
        {
            var client = new HttpManifestClient(http, new ServerOptions { MiddlewareId = "MW-1", TokenEnvironmentVariable = tokenName }, new FixedClock());
            var first = await client.GetCurrentAsync(null);
            Assert.Equal(ManifestFetchDirective.Activate, first.Directive);
            Assert.Equal(17, first.Manifest?.Version);
            Assert.Equal("\"M-1-17\"", first.ETag);

            var second = await client.GetCurrentAsync(first.ETag);
            Assert.Equal(ManifestFetchDirective.Unchanged, second.Directive);
            Assert.Equal(2, calls);
        }
        finally { Environment.SetEnvironmentVariable(tokenName, null); }
    }

    [Fact]
    public async Task ManifestClientReturnsDeactivateDirectiveForCompletedManifest()
    {
        using var http = new HttpClient(new DelegateHandler(_ =>
        {
            var json = """
                {"manifestId":"M-1","transportId":"T-1","version":18,"status":"COMPLETED",
                 "itemCount":0,"checksum":"","expiresAt":"2026-01-01T00:00:00Z","items":[]}
                """;
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"M-1-18-COMPLETED\"");
            return response;
        })) { BaseAddress = new Uri("http://test/") };

        var client = new HttpManifestClient(http,
            new ServerOptions { MiddlewareId = "MW-1", TokenEnvironmentVariable = "RFID_UNUSED_TEST_TOKEN" },
            new FixedClock());
        var result = await client.GetCurrentAsync(null);

        Assert.Equal(ManifestFetchDirective.Deactivate, result.Directive);
        Assert.Null(result.Manifest);
        Assert.Equal(new ManifestCompletion("M-1", 18), result.Completion);
    }

    private sealed class FixedClock : IClock { public DateTimeOffset UtcNow => DateTimeOffset.Parse("2026-01-01T00:00:00Z"); }

    private sealed class DelegateHandler(Func<HttpRequestMessage, HttpResponseMessage> callback) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(callback(request));
    }
}
