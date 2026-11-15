using System.Drawing;
using System.Windows.Forms;

namespace Toolbox.UI;

/// <summary>统一的确认对话框：条目清单 + 可选开关 + 危险操作着色。</summary>
internal sealed class ActionDialog : Form
{
    private static int S(int value) => Theme.S(value);

    private readonly string _title, _subtitle, _confirmText;
    private readonly IReadOnlyList<string> _items;
    private readonly IReadOnlyList<string> _options;
    private readonly bool _danger;
    private readonly List<ModernToggle> _toggles = new();

    public bool Confirmed { get; private set; }
    public bool GetOption(int index) => index >= 0 && index < _toggles.Count && _toggles[index].Checked;

    public ActionDialog(string title, string subtitle, IReadOnlyList<string> items,
        string confirmText, IReadOnlyList<string> options = null, bool danger = true)
    {
        _title = title;
        _subtitle = subtitle;
        _items = items ?? Array.Empty<string>();
        _confirmText = confirmText;
        _options = options ?? Array.Empty<string>();
        _danger = danger;

        int shown = Math.Min(_items.Count, 5);
        Width = S(480);
        Height = S(178) + shown * S(34) + _options.Count * S(28) + (string.IsNullOrEmpty(_subtitle) ? 0 : S(10));
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Theme.Window;
        ShowInTaskbar = false;
        Font = Theme.FontBase;
        MinimizeBox = MaximizeBox = false;

        int bottom = Height - S(62);
        int optionTop = bottom - _options.Count * S(28) - S(6);
        for (int i = 0; i < _options.Count; i++)
        {
            var toggle = new ModernToggle
            {
                Label = _options[i],
                Left = S(26),
                Top = optionTop + i * S(28),
                Width = Width - S(52),
                Height = S(24),
                BackColor = Color.Transparent,
            };
            _toggles.Add(toggle);
            Controls.Add(toggle);
        }

        var cancel = new ModernButton
        {
            Text = "取消", Kind = ButtonKind.Ghost,
            Left = Width - S(26) - S(84) - S(8) - S(116), Top = bottom, Width = S(84), Height = S(34),
        };
        cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };

        var ok = new ModernButton
        {
            Text = _confirmText, Kind = danger ? ButtonKind.Danger : ButtonKind.Primary,
            Left = Width - S(26) - S(116), Top = bottom, Width = S(116), Height = S(34),
        };
        ok.Click += (_, _) => { Confirmed = true; DialogResult = DialogResult.OK; Close(); };

        Controls.Add(cancel);
        Controls.Add(ok);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        NativeMethods.ApplyRoundRegion(this, S(14));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Theme.SetQuality(g);
        g.Clear(Theme.Window);
        Theme.Box(g, new Rectangle(0, 0, Width - 1, Height - 1), S(13), Theme.Window, Theme.BorderStrong);

        var logo = new Rectangle(S(24), S(22), S(34), S(34));
        Theme.Box(g, logo, S(9), _danger ? Theme.DangerDim : Theme.AccentDim);
        GlyphPainter.Draw(g, _danger ? Glyph.Trash : Glyph.Bolt,
            new Rectangle(logo.X + S(6), logo.Y + S(6), S(22), S(22)), _danger ? Theme.Danger : Theme.Accent, 1.8f);

        Theme.DrawText(g, _title, Theme.FontTitle, Theme.Text, new Rectangle(S(70), S(20), Width - S(96), S(26)));
        Theme.DrawText(g, _subtitle, Theme.FontSmall, Theme.TextMuted,
            new Rectangle(S(70), S(46), Width - S(96), S(38)),
            TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak);

        int y = S(92);
        for (int i = 0; i < Math.Min(_items.Count, 5); i++)
        {
            var rect = new Rectangle(S(24), y, Width - S(48), S(30));
            Theme.Box(g, rect, S(7), Theme.SurfaceAlt, Theme.Border);
            Theme.DrawText(g, "•", Theme.FontSmallBold, _danger ? Theme.Danger : Theme.Accent,
                new Rectangle(rect.X + S(8), rect.Y, S(14), rect.Height),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            Theme.DrawText(g, _items[i], Theme.FontSmall, Theme.Text,
                new Rectangle(rect.X + S(24), rect.Y, rect.Width - S(34), rect.Height));
            y += S(34);
        }
        if (_items.Count > 5)
            Theme.DrawText(g, $"… 以及另外 {_items.Count - 5} 项", Theme.FontSmall, Theme.TextDim,
                new Rectangle(S(28), y - S(2), Width - S(56), S(22)));

        g.DrawLine(Theme.P(Theme.Border), S(24), Height - S(70), Width - S(24), Height - S(70));
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }
}

/// <summary>深色菜单渲染器，让右键菜单与主界面保持同一套视觉语言。</summary>
internal sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
{
    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        e.Graphics.Clear(Theme.Elevated);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        e.Graphics.DrawRectangle(Theme.P(Theme.BorderStrong), 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
    }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        if (e.Item.Selected && e.Item.Enabled)
            Theme.Box(e.Graphics, new Rectangle(3, 1, e.Item.Width - 6, e.Item.Height - 2), 5, Theme.AccentWash);
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? Theme.Text : Theme.TextDim;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        e.Graphics.DrawLine(Theme.P(Theme.Border), 14, e.Item.Height / 2, e.Item.Width - 14, e.Item.Height / 2);
    }
}
