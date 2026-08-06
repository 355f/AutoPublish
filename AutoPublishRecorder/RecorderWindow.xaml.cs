using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using AutoPublishCore.Core;
using AutoPublishCore.Models;

namespace AutoPublishRecorder;

/// <summary>
/// 流程录制器：挂全局鼠标/键盘低级钩子，用户在目标程序上操作一遍，
/// 程序把点击位置的 UIA 元素解析为通用步骤（Click/SetValue/Toggle），
/// 生成 workflow 骨架，保存为 workflow.json 后可进「流程编辑」微调。
/// </summary>
public partial class RecorderWindow : Window
{
    private readonly ObservableCollection<WorkflowStep> _steps = new();

    private bool _recording;
    private string _targetTitle = "";

    // 输入模式：点击输入框后进入，键盘 Enter/Tab 或点击其他位置时提交
    private AutomationElement? _inputElement;
    private string? _inputLocator;
    private bool _pressEnter;

    // ---------- 低级钩子 ----------
    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    private const int WH_MOUSE_LL = 14;
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const uint VK_RETURN = 0x0D;
    private const uint VK_TAB = 0x09;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x; public int y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    private const int VK_CONTROL = 0x11;

    private IntPtr _mouseHook;
    private IntPtr _keyHook;
    private HookProc? _mouseProc; // 持有引用防止委托被 GC
    private HookProc? _keyProc;
    private uint _targetPid;      // 目标窗口进程（首次标题匹配时记录，同进程弹窗也接受）

    public RecorderWindow()
    {
        InitializeComponent();
        StepList.ItemsSource = _steps;
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

    private void Window_Closed(object? sender, EventArgs e)
    {
        _recording = false;
        StopHooks();
    }

    // ===================== 录制控制 =====================

    private void Start_Click(object sender, RoutedEventArgs e)
    {
        _targetTitle = TargetTitleBox.Text.Trim();
        if (_targetTitle.Length == 0)
        {
            MessageBox.Show(this, "请输入目标窗口标题。", "提示",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!StartHooks())
        {
            MessageBox.Show(this, "安装鼠标/键盘钩子失败，请以管理员身份运行后重试。", "错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        _recording = true;
        StartBtn.IsEnabled = false;
        StopBtn.IsEnabled = true;
        TargetTitleBox.IsEnabled = false;
        RecordStatus.Text = "● 录制中 — 请操作目标程序…";
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        CommitInput();
        StopHooks();
        _recording = false;
        StartBtn.IsEnabled = true;
        StopBtn.IsEnabled = false;
        TargetTitleBox.IsEnabled = true;
        RecordStatus.Text = $"已停止 — 共录制 {_steps.Count} 个步骤";
    }

    private bool StartHooks()
    {
        try
        {
            var hMod = GetModuleHandle(null);
            _mouseProc = MouseHookProc;
            _keyProc = KeyHookProc;
            _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, hMod, 0);
            _keyHook = SetWindowsHookEx(WH_KEYBOARD_LL, _keyProc, hMod, 0);
            return _mouseHook != IntPtr.Zero && _keyHook != IntPtr.Zero;
        }
        catch
        {
            StopHooks();
            return false;
        }
    }

    private void StopHooks()
    {
        if (_mouseHook != IntPtr.Zero) { UnhookWindowsHookEx(_mouseHook); _mouseHook = IntPtr.Zero; }
        if (_keyHook != IntPtr.Zero) { UnhookWindowsHookEx(_keyHook); _keyHook = IntPtr.Zero; }
    }

    // ===================== 钩子回调 =====================

    private IntPtr MouseHookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && (int)wParam == WM_LBUTTONDOWN)
        {
            var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            HandleClick(info.pt);
        }
        return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    private IntPtr KeyHookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && ((int)wParam == WM_KEYDOWN || (int)wParam == WM_SYSKEYDOWN))
        {
            var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            HandleKeyDown(info.vkCode);
        }
        return CallNextHookEx(_keyHook, nCode, wParam, lParam);
    }

    // ===================== 元素解析 =====================

    private void HandleClick(POINT pt)
    {
        if (!_recording) return;
        try
        {
            var el = AutomationElement.FromPoint(new Point(pt.x, pt.y));
            if (el == null)
            {
                RecordStatus.Text = "⏭ 忽略: 该位置无 UIA 元素";
                return;
            }

            // 目标窗口判定：标题匹配（首次，并记录进程）或同进程窗口（弹窗/确认框也录制）
            if (!IsInTargetWindow(el, out var topName))
            {
                RecordStatus.Text = string.IsNullOrEmpty(topName)
                    ? "⏭ 忽略: 无法确定所在窗口"
                    : $"⏭ 忽略: 非目标窗口点击（{topName}）";
                return;
            }

            // 若之前在输入框中，先提交（点击别处 = 结束输入）
            CommitInput();

            var locator = UiaTarget.DescribeLocator(el);

            // Ctrl+点击 = 展开树节点（区分「展开」与「勾选」）
            if (IsCtrlDown())
            {
                if (locator == null)
                {
                    RecordStatus.Text = $"⚠ 无法识别控件（缺少 AutomationId/Name），已忽略: {SafeName(el)}";
                    return;
                }
                if (!SupportsExpand(el))
                {
                    RecordStatus.Text = $"⚠ 该控件不支持展开（无 ExpandCollapsePattern）: {locator}";
                    return;
                }
                _steps.Add(new WorkflowStep { Kind = StepKind.Expand, Locator = locator });
                RecordStatus.Text = $"✔ 已录制: Expand {locator}（Ctrl+点击）";
                return;
            }

            var kind = ClassifyElement(el);
            if (kind == null || locator == null)
            {
                RecordStatus.Text = $"⚠ 无法识别控件（缺少 AutomationId/Name），已忽略: {SafeName(el)}";
                return;
            }
            var stepKind = kind.Value;

            if (stepKind == StepKind.SetValue)
            {
                _inputElement = el;
                _inputLocator = locator;
                RecordStatus.Text = $"⌨ 输入框就绪: {locator} — 输入后按 Enter 提交（回车触发搜索）/ Tab 提交";
                return;
            }

            _steps.Add(new WorkflowStep { Kind = stepKind, Locator = locator });
            RecordStatus.Text = $"✔ 已录制: {stepKind} {locator}";
        }
        catch (Exception ex)
        {
            RecordStatus.Text = "⚠ 解析控件失败: " + ex.Message;
        }
    }

    private bool IsInTargetWindow(AutomationElement el, out string? topName)
    {
        topName = UiaTarget.GetTopWindowName(el);
        if (!string.IsNullOrEmpty(topName) &&
            topName.Contains(_targetTitle, StringComparison.OrdinalIgnoreCase))
        {
            var hwnd = UiaTarget.GetTopWindowHandle(el);
            if (hwnd != IntPtr.Zero) GetWindowThreadProcessId(hwnd, out _targetPid);
            return true;
        }
        // 标题不匹配但同进程（确认框等子窗口）→ 仍视为目标窗口
        if (_targetPid != 0)
        {
            var hwnd = UiaTarget.GetTopWindowHandle(el);
            if (hwnd != IntPtr.Zero)
            {
                GetWindowThreadProcessId(hwnd, out var pid);
                if (pid == _targetPid) return true;
            }
        }
        return false;
    }

    private static bool IsCtrlDown()
        => (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0;

    private static bool SupportsExpand(AutomationElement el)
    {
        try { return el.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out _); }
        catch { return false; }
    }

    private void HandleKeyDown(uint vk)
    {
        if (!_recording || _inputElement == null) return;
        if (vk == VK_RETURN) { _pressEnter = true; CommitInput(); }
        else if (vk == VK_TAB) { _pressEnter = false; CommitInput(); }
    }

    private void CommitInput()
    {
        if (_inputElement == null) return;
        var locator = _inputLocator;
        string value = "";
        try
        {
            if (_inputElement.TryGetCurrentPattern(ValuePattern.Pattern, out var vp))
                value = ((ValuePattern)vp).Current.Value ?? "";
        }
        catch { }
        _inputElement = null;
        _inputLocator = null;

        if (value.Length == 0 || string.IsNullOrEmpty(locator)) return;

        var pressEnter = _pressEnter;
        _pressEnter = false;

        // 与上一步相同的 SetValue（同一输入框、同一文本）→ 去重，只补全回车标志
        if (_steps.Count > 0 && _steps[^1] is { Kind: StepKind.SetValue } last
            && last.Locator == locator && last.Value == value)
        {
            if (pressEnter) { last.PressEnter = true; _steps[^1] = last; }
        }
        else
        {
            _steps.Add(new WorkflowStep
            {
                Kind = StepKind.SetValue,
                Locator = locator,
                Value = value,
                PressEnter = pressEnter,
            });
        }
        RecordStatus.Text = $"✔ 已录制: SetValue {locator} = \"{value}\"" + (pressEnter ? "（回车提交）" : "");
    }

    private static StepKind? ClassifyElement(AutomationElement el)
    {
        try
        {
            var ct = el.Current.ControlType;
            if (ct == ControlType.Button) return StepKind.Click;
            if (ct == ControlType.CheckBox || ct == ControlType.RadioButton) return StepKind.Toggle;
            if (ct == ControlType.Edit || ct == ControlType.ComboBox || ct == ControlType.Document)
                return StepKind.SetValue;
            // 带复选框的树节点（TreeItem + TogglePattern）→ 勾选动作
            if (el.TryGetCurrentPattern(TogglePattern.Pattern, out _)) return StepKind.Toggle;
            if (el.TryGetCurrentPattern(InvokePattern.Pattern, out _)) return StepKind.Click;
            if (el.TryGetCurrentPattern(SelectionItemPattern.Pattern, out _)) return StepKind.Click;
        }
        catch { }
        return null;
    }

    private static string SafeName(AutomationElement el)
    {
        try { return el.Current.Name ?? ""; }
        catch { return ""; }
    }

    // ===================== 列表操作 =====================

    private void DeleteStep_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is WorkflowStep s)
            _steps.Remove(s);
    }

    /// <summary>把 SetValue 步骤的值设为 @theme（多主题发布的关键：主题变成每轮注入的数据）</summary>
    private void SetToTheme_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not WorkflowStep s) return;
        s.Value = "@theme";
        s.PressEnter = true; // 主题搜索需要回车触发
        var i = _steps.IndexOf(s);
        if (i >= 0) _steps[i] = s; // 触发列表刷新
        RecordStatus.Text = $"✔ 已设为 @theme: {s.Locator}（每轮自动注入当前主题，回车提交已开启）";
    }

    private void InsertWait_Click(object sender, RoutedEventArgs e)
    {
        int secs = 2;
        int.TryParse(WaitSecBox.Text.Trim(), out secs);
        if (secs <= 0) secs = 2;
        _steps.Add(new WorkflowStep { Kind = StepKind.Wait, TimeoutSec = secs });
        RecordStatus.Text = $"已插入: Wait {secs} 秒";
    }

    private void InsertDetect_Click(object sender, RoutedEventArgs e)
    {
        _steps.Add(new WorkflowStep { Kind = StepKind.DetectResult, TimeoutSec = 60 });
        RecordStatus.Text = "已插入: DetectResult（界面变化即判定完成，超时 60 秒）";
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (_steps.Count == 0) return;
        if (MessageBox.Show(this, "清空已录制的全部步骤？", "确认",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;
        _steps.Clear();
        RecordStatus.Text = "已清空";
    }

    /// <summary>把连续录制的树节点 Toggle（name=路径）合并为单个 CheckFiles 步骤：
    /// 读整棵树勾选、自动展开父目录、缺失文件宽容——不同主题文件结构不同时不会因某个文件缺失而整主题失败。</summary>
    private void MergeToggles_Click(object sender, RoutedEventArgs e)
    {
        var merged = new List<WorkflowStep>();
        int i = 0;
        while (i < _steps.Count)
        {
            var s = _steps[i];
            if (s.Kind == StepKind.Toggle &&
                s.Locator?.StartsWith("name=", StringComparison.OrdinalIgnoreCase) == true)
            {
                var paths = new List<string>();
                while (i < _steps.Count && _steps[i].Kind == StepKind.Toggle &&
                       _steps[i].Locator?.StartsWith("name=", StringComparison.OrdinalIgnoreCase) == true)
                {
                    paths.Add(_steps[i].Locator![5..]);
                    i++;
                }
                if (paths.Count >= 2)
                {
                    merged.Add(new WorkflowStep { Kind = StepKind.CheckFiles, Value = string.Join("|", paths) });
                }
                else
                {
                    foreach (var p in paths)
                        merged.Add(new WorkflowStep { Kind = StepKind.Toggle, Locator = "name=" + p });
                }
            }
            else { merged.Add(s); i++; }
        }

        _steps.Clear();
        foreach (var m in merged) _steps.Add(m);
        RecordStatus.Text = $"已合并完成，共 {_steps.Count} 个步骤（树勾选已转为 CheckFiles）";
    }

    // ===================== 保存 =====================

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        CommitInput();
        if (_steps.Count == 0)
        {
            MessageBox.Show(this, "还没有录制任何步骤。", "无法保存",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var wf = new WorkflowDefinition
        {
            Name = "录制流程",
            WindowTitle = _targetTitle.Length > 0 ? _targetTitle : "主题模板发布",
            Steps = _steps.ToList(),
        };
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

            // 多主题提示：若流程里有写死的 SetValue，多半是主题搜索，提醒变量化
            var hasFixedSetValue = wf.Steps.Any(s => s.Kind == StepKind.SetValue);
            var toggleCount = wf.Steps.Count(s => s.Kind == StepKind.Toggle);
            var msg = $"已保存到:\n{ConfigStore.WorkflowPath}\n";
            if (hasFixedSetValue)
                msg += "\n⚠ 提示: 流程里有 SetValue 步骤。若它是「主题搜索」（每个主题不同），\n" +
                       "请把它设为 @theme（列表里点「@主题」按钮），否则多主题发布时\n" +
                       "所有主题都会搜索同一个固定值！\n";
            if (toggleCount >= 2)
                msg += $"\n⚠ 提示: 有 {toggleCount} 个 Toggle 步骤。若它们是文件树的勾选（路径写死），\n" +
                       "建议先点「合并勾选→CheckFiles」——不同主题文件结构不同时，\n" +
                       "写死的路径会失败；CheckFiles 读整棵树勾选且缺失文件宽容。\n";
            msg += "\n是否打开「流程编辑」微调超时/失败策略等细节？";
            var ask = MessageBox.Show(this, msg, "保存成功", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (ask == MessageBoxResult.Yes)
            {
                new WorkflowEditorWindow(wf) { Owner = this }.ShowDialog();
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"保存失败: {ex.Message}", "错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
