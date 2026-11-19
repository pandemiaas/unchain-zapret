using System.Net.Http.Headers;
using System.Text.Json;

namespace Unchain.Core;

public sealed record ReleaseAsset(string Name, string Url);

public sealed record ReleaseInfo(string Tag, bool Prerelease, string? ZipballUrl, IReadOnlyList<ReleaseAsset> Assets)
{
    /// <summary>Локальная копия без сведений с GitHub (когда GitHub недоступен).</summary>
    public static ReleaseInfo Local(string tag) => new(tag, false, null, Array.Empty<ReleaseAsset>());
}

public static class GitHub
{
    public static readonly HttpClient Http = Create();

    static HttpClient Create()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("Unchain/0.1 (+https://github.com)");
        return h;
    }

    public static async Task<List<ReleaseInfo>> GetReleasesAsync(string repo, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{repo}/releases?per_page=15");
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var resp = await Http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var list = new List<ReleaseInfo>();
        foreach (var r in doc.RootElement.EnumerateArray())
        {
            if (r.TryGetProperty("draft", out var d) && d.GetBoolean()) continue;

            var assets = new List<ReleaseAsset>();
            if (r.TryGetProperty("assets", out var arr))
                foreach (var a in arr.EnumerateArray())
                    assets.Add(new ReleaseAsset(
                        a.GetProperty("name").GetString() ?? "",
                        a.GetProperty("browser_download_url").GetString() ?? ""));

            list.Add(new ReleaseInfo(
                r.GetProperty("tag_name").GetString() ?? "unknown",
                r.TryGetProperty("prerelease", out var p) && p.GetBoolean(),
                r.TryGetProperty("zipball_url", out var z) ? z.GetString() : null,
                assets));
        }
        return list;
    }

    public static async Task DownloadAsync(string url, string file, IProgress<string>? progress, string label, CancellationToken ct)
    {
        using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength;

        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        await using var dst = File.Create(file);
        var buf = new byte[81920];
        long done = 0; int n; var lastReport = DateTime.MinValue;
        while ((n = await src.ReadAsync(buf, ct)) > 0)
        {
            await dst.WriteAsync(buf.AsMemory(0, n), ct);
            done += n;
            if (progress != null && (DateTime.Now - lastReport).TotalMilliseconds > 200)
            {
                lastReport = DateTime.Now;
                progress.Report(total is > 0
                    ? $"{label}: {done * 100 / total.Value}%"
                    : $"{label}: {done / 1024} КБ");
            }
        }
    }
}
