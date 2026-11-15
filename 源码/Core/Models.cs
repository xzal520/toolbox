using Microsoft.Win32;

namespace Toolbox.Core;

/// <summary>一条已安装软件记录，对应注册表中某个 Uninstall 子键。</summary>
internal sealed class SoftwareEntry
{
    public string Name { get; init; }
    public string Publisher { get; init; }
    public string Version { get; init; }
    public string InstallLocation { get; init; }
    public string DisplayIcon { get; init; }
    public string UninstallString { get; init; }
    public string QuietUninstallString { get; init; }
    public string InstallDate { get; init; }
    public long SizeKB { get; init; }
    public RegistryHive Hive { get; init; }
    public string SubKeyPath { get; init; }
    public string Source { get; init; }
    public bool IsSystemComponent { get; init; }
    public string KeyName { get; init; }

    public string SizeText => SizeKB <= 0
        ? "—"
        : SizeKB >= 1024 * 1024 ? $"{SizeKB / 1024f / 1024f:0.##} GB"
        : SizeKB >= 1024 ? $"{SizeKB / 1024f:0.#} MB"
        : $"{SizeKB} KB";

    public string SearchText => $"{Name} {Publisher} {Version} {InstallLocation}".ToLowerInvariant();

    /// <summary>用于列表首字母色块的稳定色相。</summary>
    public int Hue => Math.Abs((Name ?? "").GetHashCode()) % 360;
}

internal enum UninstallKind
{
    Msi,
    Nsis,
    InnoSetup,
    Generic
}

internal enum UninstallMode
{
    Interactive,
    Silent
}

internal sealed class UninstallPlan
{
    public string FileName { get; init; }
    public string Arguments { get; init; }
    public UninstallKind Kind { get; init; }
    public bool IsSilent { get; init; }
    public string Note { get; init; }

    public string FullCommand => string.IsNullOrWhiteSpace(Arguments)
        ? $"\"{FileName}\""
        : $"\"{FileName}\" {Arguments}";

    public string KindText => Kind switch
    {
        UninstallKind.Msi => "MSI",
        UninstallKind.Nsis => "NSIS",
        UninstallKind.InnoSetup => "Inno",
        _ => "通用"
    };
}

internal sealed class UninstallResult
{
    public bool Started { get; init; }
    public bool Removed { get; init; }
    public string Error { get; init; }
    public string ResidueKey { get; init; }
    public string ResidueFolder { get; init; }
}

internal sealed class ResidueInfo
{
    public SoftwareEntry Entry { get; init; }
    public bool HasRegistryKey { get; init; }
    public bool HasFolder { get; init; }
    public string Folder { get; init; }
    public bool Any => HasRegistryKey || HasFolder;
}
