using System.Net;
using System.Text.RegularExpressions;

namespace Unchain.Core;

public enum ListKind { Bypass, Exclude, ExcludeIp }

/// <summary>
/// Пользовательские списки. Хранятся в C:\ProgramData\Unchain\lists и перед запуском
/// копируются в lists\ каждой версии zapret под теми же именами, что у Flowseal.
/// </summary>
public static class UserLists
{
    // Пустой hostlist/ipset у winws значит «подходит всё», поэтому пустые файлы
    // заполняем заглушкой — так же делает сам Flowseal.
    public const string DomainPlaceholder = "domain.example.abc";
    public const string IpPlaceholder = "203.0.113.113/32";

    public static event Action? Changed;

    public static string FileName(ListKind k) => k switch
    {
        ListKind.Bypass => "list-general-user.txt",
        ListKind.Exclude => "list-exclude-user.txt",
        _ => "ipset-exclude-user.txt",
    };

    static string PathFor(ListKind k) => Path.Combine(Paths.UserLists, FileName(k));

    public static List<string> Read(ListKind k)
    {
        var p = PathFor(k);
        if (!File.Exists(p)) return new();
        return File.ReadAllLines(p)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#') && l != DomainPlaceholder && l != IpPlaceholder)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    static void Write(ListKind k, IEnumerable<string> items)
    {
        var list = items.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (list.Count == 0) list.Add(k == ListKind.ExcludeIp ? IpPlaceholder : DomainPlaceholder);
        File.WriteAllLines(PathFor(k), list);
        Changed?.Invoke();
    }

    /// <returns>Сколько новых записей добавлено.</returns>
    public static int Add(ListKind k, IEnumerable<string> items)
    {
        var cur = Read(k);
        var set = new HashSet<string>(cur, StringComparer.OrdinalIgnoreCase);
        var added = items.Where(set.Add).ToList();
        if (added.Count > 0) Write(k, cur.Concat(added));
        return added.Count;
    }

    public static void Remove(ListKind k, IEnumerable<string> items)
    {
        var drop = new HashSet<string>(items, StringComparer.OrdinalIgnoreCase);
        Write(k, Read(k).Where(x => !drop.Contains(x)));
    }

    public static bool Contains(ListKind k, string item) =>
        Read(k).Contains(item, StringComparer.OrdinalIgnoreCase);

    /// <summary>Копирует списки в папку lists конкретной версии zapret.</summary>
    public static void CopyTo(string listsDir)
    {
        Directory.CreateDirectory(listsDir);
        foreach (var k in Enum.GetValues<ListKind>())
        {
            var src = PathFor(k);
            if (!File.Exists(src)) Write(k, Array.Empty<string>());
            File.Copy(src, Path.Combine(listsDir, FileName(k)), true);
        }
    }

    static readonly Regex DomainRx = new(@"^(?=.{1,253}$)([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z]{2,63}$", RegexOptions.IgnoreCase);

    /// <summary>Принимает «https://www.site.com/path», «site.com», «*.site.com» и т.п.</summary>
    public static bool TryNormalizeDomain(string input, out string domain)
    {
        domain = "";
        var s = input.Trim().ToLowerInvariant();
        if (s.Length == 0) return false;
        if (s.Contains("://")) s = s[(s.IndexOf("://", StringComparison.Ordinal) + 3)..];
        var cut = s.IndexOfAny(new[] { '/', '?', '#', ':' });
        if (cut >= 0) s = s[..cut];
        if (s.StartsWith("*.")) s = s[2..];
        if (s.StartsWith("www.")) s = s[4..];
        s = s.TrimEnd('.');
        try { s = new System.Globalization.IdnMapping().GetAscii(s); } catch { return false; }
        if (!DomainRx.IsMatch(s)) return false;
        domain = s;
        return true;
    }

    public static bool TryNormalizeIp(string input, out string cidr)
    {
        cidr = "";
        var s = input.Trim();
        var parts = s.Split('/');
        if (parts.Length > 2 || !IPAddress.TryParse(parts[0], out var ip)) return false;
        var max = ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128;
        var bits = max;
        if (parts.Length == 2 && (!int.TryParse(parts[1], out bits) || bits < 0 || bits > max)) return false;
        cidr = $"{ip}/{bits}";
        return true;
    }
}

public sealed record CatalogEntry(string Name, string Category, string[] Domains);

public static class Catalog
{
    /// <summary>Уже входит во встроенные списки Flowseal.</summary>
    public static readonly (string Name, string Category)[] Builtin =
    {
        ("YouTube", "Видео и музыка"),
        ("Discord", "Общение"),
    };

    public static readonly string[] Categories = { "Общение", "Соцсети", "Игры", "Видео и музыка", "Другое" };

    // Сервисы, популярные у подростков и ограниченные в РФ (по новостям 2025–2026)
    public static readonly CatalogEntry[] Entries =
    {
        new("WhatsApp", "Общение", new[] { "whatsapp.com", "whatsapp.net", "wa.me" }),
        new("Snapchat", "Общение", new[] { "snapchat.com", "snap.com", "sc-cdn.net", "sc-static.net", "snapkit.com" }),
        new("Viber", "Общение", new[] { "viber.com" }),
        new("Signal", "Общение", new[] { "signal.org", "whispersystems.org", "signal.art" }),

        new("Instagram", "Соцсети", new[] { "instagram.com", "cdninstagram.com", "ig.me", "instagr.am" }),
        new("Threads", "Соцсети", new[] { "threads.net", "threads.com", "cdninstagram.com" }),
        new("X (Twitter)", "Соцсети", new[] { "x.com", "twitter.com", "twimg.com", "t.co" }),
        new("Facebook", "Соцсети", new[] { "facebook.com", "fbcdn.net", "fb.com", "facebook.net", "fbsbx.com" }),

        new("Roblox", "Игры", new[] { "roblox.com", "rbxcdn.com", "rbx.com", "robloxlabs.com" }),

        new("TikTok", "Видео и музыка", new[] { "tiktok.com", "tiktokv.com", "tiktokcdn.com", "tiktokcdn-eu.com", "byteoversea.com", "ibytedtos.com", "ttwstatic.com" }),
        new("SoundCloud", "Видео и музыка", new[] { "soundcloud.com", "sndcdn.com" }),

        new("Patreon", "Другое", new[] { "patreon.com", "patreonusercontent.com" }),
        new("Medium", "Другое", new[] { "medium.com" }),
    };

    public static bool IsAdded(CatalogEntry e)
    {
        var cur = new HashSet<string>(UserLists.Read(ListKind.Bypass), StringComparer.OrdinalIgnoreCase);
        return e.Domains.All(cur.Contains);
    }
}
