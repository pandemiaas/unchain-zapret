namespace Unchain.Core;

public static class Paths
{
    // C:\ProgramData\Unchain — путь без кириллицы и пробелов из имени пользователя (winws так спокойнее)
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Unchain");

    public static string Zapret => Path.Combine(Root, "zapret");
    /// <summary>Пользовательские списки — общие для всех версий zapret.</summary>
    public static string UserLists => Path.Combine(Root, "lists");
    public static string SettingsFile => Path.Combine(Root, "settings.json");
    public static string LogFile => Path.Combine(Root, "log.txt");

    public static string ExePath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Unchain.exe");

    static Paths()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Path.Combine(Root, "zapret"));
        Directory.CreateDirectory(Path.Combine(Root, "lists"));
    }
}
