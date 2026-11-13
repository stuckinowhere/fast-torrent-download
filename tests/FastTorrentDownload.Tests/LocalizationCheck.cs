using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using FastTorrentDownload.Services;

/// <summary>
/// Guards the Spanish translation: every string the app can show through Loc.T, Loc.I or a
/// XAML literal must have an entry, placeholders must match, and the generated dictionary
/// must be in step with es.txt.
/// </summary>
internal static class LocalizationCheck
{
    private const string Separator = " ⇒ ";

    public static void Run()
    {
        var root = FindRepositoryRoot();
        var app = Path.Combine(root, "src", "FastTorrentDownload");

        // 1. The generated dictionary matches its source file.
        var source = new Dictionary<string, string>(StringComparer.Ordinal);
        var keep = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in File.ReadAllLines(Path.Combine(app, "Services", "Localization", "es.txt"), Encoding.UTF8))
        {
            if (string.IsNullOrWhiteSpace(raw) || raw.StartsWith('#')) continue;
            if (raw.StartsWith("!keep ", StringComparison.Ordinal)) { keep.Add(raw["!keep ".Length..]); continue; }
            var at = raw.IndexOf(Separator, StringComparison.Ordinal);
            Require(at > 0, $"es.txt line has no separator: {raw}");
            source[raw[..at]] = raw[(at + Separator.Length)..];
        }

        Require(source.Count == LocSpanish.Map.Count && source.All(pair => LocSpanish.Map.TryGetValue(pair.Key, out var value) && value == pair.Value),
            "LocSpanish.cs is stale: run python scripts/Generate-Localization.py");
        Require(keep.SetEquals(LocSpanish.Untranslated), "LocSpanish.Untranslated is stale: run python scripts/Generate-Localization.py");

        // 2. Placeholders survive translation ({0:0.0} may change its surroundings, not its index or format).
        foreach (var pair in LocSpanish.Map)
        {
            Require(Placeholders(pair.Key).SequenceEqual(Placeholders(pair.Value)), $"Placeholders differ in the translation of: {pair.Key}");
        }

        // 3. Every string used in code or XAML is translated or deliberately kept.
        var used = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(app, "*.cs", SearchOption.AllDirectories))
        {
            if (IsBuildOutput(file) || Path.GetFileName(file) is "Loc.cs" or "LocSpanish.cs") continue;
            var text = File.ReadAllText(file, Encoding.UTF8);
            foreach (Match match in Regex.Matches(text, "Loc\\.T\\(\"((?:[^\"\\\\]|\\\\.)*)\"\\)")) used.Add(Unescape(match.Groups[1].Value));
            foreach (Match match in Regex.Matches(text, "Loc\\.I\\(\\$\"((?:[^\"\\\\]|\\\\.)*)\"\\)")) used.Add(FormatKey(Unescape(match.Groups[1].Value)));
        }

        foreach (var file in Directory.EnumerateFiles(app, "*.axaml", SearchOption.AllDirectories))
        {
            if (IsBuildOutput(file)) continue;
            foreach (Match match in Regex.Matches(File.ReadAllText(file, Encoding.UTF8),
                         "\\b(?:Text|Content|Title|Header|PlaceholderText|ToolTip\\.Tip|AutomationProperties\\.Name)=\"([^\"{][^\"]*)\""))
            {
                var value = WebUtility.HtmlDecode(match.Groups[1].Value);
                if (Regex.IsMatch(value, "[A-Za-z]{2}")) used.Add(value);
            }
        }

        var untranslated = used.Where(text => !LocSpanish.Map.ContainsKey(text) && !LocSpanish.Untranslated.Contains(text)).ToArray();
        Require(used.Count > 200, $"The source scan found only {used.Count} strings; the scanner is broken.");
        Require(untranslated.Length == 0, $"{untranslated.Length} string(s) have no Spanish translation in es.txt: {string.Join(" | ", untranslated.Take(5))}");

        // 4. Runtime behaviour: English is the identity, Spanish translates and formats, unknown text passes through.
        try
        {
            Require(Loc.Language == Loc.English && Loc.T("Settings saved.") == "Settings saved.");
            var name = "demo";
            Require(Loc.I($"Paused {name}.") == "Paused demo." && Loc.Known("Stalled") == "Stalled");
            Require(Loc.Resolve("System", CultureInfo.GetCultureInfo("es-ES")) == Loc.Spanish && Loc.Resolve("System", CultureInfo.GetCultureInfo("fr-FR")) == Loc.English);
            Require(Loc.Resolve("English", CultureInfo.GetCultureInfo("es-ES")) == Loc.English && Loc.Resolve("Español", CultureInfo.GetCultureInfo("en-US")) == Loc.Spanish);

            Loc.Use(Loc.Spanish);
            Require(Loc.T("Settings saved.") == "Ajustes guardados." && Loc.I($"Paused {name}.") == "En pausa: demo.");
            var count = 3;
            Require(Loc.I($"Added {count} trackers to {name}.") == "Añadidos 3 trackers a demo.");
            Require(Loc.Known("Stalled") == "Parado" && Loc.Known("some library message") == "some library message" && Loc.Known(null) == string.Empty);
            Require(Loc.Misses.Count == 0);
            Require(Loc.T("a string nobody translated") == "a string nobody translated" && Loc.Misses.Contains("a string nobody translated"));
            var row = new FastTorrentDownload.ViewModels.TorrentRowViewModel(
                new FastTorrentDownload.Models.TorrentSnapshot("id", "demo", "folder", "Stalled", 62.7, 0, 0, 0, 0, 0, 0)
                {
                    Diagnostics = new FastTorrentDownload.Models.TorrentDiagnostics(20, 2, 0, 0, 62.7, "Ready", 50, 0, 0, [])
                    {
                        SwarmSeeders = 0, SwarmLeechers = 17, StalledFor = TimeSpan.FromMinutes(6)
                    }
                });
            Require(row.StateDisplay == "Parado" && row.StateLabel == "Stalled", "The chip is translated; the state identifier is not.");
            Require(row.DetailLabel.Contains("Sin semillas en el enjambre · 17 descargadores más esperando") && row.DetailLabel.Contains("verificado"));
            Require(row.DiagnosticsText.StartsWith("Estado del torrent: Parado") && row.DiagnosticsText.Contains("DHT: Listo · 50 nodos"));
        }
        finally
        {
            Loc.Use(Loc.English);
        }

        Console.WriteLine($"PASS Spanish localization covers all {used.Count} strings used in code and XAML, with matching placeholders");
    }

    private static bool IsBuildOutput(string file) =>
        file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
        file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal);

    private static string FindRepositoryRoot()
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            if (File.Exists(Path.Combine(folder.FullName, "FastTorrentDownload.sln"))) return folder.FullName;
        }

        throw new InvalidOperationException("Could not locate the repository root from the test output folder.");
    }

    private static string[] Placeholders(string text) =>
        Regex.Matches(text.Replace("{{", string.Empty).Replace("}}", string.Empty), "\\{(\\d+)(:[^}]*)?\\}")
            .Select(match => match.Groups[1].Value + match.Groups[2].Value).OrderBy(value => value, StringComparer.Ordinal).ToArray();

    private static string Unescape(string literal)
    {
        var builder = new StringBuilder(literal.Length);
        for (var index = 0; index < literal.Length; index++)
        {
            if (literal[index] == '\\' && index + 1 < literal.Length)
            {
                index++;
                builder.Append(literal[index] switch { 'n' => '\n', 't' => '\t', var other => other });
            }
            else
            {
                builder.Append(literal[index]);
            }
        }

        return builder.ToString();
    }

    // Mirrors how the compiler turns $"… {expr:format} …" into a composite format string.
    private static string FormatKey(string body)
    {
        var builder = new StringBuilder(body.Length);
        var hole = 0;
        for (var index = 0; index < body.Length; index++)
        {
            var current = body[index];
            if ((current is '{' or '}') && index + 1 < body.Length && body[index + 1] == current)
            {
                builder.Append(current).Append(current);
                index++;
                continue;
            }

            if (current != '{')
            {
                builder.Append(current);
                continue;
            }

            var depth = 0;
            var colon = -1;
            var end = index + 1;
            for (; ; end++)
            {
                var inner = body[end];
                if (inner is '(' or '[') depth++;
                else if (inner is ')' or ']') depth--;
                else if (inner == '?' && body[end + 1] == '?') end++;
                else if (inner == ':' && depth == 0 && colon < 0) colon = end;
                else if (inner == '}' && depth == 0) break;
            }

            builder.Append('{').Append(hole++);
            if (colon >= 0) builder.Append(body, colon, end - colon);
            builder.Append('}');
            index = end;
        }

        return builder.ToString();
    }

    private static void Require(bool condition, string? message = null)
    {
        if (!condition) throw new InvalidOperationException(message ?? "Localization assertion failed.");
    }
}
