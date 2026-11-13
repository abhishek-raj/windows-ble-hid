using System.Text;
using BleHid.Core;
using BleHid.Localization;

namespace BleHid.Cli;

/// <summary>Language selection and layout for translated console output.</summary>
internal static class ConsoleText
{
    private const string LanguageVariable = "BLEHID_LANG";

    /// <summary>
    /// <c>--lang de</c>, then the BLEHID_LANG environment variable, then the Windows display language.
    /// </summary>
    public static void Initialize(string[] args)
    {
        var index = Array.FindIndex(args, arg => arg.Equals("--lang", StringComparison.OrdinalIgnoreCase));
        var requested = index >= 0 && index + 1 < args.Length
            ? args[index + 1]
            : Environment.GetEnvironmentVariable(LanguageVariable);
        Loc.Initialize(requested);
        Shortcuts.ShareWithTranslations();

        // Background mode detaches from the console straight away, so it neither needs the switch
        // nor could restore it afterwards.
        if (Loc.Language == Loc.SourceLanguage || Console.IsOutputRedirected
            || args.Contains("--background", StringComparer.OrdinalIgnoreCase)) return;

        // Legacy console code pages cannot show Chinese and show some accented letters wrongly.
        // The code page outlives this process in the user's window, so it is put back on exit.
        var original = Console.OutputEncoding;
        Console.OutputEncoding = Encoding.UTF8;
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try
            {
                Console.OutputEncoding = original;
            }
            catch (IOException)
            {
                // The console has gone; there is nothing left to restore.
            }
        };
    }

    /// <summary>One help line: an untranslated command, padded, then its translated description.</summary>
    public static string Help(string command, string key, int width = 21) =>
        $"  {command.PadRight(width)}{Loc.T(key)}";

    /// <summary>Label/value rows with the values aligned, whatever the labels' language.</summary>
    public static IEnumerable<string> Rows(params (string Label, string Value)[] rows)
    {
        var width = rows.Max(row => Width(row.Label));
        return rows.Select(row => $"  {row.Label}{new string(' ', width - Width(row.Label))} : {row.Value}");
    }

    // East Asian wide characters take two console cells, so character counts misalign them.
    private static int Width(string text) => text.Sum(c => IsWide(c) ? 2 : 1);

    private static bool IsWide(char c) =>
        c is >= '\u1100' and <= '\u115F'
            or >= '\u2E80' and <= '\uA4CF'
            or >= '\uAC00' and <= '\uD7A3'
            or >= '\uF900' and <= '\uFAFF'
            or >= '\uFE30' and <= '\uFE4F'
            or >= '\uFF00' and <= '\uFF60'
            or >= '\uFFE0' and <= '\uFFE6';
}
