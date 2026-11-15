using System.Drawing;
using System.Windows.Forms;

namespace Toolbox.UI;

/// <summary>
/// 全屏框选覆盖层。构造前已完成屏幕抓取，因此遮罩本身不会出现在截图里。
/// 交互：拖动框选出选区 → 松开后进入「可调整」状态（拖边角改大小、拖内部移动）
/// → 点击选区内 / 按 Enter / 点「保存」才真正落盘；Esc 或右键取消。
/// </summary>
internal sealed class CaptureOverlay : Form
{
    /// <summary>可拖拽的部位：整块移动与八方向缩放。</summary>
    private enum Grip { None, Move, N, S, E, W, NE, NW, SE, SW }

    private const int MinSize = 4;

    private readonly Bitmap _screen;
    private readonly Bitmap _dimmed;
    public DoodleLayer Doodle { get; } = new();
    private readonly DoodleBar _bar;
    private bool _doodleMode;          // 预压暗的整屏图，避免每帧做全屏 alpha 混合
    private readonly Rectangle[] _buttons = new Rectangle[4];
    private Rectangle _selection;
    private Rectangle _origin;      // 本次拖拽开始时的选区
    private Point _press;           // 本次按下点
    private Point _start;           // 初次框选的起点
    private Grip _handle = Grip.None;
    private bool _dragging;         // 正在初次框选
    private bool _moved;            // 本次拖拽是否真的产生了位移
    private int _hoverButton = -1;
    private string _sizeText = "";
    private Size _sizeTextSize;
    private readonly Size[] _hintSizes = new Size[3];

    private static int S(int value) => Theme.S(value);

    public Rectangle Selection => _selection;

    public CaptureOverlay(Bitmap screen, Rectangle virtualBounds)
    {
        _screen = screen;
        _dimmed = BuildDimmed(screen, 120);
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = virtualBounds;
        ShowInTaskbar = false;
        TopMost = true;
        DoubleBuffered = true;
        BackColor = Color.Black;
        Cursor = Cursors.Cross;
        KeyPreview = true;
        AutoScaleMode = AutoScaleMode.None;

        Paint += Draw;
        MouseDown += OnMouseDown;
        MouseMove += OnMouseMove;
        MouseUp += OnMouseUp;
        KeyDown += OnKeyDown;
        Resize += (_, _) => { if (_bar is not null) LayoutBar(); };

        _bar = new DoodleBar(DoodleAction.Undo | DoodleAction.Clear | DoodleAction.Mode);
        _bar.BrushChanged += () =>
        {
            Doodle.BrushColor = _bar.BrushColor;
            Doodle.BrushWidth = _bar.BrushWidth;
        };
        _bar.UndoRequested += () => { Doodle.Undo(); PaintNow(); };
        _bar.ClearRequested += () => { Doodle.Clear(); PaintNow(); };
        _bar.ModeRequested += () => ToggleDoodleMode();
        Doodle.Changed += () => { _bar.HasStrokes = Doodle.HasStrokes; _bar.Invalidate(); PaintNow(); };
        Doodle.BrushColor = _bar.BrushColor;
        Doodle.BrushWidth = _bar.BrushWidth;
        Controls.Add(_bar);
        _bar.Visible = false;   // 点「画笔」按钮后才出现
        LayoutBar();
    }

    private void LayoutBar() => _bar.SetBounds(
        (ClientSize.Width - _bar.Width) / 2,
        Math.Max(S(6), ClientSize.Height - _bar.Height - S(70)),
        _bar.Width, _bar.Height);

    /// <summary>涂鸦工具条跟在「重选/取消/画笔/保存」这一行下方，放不下就翻到上方。</summary>
    private void LayoutDoodleBar(int buttonY, int buttonH, int buttonX)
    {
        int below = buttonY + buttonH + S(8);
        int y = below + _bar.Height <= ClientSize.Height - S(6)
            ? below : buttonY - _bar.Height - S(8);
        y = Math.Clamp(y, S(6), Math.Max(S(6), ClientSize.Height - _bar.Height - S(6)));
        int x = Math.Clamp(buttonX, S(6), Math.Max(S(6), ClientSize.Width - _bar.Width - S(6)));
        _bar.SetBounds(x, y, _bar.Width, _bar.Height);
    }

    private void ToggleDoodleMode()
    {
        _doodleMode = !_doodleMode;
        _bar.Visible = _doodleMode;
        _bar.ModeLabel = "框选";
        _bar.Invalidate();
        Cursor = _doodleMode ? Cursors.Cross : Cursors.Default;
        PaintNow();
    }

    /// <summary>一次性把整屏压暗成一张图：之后每帧只要贴图，不再逐像素混合。</summary>
    private static Bitmap BuildDimmed(Bitmap source, int alpha)
    {
        var bmp = new Bitmap(source.Width, source.Height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(bmp);
        g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
        g.DrawImage(source, 0, 0, source.Width, source.Height);
        g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceOver;
        using var dim = new SolidBrush(Color.FromArgb(alpha, Color.Black));
        g.FillRectangle(dim, 0, 0, source.Width, source.Height);
        return bmp;
    }

    /// <summary>
    /// 请求重绘。WinForms 的 Invalidate 本身会把多次请求合并成一次 WM_PAINT，
    /// 所以这里不用额外定时器节流——加了反而把帧率压在 30fps 左右。
    /// </summary>
    private void RequestPaint() => Invalidate();

    /// <summary>需要立刻反映的操作（松手 / 按键）立刻处理，不等下一轮消息循环。</summary>
    private void PaintNow()
    {
        Invalidate();
        Update();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _dimmed.Dispose();
        base.Dispose(disposing);
    }

    /// <summary>
    /// 没有 owner 的模态窗口偶尔不会抢到焦点，导致 Esc / 方向键失灵。
    /// 显示后主动激活一次，保证键盘操作始终可用。
    /// </summary>
    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Activate();
        BringToFront();
    }

    // ── 绘制 ────────────────────────────────────────────────

    private void Draw(object sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        Theme.SetQuality(g);
        // 整屏位图是 1:1 贴的，用高质量双三次插值纯属浪费：4K 下能差出十几毫秒。
        const System.Drawing.Drawing2D.CompositingMode Copy = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
        g.CompositingMode = Copy;
        if (ClientSize.Width == _dimmed.Width && ClientSize.Height == _dimmed.Height)
            g.DrawImageUnscaled(_dimmed, 0, 0);          // 1:1 贴图，无缩放无混合
        else
            g.DrawImage(_dimmed, 0, 0, ClientSize.Width, ClientSize.Height);

        bool hasSelection = _selection.Width >= MinSize && _selection.Height >= MinSize;
        if (hasSelection)
            g.DrawImage(_screen, _selection, _selection, GraphicsUnit.Pixel);
        g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceOver;

        if (hasSelection)
        {
            DrawFrame(g, _selection, showHandles: !_dragging);
            DrawSizeLabel(g, _selection);
        }
        else
        {
            DrawCrosshair(g);
        }

        if (hasSelection && !_dragging) DrawToolbar(g);
        if (Doodle.HasStrokes || Doodle.Drawing)
        {
            if (hasSelection) g.SetClip(_selection);
            Doodle.Draw(g, p => p, 1f);
            g.ResetClip();
        }
        DrawHint(g, hasSelection);
    }

    /// <summary>白色描边 + 八个握把，明确告诉用户「这个框现在可以调」。</summary>
    private void DrawFrame(Graphics g, Rectangle r, bool showHandles)
    {
        using var border = new Pen(Color.FromArgb(235, 255, 255, 255), S(1));
        g.DrawRectangle(border, r.X, r.Y, Math.Max(0, r.Width - 1), Math.Max(0, r.Height - 1));
        if (!showHandles) return;

        using var fill = new SolidBrush(Color.White);
        using var edge = new Pen(Color.FromArgb(90, 0, 0, 0));
        foreach (var (_, box) in Handles())
        {
            g.FillRectangle(fill, box);
            g.DrawRectangle(edge, box);
        }
    }

    private IEnumerable<(Grip Kind, Rectangle Box)> Handles()
    {
        var r = _selection;
        int d = S(9), h = d / 2;
        yield return (Grip.NW, new Rectangle(r.Left - h, r.Top - h, d, d));
        yield return (Grip.N, new Rectangle(r.Left + r.Width / 2 - h, r.Top - h, d, d));
        yield return (Grip.NE, new Rectangle(r.Right - h, r.Top - h, d, d));
        yield return (Grip.W, new Rectangle(r.Left - h, r.Top + r.Height / 2 - h, d, d));
        yield return (Grip.E, new Rectangle(r.Right - h, r.Top + r.Height / 2 - h, d, d));
        yield return (Grip.SW, new Rectangle(r.Left - h, r.Bottom - h, d, d));
        yield return (Grip.S, new Rectangle(r.Left + r.Width / 2 - h, r.Bottom - h, d, d));
        yield return (Grip.SE, new Rectangle(r.Right - h, r.Bottom - h, d, d));
    }

    private void DrawSizeLabel(Graphics g, Rectangle r)
    {
        string size = $"{r.Width} × {r.Height}";
        if (size != _sizeText)          // 每帧测量文字很贵，尺寸没变就复用
        {
            _sizeText = size;
            _sizeTextSize = TextRenderer.MeasureText(size, Theme.FontSmall);
        }
        var text = _sizeTextSize;
        var label = new Rectangle(r.X, r.Y - text.Height - S(6), text.Width + S(14), text.Height + S(4));
        if (label.Y < 0) label.Y = r.Y + S(6);                       // 顶到屏幕外就挪进选区内
        label.X = Math.Max(0, Math.Min(label.X, ClientSize.Width - label.Width));
        using var bg = new SolidBrush(Color.FromArgb(215, 20, 28, 40));
        g.FillRectangle(bg, label);
        TextRenderer.DrawText(g, size, Theme.FontSmall, label, Color.White,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    /// <summary>还没有选区时给十字辅助线，方便对准。</summary>
    private void DrawCrosshair(Graphics g)
    {
        Point p = PointToClient(Cursor.Position);
        if (!ClientRectangle.Contains(p)) return;
        using var pen = new Pen(Color.FromArgb(120, 255, 255, 255));
        pen.DashStyle = System.Drawing.Drawing2D.DashStyle.Dash;
        g.DrawLine(pen, 0, p.Y, ClientSize.Width, p.Y);
        g.DrawLine(pen, p.X, 0, p.X, ClientSize.Height);
    }

    private void DrawHint(Graphics g, bool hasSelection)
    {
        int index = _doodleMode ? 2 : (hasSelection ? 1 : 0);
        string hint = _doodleMode
            ? "拖动涂鸦　·　D 切回框选　·　Ctrl+Z 撤销　·　Enter 保存　·　Esc 取消"
            : hasSelection
                ? "拖动边角调整　·　点「画笔」可涂鸦　·　Enter 保存　·　Esc 取消"
                : "拖动框选　·　点「画笔」可涂鸦　·　Esc 取消";
        if (_hintSizes[index].IsEmpty)      // 固定两句文案，量一次即可
            _hintSizes[index] = TextRenderer.MeasureText(hint, Theme.FontSmall);
        var text = _hintSizes[index];
        var box = new Rectangle(0, S(14), text.Width + S(28), text.Height + S(10));
        box.X = (ClientSize.Width - box.Width) / 2;
        using var bg = new SolidBrush(Color.FromArgb(205, 20, 28, 40));
        g.FillRectangle(bg, box);
        TextRenderer.DrawText(g, hint, Theme.FontSmall, box, Color.White,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    private void DrawToolbar(Graphics g)
    {
        LayoutButtons();
        DrawButton(g, 0, "重选", primary: false);
        DrawButton(g, 1, "取消", primary: false);
        DrawButton(g, 2, "画笔", primary: _doodleMode);
        DrawButton(g, 3, "✓  保存", primary: true);
    }

    private void DrawButton(Graphics g, int index, string text, bool primary)
    {
        var box = _buttons[index];
        bool hover = _hoverButton == index;
        Color fill = primary
            ? (hover ? Color.FromArgb(255, 74, 144, 255) : Color.FromArgb(255, 45, 122, 240))
            : (hover ? Color.FromArgb(255, 58, 66, 80) : Color.FromArgb(240, 32, 39, 51));
        Theme.Box(g, box, S(8), fill, primary ? null : Color.FromArgb(70, 255, 255, 255));
        TextRenderer.DrawText(g, text, Theme.FontSmall, box, Color.White,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    // ── 命中判定 ────────────────────────────────────────────

    private Grip HitHandle(Point p)
    {
        if (_selection.Width < MinSize) return Grip.None;
        foreach (var (kind, box) in Handles())
            if (box.Contains(p)) return kind;
        return _selection.Contains(p) ? Grip.Move : Grip.None;
    }

    private int HitButton(Point p)
    {
        if (_selection.Width < MinSize) return -1;
        for (int i = 0; i < _buttons.Length; i++)
            if (_buttons[i].Contains(p)) return i;
        return -1;
    }

    private void LayoutButtons()
    {
        int h = S(36), gap = S(8);
        int w0 = S(78), w1 = S(78), w2 = S(78), w3 = S(100);
        int total = w0 + w1 + w2 + w3 + gap * 3;
        int y = _selection.Bottom + S(12);
        if (y + h > ClientSize.Height - S(6)) y = _selection.Top - S(12) - h;   // 下方放不下就翻到上方
        if (y < S(6)) y = Math.Min(_selection.Bottom - h - S(6), ClientSize.Height - h - S(6));
        int x = Math.Clamp(_selection.Right - total, S(6), Math.Max(S(6), ClientSize.Width - total - S(6)));
        _buttons[0] = new Rectangle(x, y, w0, h);
        _buttons[1] = new Rectangle(x + w0 + gap, y, w1, h);
        _buttons[2] = new Rectangle(x + w0 + gap + w1 + gap, y, w2, h);
        _buttons[3] = new Rectangle(x + w0 + gap + w1 + gap + w2 + gap, y, w3, h);
        LayoutDoodleBar(y, h, x);
    }

    // ── 鼠标 ────────────────────────────────────────────────

    private void OnMouseDown(object sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Right) { Cancel(); return; }
        if (e.Button != MouseButtons.Left) return;

        // 涂鸦模式下点自绘的「重选 / 取消 / 画笔 / 保存」按钮仍然生效，不要被画笔吞掉
        if (_doodleMode && HitButton(e.Location) < 0)
        {
            Doodle.Begin(e.Location);
            PaintNow();
            return;
        }

        if (HitButton(e.Location) >= 0) return;   // 按钮在 MouseUp 里统一处理，避免误触发起框选

        var hit = HitHandle(e.Location);
        if (hit != Grip.None)
        {
            _handle = hit;
            _press = e.Location;
            _origin = _selection;
            _moved = false;
            return;
        }

        // 点在选区外：重新开始框选
        _selection = Rectangle.Empty;
        _handle = Grip.None;
        _dragging = true;
        _start = e.Location;
        PaintNow();
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (Doodle.Drawing) { Doodle.Add(e.Location); RequestPaint(); return; }
        if (_dragging)
        {
            _selection = Normalize(_start, e.Location);
            RequestPaint();      // 合并高频移动，避免重绘排队导致越拖越卡
            return;
        }

        if (_handle != Grip.None)
        {
            _moved = true;
            ApplyDrag(e.Location);
            RequestPaint();
            return;
        }

        int hover = HitButton(e.Location);
        if (hover != _hoverButton) { _hoverButton = hover; RequestPaint(); }

        // 涂鸦模式固定十字光标（与放大预览一致），只有悬停自绘按钮才是手型。
        // 否则 HitHandle 会把它改成 SizeAll / 缩放箭头，看起来还像在调整选区。
        var next = hover >= 0 ? Cursors.Hand
            : _doodleMode ? Cursors.Cross : CursorFor(HitHandle(e.Location));
        if (Cursor != next) Cursor = next;
    }

    private static Cursor CursorFor(Grip hit) => hit switch
    {
        Grip.NW or Grip.SE => Cursors.SizeNWSE,
        Grip.NE or Grip.SW => Cursors.SizeNESW,
        Grip.N or Grip.S => Cursors.SizeNS,
        Grip.E or Grip.W => Cursors.SizeWE,
        Grip.Move => Cursors.SizeAll,
        _ => Cursors.Cross,
    };

    private void OnMouseUp(object sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;

        if (Doodle.Drawing) { Doodle.End(); PaintNow(); return; }

        int button = HitButton(e.Location);
        if (button >= 0) { ActivateButton(button); return; }

        if (_dragging)
        {
            _dragging = false;
            _selection = Normalize(_start, e.Location);
            if (_selection.Width < MinSize || _selection.Height < MinSize) _selection = Rectangle.Empty;
            PaintNow();
            return;
        }

        if (_handle == Grip.None) return;

        // 在选区内按下又没有拖动 = 一次「确认点击」，此时才保存
        bool confirm = _handle == Grip.Move && !_moved;
        _handle = Grip.None;
        if (confirm) { Confirm(); return; }
        if (_selection.Width < MinSize || _selection.Height < MinSize) _selection = Rectangle.Empty;
        PaintNow();
    }

    private void ActivateButton(int index)
    {
        switch (index)
        {
            case 0: _selection = Rectangle.Empty; PaintNow(); break;
            case 1: Cancel(); break;
            case 2: ToggleDoodleMode(); break;   // 「画笔」按钮：弹出涂鸦工具条
            default: Confirm(); break;
        }
    }

    /// <summary>缩放：只改被拖动的那条边/角；移动：整块平移并夹在工作区内。</summary>
    private void ApplyDrag(Point p)
    {
        if (_handle == Grip.Move)
        {
            var moved = _origin;
            moved.Offset(p.X - _press.X, p.Y - _press.Y);
            moved.X = Math.Clamp(moved.X, 0, Math.Max(0, ClientSize.Width - moved.Width));
            moved.Y = Math.Clamp(moved.Y, 0, Math.Max(0, ClientSize.Height - moved.Height));
            _selection = moved;
            return;
        }

        int left = _origin.Left, top = _origin.Top, right = _origin.Right, bottom = _origin.Bottom;
        if (_handle is Grip.W or Grip.NW or Grip.SW) left = p.X;
        if (_handle is Grip.E or Grip.NE or Grip.SE) right = p.X;
        if (_handle is Grip.N or Grip.NW or Grip.NE) top = p.Y;
        if (_handle is Grip.S or Grip.SW or Grip.SE) bottom = p.Y;

        var rect = Rectangle.FromLTRB(Math.Min(left, right), Math.Min(top, bottom),
                                      Math.Max(left, right), Math.Max(top, bottom));
        _selection = Rectangle.Intersect(rect, ClientRectangle);
    }

    private Rectangle Normalize(Point a, Point b)
    {
        var rect = Rectangle.FromLTRB(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y),
                                      Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
        return Rectangle.Intersect(rect, ClientRectangle);
    }

    // ── 键盘 ────────────────────────────────────────────────

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Control && e.KeyCode == Keys.Z) { Doodle.Undo(); PaintNow(); return; }
        switch (e.KeyCode)
        {
            case Keys.D:
                ToggleDoodleMode();
                break;
            case Keys.Escape:
                Cancel();
                break;
            case Keys.Enter:
            case Keys.Space:
                e.Handled = true;
                if (_selection.Width >= MinSize) Confirm();
                break;
            case Keys.R:
                _selection = Rectangle.Empty;
                _handle = Grip.None;
                PaintNow();
                break;
            case Keys.Left:
            case Keys.Right:
            case Keys.Up:
            case Keys.Down:
                Nudge(e);
                break;
        }
    }

    /// <summary>方向键微调位置，Shift 加速；选区为空时忽略。</summary>
    private void Nudge(KeyEventArgs e)
    {
        if (_selection.Width < MinSize) return;
        int step = e.Shift ? S(10) : 1;
        int dx = e.KeyCode == Keys.Left ? -step : e.KeyCode == Keys.Right ? step : 0;
        int dy = e.KeyCode == Keys.Up ? -step : e.KeyCode == Keys.Down ? step : 0;
        var moved = _selection;
        moved.Offset(dx, dy);
        moved.X = Math.Clamp(moved.X, 0, Math.Max(0, ClientSize.Width - moved.Width));
        moved.Y = Math.Clamp(moved.Y, 0, Math.Max(0, ClientSize.Height - moved.Height));
        _selection = moved;
        e.Handled = true;
        PaintNow();
    }

    // ── 收尾 ────────────────────────────────────────────────

    private void Confirm()
    {
        var rect = Rectangle.Intersect(_selection, ClientRectangle);
        if (rect.Width < MinSize || rect.Height < MinSize) return;   // 太小就不响应，让用户继续调整
        _selection = rect;
        DialogResult = DialogResult.OK;
        Close();
    }

    private void Cancel()
    {
        _selection = Rectangle.Empty;
        DialogResult = DialogResult.Cancel;
        Close();
    }
}
