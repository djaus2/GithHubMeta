using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

const string DbFile = "github-traffic.db";
const string ReposFile = "repos.json";

if (!File.Exists(ReposFile))
{
    Console.WriteLine($"Missing {ReposFile}. Create it with repositories to monitor.");
    return;
}

var reposConfig = await LoadReposAsync(ReposFile);
if (reposConfig.Repositories.Count == 0)
{
    Console.WriteLine("No repositories configured in repos.json.");
    return;
}

Console.Write("GitHub token: ");
var token = ReadSecret();
if (string.IsNullOrWhiteSpace(token))
{
    Console.WriteLine("Token is required.");
    return;
}

var connectionString = new SqliteConnectionStringBuilder { DataSource = DbFile }.ToString();
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

        await UpsertViewDaysAsync(db, repositoryId, syncRunId, views);
        await UpsertCloneDaysAsync(db, repositoryId, syncRunId, clones);

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
                  RepositoryId INTEGER NOT NULL,
                  [Date] TEXT NOT NULL,
                  Visitors INTEGER NOT NULL,
                  UniqueVisitors INTEGER NOT NULL,
                  SyncRunId INTEGER NOT NULL,
                  PRIMARY KEY (RepositoryId, [Date]),
                  FOREIGN KEY (RepositoryId) REFERENCES Repository(Id),
                  FOREIGN KEY (SyncRunId) REFERENCES SyncRun(Id)
              );

              CREATE TABLE IF NOT EXISTS CloneDay (
                  RepositoryId INTEGER NOT NULL,
                  [Date] TEXT NOT NULL,
                  Clones INTEGER NOT NULL,
                  UniqueCloners INTEGER NOT NULL,
                  SyncRunId INTEGER NOT NULL,
                  PRIMARY KEY (RepositoryId, [Date]),
                  FOREIGN KEY (RepositoryId) REFERENCES Repository(Id),
                  FOREIGN KEY (SyncRunId) REFERENCES SyncRun(Id)
              );
              """;

    await using var cmd = db.CreateCommand();
    cmd.CommandText = sql;
    await cmd.ExecuteNonQueryAsync();
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

static async Task UpsertViewDaysAsync(SqliteConnection db, long repositoryId, long syncRunId, IEnumerable<TrafficDay> days)
{
    foreach (var day in days)
    {
        await using var cmd = db.CreateCommand();
        cmd.CommandText = """
                          INSERT INTO ViewDay (RepositoryId, [Date], Visitors, UniqueVisitors, SyncRunId)
                          VALUES ($repositoryId, $date, $visitors, $uniqueVisitors, $syncRunId)
                          ON CONFLICT(RepositoryId, [Date]) DO UPDATE SET
                              Visitors = excluded.Visitors,
                              UniqueVisitors = excluded.UniqueVisitors,
                              SyncRunId = excluded.SyncRunId;
                          """;
        cmd.Parameters.AddWithValue("$repositoryId", repositoryId);
        cmd.Parameters.AddWithValue("$date", day.Date.ToString("yyyy-MM-dd"));
        cmd.Parameters.AddWithValue("$visitors", day.Count);
        cmd.Parameters.AddWithValue("$uniqueVisitors", day.Uniques);
        cmd.Parameters.AddWithValue("$syncRunId", syncRunId);
        await cmd.ExecuteNonQueryAsync();
    }
}

static async Task UpsertCloneDaysAsync(SqliteConnection db, long repositoryId, long syncRunId, IEnumerable<TrafficDay> days)
{
    foreach (var day in days)
    {
        await using var cmd = db.CreateCommand();
        cmd.CommandText = """
                          INSERT INTO CloneDay (RepositoryId, [Date], Clones, UniqueCloners, SyncRunId)
                          VALUES ($repositoryId, $date, $clones, $uniqueCloners, $syncRunId)
                          ON CONFLICT(RepositoryId, [Date]) DO UPDATE SET
                              Clones = excluded.Clones,
                              UniqueCloners = excluded.UniqueCloners,
                              SyncRunId = excluded.SyncRunId;
                          """;
        cmd.Parameters.AddWithValue("$repositoryId", repositoryId);
        cmd.Parameters.AddWithValue("$date", day.Date.ToString("yyyy-MM-dd"));
        cmd.Parameters.AddWithValue("$clones", day.Count);
        cmd.Parameters.AddWithValue("$uniqueCloners", day.Uniques);
        cmd.Parameters.AddWithValue("$syncRunId", syncRunId);
        await cmd.ExecuteNonQueryAsync();
    }
}

static string ReadSecret()
{
    var builder = new StringBuilder();
    while (true)
    {
        var key = Console.ReadKey(intercept: true);
        if (key.Key == ConsoleKey.Enter)
        {
            Console.WriteLine();
            break;
        }

        if (key.Key == ConsoleKey.Backspace)
        {
            if (builder.Length <= 0)
            {
                continue;
            }

            builder.Length -= 1;
            continue;
        }

        if (!char.IsControl(key.KeyChar))
        {
            builder.Append(key.KeyChar);
        }
    }

    return builder.ToString();
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
