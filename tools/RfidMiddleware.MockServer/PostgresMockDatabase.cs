using System.Data;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using RfidMiddleware.Core;

namespace RfidMiddleware.MockServer;

internal sealed class PostgresMockDatabase(string connectionString)
{
    private readonly NpgsqlDataSource _source = NpgsqlDataSource.Create(connectionString);

    internal async Task InitializeAsync(CancellationToken token = default)
    {
        await using var connection = await OpenAsync(token);
        await ExecuteAsync(connection, """
            CREATE TABLE IF NOT EXISTS middleware(id text PRIMARY KEY, token text NOT NULL);
            CREATE TABLE IF NOT EXISTS manifest(
              middleware_id text NOT NULL, manifest_id text NOT NULL, transport_id text NOT NULL, version bigint NOT NULL,
              status text NOT NULL, expires_at timestamptz NOT NULL, checksum text NOT NULL, item_count integer NOT NULL,
              PRIMARY KEY(middleware_id,manifest_id,version));
            CREATE UNIQUE INDEX IF NOT EXISTS ux_manifest_active ON manifest(middleware_id) WHERE status='ACTIVE';
            CREATE TABLE IF NOT EXISTS manifest_item(
              middleware_id text NOT NULL, manifest_id text NOT NULL, version bigint NOT NULL,
              inventory_id text NOT NULL, epc text NOT NULL,
              PRIMARY KEY(middleware_id,manifest_id,version,epc),
              FOREIGN KEY(middleware_id,manifest_id,version)
                REFERENCES manifest(middleware_id,manifest_id,version) ON DELETE CASCADE);
            CREATE TABLE IF NOT EXISTS fault_profile(
              middleware_id text PRIMARY KEY, profile text NOT NULL DEFAULT 'none', latency_ms integer NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS revocation_audit(
              middleware_id text NOT NULL, request_id text NOT NULL, manifest_id text NOT NULL,
              previous_version bigint NOT NULL, new_version bigint NOT NULL, revoked_epcs jsonb NOT NULL,
              reason text NOT NULL, requested_by text, occurred_at timestamptz NOT NULL, created_at timestamptz NOT NULL,
              PRIMARY KEY(middleware_id,request_id));
            """, token);
    }

    internal async Task ResetAndSeedAsync(int count, CancellationToken token = default)
    {
        await using var connection = await OpenAsync(token);
        await using var tx = await connection.BeginTransactionAsync(token);
        await ExecuteAsync(connection,
            "TRUNCATE revocation_audit,manifest_item,manifest,fault_profile,middleware CASCADE;", token, tx);
        await ExecuteAsync(connection, """
            INSERT INTO middleware(id,token) VALUES('WH-A-GATE-01','dev-token');
            INSERT INTO fault_profile(middleware_id,profile,latency_ms) VALUES('WH-A-GATE-01','none',0);
            """, token, tx);
        var items = Enumerable.Range(0, count)
            .Select(i => new ManifestItem($"INV{i + 1:D6}", $"E2801170{i:X16}"))
            .ToArray();
        await InsertManifestAsync(connection, tx, "WH-A-GATE-01", "TR-2026-00128", "TR-2026-00128",
            17, DateTimeOffset.UtcNow.AddDays(1), items, token);
        await tx.CommitAsync(token);
    }

    internal async Task EnsureSeededAsync(CancellationToken token = default)
    {
        await using var connection = await OpenAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM middleware;";
        if (Convert.ToInt64(await command.ExecuteScalarAsync(token)) == 0) await ResetAndSeedAsync(100, token);
    }

    internal async Task ActivateManifestAsync(string middlewareId, ManifestActivation request, CancellationToken token)
    {
        var items = request.Items is { Count: > 0 }
            ? request.Items.Select(x => new ManifestItem(x.InventoryId, EpcNormalizer.Normalize(x.Epc))).ToArray()
            : Enumerable.Range(0, Math.Clamp(request.Count, 1, 1_000_000))
                .Select(i => new ManifestItem($"INV{request.Version:D4}-{i + 1:D6}", $"E280{request.Version:X4}{i:X16}"))
                .ToArray();
        if (items.Select(x => x.Epc).Distinct(StringComparer.Ordinal).Count() != items.Length)
            throw new InvalidDataException("Explicit manifest items contain duplicate EPCs.");
        await using var connection = await OpenAsync(token);
        await using var tx = await connection.BeginTransactionAsync(token);
        await SupersedeAsync(connection, tx, middlewareId, token);
        await InsertManifestAsync(connection, tx, middlewareId, request.ManifestId, request.TransportId, request.Version,
            DateTimeOffset.UtcNow.AddMinutes(Math.Clamp(request.ExpiresMinutes, 1, 43_200)), items, token);
        await tx.CommitAsync(token);
    }

    internal async Task<RevocationResult> RevokeAsync(
        string middlewareId, string manifestId, ManifestRevocation request, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(request.RequestId)) throw new InvalidDataException("requestId is required.");
        if (request.Epcs is not { Count: > 0 }) throw new InvalidDataException("At least one EPC is required.");
        var revoked = request.Epcs.Select(EpcNormalizer.Normalize).ToHashSet(StringComparer.Ordinal);
        await using var connection = await OpenAsync(token);
        await using var tx = await connection.BeginTransactionAsync(IsolationLevel.Serializable, token);

        await using (var prior = connection.CreateCommand())
        {
            prior.Transaction = tx;
            prior.CommandText = "SELECT manifest_id,previous_version,new_version,revoked_epcs FROM revocation_audit WHERE middleware_id=@id AND request_id=@request;";
            prior.Parameters.AddWithValue("id", middlewareId);
            prior.Parameters.AddWithValue("request", request.RequestId);
            await using var reader = await prior.ExecuteReaderAsync(token);
            if (await reader.ReadAsync(token))
            {
                var result = new RevocationResult(reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2),
                    JsonSerializer.Deserialize<string[]>(reader.GetString(3)) ?? [], true);
                await tx.CommitAsync(token);
                return result;
            }
        }

        string transportId;
        long previousVersion;
        DateTimeOffset expiresAt;
        await using (var current = connection.CreateCommand())
        {
            current.Transaction = tx;
            current.CommandText = """
                SELECT transport_id,version,expires_at FROM manifest
                WHERE middleware_id=@id AND manifest_id=@manifest AND status='ACTIVE' FOR UPDATE;
                """;
            current.Parameters.AddWithValue("id", middlewareId);
            current.Parameters.AddWithValue("manifest", manifestId);
            await using var reader = await current.ExecuteReaderAsync(token);
            if (!await reader.ReadAsync(token)) throw new KeyNotFoundException("The requested active manifest was not found.");
            transportId = reader.GetString(0);
            previousVersion = reader.GetInt64(1);
            expiresAt = new DateTimeOffset(reader.GetDateTime(2).ToUniversalTime());
        }

        var activeItems = new List<ManifestItem>();
        await using (var items = connection.CreateCommand())
        {
            items.Transaction = tx;
            items.CommandText = "SELECT inventory_id,epc FROM manifest_item WHERE middleware_id=@id AND manifest_id=@manifest AND version=@version ORDER BY epc;";
            items.Parameters.AddWithValue("id", middlewareId);
            items.Parameters.AddWithValue("manifest", manifestId);
            items.Parameters.AddWithValue("version", previousVersion);
            await using var reader = await items.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token)) activeItems.Add(new(reader.GetString(0), reader.GetString(1)));
        }
        var activeEpcs = activeItems.Select(x => x.Epc).ToHashSet(StringComparer.Ordinal);
        var missing = revoked.Where(x => !activeEpcs.Contains(x)).ToArray();
        if (missing.Length > 0) throw new InvalidDataException($"EPC is not active in this manifest: {string.Join(',', missing)}");
        var remaining = activeItems.Where(x => !revoked.Contains(x.Epc)).ToArray();
        if (remaining.Length == 0)
            throw new InvalidOperationException("This request would revoke the final EPC; close the complete manifest instead.");

        var newVersion = previousVersion + 1;
        await SupersedeAsync(connection, tx, middlewareId, token);
        await InsertManifestAsync(connection, tx, middlewareId, manifestId, transportId, newVersion,
            expiresAt > DateTimeOffset.UtcNow ? expiresAt : DateTimeOffset.UtcNow.AddMinutes(30), remaining, token);
        await using (var audit = connection.CreateCommand())
        {
            audit.Transaction = tx;
            audit.CommandText = """
                INSERT INTO revocation_audit(middleware_id,request_id,manifest_id,previous_version,new_version,
                  revoked_epcs,reason,requested_by,occurred_at,created_at)
                VALUES(@id,@request,@manifest,@previous,@version,@epcs,@reason,@by,@occurred,@created);
                """;
            audit.Parameters.AddWithValue("id", middlewareId);
            audit.Parameters.AddWithValue("request", request.RequestId);
            audit.Parameters.AddWithValue("manifest", manifestId);
            audit.Parameters.AddWithValue("previous", previousVersion);
            audit.Parameters.AddWithValue("version", newVersion);
            audit.Parameters.AddWithValue("epcs", NpgsqlDbType.Jsonb, JsonSerializer.Serialize(revoked.Order()));
            audit.Parameters.AddWithValue("reason", string.IsNullOrWhiteSpace(request.Reason) ? "RETURNED_TO_STORAGE" : request.Reason);
            audit.Parameters.AddWithValue("by", (object?)request.RequestedBy ?? DBNull.Value);
            audit.Parameters.AddWithValue("occurred", request.OccurredAt ?? DateTimeOffset.UtcNow);
            audit.Parameters.AddWithValue("created", DateTimeOffset.UtcNow);
            await audit.ExecuteNonQueryAsync(token);
        }
        await tx.CommitAsync(token);
        return new(manifestId, previousVersion, newVersion, revoked.Order().ToArray(), false);
    }

    internal async Task<ManifestResponse?> GetManifestAsync(string middlewareId, CancellationToken token)
    {
        await using var connection = await OpenAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT manifest_id,transport_id,version,status,expires_at,checksum,item_count FROM manifest WHERE middleware_id=@id AND status='ACTIVE' ORDER BY version DESC LIMIT 1;";
        command.Parameters.AddWithValue("id", middlewareId);
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) return null;
        var response = new ManifestResponse(reader.GetString(0), reader.GetString(1), reader.GetInt64(2), reader.GetString(3),
            new DateTimeOffset(reader.GetDateTime(4).ToUniversalTime()), reader.GetString(5), reader.GetInt32(6), []);
        await reader.DisposeAsync();
        await using var items = connection.CreateCommand();
        items.CommandText = "SELECT inventory_id,epc FROM manifest_item WHERE middleware_id=@id AND manifest_id=@manifest AND version=@version ORDER BY epc;";
        items.Parameters.AddWithValue("id", middlewareId);
        items.Parameters.AddWithValue("manifest", response.ManifestId);
        items.Parameters.AddWithValue("version", response.Version);
        await using var itemReader = await items.ExecuteReaderAsync(token);
        var values = new List<ManifestItem>();
        while (await itemReader.ReadAsync(token)) values.Add(new(itemReader.GetString(0), itemReader.GetString(1)));
        return response with { Items = values };
    }

    internal async Task<FaultProfile> GetFaultAsync(string middlewareId, CancellationToken token)
    {
        await using var connection = await OpenAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT profile,latency_ms FROM fault_profile WHERE middleware_id=@id;";
        command.Parameters.AddWithValue("id", middlewareId);
        await using var reader = await command.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token) ? new(reader.GetString(0), reader.GetInt32(1)) : new("none", 0);
    }

    internal async Task SetFaultAsync(string middlewareId, FaultProfile profile, CancellationToken token)
    {
        await using var connection = await OpenAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO fault_profile(middleware_id,profile,latency_ms) VALUES(@id,@profile,@latency) ON CONFLICT(middleware_id) DO UPDATE SET profile=excluded.profile,latency_ms=excluded.latency_ms;";
        command.Parameters.AddWithValue("id", middlewareId);
        command.Parameters.AddWithValue("profile", profile.Profile);
        command.Parameters.AddWithValue("latency", profile.LatencyMs);
        await command.ExecuteNonQueryAsync(token);
    }

    internal async Task<bool> IsAuthorizedAsync(string middlewareId, string? token, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM middleware WHERE id=@id AND token=@token;";
        command.Parameters.AddWithValue("id", middlewareId);
        command.Parameters.AddWithValue("token", token ?? string.Empty);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken)) == 1;
    }

    private Task<NpgsqlConnection> OpenAsync(CancellationToken token) => _source.OpenConnectionAsync(token).AsTask();

    private static async Task SupersedeAsync(NpgsqlConnection connection, NpgsqlTransaction tx, string middlewareId, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "UPDATE manifest SET status='SUPERSEDED' WHERE middleware_id=@id AND status='ACTIVE';";
        command.Parameters.AddWithValue("id", middlewareId);
        await command.ExecuteNonQueryAsync(token);
    }

    private static async Task InsertManifestAsync(NpgsqlConnection connection, NpgsqlTransaction tx, string middlewareId,
        string manifestId, string transportId, long version, DateTimeOffset expiresAt, IReadOnlyList<ManifestItem> items, CancellationToken token)
    {
        await using (var manifest = connection.CreateCommand())
        {
            manifest.Transaction = tx;
            manifest.CommandText = "INSERT INTO manifest(middleware_id,manifest_id,transport_id,version,status,expires_at,checksum,item_count) VALUES(@id,@manifest,@transport,@version,'ACTIVE',@expires,@checksum,@count);";
            manifest.Parameters.AddWithValue("id", middlewareId);
            manifest.Parameters.AddWithValue("manifest", manifestId);
            manifest.Parameters.AddWithValue("transport", transportId);
            manifest.Parameters.AddWithValue("version", version);
            manifest.Parameters.AddWithValue("expires", expiresAt);
            manifest.Parameters.AddWithValue("checksum", ManifestValidator.ComputeChecksum(items.Select(x => x.Epc)));
            manifest.Parameters.AddWithValue("count", items.Count);
            await manifest.ExecuteNonQueryAsync(token);
        }
        foreach (var item in items)
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = tx;
            insert.CommandText = "INSERT INTO manifest_item VALUES(@id,@manifest,@version,@inventory,@epc);";
            insert.Parameters.AddWithValue("id", middlewareId);
            insert.Parameters.AddWithValue("manifest", manifestId);
            insert.Parameters.AddWithValue("version", version);
            insert.Parameters.AddWithValue("inventory", item.InventoryId);
            insert.Parameters.AddWithValue("epc", item.Epc);
            await insert.ExecuteNonQueryAsync(token);
        }
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, CancellationToken token, NpgsqlTransaction? tx = null)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(token);
    }
}

internal sealed record ManifestRevocation(string RequestId, IReadOnlyList<string> Epcs, string Reason = "RETURNED_TO_STORAGE",
    string? RequestedBy = null, DateTimeOffset? OccurredAt = null);
internal sealed record RevocationResult(string ManifestId, long PreviousVersion, long NewVersion,
    IReadOnlyList<string> RevokedEpcs, bool AlreadyApplied);
internal sealed record ManifestResponse(string ManifestId, string TransportId, long Version, string Status,
    DateTimeOffset ExpiresAt, string Checksum, int ItemCount, IReadOnlyList<ManifestItem> Items);
internal sealed record FaultProfile(string Profile, int LatencyMs);
internal sealed record ManifestActivation(string ManifestId = "TR-2026-00129", string TransportId = "TR-2026-00129",
    long Version = 18, int Count = 100, int ExpiresMinutes = 30, IReadOnlyList<ManifestItem>? Items = null);
