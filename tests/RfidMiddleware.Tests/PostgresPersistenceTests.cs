using Npgsql;
using RfidMiddleware.Core;
using RfidMiddleware.Persistence;

namespace RfidMiddleware.Tests;

public sealed class PostgresPersistenceTests
{
    [Fact]
    public async Task ActivatesRestoresAndStoresLocalOnlyAudit()
    {
        var baseConnection = Environment.GetEnvironmentVariable("RFID_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(baseConnection)) return;
        await using var scope = await PostgresTestScope.CreateAsync(baseConnection);
        var store = scope.Store;
        await store.InitializeAsync();
        var now = DateTimeOffset.UtcNow;
        var document = CoreTests.Document(now.AddMinutes(30), 10);

        await store.StageAndActivateAsync(document);
        var active = await store.GetActiveAsync();
        Assert.NotNull(active);
        Assert.Equal(10, active.Epcs.Count);
        var authorization = await store.GetAuthorizationStateAsync();
        Assert.Equal(AuthorizationState.Active, authorization.State);
        Assert.Equal(document.Version, authorization.Version);

        var firstPage = await store.GetActiveItemsPageAsync(3, null);
        Assert.NotNull(firstPage);
        Assert.Equal(3, firstPage.Items.Count);
        Assert.NotNull(firstPage.NextAfterEpc);
        var secondPage = await store.GetActiveItemsPageAsync(3, firstPage.NextAfterEpc);
        Assert.NotNull(secondPage);
        Assert.DoesNotContain(secondPage.Items, item => firstPage.Items.Any(first => first.Epc == item.Epc));

        var scan = new ScanEvent(UlidGenerator.NewUlid(now), document.ManifestId, document.Version,
            document.Items[0].Epc, ValidationOutcome.Authorized, now, now, 2, "R1", new HashSet<int> { 8 }, -40,
            EventSyncState.LocalOnly);
        await store.AppendAsync(scan);
        var stats = await store.GetStatsAsync();
        Assert.Equal(1, stats.Total);
        Assert.Equal(1, stats.Authorized);
        Assert.Equal("ok", await store.VerifyAsync());

        await store.DeactivateAsync(new ManifestCompletion(document.ManifestId, document.Version + 1));
        Assert.Null(await store.GetActiveAsync());
        authorization = await store.GetAuthorizationStateAsync();
        Assert.Equal(AuthorizationState.Completed, authorization.State);
        Assert.Equal(document.Version + 1, authorization.Version);
    }

    [Fact]
    public async Task ScanSessionLifecycleIsDurableAndPaginated()
    {
        var baseConnection = Environment.GetEnvironmentVariable("RFID_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(baseConnection)) return;
        await using var scope = await PostgresTestScope.CreateAsync(baseConnection);
        var store = scope.Store;
        await store.InitializeAsync();
        var now = DateTimeOffset.UtcNow;

        var first = await store.StartOrGetActiveAsync(UlidGenerator.NewUlid(now), now);
        var repeatedStart = await store.StartOrGetActiveAsync(UlidGenerator.NewUlid(now.AddSeconds(1)), now.AddSeconds(1));
        Assert.True(first.Created);
        Assert.False(repeatedStart.Created);
        Assert.Equal(first.Session.SessionId, repeatedStart.Session.SessionId);

        var observations = new[]
        {
            new ScanSessionObservation(first.Session.SessionId, 1, 1, "BAD", ValidationOutcome.InvalidEpc,
                1, -60, now, 1, 1, true),
            new ScanSessionObservation(first.Session.SessionId, 1, 2, "E2806915000060068C95C54B",
                ValidationOutcome.Authorized, 2, -45, now.AddMilliseconds(1), 2, 2, true),
            new ScanSessionObservation(first.Session.SessionId, 1, 3, "E2806915000060068C95C54B",
                ValidationOutcome.Unauthorized, 3, -40, now.AddMilliseconds(2), 3, 2, false)
        };
        await store.ApplySessionObservationsAsync(observations);

        var firstPage = await store.GetSessionItemsPageAsync(first.Session.SessionId, 1, null);
        Assert.NotNull(firstPage);
        Assert.Single(firstPage.Items);
        Assert.NotNull(firstPage.NextAfterEpc);
        var secondPage = await store.GetSessionItemsPageAsync(first.Session.SessionId, 1, firstPage.NextAfterEpc);
        var repeated = Assert.Single(secondPage!.Items);
        Assert.Equal(2, repeated.ReadCount);
        Assert.Equal(ValidationOutcome.Unauthorized, repeated.LatestOutcome);
        Assert.Equal([2, 3], repeated.AntennaIds);
        Assert.Equal(-40, repeated.StrongestRssi);

        var reset = await store.ResetSessionAsync(first.Session.SessionId, now.AddMinutes(1));
        Assert.Equal(2, reset.Generation);
        Assert.Equal(0, reset.RawReadCount);
        Assert.Empty((await store.GetSessionItemsPageAsync(first.Session.SessionId, 100, null))!.Items);
        var completed = await store.CompleteSessionAsync(first.Session.SessionId, now.AddMinutes(2));
        Assert.Equal(ScanSessionStatus.Completed, completed.Status);
        Assert.Equal(completed, await store.CompleteSessionAsync(first.Session.SessionId, now.AddMinutes(3)));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.ResetSessionAsync(first.Session.SessionId, now.AddMinutes(4)));
        Assert.Equal(0, await store.DeleteCompletedSessionsBeforeAsync(now.AddMinutes(2)));
        Assert.Equal(1, await store.DeleteCompletedSessionsBeforeAsync(now.AddMinutes(2).AddTicks(1)));

        var next = await store.StartOrGetActiveAsync(UlidGenerator.NewUlid(now.AddMinutes(5)), now.AddMinutes(5));
        Assert.True(next.Created);
        Assert.NotEqual(first.Session.SessionId, next.Session.SessionId);
    }
}

internal sealed class PostgresTestScope(string adminConnectionString, string schema, PostgresMiddlewareStore store) : IAsyncDisposable
{
    internal PostgresMiddlewareStore Store { get; } = store;

    internal static async Task<PostgresTestScope> CreateAsync(string connectionString)
    {
        var schema = $"test_{Guid.NewGuid():N}";
        await using (var connection = new NpgsqlConnection(connectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"CREATE SCHEMA \"{schema}\";";
            await command.ExecuteNonQueryAsync();
        }
        var builder = new NpgsqlConnectionStringBuilder(connectionString) { SearchPath = schema };
        return new(connectionString, schema, new(builder.ConnectionString));
    }

    public async ValueTask DisposeAsync()
    {
        await Store.DisposeAsync();
        await using var connection = new NpgsqlConnection(adminConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE;";
        await command.ExecuteNonQueryAsync();
    }
}
