using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Toolbox.Core;

/// <summary>从注册表的四个卸载清单里汇总已安装软件。</summary>
internal static class RegistryScanner
{
    private const string Uninstall = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    private const string Uninstall32 = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall";

    private static readonly (RegistryHive Hive, string Path, string Source)[] Roots =
    {
        (RegistryHive.LocalMachine, Uninstall, "本机"),
        (RegistryHive.LocalMachine, Uninstall32, "本机 · 32位"),
        (RegistryHive.CurrentUser, Uninstall, "当前用户"),
        (RegistryHive.CurrentUser, Uninstall32, "当前用户 · 32位"),
    };

    private static readonly Regex UpdateName = new(@"^KB\d{6}", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static List<SoftwareEntry> Scan(bool includeSystemComponents = false)
    {
        var result = new List<SoftwareEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in Roots)
        {
            using var baseKey = SafeOpenBase(root.Hive);
            if (baseKey is null) continue;
            using var parent = baseKey.OpenSubKey(root.Path, false);
            if (parent is null) continue;

            foreach (string keyName in parent.GetSubKeyNames())
            {
                using var key = parent.OpenSubKey(keyName, false);
                if (key is null) continue;

                string name = Str(key, "DisplayName");
                string normal = Str(key, "UninstallString");
                string quiet = Str(key, "QuietUninstallString");
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (string.IsNullOrWhiteSpace(normal) && string.IsNullOrWhiteSpace(quiet)) continue;

                bool system = Int(key, "SystemComponent") == 1;
                if (system && !includeSystemComponents) continue;
                if (!string.IsNullOrEmpty(Str(key, "ParentKeyName")) ||
                    !string.IsNullOrEmpty(Str(key, "ParentDisplayName"))) continue;

                string release = Str(key, "ReleaseType");
                if (release is "Security Update" or "Update" or "Hotfix") continue;
                if (UpdateName.IsMatch(name)) continue;

                string publisher = Str(key, "Publisher");
                string version = Str(key, "DisplayVersion");
                string guid = keyName.StartsWith('{') ? keyName : "";
                string dedupeKey = !string.IsNullOrEmpty(guid)
                    ? "guid:" + guid
                    : $"name:{name}|{publisher}|{version}";
                if (!seen.Add(dedupeKey)) continue;

                string date = Str(key, "InstallDate");
                if (date.Length == 8 && DateTime.TryParseExact(date, "yyyyMMdd", null,
                        System.Globalization.DateTimeStyles.None, out var parsed))
                    date = parsed.ToString("yyyy-MM-dd");

                result.Add(new SoftwareEntry
                {
                    Name = name.Trim(),
                    Publisher = string.IsNullOrWhiteSpace(publisher) ? "未知发布者" : publisher.Trim(),
                    Version = string.IsNullOrWhiteSpace(version) ? "—" : version.Trim(),
                    InstallLocation = Str(key, "InstallLocation").Trim().Trim('"'),
                    DisplayIcon = Str(key, "DisplayIcon").Trim(),
                    UninstallString = normal.Trim(),
                    QuietUninstallString = quiet.Trim(),
                    InstallDate = string.IsNullOrEmpty(date) ? "—" : date,
                    SizeKB = LongVal(key, "EstimatedSize"),
                    Hive = root.Hive,
                    SubKeyPath = root.Path + "\\" + keyName,
                    KeyName = keyName,
                    Source = root.Source,
                    IsSystemComponent = system,
                });
            }
        }

        return result.OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public static bool KeyExists(SoftwareEntry entry) => KeyExists(entry.Hive, entry.SubKeyPath);

    public static bool KeyExists(RegistryHive hive, string subKeyPath)
    {
        using var baseKey = SafeOpenBase(hive);
        if (baseKey is null) return false;
        using var key = baseKey.OpenSubKey(subKeyPath, false);
        return key is not null;
    }

    public static bool DeleteKey(SoftwareEntry entry, out string error)
    {
        error = "";
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(entry.Hive, RegistryView.Default);
            baseKey.DeleteSubKeyTree(entry.SubKeyPath, false);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static RegistryKey SafeOpenBase(RegistryHive hive)
    {
        try { return RegistryKey.OpenBaseKey(hive, RegistryView.Default); }
        catch { return null; }
    }

    private static string Str(RegistryKey key, string name)
    {
        try { return key.GetValue(name) as string ?? ""; }
        catch { return ""; }
    }

    private static int Int(RegistryKey key, string name)
    {
        try { return Convert.ToInt32(key.GetValue(name) ?? 0); }
        catch { return 0; }
    }

    private static long LongVal(RegistryKey key, string name)
    {
        try { return Convert.ToInt64(key.GetValue(name) ?? 0); }
        catch { return 0; }
    }
}
