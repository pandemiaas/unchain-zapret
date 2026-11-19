using System.Diagnostics;
using System.Net;

namespace Unchain.Core;

/// <param name="Service">К какому сервису относится проверка (для статусов на главной).</param>
/// <param name="MinBytes">
/// Сколько байт тела нужно дочитать. Ловит «зависание» после ~16 КБ, типичное для ТСПУ.
/// 0 — достаточно получить любой HTTP-ответ (важно, что TLS прошёл).
/// </param>
/// <param name="Core">Основные сервисы (YouTube, Discord) — весят больше при выборе стратегии.</param>
public sealed record Target(string Service, string Name, string Url, int MinBytes = 0, bool Core = false);

/// <param name="Ms">Время до ответа сервера (пинг через стратегию), если дошли.</param>
public sealed record CheckResult(Target Target, bool Ok, string Detail, int? Ms = null);

public sealed record TestResult(IReadOnlyList<CheckResult> Items)
{
    public int Passed => Items.Count(i => i.Ok);
    public int Total => Items.Count;
    public bool AllOk => Passed == Total;
    public bool CoreOk => Items.Where(i => i.Target.Core).All(i => i.Ok);

    /// <summary>Очки для сравнения стратегий: основной сервис = 10, выбранный пользователем = 1.</summary>
    public int Score => Items.Sum(i => i.Ok ? (i.Target.Core ? 10 : 1) : 0);

    /// <summary>Лучше ли этот результат: больше очков, при равенстве — меньше пинг.</summary>
    public bool BetterThan(TestResult? other) =>
        other == null || Score > other.Score || (Score == other.Score && (AvgMs ?? int.MaxValue) < (other.AvgMs ?? int.MaxValue));

    /// <summary>Средний пинг по успешным проверкам, мс.</summary>
    public int? AvgMs
    {
        get
        {
            var ms = Items.Where(i => i.Ok && i.Ms != null).Select(i => i.Ms!.Value).ToList();
            return ms.Count == 0 ? null : (int)Math.Round(ms.Average());
        }
    }

    /// <summary>null — сервис не проверялся.</summary>
    public bool? ServiceOk(string service)
    {
        var items = Items.Where(i => i.Target.Service == service).ToList();
        return items.Count == 0 ? null : items.All(i => i.Ok);
    }

    public override string ToString() =>
        $"{Passed}/{Total}{(AvgMs is { } ms ? $" · {ms} мс" : "")}  " + string.Join(", ", Items.Select(i => $"{i.Target.Name} {(i.Ok ? "✓" : "✗ " + i.Detail)}"));
}

public static class Connectivity
{
    public const string YouTube = "YouTube";
    public const string Discord = "Discord";

    public static readonly Target[] ZapretTargets =
    {
        new(YouTube, "YouTube", "https://www.youtube.com/", 64 * 1024, true),
        new(YouTube, "googlevideo", "https://redirector.googlevideo.com/", 0, true),
        new(Discord, "Discord", "https://discord.com/", 64 * 1024, true),
        new(Discord, "Discord API", "https://discord.com/api/v9/experiments", 0, true),
        new(Discord, "Discord gateway", "https://gateway.discord.gg/", 0, true),
        new(Discord, "Discord CDN", "https://cdn.discordapp.com/", 0, true),
    };

    /// <summary>
    /// Основные проверки + сервисы, которые пользователь добавил из каталога,
    /// + несколько его собственных сайтов. По ним и подбирается стратегия.
    /// </summary>
    public static List<Target> BuildTargets(int maxCustom = 4)
    {
        var list = new List<Target>(ZapretTargets);
        foreach (var e in Catalog.Entries.Where(Catalog.IsAdded))
            list.Add(new(e.Name, e.Name, $"https://{e.Domains[0]}/"));

        var catalogDomains = Catalog.Entries.SelectMany(e => e.Domains).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var d in UserLists.Read(ListKind.Bypass).Where(d => !catalogDomains.Contains(d)).Take(maxCustom))
            list.Add(new(d, d, $"https://{d}/"));
        return list;
    }

    /// <summary>Сервисы из списка проверок, кроме основных — для плашек на главной.</summary>
    public static IEnumerable<string> ExtraServices(IEnumerable<Target> targets) =>
        targets.Where(t => !t.Core).Select(t => t.Service).Distinct();

    public static async Task<TestResult> RunAsync(IEnumerable<Target> targets, CancellationToken ct)
    {
        // Новый handler на каждый прогон: соединения от прошлой стратегии не должны переиспользоваться
        using var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(4),
            PooledConnectionLifetime = TimeSpan.Zero,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 3,
            AutomaticDecompression = DecompressionMethods.None,
            UseProxy = false,
        };
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0 Safari/537.36");

        var results = await Task.WhenAll(targets.Select(t => CheckAsync(http, t, ct)));
        return new TestResult(results);
    }

    static async Task<CheckResult> CheckAsync(HttpClient http, Target t, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(7));
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, t.Url);
            var sw = Stopwatch.StartNew();
            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            var ms = (int)sw.ElapsedMilliseconds;
            if (t.MinBytes <= 0) return new(t, true, $"{ms} мс", ms);

            await using var s = await resp.Content.ReadAsStreamAsync(cts.Token);
            var buf = new byte[16384];
            long total = 0;
            while (total < t.MinBytes)
            {
                var n = await s.ReadAsync(buf, cts.Token);
                if (n == 0) break;
                total += n;
            }
            return total >= t.MinBytes
                ? new(t, true, $"{total / 1024} КБ · {ms} мс", ms)
                : new(t, false, $"только {total / 1024} КБ");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new(t, false, "таймаут");
        }
        catch (HttpRequestException e)
        {
            return new(t, false, e.InnerException?.Message ?? e.Message);
        }
        catch (IOException e)
        {
            return new(t, false, e.Message);
        }
    }
}
