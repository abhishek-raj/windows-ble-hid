using System.Text.RegularExpressions;

namespace BleHid.Translations;

/// <summary>
/// A translation submitted through the issue form. The body is untrusted user input: it is only
/// ever parsed here and validated, never passed to a shell or written without validation.
/// </summary>
internal sealed partial record IssueSubmission(string? Language, string? Json, IReadOnlyList<string> Errors, bool LicenseAccepted)
{
    // Must match the field labels in .github/ISSUE_TEMPLATE/translation.yml.
    internal const string LanguageHeading = "Language";
    internal const string OtherCodeHeading = "Language code";
    internal const string TranslationsHeading = "Translations";
    internal const string LicenseHeading = "License";

    public static IssueSubmission Parse(string? body)
    {
        var errors = new List<string>();
        var sections = Sections(body ?? string.Empty);

        var language = ResolveLanguage(sections, errors);

        var json = sections.TryGetValue(TranslationsHeading, out var raw) ? StripFences(raw) : null;
        if (string.IsNullOrWhiteSpace(json) || json.Trim() == "_No response_")
        {
            errors.Add("the **Translations** field is empty. Paste the translated JSON there.");
            json = null;
        }

        var license = sections.TryGetValue(LicenseHeading, out var licenseText)
                      && CheckedBox().IsMatch(licenseText);
        if (!license) errors.Add("please tick the license box, so your translation can be included.");

        return new IssueSubmission(language, json, errors, license);
    }

    private static string? ResolveLanguage(IReadOnlyDictionary<string, string> sections, List<string> errors)
    {
        if (!sections.TryGetValue(LanguageHeading, out var choice) || string.IsNullOrWhiteSpace(choice))
        {
            errors.Add("choose a **Language**.");
            return null;
        }

        var code = TrailingCode().Match(choice.Trim()) is { Success: true } match ? match.Groups[1].Value : null;
        if (code is null && sections.TryGetValue(OtherCodeHeading, out var other) && other.Trim() != "_No response_")
            code = other.Trim();

        if (code is null)
        {
            errors.Add("for **Other**, enter a **Language code** such as `pt-BR` or `zh-Hant`.");
            return null;
        }

        if (!BleHid.Localization.TranslationValidator.IsValidLanguageCode(code))
        {
            errors.Add($"`{Markdown.Code(code)}` is not a language code BleHid can use. Use a code such as `fr`, `pt-BR` or `zh-Hant`.");
            return null;
        }

        return code;
    }

    /// <summary>Issue forms render each field as a <c>### Label</c> heading followed by its value.</summary>
    internal static Dictionary<string, string> Sections(string body)
    {
        var sections = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? current = null;
        var lines = new List<string>();

        void Flush()
        {
            if (current is not null && !sections.ContainsKey(current)) sections[current] = string.Join('\n', lines).Trim();
            lines.Clear();
        }

        foreach (var line in body.Replace("\r\n", "\n").Split('\n'))
        {
            var heading = HeadingLine().Match(line);
            if (heading.Success)
            {
                Flush();
                current = heading.Groups[1].Value.Trim();
                continue;
            }
            lines.Add(line);
        }
        Flush();
        return sections;
    }

    /// <summary>The form wraps JSON in a code fence, and some people add their own as well.</summary>
    internal static string StripFences(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
        while (lines.Count > 0 && (string.IsNullOrWhiteSpace(lines[0]) || lines[0].TrimStart().StartsWith("```", StringComparison.Ordinal)))
            lines.RemoveAt(0);
        while (lines.Count > 0 && (string.IsNullOrWhiteSpace(lines[^1]) || lines[^1].Trim().StartsWith("```", StringComparison.Ordinal)))
            lines.RemoveAt(lines.Count - 1);
        return string.Join('\n', lines);
    }

    [GeneratedRegex(@"^###\s+(.+?)\s*$")]
    private static partial Regex HeadingLine();

    [GeneratedRegex(@"\(([A-Za-z]{2,3}(?:-[A-Za-z0-9]{2,4})*)\)$")]
    private static partial Regex TrailingCode();

    [GeneratedRegex(@"^\s*-\s*\[[xX]\]", RegexOptions.Multiline)]
    private static partial Regex CheckedBox();
}
