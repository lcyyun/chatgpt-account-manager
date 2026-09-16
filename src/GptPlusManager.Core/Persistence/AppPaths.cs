namespace GptPlusManager.Core.Persistence;

public sealed class AppPaths
{
    public AppPaths(string? dataRoot = null)
    {
        DataRoot = Path.GetFullPath(string.IsNullOrWhiteSpace(dataRoot)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "gptplus")
            : dataRoot);
    }

    public string DataRoot { get; }
    public string AccountsFile => Path.Combine(DataRoot, "accounts.json");
    public string SettingsFile => Path.Combine(DataRoot, "settings.json");
    public string TokensDirectory => Path.Combine(DataRoot, "tokens");
}
