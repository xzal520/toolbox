using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Toolbox.Core;

/// <summary>构造卸载命令、执行卸载、校验结果并清理残留。</summary>
internal static class UninstallEngine
{
    private static readonly Regex InnoUninstaller = new(@"^unins\d*\.exe$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static UninstallPlan BuildPlan(SoftwareEntry entry, UninstallMode mode)
    {
        bool requestSilent = mode == UninstallMode.Silent;
        bool hasQuiet = !string.IsNullOrWhiteSpace(entry.QuietUninstallString);
        bool useQuietString = requestSilent && hasQuiet;

        string raw = useQuietString
            ? entry.QuietUninstallString
            : (string.IsNullOrWhiteSpace(entry.UninstallString) ? entry.QuietUninstallString : entry.UninstallString);

        var cmd = CommandLine.Parse(raw);
        string file = cmd.FileName;
        string args = cmd.Arguments;
        string exe = Path.GetFileName(file);
        UninstallKind kind = DetectKind(file, args, exe);
        string note = "";

        if (useQuietString)
        {
            note = "使用软件自带的静默卸载命令";
        }
        else if (kind == UninstallKind.Msi)
        {
            args = Regex.Replace(args, @"/I(\{[0-9A-Fa-f\-]+\})", "/X$1", RegexOptions.IgnoreCase);
            if (!Regex.IsMatch(args, @"/[Xx](\{|\s)", RegexOptions.IgnoreCase) && entry.KeyName.StartsWith('{'))
                args = ($"/X{entry.KeyName} " + args).Trim();
            args = Append(args, requestSilent ? "/quiet" : "/passive");
            args = Append(args, "/norestart");
        }
        else if (requestSilent)
        {
            switch (kind)
            {
                case UninstallKind.InnoSetup when !HasQuietArgs(args):
                    args = Append(Append(Append(args, "/VERYSILENT"), "/SUPPRESSMSGBOXES"), "/NORESTART");
                    break;
                case UninstallKind.Nsis when !HasQuietArgs(args):
                    args = Append(args, "/S");
                    break;
                case UninstallKind.Generic:
                    note = "该软件未提供静默参数，将打开其卸载向导，请在向导中确认";
                    break;
            }
        }

        bool silent = requestSilent && kind != UninstallKind.Generic;
        return new UninstallPlan
        {
            FileName = file,
            Arguments = args,
            Kind = kind,
            IsSilent = silent,
            Note = note,
        };
    }

    private static UninstallKind DetectKind(string file, string args, string exe)
    {
        if (CommandLine.ExecutableName(file).Equals("msiexec", StringComparison.OrdinalIgnoreCase))
            return UninstallKind.Msi;
        if (InnoUninstaller.IsMatch(exe)) return UninstallKind.InnoSetup;
        if (exe.Contains("uninst", StringComparison.OrdinalIgnoreCase) ||
            exe.Contains("uninstall", StringComparison.OrdinalIgnoreCase) ||
            args.Contains("/allusers", StringComparison.OrdinalIgnoreCase))
            return UninstallKind.Nsis;
        return UninstallKind.Generic;
    }

    /// <summary>启动卸载程序，并在后台轮询注册表判断条目是否已消失。</summary>
    public static async Task<UninstallResult> ExecuteAsync(
        SoftwareEntry entry, UninstallPlan plan, TimeSpan timeout, IProgress<string> progress)
    {
        if (string.IsNullOrWhiteSpace(plan.FileName))
            return new UninstallResult { Error = "没有可用的卸载命令" };

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = plan.FileName,
                Arguments = plan.Arguments,
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(plan.FileName) is { Length: > 0 } dir && Directory.Exists(dir)
                    ? dir : Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            };
            // 静默卸载通常需要管理员权限，交互式卸载则由卸载程序自行申请 UAC
            if (plan.IsSilent || plan.Kind == UninstallKind.Msi) psi.Verb = "runas";

            using var process = Process.Start(psi);
            if (process is null) return new UninstallResult { Error = "无法启动卸载程序" };
            progress.Report($"已启动 {Path.GetFileName(plan.FileName)}，等待卸载完成…");
        }
        catch (Exception ex)
        {
            return new UninstallResult { Error = ex.Message };
        }

        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (!RegistryScanner.KeyExists(entry))
            {
                progress.Report("卸载完成，注册表条目已移除");
                return await Task.FromResult(Finish(entry, true));
            }
            await Task.Delay(900);
        }

        progress.Report("已超时：卸载向导可能仍在等待人工确认");
        return Finish(entry, false);

        static UninstallResult Finish(SoftwareEntry e, bool removed)
        {
            var residue = Inspect(e);
            return new UninstallResult
            {
                Started = true,
                Removed = removed,
                ResidueKey = residue.HasRegistryKey ? e.SubKeyPath : "",
                ResidueFolder = residue.HasFolder ? residue.Folder : "",
            };
        }
    }

    public static ResidueInfo Inspect(SoftwareEntry entry)
    {
        string folder = entry.InstallLocation;
        bool hasFolder = !string.IsNullOrWhiteSpace(folder) &&
                         folder.Length > 3 &&
                         Directory.Exists(folder) &&
                         !IsProtectedPath(folder);
        return new ResidueInfo
        {
            Entry = entry,
            HasRegistryKey = RegistryScanner.KeyExists(entry),
            HasFolder = hasFolder,
            Folder = hasFolder ? folder : "",
        };
    }

    /// <summary>只清理用户明确勾选的残留项，文件夹一律送入回收站而非直接删除。</summary>
    public static string Cleanup(SoftwareEntry entry, bool removeKey, bool removeFolder)
    {
        var messages = new List<string>();
        if (removeKey)
        {
            bool ok = RegistryScanner.DeleteKey(entry, out string error);
            messages.Add(ok ? "已删除残留注册表项" : $"注册表项删除失败：{error}");
        }
        if (removeFolder && !string.IsNullOrWhiteSpace(entry.InstallLocation))
        {
            bool ok = RecycleBin.Send(entry.InstallLocation, out string error);
            messages.Add(ok ? "残留目录已移入回收站" : $"目录清理失败：{error}");
        }
        return string.Join("；", messages);
    }

    private static bool IsProtectedPath(string path)
    {
        string full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        var protectedRoots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };
        return protectedRoots.Any(r => !string.IsNullOrEmpty(r) &&
            full.Equals(r.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase));
    }

    private static readonly Regex QuietFlag = new(@"(?:\s|^)/(?:s|verysilent|silent|quiet)(?:\s|$)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static bool HasQuietArgs(string args) => QuietFlag.IsMatch(args ?? "");

    private static string Append(string args, string flag) =>
        args.Contains(flag, StringComparison.OrdinalIgnoreCase) ? args : (args + " " + flag).Trim();
}
