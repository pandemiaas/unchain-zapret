namespace Unchain.Core;

public static class Log
{
    public static event Action<string>? Line;
    static readonly object Sync = new();

    public static void Write(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        lock (Sync)
        {
            try { File.AppendAllText(Paths.LogFile, line + Environment.NewLine); } catch { }
        }
        Line?.Invoke(line);
    }

    public static void TrimFile(long maxBytes = 1_000_000)
    {
        try
        {
            var fi = new FileInfo(Paths.LogFile);
            if (fi.Exists && fi.Length > maxBytes) fi.Delete();
        }
        catch { }
    }
}
