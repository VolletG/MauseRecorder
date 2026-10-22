using System.Globalization;
using System.Windows;

namespace MauseRecorder.Services;

/// <summary>
/// Swaps the merged string dictionary at runtime so every
/// {DynamicResource ...} in the UI updates instantly.
/// </summary>
public static class LocalizationService
{
    public const string English = "en";
    public const string Russian = "ru";

    public static string Language { get; private set; } = English;

    public static event Action? LanguageChanged;

    public static void Apply(string? language)
    {
        var target = language == Russian ? Russian : English;
        Language = target;

        // Keep numbers/dates in sync with the chosen UI language. Full globalization is
        // enabled (InvariantGlobalization is off) so Russian text boxes also work.
        var culture = CultureInfo.GetCultureInfo(target == Russian ? "ru-RU" : "en-US");
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;

        var app = Application.Current;
        if (app is null) return;

        var merged = app.Resources.MergedDictionaries;
        var dictionary = new ResourceDictionary
        {
            Source = new Uri($"Localization/Strings.{target}.xaml", UriKind.Relative)
        };

        var existing = merged.FirstOrDefault(d =>
            d.Source is not null &&
            d.Source.OriginalString.Contains("Localization/Strings.", StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            var index = merged.IndexOf(existing);
            merged[index] = dictionary;
        }
        else
        {
            merged.Add(dictionary);
        }

        LanguageChanged?.Invoke();
    }

    /// <summary>Looks up a localized string; falls back to the key itself.</summary>
    public static string T(string key) =>
        Application.Current?.TryFindResource(key) as string ?? key;

    /// <summary>Formats a localized string with <see cref="string.Format(string, object[])"/>.</summary>
    public static string F(string key, params object[] args)
    {
        var template = T(key);
        try
        {
            return string.Format(template, args);
        }
        catch (FormatException)
        {
            return template;
        }
    }

    /// <summary>
    /// Picks a plural form from keys like "S_take_one", "S_take_few", "S_take_many".
    /// Russian uses one/few/many; English only uses one/many.
    /// </summary>
    public static string Plural(string prefix, int count)
    {
        if (Language == Russian)
        {
            var mod10 = Math.Abs(count) % 10;
            var mod100 = Math.Abs(count) % 100;
            if (mod10 == 1 && mod100 != 11) return T(prefix + "_one");
            if (mod10 is >= 2 and <= 4 && mod100 is < 12 or > 14) return T(prefix + "_few");
            return T(prefix + "_many");
        }

        return T(count == 1 ? prefix + "_one" : prefix + "_many");
    }
}
