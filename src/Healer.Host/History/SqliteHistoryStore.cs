using Healer.Core.Abstractions;
using Healer.Core.History;
using Healer.Core.Models;
using Microsoft.Data.Sqlite;

namespace Healer.Host.History;

/// <summary>
/// SQLite-backed IHealthHistoryStore. Plain parameterized ADO.NET via Microsoft.Data.Sqlite — no
/// EF Core, whose LINQ/query-translation path isn't reliably Native AOT/trim-safe. WAL journal mode
/// plus incremental auto-vacuum: WAL so the history DB survives the exact kind of mid-write crash
/// Healer exists to guard against, and incremental auto-vacuum because a plain DELETE-based retention
/// policy alone does NOT shrink the file — it only frees pages inside it.
/// </summary>
public sealed class SqliteHistoryStore : IHealthHistoryStore
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _initialized;

    public SqliteHistoryStore(string dbPath)
    {
        var directory = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _connectionString = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
    }

    public async Task RecordHostSnapshotAsync(HostMetrics metrics, DateTimeOffset timestampUtc, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(ct);

        await using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT OR REPLACE INTO host_snapshots (ts, mem_used_pct, swap_used_pct, load_avg_1, load_avg_5, load_avg_15) VALUES ($ts, $mem, $swap, $l1, $l5, $l15);";
            cmd.Parameters.AddWithValue("$ts", timestampUtc.ToUnixTimeSeconds());
            cmd.Parameters.AddWithValue("$mem", metrics.MemUsedPercent);
            cmd.Parameters.AddWithValue("$swap", metrics.SwapUsedPercent);
            cmd.Parameters.AddWithValue("$l1", metrics.LoadAvg1);
            cmd.Parameters.AddWithValue("$l5", metrics.LoadAvg5);
            cmd.Parameters.AddWithValue("$l15", metrics.LoadAvg15);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        foreach (var (mount, pct) in metrics.DiskUsedPercentByMount)
        {
            await using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT OR REPLACE INTO disk_snapshots (ts, mount_path, used_pct) VALUES ($ts, $mount, $pct);";
            cmd.Parameters.AddWithValue("$ts", timestampUtc.ToUnixTimeSeconds());
            cmd.Parameters.AddWithValue("$mount", mount);
            cmd.Parameters.AddWithValue("$pct", pct);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
    }

    public async Task RecordContainerSnapshotAsync(IReadOnlyList<ContainerInfo> containers, DateTimeOffset timestampUtc, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(ct);

        foreach (var container in containers)
        {
            await using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT OR REPLACE INTO container_snapshots
                    (ts, container_name, cpu_pct, mem_used_bytes, mem_limit_bytes, health_status, restart_count)
                VALUES ($ts, $name, $cpu, $memUsed, $memLimit, $health, $restarts);
                """;
            cmd.Parameters.AddWithValue("$ts", timestampUtc.ToUnixTimeSeconds());
            cmd.Parameters.AddWithValue("$name", container.Name);
            cmd.Parameters.AddWithValue("$cpu", container.CpuPercent);
            cmd.Parameters.AddWithValue("$memUsed", container.MemUsedBytes);
            cmd.Parameters.AddWithValue("$memLimit", (object?)container.MemLimitBytes ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$health", container.HealthStatus.ToString());
            cmd.Parameters.AddWithValue("$restarts", container.RestartCount);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
    }

    public async Task RecordActionAsync(ActionOutcome outcome, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO actions (ts, action_type, target, trigger_reason, dry_run, outcome, detail)
            VALUES ($ts, $type, $target, $reason, $dryRun, $outcome, $detail);
            """;
        cmd.Parameters.AddWithValue("$ts", outcome.TimestampUtc.ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("$type", outcome.Action.Type.ToString());
        cmd.Parameters.AddWithValue("$target", outcome.Action.Target);
        cmd.Parameters.AddWithValue("$reason", outcome.Action.Reason);
        cmd.Parameters.AddWithValue("$dryRun", outcome.Status == ActionOutcomeStatus.DryRun ? 1 : 0);
        cmd.Parameters.AddWithValue("$outcome", outcome.Status.ToString());
        cmd.Parameters.AddWithValue("$detail", (object?)outcome.Detail ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task PruneAsync(RetentionPolicy policy, DateTimeOffset nowUtc, CancellationToken ct)
    {
        var snapshotCutoff = RetentionCutoffCalculator.SnapshotCutoffUtc(policy, nowUtc).ToUnixTimeSeconds();
        var actionCutoff = RetentionCutoffCalculator.ActionCutoffUtc(policy, nowUtc).ToUnixTimeSeconds();

        await using var connection = await OpenAsync(ct);

        await DeleteOlderThanAsync(connection, "host_snapshots", snapshotCutoff, ct);
        await DeleteOlderThanAsync(connection, "disk_snapshots", snapshotCutoff, ct);
        await DeleteOlderThanAsync(connection, "container_snapshots", snapshotCutoff, ct);
        await DeleteOlderThanAsync(connection, "actions", actionCutoff, ct);

        // Bounded incremental vacuum — a plain DELETE frees pages inside the file but does not
        // shrink it; this reclaims freed pages back to the OS a little at a time rather than a full
        // VACUUM (which needs ~2x the DB size in temp space and would be heavy on a constrained box).
        await ExecuteAsync(connection, "PRAGMA incremental_vacuum(1000);", ct);
    }

    public async Task<PagedResult<ActionHistoryRecord>> QueryActionsAsync(int page, int pageSize, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT ts, action_type, target, trigger_reason, dry_run, outcome, detail FROM actions ORDER BY ts DESC LIMIT $limit OFFSET $offset;";
        cmd.Parameters.AddWithValue("$limit", pageSize + 1);
        cmd.Parameters.AddWithValue("$offset", page * pageSize);

        var records = new List<ActionHistoryRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            records.Add(new ActionHistoryRecord
            {
                TimestampUtc = DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(0)),
                ActionType = Enum.Parse<ActionType>(reader.GetString(1)),
                Target = reader.GetString(2),
                TriggerReason = reader.GetString(3),
                DryRun = reader.GetInt32(4) != 0,
                Outcome = Enum.Parse<ActionOutcomeStatus>(reader.GetString(5)),
                Detail = reader.IsDBNull(6) ? null : reader.GetString(6),
            });
        }

        var hasMore = records.Count > pageSize;
        if (hasMore)
        {
            records.RemoveAt(records.Count - 1);
        }

        return new PagedResult<ActionHistoryRecord>(records, hasMore);
    }

    public async Task<IReadOnlyList<TrendPoint>> QueryTrendAsync(string metricName, DateTimeOffset sinceUtc, CancellationToken ct)
    {
        var (sql, filter) = ResolveTrendQuery(metricName);

        await using var connection = await OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$since", sinceUtc.ToUnixTimeSeconds());
        if (filter is not null)
        {
            cmd.Parameters.AddWithValue("$filter", filter);
        }

        var points = new List<TrendPoint>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            points.Add(new TrendPoint(DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(0)), reader.GetDouble(1)));
        }

        return points;
    }

    private static (string Sql, string? Filter) ResolveTrendQuery(string metricName) => metricName switch
    {
        "host.mem" => ("SELECT ts, mem_used_pct FROM host_snapshots WHERE ts >= $since ORDER BY ts;", null),
        "host.swap" => ("SELECT ts, swap_used_pct FROM host_snapshots WHERE ts >= $since ORDER BY ts;", null),
        var m when m.StartsWith("host.disk.", StringComparison.Ordinal) =>
            ("SELECT ts, used_pct FROM disk_snapshots WHERE ts >= $since AND mount_path = $filter ORDER BY ts;", m["host.disk.".Length..]),
        var m when m.StartsWith("container.", StringComparison.Ordinal) && m.EndsWith(".mem", StringComparison.Ordinal) =>
            ("SELECT ts, mem_used_bytes FROM container_snapshots WHERE ts >= $since AND container_name = $filter ORDER BY ts;", m["container.".Length..^".mem".Length]),
        var m when m.StartsWith("container.", StringComparison.Ordinal) && m.EndsWith(".cpu", StringComparison.Ordinal) =>
            ("SELECT ts, cpu_pct FROM container_snapshots WHERE ts >= $since AND container_name = $filter ORDER BY ts;", m["container.".Length..^".cpu".Length]),
        _ => throw new ArgumentException($"Unknown metric name: {metricName}", nameof(metricName)),
    };

    private static async Task DeleteOlderThanAsync(SqliteConnection connection, string table, long cutoffUnixSeconds, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"DELETE FROM {table} WHERE ts < $cutoff;";
        cmd.Parameters.AddWithValue("$cutoff", cutoffUnixSeconds);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);
        await EnsureInitializedAsync(connection, ct);
        return connection;
    }

    private async Task EnsureInitializedAsync(SqliteConnection connection, CancellationToken ct)
    {
        if (_initialized)
        {
            return;
        }

        await _initLock.WaitAsync(ct);
        try
        {
            if (_initialized)
            {
                return;
            }

            await ExecuteAsync(connection, "PRAGMA journal_mode=WAL;", ct);
            await ExecuteAsync(connection, "PRAGMA synchronous=NORMAL;", ct);
            await ExecuteAsync(connection, "PRAGMA auto_vacuum=INCREMENTAL;", ct);

            await ExecuteAsync(connection, """
                CREATE TABLE IF NOT EXISTS host_snapshots (
                    ts INTEGER PRIMARY KEY,
                    mem_used_pct REAL, swap_used_pct REAL,
                    load_avg_1 REAL, load_avg_5 REAL, load_avg_15 REAL
                );
                """, ct);
            await ExecuteAsync(connection, """
                CREATE TABLE IF NOT EXISTS disk_snapshots (
                    ts INTEGER, mount_path TEXT, used_pct REAL,
                    PRIMARY KEY (ts, mount_path)
                );
                """, ct);
            await ExecuteAsync(connection, """
                CREATE TABLE IF NOT EXISTS container_snapshots (
                    ts INTEGER, container_name TEXT, cpu_pct REAL,
                    mem_used_bytes INTEGER, mem_limit_bytes INTEGER,
                    health_status TEXT, restart_count INTEGER,
                    PRIMARY KEY (ts, container_name)
                );
                """, ct);
            await ExecuteAsync(connection, """
                CREATE TABLE IF NOT EXISTS actions (
                    id INTEGER PRIMARY KEY AUTOINCREMENT, ts INTEGER,
                    action_type TEXT, target TEXT, trigger_reason TEXT,
                    dry_run INTEGER, outcome TEXT, detail TEXT
                );
                """, ct);
            await ExecuteAsync(connection, "CREATE INDEX IF NOT EXISTS idx_actions_ts ON actions(ts);", ct);

            _initialized = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
