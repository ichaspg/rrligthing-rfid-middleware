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

        var scan = new ScanEvent(UlidGenerator.NewUlid(now), document.ManifestId, document.Version,
            document.Items[0].Epc, ValidationOutcome.Authorized, now, now, 2, "R1", new HashSet<int> { 8 }, -40,
            EventSyncState.LocalOnly);
        await store.AppendAsync(scan);
        var stats = await store.GetStatsAsync();
        Assert.Equal(1, stats.Total);
        Assert.Equal(1, stats.Authorized);
        Assert.Equal("ok", await store.VerifyAsync());
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
