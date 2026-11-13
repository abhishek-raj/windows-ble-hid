namespace BleHid.Translations;

/// <summary>Makes untrusted text safe to echo inside inline code in a GitHub comment.</summary>
internal static class Markdown
{
    public static string Code(string text, int maxLength = 80)
    {
        var flat = new string(text.Select(c => char.IsControl(c) ? ' ' : c).ToArray()).Replace('`', '\'');
        return flat.Length <= maxLength ? flat : flat[..maxLength] + "…";
    }
}
