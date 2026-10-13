using Npgsql;
using RfidMiddleware.Core;
using RfidMiddleware.Persistence;
using RfidMiddleware.MockServer;
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

    [Fact]
    public async Task MobileAuthorizationJourneyIsVersionedIdempotentAndCompletable()
    {
        var baseConnection = Environment.GetEnvironmentVariable("RFID_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(baseConnection)) return;
        var schema = $"mobile_{Guid.NewGuid():N}";
        await using var admin = new NpgsqlConnection(baseConnection);
        await admin.OpenAsync();
        await using (var create = admin.CreateCommand())
        {
            create.CommandText = $"CREATE SCHEMA \"{schema}\";";
            await create.ExecuteNonQueryAsync();
        }
        var builder = new NpgsqlConnectionStringBuilder(baseConnection) { SearchPath = schema };
        var database = new PostgresMockDatabase(builder.ConnectionString);
        try
        {
            await database.InitializeAsync();
            await database.ResetAndSeedAsync(2);
            Assert.True(await database.IsAuthorizedAsync("WH-A-GATE-01", "dev-token", ApiRole.Middleware, default));
            Assert.False(await database.IsAuthorizedAsync("WH-A-GATE-01", "dev-token", ApiRole.Mobile, default));
            Assert.True(await database.IsAuthorizedAsync("WH-A-GATE-01", "dev-mobile-token", ApiRole.Mobile, default));

            var authorize = new ManifestAuthorization("authorize-1", 17, "INV-EXTRA", "E2801170AAAAAAAAAAAAAAAA");
            var authorized = await database.AuthorizeAsync("WH-A-GATE-01", "TR-2026-00128", authorize, default);
            Assert.Equal(18, authorized.NewVersion);
            var duplicate = await database.AuthorizeAsync("WH-A-GATE-01", "TR-2026-00128", authorize, default);
            Assert.True(duplicate.AlreadyApplied);
            var conflict = await Assert.ThrowsAsync<VersionConflictException>(() => database.AuthorizeAsync(
                "WH-A-GATE-01", "TR-2026-00128",
                new ManifestAuthorization("authorize-stale", 17, "INV-STALE", "E2801170BBBBBBBBBBBBBBBB"), default));
            Assert.Equal(18, conflict.CurrentVersion);

            await Assert.ThrowsAsync<InvalidDataException>(() => database.RevokeAsync(
                "WH-A-GATE-01", "TR-2026-00128",
                new ManifestRevocation("revoke-no-reason", 18, ["E2801170AAAAAAAAAAAAAAAA"],
                    "", "wsl-test-operator"), default));
            await Assert.ThrowsAsync<InvalidDataException>(() => database.RevokeAsync(
                "WH-A-GATE-01", "TR-2026-00128",
                new ManifestRevocation("revoke-no-operator", 18, ["E2801170AAAAAAAAAAAAAAAA"],
                    "RETURNED_TO_STORAGE", ""), default));

            var revoked = await database.RevokeAsync("WH-A-GATE-01", "TR-2026-00128",
                new ManifestRevocation("revoke-1", 18, ["E2801170AAAAAAAAAAAAAAAA"],
                    "RETURNED_TO_STORAGE", "wsl-test-operator"), default);
            Assert.Equal(19, revoked.NewVersion);
            var completed = await database.CompleteAsync("WH-A-GATE-01", "TR-2026-00128",
                new ManifestCompletionRequest("complete-1", 19), default);
            Assert.Equal(20, completed.NewVersion);
            var manifest = await database.GetManifestAsync("WH-A-GATE-01", default);
            Assert.NotNull(manifest);
            Assert.Equal("COMPLETED", manifest.Status);
            Assert.Empty(manifest.Items);
        }
        finally
        {
            await using var drop = admin.CreateCommand();
            drop.CommandText = $"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE;";
            await drop.ExecuteNonQueryAsync();
        }
    }
}
