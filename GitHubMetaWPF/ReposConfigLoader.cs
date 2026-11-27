using System.IO;
using System.Text.Json;

namespace GitHubMetaWPF;

public static class ReposConfigLoader
{
    public static async Task<ReposConfig> LoadAsync(string reposFilePath)
    {
        if (!File.Exists(reposFilePath))
        {
            return new ReposConfig();
        }

        await using var stream = File.OpenRead(reposFilePath);
        var config = await JsonSerializer.DeserializeAsync<ReposConfig>(stream, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        return config ?? new ReposConfig();
    }

    public static async Task SaveAsync(string reposFilePath, ReposConfig config)
    {
        await using var stream = File.Create(reposFilePath);
        await JsonSerializer.SerializeAsync(stream, config, new JsonSerializerOptions
        {
            WriteIndented = true
        });
    }
}
