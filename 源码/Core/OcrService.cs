using System.Text;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace Toolbox.Core;

/// <summary>一个识别词及其在原图里的矩形；不依赖 System.Drawing，由 UI 层换算到画布坐标。</summary>
internal readonly record struct OcrWordBox(string Text, int X, int Y, int Width, int Height);

/// <summary>一次识别的结果：按行整理的文本，外加每个词的坐标供图片上拖选。</summary>
internal sealed record OcrText(string Text, string Language, int LineCount, IReadOnlyList<OcrWordBox> Words);

/// <summary>
/// Windows 系统内置 OCR（Windows.Media.Ocr）的薄封装：离线、无需密钥、随系统语言包。
/// 引擎只创建一次；所有 .NET 等待都 ConfigureAwait(false)，命令行自检同步等待也不会死锁。
/// </summary>
internal static class OcrService
{
    private static readonly object Gate = new();
    private static OcrEngine _engine;
    private static bool _initialized;
    private static string _unavailableReason = "";

    public static bool IsAvailable { get { EnsureEngine(); return _engine is not null; } }
    public static string LanguageName { get { EnsureEngine(); return _engine?.RecognizerLanguage.DisplayName ?? ""; } }
    public static string UnavailableReason { get { EnsureEngine(); return _unavailableReason; } }

    /// <summary>优先简体中文（同时能读英文数字），再退回用户语言与系统里任意可用语言。</summary>
    private static void EnsureEngine()
    {
        lock (Gate)
        {
            if (_initialized) return;
            _initialized = true;
            try
            {
                foreach (string tag in new[] { "zh-Hans-CN", "zh-CN", "zh-Hant-TW", "en-US" })
                {
                    var language = TryLanguage(tag);
                    if (language is null || !OcrEngine.IsLanguageSupported(language)) continue;
                    _engine = OcrEngine.TryCreateFromLanguage(language);
                    if (_engine is not null) return;
                }
                _engine = OcrEngine.TryCreateFromUserProfileLanguages();
                if (_engine is not null) return;
                if (OcrEngine.AvailableRecognizerLanguages.Count > 0)
                    _engine = OcrEngine.TryCreateFromLanguage(OcrEngine.AvailableRecognizerLanguages[0]);
                if (_engine is null)
                    _unavailableReason = "系统未安装 OCR 语言包。可在「Windows 设置 → 时间和语言 → 语言和区域」里添加中文语言包后重试";
            }
            catch (Exception ex)
            {
                _unavailableReason = "OCR 初始化失败：" + ex.Message;
            }
        }
    }

    private static Language TryLanguage(string tag)
    {
        try { return new Language(tag); }
        catch { return null; }
    }

    /// <summary>识别磁盘上的图片文件。</summary>
    public static async Task<OcrText> RecognizeAsync(string path, CancellationToken token = default)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("图片文件不存在", path);
        byte[] bytes = await File.ReadAllBytesAsync(path, token).ConfigureAwait(false);
        return await RecognizeBytesAsync(bytes, token).ConfigureAwait(false);
    }

    /// <summary>
    /// 识别内存中的 PNG（框选裁剪出的选区也走这里，与整图共用同一条识别链路，不落临时文件）。
    /// 用内存流解码而不是 StorageFile.GetFileFromPathAsync：桌面程序不经应用容器授权，
    /// 路径访问偶尔会被 WinRT 拒绝。
    /// </summary>
    public static async Task<OcrText> RecognizeBytesAsync(byte[] bytes, CancellationToken token = default)
    {
        EnsureEngine();
        if (_engine is null)
            throw new InvalidOperationException(string.IsNullOrEmpty(_unavailableReason)
                ? "系统未提供可用的 OCR 引擎" : _unavailableReason);
        token.ThrowIfCancellationRequested();

        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream))
        {
            writer.WriteBytes(bytes);
            await writer.StoreAsync().AsTask().ConfigureAwait(false);
            await writer.FlushAsync().AsTask().ConfigureAwait(false);
            writer.DetachStream();   // 先脱离，Dispose 时才不会把流一起关掉
        }
        stream.Seek(0);

        // WinRT 的 IAsyncOperation 要先 AsTask 才能 ConfigureAwait
        var decoder = await BitmapDecoder.CreateAsync(stream).AsTask().ConfigureAwait(false);
        using var bitmap = await decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, BuildTransform(decoder),
            ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage)
            .AsTask().ConfigureAwait(false);
        token.ThrowIfCancellationRequested();

        var result = await _engine.RecognizeAsync(bitmap).AsTask().ConfigureAwait(false);
        token.ThrowIfCancellationRequested();

        // Windows OCR 把每个汉字当成一个 word、用空格拼成 Text，直接复制会是「活 动 日 志」。
        // 按行取文本并清理 CJK 之间多余空格，保留英文单词间的空格。
        // 同时收集每个词的矩形：识别图可能被 BuildTransform 缩小过，要按比例映射回原图坐标。
        var lines = new List<string>();
        var words = new List<OcrWordBox>();
        double scaleX = (double)decoder.PixelWidth / bitmap.PixelWidth;
        double scaleY = (double)decoder.PixelHeight / bitmap.PixelHeight;
        foreach (var line in result.Lines)
        {
            string text = CleanCjkSpacing(line.Text);
            if (text.Length > 0) lines.Add(text);
            foreach (var word in line.Words)
            {
                var r = word.BoundingRect;
                words.Add(new OcrWordBox(word.Text,
                    (int)(r.X * scaleX), (int)(r.Y * scaleY),
                    Math.Max(1, (int)(r.Width * scaleX)), Math.Max(1, (int)(r.Height * scaleY))));
            }
        }
        return new OcrText(string.Join(Environment.NewLine, lines),
            _engine.RecognizerLanguage.DisplayName, lines.Count, words);
    }

    /// <summary>超过引擎上限时等比缩小，避免 8K 长图直接抛异常。</summary>
    private static BitmapTransform BuildTransform(BitmapDecoder decoder)
    {
        var transform = new BitmapTransform();
        uint max = OcrEngine.MaxImageDimension > 0 ? OcrEngine.MaxImageDimension : 10000u;
        if (decoder.PixelWidth <= max && decoder.PixelHeight <= max) return transform;
        double ratio = Math.Min((double)max / decoder.PixelWidth, (double)max / decoder.PixelHeight);
        transform.ScaledWidth = Math.Max(1u, (uint)(decoder.PixelWidth * ratio));
        transform.ScaledHeight = Math.Max(1u, (uint)(decoder.PixelHeight * ratio));
        transform.InterpolationMode = BitmapInterpolationMode.Cubic;
        return transform;
    }

    /// <summary>把拖选到的若干词拼成可复制文本，复用与整行相同的 CJK 空格规则。</summary>
    public static string JoinWords(IEnumerable<string> words) =>
        CleanCjkSpacing(string.Join(" ", words));

    private static bool IsCjk(char c) =>
        (c >= '\u2E80' && c <= '\u9FFF') ||
        (c >= '\uF900' && c <= '\uFAFF') ||
        (c >= '\u3000' && c <= '\u303F') ||
        (c >= '\uFF00' && c <= '\uFFEF') ||
        c == '·' || c == '—' || c == '…' || c == '￥';

    /// <summary>折叠连续空白；两侧都是中日韩字符或全角标点时把空格去掉。</summary>
    private static string CleanCjkSpacing(string text)
    {
        var sb = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c != ' ' && c != '\t') { sb.Append(c); continue; }
            int j = i;
            while (j < text.Length && (text[j] == ' ' || text[j] == '\t')) j++;
            char before = sb.Length > 0 ? sb[sb.Length - 1] : '\0';
            char after = j < text.Length ? text[j] : '\0';
            if (!(before != '\0' && after != '\0' && IsCjk(before) && IsCjk(after))) sb.Append(' ');
            i = j - 1;
        }
        return sb.ToString().Trim();
    }
}
