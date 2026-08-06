using System.IO;
using System.Text.Json;

namespace AutoPublishCore.Models;

/// <summary>主题 ID 存储与数据文件定位（对等 Python 版 load_ids 的健壮查找）</summary>
public static class ConfigStore
{
    public static string DataDir { get; } = AppContext.BaseDirectory;

    public static string ThemeIdsPath => Path.Combine(DataDir, "theme_ids.txt");
    public static string SettingsPath => Path.Combine(DataDir, "settings.json");
    public static string WorkflowPath => Path.Combine(DataDir, "workflow.json");

    public static List<string> LoadThemeIds()
    {
        var list = new List<string>();
        try
        {
            if (!File.Exists(ThemeIdsPath))
            {
                File.WriteAllText(ThemeIdsPath,
                    "# 每行一个主题ID\n# 2\n# 22\n", System.Text.Encoding.UTF8);
                return list;
            }
            foreach (var line in File.ReadAllLines(ThemeIdsPath, System.Text.Encoding.UTF8))
            {
                var t = line.Trim();
                if (t.Length > 0 && !t.StartsWith('#'))
                    list.Add(t);
            }
        }
        catch { }
        return list;
    }

    /// <summary>加载 workflow.json；不存在或损坏时回退内置默认流程（与旧版行为一致）</summary>
    public static WorkflowDefinition LoadWorkflow()
    {
        try
        {
            if (File.Exists(WorkflowPath))
            {
                var json = File.ReadAllText(WorkflowPath, System.Text.Encoding.UTF8);
                var wf = JsonSerializer.Deserialize<WorkflowDefinition>(json,
                    new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true,
                        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
                    });
                if (wf != null && wf.Steps.Count > 0) return wf;
            }
        }
        catch { /* 配置损坏时回退默认流程 */ }
        return WorkflowDefinition.CreateDefault();
    }

    /// <summary>按时间戳生成结果文件名，如 publish_result_20260803_120000.txt</summary>
    public static string NewResultFileName()
    {
        return $"publish_result_{DateTime.Now:yyyyMMdd_HHmmss}.txt";
    }

    /// <summary>结果保存目录（程序目录下的「结果记录」文件夹）</summary>
    public static string ResultsDir => Path.Combine(DataDir, "结果记录");

    /// <summary>确保结果目录存在；创建失败时回退到程序目录</summary>
    public static string EnsureResultsDir()
    {
        try
        {
            Directory.CreateDirectory(ResultsDir);
            return ResultsDir;
        }
        catch
        {
            return DataDir;
        }
    }
}
