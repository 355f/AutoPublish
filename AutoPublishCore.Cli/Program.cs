using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AutoPublishCore.Core;
using AutoPublishCore.Models;

namespace AutoPublishCore.Cli;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        if (args.Length == 0) { PrintHelp(); return 1; }

        string? workflowPath = null, dataPath = null, configPath = null;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--workflow" or "-w": workflowPath = args[++i]; break;
                case "--data" or "-d": dataPath = args[++i]; break;
                case "--config" or "-c": configPath = args[++i]; break;
                default:
                    Console.Error.WriteLine($"未知参数: {args[i]}");
                    PrintHelp();
                    return 1;
            }
        }
        if (workflowPath == null || dataPath == null) { PrintHelp(); return 1; }

        // ---------- 加载 workflow ----------
        WorkflowDefinition wf;
        if (File.Exists(workflowPath))
        {
            wf = JsonSerializer.Deserialize<WorkflowDefinition>(
                File.ReadAllText(workflowPath, Encoding.UTF8), JsonOpts)!;
        }
        else
        {
            Console.WriteLine($"workflow 文件不存在（{workflowPath}），使用内置默认流程");
            wf = ConfigStore.LoadWorkflow();
        }
        if (wf == null || wf.Steps.Count == 0)
        {
            Console.Error.WriteLine("workflow 为空或无效");
            return 1;
        }

        // ---------- 加载数据行（.json / .csv） ----------
        List<Dictionary<string, string>> rows;
        try
        {
            rows = LoadData(dataPath);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"数据文件解析失败: {ex.Message}");
            return 1;
        }
        if (rows == null || rows.Count == 0)
        {
            Console.Error.WriteLine("数据为空（JSON: 对象数组；CSV: 首行为表头）");
            return 1;
        }

        // ---------- 配置 ----------
        var cfg = configPath != null && File.Exists(configPath)
            ? ThemeConfig.Load(configPath)
            : new ThemeConfig();

        // ---------- 运行 ----------
        var runner = new WorkflowRunner(wf);
        runner.Log += m => Console.WriteLine(m);
        runner.Progress += (cur, total, el, eta) =>
            Console.WriteLine($"[进度] {cur}/{total} 已用 {el:F0}s 剩余 {(eta < 0 ? "-" : eta.ToString("F0") + "s")}");

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        var done = new TaskCompletionSource<bool>();
        runner.Finished += (results, ok, fail, unknown, totalSec) =>
        {
            Console.WriteLine($"\n完成: 成功 {ok} / 失败 {fail} / 未知 {unknown} / 总耗时 {totalSec:F0} 秒");
            foreach (var r in results)
                Console.WriteLine($"  {r.RowKey}\t{r.StatusText}\t{r.Detail}\t{r.ElapsedSeconds:F1}s");
            done.TrySetResult(true);
        };

        Console.WriteLine($"开始回放 {rows.Count} 行数据 → {wf.Name}（{wf.WindowTitle}）");
        await runner.RunAsync(rows, cfg, cts.Token);
        await done.Task;

        Console.WriteLine("回放结束");
        return 0;
    }

    /// <summary>按扩展名加载数据：.csv → 表头+引号解析；.xlsx → 第一个工作表；其他 → JSON 对象数组</summary>
    private static List<Dictionary<string, string>> LoadData(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext == ".csv") return LoadCsv(path);
        if (ext == ".xlsx") return LoadXlsx(path);

        return JsonSerializer.Deserialize<List<Dictionary<string, string>>>(
            File.ReadAllText(path, Encoding.UTF8), JsonOpts)
            ?? new List<Dictionary<string, string>>();
    }

    /// <summary>轻量 xlsx 读取（零依赖）：sharedStrings + 第一个工作表，首行为表头。仅支持基础单元格类型。</summary>
    private static List<Dictionary<string, string>> LoadXlsx(string path)
    {
        using var zip = System.IO.Compression.ZipFile.OpenRead(path);

        // shared strings（t="s" 单元格的取值索引）
        var shared = new List<string>();
        var ssEntry = zip.GetEntry("xl/sharedStrings.xml");
        if (ssEntry != null)
        {
            var doc = System.Xml.Linq.XDocument.Load(ssEntry.Open());
            shared = doc.Root!.Elements()
                .Where(e => e.Name.LocalName == "si")
                .Select(si => string.Concat(si.Descendants()
                    .Where(d => d.Name.LocalName == "t").Select(t => t.Value)))
                .ToList();
        }

        // 第一个工作表（sheet1.xml，按名称排序取最小）
        var sheetEntry = zip.Entries
            .Where(e => e.FullName.StartsWith("xl/worksheets/sheet", StringComparison.OrdinalIgnoreCase)
                        && e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.FullName, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (sheetEntry == null) return new List<Dictionary<string, string>>();

        var xdoc = System.Xml.Linq.XDocument.Load(sheetEntry.Open());
        var rows = new List<Dictionary<string, string>>();
        Dictionary<string, string>? header = null;

        var sheetData = xdoc.Root!.Elements().FirstOrDefault(e => e.Name.LocalName == "sheetData");
        if (sheetData == null) return rows;

        foreach (var row in sheetData.Elements().Where(e => e.Name.LocalName == "row"))
        {
            var cells = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in row.Elements().Where(e => e.Name.LocalName == "c"))
            {
                var col = ColLetters(c.Attribute("r")?.Value ?? "");
                if (col == null) continue;
                var t = (string?)c.Attribute("t") ?? "";
                var v = c.Elements().FirstOrDefault(e => e.Name.LocalName == "v")?.Value ?? "";
                cells[col] = t switch
                {
                    "s" => int.TryParse(v, out var idx) && idx >= 0 && idx < shared.Count ? shared[idx] : "",
                    "inlineStr" => string.Concat(c.Descendants()
                        .Where(d => d.Name.LocalName == "t").Select(x => x.Value)),
                    _ => v,
                };
            }

            if (header == null) { header = cells; continue; }
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var h in header)
                dict[h.Value] = cells.TryGetValue(h.Key, out var v) ? v : "";
            rows.Add(dict);
        }
        return rows;
    }

    /// <summary>从单元格引用（如 "B3"）提取列字母（"B"）；非法返回 null</summary>
    private static string? ColLetters(string cellRef)
    {
        var letters = new StringBuilder();
        foreach (var ch in cellRef)
        {
            if (char.IsLetter(ch)) letters.Append(ch);
            else break;
        }
        return letters.Length > 0 ? letters.ToString() : null;
    }

    private static List<Dictionary<string, string>> LoadCsv(string path)
    {
        var rows = new List<Dictionary<string, string>>();
        var lines = File.ReadAllLines(path, Encoding.UTF8)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .ToList();
        if (lines.Count == 0) return rows;

        var headers = ParseCsvLine(lines[0]);
        for (int i = 1; i < lines.Count; i++)
        {
            var fields = ParseCsvLine(lines[i]);
            var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int j = 0; j < headers.Count; j++)
                row[headers[j]] = j < fields.Count ? fields[j] : "";
            rows.Add(row);
        }
        return rows;
    }

    /// <summary>解析一行 CSV(支持双引号包裹与 "" 转义)</summary>
    private static List<string> ParseCsvLine(string line)
    {
        var fields = new List<string>();
        var sb = new StringBuilder();
        bool inQuotes = false;
        for (int i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                    else inQuotes = false;
                }
                else sb.Append(c);
            }
            else
            {
                if (c == '"') inQuotes = true;
                else if (c == ',') { fields.Add(sb.ToString()); sb.Clear(); }
                else sb.Append(c);
            }
        }
        fields.Add(sb.ToString());
        return fields;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("AutoPublishCore.Cli — 数据驱动界面自动化回放");
        Console.WriteLine();
        Console.WriteLine("用法:");
        Console.WriteLine("  autopublish --workflow <workflow.json> --data <data.json> [--config <settings.json>]");
        Console.WriteLine();
        Console.WriteLine("  --workflow, -w  流程定义（缺省时使用程序目录 workflow.json/内置默认）");
        Console.WriteLine("  --data, -d      数据文件：JSON 对象数组 / CSV（首行为表头）/ xlsx（第一个工作表，首行为表头），键注入流程占位符 @key");
        Console.WriteLine("  --config, -c    运行配置（settings.json，缺省用默认值）");
        Console.WriteLine();
        Console.WriteLine("示例 data.json:");
        Console.WriteLine("  [");
        Console.WriteLine("    { \"theme\": \"287\", \"files\": \"assets\\\\css|templates\" },");
        Console.WriteLine("    { \"theme\": \"288\" }");
        Console.WriteLine("  ]");
        Console.WriteLine();
        Console.WriteLine("示例 data.csv:");
        Console.WriteLine("  theme,files");
        Console.WriteLine("  287,assets\\css|templates");
        Console.WriteLine("  288,");
    }
}
