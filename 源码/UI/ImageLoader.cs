using System.Drawing;
using System.Windows.Forms;

namespace Toolbox.UI;

/// <summary>
/// 先把文件读进内存再解码：Image.FromFile 会一直占着文件句柄，
/// 导致「删除 / 清空历史」时文件被锁；共享读也允许边预览边删。
/// </summary>
internal static class ImageDecoder
{
    public static Bitmap Load(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            memory.Position = 0;
            using var loaded = Image.FromStream(memory);
            return new Bitmap(loaded);
        }
        catch { return null; }   // 图片损坏时保持空白预览
    }
}

/// <summary>
/// 后台解码 + 版本号去重：快速翻图时丢弃过期结果，绝不把旧图贴到新选中项上。
/// 预览区与大图查看器共用这一份实现，避免两处各写一遍异步解码。
/// </summary>
internal sealed class AsyncImageLoader
{
    private int _version;

    public void Load(Control owner, string path, Action<Bitmap> apply)
    {
        int version = ++_version;
        Task.Run(() =>
        {
            var bitmap = ImageDecoder.Load(path);
            if (owner.IsDisposed) { bitmap?.Dispose(); return; }
            try
            {
                owner.BeginInvoke(new Action(() =>
                {
                    if (version != _version || owner.IsDisposed) { bitmap?.Dispose(); return; }
                    apply(bitmap);
                }));
            }
            catch { bitmap?.Dispose(); }   // 窗口在回调前已销毁
        });
    }

    /// <summary>清空预览时调用，阻止上一张图的回调再贴回来。</summary>
    public void Cancel() => _version++;
}
