using System.Diagnostics;
using AutoPublishCore.Models;

namespace AutoPublishCore.Core;

/// <summary>单行执行结果</summary>
public sealed class RowResult
{
    public string RowKey { get; }
    public PublishOutcome Outcome { get; }
    public double ElapsedSeconds { get; }
    public string Detail { get; }

    public RowResult(string rowKey, PublishOutcome outcome, double elapsedSeconds, string detail = "")
    {
        RowKey = rowKey;
        Outcome = outcome;
        ElapsedSeconds = elapsedSeconds;
        Detail = detail;
    }

    public string StatusText => Outcome switch
    {
        PublishOutcome.Success => "成功",
        PublishOutcome.Failed => "失败",
        PublishOutcome.Unknown => "未知",
        PublishOutcome.Skipped => "跳过",
        _ => "失败",
    };
}

/// <summary>
/// 通用数据驱动回放引擎（AutoPublishNet.PublishEngine 的泛化版）：
/// 对数据表的每一行，把行字段注入流程占位符（@key）并执行 WorkflowDefinition 的全部步骤。
/// 与领域版 PublishEngine 的差异：不依赖主题概念，数据行可以是任意字段。
/// </summary>
public sealed class WorkflowRunner
{
    private readonly WorkflowDefinition _workflow;

    public WorkflowRunner() : this(ConfigStore.LoadWorkflow()) { }

    public WorkflowRunner(WorkflowDefinition workflow) => _workflow = workflow;

    public event Action<string>? Log;
    public event Action<int, int, double, double>? Progress; // cur, total, elapsedSec, etaSec
    public event Action<IReadOnlyList<RowResult>, int, int, int, double>? Finished;
    // results, ok, fail, unknown, totalSec

    public async Task RunAsync(IReadOnlyList<IReadOnlyDictionary<string, string>> rows,
        ThemeConfig cfg, CancellationToken ct)
    {
        await Task.Run(() => Run(rows, cfg, ct), ct);
    }

    private void Run(IReadOnlyList<IReadOnlyDictionary<string, string>> rows,
        ThemeConfig cfg, CancellationToken ct)
    {
        var results = new List<RowResult>();
        int ok = 0, fail = 0, unknown = 0;
        var t0 = Stopwatch.StartNew();

        if (rows.Count == 0)
        {
            Finished?.Invoke(results, 0, 0, 0, 0);
            return;
        }

        var target = new UiaTarget(_workflow.WindowTitle);
        if (!target.Connect())
        {
            Log?.Invoke("连接目标程序失败，请确认窗口已打开");
            foreach (var r in rows)
                results.Add(new RowResult(KeyOf(r), PublishOutcome.ConnectFailed, 0, "连接失败"));
            fail = rows.Count;
            Finished?.Invoke(results, 0, fail, 0, t0.Elapsed.TotalSeconds);
            return;
        }
        Log?.Invoke($"已连接目标程序（{_workflow.WindowTitle}）");

        var executor = new StepExecutor();

        for (int i = 0; i < rows.Count; i++)
        {
            if (ct.IsCancellationRequested)
            {
                Log?.Invoke("用户停止");
                for (int k = i; k < rows.Count; k++)
                    results.Add(new RowResult(KeyOf(rows[k]), PublishOutcome.Skipped, 0));
                break;
            }

            var vars = rows[i];
            var key = KeyOf(vars);
            var elapsedTotal = t0.Elapsed.TotalSeconds;
            var eta = i > 0
                ? elapsedTotal / i * (rows.Count - i)
                : -1;
            Progress?.Invoke(i + 1, rows.Count, elapsedTotal, eta);
            Log?.Invoke($"[{i + 1}/{rows.Count}] 行 {key}");

            // 窗口失效则重连
            if (!target.IsConnected)
            {
                Log?.Invoke("目标窗口已失效，尝试重连...");
                if (!target.Connect())
                {
                    Log?.Invoke("  重连失败，终止");
                    for (int k = i; k < rows.Count; k++)
                        results.Add(new RowResult(KeyOf(rows[k]), PublishOutcome.ConnectFailed, 0, "连接失败"));
                    fail += rows.Count - i;
                    break;
                }
            }

            var tRow = Stopwatch.StartNew();
            var outcome = PublishOutcome.Unknown;
            string detail = "";

            try
            {
                var ctx = new StepContext { Vars = vars, Config = cfg };
                PublishOutcome? detected = null;

                foreach (var step in _workflow.Steps)
                {
                    if (ct.IsCancellationRequested)
                    {
                        outcome = PublishOutcome.Skipped;
                        break;
                    }

                    // 执行（支持 OnFail="retry" 的重试）
                    StepResult r;
                    int attempt = 0;
                    while (true)
                    {
                        r = executor.Execute(step, target, ctx, msg => Log?.Invoke(msg), ct);
                        if (r.Success || step.OnFail != "retry" || attempt >= step.MaxRetries) break;
                        attempt++;
                        Log?.Invoke($"  步骤 {step.Kind} 失败，重试 {attempt}/{step.MaxRetries}...");
                        Thread.Sleep(500);
                    }

                    if (step.Kind == StepKind.DetectResult && r.Outcome.HasValue)
                        detected = r.Outcome.Value;

                    if (r.Success) continue;

                    detail = r.Detail;
                    if (step.OnFail == "continue")
                    {
                        Log?.Invoke($"  ⚠ {detail}（按配置继续后续流程）");
                        continue;
                    }
                    outcome = PublishOutcome.Failed;
                    Log?.Invoke($"  ✗ {detail}");
                    break;
                }

                if (detected.HasValue && outcome != PublishOutcome.Failed && outcome != PublishOutcome.Skipped)
                    outcome = detected.Value;
            }
            catch (OperationCanceledException)
            {
                outcome = PublishOutcome.Skipped;
            }
            catch (Exception ex)
            {
                detail = ex.Message;
                outcome = PublishOutcome.Failed;
                Log?.Invoke($"  ✗ 异常: {ex.Message}");
            }

            tRow.Stop();
            var secs = tRow.Elapsed.TotalSeconds;

            switch (outcome)
            {
                case PublishOutcome.Success:
                    ok++;
                    Log?.Invoke($"  ✔ 成功，耗时 {secs:F1} 秒");
                    break;
                case PublishOutcome.Failed:
                    fail++;
                    Log?.Invoke($"  ✗ 失败，耗时 {secs:F1} 秒（{detail}）");
                    break;
                case PublishOutcome.Unknown:
                    unknown++;
                    Log?.Invoke(cfg.ContinueOnUnknown
                        ? $"  ? 未检测到完成信号，耗时 {secs:F1} 秒"
                        : $"  ? 未检测到完成信号，按配置停止后续，耗时 {secs:F1} 秒");
                    if (!cfg.ContinueOnUnknown)
                    {
                        for (int k = i + 1; k < rows.Count; k++)
                            results.Add(new RowResult(KeyOf(rows[k]), PublishOutcome.Skipped, 0));
                        break;
                    }
                    break;
            }
            results.Add(new RowResult(key, outcome, secs, detail));

            if (outcome == PublishOutcome.Unknown && !cfg.ContinueOnUnknown)
                break;
        }

        Finished?.Invoke(results, ok, fail, unknown, t0.Elapsed.TotalSeconds);
    }

    private static string KeyOf(IReadOnlyDictionary<string, string> vars)
        => vars.Count > 0 ? vars.Values.First() : "行";
}
