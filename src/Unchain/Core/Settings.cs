using System.Text.Json;
using System.Text.Json.Serialization;

namespace Unchain.Core;

public enum GameFilterMode { Off, Tcp, Udp, All }
public enum IpsetMode { Any, None, Loaded }

public sealed class Settings
{
    // Zapret
    public string? ZapretVersion { get; set; }
    public string? ZapretStrategy { get; set; }
    public bool ManualStrategy { get; set; }
    public int MaxVersionsToTry { get; set; } = 3;
    /// <summary>Очки последней выбранной стратегии — ниже этого сохранённая считается сломавшейся.</summary>
    public int LastScore { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public GameFilterMode GameFilter { get; set; } = GameFilterMode.Off;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public IpsetMode Ipset { get; set; } = IpsetMode.Loaded;

    // Telegram
    public int TelegramPort { get; set; } = 1080;
    public bool ShowTelegramProxyDialog { get; set; } = true;

    // Развлечения
    public int SnakeBest { get; set; }
    public int CatPets { get; set; }

    // Приложение
    public bool EnableOnLaunch { get; set; }
    public bool MinimizeToTray { get; set; } = true;
    public bool CheckZapretUpdates { get; set; } = true;

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static Settings Load()
    {
        try
        {
            if (File.Exists(Paths.SettingsFile))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(Paths.SettingsFile)) ?? new();
        }
        catch (Exception e) { Log.Write("Настройки повреждены, сбрасываю: " + e.Message); }
        return new();
    }

    public void Save()
    {
        try { File.WriteAllText(Paths.SettingsFile, JsonSerializer.Serialize(this, Json)); }
        catch (Exception e) { Log.Write("Не удалось сохранить настройки: " + e.Message); }
    }
}
