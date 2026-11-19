using System.Diagnostics;

namespace Unchain.Core;

public sealed class ZapretRunner : IDisposable
{
    Process? _proc;

    /// <summary>winws завершился сам (не по нашей команде). Аргумент — код выхода.</summary>
    public event Action<int>? Exited;

    public GameFilterMode GameFilter { get; set; }
    public IpsetMode Ipset { get; set; } = IpsetMode.Loaded;

    public bool IsRunning
    {
        get { try { return _proc is { HasExited: false }; } catch { return false; } }
    }

    public ZapretBuild? CurrentBuild { get; private set; }
    public string? CurrentStrategy { get; private set; }

    public void Start(ZapretBuild build, string strategyFile)
    {
        Stop();
        KillStray();
        ZapretStore.Prepare(build, Ipset);

        var args = BatParser.ExtractWinwsArgs(strategyFile, build, GameFilter)
                   ?? throw new InvalidOperationException($"В {Path.GetFileName(strategyFile)} нет запуска winws.exe");

        var psi = new ProcessStartInfo(Path.Combine(build.Bin, "winws.exe"), args)
        {
            WorkingDirectory = build.Bin,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        p.OutputDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) Log.Write("winws: " + e.Data); };
        p.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) Log.Write("winws: " + e.Data); };
        p.Exited += OnExited;

        if (!p.Start()) throw new InvalidOperationException("Не удалось запустить winws.exe");
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();

        _proc = p;
        CurrentBuild = build;
        CurrentStrategy = strategyFile;
    }

    /// <summary>Перезапуск с теми же версией и стратегией (после смены фильтров/списков).</summary>
    public bool Restart()
    {
        if (CurrentBuild is not { } b || CurrentStrategy is not { } s) return false;
        Start(b, s);
        return true;
    }

    void OnExited(object? sender, EventArgs e)
    {
        if (sender is not Process p || !ReferenceEquals(p, _proc)) return;
        int code;
        try { code = p.ExitCode; } catch { code = -1; }
        Log.Write($"winws завершился (код {code})");
        Exited?.Invoke(code);
    }

    public void Stop()
    {
        var p = _proc;
        _proc = null;
        if (p == null) return;

        p.Exited -= OnExited;
        try
        {
            if (!p.HasExited)
            {
                p.Kill(true);
                p.WaitForExit(3000);
            }
        }
        catch { }
        p.Dispose();
    }

    /// <summary>Убиваем winws, оставшиеся от прошлых запусков или от .bat.</summary>
    public static void KillStray()
    {
        foreach (var p in Process.GetProcessesByName("winws"))
        {
            try { p.Kill(true); p.WaitForExit(3000); } catch { }
            finally { p.Dispose(); }
        }
    }

    public void Dispose() => Stop();
}
