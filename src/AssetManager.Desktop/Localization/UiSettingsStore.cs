using System.Text.Json;
using System.IO;

namespace AssetManager.Desktop.Localization;

public sealed class UiSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _settingsPath;

    public UiSettingsStore()
        : this(GetDefaultSettingsPath())
    {
    }

    public UiSettingsStore(string settingsPath)
    {
        _settingsPath = Path.GetFullPath(settingsPath);
    }

    public async Task<string?> GetCultureNameAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_settingsPath))
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(_settingsPath);
            var settings = await JsonSerializer.DeserializeAsync<UiSettingsFile>(
                stream,
                JsonOptions,
                cancellationToken);

            return settings?.CultureName;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    public async Task SaveCultureNameAsync(
        string cultureName,
        CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(_settingsPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var settings = new UiSettingsFile
        {
            CultureName = cultureName
        };

        var tempPath = _settingsPath + ".tmp";
        await using (var stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(stream, settings, JsonOptions, cancellationToken);
        }

        File.Move(tempPath, _settingsPath, overwrite: true);
    }

    private static string GetDefaultSettingsPath()
    {
        var appDataRoot = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(appDataRoot, "AssetManager", "ui-settings.json");
    }

    private sealed class UiSettingsFile
    {
        public string? CultureName { get; set; }
    }
}
