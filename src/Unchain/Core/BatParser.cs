using System.Text;
using System.Text.RegularExpressions;

namespace Unchain.Core;

/// <summary>
/// Достаёт из general*.bat строку запуска winws.exe и подставляет переменные,
/// чтобы запускать winws напрямую, без cmd и без окон.
/// </summary>
public static class BatParser
{
    const string GameRange = "1024-65535";
    const string GameOff = "12"; // так service.bat обозначает «выключено»

    public static string? ExtractWinwsArgs(string batPath, ZapretBuild build, GameFilterMode gameFilter)
    {
        var lines = JoinContinuations(File.ReadAllText(batPath, Encoding.UTF8));

        var launch = lines.FirstOrDefault(l =>
        {
            var t = l.TrimStart();
            return t.Contains("winws.exe", StringComparison.OrdinalIgnoreCase)
                   && t.Contains(" --") // строка запуска, а не tasklist/проверка процесса
                   && !t.StartsWith("::")
                   && !t.StartsWith("rem ", StringComparison.OrdinalIgnoreCase)
                   && !t.StartsWith("echo", StringComparison.OrdinalIgnoreCase);
        });
        if (launch == null) return null;

        var idx = launch.IndexOf("winws.exe", StringComparison.OrdinalIgnoreCase) + "winws.exe".Length;
        if (idx < launch.Length && launch[idx] == '"') idx++;
        var args = launch[idx..].Trim();

        var root = build.Root.TrimEnd('\\') + "\\";
        var tcp = gameFilter is GameFilterMode.Tcp or GameFilterMode.All;
        var udp = gameFilter is GameFilterMode.Udp or GameFilterMode.All;
        var vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["BIN"] = build.Bin + "\\",
            ["LISTS"] = build.Lists + "\\",
            ["GameFilter"] = gameFilter == GameFilterMode.Off ? GameOff : GameRange,
            ["GameFilterTCP"] = tcp ? GameRange : GameOff,
            ["GameFilterUDP"] = udp ? GameRange : GameOff,
        };

        // Переменные, объявленные в самом .bat (set "X=Y" / set X=Y)
        foreach (var l in lines)
        {
            var m = Regex.Match(l, @"^\s*set\s+""?([A-Za-z_][A-Za-z0-9_]*)=([^""]*)""?\s*$", RegexOptions.IgnoreCase);
            if (m.Success && !vars.ContainsKey(m.Groups[1].Value))
                vars[m.Groups[1].Value] = m.Groups[2].Value.Replace("%~dp0", root, StringComparison.OrdinalIgnoreCase);
        }

        args = args.Replace("%~dp0", root, StringComparison.OrdinalIgnoreCase);
        for (var pass = 0; pass < 3 && args.Contains('%'); pass++)
            args = Regex.Replace(args, @"%([A-Za-z_][A-Za-z0-9_]*)%", m =>
                vars.TryGetValue(m.Groups[1].Value, out var v) ? v : m.Value);

        foreach (Match m in Regex.Matches(args, @"%([A-Za-z_][A-Za-z0-9_]*)%"))
            Log.Write($"⚠ {Path.GetFileName(batPath)}: неизвестная переменная %{m.Groups[1].Value}%");

        // Экранирование cmd: ^! ^& ^| и т.п.
        args = Regex.Replace(args, @"\^(.)", "$1");
        return args;
    }

    static List<string> JoinContinuations(string text)
    {
        var result = new List<string>();
        var sb = new StringBuilder();
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var l = raw.TrimEnd();
            if (l.EndsWith('^'))
            {
                sb.Append(l, 0, l.Length - 1).Append(' ');
                continue;
            }
            sb.Append(l);
            result.Add(sb.ToString());
            sb.Clear();
        }
        if (sb.Length > 0) result.Add(sb.ToString());
        return result;
    }
}
