using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Input;
using AutoPublishCore.Models;

namespace AutoPublishRecorder;

/// <summary>
/// workflow.json 图形化编辑窗口：
/// 加载当前流程（内置默认或已有 workflow.json）→ 编辑步骤列表 → 保存到程序目录。
/// 引擎每次「开始发布」都会重新加载 workflow，因此保存后立即生效。
/// </summary>
public partial class WorkflowEditorWindow : Window
{
    private readonly ObservableCollection<WorkflowStep> _steps = new();

    public WorkflowEditorWindow() : this(null) { }

    /// <summary>打开编辑窗口；workflow 为 null 时加载当前 workflow.json（或内置默认）</summary>
    public WorkflowEditorWindow(WorkflowDefinition? workflow)
    {
        InitializeComponent();
        AddKindBox.ItemsSource = Enum.GetValues<StepKind>();
        AddKindBox.SelectedIndex = 0;
        LoadFrom(workflow ?? ConfigStore.LoadWorkflow());
    }

    // ===================== 数据加载 =====================

    private void LoadFrom(WorkflowDefinition wf)
    {
        NameBox.Text = wf.Name;
        TitleBox.Text = wf.WindowTitle;
        _steps.Clear();
        foreach (var s in wf.Steps) _steps.Add(s);
        StepsGrid.ItemsSource = _steps;
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        var path = ConfigStore.WorkflowPath;
        StatusText.Text = File.Exists(path)
            ? $"当前加载: {path}（自定义流程）"
            : "当前使用内置默认流程 — 保存后将在此路径生成 workflow.json: " + path;
    }

    // ===================== 标题栏 =====================

    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    // ===================== 步骤操作 =====================

    private static WorkflowStep? CurrentStep(object sender)
        => (sender as FrameworkElement)?.DataContext as WorkflowStep;

    private void MoveUp_Click(object sender, RoutedEventArgs e)
    {
        var s = CurrentStep(sender);
        if (s == null) return;
        var i = _steps.IndexOf(s);
        if (i > 0) _steps.Move(i, i - 1);
    }

    private void MoveDown_Click(object sender, RoutedEventArgs e)
    {
        var s = CurrentStep(sender);
        if (s == null) return;
        var i = _steps.IndexOf(s);
        if (i >= 0 && i < _steps.Count - 1) _steps.Move(i, i + 1);
    }

    private void DeleteStep_Click(object sender, RoutedEventArgs e)
    {
        var s = CurrentStep(sender);
        if (s != null) _steps.Remove(s);
    }

    private void AddStep_Click(object sender, RoutedEventArgs e)
    {
        if (AddKindBox.SelectedItem is not StepKind k) return;
        var s = new WorkflowStep { Kind = k };
        // 常用步骤预填占位符，减少手工输入
        s.Value = k switch
        {
            StepKind.Search or StepKind.SelectTheme => "@theme",
            StepKind.CheckFiles => "@files",
            _ => null,
        };
        _steps.Add(s);
    }

    private void ResetDefault_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "恢复为内置默认流程？当前未保存的编辑将丢失。", "确认",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;
        LoadFrom(WorkflowDefinition.CreateDefault());
    }

    // ===================== 保存 =====================

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_steps.Count == 0)
        {
            MessageBox.Show(this, "流程至少需要一个步骤。", "无法保存",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var wf = new WorkflowDefinition
        {
            Name = string.IsNullOrWhiteSpace(NameBox.Text) ? "主题模板发布" : NameBox.Text.Trim(),
            WindowTitle = string.IsNullOrWhiteSpace(TitleBox.Text) ? "主题模板发布" : TitleBox.Text.Trim(),
            Steps = _steps.ToList(),
        };

        // 字段规范化：无效策略回退 stop、非法数字回退默认
        foreach (var s in wf.Steps)
        {
            if (s.OnFail is not ("stop" or "continue" or "retry")) s.OnFail = "stop";
            if (s.TimeoutSec < 0) s.TimeoutSec = 0;
            if (s.MaxRetries < 0) s.MaxRetries = 2;
        }

        try
        {
            var json = JsonSerializer.Serialize(wf, new JsonSerializerOptions
            {
                WriteIndented = true,
                Converters = { new JsonStringEnumConverter() },
            });
            File.WriteAllText(ConfigStore.WorkflowPath, json, System.Text.Encoding.UTF8);
            MessageBox.Show(this,
                $"已保存到:\n{ConfigStore.WorkflowPath}\n\n下次点击「开始发布」即按新流程执行。",
                "保存成功", MessageBoxButton.OK, MessageBoxImage.Information);
            UpdateStatus();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"保存失败: {ex.Message}", "错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
