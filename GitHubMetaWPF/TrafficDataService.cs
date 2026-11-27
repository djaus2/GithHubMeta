using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace GitHubMetaWPF;

public sealed class TrafficDataService
{
    private readonly string _dbPath;

    public TrafficDataService(string dbPath)
    {
        _dbPath = dbPath;
    }

    public async Task EnsureSchemaAsync()
    {
        await using var db = await OpenAsync();
        var sql = """
                  CREATE TABLE IF NOT EXISTS Repository (
                      Id INTEGER PRIMARY KEY AUTOINCREMENT,
                      [User] TEXT NOT NULL,
                      ProjectName TEXT NOT NULL,
                      UNIQUE([User], ProjectName)
                  );

                  CREATE TABLE IF NOT EXISTS SyncRun (
                      Id INTEGER PRIMARY KEY AUTOINCREMENT,
                      StartedUtc TEXT NOT NULL,
                      CompletedUtc TEXT,
                      Status TEXT NOT NULL,
                      ErrorMessage TEXT
                  );

                  CREATE TABLE IF NOT EXISTS ViewDay (
                      DayIndex INTEGER PRIMARY KEY AUTOINCREMENT,
                      [Date] TEXT NOT NULL,
                      UNIQUE([Date])
                  );

                  CREATE TABLE IF NOT EXISTS CloneDay (
                      RepositoryId INTEGER NOT NULL,
                      DayIndex INTEGER NOT NULL,
                      Clones INTEGER NOT NULL,
                      UniqueCloners INTEGER NOT NULL,
                      Views INTEGER NOT NULL,
                      UniqueViewers INTEGER NOT NULL,
                      SyncRunId INTEGER NOT NULL,
                      PRIMARY KEY (RepositoryId, DayIndex),
                      FOREIGN KEY (RepositoryId) REFERENCES Repository(Id),
                      FOREIGN KEY (DayIndex) REFERENCES ViewDay(DayIndex),
                      FOREIGN KEY (SyncRunId) REFERENCES SyncRun(Id)
                  );
                  """;

        await using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();

        await MigrateTrafficTablesIfNeededAsync(db);
    }

    public async Task SyncRepositoryAsync(RepoConfig repo, string token)
    {
        await EnsureSchemaAsync();

        using var client = CreateGitHubClient(token);
        var views = await GetTrafficDaysAsync(client, repo.Owner, repo.Name, "views");
        var clones = await GetTrafficDaysAsync(client, repo.Owner, repo.Name, "clones");

        await using var db = await OpenAsync();
        var syncRunId = await CreateSyncRunAsync(db, DateTime.UtcNow);

        try
        {
            var repositoryId = await UpsertRepositoryAsync(db, repo.Owner, repo.Name);
            await UpsertTrafficDaysAsync(db, repositoryId, syncRunId, views, clones);
            await CompleteSyncRunAsync(db, syncRunId, DateTime.UtcNow, "Succeeded", null);
        }
        catch (Exception ex)
        {
            await CompleteSyncRunAsync(db, syncRunId, DateTime.UtcNow, "Failed", ex.Message);
            throw;
        }
    }

    public async Task<long> AddOrGetRepositoryAsync(string owner, string projectName)
    {
        await EnsureSchemaAsync();
        await using var db = await OpenAsync();
        return await UpsertRepositoryAsync(db, owner, projectName);
    }

    public async Task RemoveRepositoryAsync(long repositoryId)
    {
        await EnsureSchemaAsync();
        await using var db = await OpenAsync();

        await using var deleteRepo = db.CreateCommand();
        deleteRepo.CommandText = "DELETE FROM Repository WHERE Id = $repositoryId;";
        deleteRepo.Parameters.AddWithValue("$repositoryId", repositoryId);
        await deleteRepo.ExecuteNonQueryAsync();
    }

    public async Task<IReadOnlyList<DailyTrafficRow>> GetDailyRowsAsync(long repositoryId, DateTime fromDate, DateTime toDate)
    {
        await EnsureSchemaAsync();
        await using var db = await OpenAsync();

        await using var cmd = db.CreateCommand();
        cmd.CommandText = """
                          SELECT v.[Date], c.Clones, c.UniqueCloners, c.Views, c.UniqueViewers
                          FROM CloneDay c
                          INNER JOIN ViewDay v ON v.DayIndex = c.DayIndex
                          WHERE c.RepositoryId = $repositoryId
                            AND date(v.[Date]) >= date($fromDate)
                            AND date(v.[Date]) <= date($toDate)
                          ORDER BY date(v.[Date]) DESC;
                          """;
        cmd.Parameters.AddWithValue("$repositoryId", repositoryId);
        cmd.Parameters.AddWithValue("$fromDate", fromDate.Date.ToString("yyyy-MM-dd"));
        cmd.Parameters.AddWithValue("$toDate", toDate.Date.ToString("yyyy-MM-dd"));

        var rows = new List<DailyTrafficRow>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new DailyTrafficRow(
                DateTime.Parse(reader.GetString(0)),
                reader.GetInt32(1),
                reader.GetInt32(2),
                reader.GetInt32(3),
                reader.GetInt32(4)));
        }

        return rows;
    }

    public async Task<TrafficTotals> GetTotalsAsync(long repositoryId, DateTime fromDate, DateTime toDate)
    {
        await EnsureSchemaAsync();
        await using var db = await OpenAsync();

        await using var cmd = db.CreateCommand();
        cmd.CommandText = """
                          SELECT
                              COALESCE(SUM(c.Clones), 0),
                              COALESCE(SUM(c.UniqueCloners), 0),
                              COALESCE(SUM(c.Views), 0),
                              COALESCE(SUM(c.UniqueViewers), 0)
                          FROM CloneDay c
                          INNER JOIN ViewDay v ON v.DayIndex = c.DayIndex
                          WHERE c.RepositoryId = $repositoryId
                            AND date(v.[Date]) >= date($fromDate)
                            AND date(v.[Date]) <= date($toDate);
                          """;
        cmd.Parameters.AddWithValue("$repositoryId", repositoryId);
        cmd.Parameters.AddWithValue("$fromDate", fromDate.Date.ToString("yyyy-MM-dd"));
        cmd.Parameters.AddWithValue("$toDate", toDate.Date.ToString("yyyy-MM-dd"));

        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return new TrafficTotals(0, 0, 0, 0);
        }

        return new TrafficTotals(
            reader.GetInt32(0),
            reader.GetInt32(1),
            reader.GetInt32(2),
            reader.GetInt32(3));
    }

    public async Task<IReadOnlyList<(long Id, string Name)>> GetRepositoriesAsync()
    {
        await EnsureSchemaAsync();
        await using var db = await OpenAsync();

        await using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT Id, [User] || '/' || ProjectName AS Name FROM Repository ORDER BY Name;";

        var list = new List<(long Id, string Name)>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            list.Add((reader.GetInt64(0), reader.GetString(1)));
        }

        return list;
    }

    public async Task<IReadOnlyList<RepoConfig>> GetRepositoryConfigsAsync()
    {
        await EnsureSchemaAsync();
        await using var db = await OpenAsync();

        await using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT [User], ProjectName FROM Repository ORDER BY [User], ProjectName;";

        var list = new List<RepoConfig>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            list.Add(new RepoConfig
            {
                Owner = reader.GetString(0),
                Name = reader.GetString(1)
            });
        }

        return list;
    }

    private async Task<SqliteConnection> OpenAsync()
    {
        var connectionString = new SqliteConnectionStringBuilder { DataSource = _dbPath }.ToString();
        var db = new SqliteConnection(connectionString);
        await db.OpenAsync();

        await using var pragma = db.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys = ON;";
        await pragma.ExecuteNonQueryAsync();

        return db;
    }

    private static HttpClient CreateGitHubClient(string token)
    {
        var client = new HttpClient { BaseAddress = new Uri("https://api.github.com/") };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("GitHubMetaTrafficViewer/1.0");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        return client;
    }

    private static async Task<List<TrafficDay>> GetTrafficDaysAsync(HttpClient httpClient, string owner, string repo, string trafficKind)
    {
        using var response = await httpClient.GetAsync($"repos/{owner}/{repo}/traffic/{trafficKind}");
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"GitHub API {response.StatusCode}: {body}");
        }

        using var doc = JsonDocument.Parse(body);
        var arrayName = trafficKind == "views" ? "views" : "clones";
        if (!doc.RootElement.TryGetProperty(arrayName, out var daysElement) || daysElement.ValueKind != JsonValueKind.Array)
        {
            return new List<TrafficDay>();
        }

        var result = new List<TrafficDay>();
        foreach (var item in daysElement.EnumerateArray())
        {
            var timestamp = item.GetProperty("timestamp").GetDateTime();
            var count = item.GetProperty("count").GetInt32();
            var uniques = item.GetProperty("uniques").GetInt32();
            result.Add(new TrafficDay(timestamp.Date, count, uniques));
        }

        return result;
    }

    private static async Task<long> CreateSyncRunAsync(SqliteConnection db, DateTime startedUtc)
    {
        await using var cmd = db.CreateCommand();
        cmd.CommandText = """
                          INSERT INTO SyncRun (StartedUtc, Status)
                          VALUES ($startedUtc, 'Running');
                          SELECT last_insert_rowid();
                          """;
        cmd.Parameters.AddWithValue("$startedUtc", startedUtc.ToString("O"));
        var value = await cmd.ExecuteScalarAsync();
        return (long)(value ?? 0L);
    }

    private static async Task CompleteSyncRunAsync(SqliteConnection db, long syncRunId, DateTime completedUtc, string status, string? errorMessage)
    {
        await using var cmd = db.CreateCommand();
        cmd.CommandText = """
                          UPDATE SyncRun
                          SET CompletedUtc = $completedUtc,
                              Status = $status,
                              ErrorMessage = $errorMessage
                          WHERE Id = $id;
                          """;
        cmd.Parameters.AddWithValue("$completedUtc", completedUtc.ToString("O"));
        cmd.Parameters.AddWithValue("$status", status);
        cmd.Parameters.AddWithValue("$errorMessage", (object?)errorMessage ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$id", syncRunId);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<long> UpsertRepositoryAsync(SqliteConnection db, string user, string projectName)
    {
        await using var insertCmd = db.CreateCommand();
        insertCmd.CommandText = """
                                INSERT INTO Repository ([User], ProjectName)
                                VALUES ($user, $projectName)
                                ON CONFLICT([User], ProjectName) DO NOTHING;
                                """;
        insertCmd.Parameters.AddWithValue("$user", user);
        insertCmd.Parameters.AddWithValue("$projectName", projectName);
        await insertCmd.ExecuteNonQueryAsync();

        await using var selectCmd = db.CreateCommand();
        selectCmd.CommandText = "SELECT Id FROM Repository WHERE [User] = $user AND ProjectName = $projectName;";
        selectCmd.Parameters.AddWithValue("$user", user);
        selectCmd.Parameters.AddWithValue("$projectName", projectName);
        var value = await selectCmd.ExecuteScalarAsync();
        return Convert.ToInt64(value);
    }

    private static async Task<long> UpsertViewDayAsync(SqliteConnection db, DateTime date)
    {
        await using var insertCmd = db.CreateCommand();
        insertCmd.CommandText = """
                                INSERT INTO ViewDay ([Date])
                                VALUES ($date)
                                ON CONFLICT([Date]) DO NOTHING;
                                """;
        insertCmd.Parameters.AddWithValue("$date", date.ToString("yyyy-MM-dd"));
        await insertCmd.ExecuteNonQueryAsync();

        await using var selectCmd = db.CreateCommand();
        selectCmd.CommandText = "SELECT DayIndex FROM ViewDay WHERE [Date] = $date;";
        selectCmd.Parameters.AddWithValue("$date", date.ToString("yyyy-MM-dd"));
        var value = await selectCmd.ExecuteScalarAsync();
        return Convert.ToInt64(value);
    }

    private static async Task UpsertTrafficDaysAsync(SqliteConnection db, long repositoryId, long syncRunId, IEnumerable<TrafficDay> viewDays, IEnumerable<TrafficDay> cloneDays)
    {
        var viewsByDate = viewDays.ToDictionary(d => d.Date, d => d);
        var clonesByDate = cloneDays.ToDictionary(d => d.Date, d => d);

        var allDates = viewsByDate.Keys.Union(clonesByDate.Keys).OrderBy(d => d).ToList();
        foreach (var date in allDates)
        {
            var dayIndex = await UpsertViewDayAsync(db, date);
            viewsByDate.TryGetValue(date, out var views);
            clonesByDate.TryGetValue(date, out var clones);

            await using var cmd = db.CreateCommand();
            cmd.CommandText = """
                              INSERT INTO CloneDay (RepositoryId, DayIndex, Clones, UniqueCloners, Views, UniqueViewers, SyncRunId)
                              VALUES ($repositoryId, $dayIndex, $clones, $uniqueCloners, $views, $uniqueViewers, $syncRunId)
                              ON CONFLICT(RepositoryId, DayIndex) DO UPDATE SET
                                  Clones = excluded.Clones,
                                  UniqueCloners = excluded.UniqueCloners,
                                  Views = excluded.Views,
                                  UniqueViewers = excluded.UniqueViewers,
                                  SyncRunId = excluded.SyncRunId;
                              """;
            cmd.Parameters.AddWithValue("$repositoryId", repositoryId);
            cmd.Parameters.AddWithValue("$dayIndex", dayIndex);
            cmd.Parameters.AddWithValue("$clones", clones?.Count ?? 0);
            cmd.Parameters.AddWithValue("$uniqueCloners", clones?.Uniques ?? 0);
            cmd.Parameters.AddWithValue("$views", views?.Count ?? 0);
            cmd.Parameters.AddWithValue("$uniqueViewers", views?.Uniques ?? 0);
            cmd.Parameters.AddWithValue("$syncRunId", syncRunId);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    private static async Task MigrateTrafficTablesIfNeededAsync(SqliteConnection db)
    {
        var viewDayColumns = await GetColumnNamesAsync(db, "ViewDay");
        var cloneDayColumns = await GetColumnNamesAsync(db, "CloneDay");
        var hasRepositoryCascadeDelete = await HasRepositoryCascadeDeleteAsync(db);

        var requiresViewMigration = viewDayColumns.Contains("RepositoryId") ||
                                    viewDayColumns.Contains("Visitors") ||
                                    viewDayColumns.Contains("UniqueVisitors");

        var requiresCloneMigration = cloneDayColumns.Contains("Date") ||
                                     !cloneDayColumns.Contains("Views") ||
                                     !cloneDayColumns.Contains("UniqueViewers") ||
                                     !cloneDayColumns.Contains("DayIndex") ||
                                     !hasRepositoryCascadeDelete;

        if (!requiresViewMigration && !requiresCloneMigration)
        {
            return;
        }

        await using var cmd = db.CreateCommand();
        cmd.CommandText = """
                          DROP TABLE IF EXISTS CloneDay;
                          DROP TABLE IF EXISTS ViewDay;

                          CREATE TABLE ViewDay (
                              DayIndex INTEGER PRIMARY KEY AUTOINCREMENT,
                              [Date] TEXT NOT NULL,
                              UNIQUE([Date])
                          );

                          CREATE TABLE CloneDay (
                              RepositoryId INTEGER NOT NULL,
                              DayIndex INTEGER NOT NULL,
                              Clones INTEGER NOT NULL,
                              UniqueCloners INTEGER NOT NULL,
                              Views INTEGER NOT NULL,
                              UniqueViewers INTEGER NOT NULL,
                              SyncRunId INTEGER NOT NULL,
                              PRIMARY KEY (RepositoryId, DayIndex),
                              FOREIGN KEY (RepositoryId) REFERENCES Repository(Id) ON DELETE CASCADE,
                              FOREIGN KEY (DayIndex) REFERENCES ViewDay(DayIndex),
                              FOREIGN KEY (SyncRunId) REFERENCES SyncRun(Id)
                          );
                          """;
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<HashSet<string>> GetColumnNamesAsync(SqliteConnection db, string tableName)
    {
        await using var cmd = db.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({tableName});";
        await using var reader = await cmd.ExecuteReaderAsync();

        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync())
        {
            columns.Add(reader.GetString(1));
        }

        return columns;
    }

    private static async Task<bool> HasRepositoryCascadeDeleteAsync(SqliteConnection db)
    {
        await using var cmd = db.CreateCommand();
        cmd.CommandText = "PRAGMA foreign_key_list(CloneDay);";
        await using var reader = await cmd.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            var referencedTable = reader.GetString(2);
            var fromColumn = reader.GetString(3);
            var onDelete = reader.GetString(6);

            if (string.Equals(referencedTable, "Repository", StringComparison.OrdinalIgnoreCase)
                && string.Equals(fromColumn, "RepositoryId", StringComparison.OrdinalIgnoreCase)
                && string.Equals(onDelete, "CASCADE", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
