# AutoPublish 使用教程

一套流程 + 一张数据表 = 批量自动化任意 Windows 程序。

## 一、三种使用方式总览

| 方式 | 适用场景 | 上手难度 |
|---|---|---|
| **CLI 回放**(`autopublish`) | 脚本化、定时任务、批量处理 | ⭐ 最简单 |
| **AutoPublishNet 录制器** | 不想手写 workflow,操作一遍自动生成 | ⭐⭐ 推荐组合 |
| **库嵌入**(`WorkflowRunner`) | 集成到自己的 WPF/服务程序 | ⭐⭐⭐ |

## 二、推荐工作流:录制 → 微调 → 数据 → 回放

### 第 1 步:录制流程(用 AutoPublishNet 的「流程录制」)

打开目标程序 → 打开 AutoPublishNet → 点「流程录制」→ 输入目标窗口标题 → 开始录制 → 操作一遍 → 停止 → 保存 `workflow.json`。

录制规则:
- 点**按钮/列表项** → `Click`
- 点**复选框/树节点** → `Toggle`
- **Ctrl+点击树节点** → `Expand`(展开)
- 输入框输入后按 **Enter** → `SetValue`(回车提交,触发搜索/确认)
- 发布/确认弹窗也会录制(同进程窗口)
- 手动「插入结果检测」「插入等待」

### 第 2 步:微调流程(「流程编辑」或手写)

把**每行变化的输入**改成占位符(数据列名),比如主题搜索:
`"value": "289"` → `"value": "@theme"`

### 第 3 步:准备数据表(JSON / CSV / xlsx)

列名 = 占位符名(不含 @)。一行 = 一次完整回放。

```csv
theme,files
287,assets\css|templates\index.liquid
288,
```

### 第 4 步:CLI 回放

```bash
# 构建(首次)
dotnet build AutoPublishCore.Cli

# 回放
dotnet run --project AutoPublishCore.Cli -- --workflow workflow.json --data data.csv
```

目标程序必须**已打开**,且窗口标题与 `windowTitle` 匹配(模糊匹配)。

## 三、workflow.json 参考

### 字段

| 字段 | 说明 |
|---|---|
| `name` | 流程名(仅展示) |
| `windowTitle` | 目标窗口标题(精确/模糊匹配),引擎连接依据 |
| `steps` | 步骤数组,自上而下依次执行 |

### 步骤(kind)

| kind | 动作 | 需要的字段 |
|---|---|---|
| `Connect` | 连接目标窗口 | —(引擎自动) |
| `Click` | 点击控件 | `locator` |
| `SetValue` | 输入文本 | `locator` + `value` |
| `Toggle` | 勾选/切换复选框 | `locator` |
| `Expand` | 展开树节点 | `locator` |
| `Wait` | 固定等待 | `timeoutSec`(秒) |
| `DetectResult` | 检测界面变化判定完成 | `timeoutSec`(超时秒) |
| `Log` | 输出日志 | `value` |
| `Search`/`SelectTheme`/`CheckFiles` 等 | 领域参考实现(主题模板发布示例) | — |

### 定位器(locator)

- `id=控件AutomationId`(优先,稳定)
- `name=控件Name`(次选,可能不唯一)

### 占位符

步骤的 `value` / `locator` 中出现 `@列名` 会被替换为**当前行**该列的值。示例:

| 占位符 | 数据列 | 说明 |
|---|---|---|
| `@theme` | `theme` | 每轮注入当前主题 |
| `@orderno` | `orderno` | 任意业务字段 |
| `@files` | `files` | 文件列表(CheckFiles 专用,`\|` 分隔) |

### 通用步骤字段

| 字段 | 默认 | 说明 |
|---|---|---|
| `required` | `true` | false = 失败仅警告不中断 |
| `onFail` | `stop` | `stop` 失败即中断本行 / `continue` 警告后继续 / `retry` 重试 `maxRetries` 次 |
| `maxRetries` | `2` | `onFail=retry` 时的重试次数 |
| `timeoutSec` | 0 | 0 = 使用配置默认值 |
| `pressEnter` | `false` | `SetValue` 专用:输入后按回车(触发搜索/确认) |

## 四、作为库嵌入

```csharp
using AutoPublishCore.Core;
using AutoPublishCore.Models;

var wf = ConfigStore.LoadWorkflow();   // 或反序列化 workflow.json
var rows = new List<IReadOnlyDictionary<string, string>>
{
    new Dictionary<string, string> { ["theme"] = "287" },
};

var runner = new WorkflowRunner(wf);
runner.Log += Console.WriteLine;
runner.Progress += (cur, total, el, eta) => Console.WriteLine($"{cur}/{total}");
runner.Finished += (results, ok, fail, unknown, secs) => { /* 汇总 */ };

await runner.RunAsync(rows, new ThemeConfig(), CancellationToken.None);
```

## 五、常见问题

| 问题 | 处理 |
|---|---|
| 提示"连接目标程序失败" | 目标程序没开 / 窗口标题不匹配(`windowTitle` 改模糊词) |
| 找不到控件 | 控件无 AutomationId/Name,或不在当前窗口;改用 `name=` 或换定位 |
| 钩子安装失败(录制器) | 以管理员身份运行 AutoPublishNet |
| 多主题只发布同一个 | `SetValue` 的值没变量化——改成 `@theme` 并把数据列放进数据表 |
| 不同行文件结构不同 | 树勾选合并成 `CheckFiles`(读树勾选,缺失宽容) |

## 六、示例文件

仓库 `examples/` 目录:
- `workflow.example.json` — 订单查询导出流程模板
- `data.example.csv` — 3 行数据示例

把 `windowTitle` 改成你的目标程序标题、`locator` 改成实际控件 id 即可套用。
