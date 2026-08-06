using System.IO;
using System.Text.Json;

namespace AutoPublishCore.Models;

/// <summary>单主题发布结果状态</summary>
public enum PublishOutcome
{
    Success,   // 检测到成功标志
    Failed,    // 检测到失败标志
    Unknown,   // 超时未检测到明确结果
    Skipped,   // 用户停止后未处理
    ConnectFailed, // 无法连接目标程序
}

/// <summary>单主题发布记录</summary>
public sealed class ThemeResult
{
    public string ThemeId { get; }
    public PublishOutcome Outcome { get; }
    public double ElapsedSeconds { get; }
    public string Detail { get; }

    public ThemeResult(string themeId, PublishOutcome outcome, double elapsedSeconds, string detail = "")
    {
        ThemeId = themeId;
        Outcome = outcome;
        ElapsedSeconds = elapsedSeconds;
        Detail = detail;
    }

    public string StatusText => Outcome switch
    {
        PublishOutcome.Success => "已发布",
        PublishOutcome.Failed => "失败",
        PublishOutcome.Unknown => "未知",
        PublishOutcome.Skipped => "跳过",
        _ => "失败",
    };
}

/// <summary>发布配置（来自 settings.json + UI 输入）</summary>
public sealed class ThemeConfig
{
    public string WindowTitle { get; set; } = "主题模板发布";
    public int SearchTimeoutSec { get; set; } = 8;
    public int TreeTimeoutSec { get; set; } = 10;
    public int DetectTimeoutSec { get; set; } = 60;
    public int ExpandTimeoutSec { get; set; } = 45;

    public List<string> SuccessKeywords { get; set; } = new() { "成功", "完成", "已发布" };
    public List<string> FailKeywords { get; set; } = new() { "失败", "错误", "异常", "未成功" };
    public List<string> BusyKeywords { get; set; } = new() { "发布中", "处理中", "请稍候", "正在" };

    /// <summary>检测结果未知(超时)时是否仍继续下一个主题</summary>
    public bool ContinueOnUnknown { get; set; } = true;

    // ---------- 每次发布时由界面生成 ----------
    public bool UpdateAll { get; set; }
    public List<string> Files { get; set; } = new();

    public static ThemeConfig Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path, System.Text.Encoding.UTF8);
                var cfg = JsonSerializer.Deserialize<ThemeConfig>(json);
                if (cfg != null) return cfg;
            }
        }
        catch { /* 配置损坏时回退默认值 */ }
        return new ThemeConfig();
    }

    public void Save(string path)
    {
        try
        {
            var json = JsonSerializer.Serialize(this,
                new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json, System.Text.Encoding.UTF8);
        }
        catch { /* 忽略写配置失败 */ }
    }
}
