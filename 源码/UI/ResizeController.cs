using System.Drawing;
using System.Windows.Forms;

namespace Toolbox.UI;

/// <summary>
/// 无边框窗口的四边四角缩放。不引入任何系统边框：靠客户端消息自行做命中判定，
/// 鼠标进入边/角即切换双向箭头，按下后由本窗口捕获鼠标并计算新矩形。
/// </summary>
internal sealed class ResizeController : IMessageFilter
{
    private const int WmMouseMove = 0x0200;
    private const int WmLButtonDown = 0x0201;
    private const int WmLButtonUp = 0x0202;
    private const int WmCaptureChanged = 0x0215;

    private const int EdgeLeft = 10, EdgeRight = 11, EdgeTop = 12,
                      EdgeTopLeft = 13, EdgeTopRight = 14,
                      EdgeBottom = 15, EdgeBottomLeft = 16, EdgeBottomRight = 17;

    private readonly Form _form;
    private readonly Action _onResizeFinished;
    private readonly int _band;
    private bool _resizing;
    private int _edge;
    private Point _startCursor;
    private Rectangle _startBounds;

    public bool IsResizing => _resizing;

    public ResizeController(Form form, Action onResizeFinished)
    {
        _form = form;
        _onResizeFinished = onResizeFinished;
        _band = Theme.S(9);
    }

    public bool PreFilterMessage(ref Message m)
    {
        if (_form.IsDisposed || !_form.IsHandleCreated) return false;

        bool ours = NativeMethods.IsOwnWindow(_form.Handle, m.HWnd);
        switch (m.Msg)
        {
            case WmMouseMove:
                if (_resizing) ApplyResize();
                else UpdateCursor(ours ? HitEdge(Cursor.Position) : 0);
                return false;

            case WmLButtonDown when ours && CanResize:
                int hit = HitEdge(Cursor.Position);
                if (hit == 0) return false;
                BeginResize(hit);
                return true; // 吞掉按下消息，避免子控件把它当成普通点击

            case WmLButtonUp when _resizing:
                EndResize();
                return true;

            case WmCaptureChanged when _resizing:
                EndResize(releaseCapture: false);
                return false;
        }
        return false;
    }

    private bool CanResize => _form.WindowState == FormWindowState.Normal && !_resizing;

    /// <summary>把屏幕坐标换算成四边四角的命中码，0 表示窗口内部。</summary>
    private int HitEdge(Point screen)
    {
        Rectangle b = _form.Bounds;
        if (screen.X < b.Left || screen.X > b.Right || screen.Y < b.Top || screen.Y > b.Bottom) return 0;

        bool left = screen.X - b.Left <= _band;
        bool right = b.Right - screen.X <= _band;
        bool top = screen.Y - b.Top <= _band;
        bool bottom = b.Bottom - screen.Y <= _band;
        return (left, right, top, bottom) switch
        {
            (true, _, true, _) => EdgeTopLeft,
            (_, true, true, _) => EdgeTopRight,
            (true, _, _, true) => EdgeBottomLeft,
            (_, true, _, true) => EdgeBottomRight,
            (true, _, _, _) => EdgeLeft,
            (_, true, _, _) => EdgeRight,
            (_, _, true, _) => EdgeTop,
            (_, _, _, true) => EdgeBottom,
            _ => 0,
        };
    }

    private static Cursor CursorFor(int hit) => hit switch
    {
        EdgeLeft or EdgeRight => Cursors.SizeWE,
        EdgeTop or EdgeBottom => Cursors.SizeNS,
        EdgeTopLeft or EdgeBottomRight => Cursors.SizeNWSE,
        EdgeTopRight or EdgeBottomLeft => Cursors.SizeNESW,
        _ => Cursors.Default,
    };

    /// <summary>Cursor 是 WinForms 的环境属性，设置窗体后子控件会自动继承相同的缩放光标。</summary>
    private void UpdateCursor(int hit)
    {
        Cursor want = _form.WindowState == FormWindowState.Normal ? CursorFor(hit) : Cursors.Default;
        if (!ReferenceEquals(_form.Cursor, want)) _form.Cursor = want;
    }

    private void BeginResize(int hit)
    {
        _edge = hit;
        _startCursor = Cursor.Position;
        _startBounds = _form.Bounds;
        // 拖拽期间先撤掉圆角区域：避免每帧 SetWindowRgn 造成失效区域错乱
        _form.Region = null;
        NativeMethods.BeginCapture(_form.Handle);
        _resizing = true;
        UpdateCursor(hit);
        NativeMethods.ForceFullRedraw(_form);
    }

    private void EndResize(bool releaseCapture = true)
    {
        _resizing = false;
        if (releaseCapture) NativeMethods.EndCapture();
        UpdateCursor(HitEdge(Cursor.Position));
        _onResizeFinished();
    }

    /// <summary>按命中的边或角计算新矩形，最小尺寸始终受 MinimumSize 约束。</summary>
    private void ApplyResize()
    {
        Point now = Cursor.Position;
        int dx = now.X - _startCursor.X;
        int dy = now.Y - _startCursor.Y;
        int minW = _form.MinimumSize.Width, minH = _form.MinimumSize.Height;

        int left = _startBounds.Left, top = _startBounds.Top;
        int right = _startBounds.Right, bottom = _startBounds.Bottom;
        bool toLeft = _edge is EdgeLeft or EdgeTopLeft or EdgeBottomLeft;
        bool toRight = _edge is EdgeRight or EdgeTopRight or EdgeBottomRight;
        bool toTop = _edge is EdgeTop or EdgeTopLeft or EdgeTopRight;
        bool toBottom = _edge is EdgeBottom or EdgeBottomLeft or EdgeBottomRight;

        if (toLeft) left = Math.Min(_startBounds.Left + dx, right - minW);
        if (toRight) right = Math.Max(_startBounds.Right + dx, left + minW);
        if (toTop) top = Math.Min(_startBounds.Top + dy, bottom - minH);
        if (toBottom) bottom = Math.Max(_startBounds.Bottom + dy, top + minH);

        _form.SetBounds(left, top, right - left, bottom - top, BoundsSpecified.All);
    }
}
