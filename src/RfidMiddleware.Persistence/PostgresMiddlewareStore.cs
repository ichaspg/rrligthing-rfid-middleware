using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using RfidMiddleware.Core;

namespace RfidMiddleware.Persistence;

public sealed class PostgresMiddlewareStore(string connectionString) :
    IManifestRepository, IEventRepository, IScanSessionRepository, IAsyncDisposable
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

            CREATE TABLE IF NOT EXISTS scan_session(
              session_id text PRIMARY KEY, status text NOT NULL,
              generation integer NOT NULL, started_at timestamptz NOT NULL,
              reset_at timestamptz, completed_at timestamptz,
              raw_read_count bigint NOT NULL, unique_epc_count integer NOT NULL,
              last_sequence bigint NOT NULL, updated_at timestamptz NOT NULL);
            CREATE UNIQUE INDEX IF NOT EXISTS ux_scan_session_active
              ON scan_session((status)) WHERE status='Active';

            CREATE TABLE IF NOT EXISTS scan_session_item(
              session_id text NOT NULL, epc text NOT NULL,
              first_seen_at timestamptz NOT NULL, last_seen_at timestamptz NOT NULL,
              read_count bigint NOT NULL, latest_result text NOT NULL,
              antenna_ids integer[] NOT NULL, strongest_rssi integer NOT NULL,
              PRIMARY KEY(session_id,epc),
              FOREIGN KEY(session_id) REFERENCES scan_session(session_id) ON DELETE CASCADE);
            CREATE INDEX IF NOT EXISTS ix_scan_session_completed_at
              ON scan_session(completed_at) WHERE status='Completed';

            INSERT INTO authorization_state(singleton,state,manifest_id,version,updated_at)
            SELECT true,
              CASE WHEN active.manifest_id IS NULL THEN 'Uninitialized' ELSE 'Active' END,
              active.manifest_id,active.version,now()
            FROM (SELECT manifest_id,version FROM manifest WHERE status='Active'
                  ORDER BY activated_at DESC LIMIT 1) active
            RIGHT JOIN (SELECT 1) seed ON true
            ON CONFLICT(singleton) DO NOTHING;
            INSERT INTO schema_version(version,applied_at) VALUES(2,now()) ON CONFLICT(version) DO NOTHING;
            INSERT INTO schema_version(version,applied_at) VALUES(3,now()) ON CONFLICT(version) DO NOTHING;
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

    public Task<ManifestItemsPage?> GetActiveItemsPageAsync(
        int limit, string? afterEpc, CancellationToken cancellationToken = default) =>
        GetItemsPageAsync(limit, afterEpc, cancellationToken);

    public async Task<ScanSessionStartResult> StartOrGetActiveAsync(
        string candidateSessionId, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var advisoryLock = connection.CreateCommand())
        {
            advisoryLock.Transaction = transaction;
            advisoryLock.CommandText = "SELECT pg_advisory_xact_lock(7523431903);";
            await advisoryLock.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var existing = connection.CreateCommand())
        {
            existing.Transaction = transaction;
            existing.CommandText = """
                SELECT session_id,status,generation,started_at,reset_at,completed_at,
                  raw_read_count,unique_epc_count,last_sequence,updated_at
                FROM scan_session WHERE status='Active' LIMIT 1;
                """;
            await using var reader = await existing.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var session = ReadSession(reader);
                await reader.DisposeAsync().ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new(session, false);
            }
        }

        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO scan_session(session_id,status,generation,started_at,reset_at,completed_at,
              raw_read_count,unique_epc_count,last_sequence,updated_at)
            VALUES(@id,'Active',1,@now,NULL,NULL,0,0,0,@now)
            RETURNING session_id,status,generation,started_at,reset_at,completed_at,
              raw_read_count,unique_epc_count,last_sequence,updated_at;
            """;
        insert.Parameters.AddWithValue("id", candidateSessionId);
        insert.Parameters.AddWithValue("now", Utc(now));
        await using var insertedReader = await insert.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await insertedReader.ReadAsync(cancellationToken).ConfigureAwait(false);
        var created = ReadSession(insertedReader);
        await insertedReader.DisposeAsync().ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new(created, true);
    }

    public Task<ScanSessionSnapshot?> GetActiveSessionAsync(CancellationToken cancellationToken = default) =>
        GetSessionByPredicateAsync("status='Active'", null, cancellationToken);

    public Task<ScanSessionSnapshot?> GetSessionAsync(
        string sessionId, CancellationToken cancellationToken = default) =>
        GetSessionByPredicateAsync("session_id=@id", sessionId, cancellationToken);

    public async Task<IReadOnlySet<string>> GetSessionEpcsAsync(
        string sessionId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT epc FROM scan_session_item WHERE session_id=@id;";
        command.Parameters.AddWithValue("id", sessionId);
        var epcs = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) epcs.Add(reader.GetString(0));
        return epcs;
    }

    public async Task ApplySessionObservationsAsync(
        IReadOnlyList<ScanSessionObservation> observations, CancellationToken cancellationToken = default)
    {
        if (observations.Count == 0) return;
        var latest = observations[^1];
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE scan_session SET raw_read_count=@raw,unique_epc_count=@unique,
                  last_sequence=@sequence,updated_at=@updated
                WHERE session_id=@id AND status='Active' AND generation=@generation;
                """;
            update.Parameters.AddWithValue("raw", latest.RawReadCount);
            update.Parameters.AddWithValue("unique", latest.UniqueEpcCount);
            update.Parameters.AddWithValue("sequence", latest.Sequence);
            update.Parameters.AddWithValue("updated", Utc(latest.ObservedAt));
            update.Parameters.AddWithValue("id", latest.SessionId);
            update.Parameters.AddWithValue("generation", latest.Generation);
            if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                throw new InvalidOperationException("The active scan session changed before observations were persisted.");
        }

        foreach (var observation in observations)
        {
            await using var item = connection.CreateCommand();
            item.Transaction = transaction;
            item.CommandText = """
                INSERT INTO scan_session_item(session_id,epc,first_seen_at,last_seen_at,read_count,
                  latest_result,antenna_ids,strongest_rssi)
                VALUES(@id,@epc,@observed,@observed,1,@result,@antennas,@rssi)
                ON CONFLICT(session_id,epc) DO UPDATE SET
                  first_seen_at=LEAST(scan_session_item.first_seen_at,excluded.first_seen_at),
                  last_seen_at=GREATEST(scan_session_item.last_seen_at,excluded.last_seen_at),
                  read_count=scan_session_item.read_count+1,
                  latest_result=excluded.latest_result,
                  antenna_ids=ARRAY(
                    SELECT DISTINCT antenna_id
                    FROM unnest(scan_session_item.antenna_ids || excluded.antenna_ids) AS antenna_id
                    ORDER BY antenna_id),
                  strongest_rssi=GREATEST(scan_session_item.strongest_rssi,excluded.strongest_rssi);
                """;
            item.Parameters.AddWithValue("id", observation.SessionId);
            item.Parameters.AddWithValue("epc", observation.Epc);
            item.Parameters.AddWithValue("observed", Utc(observation.ObservedAt));
            item.Parameters.AddWithValue("result", observation.Outcome.ToString());
            item.Parameters.AddWithValue("antennas", NpgsqlDbType.Array | NpgsqlDbType.Integer,
                new[] { observation.AntennaId });
            item.Parameters.AddWithValue("rssi", observation.Rssi);
            await item.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task<ScanSessionSnapshot> ResetSessionAsync(
        string sessionId, DateTimeOffset now, CancellationToken cancellationToken = default) =>
        ChangeSessionAsync(sessionId, now, complete: false, cancellationToken);

    public Task<ScanSessionSnapshot> CompleteSessionAsync(
        string sessionId, DateTimeOffset now, CancellationToken cancellationToken = default) =>
        ChangeSessionAsync(sessionId, now, complete: true, cancellationToken);

    public async Task<ScanSessionItemsPage?> GetSessionItemsPageAsync(
        string sessionId, int limit, string? afterEpc, CancellationToken cancellationToken = default)
    {
        var session = await GetSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (session is null) return null;
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT epc,first_seen_at,last_seen_at,read_count,latest_result,antenna_ids,strongest_rssi
            FROM scan_session_item WHERE session_id=@id AND (@after IS NULL OR epc>@after)
            ORDER BY epc LIMIT @limit;
            """;
        command.Parameters.AddWithValue("id", sessionId);
        command.Parameters.Add("after", NpgsqlDbType.Text).Value = (object?)afterEpc ?? DBNull.Value;
        command.Parameters.AddWithValue("limit", limit + 1);
        var items = new List<ScanSessionItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!Enum.TryParse<ValidationOutcome>(reader.GetString(4), true, out var outcome))
                throw new InvalidDataException($"Unknown scan-session outcome '{reader.GetString(4)}'.");
            items.Add(new(reader.GetString(0), ReadTimestamp(reader, 1), ReadTimestamp(reader, 2),
                reader.GetInt64(3), outcome, reader.GetFieldValue<int[]>(5), reader.GetInt32(6)));
        }
        string? nextAfterEpc = null;
        if (items.Count > limit)
        {
            items.RemoveAt(items.Count - 1);
            nextAfterEpc = items[^1].Epc;
        }
        return new(sessionId, session.Generation, session.RawReadCount, session.UniqueEpcCount,
            session.LastSequence, items, nextAfterEpc);
    }

    public async Task<int> DeleteCompletedSessionsBeforeAsync(
        DateTimeOffset cutoff, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM scan_session WHERE status='Completed' AND completed_at<@cutoff;";
        command.Parameters.AddWithValue("cutoff", Utc(cutoff));
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
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

    private async Task<ManifestItemsPage?> GetItemsPageAsync(
        int limit, string? afterEpc, CancellationToken token)
    {
        await using var connection = await OpenAsync(token);
        await using var manifestCommand = connection.CreateCommand();
        manifestCommand.CommandText = """
            SELECT manifest_id,transport_id,version,expires_at,item_count
            FROM manifest WHERE status='Active' ORDER BY activated_at DESC LIMIT 1;
            """;
        await using var reader = await manifestCommand.ExecuteReaderAsync(token).ConfigureAwait(false);
        if (!await reader.ReadAsync(token).ConfigureAwait(false)) return null;
        var manifestId = reader.GetString(0);
        var transportId = reader.GetString(1);
        var version = reader.GetInt64(2);
        var expiresAt = ReadTimestamp(reader, 3);
        var itemCount = reader.GetInt32(4);
        await reader.DisposeAsync().ConfigureAwait(false);

        await using var itemCommand = connection.CreateCommand();
        itemCommand.CommandText = """
            SELECT inventory_id,epc FROM manifest_item
            WHERE manifest_id=@id AND version=@version AND (@after IS NULL OR epc>@after)
            ORDER BY epc LIMIT @limit;
            """;
        itemCommand.Parameters.AddWithValue("id", manifestId);
        itemCommand.Parameters.AddWithValue("version", version);
        itemCommand.Parameters.Add("after", NpgsqlDbType.Text).Value = (object?)afterEpc ?? DBNull.Value;
        itemCommand.Parameters.AddWithValue("limit", limit + 1);
        var items = new List<ManifestItem>();
        await using var itemReader = await itemCommand.ExecuteReaderAsync(token).ConfigureAwait(false);
        while (await itemReader.ReadAsync(token).ConfigureAwait(false))
            items.Add(new(itemReader.GetString(0), itemReader.GetString(1)));

        string? nextAfterEpc = null;
        if (items.Count > limit)
        {
            items.RemoveAt(items.Count - 1);
            nextAfterEpc = items[^1].Epc;
        }
        return new(manifestId, transportId, version, expiresAt, itemCount, items, nextAfterEpc);
    }

    private async Task<ScanSessionSnapshot?> GetSessionByPredicateAsync(
        string predicate, string? sessionId, CancellationToken token)
    {
        await using var connection = await OpenAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT session_id,status,generation,started_at,reset_at,completed_at,
              raw_read_count,unique_epc_count,last_sequence,updated_at
            FROM scan_session WHERE {predicate} LIMIT 1;
            """;
        if (sessionId is not null) command.Parameters.AddWithValue("id", sessionId);
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        return await reader.ReadAsync(token).ConfigureAwait(false) ? ReadSession(reader) : null;
    }

    private async Task<ScanSessionSnapshot> ChangeSessionAsync(
        string sessionId, DateTimeOffset now, bool complete, CancellationToken token)
    {
        await using var connection = await OpenAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(token);
        await using var select = connection.CreateCommand();
        select.Transaction = transaction;
        select.CommandText = """
            SELECT session_id,status,generation,started_at,reset_at,completed_at,
              raw_read_count,unique_epc_count,last_sequence,updated_at
            FROM scan_session WHERE session_id=@id FOR UPDATE;
            """;
        select.Parameters.AddWithValue("id", sessionId);
        await using var reader = await select.ExecuteReaderAsync(token).ConfigureAwait(false);
        if (!await reader.ReadAsync(token).ConfigureAwait(false))
            throw new KeyNotFoundException($"Scan session '{sessionId}' was not found.");
        var current = ReadSession(reader);
        await reader.DisposeAsync().ConfigureAwait(false);

        if (current.Status == ScanSessionStatus.Completed)
        {
            if (!complete) throw new InvalidOperationException("A completed scan session cannot be reset.");
            await transaction.CommitAsync(token).ConfigureAwait(false);
            return current;
        }

        if (!complete)
        {
            await using var delete = connection.CreateCommand();
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM scan_session_item WHERE session_id=@id;";
            delete.Parameters.AddWithValue("id", sessionId);
            await delete.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }

        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = complete
            ? """
                UPDATE scan_session SET status='Completed',completed_at=@now,updated_at=@now
                WHERE session_id=@id
                RETURNING session_id,status,generation,started_at,reset_at,completed_at,
                  raw_read_count,unique_epc_count,last_sequence,updated_at;
                """
            : """
                UPDATE scan_session SET generation=generation+1,reset_at=@now,
                  raw_read_count=0,unique_epc_count=0,updated_at=@now
                WHERE session_id=@id
                RETURNING session_id,status,generation,started_at,reset_at,completed_at,
                  raw_read_count,unique_epc_count,last_sequence,updated_at;
                """;
        update.Parameters.AddWithValue("id", sessionId);
        update.Parameters.AddWithValue("now", Utc(now));
        await using var updatedReader = await update.ExecuteReaderAsync(token).ConfigureAwait(false);
        await updatedReader.ReadAsync(token).ConfigureAwait(false);
        var updated = ReadSession(updatedReader);
        await updatedReader.DisposeAsync().ConfigureAwait(false);
        await transaction.CommitAsync(token).ConfigureAwait(false);
        return updated;
    }

    private static ScanSessionSnapshot ReadSession(NpgsqlDataReader reader)
    {
        if (!Enum.TryParse<ScanSessionStatus>(reader.GetString(1), true, out var status))
            throw new InvalidDataException($"Unknown scan-session status '{reader.GetString(1)}'.");
        return new(reader.GetString(0), status, reader.GetInt32(2), ReadTimestamp(reader, 3),
            reader.IsDBNull(4) ? null : ReadTimestamp(reader, 4),
            reader.IsDBNull(5) ? null : ReadTimestamp(reader, 5),
            reader.GetInt64(6), reader.GetInt32(7), reader.GetInt64(8), ReadTimestamp(reader, 9));
    }

    private static DateTimeOffset Utc(DateTimeOffset value) => value.ToUniversalTime();
    private static DateTimeOffset ReadTimestamp(NpgsqlDataReader reader, int ordinal) =>
        new(reader.GetDateTime(ordinal).ToUniversalTime());
}
