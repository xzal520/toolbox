namespace Toolbox.UI;

/// <summary>界面行为偏好，统一读写 settings.ini，避免各处散落字符串键。</summary>
internal static class AppPrefs
{
    /// <summary>点最小化时缩到托盘（关闭后恢复成普通最小化，任务栏仍可见）。</summary>
    public static bool TrayOnMinimize
    {
        get => Flag("app.trayOnMinimize", true);
        set => Settings.Set("app.trayOnMinimize", value ? "1" : "0");
    }

    /// <summary>点关闭按钮时缩到托盘而不是退出。</summary>
    public static bool TrayOnClose
    {
        get => Flag("app.trayOnClose", false);
        set => Settings.Set("app.trayOnClose", value ? "1" : "0");
    }

    /// <summary>随系统启动时直接驻留托盘，不弹主窗口。</summary>
    public static bool StartMinimized
    {
        get => Flag("app.startMinimized", false);
        set => Settings.Set("app.startMinimized", value ? "1" : "0");
    }

    /// <summary>首次缩到托盘时提示一次，之后不再打扰。</summary>
    public static bool TrayHintShown
    {
        get => Flag("app.trayHint", false);
        set => Settings.Set("app.trayHint", value ? "1" : "0");
    }

    private static bool Flag(string key, bool fallback) =>
        Settings.Get(key, fallback ? "1" : "0") == "1";
}
