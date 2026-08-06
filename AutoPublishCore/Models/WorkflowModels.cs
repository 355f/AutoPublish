using System.Text.Json.Serialization;

namespace AutoPublishCore.Models;

/// <summary>步骤类型——覆盖现有发布流程的全部动作（可扩展）</summary>
public enum StepKind
{
    Connect,        // 连接目标窗口
    Search,         // 搜索主题（value=@theme）
    SelectTheme,    // 选择主题（value=@theme）
    CheckFiles,     // 勾选文件列表（UpdateAll 时自动走 CheckUpdateAll）
    CheckUpdateAll, // 显式勾选「整个模板更新」
    ClickPublish,   // 点击发布
    DetectResult,   // 事件驱动检测发布结果（界面快照变化）
    Log,            // 输出日志（value 支持变量占位）

    // ---------- 通用动作（录制器生成 / 适配任意目标程序） ----------
    Click,          // 点击定位器指向的控件（locator 必填）
    SetValue,       // 向定位器指向的输入框写入文本（locator 必填，value 为文本）
    Toggle,         // 切换定位器指向的复选框/单选钮（locator 必填）
    Expand,         // 展开定位器指向的树节点（ExpandCollapsePattern，locator 必填）
    Wait,           // 固定等待 timeoutSec 秒（通用流程兜底，尽量少用）
}

/// <summary>
/// 单个流程步骤（可 JSON 序列化）。
/// 定位器与变量占位是「多主题/换目标程序」的关键：
/// 主题与文件都是每轮注入的数据，流程定义本身无需随主题变更。
/// </summary>
public sealed class WorkflowStep
{
    public StepKind Kind { get; set; }

    /// <summary>定位器（预留）："id=textBox1" / "name=发布"；留空则使用 UiaTarget 默认控件</summary>
    public string? Locator { get; set; }

    /// <summary>步骤参数，支持占位符：@theme（当前主题）、@updateAll（true/false）</summary>
    public string? Value { get; set; }

    /// <summary>步骤超时（秒）；0 表示使用配置默认值</summary>
    public int TimeoutSec { get; set; }

    /// <summary>是否必须成功；false 时失败仅记日志不中断</summary>
    public bool Required { get; set; } = true;

    /// <summary>失败策略："stop"（默认，记失败并中断本主题）/ "continue"（警告后继续下一流程）/ "retry"（重试 MaxRetries 次后仍失败则记失败）</summary>
    public string OnFail { get; set; } = "stop";

    /// <summary>OnFail="retry" 时的最大重试次数</summary>
    public int MaxRetries { get; set; } = 2;

    /// <summary>SetValue 专用：设置值后是否向该控件发送回车（触发目标程序搜索/确认）</summary>
    public bool PressEnter { get; set; }
}

/// <summary>一套完整流程定义（对应 workflow.json）</summary>
public sealed class WorkflowDefinition
{
    public string Name { get; set; } = "主题模板发布";
    public string WindowTitle { get; set; } = "主题模板发布";
    public List<WorkflowStep> Steps { get; set; } = new();

    /// <summary>
    /// 内置默认流程：与旧版 PublishEngine 硬编码行为完全一致。
    /// 没有 workflow.json（或读取失败）时使用，保证现有功能零变化。
    /// </summary>
    public static WorkflowDefinition CreateDefault() => new()
    {
        Name = "主题模板发布",
        WindowTitle = "主题模板发布",
        Steps =
        {
            new WorkflowStep { Kind = StepKind.Search,      Value = "@theme", TimeoutSec = 8  },
            new WorkflowStep { Kind = StepKind.SelectTheme, Value = "@theme", TimeoutSec = 10 },
            new WorkflowStep { Kind = StepKind.CheckFiles,  Value = "@files" },
            new WorkflowStep { Kind = StepKind.ClickPublish, Required = true },
            new WorkflowStep { Kind = StepKind.DetectResult, TimeoutSec = 60 },
        },
    };
}

/// <summary>
/// 单行执行上下文：承载每行变化的数据（通用数据驱动，Vars 字典）。
/// 领域便捷属性（ThemeId/Files/UpdateAll）由 Vars 派生，兼容既有领域步骤。
/// </summary>
public sealed class StepContext
{
    /// <summary>数据行变量（如 theme=287、files=a|b、updateAll=true），占位符 @key 在执行时替换为对应值</summary>
    public required IReadOnlyDictionary<string, string> Vars { get; init; }

    public required ThemeConfig Config { get; init; }

    // ---------- 领域便捷属性（由 Vars 派生，兼容既有代码） ----------

    public string ThemeId => Vars.TryGetValue("theme", out var v) ? v : "";

    public bool UpdateAll => Vars.TryGetValue("updateAll", out var v) && v == "true";

    public IReadOnlyList<string> Files
        => Vars.TryGetValue("files", out var f) && !string.IsNullOrWhiteSpace(f)
            ? f.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : Array.Empty<string>();

    /// <summary>解析 @ 占位符：把所有 @key（Vars 中的键）替换为对应值</summary>
    public string Resolve(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var sb = value;
        foreach (var kv in Vars)
            sb = sb.Replace("@" + kv.Key, kv.Value);
        return sb;
    }
}
