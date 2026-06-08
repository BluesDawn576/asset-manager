using System.Globalization;
using System.Threading;
using System.Windows;
using WpfApplication = System.Windows.Application;

namespace AssetManager.Desktop.Localization;

public static class LocalizationManager
{
    public const string ZhCnCultureName = "zh-CN";
    public const string EnUsCultureName = "en-US";

    private static readonly IReadOnlyList<LanguageOption> LanguageOptions =
    [
        new(ZhCnCultureName, "中文（简体）"),
        new(EnUsCultureName, "English")
    ];

    private static UiSettingsStore? _settingsStore;
    private static bool _isInitialized;

    public static event EventHandler? CultureChanged;

    public static IReadOnlyList<LanguageOption> SupportedLanguages => LanguageOptions;

    public static string CurrentCultureName { get; private set; } = EnUsCultureName;

    public static async Task InitializeAsync(
        UiSettingsStore settingsStore,
        CancellationToken cancellationToken = default)
    {
        _settingsStore = settingsStore;
        var savedCultureName = await settingsStore.GetCultureNameAsync(cancellationToken);
        var cultureName = NormalizeCultureName(savedCultureName) ?? DetectDefaultCultureName();
        ApplyCulture(cultureName);
        _isInitialized = true;
    }

    public static void EnsureInitialized()
    {
        if (_isInitialized)
        {
            return;
        }

        ApplyCulture(DetectDefaultCultureName());
        _isInitialized = true;
    }

    public static async Task SetCultureAsync(
        string cultureName,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();

        var normalizedCultureName = NormalizeCultureName(cultureName)
                                    ?? throw new ArgumentException("Unsupported culture.", nameof(cultureName));

        ApplyCulture(normalizedCultureName);
        if (_settingsStore is not null)
        {
            await _settingsStore.SaveCultureNameAsync(normalizedCultureName, cancellationToken);
        }
    }

    public static string Get(string key)
    {
        EnsureInitialized();
        return WpfApplication.Current?.TryFindResource(key) as string ?? key;
    }

    public static string Format(string key, params object?[] args)
    {
        return string.Format(CultureInfo.CurrentCulture, Get(key), args);
    }

    public static string? NormalizeCultureName(string? cultureName)
    {
        if (string.IsNullOrWhiteSpace(cultureName))
        {
            return null;
        }

        var match = SupportedLanguages.FirstOrDefault(language =>
            string.Equals(language.CultureName, cultureName.Trim(), StringComparison.OrdinalIgnoreCase));

        return match?.CultureName;
    }

    private static string DetectDefaultCultureName()
    {
        var currentCultureName = CultureInfo.CurrentUICulture.Name;
        return currentCultureName.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
            ? ZhCnCultureName
            : EnUsCultureName;
    }

    private static void ApplyCulture(string cultureName)
    {
        CurrentCultureName = cultureName;

        var culture = CultureInfo.GetCultureInfo(cultureName);
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
        Thread.CurrentThread.CurrentCulture = culture;
        Thread.CurrentThread.CurrentUICulture = culture;

        ReplaceResourceDictionary(cultureName);
        CultureChanged?.Invoke(null, EventArgs.Empty);
    }

    private static void ReplaceResourceDictionary(string cultureName)
    {
        if (WpfApplication.Current is null)
        {
            return;
        }

        var resources = WpfApplication.Current.Resources.MergedDictionaries;
        var existingDictionaries = resources
            .Where(dictionary => dictionary.Source?.OriginalString.Contains(
                "Localization/Strings.",
                StringComparison.OrdinalIgnoreCase) == true)
            .ToArray();

        foreach (var dictionary in existingDictionaries)
        {
            resources.Remove(dictionary);
        }

        resources.Add(new ResourceDictionary
        {
            Source = new Uri($"Localization/Strings.{cultureName}.xaml", UriKind.Relative)
        });
    }
}
