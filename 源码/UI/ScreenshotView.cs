using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using Toolbox.Core;

namespace Toolbox.UI;

/// <summary>工具页：快捷键截屏。热键设置、框选/全屏、保存目录、剪贴板与历史预览。</summary>
internal sealed class ScreenshotView : UserControl, IThemed
{
    private static int S(int value) => Theme.S(value);

    private readonly BufferedPanel _toolbar = new();
    private readonly BufferedPanel _footer = new();
    private readonly BufferedPanel _previewHost = new();
    private readonly BufferedPanel _historyHost = new();
    private readonly BufferedPanel _historyHeader = new();
    private readonly BufferedPanel _historyActions = new();
    private readonly PreviewPanel _preview = new();
    private readonly ShotListBox _history = new();
    private readonly ModernButton _btnDelete, _btnClear;
    private string _statusText = "";

    private readonly HotkeyBox _hotkeyBox = new();
    private readonly TextBox _folderInput = new();
    private readonly ModernButton _btnPick, _btnCapture, _btnFolder, _btnRefresh, _btnOpen;
    private readonly ModernToggle _toggleEnabled, _toggleFull, _toggleClipboard;
    private bool _ready;
    private bool _suppressPreview;
    private readonly AsyncImageLoader _previewLoader = new();

    public ScreenshotView()
    {
        Dock = DockStyle.Fill;
        BackColor = Theme.Surface;
        Font = Theme.FontBase;
        DoubleBuffered = true;

        _toolbar.Dock = DockStyle.Top;
        _toolbar.Height = S(156);
        _toolbar.Paint += PaintToolbar;

        _footer.Dock = DockStyle.Bottom;
        _footer.Height = S(76);
        _footer.Paint += PaintFooter;

        _historyHost.Dock = DockStyle.Left;
        _historyHost.Width = S(420);
        _historyHost.Paint += PaintHistory;

        _historyHeader.Dock = DockStyle.Top;
        _historyHeader.Height = S(30);
        _historyHeader.Paint += PaintHistoryHeader;
        _historyHeader.MouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            if (ShotListBox.CheckBox(HistoryHeaderRow()).Contains(e.Location)) _history.ToggleAll();
        };

        _historyActions.Dock = DockStyle.Bottom;
        _historyActions.Height = S(48);
        _historyActions.Paint += (_, e) =>
            e.Graphics.DrawLine(Theme.P(Theme.Border), 0, 0, _historyActions.Width, 0);
        _historyActions.Resize += (_, _) => LayoutHistoryActions();

        _history.Dock = DockStyle.Fill;
        _history.Visible = false;
        _history.SelectedIndexChanged += (_, _) =>
        {
            if (_suppressPreview) return;   // 刷新列表时会统一刷一次，避免同一张图解码两遍
            ShowPreview();
            UpdateHistoryButtons();
        };
        _history.DoubleClick += (_, _) => OpenViewer();
        _history.DeleteRequested += (_, _) => DeleteSelected();
        _history.CheckChanged += (_, _) => { UpdateHistoryButtons(); _historyHeader.Invalidate(); };
        _history.ContextMenuStrip = BuildHistoryMenu();
        _history.MouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Right) return;
            int index = _history.IndexFromPoint(e.Location);
            if (index < 0 || _history.GetSelected(index)) return;
            _history.ClearSelected();
            _history.SetSelected(index, true);
        };

        _btnDelete = SmallButton("删除所选", 110);
        _btnClear = SmallButton("清空", 68);
        _btnDelete.Click += (_, _) => DeleteSelected();
        _btnClear.Click += (_, _) => ClearAll();
        _btnDelete.Enabled = false;
        _btnClear.Enabled = false;
        _historyActions.Controls.AddRange(new Control[] { _btnDelete, _btnClear });

        // Dock 逆序：Fill 最先加入；Top / Bottom 要跨全宽，必须在它们之后加入
        _historyHost.Controls.Add(_history);
        _historyHost.Controls.Add(_historyActions);
        _historyHost.Controls.Add(_historyHeader);

        _preview.Dock = DockStyle.Fill;
        _preview.Cursor = Cursors.Hand;
        _preview.Click += (_, _) => OpenViewer();
        _previewHost.Dock = DockStyle.Fill;
        _previewHost.Controls.Add(_preview);
        _previewHost.Paint += (_, e) =>
            e.Graphics.DrawLine(Theme.P(Theme.Border), 0, 0, 0, _previewHost.Height);

        _folderInput.Font = Theme.FontSmall;
        _folderInput.BorderStyle = BorderStyle.FixedSingle;
        _folderInput.ReadOnly = true;

        _hotkeyBox.Width = S(220);
        _hotkeyBox.Height = S(30);
        _hotkeyBox.Captured += (_, text) => ApplyCapturedHotkey(text);

        _btnPick = SmallButton("选择…", 70);
        _btnPick.Click += (_, _) => PickFolder();

        _toggleEnabled = SmallToggle("启用热键");
        _toggleFull = SmallToggle("全屏截图（不框选）");
        _toggleClipboard = SmallToggle("自动复制剪贴板");
        _toggleEnabled.CheckedChanged += (_, _) => { if (!_ready) return; ScreenshotService.Enabled = _toggleEnabled.Checked; ScreenshotService.Register(); };
        _toggleFull.CheckedChanged += (_, _) => { if (_ready) ScreenshotService.FullScreen = _toggleFull.Checked; };
        _toggleClipboard.CheckedChanged += (_, _) => { if (_ready) ScreenshotService.CopyToClipboard = _toggleClipboard.Checked; };

        _btnCapture = SmallButton("立即截屏", 108);
        _btnFolder = SmallButton("打开目录", 100);
        _btnRefresh = SmallButton("刷新历史", 100);
        _btnOpen = SmallButton("放大预览", 100);
        _btnCapture.Kind = ButtonKind.Primary;
        _btnCapture.Icon = Glyph.Camera;
        _btnCapture.Click += (_, _) => ScreenshotService.OnHotkey();
        _btnFolder.Click += (_, _) => OpenFolder();
        _btnRefresh.Click += (_, _) => RefreshHistory();
        _btnOpen.Click += (_, _) => OpenViewer();

        _toolbar.Controls.AddRange(new Control[]
        {
            _hotkeyBox, _toggleEnabled, _folderInput, _btnPick,
            _toggleFull, _toggleClipboard,
        });
        _footer.Controls.AddRange(new Control[] { _btnCapture, _btnFolder, _btnRefresh, _btnOpen });

        Controls.Add(_previewHost);
        Controls.Add(_historyHost);
        Controls.Add(_footer);
        Controls.Add(_toolbar);

        ScreenshotService.Saved += OnSaved;
        ScreenshotService.StatusChanged += UpdateStatus;
        Disposed += (_, _) =>
        {
            ScreenshotService.Saved -= OnSaved;
            ScreenshotService.StatusChanged -= UpdateStatus;
        };
    }

    private static ModernButton SmallButton(string text, int width) => new()
    {
        Text = text, Width = S(width), Height = S(30), Kind = ButtonKind.Subtle, Font = Theme.FontSmall,
    };

    private static ModernToggle SmallToggle(string label) => new()
    {
        Label = label, Width = S(200), Height = S(24),
    };

    /// <summary>首次显示时读取配置并同步热键状态。</summary>
    public void Activate()
    {
        if (_ready) return;
        _ready = true;
        // 首次进入就建好目录：否则历史区与「打开目录」都会指向一个不存在的路径
        try { Directory.CreateDirectory(ScreenshotService.Folder); } catch { /* 路径不可用时忽略 */ }
        _hotkeyBox.Value = ScreenshotService.HotkeyText;
        _folderInput.Text = ScreenshotService.Folder;
        _toggleEnabled.Checked = ScreenshotService.Enabled;
        _toggleFull.Checked = ScreenshotService.FullScreen;
        _toggleClipboard.Checked = ScreenshotService.CopyToClipboard;
        ApplyTheme();
        RefreshHistory();
        UpdateStatus();
    }

    public void ApplyTheme()
    {
        BackColor = Theme.Surface;
        _history.ApplyTheme();
        _preview.Invalidate();
        _historyHost.Invalidate();
        _historyHeader.Invalidate();
        _previewHost.Invalidate();
        foreach (var box in new[] { _folderInput })
        {
            box.BackColor = Theme.Elevated;
            box.ForeColor = Theme.Text;
        }
        _hotkeyBox.ApplyTheme();
        Invalidate(true);
    }

    // ── 历史列表 ────────────────────────────────────────────

    private ContextMenuStrip BuildHistoryMenu()
    {
        var menu = new ContextMenuStrip { Font = Theme.FontSmall, ShowImageMargin = false };
        menu.Items.Add("放大预览 / 识别文字", null, (_, _) => OpenViewer());
        menu.Items.Add("用系统程序打开", null, (_, _) => OpenSelectedExternal());
        menu.Items.Add("打开所在文件夹", null, (_, _) => OpenFolder());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("勾选本行", null, (_, _) => { if (_history.SelectedIndex >= 0) _history.ToggleAt(_history.SelectedIndex); });
        menu.Items.Add("全选 / 取消全选", null, (_, _) => _history.ToggleAll());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("删除勾选  (Delete)", null, (_, _) => DeleteSelected());
        menu.Items.Add("清空全部", null, (_, _) => ClearAll());
        return menu;
    }

    private void DeleteSelected()
    {
        // 勾选的优先；没有勾选就退回当前行，避免「点了没反应」
        var files = _history.CheckedFiles();
        if (files.Count == 0) files = _history.SelectedItems.Cast<FileInfo>().ToList();
        if (files.Count == 0)
        {
            SetStatus("请先勾选要删除的截图");
            return;
        }
        int removed = ScreenshotService.Delete(files);
        RefreshHistory();
        SetStatus($"已删除 {removed} 张截图（可在回收站还原）");
    }

    private void ClearAll()
    {
        int count = ScreenshotService.Recent(int.MaxValue).Count;
        if (count == 0) { SetStatus("历史目录里还没有截图"); return; }
        var answer = MessageBox.Show($"将把 {count} 张截图移入回收站，确定清空？", "清空截图历史",
            MessageBoxButtons.OKCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.OK) return;
        int removed = ScreenshotService.DeleteAll();
        RefreshHistory();
        SetStatus($"已清空 {removed} 张截图（可在回收站还原）");
    }

    private void SetStatus(string text)
    {
        _statusText = text;
        _footer.Invalidate();
    }

    /// <summary>重建历史列表；<paramref name="prefer"/> 指定要选中的文件（如刚保存的那张）。</summary>
    private void RefreshHistory(string prefer = null)
    {
        if (IsDisposed) return;
        string target = prefer ?? (_history.SelectedItem as FileInfo)?.FullName;
        _suppressPreview = true;
        _history.BeginUpdate();
        _history.Items.Clear();
        foreach (var file in ScreenshotService.Recent(ScreenshotService.KeepCount)) _history.Items.Add(file);
        if (target is not null)
        {
            for (int i = 0; i < _history.Items.Count; i++)
                if (((FileInfo)_history.Items[i]).FullName == target) { _history.SelectedIndex = i; break; }
        }
        _history.EndUpdate();

        bool empty = _history.Items.Count == 0;
        _history.Visible = !empty;
        if (_history.SelectedIndex < 0 && _history.Items.Count > 0) _history.SelectedIndex = 0;
        _suppressPreview = false;

        _history.PruneChecked();
        UpdateHistoryButtons();
        _historyHeader.Invalidate();
        _historyHost.Invalidate();
        _footer.Invalidate();
        ShowPreview();   // 统一在这里刷一次
    }

    private void UpdateHistoryButtons()
    {
        int marked = _history.CheckedCount;
        _btnDelete.Text = marked > 0 ? $"删除 {marked} 张" : "删除所选";
        _btnDelete.Enabled = marked > 0 || _history.SelectedItems.Count > 0;
        _btnClear.Enabled = _history.Items.Count > 0;
        _btnDelete.Invalidate();
        _btnClear.Invalidate();
    }

    /// <summary>历史区宿主：原生 ListBox 会覆盖自身 OnPaint，空状态与分隔线只能画在外层。</summary>
    private void PaintHistory(object sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.SurfaceAlt);
        g.DrawLine(Theme.P(Theme.Border), _historyHost.Width - 1, 0, _historyHost.Width - 1, _historyHost.Height);
        if (_history.Items.Count > 0) return;
        Theme.DrawText(g, "暂无截图\n按热键或点「立即截屏」", Theme.FontSmall, Theme.TextDim,
            new Rectangle(0, _historyHeader.Height, _historyHost.Width,
                Math.Max(0, _historyHost.Height - _historyHeader.Height - _historyActions.Height)),
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
    }

    /// <summary>表头行的矩形：宽度要扣掉滚动条，才能和数据列对齐。</summary>
    private Rectangle HistoryHeaderRow()
    {
        bool scroll = _history.Items.Count * _history.ItemHeight > _history.ClientSize.Height;
        int inset = scroll ? SystemInformation.VerticalScrollBarWidth : 0;
        return new Rectangle(0, 0, Math.Max(S(60), _historyHeader.Width - inset), _historyHeader.Height);
    }

    private void PaintHistoryHeader(object sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.SurfaceAlt);
        g.DrawLine(Theme.P(Theme.Border), 0, _historyHeader.Height - 1,
            _historyHeader.Width, _historyHeader.Height - 1);

        var row = HistoryHeaderRow();
        bool all = _history.AllChecked;
        ShotListBox.DrawCheck(g, ShotListBox.CheckBox(row), all, partial: !all && _history.CheckedCount > 0);

        var (name, time, size) = ShotListBox.Columns(row);
        Theme.DrawText(g, "名称", Theme.FontSmall, Theme.TextDim, name,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
        Theme.DrawText(g, "时间", Theme.FontSmall, Theme.TextDim, time,
            TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
        Theme.DrawText(g, "大小", Theme.FontSmall, Theme.TextDim, size,
            TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
    }

    // ── 预览 ────────────────────────────────────────────────

    /// <summary>预览解码走公共异步加载器；清空时取消，避免旧图贴回新选中项。</summary>
    private void ShowPreview()
    {
        _preview.Shot = null;
        if (_history.SelectedItem is not FileInfo file || !file.Exists)
        {
            _previewLoader.Cancel();
            return;
        }
        _previewLoader.Load(this, file.FullName, bitmap => _preview.Shot = bitmap);
    }

    // ── 其它操作 ────────────────────────────────────────────

    /// <summary>捕获框给出合法组合后立即写入配置并重新注册，无需再点「应用」。</summary>
    private void ApplyCapturedHotkey(string text)
    {
        if (text.Length == 0)
        {
            SetStatus("热键已清除，点输入框重新按下组合键");
            return;
        }
        ScreenshotService.HotkeyText = text;
        ScreenshotService.Register();   // 内部触发 StatusChanged，状态栏会给出生效/失败原因
    }

    private void PickFolder()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "选择截图保存目录",
            SelectedPath = Directory.Exists(ScreenshotService.Folder) ? ScreenshotService.Folder : ScreenshotService.DefaultFolder,
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        ScreenshotService.Folder = dialog.SelectedPath;
        _folderInput.Text = dialog.SelectedPath;
        RefreshHistory();
    }

    private void OnSaved(string path)
    {
        if (IsDisposed) return;
        RefreshHistory(path);   // 新截图直接选中，省得还要手动点一下
        int trimmed = ScreenshotService.LastTrimmed;
        SetStatus("已保存：" + Path.GetFileName(path) +
                  (ScreenshotService.CopyToClipboard ? "（已复制到剪贴板）" : "") +
                  (trimmed > 0 ? $"，已自动清理最旧 {trimmed} 张" : ""));
    }

    private void UpdateStatus()
    {
        if (!_ready || IsDisposed) return;
        var hotkey = ScreenshotService.Hotkey;
        if (hotkey is null) { _statusText = "热键未初始化"; return; }
        if (hotkey.Registered)
            _statusText = $"● 全局热键已生效：{hotkey.Normalized}     ·     保存目录：{ScreenshotService.Folder}";
        else
            _statusText = (ScreenshotService.Enabled ? "○ " + hotkey.Error : "○ 截屏热键已关闭") +
                          "     ·     保存目录：" + ScreenshotService.Folder;
        _footer.Invalidate();
    }

    private static void OpenFolder()
    {
        try
        {
            Directory.CreateDirectory(ScreenshotService.Folder);
            System.Diagnostics.Process.Start("explorer.exe", ScreenshotService.Folder);
        }
        catch { /* 目录不可打开时忽略 */ }
    }

    /// <summary>内置大图查看器：缩放、翻页、OCR 文字识别与复制都在这里。</summary>
    private void OpenViewer()
    {
        var files = _history.Items.Cast<FileInfo>().Where(f => f.Exists).ToList();
        if (files.Count == 0) { SetStatus("还没有截图可以预览"); return; }
        string selected = (_history.SelectedItem as FileInfo)?.FullName;
        using var viewer = new ImageViewerForm(files, selected);
        viewer.ShowDialog(this);
    }

    /// <summary>保留系统默认程序打开的入口，放在历史列表右键菜单里。</summary>
    private void OpenSelectedExternal()
    {
        if (_history.SelectedItem is not FileInfo file || !file.Exists) { OpenFolder(); return; }
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(file.FullName) { UseShellExecute = true });
        }
        catch { OpenFolder(); }
    }

    // ── 布局与绘制 ──────────────────────────────────────────

    private void PaintToolbar(object sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.Surface);
        Theme.DrawText(g, "全局热键", Theme.FontSmall, Theme.TextMuted,
            new Rectangle(S(20), S(10), S(80), S(30)));
        Theme.DrawText(g, "保存目录", Theme.FontSmall, Theme.TextMuted,
            new Rectangle(S(20), S(54), S(80), S(30)));
        Theme.DrawText(g, "选项", Theme.FontSmall, Theme.TextMuted,
            new Rectangle(S(20), S(98), S(80), S(30)));
        Theme.DrawText(g, "框选后松开可拖边角调整，再次点击或按 Enter 才保存；截图自动存为 PNG",
            Theme.FontSmall, Theme.TextDim,
            new Rectangle(S(450), S(98), Math.Max(S(200), _toolbar.Width - S(470)), S(30)),
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        g.DrawLine(Theme.P(Theme.Border), 0, _toolbar.Height - 1, _toolbar.Width, _toolbar.Height - 1);
    }

    private void PaintFooter(object sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.Surface);
        g.DrawLine(Theme.P(Theme.Border), 0, 0, _footer.Width, 0);
        Theme.DrawText(g, _statusText, Theme.FontSmall, Theme.TextMuted,
            new Rectangle(S(20), 0, Math.Max(S(200), _btnCapture.Left - S(32)), _footer.Height));
        Theme.DrawText(g, $"共 {_history.Items.Count} 张截图 · 删除会送入回收站 · 最多保留 {ScreenshotService.KeepCount} 张",
            Theme.FontSmall, Theme.TextDim,
            new Rectangle(S(20), S(42), S(460), S(22)));
    }

    private void LayoutHistoryActions()
    {
        if (_btnDelete is null || _btnClear is null) return;
        int right = _historyActions.Width - S(12);
        _btnClear.SetBounds(right - _btnClear.Width, S(9), _btnClear.Width, S(30));
        _btnDelete.SetBounds(_btnClear.Left - S(8) - _btnDelete.Width, S(9), _btnDelete.Width, S(30));
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        // 构造函数里设置 Font 会提前触发布局，此时按钮还没创建；用真正延迟赋值的字段做哨兵
        if (_btnPick is null || _folderInput is null) return;
        int labelW = S(100);
        _hotkeyBox.SetBounds(labelW, S(10), S(220), S(30));
        _toggleEnabled.SetBounds(_hotkeyBox.Right + S(16), S(13), S(120), S(24));
        _toggleFull.SetBounds(_toggleEnabled.Right + S(12), S(13), S(210), S(24));

        _folderInput.SetBounds(labelW, S(54), Math.Max(S(260), _toolbar.Width - labelW - S(150)), S(30));
        _btnPick.SetBounds(_folderInput.Right + S(8), S(54), S(70), S(30));
        _toggleClipboard.SetBounds(labelW, S(101), S(190), S(24));

        int right = _footer.Width - S(20);
        foreach (var button in new[] { _btnOpen, _btnRefresh, _btnFolder, _btnCapture })
        {
            right -= button.Width;
            button.SetBounds(right, S(20), button.Width, S(34));
            right -= S(8);
        }
        LayoutHistoryActions();
    }

    /// <summary>自绘预览画布。PictureBox 的原生绘制会覆盖 Paint 事件，这里统一自己画。</summary>
    private sealed class PreviewPanel : BufferedPanel
    {
        private static int S(int value) => Theme.S(value);
        private Image _shot;

        public Image Shot
        {
            get => _shot;
            set { _shot?.Dispose(); _shot = value; Invalidate(); }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Theme.SetQuality(e.Graphics);
            var g = e.Graphics;
            g.Clear(Theme.SurfaceAlt);

            if (_shot is null)
            {
                Theme.DrawText(g, "选择左侧任意截图即可预览", Theme.FontSmall, Theme.TextDim,
                    ClientRectangle, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                return;
            }

            var area = Rectangle.Inflate(ClientRectangle, -S(18), -S(18));
            if (area.Width <= 0 || area.Height <= 0) return;
            float scale = Math.Min((float)area.Width / _shot.Width, (float)area.Height / _shot.Height);
            scale = Math.Min(scale, 1f);
            int w = Math.Max(1, (int)(_shot.Width * scale));
            int h = Math.Max(1, (int)(_shot.Height * scale));
            var target = new Rectangle(area.X + (area.Width - w) / 2, area.Y + (area.Height - h) / 2, w, h);
            g.DrawImage(_shot, target);
            using var pen = new Pen(Theme.Border);
            g.DrawRectangle(pen, target.X - 1, target.Y - 1, target.Width + 1, target.Height + 1);

            string caption = w == _shot.Width
                ? $"{_shot.Width} × {_shot.Height}"
                : $"{_shot.Width} × {_shot.Height}   缩放至 {w} × {h}";
            Theme.DrawText(g, "点击图片放大预览 · 滚轮缩放 · 可识别并复制文字", Theme.FontSmall, Theme.TextMuted,
                new Rectangle(S(18), Height - S(30), Math.Max(S(120), Width - S(280)), S(22)),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            Theme.DrawText(g, caption, Theme.FontSmall, Theme.TextDim,
                new Rectangle(S(18), Height - S(30), Width - S(36), S(22)),
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
        }
    }

    /// <summary>自绘的截图历史列表：名称 / 时间 / 大小 三列，支持多选与 Delete 删除。</summary>
    private sealed class ShotListBox : ListBox
    {
        private static int S(int value) => Theme.S(value);

        public event EventHandler DeleteRequested;
        public event EventHandler CheckChanged;

        private readonly ToolTip _tip = new();
        private readonly HashSet<string> _checked = new(StringComparer.OrdinalIgnoreCase);
        private string _tipText = "";

        public ShotListBox()
        {
            DrawMode = DrawMode.OwnerDrawFixed;
            SelectionMode = SelectionMode.One;   // 多选交给复选框，行选中只用于预览
            ItemHeight = S(40);
            BorderStyle = BorderStyle.None;
            IntegralHeight = false;
            BackColor = Theme.SurfaceAlt;
            Font = Theme.FontSmall;
            Disposed += (_, _) => _tip.Dispose();
        }

        public int CheckedCount => _checked.Count;
        public bool AllChecked => Items.Count > 0 && _checked.Count >= Items.Count;

        /// <summary>勾选的文件；列表刷新后仍按完整路径对应。</summary>
        public List<FileInfo> CheckedFiles() =>
            Items.Cast<FileInfo>().Where(f => _checked.Contains(f.FullName)).ToList();

        public void ToggleAt(int index)
        {
            if (index < 0 || index >= Items.Count || Items[index] is not FileInfo file) return;
            if (!_checked.Remove(file.FullName)) _checked.Add(file.FullName);
            Invalidate();
            CheckChanged?.Invoke(this, EventArgs.Empty);
        }

        public void ToggleAll()
        {
            if (Items.Count == 0) return;
            if (AllChecked) _checked.Clear();
            else foreach (FileInfo file in Items) _checked.Add(file.FullName);
            Invalidate();
            CheckChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>刷新列表后丢弃已经不存在的勾选项。</summary>
        public void PruneChecked()
        {
            var alive = Items.Cast<FileInfo>().Select(f => f.FullName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            int before = _checked.Count;
            _checked.RemoveWhere(path => !alive.Contains(path));
            if (_checked.Count != before) CheckChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>勾选框矩形：表头与数据行共用同一套坐标，保证一列对齐。</summary>
        public static Rectangle CheckBox(Rectangle row)
        {
            int d = S(18);
            return new Rectangle(row.X + S(10), row.Y + (row.Height - d) / 2, d, d);
        }

        public static void DrawCheck(Graphics g, Rectangle box, bool on, bool partial = false)
        {
            if (on)
            {
                Theme.Box(g, box, S(4), Theme.Accent, Theme.Accent);
                using var pen = new Pen(Color.White, S(2)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                g.DrawLine(pen, box.X + box.Width * 0.26f, box.Y + box.Height * 0.52f,
                    box.X + box.Width * 0.44f, box.Y + box.Height * 0.70f);
                g.DrawLine(pen, box.X + box.Width * 0.44f, box.Y + box.Height * 0.70f,
                    box.X + box.Width * 0.76f, box.Y + box.Height * 0.30f);
                return;
            }

            Theme.Box(g, box, S(4), Theme.Elevated, Theme.BorderStrong);
            if (!partial) return;
            using var bar = new Pen(Theme.Accent, S(2)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLine(bar, box.X + box.Width * 0.28f, box.Y + box.Height * 0.5f,
                box.X + box.Width * 0.72f, box.Y + box.Height * 0.5f);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                int index = IndexFromPoint(e.Location);
                if (index >= 0 && index < Items.Count &&
                    CheckBox(GetItemRectangle(index)).Contains(e.Location))
                {
                    ToggleAt(index);
                    return;   // 勾选不动行选中，也不交给 ListBox 处理
                }
            }
            base.OnMouseDown(e);
        }

        /// <summary>文件名在窄列里会被省略，悬停给出完整名称与时间。</summary>
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            string text = "";
            int index = IndexFromPoint(e.Location);
            if (index >= 0 && index < Items.Count && Items[index] is FileInfo file)
                text = $"{file.Name}\r\n{file.LastWriteTime:yyyy-MM-dd HH:mm:ss}    {FormatSize(file.Length)}";
            if (text == _tipText) return;
            _tipText = text;
            _tip.SetToolTip(this, text);
        }

        public void ApplyTheme()
        {
            BackColor = Theme.SurfaceAlt;
            Invalidate();
        }

        /// <summary>三列的横向位置。头部与数据行共用，保证严格对齐。</summary>
        public static (Rectangle Name, Rectangle Time, Rectangle Size) Columns(Rectangle row)
        {
            int pad = S(10), check = S(18), gap = S(8);
            int timeW = S(96), sizeW = S(52);
            int right = row.Right - pad;
            var size = new Rectangle(right - sizeW, row.Y, sizeW, row.Height);
            var time = new Rectangle(size.Left - gap - timeW, row.Y, timeW, row.Height);
            int nameX = row.X + pad + check + gap + S(2);
            var name = new Rectangle(nameX, row.Y, Math.Max(S(24), time.Left - gap - nameX), row.Height);
            return (name, time, size);
        }

        protected override void OnDrawItem(DrawItemEventArgs e)
        {
            if (e.Index < 0 || e.Index >= Items.Count) return;
            var g = e.Graphics;
            var rect = e.Bounds;
            bool selected = (e.State & DrawItemState.Selected) != 0;
            using var bg = new SolidBrush(selected ? Theme.AccentWash : Theme.SurfaceAlt);
            g.FillRectangle(bg, rect);

            if (Items[e.Index] is not FileInfo file) return;
            bool marked = _checked.Contains(file.FullName);
            DrawCheck(g, CheckBox(rect), marked);

            var (name, time, size) = Columns(rect);
            Theme.DrawText(g, file.Name, Theme.FontSmall, marked || selected ? Theme.Text : Theme.TextMuted, name,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            Theme.DrawText(g, file.LastWriteTime.ToString("MM-dd HH:mm"), Theme.FontSmall, Theme.TextDim, time,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
            Theme.DrawText(g, FormatSize(file.Length), Theme.FontSmall, Theme.TextDim, size,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
            g.DrawLine(Theme.P(Theme.Border), rect.X + S(10), rect.Bottom - 1, rect.Right - S(10), rect.Bottom - 1);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Delete)
            {
                e.Handled = true;
                DeleteRequested?.Invoke(this, EventArgs.Empty);
                return;
            }
            if (e.Control && e.KeyCode == Keys.A)
            {
                e.Handled = true;
                if (!AllChecked) ToggleAll();
                return;
            }
            if (e.KeyCode == Keys.Space && SelectedIndex >= 0)
            {
                e.Handled = true;
                ToggleAt(SelectedIndex);
                return;
            }
            base.OnKeyDown(e);
        }

        private static string FormatSize(long bytes) => bytes >= 1024 * 1024
            ? $"{bytes / 1024f / 1024f:0.#} MB" : $"{Math.Max(1, bytes / 1024)} KB";
    }
}
