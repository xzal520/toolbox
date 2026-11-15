using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;
using Toolbox.Core;

namespace Toolbox.UI;

/// <summary>
/// 截图放大预览：滚轮以光标为锚点缩放、拖动平移、左右键翻页；
/// 右侧同步做系统内置 OCR，文字可直接选中/复制，图片也能一键复制。
/// 图片解码统一走 AsyncImageLoader，翻页再快也不会贴错图。
/// </summary>
internal sealed class ImageViewerForm : Form, IThemed
{
    private static int S(int value) => Theme.S(value);
    private const float MinZoom = 0.05f, MaxZoom = 16f, ZoomStep = 1.2f;

    private readonly List<FileInfo> _files;
    private readonly AsyncImageLoader _loader = new();
    private readonly ZoomCanvas _canvas = new();
    private readonly BufferedPanel _titleBar = new();
    private readonly BufferedPanel _side = new();
    private readonly BufferedPanel _sideHeader = new();
    private readonly BufferedPanel _sideActions = new();
    private readonly BufferedPanel _textHost = new();
    private readonly BufferedPanel _footer = new();
    private readonly TextBox _text = new();
    private readonly ToolTip _tips = new();

    private readonly ModernButton _btnToggleText, _btnMax, _btnClose;
    private readonly ModernButton _btnPrev, _btnNext, _btnZoomOut, _btnZoomIn, _btnFit, _btnActual, _btnDoodle, _btnOpenFolder, _btnOpenExternal;
    private readonly ModernButton _btnReOcr, _btnCopyText, _btnCopyImage;
    private readonly DoodleBar _doodleBar;

    private int _index;
    private bool _loaded, _sideVisible = true, _doodleMode;
    private string _selectedText = "";
    private ContextMenuStrip _textMenu, _doodleMenu;
    private CancellationTokenSource _ocrCts;
    private string _ocrText = "", _ocrStatus = "", _flash = "", _title = "", _subTitle = "";

    private FileInfo Current => _files.Count > 0 ? _files[_index] : null;

    private static ModernButton ToolButton(string text, int width, Glyph glyph = Glyph.None) => new()
    {
        Text = text, Width = S(width), Height = S(30), Kind = ButtonKind.Subtle,
        Icon = glyph, Font = Theme.FontSmall,
    };

    private static ModernButton IconButton(Glyph glyph) => new()
    {
        Text = "", Width = S(38), Height = S(30), Kind = ButtonKind.Ghost, Icon = glyph,
    };

    public ImageViewerForm(IEnumerable<FileInfo> files, string selectedPath)
    {
        _files = files.Where(f => f is not null && f.Exists).ToList();
        _index = Math.Max(0, _files.FindIndex(f =>
            string.Equals(f.FullName, selectedPath, StringComparison.OrdinalIgnoreCase)));

        Text = "截图预览 · 实用工具箱";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = false;
        DoubleBuffered = true;
        BackColor = Theme.Window;
        Font = Theme.FontBase;
        KeyPreview = true;
        MinimizeBox = false;
        MaximizeBox = false;
        var area = Screen.PrimaryScreen.WorkingArea;
        Size = new Size(Math.Min(S(1480), area.Width - S(60)), Math.Min(S(920), area.Height - S(60)));
        MinimumSize = new Size(S(760), S(520));
        MaximizedBounds = area;   // 无边框窗口最大化时别盖住任务栏

        // ── 标题栏 ──────────────────────────────────────────
        _titleBar.Dock = DockStyle.Top;
        _titleBar.Height = S(46);
        _titleBar.Paint += PaintTitleBar;
        _titleBar.MouseDown += (_, e) => { if (e.Button == MouseButtons.Left) NativeMethods.DragWindow(Handle); };
        _titleBar.DoubleClick += (_, _) => ToggleMaximize();

        _btnToggleText = IconButton(Glyph.Collapse);
        _btnMax = IconButton(Glyph.Maximize);
        _btnClose = IconButton(Glyph.Close);
        _tips.SetToolTip(_btnToggleText, "显示 / 隐藏文字面板");
        _tips.SetToolTip(_btnMax, "最大化 / 还原");
        _tips.SetToolTip(_btnClose, "关闭 (Esc)");
        _btnToggleText.Click += (_, _) => ToggleTextPanel();
        _btnMax.Click += (_, _) => ToggleMaximize();
        _btnClose.Click += (_, _) => Close();
        _titleBar.Controls.AddRange(new Control[] { _btnToggleText, _btnMax, _btnClose });

        // ── 右侧文字面板 ────────────────────────────────────
        _side.Dock = DockStyle.Right;
        _side.Width = S(360);
        _side.Paint += (_, e) =>
        {
            e.Graphics.Clear(Theme.Surface);
            e.Graphics.DrawLine(Theme.P(Theme.Border), 0, 0, 0, _side.Height);
        };

        _sideHeader.Dock = DockStyle.Top;
        _sideHeader.Height = S(40);
        _sideHeader.Paint += PaintSideHeader;

        _sideActions.Dock = DockStyle.Bottom;
        _sideActions.Height = S(48);
        _sideActions.Paint += (_, e) =>
            e.Graphics.DrawLine(Theme.P(Theme.Border), 0, 0, _sideActions.Width, 0);

        _btnReOcr = ToolButton("重新识别", 88);
        _btnCopyText = ToolButton("复制文字", 88, Glyph.Copy);
        _btnCopyImage = ToolButton("复制图片", 88, Glyph.Image);
        _btnReOcr.Click += (_, _) => { if (Current is not null) RunOcr(Current.FullName); };
        _btnCopyText.Click += (_, _) => CopyText();
        _btnCopyImage.Click += (_, _) => CopyImage();
        _btnCopyText.Enabled = false;
        _sideActions.Controls.AddRange(new Control[] { _btnReOcr, _btnCopyText, _btnCopyImage });

        _text.Dock = DockStyle.Fill;
        _text.Multiline = true;
        _text.ReadOnly = true;
        _text.WordWrap = true;
        _text.ScrollBars = ScrollBars.Vertical;
        _text.BorderStyle = BorderStyle.None;
        _text.BackColor = Theme.Elevated;
        _text.ForeColor = Theme.Text;
        _text.Font = Theme.FontSmall;
        _textHost.Dock = DockStyle.Fill;
        _textHost.Padding = new Padding(S(10), S(8), S(10), S(10));
        _textHost.Paint += PaintTextHost;
        _textHost.Controls.Add(_text);

        // Dock 逆序：Fill 最先加入，Top / Bottom 之后加入才能跨全宽
        _side.Controls.Add(_textHost);
        _side.Controls.Add(_sideActions);
        _side.Controls.Add(_sideHeader);

        // ── 底部工具条 ──────────────────────────────────────
        _footer.Dock = DockStyle.Bottom;
        _footer.Height = S(52);
        _footer.Paint += PaintFooter;

        _btnPrev = ToolButton("← 上一张", 96);
        _btnNext = ToolButton("下一张 →", 96);
        _btnZoomOut = ToolButton("－", 34);
        _btnZoomIn = ToolButton("＋", 34);
        _btnFit = ToolButton("适应窗口", 88);
        _btnActual = ToolButton("1:1", 56);
        _btnDoodle = ToolButton("涂鸦", 72);
        _btnOpenExternal = ToolButton("用系统打开", 100, Glyph.Image);
        _btnOpenFolder = ToolButton("打开文件夹", 100, Glyph.Folder);
        _btnDoodle.Click += (_, _) => SetDoodleMode(!_doodleMode);
        _btnPrev.Click += (_, _) => SwitchImage(-1);
        _btnNext.Click += (_, _) => SwitchImage(1);
        _btnZoomOut.Click += (_, _) => ZoomBy(-1);
        _btnZoomIn.Click += (_, _) => ZoomBy(1);
        _btnFit.Click += (_, _) => _canvas.Fit();
        _btnActual.Click += (_, _) => _canvas.ActualSize();
        _btnOpenExternal.Click += (_, _) => OpenExternal();
        _btnOpenFolder.Click += (_, _) => OpenContainingFolder();
        _footer.Controls.AddRange(new Control[]
        {
            _btnPrev, _btnNext, _btnZoomOut, _btnZoomIn, _btnFit, _btnActual, _btnDoodle,
            _btnOpenFolder, _btnOpenExternal,
        });

        _canvas.Dock = DockStyle.Fill;
        _canvas.ZoomChanged += _ => _footer.Invalidate();
        _canvas.WordsSelected += OnWordsSelected;
        _canvas.Doodle.Changed += SyncDoodleBar;

        // 涂鸦工具条悬浮在画布上；两种模式各用一套右键菜单
        _doodleBar = new DoodleBar(DoodleAction.Undo | DoodleAction.Clear |
            DoodleAction.Save | DoodleAction.Copy | DoodleAction.Overwrite | DoodleAction.Exit) { Visible = false };
        _doodleBar.BrushChanged += () =>
        {
            _canvas.Doodle.BrushColor = _doodleBar.BrushColor;
            _canvas.Doodle.BrushWidth = _doodleBar.BrushWidth;
        };
        _doodleBar.UndoRequested += () => { _canvas.UndoDoodle(); SyncDoodleBar(); };
        _doodleBar.ClearRequested += () => { _canvas.ClearDoodle(); SyncDoodleBar(); };
        _doodleBar.SaveRequested += () => SaveDoodle();
        _doodleBar.CopyRequested += () => CopyDoodle();
        _doodleBar.OverwriteRequested += () => SaveOriginal();
        _doodleBar.ExitRequested += () => SetDoodleMode(false);
        _canvas.Doodle.BrushColor = _doodleBar.BrushColor;
        _canvas.Doodle.BrushWidth = _doodleBar.BrushWidth;
        _canvas.Controls.Add(_doodleBar);

        _textMenu = BuildCanvasMenu();
        _doodleMenu = BuildDoodleMenu();
        _canvas.ContextMenuStrip = _textMenu;

        Controls.Add(_canvas);
        Controls.Add(_side);
        Controls.Add(_footer);
        Controls.Add(_titleBar);

        Theme.Changed += ApplyTheme;
        ApplyTheme();
    }

    public void ApplyTheme()
    {
        BackColor = Theme.Window;
        _text.BackColor = Theme.Elevated;
        _text.ForeColor = Theme.Text;
        Invalidate(true);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        ApplyRegion();
        if (_loaded) return;
        _loaded = true;
        LoadCurrent();   // 句柄建好后才能 BeginInvoke，解码结果必须走消息循环回 UI
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Theme.Changed -= ApplyTheme;
            _ocrCts?.Cancel();
            _ocrCts?.Dispose();
            _canvas.Shot = null;
            _tips.Dispose();
        }
        base.Dispose(disposing);
    }

    // ── 图片切换与 OCR ─────────────────────────────────────

    private void LoadCurrent()
    {
        var file = Current;
        if (file is null) { _text.Text = "没有可预览的截图"; return; }

        _title = file.Name;
        _subTitle = $"{_index + 1} / {_files.Count}  ·  {file.LastWriteTime:MM-dd HH:mm}";
        _titleBar.Invalidate();
        if (_doodleMode) SetDoodleMode(false);
        _flash = "";
        _selectedText = "";
        _canvas.ClearDoodle();
        _canvas.SetWords(Array.Empty<OcrWordBox>());   // 换图先清掉上一张的词层
        _canvas.Shot = null;
        _footer.Invalidate();

        RunOcr(file.FullName);
        _loader.Load(this, file.FullName, bitmap =>
        {
            if (bitmap is null)
            {
                _text.Text = "图片解码失败，可能已损坏";
                _flash = "图片无法读取";
                _footer.Invalidate();
                return;
            }
            _canvas.Shot = bitmap;   // setter 释放上一张并自动适应窗口
            _footer.Invalidate();
        });
    }

    /// <summary>整图识别：文本填右侧面板，词坐标交给画布做「拖选文字」。</summary>
    private async void RunOcr(string path)
    {
        _ocrCts?.Cancel();
        _ocrCts?.Dispose();
        var cts = _ocrCts = new CancellationTokenSource();
        _ocrText = "";
        _btnCopyText.Enabled = false;
        _canvas.SetWords(Array.Empty<OcrWordBox>());

        try
        {
            if (!OcrService.IsAvailable)
            {
                _ocrStatus = "OCR 不可用";
                _text.Text = OcrService.UnavailableReason;
                return;
            }
            _ocrStatus = "识别中…";
            _text.Text = "正在识别文字…";
            _sideHeader.Invalidate();

            var result = await OcrService.RecognizeAsync(path, cts.Token);
            if (cts.IsCancellationRequested || IsDisposed) return;
            _ocrText = result.Text;
            _text.Text = _ocrText.Length > 0 ? _ocrText : "（这张图片里没有识别到文字）";
            _ocrStatus = $"{result.LineCount} 行 · {result.Language}";
            _btnCopyText.Enabled = _ocrText.Length > 0;
            _canvas.SetWords(result.Words);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            if (IsDisposed) return;
            _ocrStatus = "识别失败";
            _text.Text = "识别失败：" + ex.Message;
        }
        _sideHeader.Invalidate();
    }

    /// <summary>拖选松手只记录选区，不碰剪贴板；复制由右键菜单或 Ctrl+C 触发。</summary>
    private void OnWordsSelected(string text)
    {
        _selectedText = text;
        Flash($"已选中 {text.Length} 个字符 · 右键或 Ctrl+C 复制");
    }

    private void CopySelection()
    {
        if (_selectedText.Length == 0) { Flash("请先在图片上拖动选中文字"); return; }
        try
        {
            Clipboard.SetText(_selectedText);
            Flash($"已复制选中文字（{_selectedText.Length} 个字符）");
        }
        catch { Flash("复制失败，可点「复制文字」复制全文"); }
    }

    private void ClearSelection()
    {
        _selectedText = "";
        _canvas.ClearSelection();
        Flash("已取消选中");
    }

    private ContextMenuStrip BuildCanvasMenu()
    {
        var menu = new ContextMenuStrip { Font = Theme.FontSmall, ShowImageMargin = false };
        var copySelection = new ToolStripMenuItem("复制选中文字  (Ctrl+C)");
        copySelection.Click += (_, _) => CopySelection();
        menu.Items.Add(copySelection);
        menu.Items.Add("复制全部文字", null, (_, _) => CopyText());
        menu.Items.Add("复制图片", null, (_, _) => CopyImage());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("取消选中", null, (_, _) => ClearSelection());
        menu.Opening += (_, _) => copySelection.Enabled = _selectedText.Length > 0;
        return menu;
    }

    private void SetDoodleMode(bool on)
    {
        _doodleMode = on;
        _canvas.DoodleMode = on;
        _doodleBar.Visible = on;
        _btnDoodle.Kind = on ? ButtonKind.Primary : ButtonKind.Subtle;
        _btnDoodle.Invalidate();
        _canvas.ContextMenuStrip = on ? _doodleMenu : _textMenu;
        if (on) { _selectedText = ""; _canvas.ClearSelection(); }
        SyncDoodleBar();
        _footer.Invalidate();
        Flash(on ? "涂鸦：拖动画线 · Ctrl+Z 撤销 · Ctrl+S 存副本 · 点「保存」覆盖原图 · D / Esc 退出" : "已退出涂鸦");
    }

    private ContextMenuStrip BuildDoodleMenu()
    {
        var menu = new ContextMenuStrip { Font = Theme.FontSmall, ShowImageMargin = false };
        menu.Items.Add("撤销  (Ctrl+Z)", null, (_, _) => { _canvas.UndoDoodle(); SyncDoodleBar(); });
        menu.Items.Add("清空涂鸦", null, (_, _) => { _canvas.ClearDoodle(); SyncDoodleBar(); });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("保存副本（进历史记录）", null, (_, _) => SaveDoodle());
        menu.Items.Add("复制涂鸦图片", null, (_, _) => CopyDoodle());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("保存（覆盖原文件）", null, (_, _) => SaveOriginal());
        menu.Items.Add("退出涂鸦", null, (_, _) => SetDoodleMode(false));
        return menu;
    }

    private void SyncDoodleBar()
    {
        _doodleBar.HasStrokes = _canvas.HasDoodle;
        _doodleBar.Invalidate();
    }

    private void SaveDoodle()
    {
        if (Current is null || _canvas.Shot is null || !_canvas.HasDoodle) return;
        try
        {
            using var result = _canvas.RenderWithDoodle();
            string dir = Path.GetDirectoryName(Current.FullName) ?? ScreenshotService.Folder;
            string file = Path.Combine(dir, Path.GetFileNameWithoutExtension(Current.FullName)
                + "_涂鸦_" + DateTime.Now.ToString("HHmmss") + ".png");
            result.Save(file, ImageFormat.Png);
            ScreenshotService.NotifySaved(file);   // 立刻出现在主窗口的历史记录里
            Flash("已保存副本并加入历史：" + Path.GetFileName(file));
        }
        catch { Flash("保存失败，可改用「复制图片」"); }
    }

    /// <summary>「保存」= 把涂鸦烘焙回原文件；随后画布换成烘焙结果，避免重复叠加。</summary>
    private void SaveOriginal()
    {
        if (Current is null || _canvas.Shot is null) return;
        try
        {
            var rendered = _canvas.RenderWithDoodle();
            rendered.Save(Current.FullName, ImageFormat.Png);
            _canvas.AdoptRendered(rendered);   // 由画布接管，不 Dispose
            ScreenshotService.NotifySaved(Current.FullName);
            SetDoodleMode(false);
            Flash("已覆盖保存到原文件：" + Current.Name);
        }
        catch { Flash("覆盖保存失败，可改用「保存副本」"); }
    }

    private void CopyDoodle()
    {
        if (_canvas.Shot is null) return;
        try
        {
            Clipboard.SetImage(_canvas.RenderWithDoodle());   // 剪贴板接管，不要 Dispose
            Flash("涂鸦后的图片已复制到剪贴板");
        }
        catch { Flash("复制失败"); }
    }

    private void SwitchImage(int delta)
    {
        if (_files.Count < 2) return;
        _index = (_index + delta + _files.Count) % _files.Count;
        LoadCurrent();
    }

    private void CopyText()
    {
        if (_ocrText.Length == 0) return;
        try { Clipboard.SetText(_ocrText); Flash("识别到的文字已复制到剪贴板"); }
        catch { Flash("文字复制失败，请手动选中后按 Ctrl+C"); }
    }

    private void CopyImage()
    {
        if (_canvas.Shot is null) return;
        try
        {
            // 剪贴板会接管这张副本，不要 Dispose
            Clipboard.SetImage(new Bitmap(_canvas.Shot));
            Flash("图片已复制到剪贴板");
        }
        catch { Flash("图片复制失败"); }
    }

    private void OpenExternal()
    {
        if (Current is null) return;
        try
        {
            Process.Start(new ProcessStartInfo(Current.FullName) { UseShellExecute = true });
            Flash("已用系统默认程序打开原图");
        }
        catch { Flash("打开失败，可在主窗口用「打开所在文件夹」查看"); }
    }

    /// <summary>打开截图所在文件夹并选中当前图片；失败时退回只打开目录。</summary>
    private void OpenContainingFolder()
    {
        if (Current is null) return;
        try
        {
            Process.Start("explorer.exe", "/select,\"" + Current.FullName + "\"");
            Flash("已打开截图所在文件夹");
        }
        catch
        {
            try
            {
                string dir = Path.GetDirectoryName(Current.FullName) ?? ScreenshotService.Folder;
                Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
                Flash("已打开截图目录");
            }
            catch { Flash("打开文件夹失败"); }
        }
    }

    private void Flash(string message) { _flash = message; _footer.Invalidate(); }

    private void ZoomBy(int direction) => _canvas.ZoomAt(direction * 120,
        new Point(_canvas.ClientSize.Width / 2, _canvas.ClientSize.Height / 2));

    private void ToggleTextPanel()
    {
        _sideVisible = !_sideVisible;
        _side.Visible = _sideVisible;
        _btnToggleText.Icon = _sideVisible ? Glyph.Collapse : Glyph.Expand;
        _btnToggleText.Invalidate();
        _canvas.Invalidate();
    }

    private void ToggleMaximize()
    {
        WindowState = WindowState == FormWindowState.Maximized
            ? FormWindowState.Normal : FormWindowState.Maximized;
        ApplyRegion();
        BeginInvoke(new Action(_canvas.Fit));   // 等布局按新尺寸算完再适应窗口
    }

    private void ApplyRegion()
    {
        if (WindowState == FormWindowState.Maximized)
        {
            var old = Region;
            Region = null;
            old?.Dispose();
        }
        else NativeMethods.ApplyRoundRegion(this, S(12));
    }

    // ── 键盘与滚轮 ─────────────────────────────────────────

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        Point at = _canvas.PointToClient(Cursor.Position);
        if (_canvas.ClientRectangle.Contains(at)) { _canvas.ZoomAt(e.Delta, at); return; }
        base.OnMouseWheel(e);
    }

    /// <summary>Ctrl+C 放在 ProcessCmdKey：优先于焦点控件，焦点在图片或文本框都能生效。</summary>
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == (Keys.Control | Keys.C) && _selectedText.Length > 0)
        {
            CopySelection();
            return true;
        }
        if (_doodleMode && keyData == (Keys.Control | Keys.Z))
        {
            _canvas.UndoDoodle();
            SyncDoodleBar();
            return true;
        }
        if (_doodleMode && keyData == (Keys.Control | Keys.S))
        {
            SaveDoodle();
            return true;
        }
        if (_doodleMode && keyData == (Keys.Control | Keys.Shift | Keys.S))
        {
            SaveOriginal();
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape)
        {
            e.Handled = true;
            if (_doodleMode) { SetDoodleMode(false); return; }
            Close();
            return;
        }
        if (e.KeyCode == Keys.D && !e.Control && !e.Alt)
        {
            e.Handled = true;
            SetDoodleMode(!_doodleMode);
            return;
        }
        if (e.Control && e.KeyCode == Keys.C && _selectedText.Length > 0)
        {
            e.Handled = true;
            CopySelection();
            return;
        }
        bool typing = _text.Focused;
        if (!typing)
        {
            switch (e.KeyCode)
            {
                case Keys.Left: e.Handled = true; SwitchImage(-1); return;
                case Keys.Right: e.Handled = true; SwitchImage(1); return;
                case Keys.Add: case Keys.Oemplus: e.Handled = true; ZoomBy(1); return;
                case Keys.Subtract: case Keys.OemMinus: e.Handled = true; ZoomBy(-1); return;
                case Keys.F: e.Handled = true; _canvas.Fit(); return;
                case Keys.D1: e.Handled = true; _canvas.ActualSize(); return;
            }
            if (e.Control && e.KeyCode == Keys.C) { e.Handled = true; CopyText(); return; }
        }
        base.OnKeyDown(e);
    }

    // ── 布局与绘制 ─────────────────────────────────────────

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        if (_btnClose is null || _doodleBar is null || _btnOpenFolder is null) return;

        int right = _titleBar.Width - S(10);
        foreach (var button in new[] { _btnClose, _btnMax, _btnToggleText })
        {
            right -= button.Width;
            button.SetBounds(right, S(8), button.Width, S(30));
            right -= S(4);
        }

        right = _sideActions.Width - S(10);
        foreach (var button in new[] { _btnCopyImage, _btnCopyText, _btnReOcr })
        {
            right -= button.Width;
            button.SetBounds(right, S(9), button.Width, S(30));
            right -= S(6);
        }

        int x = S(14);
        foreach (var button in new[] { _btnPrev, _btnNext, _btnZoomOut, _btnZoomIn, _btnFit, _btnActual, _btnDoodle })
        {
            if (button == _btnZoomIn) x += S(62);   // 给中间的缩放百分比留出位置
            if (button == _btnDoodle) x += S(14);
            button.SetBounds(x, S(10), button.Width, S(32));
            x += button.Width + (button == _btnNext ? S(14) : S(6));
        }
        int rightEdge = _footer.Width - S(14);
        _btnOpenExternal.SetBounds(rightEdge - _btnOpenExternal.Width,
            S(10), _btnOpenExternal.Width, S(32));
        rightEdge -= _btnOpenExternal.Width + S(8);
        _btnOpenFolder.SetBounds(rightEdge - _btnOpenFolder.Width,
            S(10), _btnOpenFolder.Width, S(32));
        int barX = Math.Max(S(6), (_canvas.ClientSize.Width - _doodleBar.Width) / 2);
        _doodleBar.SetBounds(barX, S(14), _doodleBar.Width, _doodleBar.Height);
    }

    private void PaintTitleBar(object sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.Surface);
        Theme.SetQuality(g);
        GlyphPainter.Draw(g, Glyph.Image,
            new Rectangle(S(14), (_titleBar.Height - S(18)) / 2, S(18), S(18)), Theme.Accent);
        int left = S(40);
        int right = Math.Max(left + S(120), _btnToggleText.Left - S(12));
        Theme.DrawText(g, _title, Theme.FontBold, Theme.Text,
            new Rectangle(left, 0, Math.Max(S(100), right - left - S(200)), _titleBar.Height));
        Theme.DrawText(g, _subTitle, Theme.FontSmall, Theme.TextDim,
            new Rectangle(Math.Max(left, right - S(200)), 0, S(200), _titleBar.Height),
            TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        g.DrawLine(Theme.P(Theme.Border), 0, _titleBar.Height - 1, _titleBar.Width, _titleBar.Height - 1);
    }

    private void PaintSideHeader(object sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.Surface);
        Theme.DrawText(g, "识别文字", Theme.FontBold, Theme.Text,
            new Rectangle(S(14), 0, S(90), _sideHeader.Height));
        Theme.DrawText(g, _ocrStatus, Theme.FontSmall, Theme.TextDim,
            new Rectangle(S(110), 0, Math.Max(S(60), _sideHeader.Width - S(124)), _sideHeader.Height),
            TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        g.DrawLine(Theme.P(Theme.Border), 0, _sideHeader.Height - 1,
            _sideHeader.Width, _sideHeader.Height - 1);
    }

    private void PaintTextHost(object sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.Surface);
        Theme.Box(g, Rectangle.Inflate(_textHost.ClientRectangle, -S(10), -S(8)),
            S(8), Theme.Elevated, Theme.Border);
    }

    private void PaintFooter(object sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.Surface);
        g.DrawLine(Theme.P(Theme.Border), 0, 0, _footer.Width, 0);

        int pctLeft = _btnZoomOut.Right + S(6);
        int pctWidth = Math.Max(S(44), _btnZoomIn.Left - S(6) - pctLeft);
        Theme.DrawText(g, $"{_canvas.Zoom * 100:0}%", Theme.FontSmallBold, Theme.TextMuted,
            new Rectangle(pctLeft, 0, pctWidth, _footer.Height),
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

        string hint = _flash.Length > 0 ? _flash : _doodleMode
            ? "拖动画线 · Ctrl+Z 撤销 · Ctrl+S 存副本 · 「保存」覆盖原图 · D / Esc 退出"
            : "文字上拖动选中 · 右键 / Ctrl+C 复制 · D 涂鸦 · 空白处拖动平移 · Esc 关闭";
        Color color = _flash.Length > 0 ? Theme.Success : Theme.TextMuted;
        int hintLeft = _btnDoodle.Right + S(16);
        Theme.DrawText(g, hint, Theme.FontSmall, color,
            new Rectangle(hintLeft, 0, Math.Max(S(120), _btnOpenFolder.Left - S(16) - hintLeft), _footer.Height));
    }

    /// <summary>自绘画布：以图片左上角偏移 + 缩放系数绘制，滚轮与拖拽只改这两个量。</summary>
    private sealed class ZoomCanvas : BufferedPanel
    {
        private static int S(int value) => Theme.S(value);
        private Image _shot;
        private float _zoom = 1f;
        private PointF _origin;
        private bool _dragging;
        private Point _last;
        private IReadOnlyList<OcrWordBox> _words = Array.Empty<OcrWordBox>();
        private int _wordAnchor = -1, _wordFocus = -1;
        private bool _selectingWords;

        public DoodleLayer Doodle { get; } = new();
        public bool DoodleMode { get; set; }
        public bool HasDoodle => Doodle.HasStrokes;

        public event Action<float> ZoomChanged;
        /// <summary>在图片上拖选文字，松手时把选中的词拼成文本交给外层复制。</summary>
        public event Action<string> WordsSelected;
        public float Zoom => _zoom;

        public void UndoDoodle() { Doodle.Undo(); Invalidate(); }
        public void ClearDoodle() { Doodle.Clear(); Invalidate(); }

        public Image Shot
        {
            get => _shot;
            set
            {
                _shot?.Dispose();
                _shot = value;
                if (value is not null) Fit();
                Invalidate();
            }
        }

        public ZoomCanvas()
        {
            Cursor = Cursors.SizeAll;
            BackColor = Theme.SurfaceAlt;
        }

        public void Fit()
        {
            if (_shot is null) return;
            var area = Rectangle.Inflate(ClientRectangle, -S(16), -S(16));
            if (area.Width <= 0 || area.Height <= 0) return;
            float fit = Math.Min((float)area.Width / _shot.Width, (float)area.Height / _shot.Height);
            SetZoom(Math.Clamp(fit, MinZoom, MaxZoom));
            _origin = new PointF((ClientSize.Width - _shot.Width * _zoom) / 2f,
                (ClientSize.Height - _shot.Height * _zoom) / 2f);
            Invalidate();
        }

        public void ActualSize()
        {
            if (_shot is null) return;
            var center = new Point(ClientSize.Width / 2, ClientSize.Height / 2);
            float imageX = (center.X - _origin.X) / _zoom, imageY = (center.Y - _origin.Y) / _zoom;
            SetZoom(1f);
            _origin = new PointF(center.X - imageX, center.Y - imageY);
            Invalidate();
        }

        /// <summary>以光标位置为锚点缩放：锚点下的图像像素保持不动。</summary>
        public void ZoomAt(int delta, Point anchor)
        {
            if (_shot is null) return;
            float imageX = (anchor.X - _origin.X) / _zoom;
            float imageY = (anchor.Y - _origin.Y) / _zoom;
            SetZoom(Math.Clamp(_zoom * (delta > 0 ? ZoomStep : 1f / ZoomStep), MinZoom, MaxZoom));
            _origin = new PointF(anchor.X - imageX * _zoom, anchor.Y - imageY * _zoom);
            Invalidate();
        }

        private void SetZoom(float value)
        {
            _zoom = value;
            ZoomChanged?.Invoke(_zoom);
        }

        /// <summary>整图识别完成后把每个词的原图坐标交给画布，用于拖选高亮。</summary>
        public void SetWords(IReadOnlyList<OcrWordBox> words)
        {
            _words = words ?? Array.Empty<OcrWordBox>();
            _wordAnchor = _wordFocus = -1;
            _selectingWords = false;
            Invalidate();
        }

        public void ClearSelection()
        {
            _wordAnchor = _wordFocus = -1;
            Invalidate();
        }

        private PointF ToImage(Point point) =>
            new((point.X - _origin.X) / _zoom, (point.Y - _origin.Y) / _zoom);

        /// <summary>原图 + 涂鸦合成新位图；调用方负责 Dispose、交给剪贴板或交给 AdoptRendered。</summary>
        public Bitmap RenderWithDoodle()
        {
            var result = new Bitmap(_shot.Width, _shot.Height, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(result))
                g.DrawImage(_shot, new Rectangle(0, 0, _shot.Width, _shot.Height));
            Doodle.Bake(result, PointF.Empty);   // 笔画本身就是原图坐标
            return result;
        }

        /// <summary>覆盖保存后接管烘焙结果：清掉笔画层，保持原来的缩放与位置。</summary>
        public void AdoptRendered(Bitmap rendered)
        {
            Doodle.Clear();
            float zoom = _zoom;
            var origin = _origin;
            Shot = rendered;      // setter 释放旧图并 Fit
            SetZoom(zoom);
            _origin = origin;
            Invalidate();
        }

        private RectangleF WordRect(OcrWordBox word) => new(
            _origin.X + word.X * _zoom, _origin.Y + word.Y * _zoom,
            word.Width * _zoom, word.Height * _zoom);

        /// <summary>精确命中优先；nearest 时按「同行优先」找最近的词，方便拖到行尾或行间。</summary>
        private int HitWord(Point point, bool nearest)
        {
            int best = -1;
            double bestDistance = double.MaxValue;
            for (int i = 0; i < _words.Count; i++)
            {
                var rect = RectangleF.Inflate(WordRect(_words[i]), S(3), S(3));
                if (rect.Contains(point.X, point.Y)) return i;
                if (!nearest) continue;
                double dx = Math.Max(0, Math.Max(rect.Left - point.X, point.X - rect.Right));
                double dy = Math.Max(0, Math.Max(rect.Top - point.Y, point.Y - rect.Bottom));
                double distance = dx * dx + dy * dy * 4;
                if (distance < bestDistance) { bestDistance = distance; best = i; }
            }
            return best;
        }

        /// <summary>点到某个词矩形的距离，用于判断「是否算点在文字附近」。</summary>
        private double WordDistance(int index, Point point)
        {
            var rect = WordRect(_words[index]);
            double dx = Math.Max(0, Math.Max(rect.Left - point.X, point.X - rect.Right));
            double dy = Math.Max(0, Math.Max(rect.Top - point.Y, point.Y - rect.Bottom));
            return Math.Sqrt(dx * dx + dy * dy);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (_shot is null) { base.OnMouseDown(e); return; }
            if (DoodleMode && e.Button == MouseButtons.Left)
            {
                Doodle.Begin(ToImage(e.Location));
                Capture = true;
                Invalidate();
                base.OnMouseDown(e);
                return;
            }
            // 落在文字或文字附近 = 按词拖选；中键或远离文字的空白处 = 平移图片
            int hit = e.Button == MouseButtons.Left ? HitWord(e.Location, true) : -1;
            bool nearText = hit >= 0 && WordDistance(hit, e.Location) <= S(40);
            if (e.Button == MouseButtons.Left && nearText)
            {
                _selectingWords = true;
                _wordAnchor = _wordFocus = hit;
                Capture = true;
                Invalidate();
            }
            else if (e.Button == MouseButtons.Left || e.Button == MouseButtons.Middle)
            {
                _dragging = true;
                _last = e.Location;
                Capture = true;
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (Doodle.Drawing)
            {
                Doodle.Add(ToImage(e.Location));
                Invalidate();
                base.OnMouseMove(e);
                return;
            }
            if (_selectingWords)
            {
                int hit = HitWord(e.Location, true);
                if (hit >= 0 && hit != _wordFocus) { _wordFocus = hit; Invalidate(); }
            }
            else if (_dragging)
            {
                _origin.X += e.X - _last.X;
                _origin.Y += e.Y - _last.Y;
                _last = e.Location;
                Invalidate();
            }
            else if (e.Button == MouseButtons.None)
            {
                if (DoodleMode) { Cursor = Cursors.Cross; base.OnMouseMove(e); return; }
                int hover = HitWord(e.Location, true);
                Cursor = hover >= 0 && WordDistance(hover, e.Location) <= S(40)
                    ? Cursors.IBeam : Cursors.SizeAll;
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (Doodle.Drawing)
            {
                Doodle.End();
                Capture = false;
                Invalidate();
                base.OnMouseUp(e);
                return;
            }
            bool wasSelecting = _selectingWords;
            _selectingWords = false;
            _dragging = false;
            Capture = false;
            if (wasSelecting && _wordAnchor >= 0 && _wordFocus >= 0)
            {
                int lo = Math.Min(_wordAnchor, _wordFocus), hi = Math.Max(_wordAnchor, _wordFocus);
                var parts = new List<string>();
                for (int i = lo; i <= hi; i++) parts.Add(_words[i].Text);
                string text = OcrService.JoinWords(parts);   // 中文词之间不留空格，英文词之间保留
                if (text.Length > 0) WordsSelected?.Invoke(text);
            }
            base.OnMouseUp(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.Clear(Theme.SurfaceAlt);
            Theme.SetQuality(g);
            if (_shot is null)
            {
                Theme.DrawText(g, "正在载入图片…", Theme.FontSmall, Theme.TextDim, ClientRectangle,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                return;
            }

            // 放大到 2 倍以上改用最近邻，文字像素不糊；缩小时保持高质量插值
            g.InterpolationMode = _zoom >= 2f
                ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            var dest = new RectangleF(_origin.X, _origin.Y, _shot.Width * _zoom, _shot.Height * _zoom);
            g.DrawImage(_shot, dest);
            using var pen = new Pen(Theme.Border);
            g.DrawRectangle(pen, dest.X, dest.Y, dest.Width, dest.Height);

            // 拖选到的词用半透明蓝底高亮：文字本身仍是图片像素，效果与「选文本」一致
            if (_words.Count > 0 && _wordAnchor >= 0 && _wordFocus >= 0)
            {
                int lo = Math.Min(_wordAnchor, _wordFocus), hi = Math.Max(_wordAnchor, _wordFocus);
                using var highlight = new SolidBrush(Color.FromArgb(95, 92, 158, 240));
                for (int i = lo; i <= hi; i++)
                    g.FillRectangle(highlight, RectangleF.Inflate(WordRect(_words[i]), S(1), S(1)));
            }

            if (Doodle.HasStrokes || Doodle.Drawing)
                Doodle.Draw(g, p => new PointF(p.X * _zoom + _origin.X, p.Y * _zoom + _origin.Y), _zoom);

            Theme.DrawText(g, $"{_shot.Width} × {_shot.Height}", Theme.FontSmall, Theme.TextDim,
                new Rectangle(S(14), Height - S(28), Width - S(28), S(22)),
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
        }

    }
}
