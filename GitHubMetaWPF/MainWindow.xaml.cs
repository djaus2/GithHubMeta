using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.VisualBasic;

namespace GitHubMetaWPF;

public partial class MainWindow : Window
{
    private readonly string _repoRoot;
    private readonly string _dbPath;
    private readonly string _reposPath;
    private readonly TrafficDataService _service;

    public MainWindow()
    {
        InitializeComponent();

        _repoRoot = ResolveRepoRoot();
        _dbPath = Path.Combine(_repoRoot, "github-traffics.db");
        _reposPath = Path.Combine(_repoRoot, "repos.json");
        _service = new TrafficDataService(_dbPath);

        DbPathTextBox.Text = _dbPath;
        ReposPathTextBox.Text = _reposPath;
        TokenPasswordBox.Password = TokenStore.LoadToken()
                                  ?? Environment.GetEnvironmentVariable("GITHUB_TOKEN")
                                  ?? string.Empty;

        var today = DateTime.Today;
        ToDatePicker.SelectedDate = today;
        FromDatePicker.SelectedDate = today.AddYears(-1);

        Loaded += MainWindow_Loaded;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await _service.EnsureSchemaAsync();
        await RefreshRepositoriesAsync();
        await LoadDataAsync();
    }

    private async void SyncButton_Click(object sender, RoutedEventArgs e)
    {
        SyncButton.IsEnabled = false;
        try
        {
            var token = TokenPasswordBox.Password.Trim();
            if (string.IsNullOrWhiteSpace(token))
            {
                StatusTextBlock.Text = "Token is required.";
                return;
            }

            TokenStore.SaveToken(token);

            var repositories = await _service.GetRepositoryConfigsAsync();
            if (repositories.Count == 0)
            {
                StatusTextBlock.Text = "No repositories in database. Add one first.";
                return;
            }

            var failures = new List<string>();
            foreach (var repo in repositories)
            {
                try
                {
                    StatusTextBlock.Text = $"Syncing {repo.Owner}/{repo.Name}...";
                    await _service.SyncRepositoryAsync(repo, token);
                }
                catch (Exception ex)
                {
                    failures.Add($"{repo.Owner}/{repo.Name}: {ex.Message}");
                }
            }

            await RefreshRepositoriesAsync();
            await LoadDataAsync();

            StatusTextBlock.Text = failures.Count == 0
                ? "Sync completed."
                : $"Sync completed with {failures.Count} failure(s).";
        }
        finally
        {
            SyncButton.IsEnabled = true;
        }
    }

    private async void RefreshReposButton_Click(object sender, RoutedEventArgs e)
    {
        await RefreshRepositoriesAsync();
    }

    private async Task RefreshRepositoriesAsync()
    {
        var repos = await _service.GetRepositoriesAsync();
        var selectedId = RepositoryComboBox.SelectedValue as long?;

        RepositoryComboBox.ItemsSource = repos.Select(r => new RepositoryListItem(r.Id, r.Name)).ToList();
        if (RepositoryComboBox.Items.Count > 0)
        {
            var list = (IReadOnlyList<RepositoryListItem>)RepositoryComboBox.ItemsSource;
            var selected = selectedId.HasValue
                ? list.FirstOrDefault(x => x.Id == selectedId.Value)
                : list.First();

            RepositoryComboBox.SelectedItem = selected ?? list.First();
        }
        else
        {
            RepositoryComboBox.SelectedItem = null;
            DailyDataGrid.ItemsSource = null;
            ApplyTotals(new TrafficTotals(0, 0, 0, 0));
        }
    }

    private async void LoadDataButton_Click(object sender, RoutedEventArgs e)
    {
        await LoadDataAsync();
    }

    private async Task LoadDataAsync()
    {
        if (RepositoryComboBox.SelectedItem is not RepositoryListItem repo)
        {
            DailyDataGrid.ItemsSource = null;
            ApplyTotals(new TrafficTotals(0, 0, 0, 0));
            StatusTextBlock.Text = "No repository selected.";
            return;
        }

        var fromDate = FromDatePicker.SelectedDate ?? DateTime.Today.AddYears(-1);
        var toDate = ToDatePicker.SelectedDate ?? DateTime.Today;
        if (fromDate > toDate)
        {
            StatusTextBlock.Text = "From date must be before To date.";
            return;
        }

        var rows = await _service.GetDailyRowsAsync(repo.Id, fromDate, toDate);
        var totals = await _service.GetTotalsAsync(repo.Id, fromDate, toDate);

        DailyDataGrid.ItemsSource = rows;
        ApplyTotals(totals);
        StatusTextBlock.Text = $"Loaded {rows.Count} day(s) for {repo.Name}.";
    }

    private void ApplyTotals(TrafficTotals totals)
    {
        TotalClonesText.Text = totals.TotalClones.ToString("N0");
        TotalUniqueClonersText.Text = totals.TotalUniqueCloners.ToString("N0");
        TotalViewsText.Text = totals.TotalViews.ToString("N0");
        TotalUniqueViewersText.Text = totals.TotalUniqueViewers.ToString("N0");
    }

    private async void RepositoryComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }

        await LoadDataAsync();
    }

    private async void DatePicker_SelectedDateChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }

        StatusTextBlock.Text = "Applying date filter...";
        await LoadDataAsync();
    }

    private async void AddRepositoryMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var owner = Interaction.InputBox("Repository owner/user:", "Add Repository", "djaus2").Trim();
        if (string.IsNullOrWhiteSpace(owner))
        {
            return;
        }

        var name = Interaction.InputBox("Repository name:", "Add Repository", "").Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        var repoFullName = $"{owner}/{name}";
        var existingRepos = await _service.GetRepositoriesAsync();
        var exists = existingRepos.Any(r =>
            string.Equals(r.Name, repoFullName, StringComparison.OrdinalIgnoreCase));

        if (exists)
        {
            StatusTextBlock.Text = $"{repoFullName} is already in the database.";
            return;
        }

        await _service.AddOrGetRepositoryAsync(owner, name);
        await RefreshRepositoriesAsync();

        var list = (IReadOnlyList<RepositoryListItem>)RepositoryComboBox.ItemsSource;
        RepositoryComboBox.SelectedItem = list.FirstOrDefault(r =>
            string.Equals(r.Name, repoFullName, StringComparison.OrdinalIgnoreCase));

        var token = TokenPasswordBox.Password.Trim();
        if (string.IsNullOrWhiteSpace(token))
        {
            StatusTextBlock.Text = $"Added {repoFullName} to database. Enter token and click Sync to scan.";
            return;
        }

        try
        {
            StatusTextBlock.Text = $"Scanning {repoFullName}...";
            await _service.SyncRepositoryAsync(new RepoConfig { Owner = owner, Name = name }, token);
            await RefreshRepositoriesAsync();
            await LoadDataAsync();
            StatusTextBlock.Text = $"Added and scanned {repoFullName}.";
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = $"Added {repoFullName}, but scan failed: {ex.Message}";
        }
    }

    private async void RemoveRepositoryMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (RepositoryComboBox.SelectedItem is not RepositoryListItem selected)
        {
            StatusTextBlock.Text = "Select a repository first.";
            return;
        }
        var result = MessageBox.Show(
            $"Remove {selected.Name} from the database?",
            "Remove Repository",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        await _service.RemoveRepositoryAsync(selected.Id);
        await RefreshRepositoriesAsync();
        await LoadDataAsync();
        StatusTextBlock.Text = $"Removed {selected.Name} from database.";
    }

    private static string ResolveRepoRoot()
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
}