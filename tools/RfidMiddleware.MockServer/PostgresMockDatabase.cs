using System.Data;
using System.Security.Cryptography;
using System.Text;
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
            ALTER TABLE middleware ADD COLUMN IF NOT EXISTS mobile_token text NOT NULL DEFAULT 'dev-mobile-token';
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
            CREATE TABLE IF NOT EXISTS mobile_request(
              middleware_id text NOT NULL, role text NOT NULL, request_id text NOT NULL,
              operation text NOT NULL, payload_hash text NOT NULL, response jsonb NOT NULL,
              created_at timestamptz NOT NULL, PRIMARY KEY(middleware_id,role,request_id));
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
            "TRUNCATE mobile_request,revocation_audit,manifest_item,manifest,fault_profile,middleware CASCADE;", token, tx);
        await ExecuteAsync(connection, """
            INSERT INTO middleware(id,token,mobile_token) VALUES('WH-A-GATE-01','dev-token','dev-mobile-token');
            INSERT INTO fault_profile(middleware_id,profile,latency_ms) VALUES('WH-A-GATE-01','none',0);
            """, token, tx);
        var items = Enumerable.Range(0, count)
            .Select(i => new ManifestItem($"INV{i + 1:D6}", $"E2801170{i:X16}"))
            .ToArray();
        await InsertActiveManifestAsync(connection, tx, "WH-A-GATE-01", "TR-2026-00128", "TR-2026-00128",
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
        await InsertActiveManifestAsync(connection, tx, middlewareId, request.ManifestId, request.TransportId,
            request.Version, DateTimeOffset.UtcNow.AddMinutes(Math.Clamp(request.ExpiresMinutes, 1, 43_200)), items, token);
        await tx.CommitAsync(token);
    }

    internal async Task<AuthorizationResult> AuthorizeAsync(
        string middlewareId, string manifestId, ManifestAuthorization request, CancellationToken token)
    {
        ValidateRequest(request.RequestId, request.ExpectedVersion);
        if (string.IsNullOrWhiteSpace(request.InventoryId)) throw new InvalidDataException("inventoryId is required.");
        var epc = EpcNormalizer.Normalize(request.Epc);
        var hash = Hash(request);
        await using var connection = await OpenAsync(token);
        await using var tx = await connection.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var prior = await GetPriorAsync<AuthorizationResult>(connection, tx, middlewareId, request.RequestId,
            "AUTHORIZE", hash, token);
        if (prior is not null) { await tx.CommitAsync(token); return prior with { AlreadyApplied = true }; }

        var current = await LoadActiveAsync(connection, tx, middlewareId, manifestId, token);
        EnsureVersion(request.ExpectedVersion, current.Version);
        if (current.Items.Any(x => x.Epc == epc))
            throw new InvalidOperationException("EPC is already authorized by this manifest.");
        var newVersion = current.Version + 1;
        var items = current.Items.Append(new ManifestItem(request.InventoryId.Trim(), epc)).ToArray();
        await SupersedeAsync(connection, tx, middlewareId, token);
        await InsertActiveManifestAsync(connection, tx, middlewareId, manifestId, current.TransportId, newVersion,
            RefreshExpiry(current.ExpiresAt), items, token);
        var result = new AuthorizationResult(manifestId, current.Version, newVersion, request.InventoryId.Trim(), epc, false);
        await StoreRequestAsync(connection, tx, middlewareId, request.RequestId, "AUTHORIZE", hash, result, token);
        await tx.CommitAsync(token);
        return result;
    }

    internal async Task<RevocationResult> RevokeAsync(
        string middlewareId, string manifestId, ManifestRevocation request, CancellationToken token)
    {
        ValidateRequest(request.RequestId, request.ExpectedVersion);
        if (request.Epcs is not { Count: > 0 }) throw new InvalidDataException("At least one EPC is required.");
        if (string.IsNullOrWhiteSpace(request.Reason)) throw new InvalidDataException("reason is required.");
        if (string.IsNullOrWhiteSpace(request.RequestedBy)) throw new InvalidDataException("requestedBy is required.");
        var revoked = request.Epcs.Select(EpcNormalizer.Normalize).ToHashSet(StringComparer.Ordinal);
        var hash = Hash(request);
        await using var connection = await OpenAsync(token);
        await using var tx = await connection.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var prior = await GetPriorAsync<RevocationResult>(connection, tx, middlewareId, request.RequestId,
            "REVOKE", hash, token);
        if (prior is not null) { await tx.CommitAsync(token); return prior with { AlreadyApplied = true }; }

        var current = await LoadActiveAsync(connection, tx, middlewareId, manifestId, token);
        EnsureVersion(request.ExpectedVersion, current.Version);
        var activeEpcs = current.Items.Select(x => x.Epc).ToHashSet(StringComparer.Ordinal);
        var missing = revoked.Where(x => !activeEpcs.Contains(x)).ToArray();
        if (missing.Length > 0)
            throw new InvalidDataException($"EPC is not active in this manifest: {string.Join(',', missing)}");
        var remaining = current.Items.Where(x => !revoked.Contains(x.Epc)).ToArray();
        if (remaining.Length == 0 && !request.CompleteBatch)
            throw new InvalidOperationException("Revoking the final EPC requires completeBatch=true or the completion endpoint.");
        if (remaining.Length > 0 && request.CompleteBatch)
            throw new InvalidDataException("completeBatch may be used only when all remaining EPCs are revoked.");

        var newVersion = current.Version + 1;
        await SupersedeAsync(connection, tx, middlewareId, token);
        if (request.CompleteBatch)
            await InsertCompletedManifestAsync(connection, tx, middlewareId, manifestId, current.TransportId, newVersion, token);
        else
            await InsertActiveManifestAsync(connection, tx, middlewareId, manifestId, current.TransportId, newVersion,
                RefreshExpiry(current.ExpiresAt), remaining, token);

        await InsertRevocationAuditAsync(connection, tx, middlewareId, manifestId, request, current.Version,
            newVersion, revoked, token);
        var result = new RevocationResult(manifestId, current.Version, newVersion, revoked.Order().ToArray(),
            request.CompleteBatch ? "COMPLETED" : "ACTIVE", false);
        await StoreRequestAsync(connection, tx, middlewareId, request.RequestId, "REVOKE", hash, result, token);
        await tx.CommitAsync(token);
        return result;
    }

    internal async Task<CompletionResult> CompleteAsync(
        string middlewareId, string manifestId, ManifestCompletionRequest request, CancellationToken token)
    {
        ValidateRequest(request.RequestId, request.ExpectedVersion);
        var hash = Hash(request);
        await using var connection = await OpenAsync(token);
        await using var tx = await connection.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var prior = await GetPriorAsync<CompletionResult>(connection, tx, middlewareId, request.RequestId,
            "COMPLETE", hash, token);
        if (prior is not null) { await tx.CommitAsync(token); return prior with { AlreadyApplied = true }; }

        var current = await LoadActiveAsync(connection, tx, middlewareId, manifestId, token);
        EnsureVersion(request.ExpectedVersion, current.Version);
        var newVersion = current.Version + 1;
        await SupersedeAsync(connection, tx, middlewareId, token);
        await InsertCompletedManifestAsync(connection, tx, middlewareId, manifestId, current.TransportId, newVersion, token);
        var result = new CompletionResult(manifestId, current.Version, newVersion, "COMPLETED", false);
        await StoreRequestAsync(connection, tx, middlewareId, request.RequestId, "COMPLETE", hash, result, token);
        await tx.CommitAsync(token);
        return result;
    }

    internal async Task<ManifestResponse?> GetManifestAsync(string middlewareId, CancellationToken token)
    {
        await using var connection = await OpenAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT manifest_id,transport_id,version,status,expires_at,checksum,item_count
            FROM manifest WHERE middleware_id=@id AND status IN ('ACTIVE','COMPLETED')
            ORDER BY version DESC LIMIT 1;
            """;
        command.Parameters.AddWithValue("id", middlewareId);
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) return null;
        var response = new ManifestResponse(reader.GetString(0), reader.GetString(1), reader.GetInt64(2), reader.GetString(3),
            new DateTimeOffset(reader.GetDateTime(4).ToUniversalTime()), reader.GetString(5), reader.GetInt32(6), []);
        await reader.DisposeAsync();
        if (response.Status == "COMPLETED") return response;
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

    internal async Task<bool> IsAuthorizedAsync(
        string middlewareId, string? token, ApiRole role, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = role == ApiRole.Mobile
            ? "SELECT COUNT(*) FROM middleware WHERE id=@id AND mobile_token=@token;"
            : "SELECT COUNT(*) FROM middleware WHERE id=@id AND token=@token;";
        command.Parameters.AddWithValue("id", middlewareId);
        command.Parameters.AddWithValue("token", token ?? string.Empty);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken)) == 1;
    }

    private Task<NpgsqlConnection> OpenAsync(CancellationToken token) => _source.OpenConnectionAsync(token).AsTask();

    private static async Task<ActiveManifest> LoadActiveAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, string middlewareId, string manifestId, CancellationToken token)
    {
        string transportId;
        long version;
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
            version = reader.GetInt64(1);
            expiresAt = new DateTimeOffset(reader.GetDateTime(2).ToUniversalTime());
        }
        var items = new List<ManifestItem>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = tx;
            command.CommandText = "SELECT inventory_id,epc FROM manifest_item WHERE middleware_id=@id AND manifest_id=@manifest AND version=@version ORDER BY epc;";
            command.Parameters.AddWithValue("id", middlewareId);
            command.Parameters.AddWithValue("manifest", manifestId);
            command.Parameters.AddWithValue("version", version);
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token)) items.Add(new(reader.GetString(0), reader.GetString(1)));
        }
        return new(transportId, version, expiresAt, items);
    }

    private static void ValidateRequest(string requestId, long expectedVersion)
    {
        if (string.IsNullOrWhiteSpace(requestId)) throw new InvalidDataException("requestId is required.");
        if (expectedVersion < 1) throw new InvalidDataException("expectedVersion must be positive.");
    }

    private static void EnsureVersion(long expected, long current)
    {
        if (expected != current) throw new VersionConflictException(current);
    }

    private static DateTimeOffset RefreshExpiry(DateTimeOffset current) =>
        current > DateTimeOffset.UtcNow ? current : DateTimeOffset.UtcNow.AddMinutes(30);

    private static string Hash<T>(T request) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request))));

    private static async Task<T?> GetPriorAsync<T>(NpgsqlConnection connection, NpgsqlTransaction tx,
        string middlewareId, string requestId, string operation, string hash, CancellationToken token) where T : class
    {
        await using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "SELECT operation,payload_hash,response::text FROM mobile_request WHERE middleware_id=@id AND role='MOBILE' AND request_id=@request;";
        command.Parameters.AddWithValue("id", middlewareId);
        command.Parameters.AddWithValue("request", requestId);
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) return null;
        if (reader.GetString(0) != operation || reader.GetString(1) != hash)
            throw new IdempotencyConflictException();
        return JsonSerializer.Deserialize<T>(reader.GetString(2))
            ?? throw new InvalidDataException("Stored idempotency response is invalid.");
    }

    private static async Task StoreRequestAsync<T>(NpgsqlConnection connection, NpgsqlTransaction tx,
        string middlewareId, string requestId, string operation, string hash, T response, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "INSERT INTO mobile_request(middleware_id,role,request_id,operation,payload_hash,response,created_at) VALUES(@id,'MOBILE',@request,@operation,@hash,@response,@now);";
        command.Parameters.AddWithValue("id", middlewareId);
        command.Parameters.AddWithValue("request", requestId);
        command.Parameters.AddWithValue("operation", operation);
        command.Parameters.AddWithValue("hash", hash);
        command.Parameters.AddWithValue("response", NpgsqlDbType.Jsonb, JsonSerializer.Serialize(response));
        command.Parameters.AddWithValue("now", DateTimeOffset.UtcNow);
        await command.ExecuteNonQueryAsync(token);
    }

    private static async Task InsertRevocationAuditAsync(NpgsqlConnection connection, NpgsqlTransaction tx,
        string middlewareId, string manifestId, ManifestRevocation request, long previousVersion, long newVersion,
        IReadOnlySet<string> revoked, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            INSERT INTO revocation_audit(middleware_id,request_id,manifest_id,previous_version,new_version,
              revoked_epcs,reason,requested_by,occurred_at,created_at)
            VALUES(@id,@request,@manifest,@previous,@version,@epcs,@reason,@by,@occurred,@created);
            """;
        command.Parameters.AddWithValue("id", middlewareId);
        command.Parameters.AddWithValue("request", request.RequestId);
        command.Parameters.AddWithValue("manifest", manifestId);
        command.Parameters.AddWithValue("previous", previousVersion);
        command.Parameters.AddWithValue("version", newVersion);
        command.Parameters.AddWithValue("epcs", NpgsqlDbType.Jsonb, JsonSerializer.Serialize(revoked.Order()));
        command.Parameters.AddWithValue("reason", string.IsNullOrWhiteSpace(request.Reason) ? "RETURNED_TO_STORAGE" : request.Reason);
        command.Parameters.AddWithValue("by", (object?)request.RequestedBy ?? DBNull.Value);
        command.Parameters.AddWithValue("occurred", request.OccurredAt ?? DateTimeOffset.UtcNow);
        command.Parameters.AddWithValue("created", DateTimeOffset.UtcNow);
        await command.ExecuteNonQueryAsync(token);
    }

    private static async Task SupersedeAsync(NpgsqlConnection connection, NpgsqlTransaction tx,
        string middlewareId, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "UPDATE manifest SET status='SUPERSEDED' WHERE middleware_id=@id AND status='ACTIVE';";
        command.Parameters.AddWithValue("id", middlewareId);
        await command.ExecuteNonQueryAsync(token);
    }

    private static async Task InsertActiveManifestAsync(NpgsqlConnection connection, NpgsqlTransaction tx,
        string middlewareId, string manifestId, string transportId, long version, DateTimeOffset expiresAt,
        IReadOnlyList<ManifestItem> items, CancellationToken token)
    {
        await InsertManifestRowAsync(connection, tx, middlewareId, manifestId, transportId, version, "ACTIVE",
            expiresAt, ManifestValidator.ComputeChecksum(items.Select(x => x.Epc)), items.Count, token);
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

    private static Task InsertCompletedManifestAsync(NpgsqlConnection connection, NpgsqlTransaction tx,
        string middlewareId, string manifestId, string transportId, long version, CancellationToken token) =>
        InsertManifestRowAsync(connection, tx, middlewareId, manifestId, transportId, version, "COMPLETED",
            DateTimeOffset.UtcNow, string.Empty, 0, token);

    private static async Task InsertManifestRowAsync(NpgsqlConnection connection, NpgsqlTransaction tx,
        string middlewareId, string manifestId, string transportId, long version, string status,
        DateTimeOffset expiresAt, string checksum, int itemCount, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "INSERT INTO manifest(middleware_id,manifest_id,transport_id,version,status,expires_at,checksum,item_count) VALUES(@id,@manifest,@transport,@version,@status,@expires,@checksum,@count);";
        command.Parameters.AddWithValue("id", middlewareId);
        command.Parameters.AddWithValue("manifest", manifestId);
        command.Parameters.AddWithValue("transport", transportId);
        command.Parameters.AddWithValue("version", version);
        command.Parameters.AddWithValue("status", status);
        command.Parameters.AddWithValue("expires", expiresAt);
        command.Parameters.AddWithValue("checksum", checksum);
        command.Parameters.AddWithValue("count", itemCount);
        await command.ExecuteNonQueryAsync(token);
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, CancellationToken token,
        NpgsqlTransaction? tx = null)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(token);
    }

    private sealed record ActiveManifest(string TransportId, long Version, DateTimeOffset ExpiresAt,
        IReadOnlyList<ManifestItem> Items);
}

internal enum ApiRole { Middleware, Mobile }
internal sealed class VersionConflictException(long currentVersion) : Exception("Manifest version conflict.")
{
    internal long CurrentVersion { get; } = currentVersion;
}
internal sealed class IdempotencyConflictException() : Exception("The request ID was already used with different content.");

internal sealed record ManifestAuthorization(string RequestId, long ExpectedVersion, string InventoryId, string Epc,
    string? Note = null);
internal sealed record AuthorizationResult(string ManifestId, long PreviousVersion, long NewVersion,
    string InventoryId, string Epc, bool AlreadyApplied);
internal sealed record ManifestRevocation(string RequestId, long ExpectedVersion, IReadOnlyList<string> Epcs,
    string Reason, string RequestedBy, DateTimeOffset? OccurredAt = null,
    bool CompleteBatch = false);
internal sealed record RevocationResult(string ManifestId, long PreviousVersion, long NewVersion,
    IReadOnlyList<string> RevokedEpcs, string Status, bool AlreadyApplied);
internal sealed record ManifestCompletionRequest(string RequestId, long ExpectedVersion, string? Note = null);
internal sealed record CompletionResult(string ManifestId, long PreviousVersion, long NewVersion,
    string Status, bool AlreadyApplied);
internal sealed record ManifestResponse(string ManifestId, string TransportId, long Version, string Status,
    DateTimeOffset ExpiresAt, string Checksum, int ItemCount, IReadOnlyList<ManifestItem> Items);
internal sealed record FaultProfile(string Profile, int LatencyMs);
internal sealed record ManifestActivation(string ManifestId = "TR-2026-00129", string TransportId = "TR-2026-00129",
    long Version = 18, int Count = 100, int ExpiresMinutes = 30, IReadOnlyList<ManifestItem>? Items = null);
