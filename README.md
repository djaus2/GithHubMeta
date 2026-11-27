# GithHubMeta
Console app to collect GitHub traffic data into SQLite.

## Run
1. Update `repos.json` with repositories to monitor.
2. Run `dotnet run`.
3. Enter a GitHub token with access to repository traffic data.

Data is stored in `github-traffic.db` using entities:
- Repository
- ViewDay
- CloneDay
- SyncRun
