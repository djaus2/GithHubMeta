namespace GitHubMetaWPF;

public sealed class ReposConfig
{
    public List<RepoConfig> Repositories { get; init; } = new();
}

public sealed class RepoConfig
{
    public string Owner { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;

    public override string ToString() => $"{Owner}/{Name}";
}

public sealed record TrafficDay(DateTime Date, int Count, int Uniques);

public sealed record DailyTrafficRow(
    DateTime Date,
    int Clones,
    int UniqueCloners,
    int Views,
    int UniqueViewers);

public sealed record TrafficTotals(
    int TotalClones,
    int TotalUniqueCloners,
    int TotalViews,
    int TotalUniqueViewers);

public sealed record RepositoryListItem(long Id, string Name);
