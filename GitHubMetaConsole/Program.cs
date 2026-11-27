using System.Text.Json;
using Microsoft.Data.Sqlite;

var repoRoot = ResolveRepoRoot();
var dbFile = Path.Combine(repoRoot, "github-traffics.db");
var reposFile = Path.Combine(repoRoot, "repos.json");

if (!File.Exists(reposFile))
{
    Console.WriteLine($"Missing {reposFile}. Create it with repositories to monitor.");
    return;
}

var reposConfig = await LoadReposAsync(reposFile);
if (reposConfig.Repositories.Count == 0)
{
    Console.WriteLine("No repositories configured in repos.json.");
    return;
}

var token = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
if (string.IsNullOrWhiteSpace(token))
{
    Console.Write("GitHub token (paste and press Enter): ");
    token = Console.ReadLine();
}

if (string.IsNullOrWhiteSpace(token))
{
    Console.WriteLine("Token is required.");
    return;
}

var connectionString = new SqliteConnectionStringBuilder { DataSource = dbFile }.ToString();
await using var db = new SqliteConnection(connectionString);
await db.OpenAsync();

await EnsureSchemaAsync(db);

using var httpClient = CreateGitHubClient(token);
var startedUtc = DateTime.UtcNow;
var syncRunId = await CreateSyncRunAsync(db, startedUtc);

var errors = new List<string>();

foreach (var repo in reposConfig.Repositories)
{
    try
    {
        var repositoryId = await UpsertRepositoryAsync(db, repo.Owner, repo.Name);
        var views = await GetTrafficDaysAsync(httpClient, repo.Owner, repo.Name, "views");
        var clones = await GetTrafficDaysAsync(httpClient, repo.Owner, repo.Name, "clones");

        await UpsertTrafficDaysAsync(db, repositoryId, syncRunId, views, clones);

        Console.WriteLine($"Synced {repo.Owner}/{repo.Name}: views={views.Count}, clones={clones.Count}");
    }
    catch (Exception ex)
    {
        var message = $"{repo.Owner}/{repo.Name}: {ex.Message}";
        errors.Add(message);
        Console.WriteLine($"Failed {message}");
    }
}

var completedUtc = DateTime.UtcNow;
if (errors.Count == 0)
{
    await CompleteSyncRunAsync(db, syncRunId, completedUtc, "Succeeded", null);
    Console.WriteLine("Sync completed successfully.");
}
else
{
    var joinedError = string.Join(Environment.NewLine, errors);
    await CompleteSyncRunAsync(db, syncRunId, completedUtc, "Failed", joinedError);
    Console.WriteLine("Sync completed with failures.");
}

static string ResolveRepoRoot()
{
    var current = new DirectoryInfo(AppContext.BaseDirectory);
    while (current is not null)
    {
        if (Directory.Exists(Path.Combine(current.FullName, ".git")))
        {
            return current.FullName;
        }

        current = current.Parent;
    }

    return Directory.GetCurrentDirectory();
}

static HttpClient CreateGitHubClient(string token)
{
    var client = new HttpClient { BaseAddress = new Uri("https://api.github.com/") };
    client.DefaultRequestHeaders.UserAgent.ParseAdd("GitHubMetaTrafficCollector/1.0");
    client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
    client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
    return client;
}

static async Task<ReposConfig> LoadReposAsync(string path)
{
    await using var stream = File.OpenRead(path);
    var config = await JsonSerializer.DeserializeAsync<ReposConfig>(stream, new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true
    });

    return config ?? new ReposConfig();
}

static async Task EnsureSchemaAsync(SqliteConnection db)
{
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

static async Task MigrateTrafficTablesIfNeededAsync(SqliteConnection db)
{
    var viewDayColumns = await GetColumnNamesAsync(db, "ViewDay");
    var cloneDayColumns = await GetColumnNamesAsync(db, "CloneDay");

    var requiresViewMigration = viewDayColumns.Contains("RepositoryId") ||
                                viewDayColumns.Contains("Visitors") ||
                                viewDayColumns.Contains("UniqueVisitors");

    var requiresCloneMigration = cloneDayColumns.Contains("Date") ||
                                 !cloneDayColumns.Contains("Views") ||
                                 !cloneDayColumns.Contains("UniqueViewers") ||
                                 !cloneDayColumns.Contains("DayIndex");

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
                          FOREIGN KEY (RepositoryId) REFERENCES Repository(Id),
                          FOREIGN KEY (DayIndex) REFERENCES ViewDay(DayIndex),
                          FOREIGN KEY (SyncRunId) REFERENCES SyncRun(Id)
                      );
                      """;
    await cmd.ExecuteNonQueryAsync();
}

static async Task<HashSet<string>> GetColumnNamesAsync(SqliteConnection db, string tableName)
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

static async Task<long> CreateSyncRunAsync(SqliteConnection db, DateTime startedUtc)
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

static async Task CompleteSyncRunAsync(SqliteConnection db, long syncRunId, DateTime completedUtc, string status, string? errorMessage)
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

static async Task<long> UpsertRepositoryAsync(SqliteConnection db, string user, string projectName)
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

static async Task<List<TrafficDay>> GetTrafficDaysAsync(HttpClient httpClient, string owner, string repo, string trafficKind)
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

static async Task<long> UpsertViewDayAsync(SqliteConnection db, DateTime date)
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

static async Task UpsertTrafficDaysAsync(SqliteConnection db, long repositoryId, long syncRunId, IEnumerable<TrafficDay> viewDays, IEnumerable<TrafficDay> cloneDays)
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

public sealed class ReposConfig
{
    public List<RepoConfig> Repositories { get; init; } = new();
}

public sealed class RepoConfig
{
    public string Owner { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
}

public sealed record TrafficDay(DateTime Date, int Count, int Uniques);
