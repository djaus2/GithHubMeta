using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace GitHubMetaWPF;

internal static class TokenStore
{
    private static readonly string TokenFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GitHubMetaWPF",
        "token.dat");

    public static void SaveToken(string token)
    {
        var directory = Path.GetDirectoryName(TokenFilePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var raw = Encoding.UTF8.GetBytes(token);
        var encrypted = ProtectedData.Protect(raw, null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(TokenFilePath, encrypted);
    }

    public static string? LoadToken()
    {
        if (!File.Exists(TokenFilePath))
        {
            return null;
        }

        try
        {
            var encrypted = File.ReadAllBytes(TokenFilePath);
            var raw = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(raw);
        }
        catch
        {
            return null;
        }
    }
}
