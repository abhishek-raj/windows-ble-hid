using System.Text;
using BleHid.Localization;

namespace BleHid.Translations;

/// <summary>Everything the bot needs to say about, or do with, one submitted translation.</summary>
internal sealed class IssueReview
{
    public const string CommentMarker = "<!-- blehid-translation-check -->";

    private IssueReview(IssueSubmission submission, TranslationReport? report,
        IReadOnlyDictionary<string, string> english, IReadOnlyDictionary<string, string> existing,
        IReadOnlyDictionary<string, string>? merged, bool isNewLanguage)
    {
        Submission = submission;
        Report = report;
        English = english;
        Existing = existing;
        Merged = merged;
        IsNewLanguage = isNewLanguage;
    }

    public IssueSubmission Submission { get; }
    public TranslationReport? Report { get; }
    public IReadOnlyDictionary<string, string> English { get; }
    public IReadOnlyDictionary<string, string> Existing { get; }

    /// <summary>The language file as it would be after approval; null while there are errors.</summary>
    public IReadOnlyDictionary<string, string>? Merged { get; }

    public bool IsNewLanguage { get; }

    public bool IsValid => Submission.Errors.Count == 0 && Report is { IsValid: true } && Merged is not null
                           && Report.Accepted.Count > 0;

    public string? Language => Submission.Language;

    public static IssueReview Create(TranslationRepository repository, string? body)
    {
        var submission = IssueSubmission.Parse(body);
        var english = repository.English.ToDictionary();
        var existingFile = submission.Language is { } language ? repository.ReadFile(language) : null;
        var existing = existingFile?.ToDictionary() ?? new Dictionary<string, string>();

        TranslationReport? report = null;
        IReadOnlyDictionary<string, string>? merged = null;
        if (submission.Json is { } json)
        {
            report = TranslationValidator.Validate(english, TranslationFile.Parse(json));
            if (report.IsValid && submission.Errors.Count == 0)
                merged = TranslationRepository.Merge(english, existing, report.Accepted);
        }

        return new IssueReview(submission, report, english, existing, merged, existingFile is null);
    }

    public int NewStrings => Report?.Accepted.Keys.Count(key => !Existing.ContainsKey(key)) ?? 0;

    public int ChangedStrings => Report?.Accepted.Count(pair =>
        Existing.TryGetValue(pair.Key, out var old) && old != pair.Value) ?? 0;

    /// <summary>Keys that would still show English after this submission is merged.</summary>
    public IReadOnlyList<string> StillEnglish
    {
        get
        {
            var result = Merged ?? TranslationRepository.Merge(English, Existing,
                Report?.Accepted ?? new Dictionary<string, string>());
            return English.Keys.Where(key => !result.ContainsKey(key)).ToList();
        }
    }

    public string LanguageLabel => Language is null ? "?" : $"{Loc.NativeName(Language)} (`{Language}`)";

    /// <summary>The bot's comment. Never echoes submitted text, only key names made safe.</summary>
    public string Comment()
    {
        var text = new StringBuilder().AppendLine(CommentMarker);
        var errors = Submission.Errors.Select(message => (Key: (string?)null, Message: message))
            .Concat(Report?.Errors.Select(problem => (problem.Key, problem.Message)) ?? [])
            .ToList();

        if (IsValid)
        {
            text.AppendLine("## ✅ Ready for review").AppendLine();
            text.AppendLine($"**Language:** {LanguageLabel}{(IsNewLanguage ? " — new language" : string.Empty)}").AppendLine();
            text.AppendLine($"- **{Report!.Accepted.Count}** translated strings: **{NewStrings}** new, **{ChangedStrings}** changed.");
        }
        else
        {
            text.AppendLine("## ❌ Needs changes").AppendLine();
            if (Language is not null) text.AppendLine($"**Language:** {LanguageLabel}").AppendLine();
            if (errors.Count == 0 && Report is { Accepted.Count: 0 })
                errors.Add((null, "no translated strings were found. Translate the texts after the colons, and keep the keys before them unchanged."));
        }

        if (Report is { SameAsEnglish: > 0 })
            text.AppendLine($"- **{Report.SameAsEnglish}** strings were left in English, so they were skipped.");

        if (errors.Count > 0)
        {
            text.AppendLine().AppendLine($"### Please fix ({errors.Count})").AppendLine();
            foreach (var (key, message) in errors.Take(50))
                text.AppendLine(key is null ? $"- {message}" : $"- `{Markdown.Code(key)}`: {message}");
            if (errors.Count > 50) text.AppendLine($"- …and {errors.Count - 50} more.");
            text.AppendLine().AppendLine("Edit the issue to fix these; this check runs again automatically.");
        }

        if (Report is { Warnings.Count: > 0 })
        {
            text.AppendLine().AppendLine($"<details><summary>Skipped ({Report.Warnings.Count})</summary>").AppendLine();
            foreach (var warning in Report.Warnings.Take(50))
                text.AppendLine($"- `{Markdown.Code(warning.Key ?? "?")}`: {warning.Message}");
            text.AppendLine().AppendLine("</details>");
        }

        if (IsValid)
        {
            var missing = StillEnglish;
            if (missing.Count > 0)
            {
                text.AppendLine().AppendLine($"<details><summary>Still in English after this ({missing.Count})</summary>").AppendLine();
                foreach (var key in missing) text.AppendLine($"- `{key}`");
                text.AppendLine().AppendLine("These are optional; English is shown until someone translates them.").AppendLine().AppendLine("</details>");
            }

            text.AppendLine().AppendLine("A maintainer will review the translation. Approving it opens a pull request that credits you.");
        }

        return text.ToString();
    }
}
