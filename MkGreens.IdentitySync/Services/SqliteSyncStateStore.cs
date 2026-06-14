using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using MkGreens.IdentitySync.Configuration;
using MkGreens.IdentitySync.Models;

namespace MkGreens.IdentitySync.Services;

public sealed class SqliteSyncStateStore : ISyncStateStore
{
    private readonly string _connectionString;
    private readonly ILogger<SqliteSyncStateStore> _logger;
    private bool _initialised;

    public SqliteSyncStateStore(IOptions<SyncOptions> syncOptions, ILogger<SqliteSyncStateStore> logger)
    {
        ArgumentNullException.ThrowIfNull(syncOptions);

        _logger = logger;
        var configuredPath = syncOptions.Value.StateStorePath;
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            throw new InvalidOperationException("Sync:StateStorePath must be configured.");
        }

        var fullPath = Path.IsPathRooted(configuredPath)
            ? configuredPath
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, configuredPath));

        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (_initialised)
        {
            return;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await ExecuteNonQueryAsync(connection,
            """
            CREATE TABLE IF NOT EXISTS sync_runs (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                started_at TEXT NOT NULL,
                finished_at TEXT NULL,
                status TEXT NOT NULL,
                people_seen INTEGER NOT NULL DEFAULT 0,
                people_skipped_missing_email INTEGER NOT NULL DEFAULT 0,
                users_created INTEGER NOT NULL DEFAULT 0,
                users_updated INTEGER NOT NULL DEFAULT 0,
                users_disabled INTEGER NOT NULL DEFAULT 0,
                groups_created INTEGER NOT NULL DEFAULT 0,
                group_memberships_added INTEGER NOT NULL DEFAULT 0,
                group_memberships_removed INTEGER NOT NULL DEFAULT 0,
                errors TEXT NULL
            );
            """,
            cancellationToken);

        await ExecuteNonQueryAsync(connection,
            """
            CREATE TABLE IF NOT EXISTS person_links (
                action_network_person_id TEXT PRIMARY KEY,
                keycloak_user_id TEXT NOT NULL,
                email TEXT NULL,
                display_name TEXT NULL,
                last_synced_hash TEXT NULL,
                last_seen_at TEXT NOT NULL,
                last_synced_at TEXT NOT NULL
            );
            """,
            cancellationToken);

        _initialised = true;
        _logger.LogInformation("SQLite sync state initialised at {ConnectionString}", _connectionString);
    }

    public async Task<long> StartRunAsync(DateTimeOffset startedAt, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO sync_runs (started_at, status)
            VALUES ($startedAt, 'running');
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$startedAt", startedAt.UtcDateTime.ToString("O"));

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt64(result);
    }

    public async Task CompleteRunAsync(long runId, SyncRunStats stats, CancellationToken cancellationToken)
    {
        await UpdateRunAsync(runId, "completed", stats, null, cancellationToken);
    }

    public async Task FailRunAsync(long runId, SyncRunStats stats, Exception exception, CancellationToken cancellationToken)
    {
        var errors = new List<string>(stats.Errors)
        {
            exception.ToString()
        };

        stats.Errors.Clear();
        stats.Errors.AddRange(errors);

        await UpdateRunAsync(runId, "failed", stats, exception.Message, cancellationToken);
    }

    public async Task<IReadOnlyDictionary<string, PersonLinkRecord>> GetAllLinksAsync(CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);

        var records = new Dictionary<string, PersonLinkRecord>(StringComparer.OrdinalIgnoreCase);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT action_network_person_id, keycloak_user_id, email, display_name, last_synced_hash, last_seen_at, last_synced_at
            FROM person_links;
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var record = new PersonLinkRecord(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                DateTimeOffset.Parse(reader.GetString(5)),
                DateTimeOffset.Parse(reader.GetString(6)));

            records[record.ActionNetworkPersonId] = record;
        }

        return records;
    }

    public async Task UpsertPersonLinkAsync(PersonLinkRecord link, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO person_links (
                action_network_person_id,
                keycloak_user_id,
                email,
                display_name,
                last_synced_hash,
                last_seen_at,
                last_synced_at
            )
            VALUES (
                $actionNetworkPersonId,
                $keycloakUserId,
                $email,
                $displayName,
                $lastSyncedHash,
                $lastSeenAt,
                $lastSyncedAt
            )
            ON CONFLICT(action_network_person_id) DO UPDATE SET
                keycloak_user_id = excluded.keycloak_user_id,
                email = excluded.email,
                display_name = excluded.display_name,
                last_synced_hash = excluded.last_synced_hash,
                last_seen_at = excluded.last_seen_at,
                last_synced_at = excluded.last_synced_at;
            """;

        command.Parameters.AddWithValue("$actionNetworkPersonId", link.ActionNetworkPersonId);
        command.Parameters.AddWithValue("$keycloakUserId", link.KeycloakUserId);
        command.Parameters.AddWithValue("$email", (object?)link.Email ?? DBNull.Value);
        command.Parameters.AddWithValue("$displayName", (object?)link.DisplayName ?? DBNull.Value);
        command.Parameters.AddWithValue("$lastSyncedHash", (object?)link.LastSyncedHash ?? DBNull.Value);
        command.Parameters.AddWithValue("$lastSeenAt", link.LastSeenAt.UtcDateTime.ToString("O"));
        command.Parameters.AddWithValue("$lastSyncedAt", link.LastSyncedAt.UtcDateTime.ToString("O"));

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task UpdateRunAsync(long runId, string status, SyncRunStats stats, string? additionalError, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);

        var errors = stats.ErrorSummary;
        if (!string.IsNullOrWhiteSpace(additionalError) && !errors.Contains(additionalError, StringComparison.Ordinal))
        {
            errors = string.IsNullOrWhiteSpace(errors)
                ? additionalError
                : $"{errors}{Environment.NewLine}{additionalError}";
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE sync_runs
            SET
                finished_at = $finishedAt,
                status = $status,
                people_seen = $peopleSeen,
                people_skipped_missing_email = $peopleSkippedMissingEmail,
                users_created = $usersCreated,
                users_updated = $usersUpdated,
                users_disabled = $usersDisabled,
                groups_created = $groupsCreated,
                group_memberships_added = $groupMembershipsAdded,
                group_memberships_removed = $groupMembershipsRemoved,
                errors = $errors
            WHERE id = $runId;
            """;

        command.Parameters.AddWithValue("$finishedAt", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$peopleSeen", stats.PeopleSeen);
        command.Parameters.AddWithValue("$peopleSkippedMissingEmail", stats.PeopleSkippedMissingEmail);
        command.Parameters.AddWithValue("$usersCreated", stats.UsersCreated);
        command.Parameters.AddWithValue("$usersUpdated", stats.UsersUpdated);
        command.Parameters.AddWithValue("$usersDisabled", stats.UsersDisabled);
        command.Parameters.AddWithValue("$groupsCreated", stats.GroupsCreated);
        command.Parameters.AddWithValue("$groupMembershipsAdded", stats.GroupMembershipsAdded);
        command.Parameters.AddWithValue("$groupMembershipsRemoved", stats.GroupMembershipsRemoved);
        command.Parameters.AddWithValue("$errors", string.IsNullOrWhiteSpace(errors) ? DBNull.Value : errors);
        command.Parameters.AddWithValue("$runId", runId);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static async Task ExecuteNonQueryAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
