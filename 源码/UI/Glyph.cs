using System.Drawing;
using System.Drawing.Drawing2D;

namespace Toolbox.UI;

internal enum Glyph
{
    None, Bolt, Search, Refresh, Close, Minimize, Maximize, Trash, Folder, Copy,
    Chevron, Check, Registry, Filter, ArrowUp, ArrowDown,
    Toolbox, Menu, Camera, Plus, Shield, Undo, Image, Power, Collapse, Expand, Gear
}

/// <summary>不依赖图标字体的矢量小图形，保证任何语言系统下都能正确显示。</summary>
internal static class GlyphPainter
{
    public static void Draw(Graphics g, Glyph glyph, Rectangle box, Color color, float width = 1.6f)
    {
        if (glyph == Glyph.None || box.Width < 8) return;
        Theme.SetQuality(g);
        using var pen = new Pen(color, width) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        float cx = box.X + box.Width / 2f, cy = box.Y + box.Height / 2f;
        float r = Math.Min(box.Width, box.Height) / 2f - 2f;

        switch (glyph)
        {
            case Glyph.Search:
                g.DrawEllipse(pen, cx - r * 0.55f, cy - r * 0.55f, r * 1.1f, r * 1.1f);
                g.DrawLine(pen, cx + r * 0.4f, cy + r * 0.4f, cx + r, cy + r);
                break;
            case Glyph.Refresh:
                g.DrawArc(pen, cx - r, cy - r, r * 2, r * 2, 40, 280);
                g.DrawLine(pen, cx + r * 0.55f, cy - r * 0.95f, cx + r, cy - r * 0.45f);
                g.DrawLine(pen, cx + r * 0.55f, cy - r * 0.95f, cx + r * 0.18f, cy - r * 0.95f);
                break;
            case Glyph.Close:
                g.DrawLine(pen, cx - r * 0.55f, cy - r * 0.55f, cx + r * 0.55f, cy + r * 0.55f);
                g.DrawLine(pen, cx + r * 0.55f, cy - r * 0.55f, cx - r * 0.55f, cy + r * 0.55f);
                break;
            case Glyph.Minimize:
                g.DrawLine(pen, cx - r * 0.6f, cy + r * 0.45f, cx + r * 0.6f, cy + r * 0.45f);
                break;
            case Glyph.Maximize:
                g.DrawRectangle(pen, cx - r * 0.62f, cy - r * 0.62f, r * 1.24f, r * 1.24f);
                g.DrawLine(pen, cx - r * 0.62f, cy - r * 0.32f, cx + r * 0.62f, cy - r * 0.32f);
                break;
            case Glyph.Bolt:
                using (var path = new GraphicsPath())
                {
                    path.AddPolygon(new[]
                    {
                        new PointF(cx + r * 0.15f, cy - r), new PointF(cx - r * 0.65f, cy + r * 0.1f),
                        new PointF(cx - r * 0.05f, cy + r * 0.1f), new PointF(cx - r * 0.25f, cy + r),
                        new PointF(cx + r * 0.7f, cy - r * 0.2f), new PointF(cx + r * 0.05f, cy - r * 0.2f),
                    });
                    path.CloseFigure();
                    g.FillPath(Theme.B(Theme.Alpha(color, 210)), path);
                    g.DrawPath(pen, path);
                }
                break;
            case Glyph.Trash:
                g.DrawLine(pen, cx - r * 0.4f, cy - r * 0.55f, cx + r * 0.4f, cy - r * 0.55f);
                g.DrawLine(pen, cx - r * 0.75f, cy - r * 0.55f, cx + r * 0.75f, cy - r * 0.55f);
                g.DrawLine(pen, cx, cy - r * 0.85f, cx, cy - r * 0.55f);
                g.DrawRectangle(pen, cx - r * 0.6f, cy - r * 0.35f, r * 1.2f, r * 1.15f);
                g.DrawLine(pen, cx - r * 0.25f, cy - r * 0.1f, cx - r * 0.25f, cy + r * 0.5f);
                g.DrawLine(pen, cx + r * 0.25f, cy - r * 0.1f, cx + r * 0.25f, cy + r * 0.5f);
                break;
            case Glyph.Folder:
                g.DrawRectangle(pen, cx - r * 0.8f, cy - r * 0.55f, r * 1.6f, r * 1.15f);
                g.DrawLine(pen, cx - r * 0.8f, cy - r * 0.55f, cx - r * 0.35f, cy - r * 0.55f);
                break;
            case Glyph.Copy:
                g.DrawRectangle(pen, cx - r * 0.75f, cy - r * 0.75f, r * 1.1f, r * 1.1f);
                g.DrawRectangle(pen, cx - r * 0.25f, cy - r * 0.25f, r * 1.1f, r * 1.1f);
                break;
            case Glyph.Chevron:
                g.DrawLine(pen, cx - r * 0.45f, cy - r * 0.2f, cx, cy + r * 0.25f);
                g.DrawLine(pen, cx, cy + r * 0.25f, cx + r * 0.45f, cy - r * 0.2f);
                break;
            case Glyph.Check:
                g.DrawLine(pen, cx - r * 0.6f, cy, cx - r * 0.1f, cy + r * 0.5f);
                g.DrawLine(pen, cx - r * 0.1f, cy + r * 0.5f, cx + r * 0.65f, cy - r * 0.55f);
                break;
            case Glyph.Registry:
                g.DrawRectangle(pen, cx - r * 0.7f, cy - r * 0.7f, r * 0.6f, r * 0.6f);
                g.DrawRectangle(pen, cx + r * 0.1f, cy - r * 0.7f, r * 0.6f, r * 0.6f);
                g.DrawRectangle(pen, cx - r * 0.7f, cy + r * 0.1f, r * 0.6f, r * 0.6f);
                g.DrawRectangle(pen, cx + r * 0.1f, cy + r * 0.1f, r * 0.6f, r * 0.6f);
                break;
            case Glyph.Filter:
                for (int i = -1; i <= 1; i++)
                    g.DrawLine(pen, cx - r * 0.8f, cy + i * r * 0.55f, cx + r * 0.8f, cy + i * r * 0.55f);
                break;
            case Glyph.ArrowUp:
            case Glyph.ArrowDown:
                float s = glyph == Glyph.ArrowUp ? 1 : -1;
                g.DrawLine(pen, cx - r * 0.5f, cy + s * r * 0.3f, cx, cy - s * r * 0.3f);
                g.DrawLine(pen, cx, cy - s * r * 0.3f, cx + r * 0.5f, cy + s * r * 0.3f);
                break;
            case Glyph.Toolbox:
                g.DrawRectangle(pen, cx - r * 0.85f, cy - r * 0.45f, r * 1.7f, r * 1.15f);
                g.DrawLine(pen, cx - r * 0.3f, cy - r * 0.45f, cx - r * 0.3f, cy - r * 0.85f);
                g.DrawLine(pen, cx + r * 0.3f, cy - r * 0.45f, cx + r * 0.3f, cy - r * 0.85f);
                g.DrawLine(pen, cx - r * 0.42f, cy - r * 0.85f, cx + r * 0.42f, cy - r * 0.85f);
                g.DrawLine(pen, cx - r * 0.85f, cy + r * 0.1f, cx + r * 0.85f, cy + r * 0.1f);
                break;
            case Glyph.Menu:
                g.DrawRectangle(pen, cx - r * 0.85f, cy - r * 0.8f, r * 1.7f, r * 1.6f);
                for (int i = -1; i <= 1; i++)
                    g.DrawLine(pen, cx - r * 0.5f, cy + i * r * 0.45f, cx + r * 0.5f, cy + i * r * 0.45f);
                break;
            case Glyph.Camera:
                g.DrawRectangle(pen, cx - r * 0.85f, cy - r * 0.5f, r * 1.7f, r * 1.15f);
                g.DrawLine(pen, cx - r * 0.45f, cy - r * 0.5f, cx - r * 0.25f, cy - r * 0.85f);
                g.DrawLine(pen, cx + r * 0.25f, cy - r * 0.85f, cx + r * 0.45f, cy - r * 0.5f);
                g.DrawLine(pen, cx - r * 0.45f, cy - r * 0.85f, cx + r * 0.25f, cy - r * 0.85f);
                g.DrawEllipse(pen, cx - r * 0.32f, cy - r * 0.24f, r * 0.64f, r * 0.64f);
                break;
            case Glyph.Plus:
                g.DrawLine(pen, cx - r * 0.6f, cy, cx + r * 0.6f, cy);
                g.DrawLine(pen, cx, cy - r * 0.6f, cx, cy + r * 0.6f);
                break;
            case Glyph.Shield:
                using (var shield = new GraphicsPath())
                {
                    shield.AddPolygon(new[]
                    {
                        new PointF(cx, cy - r * 0.9f), new PointF(cx + r * 0.75f, cy - r * 0.55f),
                        new PointF(cx + r * 0.6f, cy + r * 0.45f), new PointF(cx, cy + r * 0.95f),
                        new PointF(cx - r * 0.6f, cy + r * 0.45f), new PointF(cx - r * 0.75f, cy - r * 0.55f),
                    });
                    shield.CloseFigure();
                    g.DrawPath(pen, shield);
                    g.DrawLine(pen, cx - r * 0.28f, cy, cx - r * 0.05f, cy + r * 0.28f);
                    g.DrawLine(pen, cx - r * 0.05f, cy + r * 0.28f, cx + r * 0.32f, cy - r * 0.3f);
                }
                break;
            case Glyph.Undo:
                g.DrawArc(pen, cx - r * 0.7f, cy - r * 0.7f, r * 1.4f, r * 1.4f, 30, 250);
                g.DrawLine(pen, cx - r * 0.68f, cy - r * 0.08f, cx - r * 0.68f, cy - r * 0.55f);
                g.DrawLine(pen, cx - r * 0.68f, cy - r * 0.55f, cx - r * 0.25f, cy - r * 0.55f);
                break;
            case Glyph.Image:
                g.DrawRectangle(pen, cx - r * 0.85f, cy - r * 0.7f, r * 1.7f, r * 1.4f);
                g.DrawEllipse(pen, cx + r * 0.35f, cy - r * 0.45f, r * 0.3f, r * 0.3f);
                g.DrawLine(pen, cx - r * 0.8f, cy + r * 0.55f, cx - r * 0.1f, cy - r * 0.1f);
                g.DrawLine(pen, cx - r * 0.1f, cy - r * 0.1f, cx + r * 0.3f, cy + r * 0.55f);
                break;
            case Glyph.Power:
                g.DrawArc(pen, cx - r * 0.65f, cy - r * 0.65f, r * 1.3f, r * 1.3f, 135, 270);
                g.DrawLine(pen, cx, cy - r * 0.9f, cx, cy - r * 0.15f);
                break;
            case Glyph.Gear:
                for (int i = 0; i < 8; i++)
                {
                    double angle = Math.PI * i / 4;
                    float cos = (float)Math.Cos(angle), sin = (float)Math.Sin(angle);
                    g.DrawLine(pen, cx + cos * r * 0.62f, cy + sin * r * 0.62f,
                        cx + cos * r * 0.98f, cy + sin * r * 0.98f);
                }
                g.DrawEllipse(pen, cx - r * 0.62f, cy - r * 0.62f, r * 1.24f, r * 1.24f);
                g.DrawEllipse(pen, cx - r * 0.22f, cy - r * 0.22f, r * 0.44f, r * 0.44f);
                break;
            case Glyph.Collapse:
            case Glyph.Expand:
                float dir = glyph == Glyph.Collapse ? 1 : -1;   // Collapse 尖朝左，Expand 尖朝右
                for (int i = 0; i < 2; i++)
                {
                    float x = cx + dir * (r * 0.15f + i * r * 0.6f);
                    g.DrawLine(pen, x + dir * r * 0.25f, cy - r * 0.55f, x - dir * r * 0.25f, cy);
                    g.DrawLine(pen, x - dir * r * 0.25f, cy, x + dir * r * 0.25f, cy + r * 0.55f);
                }
                break;
        }
    }
}
