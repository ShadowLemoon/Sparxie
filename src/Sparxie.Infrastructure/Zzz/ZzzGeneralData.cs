using System.Text;

namespace Sparxie.Infrastructure.Zzz;

/// <summary>
/// 绝区零 GENERAL_DATA.bin 的读写与平台切换。
/// 文件是带自定义混淆头的 .NET BinaryFormatter 编码 JSON 文本：
/// 头部 7 个枚举断言 + 7-bit 长度 + XOR(magic) 编码的 UTF-8 内容 + MessageEnd 尾部。
/// 布局复刻自旧 ZZZTouchLauncher 的 Sleepy 实现（来源：CollapseLauncher#466 相关社区实现），
/// 仅用于本启动器内部读取和切换 LocalUILayoutPlatform，不扩展成通用文件解析器。
/// </summary>
public static class ZzzGeneralData
{
    public const int PlatformTouch = 1;
    public const int PlatformPc = 2;

    /// <summary>GENERAL_DATA.bin 内容混淆用固定 Magic 字节（与旧 GUI 一致）。</summary>
    public static readonly byte[] Magic =
    {
        85, 110, 209, 150, 116, 209, 131, 206, 149, 110, 103, 105, 110, 208, 181,
        46, 71, 208, 176, 109, 101, 206, 159, 98, 106, 101, 209, 129, 116,
    };

    private const string PlatformFieldPattern = "(LocalUILayoutPlatform\")(\\s*:\\s*)(-?\\d+)";

    public static int ReadPlatform(string dataPath)
    {
        var raw = ReadString(dataPath);
        return ParsePlatform(raw);
    }

    public static void WritePlatform(string dataPath, int platform)
    {
        var raw = ReadString(dataPath);
        var updated = System.Text.RegularExpressions.Regex.Replace(
            raw,
            PlatformFieldPattern,
            m => m.Groups[1].Value + m.Groups[2].Value + platform);
        WriteString(dataPath, updated);
    }

    /// <summary>测试/工具用：写入完整 Sleepy 编码内容（含头部与尾部断言）。</summary>
    internal static void WriteRawString(string filePath, string content)
    {
        WriteString(filePath, content);
    }

    private static int ParsePlatform(string raw)
    {
        var match = System.Text.RegularExpressions.Regex.Match(raw, PlatformFieldPattern);
        if (match.Success && int.TryParse(match.Groups[3].Value, out var mode))
        {
            return mode == PlatformTouch ? PlatformTouch : PlatformPc;
        }

        return PlatformPc;
    }

    private static string ReadString(string filePath)
    {
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("GENERAL_DATA.bin 不存在", filePath);
        }

        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);

        AssertHeader(reader);
        var length = Read7BitEncodedInt(reader);
        var magicLength = Magic.Length;

        var bufferChars = new char[length];
        try
        {
            CreateEvil(out var evil, out _);
            var j = Decode(evil, reader, length, magicLength, bufferChars);
            AssertFooter(reader);
            return new string(bufferChars, 0, j);
        }
        finally
        {
            Array.Clear(bufferChars, 0, bufferChars.Length);
        }
    }

    private static void WriteString(string filePath, string content)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        using var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.Write);
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);

        WriteHeader(writer);

        var contentBytes = new byte[content.Length * 2];
        var encodedBytes = new byte[content.Length * 2];
        try
        {
            CreateEvil(out var evil, out _);
            var bytesWritten = Encoding.UTF8.GetBytes(content, 0, content.Length, contentBytes, 0);
            var h = Encode(Magic, bytesWritten, contentBytes, encodedBytes, evil);

            Write7BitEncodedInt(writer, h);
            writer.BaseStream.Write(encodedBytes, 0, h);
            WriteFooter(writer);
        }
        finally
        {
            Array.Clear(contentBytes, 0, contentBytes.Length);
            Array.Clear(encodedBytes, 0, encodedBytes.Length);
        }
    }

    private static int Decode(bool[] evil, BinaryReader reader, int length, int magicLength, char[] bufferChars)
    {
        var eepy = false;
        var j = 0;
        for (var i = 0; i < length; i++)
        {
            var n = i % magicLength;
            var c = reader.ReadByte();
            var ch = (byte)(c ^ Magic[n]);

            if (evil[n])
            {
                eepy = ch != 0;
            }
            else
            {
                if (eepy)
                {
                    ch += 0x40;
                    eepy = false;
                }

                bufferChars[j++] = (char)ch;
            }
        }

        return j;
    }

    private static int Encode(byte[] magic, int contentLen, byte[] contentBytes, byte[] encodedBytes, bool[] evil)
    {
        var h = 0;
        var i = 0;
        for (var j = 0; j < contentLen; j++)
        {
            var n = i % magic.Length;
            var ch = contentBytes[j];
            if (evil[n])
            {
                byte eepy = 0;
                if (contentBytes[j] > 0x40)
                {
                    ch -= 0x40;
                    eepy = 1;
                }

                encodedBytes[h++] = (byte)(eepy ^ magic[n]);
                n = ++i % magic.Length;
            }

            encodedBytes[h++] = (byte)(ch ^ magic[n]);
            i++;
        }

        return h;
    }

    private static void CreateEvil(out bool[] evil, out int evilsCount)
    {
        evil = new bool[Magic.Length];
        evilsCount = 0;
        for (var i = 0; i < Magic.Length; i++)
        {
            evil[i] = (Magic[i] & 0xC0) == 0xC0;
            if (evil[i])
            {
                evilsCount++;
            }
        }
    }

    private static void AssertHeader(BinaryReader reader)
    {
        AssertByte(reader, 0); // SerializedStreamHeader
        AssertInt32(reader, 1); // Object
        AssertInt32(reader, 10); // BinaryReference
        AssertInt32(reader, 5); // String
        AssertInt32(reader, 1); // Single
        AssertByte(reader, 8); // StringArray
        AssertInt32(reader, 1); // Single
    }

    private static void AssertFooter(BinaryReader reader)
    {
        AssertByte(reader, 11); // MessageEnd
    }

    private static void AssertByte(BinaryReader reader, byte expected)
    {
        var actual = reader.ReadByte();
        if (actual != expected)
        {
            throw new InvalidDataException($"GENERAL_DATA.bin 头校验失败 at {reader.BaseStream.Position - 1:x8}: 期望 {expected} 实际 {actual}");
        }
    }

    private static void AssertInt32(BinaryReader reader, int expected)
    {
        var actual = reader.ReadInt32();
        if (actual != expected)
        {
            throw new InvalidDataException($"GENERAL_DATA.bin 头校验失败 at {reader.BaseStream.Position - 4:x8}: 期望 {expected} 实际 {actual}");
        }
    }

    private static void WriteHeader(BinaryWriter writer)
    {
        writer.Write((byte)0); // SerializedStreamHeader
        writer.Write(1); // Object
        writer.Write(10); // BinaryReference
        writer.Write(5); // String
        writer.Write(1); // Single
        writer.Write((byte)8); // StringArray
        writer.Write(1); // Single
    }

    private static void WriteFooter(BinaryWriter writer)
    {
        writer.Write((byte)11); // MessageEnd
    }

    private static int Read7BitEncodedInt(BinaryReader reader)
    {
        var count = 0;
        var shift = 0;
        byte b;
        do
        {
            if (shift == 35)
            {
                throw new FormatException("7-bit int 格式错误");
            }

            b = reader.ReadByte();
            count |= (b & 0x7F) << shift;
            shift += 7;
        }
        while ((b & 0x80) != 0);

        return count;
    }

    private static void Write7BitEncodedInt(BinaryWriter writer, int value)
    {
        var v = (uint)value;
        while (v >= 0x80)
        {
            writer.Write((byte)(v | 0x80));
            v >>= 7;
        }

        writer.Write((byte)v);
    }
}
