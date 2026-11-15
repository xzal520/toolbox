using System.Runtime.InteropServices;
using System.Windows.Forms;
using Toolbox.Core;
using Toolbox.UI;

namespace Toolbox;

internal static class Program
{
    public const string WindowTitle = "实用工具箱 - 卸载 · 右键菜单 · 截屏";

    /// <summary>再次启动时通知已有实例把窗口恢复到前台（窗口可能正缩在托盘里）。</summary>
    internal const int WmRestoreWindow = 0x0400 + 21;   // WM_APP + 21

    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        Theme.Set(Settings.LoadTheme());
        if (args.Any(a => a.Equals("--selftest", StringComparison.OrdinalIgnoreCase)))
        {
            RunSelfTest();
            return;
        }
        if (args.Any(a => a.Equals("--ocrtest", StringComparison.OrdinalIgnoreCase)))
        {
            RunOcrTest(args);
            return;
        }
        if (args.Any(a => a.Equals("--viewer", StringComparison.OrdinalIgnoreCase)))
        {
            InstallCrashLog();   // 命令行预览也是长期运行的窗口，异常要能落日志
            RunViewerPreview(args);
            return;
        }
        if (args.Any(a => a.Equals("--install-menu", StringComparison.OrdinalIgnoreCase)))
        {
            RunMenuCommand(true, args.Contains("--silent"));
            return;
        }
        if (args.Any(a => a.Equals("--uninstall-menu", StringComparison.OrdinalIgnoreCase)))
        {
            RunMenuCommand(false, args.Contains("--silent"));
            return;
        }

        string target = ExtractTarget(args);
        using var mutex = new Mutex(true, @"Local\Toolbox.SingleInstance", out bool firstInstance);
        if (!firstInstance && string.IsNullOrEmpty(target))
        {
            NativeMethods.BringExistingToFront(WindowTitle);
            return;
        }
        GC.KeepAlive(mutex);

        // 只有开机自启（启动项带 --tray）才允许后台驻留，手动双击 exe 一律显示主窗口。
        // 旧逻辑把 StartMinimized 也套到手动启动上：设置里一勾「开机自启」会自动打开该开关，
        // 之后双击 exe 就只进托盘、窗口再也看不到。
        bool autoStart = args.Any(a => a.Equals("--tray", StringComparison.OrdinalIgnoreCase));
        bool startInTray = autoStart && AppPrefs.StartMinimized;

        // 以管理员重启并安装右键菜单：装完继续正常启动界面，用户不用再点一次开关
        if (args.Any(a => a.Equals("--elevated-install", StringComparison.OrdinalIgnoreCase)))
        {
            WriteDiagnostic("Toolbox-menu.log",
                DateTime.Now.ToString("s") + " " + ContextMenuRegistry.InstallOwn());
        }

        InstallCrashLog();
        try
        {
            Application.Run(new MainForm(target, startInTray));
        }
        catch (Exception ex)
        {
            WriteDiagnostic("Toolbox-error.log", "启动失败：\n" + ex);
            try
            {
                MessageBox.Show($"程序启动失败：{ex.Message}", "实用工具箱",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch { /* 无桌面会话时无法弹窗 */ }
        }
    }

    /// <summary>直接以命令行打开某张截图的大图预览（也便于无头验证查看器渲染）。</summary>
    private static void RunViewerPreview(string[] args)
    {
        var files = ScreenshotService.Recent(20);
        string path = args.FirstOrDefault(a =>
            a.EndsWith(".png", StringComparison.OrdinalIgnoreCase) && File.Exists(a))
            ?? files.FirstOrDefault()?.FullName;
        if (path is not null && !files.Any(f =>
                string.Equals(f.FullName, path, StringComparison.OrdinalIgnoreCase)))
            files.Insert(0, new FileInfo(path));   // 命令行指定的图片也要能正确打开
        using var viewer = new ImageViewerForm(files, path);
        viewer.ShowDialog();
    }

    /// <summary>无界面 OCR 自检：验证系统内置引擎与文本清理，结果写到临时目录。</summary>
    private static void RunOcrTest(string[] args)
    {
        var lines = new List<string> { "OCRTEST " + DateTime.Now.ToString("s") };
        try
        {
            lines.Add("AVAILABLE=" + OcrService.IsAvailable);
            lines.Add("LANGUAGE=" + OcrService.LanguageName);
            string path = args.FirstOrDefault(a =>
                a.EndsWith(".png", StringComparison.OrdinalIgnoreCase) && File.Exists(a))
                ?? ScreenshotService.Recent(1).FirstOrDefault()?.FullName;
            lines.Add("PATH=" + (path ?? "none"));
            if (path is null) lines.Add("RESULT=NO_IMAGE");
            else
            {
                // Main 是 STA 且没有消息循环：丢到线程池跑，任何 WinRT 回调都不会卡死
                var result = Task.Run(() => OcrService.RecognizeAsync(path)).GetAwaiter().GetResult();
                lines.Add($"LINES={result.LineCount} WORDS={result.Words.Count}");
                lines.Add("TEXT_BEGIN\n" + result.Text + "\nTEXT_END");
                lines.Add("RESULT=OK");
            }
        }
        catch (Exception ex)
        {
            lines.Add("RESULT=FAIL\n" + ex);
            Environment.ExitCode = 1;
        }
        WriteDiagnostic("Toolbox-ocrtest.txt", string.Join("\n", lines));
    }

    /// <summary>
    /// 兜底异常日志：工具会读写注册表、抓屏、调用外部进程，出错时静默退出会让人无从排查。
    /// 日志写到 %LOCALAPPDATA%\Toolbox\crash.log，只保留最近 20KB。
    /// </summary>
    private static void InstallCrashLog()
    {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => LogCrash("UI线程", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => LogCrash("后台线程", e.ExceptionObject as Exception);
    }

    private static void LogCrash(string source, Exception ex)
    {
        try
        {
            string folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Toolbox");
            Directory.CreateDirectory(folder);
            string file = Path.Combine(folder, "crash.log");
            string text = $"[{DateTime.Now:s}] {source}\n{ex}\n\n";
            if (File.Exists(file) && new FileInfo(file).Length > 20 * 1024)
                File.Delete(file);
            File.AppendAllText(file, text);
            WriteDiagnostic("Toolbox-crash.txt", text);
        }
        catch { /* 日志失败不影响主流程 */ }
    }

    /// <summary>无界面自检：验证注册表扫描、卸载命令推导与窗体构造，结果写入临时目录。</summary>
    private static void RunSelfTest()
    {
        var lines = new List<string> { "SELFTEST " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") };
        try
        {
            var list = Core.RegistryScanner.Scan(false);
            lines.Add($"SCAN_OK count={list.Count}");
            lines.Add($"QUIET_COMMANDS={list.Count(e => !string.IsNullOrWhiteSpace(e.QuietUninstallString))}");
            lines.Add($"MENU_OK count={ContextMenuRegistry.Scan().Count} installed={ContextMenuRegistry.OwnInstalledCount}");
            bool hotkeyOk = GlobalHotkey.TryParse(
                Settings.Get("shot.hotkey", "Ctrl + Alt + A"), out _, out _, out string normalized);
            lines.Add($"HOTKEY_OK {hotkeyOk} {normalized}");
            lines.Add("TOOLS=3");
            TestMenuRoundTrip(lines);
            foreach (var entry in list.Take(6))
            {
                var plan = Core.UninstallEngine.BuildPlan(entry, Core.UninstallMode.Silent);
                lines.Add($"SAMPLE {entry.Name} | {plan.KindText} | silent={plan.IsSilent} | {plan.FullCommand}");
            }
            using (var form = new MainForm(""))
            {
                form.Show();
                Application.DoEvents();
                lines.Add("FORM_OK " + form.DumpLayout());
                form.Close();
            }
            lines.Add("RESULT=OK");
        }
        catch (Exception ex)
        {
            lines.Add("RESULT=FAIL\n" + ex);
            Environment.ExitCode = 1;
        }
        WriteDiagnostic("Toolbox-selftest.txt", string.Join("\n", lines));
    }

    /// <summary>用自建的临时菜单项验证 新增/禁用/删除备份/恢复 全链路，结束后清理干净。</summary>
    private static void TestMenuRoundTrip(List<string> lines)
    {
        const string label = "实用工具箱 自检项";
        try
        {
            string error = ContextMenuRegistry.AddCustom(label, "cmd.exe /c echo ok",
                @"Directory\Background\shell", "");
            lines.Add("MENU_ADD " + (error.Length == 0 ? "OK" : error));
            var entry = ContextMenuRegistry.Scan().FirstOrDefault(e => e.Caption == label && e.UserScope);
            lines.Add("MENU_FOUND " + (entry is not null));
            if (entry is null) return;

            lines.Add("MENU_DISABLE " + (ContextMenuRegistry.SetEnabled(entry, false).Length == 0 ? "OK" : "FAIL"));
            var disabled = ContextMenuRegistry.Scan().FirstOrDefault(e => e.Caption == label && e.UserScope);
            lines.Add("MENU_DISABLED " + (disabled is not null && !disabled.Enabled));

            int before = ContextMenuRegistry.ReadBackups().Count;
            string deleted = ContextMenuRegistry.Delete(disabled);
            lines.Add("MENU_DELETE " + (deleted.Length == 0 ? "OK" : "FAIL"));
            // 只恢复本次自检新增的备份，绝不触碰用户已有的备份记录
            if (deleted.Length == 0 && ContextMenuRegistry.ReadBackups().Count > before)
                lines.Add("MENU_RESTORE " + ContextMenuRegistry.RestoreLast());
            var restored = ContextMenuRegistry.Scan().FirstOrDefault(e => e.Caption == label && e.UserScope);
            if (restored is not null) ContextMenuRegistry.Delete(restored);
            lines.Add("MENU_CLEANUP " + ContextMenuRegistry.RemoveBackups(label));
        }
        catch (Exception ex) { lines.Add("MENU_ROUNDTRIP_EXCEPTION " + ex.Message); }
    }

    /// <summary>命令行安装/卸载右键菜单，供 .bat / .ps1 与工具页共用同一实现。</summary>
    private static void RunMenuCommand(bool install, bool silent = false)
    {
        string result = install ? ContextMenuRegistry.InstallOwn() : ContextMenuRegistry.UninstallOwn();
        WriteDiagnostic("Toolbox-menu.log", DateTime.Now.ToString("s") + " " + result);
        if (silent) return;
        try
        {
            MessageBox.Show(result, "实用工具箱 · 右键菜单",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch { /* 无桌面会话时只写日志 */ }
    }

    private static void WriteDiagnostic(string fileName, string content)
    {
        try { File.WriteAllText(Path.Combine(Path.GetTempPath(), fileName), content); }
        catch { /* 诊断日志不影响主流程 */ }
    }

    private static string ExtractTarget(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (arg.StartsWith("--target=", StringComparison.OrdinalIgnoreCase))
                return arg.Substring(9).Trim('"');
            if (arg.Equals("--target", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                return args[i + 1].Trim('"');
            if (!arg.StartsWith("--", StringComparison.Ordinal) && (File.Exists(arg) || Directory.Exists(arg)))
                return arg.Trim('"');
        }
        return "";
    }
}

internal static class NativeMethods
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string className, string windowName);
    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int command);
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();
    [DllImport("user32.dll")]
    private static extern IntPtr SetCapture(IntPtr hWnd);
    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")]
    private static extern bool IsChild(IntPtr parent, IntPtr child);
    [DllImport("user32.dll")]
    private static extern bool RedrawWindow(IntPtr hWnd, IntPtr updateRect, IntPtr updateRgn, uint flags);

    public static void BringExistingToFront(string title)
    {
        IntPtr handle = FindWindow(null, title);
        if (handle == IntPtr.Zero) return;
        // 走自定义消息而不是直接 ShowWindow：窗口可能被隐藏到托盘，
        // 需要它自己把 ShowInTaskbar 之类状态一并恢复。
        PostMessage(handle, Program.WmRestoreWindow, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>托盘唤回时把窗口还原并真正送到前台，避免只 Show 到其它窗口后面。</summary>
    public static void ForceForeground(IntPtr handle)
    {
        ShowWindow(handle, 9);   // SW_RESTORE
        SetForegroundWindow(handle);
    }

    public static void BeginCapture(IntPtr handle) => SetCapture(handle);
    public static void EndCapture() => ReleaseCapture();
    public static bool IsOwnWindow(IntPtr parent, IntPtr child) =>
        child == parent || IsChild(parent, child);

    /// <summary>
    /// 窗口尺寸变化后强制整窗（含所有子控件）立即重绘。
    /// SetWindowRgn / 停靠控件缩放只会让系统失效"新露出的区域"，不清掉旧位置像素会留下残影。
    /// </summary>
    public static void ForceFullRedraw(Form form)
    {
        const uint RdwInvalidate = 0x0001, RdwErase = 0x0004,
                   RdwAllChildren = 0x0080, RdwUpdateNow = 0x0100, RdwEraseNow = 0x0200;
        if (form.IsHandleCreated)
            RedrawWindow(form.Handle, IntPtr.Zero, IntPtr.Zero,
                RdwInvalidate | RdwErase | RdwAllChildren | RdwUpdateNow | RdwEraseNow);
    }

    public static void DragWindow(IntPtr handle)
    {
        ReleaseCapture();
        SendMessage(handle, 0x00A1, (IntPtr)2, IntPtr.Zero); // WM_NCLBUTTONDOWN + HTCAPTION
    }

    /// <summary>纯托管圆角区域：拖拽缩放会频繁触发 Resize，避免反复创建 GDI HRGN 句柄。</summary>
    public static void ApplyRoundRegion(Form form, int radius)
    {
        int w = form.Width, h = form.Height, d = Math.Min(radius * 2, Math.Min(w, h));
        using var path = new System.Drawing.Drawing2D.GraphicsPath();
        path.AddArc(0, 0, d, d, 180, 90);
        path.AddArc(w - d, 0, d, d, 270, 90);
        path.AddArc(w - d, h - d, d, d, 0, 90);
        path.AddArc(0, h - d, d, d, 90, 90);
        path.CloseFigure();
        var previous = form.Region;
        form.Region = new Region(path);
        previous?.Dispose();
    }
}
