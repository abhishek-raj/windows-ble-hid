using System.Text;

namespace BleHid.Localization;

/// <summary>
/// Named placeholders such as <c>{host}</c>. Names tell translators what will be inserted, which
/// positional <c>{0}</c> cannot. Literal braces are written <c>{{</c> and <c>}}</c>.
/// </summary>
public static class Placeholders
{
    /// <summary>Placeholder names in order of first appearance.</summary>
    public static IReadOnlyList<string> Names(string text)
    {
        var names = new List<string>();
        Scan(text, name =>
        {
            if (!names.Contains(name, StringComparer.Ordinal)) names.Add(name);
            return string.Empty;
        });
        return names;
    }

    /// <summary>Calls <paramref name="replace"/> per placeholder and returns the rewritten text.</summary>
    public static string Scan(string text, Func<string, string> replace)
    {
        var output = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if ((c == '{' || c == '}') && i + 1 < text.Length && text[i + 1] == c)
            {
                output.Append(c);
                i++;
                continue;
            }

            if (c == '{')
            {
                var end = text.IndexOf('}', i + 1);
                if (end > i + 1 && IsName(text.AsSpan(i + 1, end - i - 1)))
                {
                    output.Append(replace(text.Substring(i + 1, end - i - 1)));
                    i = end;
                    continue;
                }
            }

            output.Append(c);
        }
        return output.ToString();
    }

    private static bool IsName(ReadOnlySpan<char> name)
    {
        if (name.IsEmpty || !char.IsAsciiLetter(name[0])) return false;
        foreach (var c in name)
            if (!char.IsAsciiLetterOrDigit(c) && c != '_') return false;
        return true;
    }
}
