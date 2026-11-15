using System.Reflection;
using System.Runtime.InteropServices;

namespace Toolbox.Core;

/// <summary>把右键传入的快捷方式 / 可执行文件 / 目录，映射回某条已安装软件记录。</summary>
internal static class ShortcutResolver
{
    public static string Resolve(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        try
        {
            if (!Path.GetExtension(path).Equals(".lnk", StringComparison.OrdinalIgnoreCase))
                return Path.GetFullPath(path);

            Type shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType is null) return "";
            object shell = Activator.CreateInstance(shellType);
            try
            {
                object folder = Invoke(shellType, shell, "NameSpace", Path.GetDirectoryName(path));
                object item = Invoke(shellType, folder, "ParseName", Path.GetFileName(path));
                object link = Get(shellType, item, "GetLink");
                object target = Get(shellType, link, "Path");
                return target as string ?? "";
            }
            finally
            {
                if (shell is not null && Marshal.IsComObject(shell)) Marshal.ReleaseComObject(shell);
            }
        }
        catch { return ""; }
    }

    /// <summary>按安装目录前缀优先、名称次之的方式打分，返回最可能的软件。</summary>
    public static SoftwareEntry Match(string target, IEnumerable<SoftwareEntry> entries)
    {
        if (string.IsNullOrWhiteSpace(target)) return null;
        string full;
        try { full = Path.GetFullPath(target); }
        catch { return null; }

        string fileName = Path.GetFileNameWithoutExtension(full).ToLowerInvariant();
        string dirName = new DirectoryInfo(Path.GetDirectoryName(full) ?? full).Name.ToLowerInvariant();
        SoftwareEntry best = null;
        int bestScore = 0;

        foreach (var entry in entries)
        {
            int score = 0;
            string loc = entry.InstallLocation?.TrimEnd(Path.DirectorySeparatorChar) ?? "";
            if (loc.Length > 3)
            {
                if (full.StartsWith(loc, StringComparison.OrdinalIgnoreCase)) score = 200 + loc.Length;
                else if (loc.StartsWith(full.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                    score = 150 + full.Length;
            }

            string display = entry.Name.ToLowerInvariant();
            if (fileName.Length >= 3 && (display.Contains(fileName) || fileName.Contains(display)))
                score = Math.Max(score, 70 + fileName.Length);
            if (dirName.Length >= 3 && (display.Contains(dirName) || dirName.Contains(display)))
                score = Math.Max(score, 60 + dirName.Length);
            if (display.Contains("update", StringComparison.OrdinalIgnoreCase)) score -= 20;

            if (score > bestScore) { bestScore = score; best = entry; }
        }
        return bestScore >= 60 ? best : null;
    }

    private static object Invoke(Type type, object instance, string member, params object[] args) =>
        type.InvokeMember(member, BindingFlags.InvokeMethod | BindingFlags.Public | BindingFlags.Instance,
            null, instance, args);

    private static object Get(Type type, object instance, string member) =>
        instance.GetType().InvokeMember(member,
            BindingFlags.GetProperty | BindingFlags.Public | BindingFlags.Instance, null, instance, null);
}
