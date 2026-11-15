namespace Toolbox.Core;

/// <summary>把注册表里形形色色的卸载字符串拆成「可执行文件 + 参数」。</summary>
internal static class CommandLine
{
    internal sealed record Command(string FileName, string Arguments);

    public static Command Parse(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return new Command("", "");
        string s = Environment.ExpandEnvironmentVariables(raw.Trim());

        if (s[0] == '"')
        {
            int end = s.IndexOf('"', 1);
            if (end < 0) return new Command(s.Trim('"'), "");
            return new Command(s.Substring(1, end - 1).Trim(), s.Substring(end + 1).Trim());
        }

        // 未加引号的路径可能自带空格：向后拼接，直到得到一个真实存在的 exe
        int probe = 0;
        int boundary = -1;
        while (probe < s.Length)
        {
            int next = s.IndexOf(' ', probe);
            if (next < 0) next = s.Length;
            string token = s.Substring(probe, next - probe);
            if (token.StartsWith('/') || token.StartsWith('-')) break;
            string candidate = s.Substring(0, next);
            if (candidate.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(candidate))
            {
                boundary = next;
                break;
            }
            probe = next + 1;
        }

        if (boundary >= 0) return new Command(s.Substring(0, boundary).Trim(), s.Substring(boundary).Trim());

        int firstSpace = s.IndexOf(' ');
        if (firstSpace < 0) return new Command(s, "");
        return new Command(s.Substring(0, firstSpace).Trim(), s.Substring(firstSpace).Trim());
    }

    public static string ExecutableName(string fileName) =>
        string.IsNullOrWhiteSpace(fileName) ? "" : Path.GetFileNameWithoutExtension(fileName.Trim('"'));
}
