# AutoPublishCore — 通用 Windows 界面自动化核心库

从 AutoPublishNet（主题模板批量发布工具）抽出的**通用引擎**：流程录制 + 数据驱动回放，
可驱动任意 Windows 程序（UIA）。

> 命名空间暂保持 `AutoPublishNet.*`（兼容既有代码），后续迭代可整体重命名为 `AutoPublishCore.*`。
> 领域步骤（`Search`/`SelectTheme`/`CheckFiles`/`ClickPublish` 等）以「主题模板发布参考实现」形式保留，
> 通用部分（`Click`/`SetValue`/`Toggle`/`Expand`/`Wait`/`DetectResult`）可直接复用。

## 结构

```
AutoPublishCore/
├── Models/
│   ├── WorkflowModels.cs   # StepKind / WorkflowStep / WorkflowDefinition / StepContext（Vars 数据驱动）
│   ├── PublishModels.cs    # ThemeConfig（运行配置）/ PublishOutcome / ThemeResult
│   └── ConfigStore.cs      # workflow.json / settings.json 加载
└── Core/
    ├── UiaTarget.cs        # UIA 目标程序封装：连接/定位(id=/name=)/Click/SetValue/Toggle/Expand/回车/快照检测
    ├── StepExecutor.cs     # 步骤执行器（WorkflowStep → UiaTarget 原语）
    └── WorkflowRunner.cs   # 通用数据驱动回放引擎（每行数据 → @key 注入 → 执行流程）
```

## 核心概念

- **流程 = 数据**：`WorkflowDefinition`（workflow.json）描述"操作序列"，不含具体数据；
- **数据 = 行**：每行是 `Dictionary<string, string>`，执行时把 `@key` 替换为行值
  （如 `@theme` → 当前主题、`@files` → 文件列表），实现"一套流程批量跑 N 行"；
- **步骤可通用可扩展**：通用动作开箱即用；领域步骤在 `StepExecutor` 中作为参考实现。

## 命令行工具（AutoPublishCore.Cli）

```bash
# 构建
dotnet build AutoPublishCore.Cli

# 运行：流程 + 数据表
dotnet run --project AutoPublishCore.Cli -- --workflow workflow.json --data data.json [--config settings.json]
```

数据文件支持 **JSON 对象数组**、**CSV**（首行为表头，支持双引号转义）、**xlsx**（第一个工作表，首行为表头，零依赖轻量解析）：

```json
[
  { "theme": "287", "files": "assets\\css|templates\\index.liquid" },
  { "theme": "288" }
]
```

```csv
theme,files
287,assets\css|templates\index.liquid
288,
```

> xlsx 解析为内置轻量实现（sharedStrings + 第一个工作表），仅支持基础单元格类型；复杂格式（公式、合并单元格、多工作表）建议先另存为 CSV。

## 作为本地 NuGet 包使用

```bash
# 1. 打包
dotnet pack AutoPublishCore -c Release -o packages

# 2. 添加本地源（一次）
dotnet nuget add source D:\dpWork\packages -n LocalPackages

# 3. 任意项目引用
dotnet add reference AutoPublishCore  # 或 dotnet add package AutoPublishCore
```

## 作为库使用

```csharp
var wf = ConfigStore.LoadWorkflow();                       // 或反序列化 workflow.json
var rows = new List<IReadOnlyDictionary<string, string>>
{
    new Dictionary<string, string> { ["theme"] = "287" },
};
var runner = new WorkflowRunner(wf);
runner.Log += Console.WriteLine;
runner.Finished += (results, ok, fail, unknown, totalSec) => { /* 汇总 */ };
await runner.RunAsync(rows, new ThemeConfig(), CancellationToken.None);
```
