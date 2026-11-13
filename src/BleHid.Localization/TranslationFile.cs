using System.Text.Json;

namespace BleHid.Localization;

/// <summary>
/// One language file: a flat JSON object of key to text. Keys starting with <c>@</c> carry
/// notes for translators (ARB style, <c>{"description": "..."}</c>) and are never displayed.
/// </summary>
public sealed class TranslationFile
{
    private static readonly JsonDocumentOptions Options = new()
    {
        // Hand-edited by translators, often pasted from an issue: be forgiving about what does
        // not change the meaning.
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip
    };

    private TranslationFile(IReadOnlyList<KeyValuePair<string, string>> strings,
        IReadOnlyDictionary<string, string> notes, IReadOnlyList<TranslationProblem> errors)
    {
        Strings = strings;
        Notes = notes;
        Errors = errors;
    }

    /// <summary>Displayable strings in file order.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Strings { get; }

    /// <summary>Translator notes by the key they describe.</summary>
    public IReadOnlyDictionary<string, string> Notes { get; }

    /// <summary>
    /// Problems that make parts of the file unusable, written for translators. Submitted key
    /// names stay in <see cref="TranslationProblem.Key"/>, never in the message, so whoever
    /// displays them can make untrusted text safe.
    /// </summary>
    public IReadOnlyList<TranslationProblem> Errors { get; }

    public bool IsValid => Errors.Count == 0;

    public Dictionary<string, string> ToDictionary() =>
        Strings.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

    public static TranslationFile Parse(string json)
    {
        var strings = new List<KeyValuePair<string, string>>();
        var notes = new Dictionary<string, string>(StringComparer.Ordinal);
        var errors = new List<TranslationProblem>();

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, Options);
        }
        catch (JsonException ex)
        {
            var line = ex.LineNumber is { } number ? $"line {number + 1}: " : string.Empty;
            errors.Add(new(null, $"{line}this is not valid JSON. Check for a missing comma or quote, or for "
                       + "curly quotes (“ ”) used instead of straight ones (\") around keys and texts."));
            return new TranslationFile(strings, notes, errors);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                errors.Add(new(null, "the translations must be one JSON object: { \"key\": \"text\", ... }"));
                return new TranslationFile(strings, notes, errors);
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!seen.Add(property.Name))
                {
                    errors.Add(new(property.Name, "appears more than once; keep only one."));
                    continue;
                }

                if (property.Name.StartsWith('@'))
                {
                    if (property.Value.ValueKind == JsonValueKind.Object
                        && property.Value.TryGetProperty("description", out var description)
                        && description.ValueKind == JsonValueKind.String)
                        notes[property.Name[1..]] = description.GetString()!;
                    continue;
                }

                if (property.Value.ValueKind != JsonValueKind.String)
                {
                    errors.Add(new(property.Name, "must be text in quotes."));
                    continue;
                }

                strings.Add(new(property.Name, property.Value.GetString()!));
            }
        }

        return new TranslationFile(strings, notes, errors);
    }
}
