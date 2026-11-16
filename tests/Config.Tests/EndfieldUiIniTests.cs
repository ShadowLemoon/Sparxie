using System.Globalization;
using Sparxie.Contracts.Models;
using Sparxie.Infrastructure.Endfield;

namespace Config.Tests;

/// <summary>Read-modify-write of endfield-ui.ini: unknown keys survive, the file is replaced atomically, and nothing but tokens and numbers can be written.</summary>
public sealed class EndfieldUiIniTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sparxie-tests", Guid.NewGuid().ToString("N"));

    public EndfieldUiIniTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // 测试清理失败不影响结论
        }
    }

    private string IniPath => Path.Combine(_dir, EndfieldUiIni.FileName);

    private static KeyValuePair<string, string>[] Values(string scale, string safe) =>
        [new(EndfieldUiSettings.ScaleKey, scale), new(EndfieldUiSettings.SafeAreaKey, safe)];

    // ---- ini text ----

    [Fact]
    public void 空文件写出两行并用CRLF()
    {
        var text = EndfieldUiIni.Merge(string.Empty, Values("auto", "auto"));

        Assert.Equal("ui_scale=auto\r\nsafe_area=auto\r\n", text);
    }

    [Fact]
    public void 已有的其他键注释空行与顺序原样保留()
    {
        const string existing = "# set by hand\r\nscale=1.25\r\n\r\nsafe_left=186\r\nsafe_right=186\r\nui_scale=auto\r\n; keep me\r\nobserve=1\r\n";

        var text = EndfieldUiIni.Merge(existing, Values("1.40", "0,60"));

        Assert.Equal("# set by hand\r\nsafe_area=0,60\r\nscale=1.25\r\n\r\nsafe_left=186\r\nsafe_right=186\r\nui_scale=1.40\r\n; keep me\r\nobserve=1\r\n", text);
    }

    [Fact]
    public void 键名不分大小写且重复的行只留一行()
    {
        const string existing = "UI_SCALE=1.10\nmode=helper\nui_scale = 1.70\nsafe_area=off\n";

        var text = EndfieldUiIni.Merge(existing, Values("auto", "auto"));

        Assert.Equal("ui_scale=auto\nmode=helper\nsafe_area=auto\n", text);
    }

    [Fact]
    public void 缺的键加到文件开头而不是末尾()
    {
        // the Runtime reads only the first 2047 bytes
        var filler = string.Join("\n", Enumerable.Range(0, 120).Select(i => $"extra_key_{i}=value_{i}")) + "\n";
        Assert.True(filler.Length > 2047);

        var text = EndfieldUiIni.Merge(filler, Values("1.50", "off"));

        Assert.StartsWith("ui_scale=1.50\nsafe_area=off\nextra_key_0=value_0\n", text);
        Assert.True(text.IndexOf("safe_area=off", StringComparison.Ordinal) < 2047);
        Assert.EndsWith(filler, text, StringComparison.Ordinal);
    }

    [Fact]
    public void 开头的注释块之后才插入新键()
    {
        var text = EndfieldUiIni.Merge("# a\n# b\nobserve=1\n", Values("auto", "auto"));

        Assert.Equal("# a\n# b\nui_scale=auto\nsafe_area=auto\nobserve=1\n", text);
    }

    [Fact]
    public void 再写一遍同样的值文本不变()
    {
        var once = EndfieldUiIni.Merge("observe=1\r\nscale=1.25\r\n", Values("1.35", "12,34"));

        Assert.Equal(once, EndfieldUiIni.Merge(once, Values("1.35", "12,34")));
    }

    [Fact]
    public void 没有结尾换行的文件不丢最后一行()
    {
        var text = EndfieldUiIni.Merge("observe=1", Values("auto", "auto"));

        Assert.Equal("ui_scale=auto\nsafe_area=auto\nobserve=1\n", text);
    }

    // ---- file ----

    [Fact]
    public void 写文件创建缺失的文件且没有BOM()
    {
        Assert.True(EndfieldUiIni.Apply(IniPath, Values("1.30", "auto")));

        var bytes = File.ReadAllBytes(IniPath);
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
        Assert.Equal("ui_scale=1.30\r\nsafe_area=auto\r\n", System.Text.Encoding.UTF8.GetString(bytes));
        Assert.Empty(Directory.GetFiles(_dir, ".*tmp-*"));
    }

    [Fact]
    public void 带BOM的旧文件被读成正常文本并写回时去掉BOM()
    {
        File.WriteAllBytes(IniPath, [.. new byte[] { 0xEF, 0xBB, 0xBF }, .. "observe=1\r\n"u8.ToArray()]);

        Assert.True(EndfieldUiIni.Apply(IniPath, Values("auto", "auto")));

        var bytes = File.ReadAllBytes(IniPath);
        Assert.Equal((byte)'u', bytes[0]);
        Assert.Contains("observe=1", System.Text.Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public void 内容没变时不碰文件()
    {
        EndfieldUiIni.Apply(IniPath, Values("1.30", "off"));
        var stamp = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(IniPath, stamp);

        Assert.False(EndfieldUiIni.Apply(IniPath, Values("1.30", "off")));

        Assert.Equal(stamp, File.GetLastWriteTimeUtc(IniPath));
    }

    [Fact]
    public void 游戏反复短暂打开读取文件时也能替换()
    {
        // the Runtime re-reads the file every few seconds with read/write/delete sharing; here a reader does it every millisecond
        EndfieldUiIni.Apply(IniPath, Values("auto", "auto"));
        File.AppendAllText(IniPath, "observe=1\r\n");
        using var stop = new CancellationTokenSource();
        var reader = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    using var stream = new FileStream(IniPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    _ = stream.ReadByte();
                }
                catch (IOException)
                {
                    // the file is being replaced at this moment
                }

                Thread.Sleep(1);
            }
        });

        try
        {
            for (var i = 0; i < 20; i++)
            {
                EndfieldUiIni.Apply(IniPath, Values((1.0 + i * 0.04).ToString("0.00", CultureInfo.InvariantCulture), "100,100"));
            }
        }
        finally
        {
            stop.Cancel();
            reader.Wait();
        }

        var text = File.ReadAllText(IniPath);
        Assert.Contains("ui_scale=1.76", text);
        Assert.Contains("safe_area=100,100", text);
        Assert.Contains("observe=1", text);
        Assert.Empty(Directory.GetFiles(_dir, ".*tmp-*"));
    }

    [Fact]
    public void 有人一直占着文件时抛出异常原文件不变且不留临时文件()
    {
        EndfieldUiIni.Apply(IniPath, Values("1.30", "off"));
        var before = File.ReadAllBytes(IniPath);

        using (new FileStream(IniPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            Assert.ThrowsAny<Exception>(() => EndfieldUiIni.Apply(IniPath, Values("1.80", "off")));
        }

        Assert.Equal(before, File.ReadAllBytes(IniPath));
        Assert.Empty(Directory.GetFiles(_dir, ".*tmp-*"));
    }

    [Theory]
    [InlineData("ui_scale", "1.25\r\nobserve=0")]
    [InlineData("ui_scale", "auto # x")]
    [InlineData("ui_scale", "a=b")]
    [InlineData("ui scale", "auto")]
    [InlineData("ui_scale\n", "auto")]
    [InlineData("", "auto")]
    public void 值或键带有可成为别的设置的字符时拒绝且文件不变(string key, string value)
    {
        EndfieldUiIni.Apply(IniPath, Values("1.30", "off"));
        var before = File.ReadAllBytes(IniPath);

        Assert.Throws<ArgumentException>(() => EndfieldUiIni.Apply(IniPath, [new(key, value)]));

        Assert.Equal(before, File.ReadAllBytes(IniPath));
    }
}
