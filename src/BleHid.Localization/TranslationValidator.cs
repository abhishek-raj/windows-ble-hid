using System.Globalization;
using System.Text.RegularExpressions;

namespace BleHid.Localization;

/// <summary>A problem with one submitted string, phrased for the translator who wrote it.</summary>
public sealed record TranslationProblem(string? Key, string Message);

/// <summary>Outcome of checking a translation against the English source.</summary>
public sealed class TranslationReport
{
    /// <summary>Strings that passed every check, in English file order.</summary>
    public required IReadOnlyDictionary<string, string> Accepted { get; init; }

    /// <summary>Problems that must be fixed before the translation can be used.</summary>
    public required IReadOnlyList<TranslationProblem> Errors { get; init; }

    /// <summary>Strings that were skipped but do not block the rest of the submission.</summary>
    public required IReadOnlyList<TranslationProblem> Warnings { get; init; }

    /// <summary>Strings left identical to English; these fall back to English anyway.</summary>
    public required int SameAsEnglish { get; init; }

    public bool IsValid => Errors.Count == 0;
}

public static partial class TranslationValidator
{
    /// <summary>Keys are lowercase, dot-separated paths such as <c>app.capture.title</c>.</summary>
    public static bool IsValidKey(string key) => KeyPattern().IsMatch(key);

    public static TranslationReport Validate(IReadOnlyDictionary<string, string> english, TranslationFile translation)
    {
        var errors = translation.Errors.ToList();
        var warnings = new List<TranslationProblem>();
        var accepted = new Dictionary<string, string>(StringComparer.Ordinal);
        var sameAsEnglish = 0;

        foreach (var (key, text) in translation.Strings)
        {
            if (!english.TryGetValue(key, out var source))
            {
                warnings.Add(new(key, "BleHid does not use this key (maybe from an older version), so it was ignored."));
                continue;
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                warnings.Add(new(key, "empty, so English will be shown."));
                continue;
            }

            if (string.Equals(text, source, StringComparison.Ordinal))
            {
                sameAsEnglish++;
                continue;
            }

            var problem = CheckText(source, text);
            if (problem is not null) errors.Add(new(key, problem));
            else accepted[key] = text;
        }

        // English order keeps generated files and diffs stable regardless of submission order.
        var ordered = english.Keys.Where(accepted.ContainsKey)
            .ToDictionary(key => key, key => accepted[key], StringComparer.Ordinal);

        return new TranslationReport
        {
            Accepted = ordered,
            Errors = errors,
            Warnings = warnings,
            SameAsEnglish = sameAsEnglish
        };
    }

    /// <summary>Null when the translated text is safe to use in place of the English one.</summary>
    public static string? CheckText(string english, string text)
    {
        var expected = Placeholders.Names(english).Order(StringComparer.Ordinal).ToList();
        var actual = Placeholders.Names(text).Order(StringComparer.Ordinal).ToList();
        if (!expected.SequenceEqual(actual, StringComparer.Ordinal))
        {
            var missing = expected.Except(actual, StringComparer.Ordinal).Select(name => $"{{{name}}}").ToList();
            var extra = actual.Except(expected, StringComparer.Ordinal).Select(name => $"{{{name}}}").ToList();
            var parts = new List<string>();
            if (missing.Count > 0) parts.Add($"must contain {string.Join(", ", missing)} exactly as written");
            if (extra.Count > 0) parts.Add($"contains {string.Join(", ", extra)}, which is not in the English text");
            return string.Join("; ", parts) + ". Words in {braces} are filled in by BleHid, so keep them untranslated.";
        }

        foreach (var c in text)
        {
            if (char.IsControl(c) && c != '\n')
                return "contains an invisible control character; retype the text.";
            // Embedding and override controls can make text display differently from how it is
            // stored. Plain marks (U+200E/U+200F) remain allowed for right-to-left languages.
            if (c is >= '\u202A' and <= '\u202E' or >= '\u2066' and <= '\u2069')
                return "contains a hidden text-direction control character; remove it.";
        }

        var limit = Math.Max(200, english.Length * 5);
        if (text.Length > limit)
            return $"is {text.Length} characters long, much longer than the English; please shorten it (limit {limit}).";

        return null;
    }

    /// <summary>
    /// Accepts language tags such as <c>de</c>, <c>pt-BR</c> or <c>zh-Hans</c> that .NET knows
    /// and that are not English, which is the source language.
    /// </summary>
    public static bool IsValidLanguageCode(string code)
    {
        if (!LanguagePattern().IsMatch(code) || code.StartsWith("en", StringComparison.Ordinal) && (code.Length == 2 || code[2] == '-'))
            return false;

        try
        {
            CultureInfo.GetCultureInfo(code, predefinedOnly: true);
            return true;
        }
        catch (CultureNotFoundException)
        {
            return false;
        }
    }

    [GeneratedRegex(@"^[a-z][a-z0-9]*(\.[a-z][a-zA-Z0-9_]*)+$")]
    private static partial Regex KeyPattern();

    [GeneratedRegex(@"^[a-z]{2,3}(-[A-Z][a-z]{3})?(-([A-Z]{2}|[0-9]{3}))?$")]
    private static partial Regex LanguagePattern();
}
