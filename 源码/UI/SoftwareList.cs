using System.Drawing;
using Toolbox.Core;

namespace Toolbox.UI;

/// <summary>虚拟绘制的软件列表：只渲染可见行，按整行滚动，支持多选、悬停、快捷键与自绘滚动条。
/// 所有尺寸经 <see cref="Theme.S"/> 按 DPI 缩放，避免高 DPI 下文字被裁切。</summary>
internal sealed class SoftwareList : Control, IThemed
{
    private static int S(int value) => Theme.S(value);

    public static int RowHeight => S(46);
    private static int HeaderHeight => S(34);
    private static int ScrollTrackWidth => S(12);
    private static int TopPad => S(6);
    private static int BottomPad => S(10);
    private static int Gap => S(10);
    private static int RowsTop => HeaderHeight + TopPad;

    private List<SoftwareEntry> _items = new();
    private readonly HashSet<int> _selected = new();
    private readonly Dictionary<string, Icon> _icons = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _kindCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _pendingIcons = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _iconFailed = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _iconGate = new();

    private int _scrollRow, _hover = -1, _anchor = -1, _focus = -1;
    private bool _draggingScroll;
    private int _dragStartY, _dragStartRow;

    public event EventHandler SelectionChanged;
    public event Action<SoftwareEntry> ItemDoubleClick;
    public event Action<SoftwareEntry, Point> ItemRightClick;
    public event Action<SoftwareEntry> ItemEnter;

    public IReadOnlyList<SoftwareEntry> Items => _items;
    public int SelectedCount => _selected.Count;
    public IReadOnlyList<SoftwareEntry> SelectedItems =>
        _selected.Where(i => i >= 0 && i < _items.Count).OrderBy(i => i).Select(i => _items[i]).ToList();
    public SoftwareEntry FocusedItem => _focus >= 0 && _focus < _items.Count ? _items[_focus] : null;

    public SoftwareList()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        BackColor = Theme.Surface;
        Font = Theme.FontBase;
        DoubleBuffered = true;
        TabStop = true;
    }

    public void ApplyTheme()
    {
        BackColor = Theme.Surface;
        Invalidate();
    }

    public void SetItems(IEnumerable<SoftwareEntry> items)
    {
        _items = items?.ToList() ?? new List<SoftwareEntry>();
        _selected.Clear();
        _hover = _anchor = _focus = -1;
        _scrollRow = 0;
        Invalidate();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SelectAll()
    {
        for (int i = 0; i < _items.Count; i++) _selected.Add(i);
        Invalidate();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ClearSelection()
    {
        _selected.Clear();
        Invalidate();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SelectByEntry(SoftwareEntry entry)
    {
        int index = _items.FindIndex(e => ReferenceEquals(e, entry) || e.SubKeyPath == entry.SubKeyPath);
        if (index < 0) return;
        _selected.Clear();
        _selected.Add(index);
        _focus = _anchor = index;
        EnsureVisible(index);
        Invalidate();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>可完整显示的行数，保证任何滚动位置都不会出现半行文字。</summary>
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

    private void EnsureVisible(int index)
    {
        if (index < _scrollRow) ScrollTo(index);
        else if (index >= _scrollRow + VisibleRows) ScrollTo(index - VisibleRows + 1);
    }

    private int IndexAt(Point point)
    {
        if (point.X < 0 || point.X > ClientSize.Width - ScrollTrackWidth) return -1;
        int y = point.Y - RowsTop; // 列标题栏不参与行命中
        if (y < 0) return -1;
        int index = _scrollRow + y / RowHeight;
        return index >= 0 && index < _items.Count ? index : -1;
    }

    private static Rectangle CheckRect(int top) =>
        new(S(16), top + (RowHeight - S(16)) / 2, S(16), S(16));

    private sealed class Columns
    {
        private readonly ColumnSet _set;
        public Columns(ColumnSet set) => _set = set;
        public TableColumn Name => _set["name"];
        public TableColumn Ver => _set["ver"];
        public TableColumn Pub => _set["pub"];
        public TableColumn Size => _set["size"];
        public TableColumn Date => _set["date"];
        public TableColumn Source => _set["source"];
        public TableColumn Kind => _set["kind"];
        public int Right => _set.Right;
        public IEnumerable<TableColumn> Visible => _set.Visible;
    }

    private const TextFormatFlags HeadLeft =
        TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
        TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis;
    private const TextFormatFlags HeadRight =
        TextFormatFlags.Right | TextFormatFlags.VerticalCenter |
        TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis;
    private const TextFormatFlags HeadCenter =
        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding;

    /// <summary>列宽统一交给 ColumnLayout：按权重铺满可用宽度，窄窗口依次隐藏可选列。</summary>
    private Columns Measure()
    {
        int width = ClientSize.Width - ScrollTrackWidth;
        var definitions = new[]
        {
            new ColumnDefinition("name", "软件名称", S(180), 26, HeadLeft),
            new ColumnDefinition("ver", "版本", S(105), 15, HeadLeft),
            new ColumnDefinition("pub", "发布者", S(140), 24, HeadLeft),
            new ColumnDefinition("size", "大小", S(86), 10, HeadRight),
            new ColumnDefinition("date", "安装日期", S(86), 13, HeadCenter),
            new ColumnDefinition("source", "来源", S(92), 12, HeadCenter),
            new ColumnDefinition("kind", "类型", S(52), 8, HeadCenter),
        };
        return new Columns(ColumnLayout.Build(S(76), width - S(12), Gap, definitions,
            "date", "source", "pub", "size"));
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
            DrawEmptyState(g);
            return;
        }

        int last = Math.Min(_items.Count - 1, _scrollRow + VisibleRows - 1);

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

            int iconSize = S(34);
            DrawIcon(g, entry, new Rectangle(S(42), top + (RowHeight - iconSize) / 2, iconSize, iconSize));

            Theme.DrawText(g, entry.Name, Theme.FontBold, Theme.Text,
                new Rectangle(c.Name.X, top, c.Name.W, RowHeight));
            Theme.DrawText(g, VersionText(entry), Theme.FontBase, Theme.TextMuted,
                new Rectangle(c.Ver.X, top, c.Ver.W, RowHeight));

            if (c.Pub.Show)
                Theme.DrawText(g, entry.Publisher, Theme.FontBase, Theme.TextMuted,
                    new Rectangle(c.Pub.X, top, c.Pub.W, RowHeight));
            if (c.Size.Show)
                Theme.DrawText(g, entry.SizeText, Theme.FontBase, Theme.TextMuted,
                    new Rectangle(c.Size.X, top, c.Size.W, RowHeight), HeadRight);
            if (c.Date.Show)
                Theme.DrawText(g, entry.InstallDate, Theme.FontSmall, Theme.TextDim,
                    new Rectangle(c.Date.X, top, c.Date.W, RowHeight), HeadCenter);

            int badgeH = S(20);
            if (c.Source.Show)
            {
                bool user = entry.Source.StartsWith("当前用户");
                Theme.Badge(g, entry.Source,
                    new Rectangle(c.Source.X, top + (RowHeight - badgeH) / 2, c.Source.W, badgeH),
                    user ? Theme.Alpha(Theme.Warning, 30) : Theme.Elevated,
                    user ? Theme.Warning : Theme.TextMuted);
            }

            Theme.Badge(g, KindOf(entry),
                new Rectangle(c.Kind.X, top + (RowHeight - badgeH) / 2, c.Kind.W, badgeH),
                Theme.Elevated, Theme.TextDim);
        }

        DrawScrollBar(g);
    }

    /// <summary>列表顶部的列标题栏，与数据列共用同一套 Measure 结果。</summary>
    private void DrawHeader(Graphics g, Columns c)
    {
        g.FillRectangle(Theme.B(Theme.SurfaceAlt), new Rectangle(0, 0, ClientSize.Width, HeaderHeight));
        g.DrawLine(Theme.P(Theme.Border), 0, HeaderHeight - 1, ClientSize.Width, HeaderHeight - 1);
        foreach (var col in c.Visible)
            Theme.DrawText(g, col.Label, Theme.FontSmallBold, Theme.TextMuted,
                new Rectangle(col.X, 0, col.W, HeaderHeight), col.Align);
    }

    private static string VersionText(SoftwareEntry entry) =>
        string.IsNullOrWhiteSpace(entry.Version) || entry.Version == "—" ? "—" : "v" + entry.Version;

    private string KindOf(SoftwareEntry entry)
    {        if (!_kindCache.TryGetValue(entry.SubKeyPath, out string text))
            _kindCache[entry.SubKeyPath] = text = UninstallEngine.BuildPlan(entry, UninstallMode.Interactive).KindText;
        return text;
    }

    private void DrawIcon(Graphics g, SoftwareEntry entry, Rectangle rect)
    {
        string key = entry.SubKeyPath;
        Icon icon;
        bool failed;
        lock (_iconGate)
        {
            _icons.TryGetValue(key, out icon);
            failed = _iconFailed.Contains(key);
        }

        if (icon is null)
        {
            if (!failed) QueueIcon(entry);
            Theme.Box(g, rect, S(8), IconLoader.HueColor(entry.Hue));
            string letter = string.IsNullOrEmpty(entry.Name) ? "?" : entry.Name.Substring(0, 1).ToUpperInvariant();
            Theme.DrawText(g, letter, Theme.FontTitle, Theme.C("#0D1219"), rect,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
        else
        {
            try { g.DrawIcon(icon, rect); }
            catch { Theme.Box(g, rect, S(8), Theme.Elevated); }
        }
    }

    private void QueueIcon(SoftwareEntry entry)
    {
        string key = entry.SubKeyPath;
        lock (_iconGate)
        {
            if (_icons.ContainsKey(key) || _iconFailed.Contains(key) || !_pendingIcons.Add(key)) return;
        }
        string displayIcon = entry.DisplayIcon;
        string uninstallExe = CommandLine.Parse(entry.UninstallString).FileName;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            Icon icon = IconLoader.Get(key, displayIcon, uninstallExe);
            try
            {
                if (!IsHandleCreated) return;
                BeginInvoke(new Action(() =>
                {
                    lock (_iconGate)
                    {
                        _pendingIcons.Remove(key);
                        if (icon is not null) _icons[key] = icon;
                        else _iconFailed.Add(key); // 失败也记下来，否则每次重绘都会重新排队
                    }
                    // 只有拿到图标才需要重绘；失败时重绘会形成 paint→queue→paint 死循环
                    if (icon is not null) Invalidate();
                }));
            }
            catch { lock (_iconGate) _pendingIcons.Remove(key); }
        });
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

    private void DrawEmptyState(Graphics g)
    {
        int cx = ClientSize.Width / 2;
        int cy = HeaderHeight + (ClientSize.Height - HeaderHeight) / 2 - S(20);
        int radius = S(38);
        Theme.Box(g, new Rectangle(cx - radius, cy - radius, radius * 2, radius * 2), radius, Theme.Elevated, Theme.Border);
        GlyphPainter.Draw(g, Glyph.Search, new Rectangle(cx - S(16), cy - S(16), S(32), S(32)), Theme.TextDim, 2f);
        Theme.DrawText(g, "没有匹配的软件", Theme.FontTitle, Theme.TextMuted,
            new Rectangle(cx - S(150), cy + S(52), S(300), S(26)),
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        Theme.DrawText(g, "换个关键词，或调整上方的筛选条件", Theme.FontSmall, Theme.TextDim,
            new Rectangle(cx - S(150), cy + S(78), S(300), S(22)),
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
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
            int delta = e.Y - _dragStartY;
            ScrollTo(_dragStartRow + (int)Math.Round(delta / (double)travel * MaxScrollRow));
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
        int top = RowTop(index);

        if (e.Button == MouseButtons.Right)
        {
            if (!_selected.Contains(index))
            {
                _selected.Clear();
                _selected.Add(index);
                _anchor = _focus = index;
                SelectionChanged?.Invoke(this, EventArgs.Empty);
            }
            ItemRightClick?.Invoke(_items[index], e.Location);
            Invalidate();
            return;
        }

        if (CheckRect(top).Contains(e.Location))
        {
            if (!_selected.Remove(index)) _selected.Add(index);
            _anchor = _focus = index;
        }
        else if (ModifierKeys.HasFlag(Keys.Control))
        {
            if (!_selected.Remove(index)) _selected.Add(index);
            _anchor = _focus = index;
        }
        else if (ModifierKeys.HasFlag(Keys.Shift) && _anchor >= 0)
        {
            _selected.Clear();
            for (int i = Math.Min(_anchor, index); i <= Math.Max(_anchor, index); i++) _selected.Add(i);
            _focus = index;
        }
        else
        {
            _selected.Clear();
            _selected.Add(index);
            _anchor = _focus = index;
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
            case Keys.Control | Keys.A: SelectAll(); return true;
            case Keys.Up: MoveFocus(-1); return true;
            case Keys.Down: MoveFocus(1); return true;
            case Keys.Home: SetFocusIndex(0); return true;
            case Keys.End: SetFocusIndex(_items.Count - 1); return true;
            case Keys.Space when _focus >= 0:
                if (!_selected.Remove(_focus)) _selected.Add(_focus);
                Invalidate(); SelectionChanged?.Invoke(this, EventArgs.Empty); return true;
            case Keys.Enter when FocusedItem is not null:
                ItemEnter?.Invoke(FocusedItem); return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private void MoveFocus(int delta) =>
        SetFocusIndex(Math.Clamp(_focus < 0 ? 0 : _focus + delta, 0, Math.Max(0, _items.Count - 1)));

    private void SetFocusIndex(int index)
    {
        if (_items.Count == 0) return;
        _focus = Math.Clamp(index, 0, _items.Count - 1);
        EnsureVisible(_focus);
        Invalidate();
    }
}
