using System.Drawing;
using System.Windows.Forms;
using Toolbox.Core;

namespace Toolbox.UI;

/// <summary>新增自定义右键菜单项的输入框，沿用主界面的无边框自绘风格。</summary>
internal sealed class AddMenuDialog : Form
{
    private static int S(int value) => Theme.S(value);

    private readonly TextBox _name = new(), _command = new(), _icon = new();
    private readonly ComboBox _mount = new();

    public string Caption => _name.Text.Trim();
    public string Command => _command.Text.Trim();
    public string IconPath => _icon.Text.Trim();
    public string MountSubKey => (_mount.SelectedItem as MenuMount)?.SubKey ?? ContextMenuRegistry.Mounts[0].SubKey;
    public bool Confirmed { get; private set; }

    public AddMenuDialog()
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(S(520), S(330));
        BackColor = Theme.Window;
        Font = Theme.FontBase;
        ShowInTaskbar = false;
        MinimizeBox = MaximizeBox = false;

        _mount.DropDownStyle = ComboBoxStyle.DropDownList;
        foreach (var mount in ContextMenuRegistry.Mounts) _mount.Items.Add(mount);
        _mount.DisplayMember = "Label";
        _mount.SelectedIndex = 2; // 默认「桌面 / 空白处」

        foreach (var box in new[] { _name, _command, _icon })
        {
            box.Font = Theme.FontSmall;
            box.BorderStyle = BorderStyle.FixedSingle;
            box.BackColor = Theme.Elevated;
            box.ForeColor = Theme.Text;
            Controls.Add(box);
        }
        _mount.Font = Theme.FontSmall;
        _mount.BackColor = Theme.Elevated;
        _mount.ForeColor = Theme.Text;
        _mount.FlatStyle = FlatStyle.Flat;
        Controls.Add(_mount);

        var cancel = new ModernButton
        {
            Text = "取消", Kind = ButtonKind.Ghost, Width = S(84), Height = S(34), Font = Theme.FontSmall,
        };
        cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        var ok = new ModernButton
        {
            Text = "创建菜单项", Kind = ButtonKind.Primary, Width = S(116), Height = S(34), Font = Theme.FontSmall,
        };
        ok.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(_name.Text) || string.IsNullOrWhiteSpace(_command.Text))
            {
                MessageBox.Show("请至少填写菜单名称和执行命令。", "新增右键菜单",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            Confirmed = true;
            DialogResult = DialogResult.OK;
            Close();
        };
        Controls.Add(cancel);
        Controls.Add(ok);

        int left = S(110), width = Width - left - S(26);
        _name.SetBounds(left, S(80), width, S(30));
        _command.SetBounds(left, S(124), width, S(30));
        _mount.SetBounds(left, S(168), width, S(30));
        _icon.SetBounds(left, S(212), width, S(30));
        cancel.SetBounds(Width - S(26) - S(116) - S(8) - S(84), Height - S(58), S(84), S(34));
        ok.SetBounds(Width - S(26) - S(116), Height - S(58), S(116), S(34));
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
        Theme.Box(g, logo, S(9), Theme.AccentDim);
        GlyphPainter.Draw(g, Glyph.Plus,
            new Rectangle(logo.X + S(6), logo.Y + S(6), S(22), S(22)), Theme.Accent, 1.9f);
        Theme.DrawText(g, "新增右键菜单项", Theme.FontTitle, Theme.Text,
            new Rectangle(S(70), S(20), Width - S(96), S(26)));
        Theme.DrawText(g, "写入当前用户范围，无需管理员权限；命令中的 %1 代表选中的文件。",
            Theme.FontSmall, Theme.TextMuted, new Rectangle(S(70), S(46), Width - S(96), S(30)));

        string[] labels = { "菜单名称", "执行命令", "挂载位置", "图标（可选）" };
        for (int i = 0; i < labels.Length; i++)
            Theme.DrawText(g, labels[i], Theme.FontSmall, Theme.TextMuted,
                new Rectangle(S(24), S(80) + i * S(44), S(82), S(30)));

        g.DrawLine(Theme.P(Theme.Border), S(24), Height - S(70), Width - S(24), Height - S(70));
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }
}
