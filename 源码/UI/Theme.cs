using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Toolbox.UI;

internal enum ThemeMode { Light, Dark, Auto }

/// <summary>一套完整配色。亮色与深色只在色值上不同，控件代码无需区分。</summary>
internal sealed class Palette
{
    public Color Window { get; init; }
    public Color Surface { get; init; }
    public Color SurfaceAlt { get; init; }
    public Color Elevated { get; init; }
    public Color ElevatedHover { get; init; }
    public Color Border { get; init; }
    public Color BorderStrong { get; init; }
    public Color Text { get; init; }
    public Color TextMuted { get; init; }
    public Color TextDim { get; init; }
    public Color Accent { get; init; }
    public Color AccentDim { get; init; }
    public Color AccentWash { get; init; }
    public Color Danger { get; init; }
    public Color DangerDim { get; init; }
    public Color Success { get; init; }
    public Color Warning { get; init; }
    public Color OnAccent { get; init; }

    private static Color C(string hex) => ColorTranslator.FromHtml(hex);

    public static readonly Palette Light = new()
    {
        Window = C("#EDF1F6"),
        Surface = C("#FFFFFF"),
        SurfaceAlt = C("#F4F7FA"),
        Elevated = C("#F6F8FB"),
        ElevatedHover = C("#E7EEF7"),
        Border = C("#E2E8F0"),
        BorderStrong = C("#C9D4E2"),
        Text = C("#101828"),
        TextMuted = C("#5B6879"),
        TextDim = C("#98A2B3"),
        Accent = C("#2E6BE6"),
        AccentDim = C("#DCE8FB"),
        AccentWash = C("#EDF3FE"),
        Danger = C("#D92D20"),
        DangerDim = C("#FEE4E2"),
        Success = C("#079455"),
        Warning = C("#B54708"),
        OnAccent = C("#FFFFFF"),
    };

    public static readonly Palette Dark = new()
    {
        Window = C("#0C0F14"),
        Surface = C("#11161E"),
        SurfaceAlt = C("#161D27"),
        Elevated = C("#1C2431"),
        ElevatedHover = C("#232D3C"),
        Border = C("#28313F"),
        BorderStrong = C("#374355"),
        Text = C("#E9EEF7"),
        TextMuted = C("#8B97AA"),
        TextDim = C("#5A6578"),
        Accent = C("#4EA8FF"),
        AccentDim = C("#1B3B5E"),
        AccentWash = C("#15293C"),
        Danger = C("#FF6076"),
        DangerDim = C("#43212B"),
        Success = C("#3DDC97"),
        Warning = C("#FFC46B"),
        OnAccent = C("#08111B"),
    };
}

/// <summary>全局配色、字体与绘制原语。颜色通过属性读取当前调色板，切换主题时所有自绘代码自动生效。</summary>
internal static class Theme
{
    private static Palette _palette = Palette.Light;
    private static readonly Dictionary<int, Brush> BrushCache = new();
    private static readonly Dictionary<int, Pen> PenCache = new();

    /// <summary>系统 DPI 缩放比例（96 DPI = 1.0）。所有硬编码尺寸都经过 <see cref="S"/> 缩放，
    /// 否则高 DPI 下字体像素高度会超过固定区域，出现文字上下截断。</summary>
    public static float DpiScale { get; }

    /// <summary>按当前 DPI 缩放一个逻辑像素值。</summary>
    public static int S(int value) => (int)Math.Round(value * DpiScale);

    static Theme()
    {
        try { DpiScale = Math.Max(1f, GetDpiForSystem() / 96f); }
        catch { DpiScale = 1f; }
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();

    public static ThemeMode Mode { get; private set; } = ThemeMode.Light;
    public static bool IsDark => ReferenceEquals(_palette, Palette.Dark);
    public static event Action Changed;

    public static void Set(ThemeMode mode)
    {
        Mode = mode;
        Palette next = mode switch
        {
            ThemeMode.Dark => Palette.Dark,
            ThemeMode.Light => Palette.Light,
            _ => SystemUsesDark() ? Palette.Dark : Palette.Light,
        };
        _palette = next;
        foreach (var brush in BrushCache.Values) brush.Dispose();
        foreach (var pen in PenCache.Values) pen.Dispose();
        BrushCache.Clear();
        PenCache.Clear();
        Changed?.Invoke();
    }

    public static bool SystemUsesDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return Convert.ToInt32(key?.GetValue("AppsUseLightTheme") ?? 1) == 0;
        }
        catch { return false; }
    }

    public static Color Window => _palette.Window;
    public static Color Surface => _palette.Surface;
    public static Color SurfaceAlt => _palette.SurfaceAlt;
    public static Color Elevated => _palette.Elevated;
    public static Color ElevatedHover => _palette.ElevatedHover;
    public static Color Border => _palette.Border;
    public static Color BorderStrong => _palette.BorderStrong;
    public static Color Text => _palette.Text;
    public static Color TextMuted => _palette.TextMuted;
    public static Color TextDim => _palette.TextDim;
    public static Color Accent => _palette.Accent;
    public static Color AccentDim => _palette.AccentDim;
    public static Color AccentWash => _palette.AccentWash;
    public static Color Danger => _palette.Danger;
    public static Color DangerDim => _palette.DangerDim;
    public static Color Success => _palette.Success;
    public static Color Warning => _palette.Warning;
    public static Color OnAccent => _palette.OnAccent;

    public static Color C(string hex) => ColorTranslator.FromHtml(hex);

    public static readonly Font FontBase = new("Microsoft YaHei UI", 9.5F, FontStyle.Regular, GraphicsUnit.Point);
    public static readonly Font FontBold = new("Microsoft YaHei UI", 9.5F, FontStyle.Bold, GraphicsUnit.Point);
    public static readonly Font FontSmall = new("Microsoft YaHei UI", 8.5F, FontStyle.Regular, GraphicsUnit.Point);
    public static readonly Font FontSmallBold = new("Microsoft YaHei UI", 8.5F, FontStyle.Bold, GraphicsUnit.Point);
    public static readonly Font FontTitle = new("Microsoft YaHei UI", 11.5F, FontStyle.Bold, GraphicsUnit.Point);
    public static readonly Font FontLogo = new("Microsoft YaHei UI", 14F, FontStyle.Bold, GraphicsUnit.Point);
    public static readonly Font FontMono = new("Cascadia Mono", 8.5F, FontStyle.Regular, GraphicsUnit.Point);

    public static Brush B(Color color)
    {
        int key = color.ToArgb();
        if (!BrushCache.TryGetValue(key, out var brush))
            BrushCache[key] = brush = new SolidBrush(color);
        return brush;
    }

    public static Pen P(Color color, float width = 1f)
    {
        int key = (color.ToArgb() * 397) ^ (int)(width * 1000);
        if (!PenCache.TryGetValue(key, out var pen))
            PenCache[key] = pen = new Pen(color, width);
        return pen;
    }

    public static Color Alpha(Color color, int alpha) => Color.FromArgb(alpha, color.R, color.G, color.B);

    public static GraphicsPath Round(Rectangle rect, int radius)
    {
        var path = new GraphicsPath();
        int d = Math.Min(radius * 2, Math.Min(rect.Width, rect.Height));
        if (d <= 0) { path.AddRectangle(rect); return path; }
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    public static void Box(Graphics g, Rectangle rect, int radius, Color fill, Color? border = null, float borderWidth = 1f)
    {
        using var path = Round(rect, radius);
        g.FillPath(B(fill), path);
        if (border.HasValue) g.DrawPath(P(border.Value, borderWidth), path);
    }

    public static void DrawText(Graphics g, string text, Font font, Color color, Rectangle area,
        TextFormatFlags flags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis)
    {
        if (!string.IsNullOrEmpty(text)) TextRenderer.DrawText(g, text, font, area, color, flags);
    }

    public static void Badge(Graphics g, string text, Rectangle rect, Color fill, Color foreground)
    {
        Box(g, rect, rect.Height / 2, fill);
        DrawText(g, text, FontSmall, foreground, rect,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }

    public static void SetQuality(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
    }
}

/// <summary>轻量键值配置，写在用户本地配置目录。主题与各工具设置共用同一份文件。</summary>
internal static class Settings
{
    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Toolbox", "settings.ini");

    private static Dictionary<string, string> Read()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(FilePath)) return map;
            foreach (string line in File.ReadAllLines(FilePath))
            {
                int split = line.IndexOf('=');
                if (split <= 0) continue;
                map[line.Substring(0, split).Trim()] = line.Substring(split + 1).Trim();
            }
        }
        catch { /* 配置读取失败时回落默认值 */ }
        return map;
    }

    private static void Write(Dictionary<string, string> map)
    {
        try
        {
            string directory = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(FilePath, string.Join(Environment.NewLine, map.Select(kv => kv.Key + "=" + kv.Value)));
        }
        catch { /* 配置写入失败不影响使用 */ }
    }

    public static string Get(string key, string fallback = "")
    {
        var map = Read();
        return map.TryGetValue(key, out string value) ? value : fallback;
    }

    public static void Set(string key, string value)
    {
        var map = Read();
        map[key] = value ?? "";
        Write(map);
    }

    public static ThemeMode LoadTheme() => Get("theme", "light").ToLowerInvariant() switch
    {
        "dark" => ThemeMode.Dark,
        "auto" => ThemeMode.Auto,
        _ => ThemeMode.Light,
    };

    public static void SaveTheme(ThemeMode mode) => Set("theme", mode.ToString().ToLowerInvariant());
}
