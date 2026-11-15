using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using Toolbox.Core;

namespace Toolbox.UI;

/// <summary>工具页：Windows 右键菜单管理（浏览 / 启用禁用 / 删除备份恢复 / 新增 / 本工具菜单）。</summary>
internal sealed class ContextMenuView : UserControl, IThemed
{
    private static int S(int value) => Theme.S(value);

    private readonly BufferedPanel _toolbar = new();
    private readonly BufferedPanel _footer = new();
    private readonly ContextMenuList _list = new();
    private readonly SearchBox _search = new("搜索菜单名称、执行命令或挂载位置…");
    private readonly ModernButton[] _filters = new ModernButton[4];
    private readonly ModernButton _btnRefresh, _btnOwn, _btnElevate;
    private readonly ModernButton _btnToggle, _btnDelete, _btnRestore, _btnAdd, _btnLocate;
    private readonly System.Windows.Forms.Timer _debounce = new() { Interval = 130 };

    private List<ContextMenuEntry> _all = new();
    private int _filterIndex;
    private bool _ready, _busy;
    private string _statusText = "";

    public ContextMenuView()
    {
        Dock = DockStyle.Fill;
        BackColor = Theme.Surface;
        Font = Theme.FontBase;
        DoubleBuffered = true;

        _toolbar.Dock = DockStyle.Top;
        _toolbar.Height = S(80);
        _toolbar.Paint += PaintToolbar;
        _footer.Dock = DockStyle.Bottom;
        _footer.Height = S(76);
        _footer.Paint += PaintFooter;
        _list.Dock = DockStyle.Fill;

        string[] labels = { "全部", "已启用", "已禁用", "本工具" };
        int[] widths = { 62, 76, 76, 76 };
        for (int i = 0; i < _filters.Length; i++)
        {
            int index = i;
            _filters[i] = new ModernButton
            {
                Text = labels[i], Width = S(widths[i]), Height = S(32), Font = Theme.FontSmall,
                Kind = i == 0 ? ButtonKind.Primary : ButtonKind.Subtle,
            };
            _filters[i].Click += (_, _) => SetFilter(index);
        }

        _btnRefresh = new ModernButton
        {
            Icon = Glyph.Refresh, Kind = ButtonKind.Subtle, Width = S(36), Height = S(32), Radius = 8,
        };
        _btnRefresh.Click += (_, _) => { if (!_busy) _ = LoadAsync(); };
        _btnOwn = Button("安装本工具菜单", 128, ButtonKind.Primary);
        _btnOwn.Click += (_, _) => ToggleOwnMenu();
        _btnElevate = Button("以管理员重启", 128, ButtonKind.Subtle);
        _btnElevate.Icon = Glyph.Shield;
        _btnElevate.Click += (_, _) => RelaunchElevated();

        _btnToggle = Button("禁用所选", 108, ButtonKind.Subtle);
        _btnDelete = Button("删除并备份", 116, ButtonKind.Danger);
        _btnDelete.Icon = Glyph.Trash;
        _btnRestore = Button("恢复备份", 108, ButtonKind.Subtle);
        _btnRestore.Icon = Glyph.Undo;
        _btnAdd = Button("新增菜单项", 116, ButtonKind.Subtle);
        _btnAdd.Icon = Glyph.Plus;
        _btnLocate = Button("注册表定位", 116, ButtonKind.Subtle);
        _btnLocate.Icon = Glyph.Registry;

        _btnToggle.Click += (_, _) => ToggleSelected();
        _btnDelete.Click += (_, _) => DeleteSelected();
        _btnRestore.Click += (_, _) => RestoreBackup();
        _btnAdd.Click += (_, _) => AddCustom();
        _btnLocate.Click += (_, _) => LocateSelected();

        _search.QueryChanged += (_, _) => { _debounce.Stop(); _debounce.Start(); };
        _debounce.Tick += (_, _) => { _debounce.Stop(); ApplyFilter(); };
        _list.SelectionChanged += (_, _) => { UpdateButtons(); UpdateStatus(); };
        _list.ItemDoubleClick += entry => _ = ToggleOne(entry);
        _list.ItemRightClick += (entry, at) => ShowRowMenu(entry, at);

        _toolbar.Controls.AddRange(new Control[] { _search, _btnRefresh, _btnOwn, _btnElevate });
        foreach (var button in _filters) _toolbar.Controls.Add(button);
        _footer.Controls.AddRange(new Control[] { _btnToggle, _btnDelete, _btnRestore, _btnAdd, _btnLocate });

        // Dock 逆序：Fill 最先加入，Bottom / Top 依次占位
        Controls.Add(_list);
        Controls.Add(_footer);
        Controls.Add(_toolbar);
    }

    private static ModernButton Button(string text, int width, ButtonKind kind) => new()
    {
        Text = text, Width = S(width), Height = S(30), Kind = kind, Font = Theme.FontSmall,
    };

    /// <summary>首次显示时扫描注册表，避免启动工具箱时阻塞界面。</summary>
    public void Activate()
    {
        if (_ready) return;
        _ready = true;
        ApplyTheme();
        _ = LoadAsync();
    }

    public void ApplyTheme()
    {
        BackColor = Theme.Surface;
        _list.ApplyTheme();
        _search.ApplyTheme();
        Invalidate(true);
    }

    private async Task LoadAsync()
    {
        _busy = true;
        _statusText = "正在扫描右键菜单项…";
        _footer.Invalidate();
        try
        {
            _all = await Task.Run(ContextMenuRegistry.Scan);
            ApplyFilter();
            UpdateOwnButton();
            UpdateStatus();
        }
        finally
        {
            _busy = false;
            _footer.Invalidate();
        }
    }

    private void SetFilter(int index)
    {
        _filterIndex = index;
        for (int i = 0; i < _filters.Length; i++)
            _filters[i].Kind = i == index ? ButtonKind.Primary : ButtonKind.Subtle;
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        IEnumerable<ContextMenuEntry> query = _all;
        query = _filterIndex switch
        {
            1 => query.Where(e => e.Enabled),
            2 => query.Where(e => !e.Enabled),
            3 => query.Where(e => e.IsOwn),
            _ => query,
        };
        string keyword = _search.Query.Trim().ToLowerInvariant();
        if (keyword.Length > 0) query = query.Where(e => e.SearchText.Contains(keyword, StringComparison.Ordinal));
        _list.SetItems(query.ToList());
        UpdateButtons();
        UpdateStatus();
    }

    private void UpdateOwnButton()
    {
        int count = ContextMenuRegistry.OwnInstalledCount;
        _btnOwn.Text = count > 0 ? $"卸载本工具菜单 ({count})" : "安装本工具菜单";
        _btnOwn.Kind = count > 0 ? ButtonKind.Subtle : ButtonKind.Primary;
        _btnOwn.Invalidate();
    }

    private void UpdateButtons()
    {
        int count = _list.SelectedCount;
        var items = _list.SelectedItems;
        _btnToggle.Enabled = !_busy && count > 0;
        _btnToggle.Text = count > 0 && items.All(e => !e.Enabled) ? "启用所选" : "禁用所选";
        _btnToggle.Invalidate();
        _btnDelete.Enabled = !_busy && count > 0;
        _btnLocate.Enabled = count == 1;
        _btnRestore.Enabled = ContextMenuRegistry.ReadBackups().Count > 0;
        _btnAdd.Enabled = !_busy;
    }

    private void UpdateStatus()
    {
        int backups = ContextMenuRegistry.ReadBackups().Count;
        string scope = ContextMenuRegistry.IsElevated ? "管理员" : "普通权限";
        _statusText = $"共 {_all.Count} 项  ·  已选 {_list.SelectedCount} 项  ·  备份 {backups} 份  ·  {scope}";
        _footer.Invalidate();
    }

    private async Task ToggleOne(ContextMenuEntry entry) => await ApplyEnabled(new[] { entry }, !entry.Enabled);

    private void ToggleSelected() =>
        _ = ApplyEnabled(_list.SelectedItems, !_list.SelectedItems.All(e => !e.Enabled));

    private async Task ApplyEnabled(IReadOnlyList<ContextMenuEntry> entries, bool enabled)
    {
        if (entries.Count == 0) return;
        string message = await Task.Run(() =>
        {
            var errors = new List<string>();
            foreach (var entry in entries)
            {
                string error = ContextMenuRegistry.SetEnabled(entry, enabled);
                if (error.Length > 0) errors.Add($"{entry.Caption}：{error}");
            }
            return string.Join("\n", errors.Take(3));
        });
        if (message.Length > 0) MessageBox.Show(message, "右键菜单管理", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        await LoadAsync();
    }

    private void DeleteSelected() => DeleteCore(_list.SelectedItems);
    private void DeleteOne(ContextMenuEntry entry) => DeleteCore(new[] { entry });

    private void DeleteCore(IReadOnlyList<ContextMenuEntry> items)
    {
        if (items.Count == 0) return;
        var lines = items.Take(5).Select(e => $"{e.Caption}   ·   {e.MountLabel}   ·   {e.ScopeText}").ToList();
        if (items.Count > 5) lines.Add($"… 以及另外 {items.Count - 5} 项");
        using var dialog = new ActionDialog("删除右键菜单项",
            "删除前会把整项注册表内容自动导出到备份文件，可随时用「恢复备份」还原。系统内置项不允许删除。",
            lines, "删除并备份", danger: true);
        if (dialog.ShowDialog(this) != DialogResult.OK || !dialog.Confirmed) return;

        var errors = new List<string>();
        foreach (var entry in items)
        {
            string error = ContextMenuRegistry.Delete(entry);
            if (error.Length > 0) errors.Add($"{entry.Caption}：{error}");
        }
        if (errors.Count > 0) MessageBox.Show(string.Join("\n", errors.Take(3)), "右键菜单管理",
            MessageBoxButtons.OK, MessageBoxIcon.Warning);
        _ = LoadAsync();
    }

    private void RestoreBackup()
    {
        string result = ContextMenuRegistry.RestoreLast();
        _statusText = result;
        _footer.Invalidate();
        _ = LoadAsync();
    }

    private void AddCustom()
    {
        using var dialog = new AddMenuDialog();
        if (dialog.ShowDialog(this) != DialogResult.OK || !dialog.Confirmed) return;
        string error = ContextMenuRegistry.AddCustom(
            dialog.Caption, dialog.Command, dialog.MountSubKey, dialog.IconPath);
        if (error.Length > 0) MessageBox.Show(error, "新增右键菜单", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        _ = LoadAsync();
    }

    private void LocateSelected()
    {
        var entry = _list.SelectedItems.FirstOrDefault();
        if (entry is not null) ContextMenuRegistry.Locate(entry);
    }

    private void ToggleOwnMenu()
    {
        string result = ContextMenuRegistry.OwnInstalledCount > 0
            ? ContextMenuRegistry.UninstallOwn()
            : ContextMenuRegistry.InstallOwn();
        _statusText = result;
        _footer.Invalidate();
        _ = LoadAsync();
    }

    private static void RelaunchElevated()
    {
        try
        {
            Process.Start(new ProcessStartInfo(Application.ExecutablePath)
            {
                UseShellExecute = true, Verb = "runas",
            });
            Application.Exit();
        }
        catch { /* 用户取消 UAC 时保持当前进程 */ }
    }

    private void ShowRowMenu(ContextMenuEntry entry, Point at)
    {
        var menu = new ContextMenuStrip { Renderer = new DarkMenuRenderer(), ShowImageMargin = false };
        AddItem(menu, entry.Enabled ? "禁用此项" : "启用此项", () => _ = ApplyEnabled(new[] { entry }, !entry.Enabled));
        AddItem(menu, "复制执行命令", () =>
        {
            if (!string.IsNullOrEmpty(entry.Command)) Clipboard.SetText(entry.Command);
        }, !string.IsNullOrEmpty(entry.Command));
        AddItem(menu, "复制注册表路径", () => Clipboard.SetText(entry.RegistryPath));
        AddItem(menu, "在注册表编辑器中定位", () => ContextMenuRegistry.Locate(entry));
        AddItem(menu, "删除并备份", () => DeleteOne(entry), !entry.IsBuiltIn);
        menu.Show(_list, at);
    }

    private static void AddItem(ContextMenuStrip menu, string text, Action action, bool enabled = true)
    {
        var item = new ToolStripMenuItem(text)
        {
            Enabled = enabled, ForeColor = Theme.Text, BackColor = Theme.Elevated, Height = 28,
        };
        item.Click += (_, _) => action();
        menu.Items.Add(item);
    }

    private void PaintToolbar(object sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.Surface);
        int hintRight = _btnElevate.Visible ? _btnElevate.Left : _btnOwn.Left;
        Theme.DrawText(g, "管理资源管理器右键菜单  ·  修改即时生效，重启资源管理器可刷新显示",
            Theme.FontSmall, Theme.TextDim,
            new Rectangle(S(20), S(46), Math.Max(S(160), hintRight - S(32)), S(30)),
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
        g.DrawLine(Theme.P(Theme.Border), 0, _toolbar.Height - 1, _toolbar.Width, _toolbar.Height - 1);
    }

    private void PaintFooter(object sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.Surface);
        g.DrawLine(Theme.P(Theme.Border), 0, 0, _footer.Width, 0);
        Theme.DrawText(g, _statusText, Theme.FontSmall, Theme.TextMuted,
            new Rectangle(S(20), 0, Math.Max(S(200), _btnToggle.Left - S(32)), _footer.Height));
        Theme.DrawText(g, "禁用只是添加 LegacyDisable 标记，可随时恢复；删除前自动备份",
            Theme.FontSmall, Theme.TextDim, new Rectangle(S(20), S(42), S(520), S(22)));
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        // 构造函数里设置 Font 会提前触发布局，此时按钮尚未创建
        if (_btnRefresh is null || _btnOwn is null) return;
        _search.SetBounds(S(20), S(12), Math.Min(S(300), Math.Max(S(180), _toolbar.Width / 4)), S(36));
        int x = _search.Right + S(12);
        foreach (var button in _filters)
        {
            button.SetBounds(x, S(14), button.Width, S(32));
            x += button.Width + S(6);
        }
        _btnRefresh.SetBounds(_toolbar.Width - S(56), S(14), S(36), S(32));
        // 功能按钮放第二行，避免内容区变窄后与第一行的筛选按钮重叠
        _btnOwn.SetBounds(_toolbar.Width - S(20) - S(128), S(46), S(128), S(30));
        _btnElevate.SetBounds(_btnOwn.Left - S(140), S(46), S(128), S(30));
        _btnElevate.Visible = !ContextMenuRegistry.IsElevated;

        int right = _footer.Width - S(20);
        foreach (var button in new[] { _btnLocate, _btnAdd, _btnRestore, _btnDelete, _btnToggle })
        {
            right -= button.Width;
            button.SetBounds(right, S(20), button.Width, S(34));
            right -= S(8);
        }
    }
}
