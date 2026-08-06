# AutoPublish — 通用 Windows 界面自动化

> 流程录制 + 数据驱动回放:把"在某个 Windows 程序里重复做的操作"录成流程,
> 再用数据表批量驱动,一套流程跑 N 行数据。

基于真实生产工具(主题模板批量发布,数千主题连续发布)提炼的通用引擎,
**不绑定任何特定程序**——目标程序换成任意 Windows 应用都能用。

## 组成

| 项目 | 说明 |
|---|---|
| **AutoPublishCore** | 核心库(UIA 驱动):数据驱动回放引擎 + 流程录制组件 |
| **AutoPublishCore.Cli** | 命令行工具 `autopublish`:流程 + 数据表批量回放 |

## 核心概念

- **流程 = 数据**:`workflow.json` 描述操作序列(步骤:点击 / 输入 / 勾选 / 展开 / 等待 / 结果检测),
  不含具体数据;
- **数据 = 行**:每行是键值对,执行时把 `@key` 替换为行值(如 `@theme` → 当前主题),
  实现"一套流程批量跑 N 行";
- **事件驱动**:等待用轮询(元素出现 / 界面变化)代替固定 sleep,响应快、不易误操作;
- **录制**:全局钩子捕获鼠标/键盘,把点击位置的 UIA 元素解析成步骤,生成流程骨架。

## 快速开始

```bash
# 构建
dotnet build AutoPublishCore.Cli

# 回放:流程 + 数据表(JSON / CSV / xlsx)
dotnet run --project AutoPublishCore.Cli -- --workflow workflow.json --data data.csv
```

`workflow.json`(流程):

```json
{
  "name": "示例流程",
  "windowTitle": "目标程序窗口标题",
  "steps": [
    { "kind": "SetValue", "locator": "id=textBox1", "value": "@theme", "pressEnter": true },
    { "kind": "Click",    "locator": "id=btn_ok" },
    { "kind": "DetectResult" }
  ]
}
```

`data.csv`(数据,一行一次回放):

```csv
theme,files
287,assets\css|templates\index.liquid
288,
```

## 步骤类型

- 通用动作:`Connect` / `Click` / `SetValue` / `Toggle` / `Expand` / `Wait` / `DetectResult` / `Log`
- 定位器:`id=控件AutomationId` 或 `name=控件Name`
- 失败策略:`stop`(默认) / `continue` / `retry`
- 领域参考实现:`Search` / `SelectTheme` / `CheckFiles` / `CheckUpdateAll` / `ClickPublish`(主题模板发布示例)

## 文档

详细说明见 [AutoPublishCore/README.md](AutoPublishCore/README.md)。

## License

[MIT](LICENSE)
