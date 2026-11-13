using System.Globalization;
using System.Reflection;

namespace BleHid.Localization;

/// <summary>
/// Looks up user-facing text in the chosen language, falling back to English per string.
/// Diagnostic logs deliberately stay English so bug reports remain readable to maintainers.
/// </summary>
public static class Loc
{
    public const string SourceLanguage = "en";
    private const string ResourcePrefix = "BleHid.Localization.Strings.";

    private static readonly Lazy<IReadOnlyDictionary<string, string>> English =
        new(() => Load(SourceLanguage) ?? new Dictionary<string, string>());

    private static volatile Catalog _current = new(SourceLanguage, []);

    /// <summary>The language actually in use, e.g. <c>de</c> or <c>en</c>.</summary>
    public static string Language => _current.Language;

    /// <summary>True when the language in use is written right to left.</summary>
    public static bool IsRightToLeft =>
        CultureInfo.GetCultureInfo(Language).TextInfo.IsRightToLeft;

    /// <summary>Every language that has a translation file, including English, sorted by code.</summary>
    public static IReadOnlyList<string> AvailableLanguages { get; } =
        typeof(Loc).Assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith(ResourcePrefix, StringComparison.Ordinal) && name.EndsWith(".json", StringComparison.Ordinal))
            .Select(name => name[ResourcePrefix.Length..^".json".Length])
            .Order(StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// Chooses the language for this process. <paramref name="preferred"/> is a language code;
    /// null or empty follows the Windows display language.
    /// </summary>
    public static void Initialize(string? preferred = null)
    {
        var culture = CultureInfo.CurrentUICulture;
        if (!string.IsNullOrWhiteSpace(preferred))
        {
            try
            {
                culture = CultureInfo.GetCultureInfo(preferred.Trim());
            }
            catch (CultureNotFoundException)
            {
                // An unknown override falls back to the system language rather than failing to start.
            }
        }

        var chain = new List<IReadOnlyDictionary<string, string>>();
        string? language = null;
        // Parents give the right fallback without a table: zh-CN -> zh-Hans, de-AT -> de.
        for (var current = culture; !string.IsNullOrEmpty(current.Name); current = current.Parent)
        {
            if (current.Name.Equals(SourceLanguage, StringComparison.OrdinalIgnoreCase)) break;
            var match = AvailableLanguages.FirstOrDefault(code => code.Equals(current.Name, StringComparison.OrdinalIgnoreCase));
            if (match is null || match == SourceLanguage) continue;
            if (Load(match) is { } strings)
            {
                language ??= match;
                chain.Add(strings);
            }
        }

        _current = new Catalog(language ?? SourceLanguage, chain);
    }

    /// <summary>The text for <paramref name="key"/>; the key itself if it is missing even in English.</summary>
    public static string T(string key) => Resolve(Lookup(key), null);

    /// <summary>The text for <paramref name="key"/> with its named placeholders filled in.</summary>
    public static string T(string key, params (string Name, object? Value)[] values) =>
        Resolve(Lookup(key), values.ToDictionary(pair => pair.Name, pair => pair.Value, StringComparer.Ordinal));

    /// <summary>
    /// The text for a key with a single placeholder of its own, whatever that placeholder is
    /// called. Shared values such as shortcuts are still filled in by name.
    /// </summary>
    public static string Fill(string key, object? value) =>
        Placeholders.Scan(Lookup(key), name => SharedValues.TryGetValue(name, out var shared) ? shared() : Format(value));

    /// <summary>
    /// Makes <c>{name}</c> available to every string, computed when the string is used. Shortcuts
    /// are shared this way so every sentence shows the same, translated key names.
    /// </summary>
    public static void SetSharedValue(string name, Func<string> value) => SharedValues[name] = value;

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Func<string>> SharedValues =
        new(StringComparer.Ordinal);

    private static string Lookup(string key)
    {
        foreach (var strings in _current.Chain)
            if (strings.TryGetValue(key, out var text)) return text;
        return English.Value.TryGetValue(key, out var english) ? english : key;
    }

    // One pass over the text, so escaped braces are unescaped exactly once.
    private static string Resolve(string text, IReadOnlyDictionary<string, object?>? values) =>
        Placeholders.Scan(text, name =>
            values is not null && values.TryGetValue(name, out var value) ? Format(value)
            : SharedValues.TryGetValue(name, out var shared) ? shared()
            : "{" + name + "}");

    private static string Format(object? value) => Convert.ToString(value, CultureInfo.CurrentCulture) ?? string.Empty;

    /// <summary>The English source strings, for tooling and tests.</summary>
    public static IReadOnlyDictionary<string, string> SourceStrings => English.Value;

    /// <summary>Raw file contents for a language code, for tooling and tests.</summary>
    public static string? ReadFile(string language)
    {
        using var stream = typeof(Loc).Assembly.GetManifestResourceStream($"{ResourcePrefix}{language}.json");
        if (stream is null) return null;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>The language's own name for itself, e.g. Deutsch or 中文(简体).</summary>
    public static string NativeName(string language)
    {
        try
        {
            var native = CultureInfo.GetCultureInfo(language).NativeName;
            return native.Length > 0 ? char.ToUpper(native[0], CultureInfo.GetCultureInfo(language)) + native[1..] : language;
        }
        catch (CultureNotFoundException)
        {
            return language;
        }
    }

    private static Dictionary<string, string>? Load(string language)
    {
        var json = ReadFile(language);
        if (json is null) return null;
        // A broken file must not stop the app; tests and the bot are what keep files valid.
        var file = TranslationFile.Parse(json);
        return file.ToDictionary();
    }

    private sealed record Catalog(string Language, IReadOnlyList<IReadOnlyDictionary<string, string>> Chain);
}
