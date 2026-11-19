using System.IO.Compression;
using System.Text.RegularExpressions;

namespace Unchain.Core;

public sealed class ZapretBuild
{
    public required string Tag { get; init; }
    public required string Root { get; init; }
    public string Bin => Path.Combine(Root, "bin");
    public string Lists => Path.Combine(Root, "lists");
    public required IReadOnlyList<string> Strategies { get; init; }
}

/// <summary>Скачивание, хранение и подготовка релизов Flowseal/zapret-discord-youtube.</summary>
public static class ZapretStore
{
    public const string Repo = "Flowseal/zapret-discord-youtube";
    const string IpsetUrl = "https://raw.githubusercontent.com/Flowseal/zapret-discord-youtube/main/lists/ipset-all.txt";
    const string IpsetFile = "ipset-all.txt";
    const string IpsetBackup = "ipset-all.loaded.txt";

    public static async Task<List<ReleaseInfo>> ListRemoteAsync(CancellationToken ct)
    {
        var all = await GitHub.GetReleasesAsync(Repo, ct);
        return all.Where(r => !r.Prerelease).ToList();
    }

    /// <summary>Уже скачанные версии, новые сверху (на случай, если GitHub недоступен).</summary>
    public static List<ReleaseInfo> ListLocal() =>
        Directory.Exists(Paths.Zapret)
            ? Directory.GetDirectories(Paths.Zapret)
                .Where(d => FindRoot(d) != null)
                .OrderByDescending(Directory.GetLastWriteTimeUtc)
                .Select(d => ReleaseInfo.Local(Path.GetFileName(d)))
                .ToList()
            : new();

    public static ZapretBuild? TryLoadLocal(string tag)
    {
        var root = FindRoot(DirFor(tag));
        return root == null ? null : Load(tag, root);
    }

    public static async Task<ZapretBuild> EnsureAsync(ReleaseInfo rel, IProgress<string>? progress, CancellationToken ct)
    {
        var local = TryLoadLocal(rel.Tag);
        if (local != null) return local;

        var url = rel.Assets.FirstOrDefault(a => a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))?.Url
                  ?? rel.ZipballUrl
                  ?? throw new InvalidOperationException($"В релизе {rel.Tag} нет архива");

        var dir = DirFor(rel.Tag);
        var tmp = Path.Combine(Paths.Root, $"dl-{Guid.NewGuid():N}.zip");
        try
        {
            Log.Write($"Скачиваю zapret {rel.Tag}");
            await GitHub.DownloadAsync(url, tmp, progress, $"Загрузка {rel.Tag}", ct);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            ZipFile.ExtractToDirectory(tmp, dir);
        }
        finally
        {
            try { File.Delete(tmp); } catch { }
        }

        var root = FindRoot(dir) ?? throw new InvalidOperationException(
            $"В {rel.Tag} не найден bin\\winws.exe. Возможно, его удалил антивирус — добавь {Paths.Root} в исключения.");
        return Load(rel.Tag, root);
    }

    /// <summary>Перед запуском: свои списки и режим IPSet.</summary>
    public static void Prepare(ZapretBuild build, IpsetMode ipset)
    {
        UserLists.CopyTo(build.Lists);

        var file = Path.Combine(build.Lists, IpsetFile);
        var backup = Path.Combine(build.Lists, IpsetBackup);
        if (!File.Exists(backup))
        {
            // Первый раз: сохраняем оригинальный список (если он уже не заглушка)
            var cur = File.Exists(file) ? File.ReadAllText(file).Trim() : "";
            if (cur.Length > 0 && cur != UserLists.IpPlaceholder) File.WriteAllText(backup, cur + Environment.NewLine);
        }

        switch (ipset)
        {
            case IpsetMode.Loaded:
                if (File.Exists(backup)) File.Copy(backup, file, true);
                break;
            case IpsetMode.None:
                File.WriteAllText(file, UserLists.IpPlaceholder + Environment.NewLine);
                break;
            case IpsetMode.Any:
                File.WriteAllText(file, "");
                break;
        }
    }

    /// <summary>Свежий ipset-all.txt из репозитория — во все скачанные версии.</summary>
    public static async Task<int> UpdateIpsetAsync(CancellationToken ct)
    {
        var text = await GitHub.Http.GetStringAsync(IpsetUrl, ct);
        var count = text.Split('\n').Count(l => l.Trim().Length > 0 && !l.TrimStart().StartsWith('#'));
        if (count < 10) throw new InvalidOperationException("Получен подозрительно короткий список, не применяю");

        foreach (var rel in ListLocal())
        {
            var b = TryLoadLocal(rel.Tag);
            if (b == null) continue;
            Directory.CreateDirectory(b.Lists);
            File.WriteAllText(Path.Combine(b.Lists, IpsetBackup), text);
        }
        Log.Write($"IPSet обновлён: {count} записей");
        return count;
    }

    public static int BuiltinDomainCount(ZapretBuild b)
    {
        var f = Path.Combine(b.Lists, "list-general.txt");
        return File.Exists(f)
            ? File.ReadLines(f).Count(l => l.Trim().Length > 0 && !l.TrimStart().StartsWith('#'))
            : 0;
    }

    static string DirFor(string tag) =>
        Path.Combine(Paths.Zapret, string.Concat(tag.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)));

    static string? FindRoot(string dir)
    {
        if (!Directory.Exists(dir)) return null;
        foreach (var exe in Directory.EnumerateFiles(dir, "winws.exe", SearchOption.AllDirectories))
        {
            var bin = Path.GetDirectoryName(exe)!;
            if (Path.GetFileName(bin).Equals("bin", StringComparison.OrdinalIgnoreCase))
                return Path.GetDirectoryName(bin);
        }
        return null;
    }

    static ZapretBuild Load(string tag, string root)
    {
        var strategies = Directory.GetFiles(root, "general*.bat")
            .OrderBy(f => Path.GetFileName(f).Equals("general.bat", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(f => NaturalKey(Path.GetFileNameWithoutExtension(f)), StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new ZapretBuild { Tag = tag, Root = root, Strategies = strategies };
    }

    static string NaturalKey(string s) => Regex.Replace(s, @"\d+", m => m.Value.PadLeft(6, '0'));

    /// <summary>«general (ALT3).bat» → «ALT3», «general.bat» → «Стандартная».</summary>
    public static string StrategyName(string file)
    {
        var n = Path.GetFileNameWithoutExtension(file);
        var m = Regex.Match(n, @"\((.+)\)");
        if (m.Success) return m.Groups[1].Value;
        return n.Equals("general", StringComparison.OrdinalIgnoreCase) ? "Стандартная" : n;
    }
}
