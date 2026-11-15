using System.Drawing;
using System.Windows.Forms;
using Toolbox.Core;

namespace Toolbox.UI;

/// <summary>设置面板：开机自启、托盘行为、右键菜单集成。改动即时生效并写入配置。</summary>
internal sealed class SettingsDialog : Form
{
    private static int S(int value) => Theme.S(value);

    private readonly ModernToggle _startup = new() { Label = "开机自动启动" };
    private readonly ModernToggle _startMin = new() { Label = "开机自启时直接驻留托盘" };
    private readonly ModernToggle _trayMin = new() { Label = "最小化时隐藏到系统托盘" };
    private readonly ModernToggle _trayClose = new() { Label = "关闭窗口时隐藏到托盘（不退出程序）" };
    private readonly ModernToggle _integration = new() { Label = "在右键菜单显示「快速卸载软件」" };

    private bool _loading = true;
    private string _note = "";
    private string _error = "";

    public SettingsDialog()
    {
        Width = S(560);
        Height = S(452);
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Theme.Window;
        ShowInTaskbar = false;
        Font = Theme.FontBase;
        MinimizeBox = MaximizeBox = false;
        KeyPreview = true;

        AddToggle(_startup, S(96));
        AddToggle(_startMin, S(126));
        AddToggle(_trayMin, S(214));
        AddToggle(_trayClose, S(244));
        AddToggle(_integration, S(332));

        var close = new ModernButton
        {
            Text = "完成", Kind = ButtonKind.Primary,
            Width = S(104), Height = S(34),
            Left = Width - S(26) - S(104), Top = Height - S(62),
        };
        close.Click += (_, _) => Close();
        Controls.Add(close);

        MouseDown += (_, e) => { if (e.Button == MouseButtons.Left && e.Y < S(52)) NativeMethods.DragWindow(Handle); };

        LoadState();
        _loading = false;
    }

    /// <summary>
    /// 提权执行后不阻塞界面：起一个短周期定时器轮询真实注册表状态，
    /// 到位或超时后再校正开关。用 WaitForExit 会冻住界面（UAC 停留时尤其明显）。
    /// </summary>
    private void LaunchElevated(string arguments, Func<bool> done, Action after)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(StartupRegistry.ExecutablePath)
            {
                UseShellExecute = true,
                Verb = "runas",
                Arguments = arguments,
            });
        }
        catch
        {
            _error = "已取消管理员授权";
            after();
            return;
        }

        int attempts = 0;
        var timer = new System.Windows.Forms.Timer { Interval = 500 };
        timer.Tick += (_, _) =>
        {
            attempts++;
            if (done() || attempts >= 20)
            {
                timer.Stop();
                timer.Dispose();
                after();
            }
        };
        timer.Start();
    }

    private void EnableIntegration()
    {
        if (ContextMenuRegistry.IsElevated)
        {
            ContextMenuRegistry.InstallOwn();
            return;
        }
        LaunchElevated("--install-menu --silent",
            () => ContextMenuRegistry.OwnFullyInstalled,
            () =>
            {
                if (!ContextMenuRegistry.OwnFullyInstalled && _error.Length == 0)
                    _error = "右键菜单未安装（可能取消了管理员授权）";
                SyncIntegrationSwitch();
            });
    }

    private void DisableIntegration()
    {
        if (ContextMenuRegistry.IsElevated)
        {
            ContextMenuRegistry.UninstallOwn();
            return;
        }
        LaunchElevated("--uninstall-menu --silent",
            () => !ContextMenuRegistry.HasMachineScope,
            () =>
            {
                if (ContextMenuRegistry.HasMachineScope && _error.Length == 0)
                    _error = "右键菜单仍然存在（可能取消了管理员授权）";
                SyncIntegrationSwitch();
            });
    }

    private void SyncIntegrationSwitch()
    {
        _loading = true;
        _integration.Checked = ContextMenuRegistry.OwnInstalledCount > 0;
        _loading = false;
        _note = IntegrationNote();
        Invalidate();
    }

    private void AddToggle(ModernToggle toggle, int top)
    {
        toggle.Left = S(26);
        toggle.Top = top;
        toggle.Width = Width - S(52);
        toggle.Height = S(24);
        toggle.CheckedChanged += (_, _) => OnToggle(toggle);
        Controls.Add(toggle);
    }

    private void LoadState()
    {
        _startup.Checked = StartupRegistry.IsEnabled;
        _startMin.Checked = AppPrefs.StartMinimized;
        _trayMin.Checked = AppPrefs.TrayOnMinimize;
        _trayClose.Checked = AppPrefs.TrayOnClose;
        _integration.Checked = ContextMenuRegistry.OwnInstalledCount > 0;
        _note = IntegrationNote();
    }

    private string IntegrationNote()
    {
        int count = ContextMenuRegistry.OwnInstalledCount;
        if (count > 0)
            return $"已添加到 {count} 个位置。Windows 11 默认收起传统菜单，右键后点「显示更多选项」即可看到";
        return "关闭后可从右键菜单移除，随时可以再打开";
    }

    private void OnToggle(ModernToggle toggle)
    {
        if (_loading) return;
        _error = "";

        if (ReferenceEquals(toggle, _startup))
        {
            bool ok = toggle.Checked ? StartupRegistry.Enable(out string error) : StartupRegistry.Disable(out error);
            if (!ok)
            {
                _error = "设置开机启动失败：" + error;
                _loading = true;
                toggle.Checked = !toggle.Checked;   // 失败就回滚，避免显示与真实状态不一致
                _loading = false;
            }
            else if (toggle.Checked)
            {
                AppPrefs.StartMinimized = true;      // 自启时缩到托盘更合理
                _loading = true;
                _startMin.Checked = true;
                _loading = false;
            }
        }
        else if (ReferenceEquals(toggle, _startMin)) AppPrefs.StartMinimized = toggle.Checked;
        else if (ReferenceEquals(toggle, _trayMin)) AppPrefs.TrayOnMinimize = toggle.Checked;
        else if (ReferenceEquals(toggle, _trayClose)) AppPrefs.TrayOnClose = toggle.Checked;
        else if (ReferenceEquals(toggle, _integration))
        {
            _error = "";
            if (toggle.Checked) EnableIntegration();
            else DisableIntegration();
            _note = IntegrationNote();   // 真实状态由回调校正，这里给个即时反馈
        }

        Invalidate();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        NativeMethods.ApplyRoundRegion(this, S(14));
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape) { Close(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Theme.SetQuality(g);
        g.Clear(Theme.Window);
        Theme.Box(g, new Rectangle(0, 0, Width - 1, Height - 1), S(13), Theme.Window, Theme.BorderStrong);

        var logo = new Rectangle(S(24), S(20), S(34), S(34));
        Theme.Box(g, logo, S(9), Theme.AccentDim, Theme.Alpha(Theme.Accent, 90));
        GlyphPainter.Draw(g, Glyph.Gear,
            new Rectangle(logo.X + S(6), logo.Y + S(6), S(22), S(22)), Theme.Accent, 1.7f);
        Theme.DrawText(g, "设置", Theme.FontTitle, Theme.Text, new Rectangle(S(70), S(22), S(200), S(30)));
        Theme.DrawText(g, "改动即时生效，配置保存在本机", Theme.FontSmall, Theme.TextDim,
            new Rectangle(S(70), S(46), Width - S(96), S(20)));

        Section(g, "启动", S(74));
        Section(g, "窗口行为", S(190));
        Section(g, "系统集成", S(300));

        Theme.DrawText(g, _note, Theme.FontSmall, Theme.TextDim,
            new Rectangle(S(98), S(354), Width - S(124), S(20)),
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

        if (_error.Length > 0)
            Theme.DrawText(g, _error, Theme.FontSmall, Theme.Danger,
                new Rectangle(S(26), Height - S(94), Width - S(52), S(24)),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

        g.DrawLine(Theme.P(Theme.Border), S(26), Height - S(76), Width - S(26), Height - S(76));
    }

    private static void Section(Graphics g, string title, int top)
    {
        Theme.DrawText(g, title, Theme.FontSmallBold, Theme.TextMuted,
            new Rectangle(S(26), top, S(200), S(20)));
        g.DrawLine(Theme.P(Theme.Border), S(72), top + S(10), S(180), top + S(10));
    }
}
