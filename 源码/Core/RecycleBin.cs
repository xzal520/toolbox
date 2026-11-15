using System.Runtime.InteropServices;

namespace Toolbox.Core;

/// <summary>
/// 统一的「移入回收站」实现：文件与目录都走这里，保证可撤销、不直接物理删除。
/// 卸载残留清理与截图删除共用同一套 P/Invoke，避免两处重复。
/// </summary>
internal static class RecycleBin
{
    /// <summary>把一批路径送到回收站；空列表视为成功。</summary>
    public static bool Send(IEnumerable<string> paths, out string error)
    {
        error = "";
        var items = paths.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct().ToArray();
        if (items.Length == 0) return true;

        var operation = new ShFileOp
        {
            Func = FileOperationType.Delete,
            From = string.Join("\0", items) + "\0\0",
            Flags = FileOperationFlags.AllowUndo | FileOperationFlags.NoConfirmation |
                    FileOperationFlags.Silent | FileOperationFlags.NoErrorUi,
        };
        int code = SHFileOperation(ref operation);
        if (code != 0 || operation.Aborted) error = $"系统返回 {code}";
        return error.Length == 0;
    }

    public static bool Send(string path, out string error) => Send(new[] { path }, out error);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHFileOperationW")]
    private static extern int SHFileOperation(ref ShFileOp fileOp);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileOp
    {
        public IntPtr Hwnd;
        public FileOperationType Func;
        [MarshalAs(UnmanagedType.LPWStr)] public string From;
        [MarshalAs(UnmanagedType.LPWStr)] public string To;
        public FileOperationFlags Flags;
        [MarshalAs(UnmanagedType.Bool)] public bool Aborted;
        public IntPtr NameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string ProgressTitle;
    }

    [Flags]
    private enum FileOperationFlags : ushort
    {
        Silent = 0x0004,
        NoConfirmation = 0x0010,
        AllowUndo = 0x0040,
        NoErrorUi = 0x0400,
    }

    private enum FileOperationType : uint
    {
        Delete = 0x0003,
    }
}
