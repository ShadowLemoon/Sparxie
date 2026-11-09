using System.Net.Http;
using System.Text.Json;
using Sparxie.Contracts.Models;

namespace Sparxie.Desktop.Services;

/// <summary>
/// 官方启动器（HoYoPlay）版本背景图获取，参考 HoYoPlay 的 hyp-api 调用：
/// getAllGameBasicInfo?launcher_id=jGHBHlcOq1 → data.game_info_list[].backgrounds[].background.url。
/// 图片缓存到 %LOCALAPPDATA%\Sparxie\backgrounds，网络失败时回退到旧缓存。
/// </summary>
public sealed class GameBackgroundService
{
    private const string ApiUrl =
        "https://hyp-api.mihoyo.com/hyp/hyp-connect/api/getAllGameBasicInfo?launcher_id=jGHBHlcOq1&language=zh-cn";

    private const string GamesApiUrl =
        "https://hyp-api.mihoyo.com/hyp/hyp-connect/api/getGames?launcher_id=jGHBHlcOq1&language=zh-cn";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) SparxieDesktop/1.0");
        client.Timeout = TimeSpan.FromSeconds(20);
        return client;
    }

    private static string CacheDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Sparxie", "backgrounds");

    private static string BizOf(GameType game) => game switch
    {
        GameType.Genshin => "hk4e_cn",
        GameType.StarRail => "hkrpg_cn",
        GameType.ZenlessZoneZero => "nap_cn",
        _ => string.Empty,
    };

    /// <summary>返回本地背景图路径；无法获取时返回 null（调用方回退纯色背景）。</summary>
    public async Task<string?> GetBackgroundPathAsync(GameType game)
    {
        var biz = BizOf(game);
        if (biz.Length == 0)
        {
            return null;
        }

        Directory.CreateDirectory(CacheDir);
        var imageFile = Path.Combine(CacheDir, $"{biz}.img");
        var urlFile = Path.Combine(CacheDir, $"{biz}.url");

        string? url = null;
        try
        {
            url = await FetchBackgroundUrlAsync(biz).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            // 离线或接口异常时回退旧缓存。
        }

        if (!string.IsNullOrWhiteSpace(url))
        {
            var cached = File.Exists(urlFile) ? await File.ReadAllTextAsync(urlFile).ConfigureAwait(false) : null;
            if (cached == url && File.Exists(imageFile))
            {
                return imageFile;
            }

            try
            {
                var bytes = await Http.GetByteArrayAsync(url).ConfigureAwait(false);
                await File.WriteAllBytesAsync(imageFile, bytes).ConfigureAwait(false);
                await File.WriteAllTextAsync(urlFile, url).ConfigureAwait(false);
                return imageFile;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                // 下载失败回退旧缓存。
            }
        }

        return File.Exists(imageFile) ? imageFile : null;
    }

    /// <summary>返回游戏图标（getGames → display.icon）本地路径；失败返回 null。</summary>
    public async Task<string?> GetGameIconPathAsync(GameType game)
    {
        var biz = BizOf(game);
        if (biz.Length == 0)
        {
            return null;
        }

        Directory.CreateDirectory(CacheDir);
        var imageFile = Path.Combine(CacheDir, $"{biz}-icon.img");
        var urlFile = Path.Combine(CacheDir, $"{biz}-icon.url");

        string? url = null;
        try
        {
            using var doc = JsonDocument.Parse(await Http.GetStringAsync(GamesApiUrl).ConfigureAwait(false));
            foreach (var item in doc.RootElement.GetProperty("data").GetProperty("games").EnumerateArray())
            {
                if (item.GetProperty("biz").GetString() != biz)
                {
                    continue;
                }

                url = item.GetProperty("display").GetProperty("icon").GetProperty("url").GetString();
                break;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException
            or KeyNotFoundException)
        {
            // 离线或接口异常时回退旧缓存。
        }

        return await DownloadOrCacheAsync(url, imageFile, urlFile).ConfigureAwait(false);
    }

    private static async Task<string?> DownloadOrCacheAsync(string? url, string imageFile, string urlFile)
    {
        if (!string.IsNullOrWhiteSpace(url))
        {
            var cached = File.Exists(urlFile) ? await File.ReadAllTextAsync(urlFile).ConfigureAwait(false) : null;
            if (cached == url && File.Exists(imageFile))
            {
                return imageFile;
            }

            try
            {
                var bytes = await Http.GetByteArrayAsync(url).ConfigureAwait(false);
                await File.WriteAllBytesAsync(imageFile, bytes).ConfigureAwait(false);
                await File.WriteAllTextAsync(urlFile, url).ConfigureAwait(false);
                return imageFile;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                // 下载失败回退旧缓存。
            }
        }

        return File.Exists(imageFile) ? imageFile : null;
    }

    private static async Task<string?> FetchBackgroundUrlAsync(string biz)
    {
        using var doc = JsonDocument.Parse(
            await Http.GetStringAsync(ApiUrl).ConfigureAwait(false));
        foreach (var gameInfo in doc.RootElement.GetProperty("data").GetProperty("game_info_list").EnumerateArray())
        {
            if (gameInfo.GetProperty("game").GetProperty("biz").GetString() != biz)
            {
                continue;
            }

            if (!gameInfo.TryGetProperty("backgrounds", out var backgrounds)
                || backgrounds.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (var background in backgrounds.EnumerateArray())
            {
                if (background.TryGetProperty("background", out var image)
                    && image.TryGetProperty("url", out var url))
                {
                    var value = url.GetString();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        return value;
                    }
                }
            }

            return null;
        }

        return null;
    }
}
