using AutoPublishCore.Models;

namespace AutoPublishCore.Core;

/// <summary>单步执行结果</summary>
public sealed record StepResult(bool Success, string Detail = "", PublishOutcome? Outcome = null);

/// <summary>
/// 通用步骤执行器：把 WorkflowStep 翻译成对 UiaTarget 现有原语的调用。
/// UiaTarget 本身零改动——这里只负责「流程数据 → 原子操作」的映射。
/// </summary>
public sealed class StepExecutor
{
    private const int ConnectTimeoutMs = 8000;

    public StepResult Execute(WorkflowStep step, UiaTarget target, StepContext ctx,
        Action<string> log, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        return step.Kind switch
        {
            StepKind.Connect =>
                Exec(target.Connect(step.TimeoutSec > 0 ? step.TimeoutSec * 1000 : ConnectTimeoutMs),
                    "连接目标程序失败"),

            StepKind.Search =>
                Exec(target.Search(ctx.Resolve(step.Value), TimeoutMs(step, ctx.Config.SearchTimeoutSec)),
                    "搜索无匹配结果"),

            StepKind.SelectTheme =>
                Exec(target.SelectTheme(ctx.Resolve(step.Value), TimeoutMs(step, ctx.Config.TreeTimeoutSec), out _),
                    "选择主题失败"),

            StepKind.CheckFiles =>
                CheckFilesStep(step, target, ctx, log, ct),

            StepKind.CheckUpdateAll =>
                Exec(target.CheckUpdateAll(), "勾选「整个模板更新」失败"),

            StepKind.ClickPublish =>
                Exec(target.ClickPublish(), "点击发布失败"),

            StepKind.Click =>
                Exec(target.InvokeByLocator(ctx.Resolve(step.Locator), TimeoutMs(step, 5)),
                    "点击控件失败（定位器: " + step.Locator + "）"),

            StepKind.SetValue =>
                SetValueStep(step, target, ctx, ct),

            StepKind.Toggle =>
                Exec(target.ToggleByLocator(ctx.Resolve(step.Locator), TimeoutMs(step, 5)),
                    "切换控件失败（定位器: " + step.Locator + "）"),

            StepKind.Expand =>
                Exec(target.ExpandByLocator(ctx.Resolve(step.Locator), TimeoutMs(step, 5)),
                    "展开节点失败（定位器: " + step.Locator + "）"),

            StepKind.Wait =>
                WaitStep(step, ct),

            StepKind.DetectResult =>
                new StepResult(true, Outcome: target.DetectResult(ctx.Config, ct)),

            StepKind.Log =>
                LogStep(step, ctx, log),

            _ => new StepResult(false, $"未知步骤: {step.Kind}"),
        };
    }

    // ---------- 私有辅助 ----------

    private static StepResult Exec(bool ok, string failDetail)
        => ok ? new StepResult(true) : new StepResult(false, failDetail);

    private static int TimeoutMs(WorkflowStep step, int defaultSec)
        => (step.TimeoutSec > 0 ? step.TimeoutSec : defaultSec) * 1000;

    /// <summary>勾选文件：整模板更新时走 CheckUpdateAll；否则按文件列表勾选并记录缺失项。
    /// 文件列表优先取步骤 Value（录制合并生成，| 分隔），为空时用上下文（UI 勾选）的文件列表。</summary>
    private static StepResult CheckFilesStep(WorkflowStep step, UiaTarget target, StepContext ctx,
        Action<string> log, CancellationToken ct)
    {
        if (ctx.UpdateAll)
        {
            var ok = target.CheckUpdateAll();
            if (ok) log("  已勾选「整个模板更新」");
            return Exec(ok, "勾选「整个模板更新」失败");
        }

        // 文件列表：先解析步骤 Value（支持 @files 占位），为空时用上下文文件列表
        var raw = ctx.Resolve(step.Value);
        var files = !string.IsNullOrWhiteSpace(raw)
            ? raw.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList()
            : ctx.Files.ToList();

        var (checkedCount, missing) = target.CheckFiles(files, ct);
        foreach (var m in missing)
            log($"    未找到或勾选失败: {m}");
        log($"  勾选文件 {checkedCount}/{files.Count}");
        return checkedCount > 0
            ? new StepResult(true)
            : new StepResult(false, "没有文件被成功勾选");
    }

    /// <summary>SetValue 步骤：写入文本；若 PressEnter 则再向控件发回车（触发搜索/确认）</summary>
    private static StepResult SetValueStep(WorkflowStep step, UiaTarget target, StepContext ctx,
        CancellationToken ct)
    {
        var locator = ctx.Resolve(step.Locator);
        if (!target.SetValueByLocator(locator, ctx.Resolve(step.Value), TimeoutMs(step, 5)))
            return new StepResult(false, "输入文本失败（定位器: " + step.Locator + "）");

        if (step.PressEnter && !target.PressEnterByLocator(locator, 2000))
            return new StepResult(false, "提交回车失败（定位器: " + step.Locator + "）");

        // 回车触发搜索/刷新后，给目标程序短暂响应时间（列表/树重建），避免后续步骤读旧界面
        if (step.PressEnter)
            Thread.Sleep(1500);

        return new StepResult(true);
    }

    private static StepResult LogStep(WorkflowStep step, StepContext ctx, Action<string> log)
    {
        log(ctx.Resolve(step.Value));
        return new StepResult(true);
    }

    /// <summary>固定等待：timeoutSec 为等待秒数（0 则默认 1 秒），支持取消</summary>
    private static StepResult WaitStep(WorkflowStep step, CancellationToken ct)
    {
        var secs = step.TimeoutSec > 0 ? step.TimeoutSec : 1;
        try
        {
            Task.Delay(secs * 1000, ct).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        return new StepResult(true);
    }
}
