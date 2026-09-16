using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Windows;

namespace GptPlusManager.Wpf;

public static class ThemeService
{
    private const string PreferencesFileName = "wpf-preferences.json";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };
    private static string? _preferencesPath;

    public static bool IsDark { get; private set; } = true;

    // The label describes the action a theme toggle performs.
    public static string ThemeLabel => IsDark ? "浅色模式" : "深色模式";

    public static event EventHandler? ThemeChanged;

    public static void Initialize(string dataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        _preferencesPath = Path.Combine(Path.GetFullPath(dataRoot), PreferencesFileName);

        var isDark = true;
        try
        {
            if (File.Exists(_preferencesPath))
            {
                var preferences = JsonSerializer.Deserialize<WpfPreferences>(File.ReadAllText(_preferencesPath));
                isDark = !string.Equals(preferences?.Theme, "Light", StringComparison.OrdinalIgnoreCase);
            }
        }
        catch (JsonException)
        {
            // A malformed preferences file should not prevent the application from starting.
        }
        catch (IOException)
        {
            // Keep the default theme when preferences cannot be read.
        }
        catch (UnauthorizedAccessException)
        {
            // Keep the default theme when preferences cannot be read.
        }

        Apply(isDark);
    }

    public static void Toggle()
    {
        Apply(!IsDark);
        Save();
    }

    private static void Apply(bool isDark)
    {
        IsDark = isDark;
        var resources = Application.Current?.Resources;
        if (resources is not null)
        {
            var dictionaries = resources.MergedDictionaries;
            // Remove every theme dictionary, then insert the requested one last so its
            // brushes win over any previously merged palette.
            for (var index = dictionaries.Count - 1; index >= 0; index--)
            {
                var source = dictionaries[index].Source?.OriginalString;
                if (source is not null &&
                    (source.EndsWith("Themes/Dark.xaml", StringComparison.OrdinalIgnoreCase) ||
                     source.EndsWith("Themes/Light.xaml", StringComparison.OrdinalIgnoreCase)))
                {
                    dictionaries.RemoveAt(index);
                }
            }

            dictionaries.Insert(0, new ResourceDictionary
            {
                Source = new Uri($"Themes/{(isDark ? "Dark" : "Light")}.xaml", UriKind.Relative)
            });

            // DynamicResource re-resolution normally happens automatically, but forcing a
            // visual refresh guarantees programmatically created brushes update immediately.
            foreach (Window window in Application.Current!.Windows)
            {
                if (window.Content is FrameworkElement root)
                {
                    root.InvalidateVisual();
                }
            }
        }

        ThemeChanged?.Invoke(null, EventArgs.Empty);
    }

    private static void Save()
    {
        if (_preferencesPath is null)
        {
            return;
        }

        try
        {
            var directory = Path.GetDirectoryName(_preferencesPath)!;
            Directory.CreateDirectory(directory);
            var temporaryPath = _preferencesPath + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(
                new WpfPreferences { Theme = IsDark ? "Dark" : "Light" }, JsonOptions));
            File.Move(temporaryPath, _preferencesPath, true);
        }
        catch (IOException)
        {
            // Theme switching remains available even if preferences cannot be persisted.
        }
        catch (UnauthorizedAccessException)
        {
            // Theme switching remains available even if preferences cannot be persisted.
        }
    }

    private sealed class WpfPreferences
    {
        public string Theme { get; init; } = "Dark";
    }
}
