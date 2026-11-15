using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Toolbox.UI;

/// <summary>全局热键封装。MOD_NOREPEAT 避免长按重复触发截屏。</summary>
internal sealed class GlobalHotkey : IDisposable
{
    public const int WmHotKey = 0x0312;
    private const int HotkeyId = 0x5A31;
    private const uint ModAlt = 0x0001, ModControl = 0x0002, ModShift = 0x0004, ModWin = 0x0008;
    private const uint ModNoRepeat = 0x4000;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);
    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private readonly IntPtr _handle;
    public bool Registered { get; private set; }
    public string Error { get; internal set; } = "";
    public string Normalized { get; private set; } = "";

    public GlobalHotkey(IntPtr handle) => _handle = handle;

    public bool Matches(int id) => id == HotkeyId;

    public bool Register(string text)
    {
        Unregister();
        Error = "";
        Normalized = "";
        if (string.IsNullOrWhiteSpace(text)) { Error = "未设置快捷键"; return false; }
        if (!TryParse(text, out uint modifiers, out uint vk, out string normalized))
        {
            Error = "快捷键格式无法识别，例如 Ctrl + Alt + A";
            return false;
        }
        Normalized = normalized;
        if (!RegisterHotKey(_handle, HotkeyId, modifiers | ModNoRepeat, vk))
        {
            Error = "快捷键已被其它程序占用，请换一组组合键";
            return false;
        }
        Registered = true;
        return true;
    }

    public void Unregister()
    {
        if (!Registered) return;
        try { UnregisterHotKey(_handle, HotkeyId); }
        catch { /* 窗口已销毁时忽略 */ }
        Registered = false;
    }

    public void Dispose() => Unregister();

    public static bool TryParse(string text, out uint modifiers, out uint vk, out string normalized)
    {
        modifiers = 0; vk = 0; normalized = "";
        string[] parts = text.Split('+').Select(p => p.Trim())
            .Where(p => p.Length > 0).ToArray();
        if (parts.Length == 0) return false;

        var names = new List<string>();
        string keyToken = "";
        foreach (string part in parts)
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl": case "control": modifiers |= ModControl; names.Add("Ctrl"); break;
                case "alt": modifiers |= ModAlt; names.Add("Alt"); break;
                case "shift": modifiers |= ModShift; names.Add("Shift"); break;
                case "win": case "windows": modifiers |= ModWin; names.Add("Win"); break;
                default: keyToken = part; break;
            }
        }
        if (keyToken.Length == 0) return false;

        string alias = keyToken.ToLowerInvariant() switch
        {
            "prtsc" or "printscreen" or "print screen" => "PrintScreen",
            "esc" => "Escape",
            "del" => "Delete",
            "ins" => "Insert",
            "space" => "Space",
            _ => keyToken,
        };
        if (alias.Length == 1 && char.IsLetterOrDigit(alias[0])) alias = alias.ToUpperInvariant();
        if (alias.Length == 1 && char.IsDigit(alias[0])) alias = "D" + alias;   // 显示为 1..9，实际是 D1..D9
        if (!Enum.TryParse(alias, true, out Keys key) || key == Keys.None) return false;

        vk = (uint)key & 0xFF;
        if (vk == 0) return false;
        names.Add(alias);
        normalized = string.Join(" + ", names);
        return true;
    }
}

/// <summary>
/// 截屏工具服务：热键注册、屏幕捕获、保存、剪贴板与历史通知。
/// UI 与消息循环共用同一入口，避免多处重复注册热键。
/// </summary>
internal static class ScreenshotService
{
    private static int S(int value) => Theme.S(value);

    public static GlobalHotkey Hotkey { get; private set; }
    public static event Action<string> Saved;
    public static event Action StatusChanged;

    /// <summary>外部写入历史目录（涂鸦存副本 / 覆盖原图）后通知主界面刷新，不做容量清理。</summary>
    public static void NotifySaved(string path) => Saved?.Invoke(path);

    private static string _defaultFolder;

    /// <summary>
    /// 截图目录。旧版叫「Toolbox 截图」，改名时把老目录整体搬过去，
    /// 避免升级后历史截图在列表里“消失”。
    /// </summary>
    public static string DefaultFolder
    {
        get
        {
            if (_defaultFolder is not null) return _defaultFolder;
            string pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            string target = Path.Combine(pictures, "实用工具箱 截图");
            string legacy = Path.Combine(pictures, "Toolbox 截图");
            try
            {
                if (!Directory.Exists(target) && Directory.Exists(legacy)) Directory.Move(legacy, target);
                _defaultFolder = target;
            }
            catch { _defaultFolder = Directory.Exists(legacy) ? legacy : target; }
            return _defaultFolder;
        }
    }

    public static string Folder
    {
        get => Settings.Get("shot.folder", DefaultFolder);
        set => Settings.Set("shot.folder", value);
    }

    public static bool Enabled
    {
        get => Settings.Get("shot.enabled", "1") == "1";
        set => Settings.Set("shot.enabled", value ? "1" : "0");
    }

    public static bool FullScreen
    {
        get => Settings.Get("shot.fullscreen", "0") == "1";
        set => Settings.Set("shot.fullscreen", value ? "1" : "0");
    }

    public static bool CopyToClipboard
    {
        get => Settings.Get("shot.clipboard", "1") == "1";
        set => Settings.Set("shot.clipboard", value ? "1" : "0");
    }

    public static string HotkeyText
    {
        get => Settings.Get("shot.hotkey", "Ctrl + Alt + A");
        set => Settings.Set("shot.hotkey", value);
    }

    /// <summary>历史截图保留上限，超出后自动清理最旧的。</summary>
    public static int KeepCount
    {
        get => int.TryParse(Settings.Get("shot.keep", "10"), out int n) ? Math.Clamp(n, 1, 200) : 10;
        set => Settings.Set("shot.keep", Math.Clamp(value, 1, 200).ToString());
    }

    /// <summary>最近一次自动清理掉的数量，用于状态栏提示。</summary>
    public static int LastTrimmed { get; private set; }

    public static void Initialize(IntPtr handle)
    {
        Hotkey = new GlobalHotkey(handle);
        Register();
        // 启动时绝不清理历史文件：容量收敛只在真正保存了新截图后做，且送回收站可还原。
        // 否则光是打开程序（或跑自检）就可能静默删掉用户最旧的截图。
    }

    /// <summary>窗口句柄重建后重新绑定热键：RegisterHotKey 是绑在 HWND 上的，句柄没了就失效。</summary>
    public static void Rebind(IntPtr handle)
    {
        Hotkey?.Dispose();
        Hotkey = new GlobalHotkey(handle);
        Register();
    }

    public static void Register()
    {
        if (Hotkey is null) return;
        if (!Enabled) { Hotkey.Unregister(); Hotkey.Error = "截屏热键已关闭"; }
        else Hotkey.Register(HotkeyText);
        StatusChanged?.Invoke();
    }

    public static void Shutdown()
    {
        Hotkey?.Dispose();
        Hotkey = null;
    }

    public static void OnHotkey()
    {
        // WM_HOTKEY 是在 WndProc 里同步收到的；延后到消息循环再弹覆盖层，
        // 避免在消息派发过程中直接开启嵌套模态循环。
        var host = Application.OpenForms.Count > 0 ? Application.OpenForms[0] : null;
        if (host is null || !host.IsHandleCreated) return;
        host.BeginInvoke(new Action(CaptureAndSave));
    }

    /// <summary>
    /// 抓屏 → 框选 → 存盘。PNG 编码放到后台线程：
    /// 4K 全屏图压缩往往要 1 秒以上，同步做会让覆盖层关掉后界面僵住。
    /// </summary>
    public static void CaptureAndSave()
    {
        var context = SynchronizationContext.Current;
        try
        {
            Rectangle bounds = SystemInformation.VirtualScreen;
            using var full = new Bitmap(bounds.Width, bounds.Height);
            using (var g = Graphics.FromImage(full))
                g.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);

            Rectangle selection;
            DoodleLayer doodle = null;
            if (FullScreen)
            {
                selection = new Rectangle(0, 0, full.Width, full.Height);
            }
            else
            {
                using var overlay = new CaptureOverlay(full, bounds);
                if (overlay.ShowDialog() != DialogResult.OK) return;
                selection = overlay.Selection;
                doodle = overlay.Doodle;
            }
            if (selection.Width < 2 || selection.Height < 2) return;

            var snap = full.Clone(selection, full.PixelFormat);
            if (doodle is not null && doodle.HasStrokes)
                doodle.Bake(snap, new PointF(selection.X, selection.Y));   // 选区外的笔画自然被裁掉

            string folder = Folder;
            Directory.CreateDirectory(folder);
            string file = Path.Combine(folder, "截图_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + ".png");

            Task.Run(() =>
            {
                try { snap.Save(file, ImageFormat.Png); }
                catch { /* 磁盘错误等：下面仍会刷新列表，用户能看到没生成 */ }
                finally { snap.Dispose(); }

                // 剪贴板操作（位图 → DIB → 全局内存）在大图上能到 200ms 以上，
                // 放到独立 STA 线程：UI 完全不等，粘贴时也一定拿得到。
                if (CopyToClipboard) CopyToClipboardAsync(file);

                void Finish()
                {
                    LastTrimmed = TrimExcess();
                    Saved?.Invoke(file);
                }
                if (context is not null) context.Post(_ => Finish(), null);
                else Finish();
            });
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke();
            MessageBox.Show("截屏失败：" + ex.Message, "快捷键截屏",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// <summary>
    /// 在独立 STA 线程里把文件复制到剪贴板。剪贴板 API 要求 STA，
    /// 但它本身的耗时与界面无关，没必要占着 UI 线程。
    /// </summary>
    private static void CopyToClipboardAsync(string file)
    {
        var thread = new Thread(() =>
        {
            for (int attempt = 0; attempt < 4; attempt++)
            {
                Bitmap bitmap = null;
                try
                {
                    using var image = Image.FromFile(file);
                    bitmap = new Bitmap(image);
                    Clipboard.SetImage(bitmap);
                    return;   // 剪贴板接管该位图，不再 Dispose
                }
                catch
                {
                    bitmap?.Dispose();
                    Thread.Sleep(60);
                }
            }
        })
        { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    public static List<FileInfo> Recent(int take = 40)
    {
        try
        {
            var dir = new DirectoryInfo(Folder);
            if (!dir.Exists) return new List<FileInfo>();
            return dir.GetFiles("*.png").OrderByDescending(f => f.LastWriteTime).Take(take).ToList();
        }
        catch { return new List<FileInfo>(); }
    }

    /// <summary>删除指定截图，送入回收站（可撤销）；返回实际处理的文件数。</summary>
    public static int Delete(IEnumerable<FileInfo> files)
    {
        var paths = files.Where(f => f is not null && f.Exists).Select(f => f.FullName).ToArray();
        if (paths.Length == 0) return 0;
        Core.RecycleBin.Send(paths, out _);
        return paths.Length;
    }

    /// <summary>清空历史目录里的全部截图（只删 .png，不动目录本身）。</summary>
    public static int DeleteAll() => Delete(Recent(int.MaxValue));

    /// <summary>
    /// 超出保留上限时把最旧的截图送进回收站（而不是物理删除），误清理也能还原。
    /// 只在保存了新截图之后调用；启动程序不调用。
    /// </summary>
    public static int TrimExcess()
    {
        try
        {
            var all = Recent(int.MaxValue);
            var excess = all.Skip(KeepCount).Select(f => f.FullName).ToArray();
            if (excess.Length == 0) return 0;
            return Core.RecycleBin.Send(excess, out _) ? excess.Length : 0;
        }
        catch { return 0; }
    }
}
