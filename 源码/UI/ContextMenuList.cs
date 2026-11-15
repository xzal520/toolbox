using System.Drawing;
using System.Windows.Forms;
using Toolbox.Core;

namespace Toolbox.UI;

/// <summary>右键菜单项列表：列标题 + 整行滚动 + 多选，列宽复用 ColumnLayout。</summary>
internal sealed class ContextMenuList : Control, IThemed
{
    private static int S(int value) => Theme.S(value);
    private static int RowHeight => S(46);
    private static int HeaderHeight => S(34);
    private static int ScrollTrackWidth => S(12);
    private static int TopPad => S(6);
    private static int BottomPad => S(10);
    private static int Gap => S(10);
    private static int RowsTop => HeaderHeight + TopPad;

    private const TextFormatFlags HeadLeft =
        TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
        TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis;
    private const TextFormatFlags HeadCenter =
        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding;

    private List<ContextMenuEntry> _items = new();
    private readonly HashSet<int> _selected = new();
    private int _scrollRow, _hover = -1, _anchor = -1;
    private bool _draggingScroll;
    private int _dragStartY, _dragStartRow;

    public event EventHandler SelectionChanged;
    public event Action<ContextMenuEntry, Point> ItemRightClick;
    public event Action<ContextMenuEntry> ItemDoubleClick;

    public IReadOnlyList<ContextMenuEntry> SelectedItems =>
        _selected.Where(i => i >= 0 && i < _items.Count).OrderBy(i => i).Select(i => _items[i]).ToList();
    public int SelectedCount => _selected.Count;

    public ContextMenuList()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        BackColor = Theme.Surface;
        Font = Theme.FontBase;
        DoubleBuffered = true;
        TabStop = true;
    }

    public void ApplyTheme() { BackColor = Theme.Surface; Invalidate(); }

    public void SetItems(IEnumerable<ContextMenuEntry> items)
    {
        _items = items?.ToList() ?? new List<ContextMenuEntry>();
        _selected.Clear();
        _hover = _anchor = -1;
        _scrollRow = 0;
        Invalidate();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ClearSelection()
    {
        _selected.Clear();
        Invalidate();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private int VisibleRows => Math.Max(1, (ClientSize.Height - RowsTop - BottomPad) / RowHeight);
    private int MaxScrollRow => Math.Max(0, _items.Count - VisibleRows);
    private int RowTop(int index) => RowsTop + (index - _scrollRow) * RowHeight;

    private void ScrollTo(int row)
    {
        int next = Math.Clamp(row, 0, MaxScrollRow);
        if (next == _scrollRow) return;
        _scrollRow = next;
        Invalidate();
    }

    private int IndexAt(Point point)
    {
        if (point.X < 0 || point.X > ClientSize.Width - ScrollTrackWidth) return -1;
        int y = point.Y - RowsTop;
        if (y < 0) return -1;
        int index = _scrollRow + y / RowHeight;
        return index >= 0 && index < _items.Count ? index : -1;
    }

    private static Rectangle CheckRect(int top) =>
        new(S(16), top + (RowHeight - S(16)) / 2, S(16), S(16));

    private ColumnSet Measure()
    {
        int width = ClientSize.Width - ScrollTrackWidth;
        var definitions = new[]
        {
            new ColumnDefinition("name", "菜单名称", S(160), 22, HeadLeft),
            new ColumnDefinition("command", "执行命令", S(200), 36, HeadLeft),
            new ColumnDefinition("mount", "挂载位置", S(110), 16, HeadLeft),
            new ColumnDefinition("scope", "作用域", S(80), 10, HeadCenter),
            new ColumnDefinition("status", "状态", S(80), 10, HeadCenter),
        };
        return ColumnLayout.Build(S(76), width - S(12), Gap, definitions, "scope", "mount");
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Theme.SetQuality(g);
        g.Clear(Theme.Surface);
        var c = Measure();
        DrawHeader(g, c);

        if (_items.Count == 0)
        {
            Theme.DrawText(g, "没有匹配的右键菜单项", Theme.FontSmall, Theme.TextDim,
                new Rectangle(0, RowsTop + S(40), ClientSize.Width, S(30)), HeadCenter);
            return;
        }

        int last = Math.Min(_items.Count - 1, _scrollRow + VisibleRows - 1);
        int badgeH = S(20);
        for (int i = _scrollRow; i <= last; i++)
        {
            var entry = _items[i];
            int top = RowTop(i);
            bool selected = _selected.Contains(i);
            bool hovered = _hover == i;
            var row = new Rectangle(S(8), top + S(3), c.Right - S(8), RowHeight - S(6));

            if (selected || hovered)
            {
                Theme.Box(g, row, S(9), selected ? Theme.AccentWash : Theme.ElevatedHover,
                    selected ? Theme.Alpha(Theme.Accent, 90) : Theme.Alpha(Theme.Border, 170));
                if (selected)
                    Theme.Box(g, new Rectangle(row.X, row.Y + S(8), S(3), row.Height - S(16)), S(2), Theme.Accent);
            }

            var check = CheckRect(top);
            Theme.Box(g, check, S(4), selected ? Theme.Accent : Theme.SurfaceAlt,
                selected ? Theme.Accent : Theme.BorderStrong);
            if (selected)
                GlyphPainter.Draw(g, Glyph.Check,
                    new Rectangle(check.X + S(1), check.Y + S(1), check.Width - S(2), check.Height - S(2)),
                    Theme.OnAccent, 2f);

            int iconX = S(42);
            var icon = new Rectangle(iconX, top + (RowHeight - S(30)) / 2, S(30), S(30));
            Theme.Box(g, icon, S(7), entry.IsOwn ? Theme.AccentDim : Theme.Elevated, Theme.Border);
            GlyphPainter.Draw(g, entry.IsOwn ? Glyph.Bolt : Glyph.Menu,
                new Rectangle(icon.X + S(6), icon.Y + S(6), S(18), S(18)),
                entry.IsOwn ? Theme.Accent : Theme.TextMuted, 1.5f);

            Theme.DrawText(g, entry.Caption, Theme.FontBold, Theme.Text,
                new Rectangle(c["name"].X, top, c["name"].W, RowHeight));
            Theme.DrawText(g, entry.Command, Theme.FontSmall, Theme.TextMuted,
                new Rectangle(c["command"].X, top, c["command"].W, RowHeight));
            if (c["mount"].Show)
                Theme.DrawText(g, entry.MountLabel, Theme.FontSmall, Theme.TextMuted,
                    new Rectangle(c["mount"].X, top, c["mount"].W, RowHeight));
            if (c["scope"].Show)
                Theme.DrawText(g, entry.ScopeText, Theme.FontSmall, Theme.TextDim,
                    new Rectangle(c["scope"].X, top, c["scope"].W, RowHeight), HeadCenter);

            Theme.Badge(g, entry.StatusText,
                new Rectangle(c["status"].X, top + (RowHeight - badgeH) / 2, c["status"].W, badgeH),
                entry.Enabled ? Theme.Alpha(Theme.Success, 28) : Theme.Alpha(Theme.Warning, 34),
                entry.Enabled ? Theme.Success : Theme.Warning);
        }
        DrawScrollBar(g);
    }

    private void DrawHeader(Graphics g, ColumnSet c)
    {
        g.FillRectangle(Theme.B(Theme.SurfaceAlt), new Rectangle(0, 0, ClientSize.Width, HeaderHeight));
        g.DrawLine(Theme.P(Theme.Border), 0, HeaderHeight - 1, ClientSize.Width, HeaderHeight - 1);
        foreach (var column in c.Visible)
            Theme.DrawText(g, column.Label, Theme.FontSmallBold, Theme.TextMuted,
                new Rectangle(column.X, 0, column.W, HeaderHeight), column.Align);
    }

    private int ThumbHeight(int trackHeight) =>
        Math.Max(S(28), (int)(trackHeight * Math.Min(1f, VisibleRows / (float)Math.Max(1, _items.Count))));

    private void DrawScrollBar(Graphics g)
    {
        if (MaxScrollRow <= 0) return;
        int trackHeight = ClientSize.Height - HeaderHeight - S(12);
        int thumbHeight = ThumbHeight(trackHeight);
        int travel = Math.Max(1, trackHeight - thumbHeight);
        int thumbTop = HeaderHeight + S(6) + (int)(travel * (_scrollRow / (float)MaxScrollRow));
        Theme.Box(g, new Rectangle(ClientSize.Width - ScrollTrackWidth + S(3), thumbTop, S(5), thumbHeight), S(3),
            _draggingScroll ? Theme.Accent : Theme.BorderStrong);
    }

    protected override void OnResize(EventArgs e)
    {
        ScrollTo(_scrollRow);
        base.OnResize(e);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        ScrollTo(_scrollRow - Math.Sign(e.Delta) * 3);
        _hover = IndexAt(e.Location);
        base.OnMouseWheel(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_draggingScroll)
        {
            int trackHeight = ClientSize.Height - HeaderHeight - S(12);
            int travel = Math.Max(1, trackHeight - ThumbHeight(trackHeight));
            ScrollTo(_dragStartRow + (int)Math.Round((e.Y - _dragStartY) / (double)travel * MaxScrollRow));
            return;
        }
        int index = IndexAt(e.Location);
        if (index != _hover) { _hover = index; Invalidate(); }
        base.OnMouseMove(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        Focus();
        if (e.X >= ClientSize.Width - ScrollTrackWidth && e.Y >= HeaderHeight && MaxScrollRow > 0)
        {
            _draggingScroll = true;
            _dragStartY = e.Y;
            _dragStartRow = _scrollRow;
            Invalidate();
            return;
        }

        int index = IndexAt(e.Location);
        if (index < 0) return;
        if (e.Button == MouseButtons.Right)
        {
            if (!_selected.Contains(index)) { _selected.Clear(); _selected.Add(index); _anchor = index; }
            ItemRightClick?.Invoke(_items[index], e.Location);
            Invalidate();
            SelectionChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (CheckRect(RowTop(index)).Contains(e.Location) && e.Button == MouseButtons.Left)
        {
            if (!_selected.Remove(index)) _selected.Add(index);
            _anchor = index;
        }
        else if (ModifierKeys.HasFlag(Keys.Control))
        {
            if (!_selected.Remove(index)) _selected.Add(index);
            _anchor = index;
        }
        else if (ModifierKeys.HasFlag(Keys.Shift) && _anchor >= 0)
        {
            _selected.Clear();
            for (int i = Math.Min(_anchor, index); i <= Math.Max(_anchor, index); i++) _selected.Add(i);
        }
        else
        {
            _selected.Clear();
            _selected.Add(index);
            _anchor = index;
        }
        Invalidate();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        base.OnMouseDown(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        _draggingScroll = false;
        Invalidate();
        base.OnMouseUp(e);
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        int index = IndexAt(e.Location);
        if (index >= 0 && e.Button == MouseButtons.Left) ItemDoubleClick?.Invoke(_items[index]);
        base.OnMouseDoubleClick(e);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        switch (keyData)
        {
            case Keys.Control | Keys.A:
                for (int i = 0; i < _items.Count; i++) _selected.Add(i);
                Invalidate(); SelectionChanged?.Invoke(this, EventArgs.Empty); return true;
            case Keys.Up: ScrollTo(_scrollRow - 1); return true;
            case Keys.Down: ScrollTo(_scrollRow + 1); return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }
}
