using System.Drawing;
using System.Runtime.InteropServices;

namespace Toolbox.UI;

internal enum ButtonKind { Primary, Danger, Subtle, Ghost, Outline }

/// <summary>需要跟随主题刷新的控件实现此接口，主窗体切换主题时统一调用。</summary>
internal interface IThemed
{
    void ApplyTheme();
}

/// <summary>自绘面板：双缓冲 + 尺寸变化整面重绘，避免窗口缩放时留下旧像素残影。</summary>
internal class BufferedPanel : Panel
{
    public BufferedPanel()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }
}

internal sealed class ModernButton : Control, IThemed
{
    private static int S(int value) => Theme.S(value);

    private bool _hover, _pressed;

    public ButtonKind Kind { get; set; } = ButtonKind.Subtle;
    public Glyph Icon { get; set; } = Glyph.None;
    public int Radius { get; set; } = 8;

    public void ApplyTheme() => Invalidate();

    public ModernButton()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                 ControlStyles.SupportsTransparentBackColor | ControlStyles.Selectable, true);
        BackColor = Color.Transparent;
        Font = Theme.FontBase;
        Height = S(34);
        Cursor = Cursors.Hand;
        TabStop = false;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Theme.SetQuality(e.Graphics);
        var g = e.Graphics;
        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        var (fill, border, fg) = Palette();
        if (fill.A > 0 || border.A > 0)
            Theme.Box(g, rect, S(Radius), fill, border.A > 0 ? border : null);

        int iconW = Icon == Glyph.None ? 0 : S(20);
        int textW = string.IsNullOrEmpty(Text) ? 0 : TextRenderer.MeasureText(g, Text, Font).Width;
        int total = iconW + (textW > 0 ? textW + (iconW > 0 ? S(6) : 0) : 0);
        int start = Math.Max(0, (Width - total) / 2);

        if (Icon != Glyph.None)
        {
            int iconSize = S(18);
            GlyphPainter.Draw(g, Icon, new Rectangle(start, (Height - iconSize) / 2, iconSize, iconSize), fg);
            start += iconW + S(6);
        }
        if (textW > 0)
            Theme.DrawText(g, Text, Font, fg, new Rectangle(start, 0, Width - start, Height),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }

    private (Color Fill, Color Border, Color Text) Palette()
    {
        if (!Enabled) return (Theme.SurfaceAlt, Theme.Border, Theme.TextDim);
        return Kind switch
        {
            ButtonKind.Primary => (
                _pressed ? ControlPaint.Dark(Theme.Accent, 0.06f) : _hover ? ControlPaint.Light(Theme.Accent, 0.14f) : Theme.Accent,
                Theme.Alpha(Theme.Accent, 0), Theme.OnAccent),
            ButtonKind.Danger => (
                _hover ? Theme.Alpha(Theme.Danger, 42) : Theme.DangerDim,
                Theme.Alpha(Theme.Danger, 120), _hover ? Theme.Text : Theme.Danger),
            ButtonKind.Ghost => (
                _hover ? Theme.ElevatedHover : Theme.Alpha(Theme.Surface, 0),
                Theme.Alpha(Theme.Border, 0), _hover ? Theme.Text : Theme.TextMuted),
            ButtonKind.Outline => (
                _hover ? Theme.ElevatedHover : Theme.Alpha(Theme.Surface, 0),
                Theme.BorderStrong, Theme.TextMuted),
            _ => (
                _pressed ? Theme.Alpha(Theme.Text, 22) : _hover ? Theme.ElevatedHover : Theme.Elevated,
                Theme.Border, Theme.Text),
        };
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; _pressed = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { _pressed = true; Invalidate(); } base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _pressed = false; Invalidate(); base.OnMouseUp(e); }
}

/// <summary>带原生提示文字（cue banner）的搜索框。</summary>
internal sealed class SearchBox : Control, IThemed
{
    private static int S(int value) => Theme.S(value);

    private readonly TextBox _input;
    private readonly string _placeholder;
    private bool _hoverClear;
    private bool _focused;

    public event EventHandler QueryChanged;
    public string Query => _input.Text;
    public void FocusInput() { _input.Focus(); _input.SelectAll(); }
    public void SetQuery(string value) { _input.Text = value; }

    public void ApplyTheme()
    {
        BackColor = Theme.SurfaceAlt;
        _input.BackColor = Theme.SurfaceAlt;
        _input.ForeColor = Theme.Text;
        Invalidate();
    }

    public SearchBox(string placeholder = "搜索软件名称、发布者或安装路径…")
    {
        _placeholder = placeholder;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        _input = new TextBox
        {
            BorderStyle = BorderStyle.None,
            BackColor = Theme.SurfaceAlt,
            ForeColor = Theme.Text,
            Font = Theme.FontBase,
            Dock = DockStyle.None,
        };
        _input.TextChanged += (_, _) => { Invalidate(); QueryChanged?.Invoke(this, EventArgs.Empty); };
        _input.Enter += (_, _) => { _focused = true; Invalidate(); };
        _input.Leave += (_, _) => { _focused = false; Invalidate(); };
        _input.HandleCreated += (_, _) => SendMessage(_input.Handle, 0x1501, (IntPtr)1, _placeholder);
        Controls.Add(_input);
        Height = S(36);
        BackColor = Theme.SurfaceAlt;
        LayoutInput();
    }

    private void LayoutInput()
    {
        if (_input is null || _input.IsDisposed) return;
        _input.SetBounds(S(36), S(7), Math.Max(S(40), Width - S(36) - S(30)), Height - S(14));
    }

    protected override void OnResize(EventArgs e) { LayoutInput(); base.OnResize(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        Theme.SetQuality(e.Graphics);
        var g = e.Graphics;
        Theme.Box(g, new Rectangle(0, 0, Width - 1, Height - 1), S(9),
            _focused ? Theme.Elevated : Theme.SurfaceAlt,
            _focused ? Theme.Accent : Theme.Border);
        int glyph = S(18);
        GlyphPainter.Draw(g, Glyph.Search, new Rectangle(S(10), (Height - glyph) / 2, glyph, glyph),
            _focused ? Theme.Accent : Theme.TextDim);
        if (!string.IsNullOrEmpty(_input.Text))
        {
            var clearRect = ClearRect();
            if (_hoverClear) Theme.Box(g, clearRect, S(8), Theme.ElevatedHover);
            GlyphPainter.Draw(g, Glyph.Close,
                new Rectangle(clearRect.X + S(3), clearRect.Y + S(3), clearRect.Width - S(6), clearRect.Height - S(6)),
                Theme.TextMuted, 1.4f);
        }
        _input.BackColor = _focused ? Theme.Elevated : Theme.SurfaceAlt;
    }

    private Rectangle ClearRect() => new(Width - S(28), (Height - S(18)) / 2, S(18), S(18));

    protected override void OnMouseMove(MouseEventArgs e)
    {
        bool over = !string.IsNullOrEmpty(_input.Text) && ClearRect().Contains(e.Location);
        if (over != _hoverClear) { _hoverClear = over; Invalidate(); }
        Cursor = over ? Cursors.Hand : Cursors.IBeam;
        base.OnMouseMove(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (!string.IsNullOrEmpty(_input.Text) && ClearRect().Contains(e.Location))
        {
            _input.Text = "";
            _input.Focus();
            Invalidate();
            return;
        }
        _input.Focus();
        base.OnMouseDown(e);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);
}

internal sealed class ModernToggle : Control
{
    private static int S(int value) => Theme.S(value);

    private bool _checked, _hover;
    public bool Checked
    {
        get => _checked;
        set { if (_checked != value) { _checked = value; Invalidate(); CheckedChanged?.Invoke(this, EventArgs.Empty); } }
    }
    public string Label { get; set; } = "";
    public event EventHandler CheckedChanged;

    public ModernToggle()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Height = S(24);
        Cursor = Cursors.Hand;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Theme.SetQuality(e.Graphics);
        var g = e.Graphics;
        int trackW = S(30), trackH = S(16);
        var track = new Rectangle(0, (Height - trackH) / 2, trackW, trackH);
        Theme.Box(g, track, trackH / 2, _checked ? Theme.Accent : (_hover ? Theme.BorderStrong : Theme.Border));
        int knob = S(12);
        g.FillEllipse(Theme.B(Theme.C("#FFFFFF")),
            new Rectangle(_checked ? track.Right - knob - S(2) : track.X + S(2), track.Y + S(2), knob, knob));
        if (!string.IsNullOrEmpty(Label))
            Theme.DrawText(g, Label, Theme.FontSmall, _hover ? Theme.Text : Theme.TextMuted,
                new Rectangle(trackW + S(8), 0, Width - trackW - S(8), Height),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnClick(EventArgs e) { Checked = !Checked; base.OnClick(e); }
}

internal enum LogLevel { Info, Ok, Warn, Error }

internal sealed class LogPanel : Control, IThemed
{
    private static int S(int value) => Theme.S(value);
    private static int LineHeight => S(21);

    private readonly List<(string Time, string Message, LogLevel Level)> _lines = new();
    private int _offset;

    public void ApplyTheme()
    {
        BackColor = Theme.Surface;
        Invalidate();
    }

    public LogPanel()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Surface;
        Font = Theme.FontSmall;
    }

    public void Append(string message, LogLevel level = LogLevel.Info)
    {
        _lines.Add((DateTime.Now.ToString("HH:mm:ss"), message, level));
        if (_lines.Count > 300) _lines.RemoveAt(0);
        _offset = MaxOffset;
        Invalidate();
    }

    public void ClearAll() { _lines.Clear(); _offset = 0; Invalidate(); }

    private int MaxOffset => Math.Max(0, _lines.Count * LineHeight - ClientSize.Height);

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        _offset = Math.Clamp(_offset - Math.Sign(e.Delta) * LineHeight * 3, 0, MaxOffset);
        Invalidate();
        base.OnMouseWheel(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Theme.SetQuality(e.Graphics);
        var g = e.Graphics;
        g.Clear(Theme.Surface);
        g.DrawLine(Theme.P(Theme.Border), 0, 0, Width, 0);

        int first = Math.Max(0, _offset / LineHeight);
        int visible = ClientSize.Height / LineHeight + 2;
        for (int i = first; i < Math.Min(_lines.Count, first + visible); i++)
        {
            var line = _lines[i];
            int y = S(8) + i * LineHeight - _offset;
            if (y < -LineHeight || y > Height) continue;
            Color color = line.Level switch
            {
                LogLevel.Ok => Theme.Success,
                LogLevel.Warn => Theme.Warning,
                LogLevel.Error => Theme.Danger,
                _ => Theme.Accent,
            };
            g.FillRectangle(Theme.B(color), new Rectangle(S(16), y + S(7), S(4), S(4)));
            Theme.DrawText(g, line.Time, Theme.FontMono, Theme.TextDim, new Rectangle(S(28), y, S(62), LineHeight));
            Theme.DrawText(g, line.Message, Theme.FontSmall,
                line.Level == LogLevel.Info ? Theme.TextMuted : color,
                new Rectangle(S(96), y, Width - S(112), LineHeight));
        }
        if (_lines.Count == 0)
            Theme.DrawText(g, "就绪 · 选择软件后可直接卸载，卸载过程与结果会显示在这里",
                Theme.FontSmall, Theme.TextDim, new Rectangle(S(16), S(8), Width - S(32), LineHeight));
    }
}
