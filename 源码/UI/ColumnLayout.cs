using System.Drawing;
using System.Windows.Forms;

namespace Toolbox.UI;

/// <summary>一列的运行时布局结果。</summary>
internal sealed class TableColumn
{
    public string Key, Label;
    public int Min, Weight, X, W;
    public bool Show = true;
    public TextFormatFlags Align;
}

internal sealed class ColumnDefinition
{
    public string Key, Label;
    public int Min, Weight;
    public TextFormatFlags Align;

    public ColumnDefinition(string key, string label, int min, int weight, TextFormatFlags align)
    {
        Key = key; Label = label; Min = min; Weight = weight; Align = align;
    }
}

/// <summary>一次 Measure 的列集合，供绘制与列标题共用。</summary>
internal sealed class ColumnSet
{
    private readonly Dictionary<string, TableColumn> _map;

    public IReadOnlyList<TableColumn> All { get; }
    public int Right { get; }
    public IEnumerable<TableColumn> Visible => All.Where(c => c.Show);
    public TableColumn this[string key] => _map[key];

    public ColumnSet(IReadOnlyList<TableColumn> all, int right)
    {
        All = all;
        Right = right;
        _map = all.ToDictionary(c => c.Key, StringComparer.OrdinalIgnoreCase);
    }
}

/// <summary>
/// 所有列按权重共享可用宽度：先保证 Min，剩余空间按 Weight 分配，最后一列吃取整误差，
/// 因此任何窗口宽度下都能恰好铺满，不会出现右侧留白或某列独占剩余空间。
/// 空间不足时按 hideOrder 依次隐藏可选列。
/// </summary>
internal static class ColumnLayout
{
    public static ColumnSet Build(int left, int right, int gap,
        IReadOnlyList<ColumnDefinition> definitions, params string[] hideOrder)
    {
        var columns = definitions.Select(d => new TableColumn
        {
            Key = d.Key, Label = d.Label, Min = d.Min, Weight = d.Weight, Align = d.Align,
        }).ToList();

        foreach (string key in hideOrder)
        {
            if (Fits(columns, left, right, gap)) break;
            var column = columns.FirstOrDefault(c => c.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
            if (column is null) continue;
            column.Show = false;
        }

        var visible = columns.Where(c => c.Show).ToList();
        int total = Math.Max(0, right - left - gap * Math.Max(0, visible.Count - 1));
        int minimum = visible.Sum(c => c.Min);

        if (total < minimum)
        {
            double scale = minimum == 0 ? 0 : total / (double)minimum;
            foreach (var column in visible)
                column.W = Math.Max(1, (int)(column.Min * scale));
        }
        else
        {
            int slack = total - minimum;
            int weightSum = Math.Max(1, visible.Sum(c => c.Weight));
            int used = 0;
            for (int i = 0; i < visible.Count; i++)
            {
                int add = i == visible.Count - 1
                    ? slack - used
                    : (int)(slack * visible[i].Weight / (double)weightSum);
                add = Math.Max(0, add);
                visible[i].W = visible[i].Min + add;
                used += add;
            }
        }

        int x = left;
        foreach (var column in visible)
        {
            column.X = x;
            x += column.W + gap;
        }
        return new ColumnSet(columns, right);
    }

    private static bool Fits(List<TableColumn> columns, int left, int right, int gap)
    {
        var visible = columns.Where(c => c.Show).ToList();
        int needed = visible.Sum(c => c.Min) + gap * Math.Max(0, visible.Count - 1);
        return needed <= right - left;
    }
}
