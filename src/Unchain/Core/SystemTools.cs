using System.Diagnostics;
using System.Security.Principal;

namespace Unchain.Core;

public sealed record DiagItem(string Name, bool Ok, string Detail);

public static class SystemTools
{
    const string TaskName = "Unchain";

    /// <summary>
    /// Открыть ссылку/протокол от имени обычного пользователя (через explorer),
    /// чтобы Telegram не запустился с правами администратора.
    /// </summary>
    public static void OpenUnelevated(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { url }, UseShellExecute = false })?.Dispose();
        }
        catch
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
        }
    }

    // ───── Автозапуск через Планировщик (без UAC при входе в систему) ─────

    public static bool IsAutostartEnabled() => Run("schtasks.exe", out _, "/Query", "/TN", TaskName) == 0;

    public static bool SetAutostart(bool enable)
    {
        string o1;
        var code = enable
            ? Run("schtasks.exe", out o1, "/Create", "/F", "/TN", TaskName, "/SC", "ONLOGON", "/RL", "HIGHEST",
                "/TR", $"\"{Paths.ExePath}\" --autostart")
            : Run("schtasks.exe", out o1, "/Delete", "/F", "/TN", TaskName);
        if (code != 0) Log.Write($"Автозапуск: schtasks вернул {code}: {o1.Trim()}");
        return code == 0;
    }

    // ───── Диагностика (аналог пункта Diagnostics в service.bat) ─────

    public static async Task<List<DiagItem>> DiagnoseAsync(bool weRunWinws, CancellationToken ct)
    {
        var list = new List<DiagItem>();

        using (var id = WindowsIdentity.GetCurrent())
        {
            var admin = new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
            list.Add(new("Права администратора", admin, admin ? "Есть" : "Нет — winws не запустится"));
        }

        var gdpi = Process.GetProcessesByName("goodbyedpi");
        list.Add(new("GoodbyeDPI", gdpi.Length == 0, gdpi.Length == 0 ? "Не запущен" : "Запущен — закрой его, он конфликтует"));
        foreach (var p in gdpi) p.Dispose();

        var winws = Process.GetProcessesByName("winws");
        var foreign = weRunWinws ? Math.Max(0, winws.Length - 1) : winws.Length;
        list.Add(new("Посторонний winws.exe", foreign == 0, foreign == 0 ? "Нет" : $"Найдено: {foreign} — запущен .bat или служба"));
        foreach (var p in winws) p.Dispose();

        var svc = Run("sc.exe", out var svcOut, "query", "zapret") == 0;
        list.Add(new("Служба zapret (service.bat)", !svc, svc ? "Установлена — удали её через service.bat → Remove Services" : "Не установлена"));
        _ = svcOut;

        foreach (var name in new[] { "WinDivert", "WinDivert14" })
        {
            if (Run("sc.exe", out var o, "query", name) == 0 && o.Contains("RUNNING") && !weRunWinws)
                list.Add(new($"Драйвер {name}", true, "Загружен (это нормально после работы zapret)"));
        }

        bool github;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(6));
            using var r = await GitHub.Http.GetAsync("https://api.github.com/", cts.Token);
            github = true;
        }
        catch (Exception) when (!ct.IsCancellationRequested) { github = false; }
        list.Add(new("Доступ к GitHub", github, github ? "Есть" : "Нет — обновления и новые версии недоступны"));

        var dns = Environment.OSVersion.Version.Build >= 22000;
        list.Add(new("Secure DNS", true, dns
            ? "Проверь, что в браузере и Windows включён DNS over HTTPS"
            : "На Windows 10 включи DoH в браузере"));

        foreach (var d in list) Log.Write($"Диагностика · {d.Name}: {(d.Ok ? "✓" : "✗")} {d.Detail}");
        return list;
    }

    static int Run(string exe, out string output, params string[] args)
    {
        output = "";
        try
        {
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit(10000);
            return p.ExitCode;
        }
        catch (Exception e)
        {
            output = e.Message;
            return -1;
        }
    }
}
