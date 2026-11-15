using System.Windows.Forms;
using Microsoft.Win32;

namespace Toolbox.Core;

/// <summary>
/// 开机自启动：写 HKCU 的 Run 项，不需要管理员权限，也不依赖安装目录。
/// 启动参数带 --tray 表示这是开机自启；是否驻留托盘由「开机自启时直接驻留托盘」设置决定。
/// </summary>
internal static class StartupRegistry
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Toolbox";
    private const string TrayArgument = "--tray";

    /// <summary>当前进程的可执行文件路径（单文件发布时就是 exe 本身）。</summary>
    public static string ExecutablePath =>
        Environment.ProcessPath ?? Application.ExecutablePath;

    public static bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                return key?.GetValue(ValueName) is string value && value.Length > 0;
            }
            catch { return false; }
        }
    }

    /// <summary>注册表里记录的命令与实际路径不一致时（程序被移动过），需要重写。</summary>
    public static bool IsStale
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                if (key?.GetValue(ValueName) is not string value) return false;
                return !value.Contains(ExecutablePath, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }
    }

    public static bool Enable(out string error)
    {
        error = "";
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, true);
            if (key is null) { error = "无法打开启动项注册表键"; return false; }
            key.SetValue(ValueName, $"\"{ExecutablePath}\" {TrayArgument}", RegistryValueKind.String);
            return true;
        }
        catch (Exception ex) { error = ex.Message; return false; }
    }

    public static bool Disable(out string error)
    {
        error = "";
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, true);
            if (key?.GetValue(ValueName) is null) return true;   // 本来就没设置
            key.DeleteValue(ValueName, throwOnMissingValue: false);
            return true;
        }
        catch (Exception ex) { error = ex.Message; return false; }
    }
}
