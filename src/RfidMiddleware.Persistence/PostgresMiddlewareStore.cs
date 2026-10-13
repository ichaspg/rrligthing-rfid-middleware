using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using RfidMiddleware.Core;

namespace RfidMiddleware.Persistence;

public sealed class PostgresMiddlewareStore(string connectionString) : IManifestRepository, IEventRepository, IAsyncDisposable
{
    private readonly NpgsqlDataSource _source = NpgsqlDataSource.Create(connectionString);

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS schema_version(
              version integer PRIMARY KEY, applied_at timestamptz NOT NULL);
            INSERT INTO schema_version(version,applied_at) VALUES(1,now()) ON CONFLICT(version) DO NOTHING;

            CREATE TABLE IF NOT EXISTS manifest(
              manifest_id text NOT NULL, transport_id text NOT NULL, version bigint NOT NULL,
              status text NOT NULL, etag text, checksum text, item_count integer NOT NULL,
              downloaded_at timestamptz NOT NULL, validated_at timestamptz,
              activated_at timestamptz, expires_at timestamptz NOT NULL,
              PRIMARY KEY(manifest_id,version));
            CREATE UNIQUE INDEX IF NOT EXISTS ux_edge_manifest_active
              ON manifest((status)) WHERE status='Active';

            CREATE TABLE IF NOT EXISTS manifest_item(
              manifest_id text NOT NULL, version bigint NOT NULL, inventory_id text NOT NULL, epc text NOT NULL,
              PRIMARY KEY(manifest_id,version,epc),
              FOREIGN KEY(manifest_id,version) REFERENCES manifest(manifest_id,version) ON DELETE CASCADE);

            CREATE TABLE IF NOT EXISTS middleware_state(
              key text PRIMARY KEY, value jsonb NOT NULL, updated_at timestamptz NOT NULL);

            CREATE TABLE IF NOT EXISTS authorization_state(
              singleton boolean PRIMARY KEY DEFAULT true CHECK(singleton),
              state text NOT NULL, manifest_id text, version bigint, updated_at timestamptz NOT NULL);

            CREATE TABLE IF NOT EXISTS scan_event(
              event_id text PRIMARY KEY, manifest_id text, manifest_version bigint, epc text NOT NULL,
              result text NOT NULL, first_seen_at timestamptz NOT NULL, last_seen_at timestamptz NOT NULL,
              read_count integer NOT NULL, reader_id text NOT NULL, antenna_ids jsonb NOT NULL,
              strongest_rssi integer NOT NULL, storage_policy text NOT NULL DEFAULT 'LocalOnly',
              created_at timestamptz NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_scan_event_created_at ON scan_event(created_at);
            CREATE INDEX IF NOT EXISTS ix_scan_event_epc_created_at ON scan_event(epc,created_at);

            INSERT INTO authorization_state(singleton,state,manifest_id,version,updated_at)
            SELECT true,
              CASE WHEN active.manifest_id IS NULL THEN 'Uninitialized' ELSE 'Active' END,
              active.manifest_id,active.version,now()
            FROM (SELECT manifest_id,version FROM manifest WHERE status='Active'
                  ORDER BY activated_at DESC LIMIT 1) active
            RIGHT JOIN (SELECT 1) seed ON true
            ON CONFLICT(singleton) DO NOTHING;
            INSERT INTO schema_version(version,applied_at) VALUES(2,now()) ON CONFLICT(version) DO NOTHING;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task StageAndActivateAsync(ManifestDocument manifest, CancellationToken cancellationToken = default)
    {
        ManifestValidator.Validate(manifest);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        await using (var advisoryLock = connection.CreateCommand())
        {
            advisoryLock.Transaction = transaction;
            advisoryLock.CommandText = "SELECT pg_advisory_xact_lock(7523431901);";
            await advisoryLock.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        }
        await EnsureNewerVersionAsync(connection, transaction, manifest.Version, cancellationToken);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO manifest(manifest_id,transport_id,version,status,etag,checksum,item_count,
                  downloaded_at,validated_at,expires_at)
                VALUES(@id,@transport,@version,'Validated',@etag,@checksum,@count,@downloaded,@now,@expires)
                ON CONFLICT(manifest_id,version) DO UPDATE SET
                  status='Validated',etag=excluded.etag,checksum=excluded.checksum,item_count=excluded.item_count,
                  downloaded_at=excluded.downloaded_at,validated_at=excluded.validated_at,expires_at=excluded.expires_at;
                DELETE FROM manifest_item WHERE manifest_id=@id AND version=@version;
                """;
            command.Parameters.AddWithValue("id", manifest.ManifestId);
            command.Parameters.AddWithValue("transport", manifest.TransportId);
            command.Parameters.AddWithValue("version", manifest.Version);
            command.Parameters.AddWithValue("etag", (object?)manifest.ETag ?? DBNull.Value);
            command.Parameters.AddWithValue("checksum", (object?)manifest.Checksum ?? DBNull.Value);
            command.Parameters.AddWithValue("count", manifest.ItemCount);
            command.Parameters.AddWithValue("downloaded", Utc(manifest.DownloadedAt));
            command.Parameters.AddWithValue("now", now);
            command.Parameters.AddWithValue("expires", Utc(manifest.ExpiresAt));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        foreach (var item in manifest.Items)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO manifest_item(manifest_id,version,inventory_id,epc) VALUES(@id,@version,@inventory,@epc);";
            command.Parameters.AddWithValue("id", manifest.ManifestId);
            command.Parameters.AddWithValue("version", manifest.Version);
            command.Parameters.AddWithValue("inventory", item.InventoryId);
            command.Parameters.AddWithValue("epc", EpcNormalizer.Normalize(item.Epc));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await using (var activate = connection.CreateCommand())
        {
            activate.Transaction = transaction;
            activate.CommandText = """
                UPDATE manifest SET status='Superseded' WHERE status='Active';
                UPDATE manifest SET status='Active',activated_at=@now WHERE manifest_id=@id AND version=@version;
                INSERT INTO middleware_state(key,value,updated_at) VALUES('active_manifest',@value,@now)
                  ON CONFLICT(key) DO UPDATE SET value=excluded.value,updated_at=excluded.updated_at;
                UPDATE authorization_state SET state='Active',manifest_id=@id,version=@version,updated_at=@now
                  WHERE singleton=true;
                """;
            activate.Parameters.AddWithValue("now", now);
            activate.Parameters.AddWithValue("id", manifest.ManifestId);
            activate.Parameters.AddWithValue("version", manifest.Version);
            activate.Parameters.AddWithValue("value", NpgsqlDbType.Jsonb,
                JsonSerializer.Serialize(new { manifest.ManifestId, manifest.Version }));
            await activate.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<AuthorizationStateSnapshot> GetAuthorizationStateAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT state,manifest_id,version,updated_at FROM authorization_state WHERE singleton=true;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return new(AuthorizationState.Uninitialized, null, null, DateTimeOffset.UtcNow);
        if (!Enum.TryParse<AuthorizationState>(reader.GetString(0), true, out var state))
            throw new InvalidDataException($"Unknown authorization state '{reader.GetString(0)}'.");
        return new(state, reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetInt64(2), ReadTimestamp(reader, 3));
    }

    public async Task DeactivateAsync(ManifestCompletion completion, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var advisoryLock = connection.CreateCommand())
        {
            advisoryLock.Transaction = transaction;
            advisoryLock.CommandText = "SELECT pg_advisory_xact_lock(7523431901);";
            await advisoryLock.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        }
        await EnsureNewerVersionAsync(connection, transaction, completion.Version, cancellationToken);
        var now = DateTimeOffset.UtcNow;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE manifest SET status='Completed' WHERE status='Active';
            DELETE FROM middleware_state WHERE key='active_manifest';
            UPDATE authorization_state SET state='Completed',manifest_id=@id,version=@version,updated_at=@now
              WHERE singleton=true;
            """;
        command.Parameters.AddWithValue("id", completion.ManifestId);
        command.Parameters.AddWithValue("version", completion.Version);
        command.Parameters.AddWithValue("now", now);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<ManifestSnapshot?> GetActiveAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT manifest_id,transport_id,version,activated_at,expires_at
            FROM manifest WHERE status='Active' ORDER BY activated_at DESC LIMIT 1;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
        var id = reader.GetString(0);
        var transport = reader.GetString(1);
        var version = reader.GetInt64(2);
        var activated = ReadTimestamp(reader, 3);
        var expires = ReadTimestamp(reader, 4);
        await reader.DisposeAsync().ConfigureAwait(false);
        await using var itemCommand = connection.CreateCommand();
        itemCommand.CommandText = "SELECT inventory_id,epc FROM manifest_item WHERE manifest_id=@id AND version=@version;";
        itemCommand.Parameters.AddWithValue("id", id);
        itemCommand.Parameters.AddWithValue("version", version);
        var items = new List<ManifestItem>();
        await using var itemReader = await itemCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await itemReader.ReadAsync(cancellationToken).ConfigureAwait(false))
            items.Add(new(itemReader.GetString(0), itemReader.GetString(1)));
        return ManifestSnapshot.Create(new ManifestDocument(id, transport, version, "ACTIVE", items.Count, null, null,
            activated, expires, items), activated);
    }

    public Task AppendAsync(ScanEvent scanEvent, CancellationToken cancellationToken = default) =>
        AppendBatchAsync([scanEvent], cancellationToken);

    public async Task AppendBatchAsync(IReadOnlyList<ScanEvent> scanEvents, CancellationToken cancellationToken = default)
    {
        if (scanEvents.Count == 0) return;
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        foreach (var item in scanEvents)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO scan_event(event_id,manifest_id,manifest_version,epc,result,first_seen_at,last_seen_at,
                  read_count,reader_id,antenna_ids,strongest_rssi,storage_policy,created_at)
                VALUES(@event,@manifest,@version,@epc,@result,@first,@last,@count,@reader,@antennas,@rssi,'LocalOnly',@created);
                """;
            command.Parameters.AddWithValue("event", item.EventId);
            command.Parameters.AddWithValue("manifest", (object?)item.ManifestId ?? DBNull.Value);
            command.Parameters.AddWithValue("version", (object?)item.ManifestVersion ?? DBNull.Value);
            command.Parameters.AddWithValue("epc", item.Epc);
            command.Parameters.AddWithValue("result", item.Result.ToString());
            command.Parameters.AddWithValue("first", Utc(item.FirstSeenAt));
            command.Parameters.AddWithValue("last", Utc(item.LastSeenAt));
            command.Parameters.AddWithValue("count", item.ReadCount);
            command.Parameters.AddWithValue("reader", item.ReaderId);
            command.Parameters.AddWithValue("antennas", NpgsqlDbType.Jsonb, JsonSerializer.Serialize(item.AntennaIds.Order()));
            command.Parameters.AddWithValue("rssi", item.StrongestRssi);
            command.Parameters.AddWithValue("created", Utc(item.FirstSeenAt));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<LocalAuditStats> GetStatsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*),
              COUNT(*) FILTER (WHERE result='Authorized'),
              COUNT(*) FILTER (WHERE result='Unauthorized'),
              COUNT(*) FILTER (WHERE result NOT IN ('Authorized','Unauthorized')),
              MAX(last_seen_at)
            FROM scan_event;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        return new(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3),
            reader.IsDBNull(4) ? null : ReadTimestamp(reader, 4));
    }

    public async Task<string> VerifyAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT CASE
              WHEN (SELECT COUNT(*) FROM manifest WHERE status='Active') > 1 THEN 'multiple active manifests'
              WHEN EXISTS(SELECT 1 FROM scan_event WHERE storage_policy <> 'LocalOnly') THEN 'non-local scan policy found'
              ELSE 'ok'
            END;
            """;
        return Convert.ToString(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) ?? "unknown";
    }

    public ValueTask DisposeAsync() => _source.DisposeAsync();

    private Task<NpgsqlConnection> OpenAsync(CancellationToken token) => _source.OpenConnectionAsync(token).AsTask();

    private static async Task EnsureNewerVersionAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, long candidate, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT version FROM authorization_state WHERE singleton=true FOR UPDATE;";
        var current = await command.ExecuteScalarAsync(token).ConfigureAwait(false);
        if (current is not null and not DBNull && candidate <= Convert.ToInt64(current))
            throw new InvalidDataException($"Manifest version {candidate} is not newer than applied version {current}.");
    }

    private static DateTimeOffset Utc(DateTimeOffset value) => value.ToUniversalTime();
    private static DateTimeOffset ReadTimestamp(NpgsqlDataReader reader, int ordinal) =>
        new(reader.GetDateTime(ordinal).ToUniversalTime());
}
