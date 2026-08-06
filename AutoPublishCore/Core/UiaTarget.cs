using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
using AutoPublishCore.Models;

namespace AutoPublishCore.Core;

/// <summary>
/// 「主题模板发布」目标程序的 UIA 封装（对应 Python 版 pywinauto 层）。
///
/// 关键约定（来自旧版踩坑经验）：
/// 1. 目标程序每切换一个主题都会刷新重建界面内容（文件树等），
///    因此所有控件查找都必须在「当前主题上下文」内实时进行，
///    【绝不跨主题缓存】任何元素引用，否则会"假操作"（不报错但无效）。
/// 2. 连接（窗口句柄）可复用，但控件必须每次重新查找。
/// 3. 所有固定 sleep 均替换为「轮询等待元素/状态」的事件驱动方式。
/// </summary>
public sealed class UiaTarget
{
    // ---------- Win32 ----------
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int VK_RETURN = 0x0D;
    private const int BM_CLICK = 0x00F5;

    private const string EditSearchId = "textBox1";
    private const string ListThemeId = "listBox1";
    private const string TreeFilesId = "treeView1";
    private const string BtnPublishId = "btn_publish";
    private const string CkbUpdateAllId = "ckb_updatealltemplate";

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")]
    private static extern IntPtr SendMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    private readonly string _windowTitle;
    private AutomationElement? _window;

    public bool IsConnected => _window != null && IsWindowAlive();
    public AutomationElement? Window => _window;

    public UiaTarget(string windowTitle) => _windowTitle = windowTitle;

    // ===================== 连接 =====================

    /// <summary>按窗口标题连接目标程序（先精确匹配，再模糊匹配）</summary>
    public bool Connect(int timeoutMs = 8000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            try
            {
                _window = FindWindowByTitle(_windowTitle);
                if (_window != null)
                {
                    // 用发布按钮的存在性验证窗口就绪
                    if (FindByAutomationId(BtnPublishId, 2000) != null)
                        return true;
                }
            }
            catch { }
            Thread.Sleep(200);
        }
        _window = null;
        return false;
    }

    private static AutomationElement? FindWindowByTitle(string title)
    {
        try
        {
            var exact = new PropertyCondition(AutomationElement.NameProperty, title);
            var el = AutomationElement.RootElement.FindFirst(TreeScope.Children, exact);
            if (el != null) return el;
            foreach (AutomationElement child in
                     AutomationElement.RootElement.FindAll(TreeScope.Children, Condition.TrueCondition))
            {
                try
                {
                    if (child.Current.Name?.Contains(title) == true) return child;
                }
                catch { }
            }
        }
        catch { }
        return null;
    }

    public bool IsWindowAlive()
    {
        try
        {
            if (_window == null) return false;
            var hwnd = new IntPtr(_window.Current.NativeWindowHandle);
            return hwnd != IntPtr.Zero && IsWindow(hwnd);
        }
        catch
        {
            return false; // 元素已失效（目标程序重建）
        }
    }

    // ===================== 通用查找（轮询，不跨主题缓存） =====================

    public AutomationElement? FindByAutomationId(string autoId, int timeoutMs)
    {
        if (_window == null) return null;
        var cond = new PropertyCondition(AutomationElement.AutomationIdProperty, autoId);
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            try
            {
                var el = _window.FindFirst(TreeScope.Descendants, cond);
                if (el != null) return el;
            }
            catch { return null; }
            Thread.Sleep(100);
        }
        return null;
    }

    public AutomationElement? FindByName(string name, int timeoutMs, TreeScope scope = TreeScope.Descendants)
    {
        if (_window == null) return null;
        var cond = new PropertyCondition(AutomationElement.NameProperty, name);
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            try
            {
                var el = _window.FindFirst(scope, cond);
                if (el != null) return el;
            }
            catch { return null; }
            Thread.Sleep(100);
        }
        return null;
    }

    private static readonly Condition CheckBoxCond =
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.CheckBox);

    private static readonly Condition TreeItemCond =
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TreeItem);

    private static readonly Condition ListItemCond =
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem);

    private static string SafeName(AutomationElement el)
    {
        try { return el.Current.Name ?? ""; }
        catch { return ""; }
    }

    // ===================== 搜索主题 =====================

    /// <summary>
    /// 在搜索框输入主题 ID 并回车，然后轮询列表直到出现匹配项。
    /// 无固定等待，靠列表内容驱动。
    /// </summary>
    public bool Search(string tid, int timeoutMs)
    {
        var box = FindByAutomationId(EditSearchId, 5000);
        if (box == null) return false;

        var vp = box.GetCurrentPattern(ValuePattern.Pattern) as ValuePattern;
        try { vp?.SetValue(""); Thread.Sleep(30); } catch { }
        try { vp?.SetValue(tid); } catch { }

        var hwnd = new IntPtr(box.Current.NativeWindowHandle);
        if (hwnd != IntPtr.Zero)
        {
            PostMessageW(hwnd, WM_KEYDOWN, VK_RETURN, IntPtr.Zero);
            PostMessageW(hwnd, WM_KEYUP, VK_RETURN, IntPtr.Zero);
        }

        // 轮询列表出现匹配项（搜索生效的信号）
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (FindMatchingItem(tid) != null) return true;
            Thread.Sleep(100);
        }
        return FindMatchingItem(tid) != null;
    }

    /// <summary>在 listBox1 中查找名称以 "tid:" / "tid：" 开头的项</summary>
    private AutomationElement? FindMatchingItem(string tid)
    {
        var lst = FindByAutomationId(ListThemeId, 2000);
        if (lst == null) return null;
        try
        {
            foreach (AutomationElement item in lst.FindAll(TreeScope.Children, ListItemCond))
            {
                var name = SafeName(item);
                if (name.StartsWith(tid + ":") || name.StartsWith(tid + "："))
                    return item;
            }
        }
        catch { }
        return null;
    }

    // ===================== 选择主题 =====================

    /// <summary>选中匹配项；列表为空或无匹配时回退选中第一条。返回选中项文本。</summary>
    public bool SelectTheme(string tid, int timeoutMs, out string? selectedName)
    {
        selectedName = null;
        var item = FindMatchingItem(tid);
        if (item == null)
        {
            var lst = FindByAutomationId(ListThemeId, 3000);
            if (lst == null) return false;
            try
            {
                item = lst.FindFirst(TreeScope.Children, ListItemCond);
            }
            catch { return false; }
        }
        if (item == null) return false;

        if (item.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var sel))
        {
            ((SelectionItemPattern)sel).Select();
        }
        else if (item.TryGetCurrentPattern(InvokePattern.Pattern, out var inv))
        {
            ((InvokePattern)inv).Invoke();
        }
        else
        {
            var hwnd = new IntPtr(item.Current.NativeWindowHandle);
            if (hwnd != IntPtr.Zero)
                PostMessageW(hwnd, WM_KEYDOWN, VK_RETURN, IntPtr.Zero);
        }
        selectedName = SafeName(item);
        return true;
    }

    // ===================== 勾选文件 =====================

    /// <summary>勾选「整个模板更新」复选框</summary>
    public bool CheckUpdateAll()
    {
        var ckb = FindByAutomationId(CkbUpdateAllId, 3000);
        if (ckb == null) return false;
        try
        {
            if (ckb.TryGetCurrentPattern(TogglePattern.Pattern, out var tp))
            {
                var t = (TogglePattern)tp;
                if (t.Current.ToggleState != ToggleState.On) t.Toggle();
                return true;
            }
        }
        catch { }
        return false;
    }

    /// <summary>
    /// 勾选指定文件路径（路径以 \ 分隔）。
    /// 流程：展开父文件夹 → 重新读取整棵文件树建立 name→节点 映射 → 勾选。
    /// 映射每次重新建立，绝不跨主题复用（目标程序切主题后树会重建）。
    /// </summary>
    public (int checkedCount, List<string> missing) CheckFiles(
        IReadOnlyList<string> paths, CancellationToken ct)
    {
        var tree = FindByAutomationId(TreeFilesId, 5000);
        if (tree == null) return (0, paths.ToList());

        var norm = paths.Select(p => p.Replace('/', '\\')).ToList();
        if (norm.Count == 0) return (0, new List<string>());

        // ---- 第 1 步：展开所有必要父文件夹（按深度优先，短等待由轮询完成） ----
        var folders = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var p in norm)
        {
            var segs = p.Split('\\', StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < segs.Length - 1; i++)
                folders.Add(string.Join("\\", segs.Take(i + 1)));
        }
        foreach (var folder in folders)
        {
            if (ct.IsCancellationRequested) break;
            ExpandFolderNode(tree, folder);
        }

        // ---- 第 2 步：读取整棵树建立 name -> element 映射 ----
        // 优先 CheckBox；一个都没有则退化为 TreeItem（目标程序树节点类型可能不同）
        var nodes = new List<AutomationElement>();
        try
        {
            foreach (AutomationElement n in tree.FindAll(TreeScope.Descendants, CheckBoxCond))
                nodes.Add(n);
        }
        catch { }
        if (nodes.Count == 0)
        {
            try
            {
                foreach (AutomationElement n in tree.FindAll(TreeScope.Descendants, TreeItemCond))
                    nodes.Add(n);
            }
            catch { }
        }

        var map = new Dictionary<string, AutomationElement>(StringComparer.Ordinal);
        foreach (var n in nodes)
        {
            var name = SafeName(n);
            if (name.Length > 0 && !map.ContainsKey(name)) map[name] = n;
        }

        // ---- 第 3 步：从映射直接勾选（O(1)） ----
        int ok = 0;
        var missing = new List<string>();
        foreach (var p in norm)
        {
            if (ct.IsCancellationRequested) break;
            if (map.TryGetValue(p, out var node))
            {
                if (TryToggleOn(node)) ok++;
                else missing.Add(p);
            }
            else missing.Add(p);
        }
        return (ok, missing);
    }

    private void ExpandFolderNode(AutomationElement tree, string folder)
    {
        try
        {
            var cond = new PropertyCondition(AutomationElement.NameProperty, folder);
            var node = tree.FindFirst(TreeScope.Descendants, cond);
            if (node == null) return;
            if (node.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var ep))
            {
                var ecp = (ExpandCollapsePattern)ep;
                if (ecp.Current.ExpandCollapseState != ExpandCollapseState.Expanded)
                    ecp.Expand();
                return;
            }
            // 兜底：某些树节点用 Invoke 展开
            if (node.TryGetCurrentPattern(InvokePattern.Pattern, out var ip))
            {
                ((InvokePattern)ip).Invoke();
            }
        }
        catch { }
    }

    private static bool TryToggleOn(AutomationElement node)
    {
        try
        {
            if (node.TryGetCurrentPattern(TogglePattern.Pattern, out var tp))
            {
                var t = (TogglePattern)tp;
                if (t.Current.ToggleState != ToggleState.On) t.Toggle();
                return true;
            }
            if (node.TryGetCurrentPattern(InvokePattern.Pattern, out var ip))
            {
                ((InvokePattern)ip).Invoke();
                return true;
            }
        }
        catch { }
        return false;
    }

    // ===================== 点击发布 =====================

    public bool ClickPublish()
    {
        var btn = FindByAutomationId(BtnPublishId, 5000);
        if (btn == null) return false;
        if (btn.TryGetCurrentPattern(InvokePattern.Pattern, out var ip))
        {
            try { ((InvokePattern)ip).Invoke(); return true; }
            catch { }
        }
        var hwnd = new IntPtr(btn.Current.NativeWindowHandle);
        if (hwnd != IntPtr.Zero)
        {
            SendMessageW(hwnd, BM_CLICK, IntPtr.Zero, IntPtr.Zero);
            return true;
        }
        return false;
    }

    // ===================== 通用定位与动作（录制/通用流程用） =====================

    /// <summary>解析定位器："id=xxx"（AutomationId）或 "name=xxx"（Name），返回元素</summary>
    public AutomationElement? FindByLocator(string locator, int timeoutMs)
    {
        if (string.IsNullOrWhiteSpace(locator)) return null;
        locator = locator.Trim();
        if (locator.StartsWith("id=", StringComparison.OrdinalIgnoreCase))
            return FindByAutomationId(locator[3..], timeoutMs);
        if (locator.StartsWith("name=", StringComparison.OrdinalIgnoreCase))
            return FindByName(locator[5..], timeoutMs);
        return null;
    }

    /// <summary>点击定位器指向的控件（优先 Invoke，兜底 WM_CLICK）</summary>
    public bool InvokeByLocator(string locator, int timeoutMs)
    {
        var el = FindByLocator(locator, timeoutMs);
        if (el == null) return false;
        if (el.TryGetCurrentPattern(InvokePattern.Pattern, out var ip))
        {
            try { ((InvokePattern)ip).Invoke(); return true; }
            catch { }
        }
        var hwnd = new IntPtr(el.Current.NativeWindowHandle);
        if (hwnd != IntPtr.Zero)
        {
            SendMessageW(hwnd, BM_CLICK, IntPtr.Zero, IntPtr.Zero);
            return true;
        }
        return false;
    }

    /// <summary>向定位器指向的输入框写入文本（ValuePattern.SetValue）</summary>
    public bool SetValueByLocator(string locator, string value, int timeoutMs)
    {
        if (string.IsNullOrEmpty(locator)) return false;
        var el = FindByLocator(locator, timeoutMs);
        if (el == null) return false;
        try
        {
            if (el.TryGetCurrentPattern(ValuePattern.Pattern, out var vp))
            {
                ((ValuePattern)vp).SetValue(value ?? "");
                return true;
            }
        }
        catch { }
        return false;
    }

    /// <summary>向定位器指向的控件发送回车键（触发搜索/确认；与 Search 内部相同的 PostMessage 方式）</summary>
    public bool PressEnterByLocator(string locator, int timeoutMs)
    {
        var el = FindByLocator(locator, timeoutMs);
        if (el == null) return false;
        try
        {
            var hwnd = new IntPtr(el.Current.NativeWindowHandle);
            if (hwnd == IntPtr.Zero) return false;
            PostMessageW(hwnd, WM_KEYDOWN, VK_RETURN, IntPtr.Zero);
            PostMessageW(hwnd, WM_KEYUP, VK_RETURN, IntPtr.Zero);
            return true;
        }
        catch { return false; }
    }

    /// <summary>切换定位器指向的复选框/单选钮（Toggle；无 Toggle 时兜底 Invoke）</summary>
    public bool ToggleByLocator(string locator, int timeoutMs)
    {
        var el = FindByLocator(locator, timeoutMs);
        if (el == null) return false;
        if (el.TryGetCurrentPattern(TogglePattern.Pattern, out var tp))
        {
            var t = (TogglePattern)tp;
            if (t.Current.ToggleState != ToggleState.On) t.Toggle();
            return true;
        }
        if (el.TryGetCurrentPattern(InvokePattern.Pattern, out var ip))
        {
            try { ((InvokePattern)ip).Invoke(); return true; }
            catch { }
        }
        return false;
    }

    /// <summary>展开定位器指向的树节点（ExpandCollapsePattern；无展开能力时兜底 Invoke）</summary>
    public bool ExpandByLocator(string locator, int timeoutMs)
    {
        var el = FindByLocator(locator, timeoutMs);
        if (el == null) return false;
        try
        {
            if (el.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var ep))
            {
                var ecp = (ExpandCollapsePattern)ep;
                if (ecp.Current.ExpandCollapseState != ExpandCollapseState.Expanded) ecp.Expand();
                return true;
            }
            if (el.TryGetCurrentPattern(InvokePattern.Pattern, out var ip))
            {
                ((InvokePattern)ip).Invoke();
                return true;
            }
        }
        catch { }
        return false;
    }

    /// <summary>从元素生成定位器："id=xxx"（优先）/"name=xxx"；都无法识别返回 null</summary>
    public static string? DescribeLocator(AutomationElement el)
    {
        try
        {
            var id = el.Current.AutomationId;
            if (!string.IsNullOrEmpty(id)) return "id=" + id;
            var name = el.Current.Name;
            if (!string.IsNullOrEmpty(name)) return "name=" + name;
        }
        catch { }
        return null;
    }

    /// <summary>向上找到元素所属的顶层窗口句柄（用于录制器的进程匹配）</summary>
    public static IntPtr GetTopWindowHandle(AutomationElement el)
    {
        try
        {
            var cur = el;
            var walker = TreeWalker.ControlViewWalker;
            IntPtr top = IntPtr.Zero;
            while (cur != null)
            {
                if (cur.Current.ControlType == ControlType.Window)
                {
                    var h = new IntPtr(cur.Current.NativeWindowHandle);
                    if (h != IntPtr.Zero) top = h;
                }
                try { cur = walker.GetParent(cur); }
                catch { break; }
            }
            return top;
        }
        catch { return IntPtr.Zero; }
    }

    /// <summary>向上找到元素所属的顶层窗口元素（用于判断点击是否在目标窗口内）</summary>
    public static string? GetTopWindowName(AutomationElement el)
    {
        try
        {
            var cur = el;
            var walker = TreeWalker.ControlViewWalker;
            string? top = null;
            while (cur != null)
            {
                if (cur.Current.ControlType == ControlType.Window)
                    top = SafeName(cur);
                try { cur = walker.GetParent(cur); }
                catch { break; }
            }
            return top;
        }
        catch { return null; }
    }

    // ===================== 发布结果检测（事件驱动，不依赖关键词） =====================

    // 快照采集范围：覆盖文本/状态栏/按钮/列表项/数据行
    // （目标程序的「执行记录」通常是 ListBox/DataGrid 里的 ListItem/DataItem，
    //   发布完成后会追加一行记录 → 界面文本变化）
    private static readonly Condition SnapshotCond = new AndCondition(
        new PropertyCondition(AutomationElement.IsOffscreenProperty, false),
        new OrCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.StatusBar),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.DataItem)));

    /// <summary>采集当前窗口可见文本快照（有序拼接），用于变化比对</summary>
    private string CollectSnapshotKey()
    {
        var sb = new StringBuilder();
        if (_window == null) return "";
        try
        {
            foreach (AutomationElement el in _window.FindAll(TreeScope.Descendants, SnapshotCond))
            {
                var name = SafeName(el);
                if (name.Length > 0) sb.Append(name).Append('|');
            }
        }
        catch { }
        return sb.ToString();
    }

    /// <summary>
    /// 发布结果检测（事件驱动）：
    /// 1. 点击发布后立即采集基线快照；
    /// 2. 轮询窗口快照 —— 与上次不同说明界面有活动（执行记录新增/状态刷新），进入活动状态；
    /// 3. 活动状态后连续 3 次（约 1 秒）快照完全相同 → 判定发布执行完成（记录耗时）；
    /// 4. 直到超时仍未检测到任何变化 → 未知，由上层决定是否继续。
    /// 完全依赖发布程序的界面变化（执行记录），不使用任何成功/失败关键词。
    /// </summary>
    public PublishOutcome DetectResult(ThemeConfig cfg, CancellationToken ct)
    {
        var baseline = CollectSnapshotKey();
        var sw = Stopwatch.StartNew();
        string lastKey = baseline;
        bool activitySeen = false;
        int stableCount = 0;

        while (sw.ElapsedMilliseconds < cfg.DetectTimeoutSec * 1000L && !ct.IsCancellationRequested)
        {
            string cur;
            try { cur = CollectSnapshotKey(); }
            catch { Thread.Sleep(300); continue; }

            if (cur != lastKey)
            {
                // 界面有变化（发布开始 / 执行记录新增 / 状态刷新）
                activitySeen = true;
                stableCount = 0;
            }
            else if (activitySeen)
            {
                // 变化后界面已稳定 → 发布执行完成
                stableCount++;
                if (stableCount >= 3)
                    return PublishOutcome.Success; // 语义：已发布（执行记录已更新）
            }
            lastKey = cur;
            Thread.Sleep(300);
        }

        // 从未检测到任何变化（执行记录可能在其他地方），超时视为未知
        return PublishOutcome.Unknown;
    }
}
