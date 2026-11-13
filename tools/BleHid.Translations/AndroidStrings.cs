using System.Text;
using BleHid.Localization;

namespace BleHid.Translations;

/// <summary>
/// Writes Android string resources from the shared JSON, so the companion app is translated by
/// the same files and the same issue form as the Windows app.
/// </summary>
internal static class AndroidStrings
{
    public const string Prefix = "android.";
    public const string FileName = "strings.xml";
    public const string Marker = "Generated from localization/";

    public static bool IsAndroidKey(string key) =>
        key.StartsWith(Prefix, StringComparison.Ordinal)
        && key.Length > Prefix.Length
        && char.IsAsciiLetterLower(key[Prefix.Length])
        && key[Prefix.Length..].All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '_');

    /// <summary>
    /// Android folder qualifiers: <c>de</c> becomes <c>values-de</c>; tags with a script or region
    /// use the BCP 47 form, e.g. <c>values-b+zh+Hans</c>.
    /// </summary>
    public static string PathFor(string resources, string language)
    {
        var folder = language == Loc.SourceLanguage ? "values"
            : language.Contains('-') ? "values-b+" + language.Replace('-', '+')
            : "values-" + language;
        return Path.Combine(resources, folder, FileName);
    }

    public static string Generate(IReadOnlyDictionary<string, string> english, IReadOnlyDictionary<string, string> strings,
        string language)
    {
        var xml = new StringBuilder()
            .Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n")
            .Append($"<!-- {Marker}{language}.json by tools/BleHid.Translations. Do not edit; see TRANSLATING.md. -->\n")
            .Append("<resources>\n");

        foreach (var (key, englishText) in english)
        {
            if (!IsAndroidKey(key) || !strings.TryGetValue(key, out var text)) continue;
            var order = Placeholders.Names(englishText);
            var formatted = order.Count > 0;
            var value = Escape(text, order);
            var attribute = !formatted && text.Contains('%') ? " formatted=\"false\"" : string.Empty;
            xml.Append($"    <string name=\"{key[Prefix.Length..]}\"{attribute}>{value}</string>\n");
        }

        return xml.Append("</resources>\n").ToString();
    }

    /// <summary>
    /// Android resource escaping plus placeholder conversion: <c>{host}</c> becomes <c>%1$s</c>,
    /// numbered by its position in the English text so translations may reorder them.
    /// </summary>
    internal static string Escape(string text, IReadOnlyList<string> order)
    {
        var formatted = order.Count > 0;
        var output = new StringBuilder();
        var withSlots = Placeholders.Scan(text, name => "\u0001" + (order.ToList().IndexOf(name) + 1) + "\u0002");

        for (var i = 0; i < withSlots.Length; i++)
        {
            var c = withSlots[i];
            switch (c)
            {
                case '\u0001':
                    var end = withSlots.IndexOf('\u0002', i);
                    output.Append('%').Append(withSlots, i + 1, end - i - 1).Append("$s");
                    i = end;
                    break;
                case '%' when formatted: output.Append("%%"); break;
                case '&': output.Append("&amp;"); break;
                case '<': output.Append("&lt;"); break;
                case '>': output.Append("&gt;"); break;
                case '\\': output.Append("\\\\"); break;
                case '\'': output.Append("\\'"); break;
                case '"': output.Append("\\\""); break;
                case '\n': output.Append("\\n"); break;
                case '@' or '?' when output.Length == 0: output.Append('\\').Append(c); break;
                default: output.Append(c); break;
            }
        }
        return output.ToString();
    }
}
