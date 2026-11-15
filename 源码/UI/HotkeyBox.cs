using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Toolbox.UI;

/// <summary>
/// 快捷键捕获框：获得焦点后直接按下组合键即可识别，不再让用户手输文本。
/// 必须带至少一个修饰键，避免把单个字母注册成全局热键抢掉系统按键。
/// </summary>
internal sealed class HotkeyBox : Control, IThemed
{
    private static int S(int value) => Theme.S(value);

    private const int VkShift = 0x10, VkControl = 0x11, VkMenu = 0x12, VkLWin = 0x5B, VkRWin = 0x5C;
    private const uint ModAlt = 0x0001, ModControl = 0x0002, ModShift = 0x0004, ModWin = 0x0008;

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int vk);

    private readonly System.Windows.Forms.Timer _errorTimer = new() { Interval = 1800 };
    private string _value = "";
    private string _error = "";
    private bool _capturing;
    private bool _hover;

    /// <summary>捕获到合法组合时触发；参数为规范化之后的文本（如 Ctrl + Alt + A），清空时为空串。</summary>
    public event EventHandler<string> Captured;

    public HotkeyBox()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                 ControlStyles.Selectable, true);
        Height = S(30);
        Cursor = Cursors.Hand;
        TabStop = true;
        Font = Theme.FontBase;
        _errorTimer.Tick += (_, _) => { _errorTimer.Stop(); _error = ""; Invalidate(); };
        Disposed += (_, _) => _errorTimer.Dispose();
    }

    public string Value
    {
        get => _value;
        set { _value = value ?? ""; _error = ""; Invalidate(); }
    }

    public bool Capturing => _capturing;

    public void ApplyTheme() => Invalidate();

    /// <summary>把 Alt 组合键也当普通输入，否则会被当成菜单访问键吞掉。</summary>
    protected override bool IsInputKey(Keys keyData) => true;

    protected override bool IsInputChar(char charCode) => false;

    protected override void OnEnter(EventArgs e)
    {
        _capturing = true;
        _error = "";
        Invalidate();
        base.OnEnter(e);
    }

    protected override void OnLeave(EventArgs e)
    {
        _capturing = false;
        Invalidate();
        base.OnLeave(e);
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            // 截屏覆盖层关掉后主窗口未必是前台，先激活再聚焦，保证一次点击就能改
            if (!Focused) Focus();
            if (!Focused && FindForm() is { } form)
            {
                form.Activate();
                Focus();
            }
            // 捕获态直接由点击驱动：捕获完成后会清空 ActiveControl，
            // 只靠 OnEnter 的话第二次点击就进不来。
            _capturing = true;
            _error = "";
            Invalidate();
        }
        base.OnMouseDown(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (!_capturing) { base.OnKeyDown(e); return; }
        e.Handled = true;
        e.SuppressKeyPress = true;

        switch (e.KeyCode)
        {
            case Keys.Escape:
                Release();
                return;
            case Keys.Back:
            case Keys.Delete:
                _value = "";
                Captured?.Invoke(this, "");
                Release();
                return;
            case Keys.ControlKey:
            case Keys.ShiftKey:
            case Keys.Menu:
            case Keys.LWin:
            case Keys.RWin:
                return;   // 只按了修饰键，继续等主键
        }

        uint modifiers = CurrentModifiers();
        if (modifiers == 0)
        {
            Fail("需要至少一个修饰键（Ctrl / Alt / Shift / Win）");
            return;
        }

        string text = Describe(modifiers, e.KeyCode);
        if (!GlobalHotkey.TryParse(text, out _, out _, out string normalized))
        {
            Fail("这个按键不支持，换一个试试");
            return;
        }

        _value = normalized;
        _error = "";
        Invalidate();
        Captured?.Invoke(this, normalized);
        Release();
    }

    private void Release()
    {
        _capturing = false;
        Invalidate();
        if (FindForm() is { } form) form.ActiveControl = null;
    }

    private void Fail(string reason)
    {
        _error = reason;
        Invalidate();
        _errorTimer.Stop();
        _errorTimer.Start();
    }

    private static uint CurrentModifiers()
    {
        uint modifiers = 0;
        if (IsDown(VkControl)) modifiers |= ModControl;
        if (IsDown(VkMenu)) modifiers |= ModAlt;
        if (IsDown(VkShift)) modifiers |= ModShift;
        if (IsDown(VkLWin) || IsDown(VkRWin)) modifiers |= ModWin;
        return modifiers;
    }

    private static bool IsDown(int vk) => (GetKeyState(vk) & 0x8000) != 0;

    private static string Describe(uint modifiers, Keys key)
    {
        var names = new List<string>(4);
        if ((modifiers & ModControl) != 0) names.Add("Ctrl");
        if ((modifiers & ModAlt) != 0) names.Add("Alt");
        if ((modifiers & ModShift) != 0) names.Add("Shift");
        if ((modifiers & ModWin) != 0) names.Add("Win");
        names.Add(KeyName(key));
        return string.Join(" + ", names);
    }

    /// <summary>数字键显示成 1..9 而不是 D1..D9；其余直接用 Keys 名称，保证能反解析。</summary>
    private static string KeyName(Keys key)
    {
        if (key >= Keys.D0 && key <= Keys.D9) return ((int)(key - Keys.D0)).ToString();
        return key.ToString();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Theme.SetQuality(e.Graphics);
        var g = e.Graphics;
        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        Color fill = _capturing ? Theme.Elevated : _hover ? Theme.ElevatedHover : Theme.Elevated;
        Theme.Box(g, rect, S(8), fill, _capturing ? Theme.Accent : Theme.Border);

        string text;
        Color fg;
        if (_error.Length > 0)
        {
            text = _error;
            fg = Theme.Danger;
        }
        else if (_capturing)
        {
            text = "请按下组合键…（Esc 取消）";
            fg = Theme.Accent;
        }
        else if (_value.Length == 0)
        {
            text = "点击后按下组合键";
            fg = Theme.TextDim;
        }
        else
        {
            text = _value;
            fg = Theme.Text;
        }

        Theme.DrawText(g, text, Theme.FontSmall, fg,
            new Rectangle(S(12), 0, Width - S(24), Height),
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

        // 右侧小键盘提示点：捕获中呈实心，其余为空心
        var dot = new Rectangle(Width - S(18), (Height - S(8)) / 2, S(8), S(8));
        using var brush = new SolidBrush(_capturing ? Theme.Accent : Theme.BorderStrong);
        g.FillEllipse(brush, dot);
    }
}
