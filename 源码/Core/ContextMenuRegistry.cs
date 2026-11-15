using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace Toolbox.Core;

/// <summary>右键菜单挂载点。SubKey 是 Classes 根下的相对路径。</summary>
internal sealed record MenuMount(string SubKey, string Label, bool Targeted);

/// <summary>一条 Windows 右键菜单项（shell verb）。</summary>
internal sealed class ContextMenuEntry
{
    public string KeyName { get; init; }
    public string Caption { get; init; }
    public string Command { get; init; }
    public string Icon { get; init; }
    public string MountKey { get; init; }
    public string MountLabel { get; init; }
    public bool UserScope { get; init; }
    public bool Enabled { get; init; }
    public bool Extended { get; init; }
    public bool IsOwn { get; init; }
    public bool IsBuiltIn { get; init; }

    public string ScopeText => UserScope ? "当前用户" : "本机";
    public string StatusText => Enabled ? "已启用" : "已禁用";
    public string KeyPath => ContextMenuRegistry.FullKeyPath(UserScope, MountKey, KeyName);
    public string RegistryPath => (UserScope ? @"HKEY_CURRENT_USER\Software\Classes\" : @"HKEY_LOCAL_MACHINE\SOFTWARE\Classes\")
                                  + MountKey + "\\" + KeyName;
    public string SearchText => $"{Caption} {Command} {MountLabel} {ScopeText} {KeyName}".ToLowerInvariant();
}

internal sealed class BackupValue
{
    public string Name { get; set; } = "";
    public int Kind { get; set; }
    public string Data { get; set; } = "";
}

internal sealed class BackupKey
{
    public string Name { get; set; } = "";
    public List<BackupValue> Values { get; set; } = new();
    public List<BackupKey> Children { get; set; } = new();
}

internal sealed class BackupRecord
{
    public DateTime Time { get; set; }
    public bool UserScope { get; set; }
    public string KeyPath { get; set; } = "";
    public string Caption { get; set; } = "";
    public BackupKey Root { get; set; }
}

/// <summary>
/// 右键菜单注册表读写。所有写操作集中在这里，UI 与命令行共用同一套实现（DRY）。
/// 本工具自己的菜单写入 HKCU\Software\Classes，因此安装/卸载不再需要管理员权限。
/// </summary>
internal static class ContextMenuRegistry
{
    public const string OwnKeyName = "Toolbox";

    /// <summary>Win11 一级菜单的 COM 组件 CLSID，必须与 ShellExt 项目里的一致。</summary>
    private const string ShellClsid = "C1A9B7E4-5D28-4F63-8B90-7A2D6E4C1F58";
    private const string ExplorerCommandValue = "ExplorerCommandHandler";
    private const string ShellExtFileName = "ToolboxShellNative.dll";
    // 文本里不要再带 ⚡ 之类的符号：菜单本身已经有 Icon，重复一次会变成「图标 + ⚡ + 文字」
    private const string OwnMainLabel = "快速卸载软件";
    private const string OwnTargetLabel = "卸载此软件";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int SHLoadIndirectString(string source, StringBuilder output,
        uint outputChars, IntPtr reserved);

    /// <summary>把 @%SystemRoot%\...\shell32.dll,-123 这类 MUI 资源串解析成可读文字。</summary>
    private static string ResolveCaption(string raw, string fallback)
    {
        if (string.IsNullOrWhiteSpace(raw)) return fallback;
        if (raw[0] != '@') return raw;
        try
        {
            var buffer = new StringBuilder(512);
            if (SHLoadIndirectString(raw, buffer, (uint)buffer.Capacity, IntPtr.Zero) == 0 &&
                buffer.Length > 0 && !buffer.ToString().StartsWith("@"))
                return buffer.ToString();
        }
        catch { /* 解析失败时回落到键名 */ }
        return fallback;
    }

    private static readonly string[] BuiltInVerbs =
    {
        "open", "print", "runas", "edit", "find", "properties", "explore", "cmd",
        "powershell", "opennewwindow", "pintohomescreen", "printto", "enqueue", "play",
    };

    /// <summary>扫描用的全部挂载点。</summary>
    public static readonly MenuMount[] Mounts =
    {
        new(@"*\shell", "所有文件", true),
        new(@"Directory\shell", "文件夹", true),
        new(@"Directory\Background\shell", "桌面 / 空白处", false),
        new(@"Drive\shell", "磁盘驱动器", true),
        new(@"Folder\shell", "任意文件夹", true),
        new(@"AllFilesystemObjects\shell", "文件与文件夹", true),
        new(@"lnkfile\shell", "快捷方式", true),
        new(@"exefile\shell", "可执行文件", true),
        new(@"CLSID\{20D04FE0-3AEA-1069-A2D8-08002B30309D}\shell", "此电脑", false),
    };

    /// <summary>本工具菜单安装的 5 个挂载点，与原脚本保持一致。</summary>
    private static readonly MenuMount[] OwnMounts =
    {
        new(@"Directory\Background\shell", "桌面 / 文件夹空白处", false),
        new(@"Directory\shell", "文件夹", true),
        new(@"CLSID\{20D04FE0-3AEA-1069-A2D8-08002B30309D}\shell", "此电脑", false),
        new(@"lnkfile\shell", "快捷方式 (.lnk)", true),
        new(@"exefile\shell", "可执行文件 (.exe)", true),
    };

    public static bool IsElevated
    {
        get
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }
    }

    private static string BackupFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Toolbox", "context-menu-backup.json");

    private static RegistryKey Root(bool user) => user ? Registry.CurrentUser : Registry.LocalMachine;
    private static string BasePath(bool user) => user ? @"Software\Classes" : @"SOFTWARE\Classes";
    public static string FullKeyPath(bool user, string mount, string name) =>
        BasePath(user) + "\\" + mount + "\\" + name;
    private static string ParentPath(bool user, string mount) => BasePath(user) + "\\" + mount;

    public static List<ContextMenuEntry> Scan()
    {
        var result = new List<ContextMenuEntry>();
        foreach (var mount in Mounts)
            foreach (bool user in new[] { true, false })
            {
                try
                {
                    using var parent = Root(user).OpenSubKey(ParentPath(user, mount.SubKey));
                    if (parent is null) continue;
                    foreach (string name in parent.GetSubKeyNames())
                    {
                        try
                        {
                            using var key = parent.OpenSubKey(name);
                            if (key is null) continue;
                            string caption = ResolveCaption(
                                FirstNonEmpty(Value(key, "MUIVerb"), Value(key, ""), name), name);
                            string command = "";
                            using (var cmd = key.OpenSubKey("command"))
                                if (cmd is not null) command = Value(cmd, "") ?? "";

                            result.Add(new ContextMenuEntry
                            {
                                KeyName = name,
                                Caption = caption,
                                Command = command,
                                Icon = Value(key, "Icon") ?? "",
                                MountKey = mount.SubKey,
                                MountLabel = mount.Label,
                                UserScope = user,
                                Enabled = key.GetValue("LegacyDisable") is null &&
                                          key.GetValue("ProgrammaticAccessOnly") is null,
                                Extended = key.GetValue("Extended") is not null,
                                IsOwn = name.Equals(OwnKeyName, StringComparison.OrdinalIgnoreCase),
                                IsBuiltIn = BuiltInVerbs.Contains(name, StringComparer.OrdinalIgnoreCase),
                            });
                        }
                        catch { /* 单个权限受限的键不影响整体扫描 */ }
                    }
                }
                catch { /* 挂载点不存在或不可读 */ }
            }
        return result.OrderBy(e => e.Caption, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static string Value(RegistryKey key, string name)
    {
        try { return key.GetValue(name) as string; }
        catch { return null; }
    }

    private static string FirstNonEmpty(params string[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "";

    /// <summary>启用/禁用：通过 LegacyDisable 值实现，随时可恢复，不删除任何数据。</summary>
    public static string SetEnabled(ContextMenuEntry entry, bool enabled)
    {
        if (!CanWrite(entry, out string reason)) return reason;
        try
        {
            using var key = Root(entry.UserScope).OpenSubKey(entry.KeyPath, true);
            if (key is null) return "注册表项已不存在，请刷新列表";
            if (enabled) key.DeleteValue("LegacyDisable", false);
            else key.SetValue("LegacyDisable", "", RegistryValueKind.String);
            return "";
        }
        catch (Exception ex) { return ex.Message; }
    }

    /// <summary>删除前把整棵子键导出到备份文件，可一键恢复。</summary>
    public static string Delete(ContextMenuEntry entry)
    {
        if (!CanWrite(entry, out string reason)) return reason;
        if (entry.IsBuiltIn) return "系统内置菜单项不允许删除，请使用「禁用」";
        try
        {
            using (var key = Root(entry.UserScope).OpenSubKey(entry.KeyPath))
            {
                if (key is null) return "注册表项已不存在，请刷新列表";
                AppendBackup(new BackupRecord
                {
                    Time = DateTime.Now,
                    UserScope = entry.UserScope,
                    KeyPath = entry.KeyPath,
                    Caption = entry.Caption,
                    Root = Export(key, entry.KeyName),
                });
            }
            Root(entry.UserScope).DeleteSubKeyTree(entry.KeyPath, false);
            return "";
        }
        catch (Exception ex) { return ex.Message; }
    }

    /// <summary>在当前用户的指定挂载点新增自定义菜单项，无需管理员权限。</summary>
    public static string AddCustom(string caption, string command, string mountSubKey, string icon)
    {
        if (string.IsNullOrWhiteSpace(caption)) return "请填写菜单名称";
        if (string.IsNullOrWhiteSpace(command)) return "请填写执行命令";
        string safe = "QuickTool_" + Guid.NewGuid().ToString("N").Substring(0, 8);
        string path = FullKeyPath(true, mountSubKey, safe);
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(path, true);
            key.SetValue("", caption, RegistryValueKind.String);
            key.SetValue("MUIVerb", caption, RegistryValueKind.String);
            if (!string.IsNullOrWhiteSpace(icon))
                key.SetValue("Icon", icon, RegistryValueKind.String);
            using var cmd = key.CreateSubKey("command", true);
            cmd.SetValue("", command, RegistryValueKind.String);
            return "";
        }
        catch (Exception ex) { return ex.Message; }
    }

    private static bool CanWrite(ContextMenuEntry entry, out string reason)
    {
        reason = "";
        if (!entry.UserScope && !IsElevated)
            reason = "修改「本机」范围的菜单需要管理员权限，请以管理员身份重启本工具";
        return reason.Length == 0;
    }

    // ---------- 本工具右键菜单（唯一实现，供 UI 与 --install-menu 共用） ----------

    /// <summary>
    /// CLSID 下的 shell 键（如「此电脑」）受系统保护，连管理员也写不进去，
    /// 这类挂载点不参与安装与计数，否则状态会永远显示「不完整」。
    /// </summary>
    private static bool IsWritable(MenuMount mount) =>
        !mount.SubKey.StartsWith("CLSID", StringComparison.OrdinalIgnoreCase);

    private static int WritableMountCount => OwnMounts.Count(IsWritable);

    /// <summary>
    /// 统计真正生效的挂载点。实测在部分系统上 HKCU\Software\Classes 里的 shell verb
    /// 不会被资源管理器采纳（只有 HKLM 的会显示），所以两处都要看。
    /// </summary>
    public static int OwnInstalledCount
    {
        get
        {
            int count = 0;
            foreach (var mount in OwnMounts)
            {
                if (!IsWritable(mount)) continue;
                bool found = false;
                foreach (bool user in new[] { false, true })
                {
                    try
                    {
                        using var key = Root(user).OpenSubKey(FullKeyPath(user, mount.SubKey, OwnKeyName));
                        if (key is not null) { found = true; break; }
                    }
                    catch { /* 忽略不可读项 */ }
                }
                if (found) count++;
            }
            return count;
        }
    }

    public static bool OwnFullyInstalled => OwnInstalledCount >= WritableMountCount;

    /// <summary>机器范围（HKLM）里是否已有菜单项——移除这类项需要管理员权限。</summary>
    public static bool HasMachineScope
    {
        get
        {
            foreach (var mount in OwnMounts)
            {
                if (!IsWritable(mount)) continue;
                try
                {
                    using var key = Registry.LocalMachine.OpenSubKey(FullKeyPath(false, mount.SubKey, OwnKeyName));
                    if (key is not null) return true;
                }
                catch { /* 读不到就当作没有 */ }
            }
            return false;
        }
    }

    /// <summary>
    /// 已装但不完整（例如旧版只写了文件夹项）时自动补齐，前提是有管理员权限。
    /// 返回是否执行了修复。
    /// </summary>
    public static bool TryRepairInstall()
    {
        int count = OwnInstalledCount;
        if (count == 0 || count >= OwnMounts.Length) return false;
        if (!IsElevated) return false;
        InstallOwn();
        return true;
    }

    public static string InstallOwn()
    {
        string exe = Environment.ProcessPath ?? "";
        if (!File.Exists(exe)) return "无法定位主程序路径";
        if (!IsElevated)
            return "需要管理员权限才能写入系统右键菜单，请用「以管理员身份重启」后再试";

        if (UseModernMenuItem) RegisterShellExtension(exe);
        else UnregisterShellExtension();   // 不用一级菜单时清掉残留的 CLSID 注册

        string icon = "\"" + exe + "\",0";
        int done = 0;
        var skipped = new List<string>();
        try
        {
            foreach (var mount in OwnMounts)
            {
                if (!IsWritable(mount)) continue;   // CLSID 下受系统保护，直接跳过
                try
                {
                    string label = mount.Targeted ? OwnTargetLabel : OwnMainLabel;
                    string command = "\"" + exe + "\"" + (mount.Targeted ? " --target \"%1\"" : "");
                    using var key = Registry.LocalMachine.CreateSubKey(FullKeyPath(false, mount.SubKey, OwnKeyName), true);

                    if (UseExplorerCommand(mount) && ShellExtReady)
                    {
                        // Win11 一级菜单：交给 IExplorerCommand 组件渲染。
                        // 实测照抄系统里其它一级扩展的写法——verb 下只留 ExplorerCommandHandler，
                        // 文字和图标由组件自己提供；一旦带上 MUIVerb/Icon，菜单会退回传统列表。
                        key.SetValue(ExplorerCommandValue, "{" + ShellClsid + "}", RegistryValueKind.String);
                        key.DeleteSubKeyTree("command", false);
                        key.DeleteValue("MUIVerb", false);
                        key.DeleteValue("Icon", false);
                        key.DeleteValue("", false);
                    }
                    else
                    {
                        key.SetValue("", label, RegistryValueKind.String);
                        key.SetValue("MUIVerb", label, RegistryValueKind.String);
                        key.SetValue("Icon", icon, RegistryValueKind.String);
                        key.DeleteValue(ExplorerCommandValue, false);
                        using var cmd = key.CreateSubKey("command", true);
                        cmd.SetValue("", command, RegistryValueKind.String);
                    }
                    done++;
                }
                catch
                {
                    //「此电脑」等 CLSID 下的 shell 键受系统保护，连管理员也写不进去，跳过即可
                    skipped.Add(mount.Label);
                }
            }
            RemoveLegacyScope();  // 清掉旧的 QuickUninstall 键名（改名前的残留）
            RemoveUserScope();   // 清掉当前用户下的同名残留，避免两处并存
            string note = skipped.Count == 0
                ? "（本机范围，exe / 快捷方式 / 文件夹 / 空白处）"
                : $"（已跳过 {string.Join("、", skipped)}：系统保护，无法写入）";
            return $"已安装 {done} 个挂载点{note}";
        }
        catch (Exception ex) { return "安装失败：" + ex.Message; }
        finally { NotifyShellChanged(); }
    }

    /// <summary>同目录是否存在 IExplorerCommand 组件；没有就退回传统菜单，功能不至于丢。</summary>
    private static bool ShellExtReady
    {
        get
        {
            string exe = Environment.ProcessPath ?? "";
            string dir = Path.GetDirectoryName(exe) ?? "";
            return dir.Length > 0 && File.Exists(Path.Combine(dir, ShellExtFileName));
        }
    }

    /// <summary>
    /// 是否使用 Win11 一级菜单（IExplorerCommand 组件）。
    /// 组件本身能创建（CoCreateInstance 成功、GetTitle 正常），但本机的资源管理器
    /// 始终不调用注册的 ExplorerCommandHandler，实测排除了中文路径、CRT 依赖、
    /// 线程模型、HKLM/HKCU 注册位置等因素。在找到原因前关闭，退回传统菜单，
    /// 保证功能可用（菜单在「显示更多选项」里）。
    /// </summary>
    // 用 readonly 而非 const：保留「一行切回一级菜单扩展」的开关，
    // 也不会让编译器把关闭的分支折叠成不可达代码（CS0162）。
    private static readonly bool UseModernMenuItem = false;

    private static bool UseExplorerCommand(MenuMount mount) =>
        UseModernMenuItem && mount.Targeted;

    private static void RegisterShellExtension(string exe)
    {
        string source = Path.Combine(Path.GetDirectoryName(exe) ?? "", ShellExtFileName);
        if (!File.Exists(source)) return;

        // 复制到纯英文路径再注册：避免中文路径带来的加载问题，也让组件不跟着 exe 移动而失效
        string target = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Toolbox", "Shell", ShellExtFileName);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target) ?? "");
            if (!File.Exists(target) || !FilesEqual(source, target))
            {
                try { File.Copy(source, target, true); }
                catch { /* 被资源管理器占用时保留旧文件 */ }
            }
        }
        catch { return; }

        if (!File.Exists(target)) return;

        try
        {
            using var key = Registry.LocalMachine.CreateSubKey(@"Software\Classes\CLSID\{" + ShellClsid + "}", true);
            if (key is null) return;
            key.SetValue("", "Toolbox Uninstall Command", RegistryValueKind.String);
            // 照抄系统里能正常显示的同类扩展（小米互传 / 百度网盘）的注册结构
            key.SetValue("Version", "1.0", RegistryValueKind.String);
            using (key.CreateSubKey("Programmable", true)) { }
            using var inproc = key.CreateSubKey("InprocServer32", true);
            if (inproc is null) return;
            inproc.SetValue("", target, RegistryValueKind.String);
            inproc.SetValue("ThreadingModel", "Apartment", RegistryValueKind.String);
        }
        catch { /* 注册失败时 UseExplorerCommand 分支会自然退回传统菜单 */ }
    }

    private static bool FilesEqual(string a, string b)
    {
        try
        {
            var fa = new FileInfo(a);
            var fb = new FileInfo(b);
            return fa.Length == fb.Length && fa.LastWriteTimeUtc == fb.LastWriteTimeUtc;
        }
        catch { return false; }
    }

    private static void UnregisterShellExtension()
    {
        try { Registry.LocalMachine.DeleteSubKeyTree(@"Software\Classes\CLSID\{" + ShellClsid + "}", false); }
        catch { /* 无权限或本来就没有 */ }
    }

    /// <summary>清掉 HKCU 下的旧注册（旧版本写过，或某些系统上无效）。</summary>
    private static void RemoveUserScope()    {
        foreach (var mount in OwnMounts)
        {
            string path = FullKeyPath(true, mount.SubKey, OwnKeyName);
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(path);
                if (key is null) continue;
                key.Dispose();
                Registry.CurrentUser.DeleteSubKeyTree(path, false);
            }
            catch { /* 删不掉就留着，不影响 HKLM 生效 */ }
        }
    }

    /// <summary>产品改名前的键名，安装时顺手清掉，避免右键菜单里出现两个同功能项。</summary>
    private const string LegacyKeyName = "QuickUninstall";

    private static void RemoveLegacyScope()
    {
        foreach (var mount in OwnMounts)
            foreach (bool user in new[] { false, true })
            {
                string path = FullKeyPath(user, mount.SubKey, LegacyKeyName);
                try
                {
                    using var key = Root(user).OpenSubKey(path);
                    if (key is null) continue;
                    key.Dispose();
                    Root(user).DeleteSubKeyTree(path, false);
                }
                catch { /* 无权限时跳过，用户下次提权安装会再清一次 */ }
            }
    }

    /// <summary>
    /// 通知资源管理器「文件关联变了」。少了这一步，注册表写进去后
    /// 右键菜单往往仍是旧的缓存，表现为「装了但看不见」。
    /// </summary>
    private static void NotifyShellChanged()
    {
        try { SHChangeNotify(ShcneAssocChanged, ShcnfIdList, IntPtr.Zero, IntPtr.Zero); }
        catch { /* 通知失败不影响注册表本身 */ }
    }

    private const int ShcneAssocChanged = 0x08000000;
    private const uint ShcnfIdList = 0x0000;

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);

    public static string UninstallOwn()
    {
        int removed = 0, denied = 0;
        foreach (var mount in OwnMounts)
            foreach (bool user in new[] { true, false })
            {
                string path = FullKeyPath(user, mount.SubKey, OwnKeyName);
                try
                {
                    using var key = Root(user).OpenSubKey(path);
                    if (key is null) continue;
                    key.Dispose();
                    Root(user).DeleteSubKeyTree(path, false);
                    removed++;
                }
                catch { denied++; /* 本机范围无权限时跳过，计数以便上层提示 */ }
            }
        UnregisterShellExtension();
        NotifyShellChanged();
        if (denied > 0 && !IsElevated)
            return $"已移除 {removed} 个，还有 {denied} 个需要管理员权限";
        return $"已移除 {removed} 个挂载点";
    }

    /// <summary>在注册表编辑器中定位该项。</summary>
    public static void Locate(ContextMenuEntry entry)
    {
        try
        {
            using var regedit = Registry.CurrentUser.CreateSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Applets\Regedit", true);
            regedit?.SetValue("LastKey", entry.RegistryPath, RegistryValueKind.String);
            System.Diagnostics.Process.Start("regedit.exe");
        }
        catch { /* 定位失败不影响主流程 */ }
    }

    // ---------- 备份 ----------

    public static List<BackupRecord> ReadBackups()
    {
        try
        {
            if (!File.Exists(BackupFile)) return new List<BackupRecord>();
            return JsonSerializer.Deserialize<List<BackupRecord>>(File.ReadAllText(BackupFile), JsonOptions)
                   ?? new List<BackupRecord>();
        }
        catch { return new List<BackupRecord>(); }
    }

    public static string RestoreLast()
    {
        var records = ReadBackups();
        if (records.Count == 0) return "没有可恢复的备份";
        var record = records[^1];
        try
        {
            int cut = record.KeyPath.LastIndexOf('\\');
            if (cut <= 0) return "备份路径无效";
            string parent = record.KeyPath.Substring(0, cut);
            using var parentKey = Root(record.UserScope).CreateSubKey(parent, true);
            Import(parentKey, record.Root);
            records.RemoveAt(records.Count - 1);
            SaveBackups(records);
            return $"已恢复「{record.Caption}」";
        }
        catch (Exception ex) { return "恢复失败：" + ex.Message; }
    }

    /// <summary>删除指定名称的备份记录，供自检清理或用户清空单条记录使用。</summary>
    public static int RemoveBackups(string caption)
    {
        var records = ReadBackups();
        int removed = records.RemoveAll(r => string.Equals(r.Caption, caption, StringComparison.Ordinal));
        if (removed > 0) SaveBackups(records);
        return removed;
    }

    private static void AppendBackup(BackupRecord record)
    {
        var list = ReadBackups();
        list.Add(record);
        if (list.Count > 50) list = list.Skip(list.Count - 50).ToList();
        SaveBackups(list);
    }

    private static void SaveBackups(List<BackupRecord> records)
    {
        string file = BackupFile;
        Directory.CreateDirectory(Path.GetDirectoryName(file));
        File.WriteAllText(file, JsonSerializer.Serialize(records, JsonOptions));
    }

    private static BackupKey Export(RegistryKey key, string name)
    {
        var node = new BackupKey { Name = name };
        foreach (string valueName in key.GetValueNames())
        {
            try
            {
                var kind = key.GetValueKind(valueName);
                object raw = key.GetValue(valueName);
                node.Values.Add(new BackupValue
                {
                    Name = valueName, Kind = (int)kind, Data = Encode(raw, kind),
                });
            }
            catch { /* 跳过无法读取的值 */ }
        }
        foreach (string child in key.GetSubKeyNames())
        {
            try
            {
                using var sub = key.OpenSubKey(child);
                if (sub is not null) node.Children.Add(Export(sub, child));
            }
            catch { /* 跳过不可读子键 */ }
        }
        return node;
    }

    private static void Import(RegistryKey parent, BackupKey node)
    {
        using var key = parent.CreateSubKey(node.Name, true);
        foreach (var value in node.Values)
        {
            try { key.SetValue(value.Name, Decode(value), (RegistryValueKind)value.Kind); }
            catch { /* 跳过无法写入的值 */ }
        }
        foreach (var child in node.Children) Import(key, child);
    }

    private static string Encode(object raw, RegistryValueKind kind) => kind switch
    {
        RegistryValueKind.Binary => Convert.ToBase64String(raw as byte[] ?? Array.Empty<byte>()),
        RegistryValueKind.MultiString => string.Join("\u0000", raw as string[] ?? Array.Empty<string>()),
        _ => Convert.ToString(raw, CultureInfo.InvariantCulture) ?? "",
    };

    private static object Decode(BackupValue value) => ((RegistryValueKind)value.Kind) switch
    {
        RegistryValueKind.DWord => int.TryParse(value.Data, out int i) ? i : 0,
        RegistryValueKind.QWord => long.TryParse(value.Data, out long l) ? l : 0L,
        RegistryValueKind.Binary => Convert.FromBase64String(value.Data),
        RegistryValueKind.MultiString => value.Data.Split('\u0000'),
        _ => value.Data,
    };
}
