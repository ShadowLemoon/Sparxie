using System.Text;

namespace Sparxie.Infrastructure.Configuration;

/// <summary>同目录临时文件写入、刷盘后原子替换。</summary>
public static class AtomicFile
{
    /// <summary>把字节以 CreateNew 方式持久化写入（防覆盖已有文件）。</summary>
    public static void WriteBytesNew(string path, ReadOnlySpan<byte> bytes)
    {
        using var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        fs.Write(bytes);
        fs.Flush(flushToDisk: true);
    }

    /// <summary>原子替换：临时文件写入并刷盘后移动到目标路径。</summary>
    public static void WriteAllTextAtomic(string targetPath, string content)
    {
        var dir = Path.GetDirectoryName(targetPath)!;
        var tmp = Path.Combine(dir, $".{Path.GetFileName(targetPath)}.tmp-{Guid.NewGuid():N}");

        try
        {
            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var bytes = Encoding.UTF8.GetBytes(content);
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush(flushToDisk: true);
            }

            File.Move(tmp, targetPath, overwrite: true);
        }
        catch
        {
            TryDelete(tmp);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // 忽略清理失败，原始异常优先
        }
    }
}
