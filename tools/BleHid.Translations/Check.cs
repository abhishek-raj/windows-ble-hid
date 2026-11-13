using BleHid.Localization;

namespace BleHid.Translations;

/// <summary>
/// Keeps the repository consistent: valid translations in canonical form, and Android resources
/// regenerated from them. Runs in tests, so a stale file fails the build rather than a release.
/// </summary>
internal static class Check
{
    public static int Run(TranslationRepository repository, bool fix, TextWriter output)
    {
        var problems = new List<string>();
        var englishFile = repository.English;
        problems.AddRange(englishFile.Errors.Select(error => $"en.json: {error.Key} {error.Message}".Trim()));
        var english = englishFile.ToDictionary();

        foreach (var key in english.Keys.Where(key => !TranslationValidator.IsValidKey(key)))
            problems.Add($"en.json: `{key}` is not a valid key (lowercase dot-separated, e.g. app.capture.title)");
        foreach (var key in english.Keys.Where(key => key.StartsWith(AndroidStrings.Prefix, StringComparison.Ordinal) && !AndroidStrings.IsAndroidKey(key)))
            problems.Add($"en.json: `{key}` must be android.snake_case to become an Android resource name");
        foreach (var key in englishFile.Notes.Keys.Where(key => !english.ContainsKey(key)))
            problems.Add($"en.json: note @{key} describes a key that does not exist");

        foreach (var language in repository.Languages)
        {
            var file = repository.ReadFile(language)!;
            var report = TranslationValidator.Validate(english, file);
            var canonical = TranslationRepository.Serialize(report.Accepted);
            var current = File.ReadAllText(repository.FilePath(language)).Replace("\r\n", "\n");

            foreach (var error in report.Errors) problems.Add($"{language}.json: {error.Key} {error.Message}".Trim());
            if (!report.IsValid) continue;

            if (current == canonical) continue;
            if (fix) repository.Write(language, report.Accepted);
            else problems.Add($"{language}.json: has unused, empty or untranslated entries, or is not in canonical order; run `check --fix`");
        }

        var expected = repository.AndroidFiles();
        foreach (var (path, content) in expected)
        {
            var actual = File.Exists(path) ? File.ReadAllText(path).Replace("\r\n", "\n") : null;
            if (actual == content) continue;
            if (fix)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, content);
            }
            else problems.Add($"{Path.GetRelativePath(repository.Root, path)} is out of date; run `check --fix`");
        }

        foreach (var stale in repository.ExistingAndroidFiles().Where(path => !expected.ContainsKey(path)))
        {
            if (fix) File.Delete(stale);
            else problems.Add($"{Path.GetRelativePath(repository.Root, stale)} no longer has a source language file; run `check --fix`");
        }

        foreach (var problem in problems) output.WriteLine(problem);
        if (problems.Count == 0) output.WriteLine($"ok: {repository.Languages.Count} translations, {english.Count} strings");
        return problems.Count == 0 ? 0 : 1;
    }
}
