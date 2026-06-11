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
        var settings = await ReadSettingsAsync(cancellationToken);
        return settings?.CultureName;
    }

    public async Task SaveCultureNameAsync(
        string cultureName,
        CancellationToken cancellationToken = default)
    {
        var settings = await ReadSettingsAsync(cancellationToken) ?? new UiSettingsFile();
        settings.CultureName = cultureName;
        await WriteSettingsAsync(settings, cancellationToken);
    }

    public async Task<ThumbnailDisplaySettings> GetThumbnailDisplaySettingsAsync(
        CancellationToken cancellationToken = default)
    {
        var settings = await ReadSettingsAsync(cancellationToken);
        var thumbnail = settings?.ThumbnailDisplaySettings;
        return thumbnail is null
            ? ThumbnailDisplaySettings.Default
            : new ThumbnailDisplaySettings(
                thumbnail.ShowType,
                thumbnail.ShowStatus,
                thumbnail.ShowTags,
                thumbnail.ShowPath,
                thumbnail.ShowSize);
    }

    public async Task SaveThumbnailDisplaySettingsAsync(
        ThumbnailDisplaySettings thumbnailDisplaySettings,
        CancellationToken cancellationToken = default)
    {
        var settings = await ReadSettingsAsync(cancellationToken) ?? new UiSettingsFile();
        settings.ThumbnailDisplaySettings = new ThumbnailDisplaySettingsFile
        {
            ShowType = thumbnailDisplaySettings.ShowType,
            ShowStatus = thumbnailDisplaySettings.ShowStatus,
            ShowTags = thumbnailDisplaySettings.ShowTags,
            ShowPath = thumbnailDisplaySettings.ShowPath,
            ShowSize = thumbnailDisplaySettings.ShowSize
        };
        await WriteSettingsAsync(settings, cancellationToken);
    }

    public async Task<bool> GetLowImpactImportEnabledAsync(
        CancellationToken cancellationToken = default)
    {
        var settings = await ReadSettingsAsync(cancellationToken);
        return settings?.LowImpactImportEnabled ?? false;
    }

    public async Task SaveLowImpactImportEnabledAsync(
        bool isEnabled,
        CancellationToken cancellationToken = default)
    {
        var settings = await ReadSettingsAsync(cancellationToken) ?? new UiSettingsFile();
        settings.LowImpactImportEnabled = isEnabled;
        await WriteSettingsAsync(settings, cancellationToken);
    }

    public async Task<AssetViewSettings> GetAssetViewSettingsAsync(
        CancellationToken cancellationToken = default)
    {
        var settings = await ReadSettingsAsync(cancellationToken);
        var assetView = settings?.AssetViewSettings;
        return assetView is null
            ? AssetViewSettings.Default
            : new AssetViewSettings(
                assetView.AssetPreviewScale,
                assetView.IsDetailsPanelExpanded,
                assetView.DetailsPanelWidth,
                assetView.CurrentFolder,
                assetView.AssetListVerticalOffset);
    }

    public async Task SaveAssetViewSettingsAsync(
        AssetViewSettings assetViewSettings,
        CancellationToken cancellationToken = default)
    {
        var settings = await ReadSettingsAsync(cancellationToken) ?? new UiSettingsFile();
        settings.AssetViewSettings = new AssetViewSettingsFile
        {
            AssetPreviewScale = assetViewSettings.AssetPreviewScale,
            IsDetailsPanelExpanded = assetViewSettings.IsDetailsPanelExpanded,
            DetailsPanelWidth = assetViewSettings.DetailsPanelWidth,
            CurrentFolder = assetViewSettings.CurrentFolder,
            AssetListVerticalOffset = assetViewSettings.AssetListVerticalOffset
        };
        await WriteSettingsAsync(settings, cancellationToken);
    }

    private static string GetDefaultSettingsPath()
    {
        var appDataRoot = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(appDataRoot, "AssetManager", "ui-settings.json");
    }

    private async Task<UiSettingsFile?> ReadSettingsAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_settingsPath))
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(_settingsPath);
            return await JsonSerializer.DeserializeAsync<UiSettingsFile>(
                stream,
                JsonOptions,
                cancellationToken);
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

    private async Task WriteSettingsAsync(UiSettingsFile settings, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_settingsPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = _settingsPath + ".tmp";
        await using (var stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(stream, settings, JsonOptions, cancellationToken);
        }

        File.Move(tempPath, _settingsPath, overwrite: true);
    }

    private sealed class UiSettingsFile
    {
        public string? CultureName { get; set; }

        public ThumbnailDisplaySettingsFile? ThumbnailDisplaySettings { get; set; }

        public bool LowImpactImportEnabled { get; set; }

        public AssetViewSettingsFile? AssetViewSettings { get; set; }
    }

    private sealed class ThumbnailDisplaySettingsFile
    {
        public bool ShowType { get; set; } = ThumbnailDisplaySettings.Default.ShowType;

        public bool ShowStatus { get; set; } = ThumbnailDisplaySettings.Default.ShowStatus;

        public bool ShowTags { get; set; } = ThumbnailDisplaySettings.Default.ShowTags;

        public bool ShowPath { get; set; } = ThumbnailDisplaySettings.Default.ShowPath;

        public bool ShowSize { get; set; } = ThumbnailDisplaySettings.Default.ShowSize;
    }

    private sealed class AssetViewSettingsFile
    {
        public double AssetPreviewScale { get; set; } = AssetViewSettings.Default.AssetPreviewScale;

        public bool IsDetailsPanelExpanded { get; set; } = AssetViewSettings.Default.IsDetailsPanelExpanded;

        public double DetailsPanelWidth { get; set; } = AssetViewSettings.Default.DetailsPanelWidth;

        public string CurrentFolder { get; set; } = AssetViewSettings.Default.CurrentFolder;

        public double AssetListVerticalOffset { get; set; } = AssetViewSettings.Default.AssetListVerticalOffset;
    }
}

public sealed record AssetViewSettings(
    double AssetPreviewScale,
    bool IsDetailsPanelExpanded,
    double DetailsPanelWidth,
    string CurrentFolder,
    double AssetListVerticalOffset)
{
    public static AssetViewSettings Default { get; } = new(
        AssetPreviewScale: 1d,
        IsDetailsPanelExpanded: true,
        DetailsPanelWidth: 360d,
        CurrentFolder: string.Empty,
        AssetListVerticalOffset: 0d);
}
