using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using BleHid.Localization;

namespace BleHid.Translations;

/// <summary>The translation files on disk and everything generated from them.</summary>
internal sealed class TranslationRepository
{
    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Indented = true,
        // Translators read these files: keep their letters readable instead of \u escapes.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public TranslationRepository(string root)
    {
        Root = root;
        LocalizationDirectory = Path.Combine(root, "localization");
        AndroidResources = Path.Combine(root, "android", "BleHid.Companion", "app", "src", "main", "res");
    }

    public string Root { get; }
    public string LocalizationDirectory { get; }
    public string AndroidResources { get; }

    /// <summary>Walks up from <paramref name="start"/> to the folder holding localization/en.json.</summary>
    public static TranslationRepository Find(string start)
    {
        for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "localization", "en.json")))
                return new TranslationRepository(directory.FullName);
        throw new InvalidOperationException($"no localization/en.json above {start}");
    }

    public TranslationFile English => ReadFile(Loc.SourceLanguage)!;

    /// <summary>Every translated language, excluding English.</summary>
    public IReadOnlyList<string> Languages =>
        Directory.EnumerateFiles(LocalizationDirectory, "*.json")
            .Select(Path.GetFileNameWithoutExtension)
            .OfType<string>()
            .Where(code => code != Loc.SourceLanguage)
            .Order(StringComparer.Ordinal)
            .ToList();

    public string FilePath(string language) => Path.Combine(LocalizationDirectory, language + ".json");

    public TranslationFile? ReadFile(string language) =>
        File.Exists(FilePath(language)) ? TranslationFile.Parse(File.ReadAllText(FilePath(language))) : null;

    /// <summary>
    /// Folds accepted strings over an existing translation. Strings the submission leaves out are
    /// kept, and keys English no longer has are dropped.
    /// </summary>
    public static Dictionary<string, string> Merge(IReadOnlyDictionary<string, string> english,
        IReadOnlyDictionary<string, string> existing, IReadOnlyDictionary<string, string> accepted)
    {
        var merged = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in english.Keys)
        {
            if (accepted.TryGetValue(key, out var text)) merged[key] = text;
            else if (existing.TryGetValue(key, out var kept) && TranslationValidator.CheckText(english[key], kept) is null)
                merged[key] = kept;
        }
        return merged;
    }

    /// <summary>Flat JSON in English key order, so diffs show only real changes.</summary>
    public static string Serialize(IReadOnlyDictionary<string, string> strings)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            writer.WriteStartObject();
            foreach (var (key, text) in strings) writer.WriteString(key, text);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray()).Replace("\r\n", "\n") + "\n";
    }

    public void Write(string language, IReadOnlyDictionary<string, string> strings) =>
        File.WriteAllText(FilePath(language), Serialize(strings), new UTF8Encoding(false));

    /// <summary>Generated Android resources keyed by path, for English and every translation.</summary>
    public IReadOnlyDictionary<string, string> AndroidFiles()
    {
        var english = English.ToDictionary();
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [AndroidStrings.PathFor(AndroidResources, Loc.SourceLanguage)] = AndroidStrings.Generate(english, english, Loc.SourceLanguage)
        };

        foreach (var language in Languages)
        {
            var translated = ReadFile(language)!.ToDictionary();
            if (!translated.Keys.Any(AndroidStrings.IsAndroidKey)) continue;
            files[AndroidStrings.PathFor(AndroidResources, language)] = AndroidStrings.Generate(english, translated, language);
        }
        return files;
    }

    /// <summary>Generated resource files on disk, including ones for languages that no longer exist.</summary>
    public IReadOnlyList<string> ExistingAndroidFiles() =>
        Directory.Exists(AndroidResources)
            ? Directory.EnumerateDirectories(AndroidResources, "values*")
                .Select(folder => Path.Combine(folder, AndroidStrings.FileName))
                .Where(path => File.Exists(path) && File.ReadAllText(path).Contains(AndroidStrings.Marker, StringComparison.Ordinal))
                .ToList()
            : [];
}
