using Npgsql;
using RfidMiddleware.Core;
using RfidMiddleware.Persistence;
using Xunit;

namespace RfidMiddleware.EndToEndTests;

public sealed class PostgresJourneyTests
{
    [Fact]
    public async Task ManifestValidationAndAuditRemainLocal()
    {
        var baseConnection = Environment.GetEnvironmentVariable("RFID_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(baseConnection)) return;
        var schema = $"e2e_{Guid.NewGuid():N}";
        await using var admin = new NpgsqlConnection(baseConnection);
        await admin.OpenAsync();
        await using (var create = admin.CreateCommand())
        {
            create.CommandText = $"CREATE SCHEMA \"{schema}\";";
            await create.ExecuteNonQueryAsync();
        }
        var builder = new NpgsqlConnectionStringBuilder(baseConnection) { SearchPath = schema };
        await using var store = new PostgresMiddlewareStore(builder.ConnectionString);
        try
        {
            await store.InitializeAsync();
            const string epc = "E28011700000000000000001";
            var now = DateTimeOffset.UtcNow;
            var items = new[] { new ManifestItem("I-1", epc) };
            var document = new ManifestDocument("M-1", "T-1", 17, "ACTIVE", 1,
                ManifestValidator.ComputeChecksum([epc]), "\"M-1-17\"", now, now.AddMinutes(30), items);
            await store.StageAndActivateAsync(document);
            var engine = new ValidationEngine();
            engine.Activate((await store.GetActiveAsync())!);
            var decision = engine.Validate(epc, now);
            await store.AppendAsync(new(UlidGenerator.NewUlid(now), document.ManifestId, document.Version, epc,
                decision.Outcome, now, now, 1, "R-1", new HashSet<int> { 8 }, -35, EventSyncState.LocalOnly));
            var stats = await store.GetStatsAsync();
            Assert.Equal(ValidationOutcome.Authorized, decision.Outcome);
            Assert.Equal(1, stats.Total);
            Assert.Equal("ok", await store.VerifyAsync());
        }
        finally
        {
            await using var drop = admin.CreateCommand();
            drop.CommandText = $"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE;";
            await drop.ExecuteNonQueryAsync();
        }
    }
}
