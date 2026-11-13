using System.Text.RegularExpressions;
using BleHid.Localization;
using BleHid.Translations;
using Xunit;

namespace BleHid.Core.Tests;

/// <summary>
/// The translation bot reads untrusted issue text. These pin down that it extracts only what
/// the form defines, never echoes submitted text back, and produces credit-safe commits.
/// </summary>
public sealed partial class TranslationIssueTests
{
    private static string Body(string language, string json, bool license = true, string otherCode = "_No response_") => $"""
        ### Language

        {language}

        ### Language code

        {otherCode}

        ### Translations

        ```json
        {json}
        ```

        ### License

        - [{(license ? "X" : " ")}] I license my translation under the MIT License.
        """;

    [Fact]
    public void A_form_submission_yields_the_language_and_json()
    {
        var submission = IssueSubmission.Parse(Body("German (de)", "{ \"app.nav.status\": \"Status\" }"));

        Assert.Empty(submission.Errors);
        Assert.Equal("de", submission.Language);
        Assert.Equal("{ \"app.nav.status\": \"Status\" }", submission.Json);
    }

    [Fact]
    public void Other_uses_the_typed_code_and_rejects_invalid_ones()
    {
        Assert.Equal("pt-BR", IssueSubmission.Parse(Body("Other", "{}", otherCode: "pt-BR")).Language);

        var invalid = IssueSubmission.Parse(Body("Other", "{}", otherCode: "x`y"));
        Assert.Null(invalid.Language);
        Assert.Contains(invalid.Errors, error => error.Contains("x'y"));
    }

    [Fact]
    public void An_unticked_license_or_empty_field_is_reported()
    {
        var submission = IssueSubmission.Parse(Body("German (de)", "_No response_", license: false));
        Assert.Equal(2, submission.Errors.Count);
    }

    [Fact]
    public void Extra_code_fences_pasted_by_the_translator_are_removed() =>
        Assert.Equal("{ }", IssueSubmission.StripFences("```json\n```\n{ }\n```\n```"));

    [Fact]
    public void Review_accepts_a_valid_translation_without_echoing_submitted_text()
    {
        var repository = TranslationRepository.Find(AppContext.BaseDirectory);
        const string payload = "<script>@everyone</script>";
        var review = IssueReview.Create(repository, Body("Other", $$"""
            { "app.nav.status": "{{payload}}", "`bad` key": "x" }
            """, otherCode: "fr"));

        Assert.True(review.IsValid);
        var comment = review.Comment();
        Assert.StartsWith(IssueReview.CommentMarker, comment);
        Assert.Contains("Ready for review", comment);
        Assert.DoesNotContain(payload, comment);
        Assert.DoesNotContain("`bad`", comment);
    }

    [Fact]
    public void Malicious_keys_in_parse_errors_cannot_escape_into_the_comment()
    {
        var repository = TranslationRepository.Find(AppContext.BaseDirectory);
        const string attack = "x` @octocat\\n\\n# Injected [link](https://example.com) `y";
        var review = IssueReview.Create(repository, Body("Other", $$"""
            { "app.nav.status": "Zustand", "{{attack}}": 1, "{{attack}}2": "a", "{{attack}}2": "b" }
            """, otherCode: "fr"));

        var comment = review.Comment();
        Assert.False(review.IsValid);
        Assert.DoesNotContain("\n# Injected", comment);
        foreach (var line in comment.Split('\n').Where(line => line.Contains("octocat")))
        {
            // The key is inside one code span, so the mention and link are not rendered.
            Assert.StartsWith("- `", line);
            Assert.Equal(2, line.Count(c => c == '`'));
        }
    }

    [Fact]
    public void Review_lists_placeholder_errors_and_blocks_approval()
    {
        var repository = TranslationRepository.Find(AppContext.BaseDirectory);
        var review = IssueReview.Create(repository, Body("Other", """
            { "app.capture.sendingTo": "Senden an: {ziel}" }
            """, otherCode: "fr"));

        Assert.False(review.IsValid);
        Assert.Null(review.Merged);
        Assert.Contains("{target}", review.Comment());
    }

    [Fact]
    public void Merging_keeps_untouched_strings_and_drops_keys_english_no_longer_has()
    {
        var english = new Dictionary<string, string> { ["app.a"] = "A {x}", ["app.b"] = "B", ["app.c"] = "C" };
        var existing = new Dictionary<string, string> { ["app.a"] = "Alt {x}", ["app.b"] = "Bee", ["app.gone"] = "G" };
        var accepted = new Dictionary<string, string> { ["app.b"] = "Neu" };

        var merged = TranslationRepository.Merge(english, existing, accepted);

        Assert.Equal(["app.a", "app.b"], merged.Keys);
        Assert.Equal("Alt {x}", merged["app.a"]);
        Assert.Equal("Neu", merged["app.b"]);
    }

    [Fact]
    public void Serialized_files_keep_letters_readable_and_end_with_a_newline()
    {
        var json = TranslationRepository.Serialize(new Dictionary<string, string> { ["app.a"] = "Größe 中文 \"q\"\nx" });
        Assert.Contains("Größe 中文", json);
        Assert.Contains("\\\"q\\\"\\nx", json);
        Assert.EndsWith("}\n", json);
    }

    [Theory]
    [InlineData("valid-login", true)]
    [InlineData("bad\nCo-authored-by: x", false)]
    [InlineData("name <x@y>", false)]
    public void Only_plain_github_logins_reach_the_commit_trailer(string login, bool accepted)
    {
        var path = Path.Combine(Path.GetTempPath(), $"issue-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(new
        {
            issue = new { number = 7, body = "", user = new { login, id = 42 } }
        }));
        try
        {
            if (accepted)
                Assert.Equal($"Co-authored-by: {login} <42+{login}@users.noreply.github.com>", GitHubIssue.Read(path).CoAuthorTrailer);
            else
                Assert.Throws<InvalidDataException>(() => GitHubIssue.Read(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("It's \"ok\" & <fine>", "It\\'s \\\"ok\\\" &amp; &lt;fine&gt;")]
    [InlineData("@home", "\\@home")]
    [InlineData("100% {b} then {a}", "100%% %2$s then %1$s")]
    [InlineData("line\nbreak", "line\\nbreak")]
    public void Android_text_is_escaped_and_placeholders_follow_english_order(string text, string expected) =>
        Assert.Equal(expected, AndroidStrings.Escape(text, text.Contains('{') ? ["a", "b"] : []));

    [Theory]
    [InlineData("de", "values-de")]
    [InlineData("zh-Hans", "values-b+zh+Hans")]
    [InlineData("pt-BR", "values-b+pt+BR")]
    [InlineData("en", "values")]
    public void Android_folders_use_resource_qualifiers(string language, string folder) =>
        Assert.Equal(Path.Combine("res", folder, "strings.xml"), AndroidStrings.PathFor("res", language));
}

/// <summary>
/// Keeps the English source, the code that uses it and the generated Android resources in step.
/// </summary>
public sealed partial class TranslationRepositoryTests
{
    private static readonly TranslationRepository Repository = TranslationRepository.Find(AppContext.BaseDirectory);

    [Fact]
    public void Translation_files_and_android_resources_are_valid_and_current()
    {
        var output = new StringWriter();
        Assert.True(Check.Run(Repository, fix: false, output) == 0, output.ToString());
    }

    [Fact]
    public void Every_key_used_in_code_exists_and_every_english_string_is_used()
    {
        var english = Repository.English.ToDictionary();
        var used = UsedKeys().ToHashSet(StringComparer.Ordinal);

        var missing = used.Where(key => !english.ContainsKey(key)).Order().ToList();
        var unused = english.Keys.Where(key => !used.Contains(key)).Order().ToList();

        Assert.True(missing.Count == 0, "used but missing from en.json: " + string.Join(", ", missing));
        Assert.True(unused.Count == 0, "in en.json but never used: " + string.Join(", ", unused));
    }

    [Fact]
    public void Embedded_strings_match_the_files_translators_edit()
    {
        foreach (var language in Repository.Languages.Append(Loc.SourceLanguage))
            Assert.Equal(File.ReadAllText(Repository.FilePath(language)).Replace("\r\n", "\n"),
                Loc.ReadFile(language)?.Replace("\r\n", "\n"));
    }

    private static IEnumerable<string> UsedKeys()
    {
        var src = Path.Combine(Repository.Root, "src");
        foreach (var file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories).Where(NotBuildOutput))
            foreach (Match match in CodeKey().Matches(File.ReadAllText(file)))
                yield return match.Groups[1].Value;

        foreach (var file in Directory.EnumerateFiles(src, "*.xaml", SearchOption.AllDirectories).Where(NotBuildOutput))
            foreach (Match match in XamlKey().Matches(File.ReadAllText(file)))
                yield return match.Groups[1].Value;

        var android = Path.Combine(Repository.Root, "android", "BleHid.Companion", "app", "src", "main");
        foreach (var file in Directory.EnumerateFiles(android, "*.kt", SearchOption.AllDirectories))
            foreach (Match match in KotlinKey().Matches(File.ReadAllText(file)))
                yield return AndroidStrings.Prefix + match.Groups[1].Value;

        foreach (var file in Directory.EnumerateFiles(android, "*.xml", SearchOption.AllDirectories)
                     .Where(path => !File.ReadAllText(path).Contains(AndroidStrings.Marker, StringComparison.Ordinal)))
            foreach (Match match in AndroidXmlKey().Matches(File.ReadAllText(file)))
                yield return AndroidStrings.Prefix + match.Groups[1].Value;
    }

    private static bool NotBuildOutput(string path) =>
        !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
        && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}");

    [GeneratedRegex("\"((?:app|cli|common|target|lock|advertisement|key)\\.[A-Za-z0-9_.]+)\"")]
    private static partial Regex CodeKey();

    [GeneratedRegex(@"\{l:(?:Tr|Format) ([A-Za-z0-9_.]+)\}")]
    private static partial Regex XamlKey();

    [GeneratedRegex(@"R\.string\.([a-z0-9_]+)")]
    private static partial Regex KotlinKey();

    [GeneratedRegex(@"@string/([a-z0-9_]+)")]
    private static partial Regex AndroidXmlKey();
}
