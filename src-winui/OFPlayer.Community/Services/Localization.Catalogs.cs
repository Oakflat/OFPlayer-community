using System.Text.Json;
using System.Text.RegularExpressions;

namespace OFPlayer_WinUI.Services;
public static partial class Localization
{
    // Fail visibly when a shipped locale loses a key or parameter; never fill gaps with English.
    private static readonly Lazy<IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>> AdditionalLocales = new(LoadLocales);
    public static void ValidateCatalogs() => _ = AdditionalLocales.Value;
    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> LoadLocales()
    {
        var result = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
        foreach (var locale in new[]
        {
            "ja",
            "fr",
            "de"
        }

        )
        {
            using var stream = typeof(Localization).Assembly.GetManifestResourceStream($"OFPlayer.Localization.{locale}.json") ?? throw new InvalidOperationException($"Missing localization resource: {locale}");
            using var document = JsonDocument.Parse(stream);
            var catalog = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                var value = property.Value.GetString();
                if (string.IsNullOrWhiteSpace(value) || !catalog.TryAdd(property.Name, value))
                    throw new InvalidOperationException($"Empty or duplicate translation: {locale}/{property.Name}");
            }

            if (!catalog.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(Strings.Keys))
                throw new InvalidOperationException($"Localization keys differ: {locale}");
            foreach (var(key, entry)in Strings)
            {
                if (!Placeholders(entry.En).SequenceEqual(Placeholders(catalog[key]), StringComparer.Ordinal))
                    throw new InvalidOperationException($"Localization parameters differ: {locale}/{key}");
            }

            result.Add(locale, catalog);
        }

        return result;
    }

    private static IEnumerable<string> Placeholders(string value) => Regex.Matches(value, @"\{[A-Za-z][A-Za-z0-9]*\}").Select(match => match.Value).Order(StringComparer.Ordinal);
}
