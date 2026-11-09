using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.UI;

namespace Sparxie.Desktop.Services;

/// <summary>
/// 背景图主题色：取整张图的平均色，保留它的色相与明度（V），
/// 饱和度固定 0.6。因此暗图得到偏暗的主题色、亮图才亮——不会像固定亮度那样总是刺眼。
/// </summary>
public static class AccentColorExtractor
{
    public static readonly Color Fallback = Color.FromArgb(0xFF, 0x5B, 0x8D, 0xEF);

    /// <summary>平均色饱和度低于此值视为无彩色（纯灰/黑白图），色相无意义，回退默认色。</summary>
    private const double AchromaticSaturation = 0.05;

    public static async Task<Color> ExtractAsync(string path)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            using var stream = await file.OpenReadAsync();
            var decoder = await BitmapDecoder.CreateAsync(stream);
            var transform = new BitmapTransform
            {
                ScaledWidth = 96,
                ScaledHeight = 96,
                InterpolationMode = BitmapInterpolationMode.Fant,
            };
            var pixels = await decoder.GetPixelDataAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                transform,
                ExifOrientationMode.IgnoreExifOrientation,
                ColorManagementMode.ColorManageToSRgb);
            return FromAverage(pixels.DetachPixelData());
        }
        catch
        {
            return Fallback;
        }
    }

    private static Color FromAverage(byte[] bgra)
    {
        long b = 0, g = 0, r = 0;
        var count = bgra.Length / 4;
        if (count == 0)
        {
            return Fallback;
        }

        for (var i = 0; i + 3 < bgra.Length; i += 4)
        {
            b += bgra[i];
            g += bgra[i + 1];
            r += bgra[i + 2];
        }

        var (h, s, v) = ToHsv((byte)(r / count), (byte)(g / count), (byte)(b / count));
        return s < AchromaticSaturation ? Fallback : FromHsv(h, 0.6, v);
    }

    /// <summary>保留颜色的色相，重设饱和度与明度（官方启动器的“开始游戏”按钮是该游戏色相的浅色/柔色填充）。</summary>
    public static Color WithSv(Color color, double s, double v)
    {
        var (h, _, _) = ToHsv(color.R, color.G, color.B);
        return FromHsv(h, s, v);
    }

    private static (double H, double S, double V) ToHsv(byte r, byte g, byte b)
    {
        double rf = r / 255.0, gf = g / 255.0, bf = b / 255.0;
        var max = Math.Max(rf, Math.Max(gf, bf));
        var min = Math.Min(rf, Math.Min(gf, bf));
        var d = max - min;
        double h = 0;
        if (d > 0)
        {
            h = max == rf ? 60 * (((gf - bf) / d) % 6)
                : max == gf ? 60 * (((bf - rf) / d) + 2)
                : 60 * (((rf - gf) / d) + 4);
            if (h < 0)
            {
                h += 360;
            }
        }

        return (h, max <= 0 ? 0 : d / max, max);
    }

    private static Color FromHsv(double h, double s, double v)
    {
        var c = v * s;
        var x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        var m = v - c;
        var (r, g, b) = (int)(h / 60) switch
        {
            0 => (c, x, 0.0),
            1 => (x, c, 0.0),
            2 => (0.0, c, x),
            3 => (0.0, x, c),
            4 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        return Color.FromArgb(0xFF, (byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
    }
}
