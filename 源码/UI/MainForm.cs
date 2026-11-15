using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using Toolbox.Core;

namespace Toolbox.UI;

internal sealed partial class MainForm : Form
{
    private const string Hint = "双击卸载  ·  Ctrl+A 全选  ·  F5 刷新";

    private static readonly ThemeMode[] ThemeOrder = { ThemeMode.Light, ThemeMode.Dark, ThemeMode.Auto };
    private static readonly string[] ThemeLabels = { "亮色", "深色", "自动" };

    private readonly string _target;
    private readonly SoftwareList _list = new();
    private readonly SearchBox _search = new();
    private readonly LogPanel _log = new();
    private readonly Panel _titleBar = new BufferedPanel();
    private readonly Panel _toolbar = new BufferedPanel();
    private readonly Panel _footer = new BufferedPanel();
    private readonly Panel _logHost = new BufferedPanel();
    private readonly Panel _logHeader = new BufferedPanel();
    private readonly ModernToggle _toggleSystem = new() { Label = "显示系统组件" };
    private readonly ModernButton[] _filters = new ModernButton[4];
    private readonly ModernButton[] _themeButtons = new ModernButton[3];
    private ModernButton _btnSort, _btnRefresh, _btnLog, _btnMin, _btnMax, _btnClose;
    private ModernButton _btnFolder, _btnCopy, _btnUninstall, _btnSilent, _btnBatch;
    private readonly System.Windows.Forms.Timer _debounce = new() { Interval = 130 };
    private readonly System.Windows.Forms.Timer _spinner = new() { Interval = 220 };

    private List<SoftwareEntry> _all = new();
    private List<SoftwareEntry> _filtered = new();
    private int _filterIndex, _sortIndex, _dotCount;
    private bool _busy, _logVisible;
    private string _statusText = "", _statusOverride = "";
    private ContextMenuStrip _rowMenu;

    private readonly ResizeController _resizer;

    // 工具箱外壳：左侧导航 + 右侧内容区；卸载界面整体收进 _uninstallView
    private readonly BufferedPanel _nav = new();
    private readonly BufferedPanel _contentHost = new();
    private readonly BufferedPanel _uninstallView = new();
    private readonly ContextMenuView _contextView = new();
    private readonly ScreenshotView _screenshotView = new();
    private readonly ModernButton[] _navButtons = new ModernButton[3];
    private ModernButton _btnNavToggle;
    private ModernButton _btnSettings;
    private readonly NotifyIcon _tray = new();
    private bool _exitRequested;
    private readonly bool _startInTray;
    private readonly ToolTip _navTips = new();
    private static readonly string[] ToolNames = { "软件卸载", "右键菜单", "快捷键截屏" };
    private static readonly Glyph[] ToolGlyphs = { Glyph.Trash, Glyph.Menu, Glyph.Camera };
    private static readonly string[] ThemeShortLabels = { "亮", "深", "自" };
    private const int NavExpandedWidth = 198;
    private const int NavCollapsedWidth = 64;
    private bool _navCollapsed;
    private int _activeTool;
    private string _badgeText = "";

    public MainForm(string target, bool startInTray = false)
    {
        _target = target ?? "";
        _startInTray = startInTray;
        Text = Program.WindowTitle;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        var area = Screen.PrimaryScreen.WorkingArea;
        // 左侧导航固定占 S(196)，默认窗口相应加宽，保证表格列仍有充足空间
        int wantW = Theme.S(1320), wantH = Theme.S(800);
        Size = new Size(Math.Min(wantW, Math.Max(Theme.S(920), area.Width - Theme.S(40))),
                        Math.Min(wantH, Math.Max(Theme.S(620), area.Height - Theme.S(40))));
        MinimumSize = new Size(Theme.S(920), Theme.S(620));
        BackColor = Theme.Window;
        Font = Theme.FontBase;
        ShowInTaskbar = true;
        DoubleBuffered = true;
        SetAppIcon();

        // 控制器必须先于 WireEvents 建立：Resize 事件里要读取它的拖拽状态
        _resizer = new ResizeController(this, ResizeFinished);
        Application.AddMessageFilter(_resizer);
        FormClosed += (_, _) =>
        {
            Application.RemoveMessageFilter(_resizer);
            ScreenshotService.Shutdown();
            _tray.Visible = false;
            _tray.Dispose();
        };

        BuildControls();
        WireEvents();
        ApplyLayout();
        ApplyNavState(Settings.Get("nav.collapsed", "0") == "1");
        Theme.Changed += ApplyTheme;
        UpdateThemeButtons();
        Log("欢迎使用实用工具箱 · 正在读取已安装软件清单…");
    }

    private void SetTheme(ThemeMode mode)
    {
        Theme.Set(mode);
        Settings.SaveTheme(mode);
        Log($"主题已切换为：{mode switch { ThemeMode.Light => "亮色", ThemeMode.Dark => "深色", _ => "自动（跟随系统）" }}");
    }

    private void ApplyTheme()
    {
        BackColor = Theme.Window;
        foreach (var control in Descendants(this))
            if (control is IThemed themed) themed.ApplyTheme();
        Invalidate(true);
        UpdateThemeButtons();
    }

    private void UpdateThemeButtons()
    {
        for (int i = 0; i < _themeButtons.Length; i++)
            _themeButtons[i].Kind = ThemeOrder[i] == Theme.Mode ? ButtonKind.Primary : ButtonKind.Ghost;
    }

    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }

    /// <summary>供无界面自检输出关键布局尺寸。</summary>
    internal string DumpLayout() =>
        $"form={ClientSize.Width}x{ClientSize.Height} title={_titleBar.Bounds} nav={_nav.Bounds} " +
        $"content={_contentHost.Bounds} toolbar={_toolbar.Bounds} list={_list.Bounds} " +
        $"rowHeight={SoftwareList.RowHeight} tool={_activeTool}";

    private void SetAppIcon()
    {
        try
        {
            using var bmp = new Bitmap(32, 32);
            using (var g = Graphics.FromImage(bmp))
            {
                Theme.SetQuality(g);
                Theme.Box(g, new Rectangle(0, 0, 31, 31), 7, Theme.Accent);
                GlyphPainter.Draw(g, Glyph.Bolt, new Rectangle(5, 5, 22, 22), Theme.C("#08111B"), 2f);
            }
            Icon = System.Drawing.Icon.FromHandle(bmp.GetHicon());
        }
        catch { /* 图标不影响功能 */ }
    }

    private void BuildControls()
    {
        _list.Dock = DockStyle.Fill;

        _logHeader.Dock = DockStyle.Top;
        _logHeader.Height = Theme.S(32);
        _logHeader.Paint += (_, e) =>
        {
            e.Graphics.Clear(Theme.Surface);
            Theme.DrawText(e.Graphics, "执行日志", Theme.FontSmallBold, Theme.TextMuted,
                new Rectangle(Theme.S(18), 0, Theme.S(160), Theme.S(32)));
            e.Graphics.DrawLine(Theme.P(Theme.Border), 0, Theme.S(31), _logHeader.Width, Theme.S(31));
        };
        var clearLog = new ModernButton
        {
            Text = "清空", Kind = ButtonKind.Ghost, Width = Theme.S(60), Height = Theme.S(24), Font = Theme.FontSmall,
        };
        clearLog.Click += (_, _) => _log.ClearAll();
        _logHeader.Controls.Add(clearLog);
        _logHeader.Resize += (_, _) =>
            clearLog.SetBounds(_logHeader.Width - Theme.S(78), Theme.S(4), Theme.S(60), Theme.S(24));

        _log.Dock = DockStyle.Fill;
        _logHost.Dock = DockStyle.Bottom;
        _logHost.Height = Theme.S(152);
        _logHost.Visible = false;
        _logHost.Controls.Add(_log);
        _logHost.Controls.Add(_logHeader);

        _footer.Dock = DockStyle.Bottom;
        _footer.Height = Theme.S(76);
        _footer.Paint += PaintFooter;

        _toolbar.Dock = DockStyle.Top;
        _toolbar.Height = Theme.S(80);
        _toolbar.Paint += PaintToolbar;

        _titleBar.Dock = DockStyle.Top;
        _titleBar.Height = Theme.S(56);
        _titleBar.Paint += PaintTitle;

        _search.Width = Theme.S(340);
        _search.Height = Theme.S(36);

        string[] labels = { "全部", "本机", "当前用户", "32 位" };
        int[] widths = { 62, 62, 84, 66 };
        for (int i = 0; i < _filters.Length; i++)
        {
            int index = i;
            _filters[i] = new ModernButton
            {
                Text = labels[i], Width = Theme.S(widths[i]), Height = Theme.S(32),
                Kind = i == 0 ? ButtonKind.Primary : ButtonKind.Subtle,
                Font = Theme.FontSmall,
            };
            _filters[i].Click += (_, _) => SetFilter(index);
        }

        _btnSort = Button("名称 ↑", Glyph.Filter, 96, ButtonKind.Subtle);
        _btnRefresh = new ModernButton { Icon = Glyph.Refresh, Kind = ButtonKind.Subtle, Width = Theme.S(36), Height = Theme.S(32), Radius = 8 };
        _btnLog = Button("执行日志", Glyph.Chevron, 96, ButtonKind.Ghost);
        _toggleSystem.Width = Theme.S(156);
        _toggleSystem.Height = Theme.S(24);

        for (int i = 0; i < _themeButtons.Length; i++)
        {
            int index = i;
            _themeButtons[i] = new ModernButton
            {
                Text = ThemeLabels[i], Width = Theme.S(54), Height = Theme.S(26),
                Kind = ButtonKind.Ghost, Font = Theme.FontSmall, Radius = 7,
            };
            _themeButtons[i].Click += (_, _) => SetTheme(ThemeOrder[index]);
            _nav.Controls.Add(_themeButtons[i]); // 主题是全局设置，放在导航底部
        }

        _btnFolder = Button("打开目录", Glyph.Folder, 100, ButtonKind.Subtle);
        _btnCopy = Button("复制命令", Glyph.Copy, 100, ButtonKind.Subtle);
        _btnUninstall = Button("卸载", Glyph.Trash, 88, ButtonKind.Danger);
        _btnSilent = Button("静默卸载", Glyph.Bolt, 112, ButtonKind.Primary);
        _btnBatch = Button("批量卸载", Glyph.Check, 108, ButtonKind.Subtle);

        _btnMin = new ModernButton { Icon = Glyph.Minimize, Kind = ButtonKind.Ghost, Width = Theme.S(34), Height = Theme.S(30), Radius = 7 };
        _btnMax = new ModernButton { Icon = Glyph.Maximize, Kind = ButtonKind.Ghost, Width = Theme.S(34), Height = Theme.S(30), Radius = 7 };
        _btnClose = new ModernButton { Icon = Glyph.Close, Kind = ButtonKind.Ghost, Width = Theme.S(34), Height = Theme.S(30), Radius = 7 };

        _toolbar.Controls.AddRange(new Control[] { _search, _btnSort, _btnRefresh, _btnLog, _toggleSystem });
        foreach (var button in _filters) _toolbar.Controls.Add(button);
        _footer.Controls.AddRange(new Control[] { _btnFolder, _btnCopy, _btnUninstall, _btnSilent, _btnBatch });
        _titleBar.Controls.AddRange(new Control[] { _btnMin, _btnMax, _btnClose });

        for (int i = 0; i < _navButtons.Length; i++)
        {
            int index = i;
            _navButtons[i] = new ModernButton
            {
                Text = ToolNames[i], Icon = ToolGlyphs[i], Font = Theme.FontBase,
                Kind = i == 0 ? ButtonKind.Primary : ButtonKind.Ghost,
                Width = Theme.S(168), Height = Theme.S(44), Radius = 9,
            };
            _navButtons[i].Click += (_, _) => SelectTool(index);
            _navTips.SetToolTip(_navButtons[i], ToolNames[i]);
            _nav.Controls.Add(_navButtons[i]);
        }

        _btnNavToggle = new ModernButton
        {
            Icon = Glyph.Collapse, Kind = ButtonKind.Ghost,
            Width = Theme.S(28), Height = Theme.S(28), Radius = 7,
        };
        _btnNavToggle.Click += (_, _) => SetNavCollapsed(!_navCollapsed);
        _navTips.SetToolTip(_btnNavToggle, "收起 / 展开导航栏");
        _nav.Controls.Add(_btnNavToggle);

        _btnSettings = new ModernButton
        {
            Text = "设置", Icon = Glyph.Gear, Kind = ButtonKind.Ghost, Font = Theme.FontBase,
            Width = Theme.S(168), Height = Theme.S(30), Radius = 8,
        };
        _btnSettings.Click += (_, _) => OpenSettings();
        _navTips.SetToolTip(_btnSettings, "设置：开机启动 / 托盘 / 右键菜单集成");
        _nav.Controls.Add(_btnSettings);

        // 卸载工具内部：Fill 最先加入，随后 Bottom / Top 依次占位
        _uninstallView.Dock = DockStyle.Fill;
        _uninstallView.Controls.Add(_list);
        _uninstallView.Controls.Add(_logHost);
        _uninstallView.Controls.Add(_footer);
        _uninstallView.Controls.Add(_toolbar);

        _contextView.Dock = DockStyle.Fill;
        _screenshotView.Dock = DockStyle.Fill;
        _contextView.Visible = false;
        _screenshotView.Visible = false;
        _contentHost.Dock = DockStyle.Fill;
        _contentHost.Controls.Add(_uninstallView);
        _contentHost.Controls.Add(_contextView);
        _contentHost.Controls.Add(_screenshotView);

        _nav.Dock = DockStyle.Left;
        _nav.Width = Theme.S(NavExpandedWidth);
        _nav.Paint += PaintNav;
        _nav.DoubleClick += (_, _) => SetNavCollapsed(!_navCollapsed);   // 空白处双击也能收起

        // 窗体级 Dock：Fill 最先，Left / Top 依次占位，标题栏横跨全宽
        Controls.Add(_contentHost);
        Controls.Add(_nav);
        Controls.Add(_titleBar);
    }

    private static ModernButton Button(string text, Glyph glyph, int width, ButtonKind kind) => new()
    {
        Text = text, Icon = glyph, Width = Theme.S(width), Height = Theme.S(34), Kind = kind, Font = Theme.FontBase,
    };

    private void WireEvents()
    {
        Shown += async (_, _) =>
        {
            NativeMethods.ApplyRoundRegion(this, Theme.S(14));
            ScreenshotService.Initialize(Handle);
            SelectTool(0);
            if (ContextMenuRegistry.TryRepairInstall())
                Log("已补齐缺失的右键菜单项（可执行文件 / 快捷方式等）");
            if (_startInTray && string.IsNullOrEmpty(_target)) HideToTray();
            await LoadSoftwareAsync();
            HandleTarget();
        };
        Resize += (_, _) =>
        {
            // 最小化到托盘：直接隐藏，任务栏不再留按钮
            if (WindowState == FormWindowState.Minimized && AppPrefs.TrayOnMinimize && Visible)
            {
                HideToTray();
                return;
            }
            if (WindowState == FormWindowState.Maximized) Region = null;
            else if (!_resizer.IsResizing) NativeMethods.ApplyRoundRegion(this, Theme.S(14));
            ApplyLayout();
            // 只标记整窗（含子控件）失效，交给消息循环绘制；
            // 这里若同步 RedrawWindow(UPDATENOW) 会在布局中递归派发 WM_PAINT，导致 CPU 满载
            Invalidate(true);
        };

        _titleBar.MouseDown += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) NativeMethods.DragWindow(Handle);
        };
        _titleBar.DoubleClick += (_, _) => ToggleMaximize();
        _btnMin.Click += (_, _) => WindowState = FormWindowState.Minimized;
        _btnMax.Click += (_, _) => ToggleMaximize();
        _btnClose.Click += (_, _) => Close();   // 是否缩到托盘由 OnFormClosing 决定
        BuildTray();

        _search.QueryChanged += (_, _) => { _debounce.Stop(); _debounce.Start(); };
        _debounce.Tick += (_, _) => { _debounce.Stop(); ApplyFilter(); };
        _btnSort.Click += (_, _) =>
        {
            _sortIndex = (_sortIndex + 1) % 3;
            _btnSort.Text = new[] { "名称 ↑", "大小 ↓", "日期 ↓" }[_sortIndex];
            ApplyFilter();
        };
        _btnRefresh.Click += (_, _) => { if (!_busy) _ = LoadSoftwareAsync(); };
        _btnLog.Click += (_, _) => SetLogVisible(!_logVisible);
        _toggleSystem.CheckedChanged += (_, _) => { if (!_busy) _ = LoadSoftwareAsync(true); };
        _spinner.Tick += (_, _) =>
        {
            _dotCount = (_dotCount + 1) % 4;
            if (_busy) { _footer.Invalidate(); }
        };

        _list.SelectionChanged += (_, _) => { UpdateButtons(); _footer.Invalidate(); };
        _list.ItemDoubleClick += entry => _ = RunUninstall(new[] { entry }, UninstallMode.Interactive);
        _list.ItemEnter += entry => _ = RunUninstall(new[] { entry }, UninstallMode.Interactive);
        _list.ItemRightClick += (entry, at) => ShowRowMenu(entry, at);

        _btnUninstall.Click += (_, _) => _ = RunUninstall(_list.SelectedItems, UninstallMode.Interactive);
        _btnSilent.Click += (_, _) => _ = RunUninstall(_list.SelectedItems, UninstallMode.Silent);
        _btnBatch.Click += (_, _) => _ = RunUninstall(_list.SelectedItems, UninstallMode.Silent);
        _btnFolder.Click += (_, _) => OpenInstallFolder();
        _btnCopy.Click += (_, _) => CopyUninstallCommand();
    }

    private void ApplyLayout()
    {
        int w = _toolbar.Width;
        _search.SetBounds(Theme.S(20), Theme.S(12), Math.Min(Theme.S(340), Math.Max(Theme.S(200), w / 3)), Theme.S(36));
        int x = _search.Right + Theme.S(12);
        foreach (var button in _filters)
        {
            button.SetBounds(x, Theme.S(14), button.Width, Theme.S(32));
            x += button.Width + Theme.S(6);
        }
        _btnRefresh.SetBounds(w - Theme.S(56), Theme.S(14), Theme.S(36), Theme.S(32));
        _toggleSystem.SetBounds(Theme.S(20), Theme.S(52), Theme.S(156), Theme.S(24));
        _btnLog.SetBounds(Theme.S(186), Theme.S(49), Theme.S(96), Theme.S(28));
        _btnSort.SetBounds(Theme.S(296), Theme.S(47), Theme.S(96), Theme.S(34));

        int navPad = _navCollapsed ? Theme.S(10) : Theme.S(14);
        for (int i = 0; i < _navButtons.Length; i++)
            _navButtons[i].SetBounds(navPad, Theme.S(74) + i * Theme.S(52),
                _nav.Width - navPad * 2, Theme.S(44));

        if (_btnNavToggle is not null)
            _btnNavToggle.SetBounds(_navCollapsed ? (_nav.Width - Theme.S(28)) / 2 : _nav.Width - Theme.S(14) - Theme.S(28),
                Theme.S(16), Theme.S(28), Theme.S(28));

        if (_navCollapsed)
        {
            // 收起时主题按钮改成竖排单字，宽度只够放一个字；设置按钮只剩齿轮
            int settingsTop = _nav.Height - Theme.S(74);
            _btnSettings.SetBounds(navPad, settingsTop, _nav.Width - navPad * 2, Theme.S(30));
            int startY = settingsTop - Theme.S(12) - _themeButtons.Length * Theme.S(30);
            for (int i = 0; i < _themeButtons.Length; i++)
                _themeButtons[i].SetBounds(navPad, startY + i * Theme.S(30), _nav.Width - navPad * 2, Theme.S(26));
        }
        else
        {
            int themeTop = Math.Max(Theme.S(160), _nav.Height - Theme.S(106));
            for (int i = 0; i < _themeButtons.Length; i++)
                _themeButtons[i].SetBounds(Theme.S(14) + i * (Theme.S(54) + Theme.S(6)),
                    themeTop, Theme.S(54), Theme.S(26));
            _btnSettings.SetBounds(Theme.S(14), _nav.Height - Theme.S(70),
                _nav.Width - Theme.S(28), Theme.S(30));
        }

        int right = _footer.Width - Theme.S(20);
        _btnBatch.SetBounds(right - _btnBatch.Width, Theme.S(20), _btnBatch.Width, Theme.S(34));
        right -= _btnBatch.Width + Theme.S(8);
        _btnSilent.SetBounds(right - _btnSilent.Width, Theme.S(20), _btnSilent.Width, Theme.S(34));
        right -= _btnSilent.Width + Theme.S(8);
        _btnUninstall.SetBounds(right - _btnUninstall.Width, Theme.S(20), _btnUninstall.Width, Theme.S(34));
        right -= _btnUninstall.Width + Theme.S(8);
        _btnCopy.SetBounds(right - _btnCopy.Width, Theme.S(20), _btnCopy.Width, Theme.S(34));
        right -= _btnCopy.Width + Theme.S(8);
        _btnFolder.SetBounds(right - _btnFolder.Width, Theme.S(20), _btnFolder.Width, Theme.S(34));

        _btnClose.SetBounds(_titleBar.Width - Theme.S(20) - Theme.S(34), Theme.S(13), Theme.S(34), Theme.S(30));
        _btnMax.SetBounds(_btnClose.Left - Theme.S(38), Theme.S(13), Theme.S(34), Theme.S(30));
        _btnMin.SetBounds(_btnMax.Left - Theme.S(38), Theme.S(13), Theme.S(34), Theme.S(30));
    }

    private void PaintTitle(object sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        Theme.SetQuality(g);
        g.Clear(Theme.Surface);
        var logo = new Rectangle(Theme.S(20), Theme.S(11), Theme.S(34), Theme.S(34));
        Theme.Box(g, logo, Theme.S(9), Theme.AccentDim, Theme.Alpha(Theme.Accent, 90));
        GlyphPainter.Draw(g, Glyph.Bolt,
            new Rectangle(logo.X + Theme.S(6), logo.Y + Theme.S(6), Theme.S(22), Theme.S(22)), Theme.Accent, 1.9f);
        Theme.DrawText(g, "实用工具箱", Theme.FontLogo, Theme.Text,
            new Rectangle(Theme.S(64), Theme.S(5), Theme.S(180), Theme.S(26)));
        Theme.DrawText(g, "卸载 · 右键菜单 · 快捷键截屏", Theme.FontSmall, Theme.TextDim,
            new Rectangle(Theme.S(64), Theme.S(30), Theme.S(420), Theme.S(20)));

        string badge = _badgeText;
        int badgeW = Theme.S(120);
        var badgeRect = new Rectangle(_btnMin.Left - badgeW - Theme.S(16), Theme.S(14), badgeW, Theme.S(28));
        Theme.Box(g, badgeRect, Theme.S(14), Theme.Elevated, Theme.Border);
        Theme.DrawText(g, badge, Theme.FontSmall, Theme.TextMuted, badgeRect,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        g.DrawLine(Theme.P(Theme.Border), 0, _titleBar.Height - 1, _titleBar.Width, _titleBar.Height - 1);
    }

    private void PaintToolbar(object sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.Surface);
        Theme.DrawText(g, Hint, Theme.FontSmall, Theme.TextDim,
            new Rectangle(Theme.S(410), Theme.S(50), Math.Max(Theme.S(120), _toolbar.Width - Theme.S(430)), Theme.S(26)),
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
        g.DrawLine(Theme.P(Theme.Border), 0, _toolbar.Height - 1, _toolbar.Width, _toolbar.Height - 1);
    }

    private void PaintFooter(object sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.Surface);
        g.DrawLine(Theme.P(Theme.Border), 0, 0, _footer.Width, 0);
        string text = _statusOverride.Length > 0 ? _statusOverride : _statusText;
        if (_busy) text += new string('.', _dotCount + 1);
        Theme.DrawText(g, text, Theme.FontSmall, _busy ? Theme.Accent : Theme.TextMuted,
            new Rectangle(Theme.S(20), 0, Math.Max(Theme.S(200), _btnFolder.Left - Theme.S(32)), _footer.Height));
        Theme.DrawText(g, "卸载前请确认软件名称，重要数据请提前备份", Theme.FontSmall, Theme.TextDim,
            new Rectangle(Theme.S(20), Theme.S(42), Theme.S(400), Theme.S(22)));
    }

    private void PaintNav(object sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.SurfaceAlt);
        if (!_navCollapsed)
            Theme.DrawText(g, "工具", Theme.FontSmallBold, Theme.TextDim,
                new Rectangle(Theme.S(20), Theme.S(22), Theme.S(120), Theme.S(26)));

        int labelTop = Math.Max(Theme.S(100), _nav.Height - Theme.S(126));
        int versionTop = Math.Max(Theme.S(190), _nav.Height - Theme.S(32));
        int lineY = Math.Max(Theme.S(80), _nav.Height - Theme.S(136));
        if (!_navCollapsed)
        {
            Theme.DrawText(g, "外观", Theme.FontSmallBold, Theme.TextDim,
                new Rectangle(Theme.S(20), labelTop, Theme.S(160), Theme.S(26)));
            Theme.DrawText(g, "实用工具箱  ·  3 个工具", Theme.FontSmall, Theme.TextDim,
                new Rectangle(Theme.S(14), versionTop, _nav.Width - Theme.S(28), Theme.S(22)),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
        g.DrawLine(Theme.P(Theme.Border), _nav.Width - 1, 0, _nav.Width - 1, _nav.Height);
        if (!_navCollapsed)
            g.DrawLine(Theme.P(Theme.Border), Theme.S(14), lineY, _nav.Width - Theme.S(14), lineY);
    }

    /// <summary>收起/展开左侧导航：收起后只留图标，宽度收窄，状态记入配置。</summary>
    private void SetNavCollapsed(bool collapsed)
    {
        if (_navCollapsed == collapsed) return;
        Settings.Set("nav.collapsed", collapsed ? "1" : "0");
        ApplyNavState(collapsed);
    }

    private void ApplyNavState(bool collapsed)
    {
        _navCollapsed = collapsed;
        _nav.Width = Theme.S(collapsed ? NavCollapsedWidth : NavExpandedWidth);
        if (_btnNavToggle is not null) _btnNavToggle.Icon = collapsed ? Glyph.Expand : Glyph.Collapse;
        if (_btnSettings is not null) _btnSettings.Text = collapsed ? "" : "设置";
        for (int i = 0; i < _themeButtons.Length; i++)
            _themeButtons[i].Text = collapsed ? ThemeShortLabels[i] : ThemeLabels[i];
        for (int i = 0; i < _navButtons.Length; i++)
            _navButtons[i].Text = collapsed ? "" : ToolNames[i];
        ApplyLayout();
        _nav.Invalidate();
        Invalidate(true);
    }

    // ── 托盘 / 设置 ────────────────────────────────────────

    private void BuildTray()
    {
        var menu = new ContextMenuStrip
        {
            Renderer = new DarkMenuRenderer(),
            Font = Theme.FontSmall,
            ShowImageMargin = false,
        };
        menu.Items.Add("打开主界面", null, (_, _) => RestoreFromTray());
        menu.Items.Add("立即截屏", null, (_, _) => { RestoreFromTray(); ScreenshotService.OnHotkey(); });
        menu.Items.Add("创建桌面快捷方式", null, (_, _) => CreateDesktopShortcut());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("设置…", null, (_, _) => { RestoreFromTray(); OpenSettings(); });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => ExitApplication());

        _tray.Icon = BuildTrayIcon();
        _tray.Text = "实用工具箱 · 卸载 / 右键菜单 / 快捷键截屏";
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => RestoreFromTray();
        _tray.Visible = true;
    }

    /// <summary>托盘图标不跟随主题，固定用品牌蓝，避免切深色后变成一团黑。</summary>
    private static Icon BuildTrayIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            Theme.SetQuality(g);
            g.Clear(Color.Transparent);
            Theme.Box(g, new Rectangle(0, 0, 31, 31), 7, Color.FromArgb(45, 122, 240));
            GlyphPainter.Draw(g, Glyph.Bolt, new Rectangle(5, 5, 22, 22), Color.FromArgb(8, 17, 27), 2f);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    private void HideToTray()
    {
        // 只 Hide，不去改 ShowInTaskbar：那个属性一变 WinForms 会重建窗口句柄，
        // 注册在旧句柄上的全局热键就失效了。窗口隐藏后任务栏按钮本来也会消失。
        Hide();
        if (AppPrefs.TrayHintShown) return;
        AppPrefs.TrayHintShown = true;
        _tray.ShowBalloonTip(3000, "仍在后台运行",
            "已最小化到托盘，双击托盘图标可以重新打开。", ToolTipIcon.Info);
    }

    private void RestoreFromTray()
    {
        Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        NativeMethods.ForceForeground(Handle);   // 还原 + 置前一步到位，避免只显示到别的窗口后面
        if (WindowState == FormWindowState.Normal) NativeMethods.ApplyRoundRegion(this, Theme.S(14));
        Invalidate(true);
    }

    private void ExitApplication()
    {
        _exitRequested = true;
        Close();
    }

    private void OpenSettings()
    {
        using var dialog = new SettingsDialog();
        dialog.ShowDialog(this);
    }

    /// <summary>在桌面生成「实用工具箱」快捷方式（走 WScript.Shell，无需额外脚本）。</summary>
    private void CreateDesktopShortcut()
    {
        try
        {
            string exe = StartupRegistry.ExecutablePath;
            string path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "实用工具箱.lnk");
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null) { Log("创建快捷方式失败：当前系统不支持 WScript.Shell", LogLevel.Error); return; }

            dynamic shell = Activator.CreateInstance(shellType);
            dynamic link = shell.CreateShortcut(path);
            link.TargetPath = exe;
            link.WorkingDirectory = Path.GetDirectoryName(exe) ?? "";
            link.IconLocation = exe + ",0";
            link.Description = "卸载软件 · 管理右键菜单 · 快捷键截屏";
            link.Save();
            Log("已在桌面创建「实用工具箱」快捷方式", LogLevel.Ok);
        }
        catch (Exception ex) { Log("创建快捷方式失败：" + ex.Message, LogLevel.Error); }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // 点关闭按钮时按设置缩到托盘；Alt+F4 之类同样算用户主动关闭
        if (!_exitRequested && AppPrefs.TrayOnClose && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }
        base.OnFormClosing(e);
    }

    private void SelectTool(int index)
    {
        _activeTool = index;
        _uninstallView.Visible = index == 0;
        _contextView.Visible = index == 1;
        _screenshotView.Visible = index == 2;
        for (int i = 0; i < _navButtons.Length; i++)
            _navButtons[i].Kind = i == index ? ButtonKind.Primary : ButtonKind.Ghost;
        _badgeText = index switch
        {
            0 => $"{_all.Count} 款软件",
            1 => "右键菜单管理",
            _ => "全局热键截屏",
        };
        if (index == 1) _contextView.Activate();
        if (index == 2) _screenshotView.Activate();
        _contentHost.PerformLayout();
        _titleBar.Invalidate();
        Invalidate(true);
    }

    /// <summary>句柄重建时（例如系统改了窗口样式）把全局热键重新绑到新句柄上。</summary>
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (ScreenshotService.Hotkey is not null) ScreenshotService.Rebind(Handle);
    }

    /// <summary>接收全局热键；其余消息交回基类，不干预窗口边框行为。</summary>
    protected override void WndProc(ref Message m)
    {
        // 再次双击 exe：已有实例收到该消息后把自己从托盘里叫回来
        if (m.Msg == Program.WmRestoreWindow)
        {
            RestoreFromTray();
            m.Result = IntPtr.Zero;
            return;
        }
        if (m.Msg == GlobalHotkey.WmHotKey &&
            ScreenshotService.Hotkey?.Matches(m.WParam.ToInt32()) == true)
        {
            ScreenshotService.OnHotkey();
            m.Result = IntPtr.Zero;
            return;
        }
        // 为了任务栏能最小化，窗口带上了 WS_SYSMENU；但标题栏是自绘的，
        // Alt+Space 弹出的原生系统菜单与界面风格冲突，直接吞掉。
        if (m.Msg == 0x0112 && (m.WParam.ToInt32() & 0xFFF0) == 0xF100) return;
        base.WndProc(ref m);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ClassStyle |= 0x00020000; // CS_DROPSHADOW：仅保留投影，不引入系统边框

            // FormBorderStyle.None 会一并丢掉 WS_MINIMIZEBOX / WS_SYSMENU，
            // 任务栏据此认为窗口「不可最小化」，于是点击任务栏图标不会缩回。
            // 这两个样式只影响系统命令，不会画出标题栏（那取决于 WS_CAPTION）。
            const int WsMinimizeBox = 0x00020000, WsSysMenu = 0x00080000;
            cp.Style |= WsMinimizeBox | WsSysMenu;
            return cp;
        }
    }

    /// <summary>拖拽结束时恢复圆角，并做最后一次整窗重绘。</summary>
    private void ResizeFinished()
    {
        if (WindowState == FormWindowState.Normal)
            NativeMethods.ApplyRoundRegion(this, Theme.S(14));
        ApplyLayout();
        NativeMethods.ForceFullRedraw(this);
    }

    private void ToggleMaximize()
    {
        WindowState = WindowState == FormWindowState.Maximized
            ? FormWindowState.Normal : FormWindowState.Maximized;
    }

    private void SetFilter(int index)
    {
        _filterIndex = index;
        for (int i = 0; i < _filters.Length; i++)
            _filters[i].Kind = i == index ? ButtonKind.Primary : ButtonKind.Subtle;
        ApplyFilter();
    }

    private void SetLogVisible(bool visible)
    {
        _logVisible = visible;
        _logHost.Visible = visible;
        _btnLog.Kind = visible ? ButtonKind.Subtle : ButtonKind.Ghost;
        _btnLog.Invalidate();
    }

    private async Task LoadSoftwareAsync(bool quiet = false)
    {
        _statusOverride = "正在扫描已安装软件";
        _footer.Invalidate();
        bool includeSystem = _toggleSystem.Checked;
        List<SoftwareEntry> result = await Task.Run(() => RegistryScanner.Scan(includeSystem));
        _all = result;
        if (_activeTool == 0) _badgeText = $"{_all.Count} 款软件";
        _titleBar.Invalidate();
        ApplyFilter();
        _statusOverride = "";
        _footer.Invalidate();
        if (!quiet) Log($"扫描完成：共发现 {_all.Count} 款可卸载软件", LogLevel.Ok);
    }

    private void ApplyFilter()
    {
        IEnumerable<SoftwareEntry> query = _all;
        query = _filterIndex switch
        {
            1 => query.Where(e => e.Source.StartsWith("本机", StringComparison.Ordinal)),
            2 => query.Where(e => e.Source.StartsWith("当前用户", StringComparison.Ordinal)),
            3 => query.Where(e => e.Source.Contains("32位", StringComparison.Ordinal)),
            _ => query,
        };
        string keyword = _search.Query.Trim().ToLowerInvariant();
        if (keyword.Length > 0)
            query = query.Where(e => e.SearchText.Contains(keyword, StringComparison.Ordinal));
        query = _sortIndex switch
        {
            1 => query.OrderByDescending(e => e.SizeKB).ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase),
            2 => query.OrderByDescending(e => e.InstallDate).ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase),
            _ => query.OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase),
        };
        _filtered = query.ToList();
        _list.SetItems(_filtered);
        _list.ClearSelection();
        UpdateStatus();
        UpdateButtons();
    }

    private void UpdateStatus()
    {
        long total = _filtered.Sum(e => e.SizeKB);
        _statusText = $"已选 {_list.SelectedCount} 项    ·    共 {_filtered.Count} 款软件    ·    占用 {FormatSize(total)}";
    }

    private void UpdateButtons()
    {
        int count = _list.SelectedCount;
        var single = count == 1 ? _list.SelectedItems[0] : null;
        _btnUninstall.Enabled = !_busy && count > 0;
        _btnSilent.Enabled = !_busy && count > 0;
        _btnBatch.Enabled = !_busy && count > 1;
        _btnCopy.Enabled = !_busy && single is not null;
        _btnFolder.Enabled = !_busy && single is not null &&
                             !string.IsNullOrWhiteSpace(single.InstallLocation) &&
                             Directory.Exists(single.InstallLocation);
        _btnRefresh.Enabled = !_busy;
        UpdateStatus();
        _footer.Invalidate();
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        if (busy) { _dotCount = 0; _spinner.Start(); SetLogVisible(true); }
        else { _spinner.Stop(); _statusOverride = ""; }
        UpdateButtons();
    }

    private async Task RunUninstall(IReadOnlyList<SoftwareEntry> entries, UninstallMode mode)
    {
        if (_busy || entries.Count == 0) return;
        if (!ConfirmUninstall(entries, mode, out bool autoClean)) return;

        SetBusy(true);
        int succeeded = 0, failed = 0;
        foreach (var entry in entries)
        {
            _statusOverride = $"正在卸载 {Truncate(entry.Name, 26)}";
            _footer.Invalidate();

            var plan = UninstallEngine.BuildPlan(entry, mode);
            Log($"▶ {entry.Name}   [{plan.KindText} · {(plan.IsSilent ? "静默" : "卸载向导")}]");
            if (!string.IsNullOrEmpty(plan.Note)) Log("   " + plan.Note, LogLevel.Warn);

            var progress = new Progress<string>(message => Log("   " + message));
            var result = await UninstallEngine.ExecuteAsync(
                entry, plan, TimeSpan.FromSeconds(mode == UninstallMode.Silent ? 75 : 25), progress);

            if (!string.IsNullOrEmpty(result.Error))
            {
                failed++;
                Log($"✖ 启动失败：{result.Error}", LogLevel.Error);
            }
            else if (result.Removed)
            {
                succeeded++;
                Log($"✔ {entry.Name} 已卸载", LogLevel.Ok);
                if (autoClean) OfferCleanup(entry);
            }
            else
            {
                Log($"… 未检测到卸载完成（可能仍在等待向导确认），稍后可点刷新", LogLevel.Warn);
            }
        }

        SetBusy(false);
        Log($"本次完成 {succeeded} 款，失败 {failed} 款", succeeded > 0 ? LogLevel.Ok : LogLevel.Info);
        await LoadSoftwareAsync(true);
    }

    private bool ConfirmUninstall(IReadOnlyList<SoftwareEntry> entries, UninstallMode mode, out bool autoClean)
    {
        autoClean = false;
        string subtitle = mode == UninstallMode.Silent
            ? "将尝试静默卸载下列软件，不弹出卸载向导。少数程序仍会请求确认，请在弹窗中完成操作。"
            : "将打开下列软件自带的卸载向导，请在向导中确认卸载。";
        var items = entries.Take(5).Select(e => $"{e.Name}    ·    {e.SizeText}    ·    {e.Source}").ToList();
        if (entries.Count > 5) items.Add($"… 以及另外 {entries.Count - 5} 款软件");

        using var dialog = new ActionDialog("确认卸载", subtitle, items, "开始卸载",
            new[] { "卸载完成后自动检查残留" }, danger: true);
        bool ok = dialog.ShowDialog(this) == DialogResult.OK && dialog.Confirmed;
        autoClean = ok && dialog.GetOption(0);
        return ok;
    }

    private void OfferCleanup(SoftwareEntry entry)
    {
        var residue = UninstallEngine.Inspect(entry);
        if (!residue.Any) { Log($"   {entry.Name} 无残留", LogLevel.Ok); return; }

        var items = new List<string>();
        var options = new List<string>();
        if (residue.HasRegistryKey) { items.Add("注册表  " + entry.SubKeyPath); options.Add("删除残留的注册表项"); }
        if (residue.HasFolder) { items.Add("目录  " + residue.Folder); options.Add("把残留目录移入回收站（可恢复）"); }

        using var dialog = new ActionDialog("发现卸载残留",
            "以下内容在卸载后仍然存在。勾选需要清理的项目，目录会送入回收站而非直接删除。",
            items, "清理所选", options, danger: false);
        if (dialog.ShowDialog(this) != DialogResult.OK || !dialog.Confirmed) return;

        bool removeKey = residue.HasRegistryKey && dialog.GetOption(0);
        bool removeFolder = residue.HasFolder && dialog.GetOption(residue.HasRegistryKey ? 1 : 0);
        if (!removeKey && !removeFolder) return;
        Log("   " + UninstallEngine.Cleanup(entry, removeKey, removeFolder), LogLevel.Ok);
    }

    private void OpenInstallFolder()
    {
        var entry = _list.SelectedItems.FirstOrDefault();
        if (entry is null || string.IsNullOrWhiteSpace(entry.InstallLocation) ||
            !Directory.Exists(entry.InstallLocation)) return;
        try { Process.Start("explorer.exe", entry.InstallLocation); }
        catch (Exception ex) { Log($"打开目录失败：{ex.Message}", LogLevel.Error); }
    }

    private void CopyUninstallCommand()
    {
        var entry = _list.SelectedItems.FirstOrDefault();
        if (entry is null) return;
        var plan = UninstallEngine.BuildPlan(entry, UninstallMode.Silent);
        Clipboard.SetText(plan.FullCommand);
        Log($"已复制静默卸载命令：{entry.Name}", LogLevel.Ok);
    }

    private void HandleTarget()
    {
        if (string.IsNullOrEmpty(_target)) return;
        string resolved = ShortcutResolver.Resolve(_target);
        var match = ShortcutResolver.Match(resolved, _all);
        string hint = Path.GetFileNameWithoutExtension(_target.Trim('"'));

        if (match is null)
        {
            _search.SetQuery(hint);
            Log($"未匹配到「{hint}」的安装记录，已按名称筛选，请手动选择", LogLevel.Warn);
            SetLogVisible(true);
            return;
        }
        _list.SelectByEntry(match);
        Log($"右键目标已匹配 → {match.Name}", LogLevel.Ok);
        _ = RunUninstall(new[] { match }, UninstallMode.Interactive);
    }

    private void ShowRowMenu(SoftwareEntry entry, Point at)
    {
        _rowMenu?.Dispose();
        _rowMenu = new ContextMenuStrip
        {
            Renderer = new DarkMenuRenderer(),
            ShowImageMargin = true,
            Padding = new Padding(2, 4, 2, 4),
        };
        AddMenuItem(_rowMenu, "卸载此软件", Glyph.Trash,
            () => _ = RunUninstall(new[] { entry }, UninstallMode.Interactive));
        AddMenuItem(_rowMenu, "静默卸载", Glyph.Bolt,
            () => _ = RunUninstall(new[] { entry }, UninstallMode.Silent));
        _rowMenu.Items.Add(new ToolStripSeparator());
        AddMenuItem(_rowMenu, "打开安装目录", Glyph.Folder, () =>
        {
            _list.SelectByEntry(entry); OpenInstallFolder();
        }, !string.IsNullOrWhiteSpace(entry.InstallLocation) && Directory.Exists(entry.InstallLocation));
        AddMenuItem(_rowMenu, "复制静默卸载命令", Glyph.Copy, () =>
        {
            _list.SelectByEntry(entry); CopyUninstallCommand();
        });
        AddMenuItem(_rowMenu, "复制注册表路径", Glyph.Registry, () =>
        {
            Clipboard.SetText((entry.Hive == Microsoft.Win32.RegistryHive.LocalMachine ? "HKLM" : "HKCU") +
                              "\\" + entry.SubKeyPath);
            Log("已复制注册表路径", LogLevel.Ok);
        });
        _rowMenu.Items.Add(new ToolStripSeparator());
        AddMenuItem(_rowMenu, "检查卸载残留", Glyph.Search, () => OfferCleanup(entry));
        _rowMenu.Show(_list, at);
    }

    private static void AddMenuItem(ContextMenuStrip menu, string text, Glyph glyph, Action action, bool enabled = true)
    {
        var item = new ToolStripMenuItem(text)
        {
            Enabled = enabled,
            Image = GlyphBitmap(glyph),
            ImageScaling = ToolStripItemImageScaling.None,
            ForeColor = Theme.Text,
            BackColor = Theme.Elevated,
            Height = 28,
        };
        item.Click += (_, _) => action();
        menu.Items.Add(item);
    }

    private static Bitmap GlyphBitmap(Glyph glyph)
    {
        var bitmap = new Bitmap(18, 18);
        using (var g = Graphics.FromImage(bitmap))
        {
            Theme.SetQuality(g);
            GlyphPainter.Draw(g, glyph, new Rectangle(1, 1, 16, 16), Theme.TextMuted, 1.5f);
        }
        return bitmap;
    }

    private void Log(string message, LogLevel level = LogLevel.Info) => _log.Append(message, level);

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value.Substring(0, max) + "…";

    private static string FormatSize(long kb) => kb <= 0 ? "0 MB"
        : kb >= 1024 * 1024 ? $"{kb / 1024f / 1024f:0.##} GB"
        : kb >= 1024 ? $"{kb / 1024f:0.#} MB"
        : $"{kb} KB";

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        // 卸载工具的快捷键只在对应页面生效，避免抢走其它工具输入框里的按键
        if (_activeTool != 0) return base.ProcessCmdKey(ref msg, keyData);
        switch (keyData)
        {
            case Keys.F5:
                if (!_busy) _ = LoadSoftwareAsync();
                return true;
            case Keys.Control | Keys.F:
            case Keys.OemQuestion:
            case Keys.Divide:
                _search.FocusInput();
                return true;
            case Keys.Control | Keys.A:
                _list.SelectAll();
                return true;
            case Keys.Escape:
                if (_search.Query.Length > 0) { _search.SetQuery(""); _search.FocusInput(); }
                else Close();
                return true;
            case Keys.Delete:
                if (!_busy && _list.SelectedCount > 0)
                    _ = RunUninstall(_list.SelectedItems, UninstallMode.Interactive);
                return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }
}
