using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Toolbox.UI;

[Flags]
internal enum DoodleAction { None = 0, Undo = 1, Clear = 2, Save = 4, Copy = 8, Overwrite = 16, Mode = 32, Exit = 64 }

/// <summary>一条笔画。点存在宿主坐标系里：查看器是原图像素，截图覆盖层是全屏客户区像素。</summary>
internal sealed class DoodleStroke
{
    public Color Color { get; }
    public float Width { get; }
    public List<PointF> Points { get; } = new();

    public DoodleStroke(Color color, float width) { Color = color; Width = width; }
}

/// <summary>涂鸦图层：两处预览共用。绘制时由宿主提供坐标映射，保存时提供偏移即可烘焙。</summary>
internal sealed class DoodleLayer
{
    private readonly List<DoodleStroke> _strokes = new();
    private DoodleStroke _active;

    public Color BrushColor { get; set; } = Color.FromArgb(255, 229, 72, 78);
    public float BrushWidth { get; set; } = 5f;
    public bool HasStrokes => _strokes.Count > 0;
    public bool Drawing => _active is not null;
    public event Action Changed;

    public void Begin(PointF point)
    {
        _active = new DoodleStroke(BrushColor, BrushWidth);
        _active.Points.Add(point);
    }

    public void Add(PointF point)
    {
        if (_active is null) return;
        var last = _active.Points[^1];
        if (Math.Abs(last.X - point.X) + Math.Abs(last.Y - point.Y) < 1f) return;
        _active.Points.Add(point);
    }

    public void End()
    {
        if (_active is null) return;
        if (_active.Points.Count == 1) _active.Points.Add(_active.Points[0]);   // 单击也留下一个圆点
        _strokes.Add(_active);
        _active = null;
        Changed?.Invoke();
    }

    public void Undo()
    {
        if (_strokes.Count == 0) return;
        _strokes.RemoveAt(_strokes.Count - 1);
        Changed?.Invoke();
    }

    public void Clear()
    {
        _strokes.Clear();
        _active = null;
        Changed?.Invoke();
    }

    public void Draw(Graphics g, Func<PointF, PointF> map, float scale)
    {
        Theme.SetQuality(g);
        foreach (var stroke in _strokes) DrawStroke(g, stroke, map, scale);
        if (_active is not null) DrawStroke(g, _active, map, scale);
    }

    private static void DrawStroke(Graphics g, DoodleStroke stroke, Func<PointF, PointF> map, float scale)
    {
        if (stroke.Points.Count == 0) return;
        var points = new PointF[stroke.Points.Count];
        for (int i = 0; i < points.Length; i++) points[i] = map(stroke.Points[i]);
        float width = Math.Max(1f, stroke.Width * scale);
        using var pen = new Pen(stroke.Color, width)
        { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        if (points.Length == 1)
        {
            using var brush = new SolidBrush(stroke.Color);
            g.FillEllipse(brush, points[0].X - width / 2, points[0].Y - width / 2, width, width);
        }
        else g.DrawLines(pen, points);
    }

    /// <summary>保存时把笔画烘焙到目标位图；offset 是宿主坐标减去裁剪区左上角。</summary>
    public void Bake(Bitmap target, PointF offset)
    {
        using var g = Graphics.FromImage(target);
        Draw(g, p => new PointF(p.X - offset.X, p.Y - offset.Y), 1f);
    }
}

/// <summary>浮动画笔工具条：颜色、粗细、撤销/清空/保存/复制/完成/模式切换，按宿主需要组合。</summary>
internal sealed class DoodleBar : BufferedPanel
{
    private static int S(int value) => Theme.S(value);
    private static readonly Color[] Palette =
    {
        Color.FromArgb(255, 229, 72, 78),    // 红
        Color.FromArgb(255, 245, 158, 11),   // 橙
        Color.FromArgb(255, 250, 204, 21),   // 黄
        Color.FromArgb(255, 34, 197, 94),    // 绿
        Color.FromArgb(255, 59, 130, 246),   // 蓝
        Color.FromArgb(255, 255, 255, 255),  // 白
    };
    private static readonly float[] Widths = { 2f, 5f, 10f };

    private readonly DoodleAction _actions;
    private readonly List<(Rectangle Rect, Action Click)> _hits = new();
    private int _hover = -1;

    public Color BrushColor { get; private set; } = Palette[0];
    public float BrushWidth { get; private set; } = Widths[1];
    public bool HasStrokes { get; set; }
    public string ModeLabel { get; set; } = "画笔";

    public event Action BrushChanged, UndoRequested, ClearRequested,
        SaveRequested, CopyRequested, OverwriteRequested, ExitRequested, ModeRequested;

    public DoodleBar(DoodleAction actions)
    {
        _actions = actions;
        Height = S(46);
        Width = S(14) + Palette.Length * (S(22) + S(8)) + S(10)
                + Widths.Length * (S(26) + S(6)) + S(10)
                + Buttons().Sum(b => b.Width + S(6)) + S(14);
        BackColor = Color.Transparent;
        Cursor = Cursors.Hand;
    }

    private IEnumerable<(string Text, int Width, bool NeedsStrokes, Action Raise)> Buttons()
    {
        if ((_actions & DoodleAction.Undo) != 0)
            yield return ("撤销", S(52), true, () => UndoRequested?.Invoke());
        if ((_actions & DoodleAction.Clear) != 0)
            yield return ("清空", S(52), true, () => ClearRequested?.Invoke());
        if ((_actions & DoodleAction.Save) != 0)
            yield return ("保存副本", S(76), true, () => SaveRequested?.Invoke());
        if ((_actions & DoodleAction.Copy) != 0)
            yield return ("复制图片", S(76), true, () => CopyRequested?.Invoke());
        if ((_actions & DoodleAction.Overwrite) != 0)
            yield return ("保存", S(52), false, () => OverwriteRequested?.Invoke());
        if ((_actions & DoodleAction.Exit) != 0)
            yield return ("退出", S(52), false, () => ExitRequested?.Invoke());
        if ((_actions & DoodleAction.Mode) != 0)
            yield return (ModeLabel, S(64), false, () => ModeRequested?.Invoke());
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Theme.SetQuality(g);
        _hits.Clear();
        Theme.Box(g, new Rectangle(0, 0, Width - 1, Height - 1), S(10),
            Color.FromArgb(238, 28, 34, 46), Color.FromArgb(60, 255, 255, 255));

        int x = S(14), cy = Height / 2;
        for (int i = 0; i < Palette.Length; i++)
        {
            var rect = new Rectangle(x, cy - S(11), S(22), S(22));
            if (Palette[i].ToArgb() == BrushColor.ToArgb())
            {
                using var ring = new Pen(Color.White, S(2));
                g.DrawEllipse(ring, Rectangle.Inflate(rect, S(3), S(3)));
            }
            using (var brush = new SolidBrush(Palette[i])) g.FillEllipse(brush, rect);
            int index = i;
            _hits.Add((Rectangle.Inflate(rect, S(3), S(3)), () =>
            {
                BrushColor = Palette[index];
                BrushChanged?.Invoke();
            }));
            x += S(22) + S(8);
        }
        DrawDivider(g, ref x);

        for (int i = 0; i < Widths.Length; i++)
        {
            var box = new Rectangle(x, cy - S(13), S(26), S(26));
            if (Math.Abs(Widths[i] - BrushWidth) < 0.1f)
                Theme.Box(g, Rectangle.Inflate(box, S(2), S(2)), S(6), Color.FromArgb(70, 255, 255, 255));
            int d = S(3 + i * 5);
            using (var brush = new SolidBrush(Color.White))
                g.FillEllipse(brush, new Rectangle(box.X + (box.Width - d) / 2,
                    box.Y + (box.Height - d) / 2, d, d));
            int index = i;
            _hits.Add((box, () =>
            {
                BrushWidth = Widths[index];
                BrushChanged?.Invoke();
            }));
            x += S(26) + S(6);
        }
        DrawDivider(g, ref x);

        foreach (var (text, width, needsStrokes, raise) in Buttons())
        {
            var rect = new Rectangle(x, S(8), width, Height - S(16));
            bool enabled = !needsStrokes || HasStrokes;
            int index = _hits.Count;
            Theme.Box(g, rect, S(6),
                _hover == index && enabled ? Color.FromArgb(85, 255, 255, 255) : Color.FromArgb(40, 255, 255, 255));
            Theme.DrawText(g, text, Theme.FontSmall,
                enabled ? Color.White : Color.FromArgb(110, 255, 255, 255), rect,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            if (enabled) _hits.Add((rect, raise));
            else _hits.Add((Rectangle.Empty, () => { }));   // 保持索引与绘制一致
            x += width + S(6);
        }
    }

    private void DrawDivider(Graphics g, ref int x)
    {
        x += S(4);
        using var pen = new Pen(Color.FromArgb(60, 255, 255, 255));
        g.DrawLine(pen, x, S(10), x, Height - S(10));
        x += S(10);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        for (int i = 0; i < _hits.Count; i++)
            if (!_hits[i].Rect.IsEmpty && _hits[i].Rect.Contains(e.Location))
            {
                _hits[i].Click();
                Invalidate();
                return;
            }
        base.OnMouseDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int hover = -1;
        for (int i = 0; i < _hits.Count; i++)
            if (!_hits[i].Rect.IsEmpty && _hits[i].Rect.Contains(e.Location)) { hover = i; break; }
        if (hover != _hover) { _hover = hover; Invalidate(); }
        base.OnMouseMove(e);
    }
}
