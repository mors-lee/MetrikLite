// ============================================================================
// ConfigStore.cs —— 用户配置持久化到 %APPDATA%\MetrikLite\config.json
// ============================================================================

using System.IO;
using System.Text.Json;

namespace MetrikLite;

public sealed class TrayConfig
{
    /// <summary>刷新间隔，单位为秒；程序运行时最低按 10 秒执行。</summary>
    public int RefreshSeconds { get; set; } = 30;

    /// <summary>是否使用白字和深色描边，适合深色任务栏。</summary>
    public bool LightGlyphs { get; set; }

    /// <summary>是否注册当前用户级开机自启。</summary>
    public bool AutoStart { get; set; }

    /// <summary>允许显示的 Agent；空列表表示显示全部。</summary>
    public List<string> VisibleAgents { get; set; } = new() { "codex" };

    public bool IsAgentVisible(string adapterId)
        => VisibleAgents.Count == 0 || VisibleAgents.Contains(adapterId);

    public void ToggleAgent(string adapterId, IReadOnlyList<string> allAdapterIds)
    {
        if (VisibleAgents.Count == 0)
        {
            VisibleAgents = allAdapterIds.Where(id => id != adapterId).ToList();
            return;
        }

        VisibleAgents = VisibleAgents.Contains(adapterId)
            ? VisibleAgents.Where(id => id != adapterId).ToList()
            : VisibleAgents.Append(adapterId).ToList();
    }
}

public static class ConfigStore
{
    private static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MetrikLite");
    private static readonly string ConfigPath = Path.Combine(Dir, "config.json");
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static TrayConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var cfg = JsonSerializer.Deserialize<TrayConfig>(File.ReadAllText(ConfigPath), Options);
                if (cfg != null)
                {
                    return cfg;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("failed to load config, using defaults", ex);
        }

        return new TrayConfig();
    }

    public static void Save(TrayConfig config)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(config, Options));
        }
        catch (Exception ex)
        {
            Log.Error("failed to save config", ex);
        }
    }
}
