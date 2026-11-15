using System.Drawing;

namespace Toolbox.UI;

/// <summary>按 DisplayIcon / 卸载程序路径提取图标，结果缓存，避免重复读盘。</summary>
internal static class IconLoader
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, Icon> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> Failed = new(StringComparer.OrdinalIgnoreCase);

    public static Icon Get(string cacheKey, string displayIcon, string fallbackExe)
    {
        lock (Gate)
        {
            if (Cache.TryGetValue(cacheKey, out var hit)) return hit;
            if (Failed.Contains(cacheKey)) return null;
        }

        Icon icon = TryLoad(displayIcon) ?? TryLoad(fallbackExe);
        lock (Gate)
        {
            if (icon is not null) Cache[cacheKey] = icon;
            else Failed.Add(cacheKey);
        }
        return icon;
    }

    private static Icon TryLoad(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        string path = raw.Trim();
        int comma = path.IndexOf(',');
        if (comma > 0) path = path.Substring(0, comma);
        path = path.Trim().Trim('"');
        if (path.Length < 3) return null;
        try
        {
            path = Environment.ExpandEnvironmentVariables(path);
            if (!File.Exists(path)) return null;
            if (path.EndsWith(".ico", StringComparison.OrdinalIgnoreCase))
            {
                using var source = new Icon(path);
                return new Icon(source, 32, 32);
            }
            return Icon.ExtractAssociatedIcon(path);
        }
        catch { return null; }
    }

    public static Color HueColor(int hue)
    {
        double h = hue / 360.0, s = 0.42, l = 0.48;
        double q = l < 0.5 ? l * (1 + s) : l + s - l * s;
        double p = 2 * l - q;
        return Color.FromArgb(
            (int)(255 * HueToRgb(p, q, h + 1.0 / 3)),
            (int)(255 * HueToRgb(p, q, h)),
            (int)(255 * HueToRgb(p, q, h - 1.0 / 3)));
    }

    private static double HueToRgb(double p, double q, double t)
    {
        if (t < 0) t += 1;
        if (t > 1) t -= 1;
        if (t < 1.0 / 6) return p + (q - p) * 6 * t;
        if (t < 1.0 / 2) return q;
        if (t < 2.0 / 3) return p + (q - p) * (2.0 / 3 - t) * 6;
        return p;
    }
}
