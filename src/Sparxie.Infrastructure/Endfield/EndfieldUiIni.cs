using System.Text;

namespace Sparxie.Infrastructure.Endfield;

/// <summary>
/// Read-modify-write of endfield-ui.ini, the small file EndfieldRuntime.dll re-reads every few seconds while the game runs.
/// Only the keys passed in are set; every other line (comments, keys of the Runtime that Sparxie does not know, blank lines, their order) is kept as it is.
/// The file is replaced atomically, so the Runtime never sees a half-written file, and an unchanged file is not touched at all.
/// </summary>
public static class EndfieldUiIni
{
    public const string FileName = "endfield-ui.ini";

    private const int MoveAttempts = 8;

    /// <summary>The ini next to EndfieldRuntime.dll (the folder of SessionHost and EndfieldCore).</summary>
    public static string DefaultPath() => Path.Combine(AppContext.BaseDirectory, FileName);

    /// <summary>
    /// Sets <paramref name="values"/> (key → value) in the ini at <paramref name="path"/>, creating the file when it is missing.
    /// Returns true when the file was written, false when it already said exactly this.
    /// </summary>
    public static bool Apply(string path, IReadOnlyList<KeyValuePair<string, string>> values)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        foreach (var (key, value) in values)
        {
            EnsureSafe(key, value);
        }

        var existing = ReadExisting(path);
        var updated = Merge(existing, values);
        if (string.Equals(existing, updated, StringComparison.Ordinal))
        {
            return false;
        }

        WriteAtomic(path, updated);
        return true;
    }

    /// <summary>The text of an ini after setting <paramref name="values"/> in <paramref name="existing"/> (pure; the part the tests exercise).</summary>
    public static string Merge(string existing, IReadOnlyList<KeyValuePair<string, string>> values)
    {
        var newline = existing.Length == 0 || existing.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = existing.Length == 0
            ? new List<string>()
            : existing.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList();
        if (lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        var pending = new List<KeyValuePair<string, string>>(values);
        for (var i = 0; i < lines.Count; i++)
        {
            var key = KeyOf(lines[i]);
            if (key is null)
            {
                continue;
            }

            var index = pending.FindIndex(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
            {
                lines[i] = $"{pending[index].Key}={pending[index].Value}";
                pending.RemoveAt(index);
                continue;
            }

            // a second line of a key that is already set: the Runtime would read the later one, so only one may stay
            if (values.Any(v => string.Equals(v.Key, key, StringComparison.OrdinalIgnoreCase)))
            {
                lines.RemoveAt(i);
                i--;
            }
        }

        if (pending.Count > 0)
        {
            // the Runtime reads only the first 2047 bytes of the file: a key that is not there yet goes to the top (below a leading comment), never to the end
            var at = 0;
            while (at < lines.Count && (lines[at].Trim().Length == 0 || IsComment(lines[at])))
            {
                at++;
            }

            lines.InsertRange(at, pending.Select(p => $"{p.Key}={p.Value}"));
        }

        return lines.Count == 0 ? string.Empty : string.Join(newline, lines) + newline;
    }

    private static string? KeyOf(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0 || IsComment(trimmed))
        {
            return null;
        }

        var equals = trimmed.IndexOf('=', StringComparison.Ordinal);
        return equals <= 0 ? null : trimmed[..equals].Trim();
    }

    private static bool IsComment(string line)
    {
        var trimmed = line.TrimStart();
        return trimmed.StartsWith('#') || trimmed.StartsWith(';') || trimmed.StartsWith('[');
    }

    private static void EnsureSafe(string key, string value)
    {
        // only tokens and numbers ever go in: a line break or a second key in a value would be another setting
        if (key.Length == 0 || key.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '_')))
        {
            throw new ArgumentException($"ini key not allowed: {key}", nameof(key));
        }

        if (value.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or ',' or '-' or '_')))
        {
            throw new ArgumentException($"ini value not allowed for {key}: {value}", nameof(value));
        }
    }

    private static string ReadExisting(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }
        catch (FileNotFoundException)
        {
            return string.Empty;
        }
        catch (DirectoryNotFoundException)
        {
            return string.Empty;
        }
    }

    private static void WriteAtomic(string path, string content)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);
        var temp = Path.Combine(directory, $".{FileName}.tmp-{Guid.NewGuid():N}");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                // no byte-order mark: the Runtime reads the bytes as they are
                var bytes = new UTF8Encoding(false).GetBytes(content);
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(flushToDisk: true);
            }

            // the game may have the file open for reading (the Runtime opens it shared); a replace can lose a race with that read, so try a few times
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    File.Move(temp, path, overwrite: true);
                    return;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < MoveAttempts)
                {
                    Thread.Sleep(30 * attempt);
                }
            }
        }
        catch
        {
            try
            {
                File.Delete(temp);
            }
            catch (Exception)
            {
                // the original error is the one to report
            }

            throw;
        }
    }
}
