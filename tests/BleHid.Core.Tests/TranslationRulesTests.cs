using BleHid.Localization;
using Xunit;

namespace BleHid.Core.Tests;

/// <summary>
/// The rules every translation must pass. The issue bot and the build run the same code, so a
/// broken string is rejected before it can reach a release.
/// </summary>
public sealed class TranslationRulesTests
{
    [Fact]
    public void Placeholders_are_listed_once_in_order_and_escaped_braces_are_not_placeholders()
    {
        Assert.Equal(["host", "count"], Placeholders.Names("{host} has {count} keys, {host}"));
        Assert.Empty(Placeholders.Names("literal {{braces}} and { spaced }"));
    }

    [Fact]
    public void Scanning_replaces_each_placeholder_and_unescapes_braces_once() =>
        Assert.Equal("[a] {b} [c]", Placeholders.Scan("{a} {{b}} {c}", name => $"[{name}]"));

    [Fact]
    public void Parser_tolerates_trailing_commas_and_comments_and_separates_notes()
    {
        var file = TranslationFile.Parse("""
            {
              // pasted from an issue
              "app.x": "Text",
              "@app.x": { "description": "note" },
            }
            """);

        Assert.True(file.IsValid);
        Assert.Equal("Text", Assert.Single(file.Strings).Value);
        Assert.Equal("note", file.Notes["app.x"]);
    }

    [Theory]
    [InlineData("{\"app.x\": \"a\", \"app.x\": \"b\"}", "more than once")]
    [InlineData("{\"app.x\": 5}", "must be text")]
    [InlineData("[\"app.x\"]", "one JSON object")]
    [InlineData("{\n\"app.x\": “a”\n}", "line 2")]
    public void Parser_explains_unusable_input_in_plain_words(string json, string expected)
    {
        var file = TranslationFile.Parse(json);
        Assert.False(file.IsValid);
        Assert.Contains(expected, Assert.Single(file.Errors).Message);
    }

    [Theory]
    [InlineData("Senden an: {ziel}", "{target}")]
    [InlineData("Senden an", "{target}")]
    [InlineData("Senden an {target} {extra}", "{extra}")]
    public void Placeholders_must_match_the_english_exactly(string translation, string mentioned)
    {
        var problem = TranslationValidator.CheckText("Sending to: {target}", translation);
        Assert.NotNull(problem);
        Assert.Contains(mentioned, problem);
    }

    [Theory]
    [InlineData("a\u0007b")]
    [InlineData("a\u202Eb")]
    [InlineData("a\u2067b")]
    public void Invisible_and_direction_override_characters_are_rejected(string text) =>
        Assert.NotNull(TranslationValidator.CheckText("ab", text));

    [Fact]
    public void Right_to_left_marks_and_line_breaks_are_allowed() =>
        Assert.Null(TranslationValidator.CheckText("a\nb", "\u200Fא\nב"));

    [Fact]
    public void Text_far_longer_than_the_english_is_rejected()
    {
        Assert.Null(TranslationValidator.CheckText("Stop", new string('x', 200)));
        Assert.NotNull(TranslationValidator.CheckText("Stop", new string('x', 201)));
    }

    [Fact]
    public void Unknown_empty_and_untranslated_entries_are_skipped_without_blocking_the_rest()
    {
        var english = new Dictionary<string, string> { ["app.a"] = "One", ["app.b"] = "Two", ["app.c"] = "Three" };
        var report = TranslationValidator.Validate(english, TranslationFile.Parse("""
            { "app.c": "Drei", "app.a": "One", "app.b": " ", "app.old": "Alt" }
            """));

        Assert.True(report.IsValid);
        Assert.Equal(["app.c"], report.Accepted.Keys);
        Assert.Equal(1, report.SameAsEnglish);
        Assert.Equal(2, report.Warnings.Count);
    }

    [Theory]
    [InlineData("de", true)]
    [InlineData("pt-BR", true)]
    [InlineData("zh-Hans", true)]
    [InlineData("en", false)]
    [InlineData("en-GB", false)]
    [InlineData("DE", false)]
    [InlineData("de-de", false)]
    [InlineData("de;rm", false)]
    [InlineData("", false)]
    public void Language_codes_must_be_real_non_english_tags(string code, bool valid) =>
        Assert.Equal(valid, TranslationValidator.IsValidLanguageCode(code));

    [Theory]
    [InlineData("app.capture.title", true)]
    [InlineData("android.share_diagnostics", true)]
    [InlineData("App.title", false)]
    [InlineData("title", false)]
    [InlineData("app..title", false)]
    public void Keys_are_lowercase_dotted_paths(string key, bool valid) =>
        Assert.Equal(valid, TranslationValidator.IsValidKey(key));
}

/// <summary>Language selection mutates process-wide state, so these tests do not run in parallel.</summary>
[Collection(nameof(LocalizationStateCollection))]
public sealed class LanguageSelectionTests : IDisposable
{
    public void Dispose() => Loc.Initialize(Loc.SourceLanguage);

    [Theory]
    [InlineData("de-AT", "de")]
    [InlineData("zh-CN", "zh-Hans")]
    [InlineData("zh-SG", "zh-Hans")]
    [InlineData("zh-TW", "en")]
    [InlineData("en-GB", "en")]
    public void The_closest_available_translation_is_chosen(string requested, string expected)
    {
        Loc.Initialize(requested);
        Assert.Equal(expected, Loc.Language);
    }

    [Fact]
    public void Missing_strings_fall_back_to_english_and_unknown_keys_to_the_key()
    {
        Loc.Initialize("de");
        Assert.Equal("BleHid Companion", Loc.T("android.app_name"));
        Assert.Equal("no.such.key", Loc.T("no.such.key"));
    }

    [Theory]
    [InlineData("de", "Strg+Alt+Q", "Strg+Alt+Feststelltaste", "Strg + D + C")]
    [InlineData("zh-Hans", "Ctrl+Alt+Q", "Ctrl+Alt+CapsLock", "Ctrl + D + C")]
    [InlineData("en", "Ctrl+Alt+Q", "Ctrl+Alt+CapsLock", "Ctrl + D + C")]
    public void Shortcuts_use_the_translated_key_names_in_every_string(string language, string stop, string next, string alternative)
    {
        Loc.Initialize(language);
        Shortcuts.ShareWithTranslations();

        Assert.Equal(stop, Shortcuts.Stop);
        Assert.Equal(next, Shortcuts.SwitchTarget);
        Assert.Contains(stop, Loc.T("app.capture.redirecting.message"));
        Assert.Contains(next, Loc.T("app.capture.redirecting.message"));
        Assert.Contains(next, Loc.T("app.settings.gesture.description"));
        Assert.Contains(stop, Loc.T("cli.help.capture"));
        Assert.Contains(alternative.Replace(" ", string.Empty), Loc.T("cli.help.switchTarget"));
        Assert.EndsWith(alternative, Loc.Fill("app.capture.switchShortcutAlternative", Shortcuts.SwitchTargetAlternativeSpaced));
    }

    [Fact]
    public void Explicit_values_fill_their_own_placeholders_and_shared_ones_by_name()
    {
        Loc.Initialize("en");
        Shortcuts.ShareWithTranslations();
        Assert.Equal("Sending to: X", Loc.T("app.capture.sendingTo", ("target", "X")));
        Assert.Equal("Sending to: X", Loc.Fill("app.capture.sendingTo", "X"));
        Assert.DoesNotContain("{", Loc.T("app.capture.redirecting.message"));
    }

    [Fact]
    public void Every_shipped_translation_reaches_the_runtime()
    {
        Assert.Contains("en", Loc.AvailableLanguages);
        foreach (var language in Loc.AvailableLanguages.Where(code => code != Loc.SourceLanguage))
        {
            Loc.Initialize(language);
            Assert.Equal(language, Loc.Language);
            Assert.NotEqual(Loc.SourceStrings["app.nav.settings"], Loc.T("app.nav.settings"));
        }
    }
}

[CollectionDefinition(nameof(LocalizationStateCollection), DisableParallelization = true)]
public sealed class LocalizationStateCollection;
