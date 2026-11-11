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

    private static string CacheDir => AppPaths.BackgroundCacheDirectory;

    private static string BizOf(GameType game) => game switch
    {
        GameType.Genshin => "hk4e_cn",
        GameType.StarRail => "hkrpg_cn",
        GameType.ZenlessZoneZero => "nap_cn",
        _ => string.Empty,
    };

    // 背景缓存：图片 / 视频文件名由 URL 决定（官方换图即换文件名）；
    // {biz}.list 记住上次拿到的背景列表（离线时用），{biz}.sel 记住用户选的是哪一张。
    private static readonly Dictionary<string, (List<RemoteBackground> Items, DateTime FetchedAt)> ListMemo = [];
    private static readonly TimeSpan ListMemoTtl = TimeSpan.FromMinutes(10);

    private sealed record RemoteBackground(string Id, string ImageUrl, string? VideoUrl, string? ThemeUrl);

    private sealed record CachedBackground(string Id, string ImageFile, string? VideoUrl, string? ThemeFile = null);

    private static string ListFile(string biz) => Path.Combine(CacheDir, $"{biz}.list");

    private static string SelectionFile(string biz) => Path.Combine(CacheDir, $"{biz}.sel");

    private static string HashOf(string text) =>
        Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(text)))[..12].ToLowerInvariant();

    /// <summary>该游戏记住的背景编号（用户上次选的那一张）；没有记录返回 null。</summary>
    public string? GetSelectedId(GameType game)
    {
        var biz = BizOf(game);
        try
        {
            return biz.Length > 0 && File.Exists(SelectionFile(biz)) ? File.ReadAllText(SelectionFile(biz)).Trim() : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    public void SetSelectedId(GameType game, string id)
    {
        var biz = BizOf(game);
        if (biz.Length == 0)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(CacheDir);
            File.WriteAllText(SelectionFile(biz), id);
        }
        catch (IOException)
        {
            // 记不住选择不影响使用。
        }
    }

    /// <summary>同步返回上次缓存的背景列表（只含图片已在本地的项）；用于切换游戏时立即上屏。</summary>
    public IReadOnlyList<GameBackgroundItem> GetCachedBackgrounds(GameType game)
    {
        var biz = BizOf(game);
        if (biz.Length == 0 || !File.Exists(ListFile(biz)))
        {
            return [];
        }

        try
        {
            var cached = JsonSerializer.Deserialize<List<CachedBackground>>(File.ReadAllText(ListFile(biz))) ?? [];
            return cached
                .Select(item => (item, path: Path.Combine(CacheDir, item.ImageFile)))
                .Where(entry => File.Exists(entry.path))
                .Select(entry => new GameBackgroundItem(entry.item.Id, entry.path, entry.item.VideoUrl, ExistingFile(entry.item.ThemeFile)))
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return [];
        }
    }

    private static string? ExistingFile(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return null;
        }

        var path = Path.Combine(CacheDir, name);
        return File.Exists(path) ? path : null;
    }

    /// <summary>同步返回该游戏当前应显示的背景图本地路径（记住的那张，没有就第一张）；没有缓存时返回 null。</summary>
    public string? GetCachedBackgroundPath(GameType game) => Pick(GetCachedBackgrounds(game), GetSelectedId(game))?.ImagePath;

    /// <summary>从列表里取记住的那一项，找不到就取第一项。</summary>
    public static GameBackgroundItem? Pick(IReadOnlyList<GameBackgroundItem> items, string? selectedId) =>
        items.FirstOrDefault(item => item.Id == selectedId) ?? items.FirstOrDefault();

    /// <summary>
    /// 官方启动器的全部背景（图片已下载到本地）。无法联网时回退上次缓存。
    /// 一项可以带视频（动态背景）；视频体积大，按需用 <see cref="EnsureVideoAsync"/> 下载。
    /// </summary>
    public async Task<IReadOnlyList<GameBackgroundItem>> GetBackgroundsAsync(GameType game)
    {
        var biz = BizOf(game);
        if (biz.Length == 0)
        {
            return [];
        }

        Directory.CreateDirectory(CacheDir);

        List<RemoteBackground> remote;
        try
        {
            remote = await GetRemoteBackgroundsAsync(biz).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException)
        {
            return GetCachedBackgrounds(game);
        }

        var result = new List<GameBackgroundItem>();
        var cached = new List<CachedBackground>();
        foreach (var item in remote)
        {
            var name = $"{biz}-{HashOf(item.ImageUrl)}.img";
            var file = Path.Combine(CacheDir, name);
            try
            {
                if (!File.Exists(file))
                {
                    await File.WriteAllBytesAsync(file, await Http.GetByteArrayAsync(item.ImageUrl).ConfigureAwait(false)).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
            {
                continue;
            }

            // 叠加图（theme）：游戏标题 / Logo 的透明图，和图片、视频同一画幅，盖在它们上面。
            string? themeName = null;
            if (!string.IsNullOrWhiteSpace(item.ThemeUrl))
            {
                var themeFile = $"{biz}-{HashOf(item.ThemeUrl)}.img";
                try
                {
                    if (!File.Exists(Path.Combine(CacheDir, themeFile)))
                    {
                        await File.WriteAllBytesAsync(Path.Combine(CacheDir, themeFile), await Http.GetByteArrayAsync(item.ThemeUrl).ConfigureAwait(false)).ConfigureAwait(false);
                    }

                    themeName = themeFile;
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
                {
                    // 叠加图失败只是少了标题，背景照常显示。
                }
            }

            result.Add(new GameBackgroundItem(item.Id, file, item.VideoUrl, ExistingFile(themeName)));
            cached.Add(new CachedBackground(item.Id, name, item.VideoUrl, themeName));
        }

        if (result.Count == 0)
        {
            return GetCachedBackgrounds(game);
        }

        try
        {
            await File.WriteAllTextAsync(ListFile(biz), JsonSerializer.Serialize(cached)).ConfigureAwait(false);
        }
        catch (IOException)
        {
            // 列表缓存写失败只影响离线回退。
        }

        return result;
    }

    /// <summary>把背景的视频下载到本地缓存并返回路径；没有视频或下载失败返回 null。</summary>
    public async Task<string?> EnsureVideoAsync(GameBackgroundItem item)
    {
        if (string.IsNullOrWhiteSpace(item.VideoUrl))
        {
            return null;
        }

        try
        {
            Directory.CreateDirectory(CacheDir);
            var extension = Path.GetExtension(new Uri(item.VideoUrl).AbsolutePath);
            var file = Path.Combine(CacheDir, $"video-{HashOf(item.VideoUrl)}{(extension.Length > 0 ? extension : ".mp4")}");
            if (!File.Exists(file))
            {
                // 先写临时文件再改名，避免下载一半就被当成完整视频。
                var temp = file + ".part";
                await using (var output = File.Create(temp))
                await using (var input = await Http.GetStreamAsync(item.VideoUrl).ConfigureAwait(false))
                {
                    await input.CopyToAsync(output).ConfigureAwait(false);
                }

                File.Move(temp, file, overwrite: true);
            }

            return file;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or UriFormatException)
        {
            return null;
        }
    }

    private static async Task<List<RemoteBackground>> GetRemoteBackgroundsAsync(string biz)
    {
        lock (ListMemo)
        {
            if (ListMemo.TryGetValue(biz, out var memo) && DateTime.UtcNow - memo.FetchedAt < ListMemoTtl)
            {
                return memo.Items;
            }
        }

        var items = await FetchBackgroundsAsync(biz).ConfigureAwait(false);
        lock (ListMemo)
        {
            ListMemo[biz] = (items, DateTime.UtcNow);
        }

        return items;
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

    /// <summary>解析接口里该游戏的全部背景：background.url 是图，video.url（可选）是动态背景的视频。</summary>
    private static async Task<List<RemoteBackground>> FetchBackgroundsAsync(string biz)
    {
        using var doc = JsonDocument.Parse(await Http.GetStringAsync(ApiUrl).ConfigureAwait(false));
        var result = new List<RemoteBackground>();
        foreach (var gameInfo in doc.RootElement.GetProperty("data").GetProperty("game_info_list").EnumerateArray())
        {
            if (gameInfo.GetProperty("game").GetProperty("biz").GetString() != biz
                || !gameInfo.TryGetProperty("backgrounds", out var backgrounds)
                || backgrounds.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var background in backgrounds.EnumerateArray())
            {
                var image = UrlOf(background, "background");
                if (image is null)
                {
                    continue;
                }

                var id = background.TryGetProperty("id", out var idElement) ? idElement.GetString() : null;
                result.Add(new RemoteBackground(string.IsNullOrWhiteSpace(id) ? HashOf(image) : id, image, UrlOf(background, "video"), UrlOf(background, "theme")));
            }
        }

        // 启动器的游戏海报（getGames → display.background）：Starward 也把它追加成最后一张静态背景。
        try
        {
            if (await FetchPosterUrlAsync(biz).ConfigureAwait(false) is { } poster && result.All(item => item.ImageUrl != poster))
            {
                result.Add(new RemoteBackground("poster", poster, null, null));
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException)
        {
            // 海报拿不到只是少一张，不影响其余背景。
        }

        return result;
    }

    private static async Task<string?> FetchPosterUrlAsync(string biz)
    {
        using var doc = JsonDocument.Parse(await Http.GetStringAsync(GamesApiUrl).ConfigureAwait(false));
        foreach (var item in doc.RootElement.GetProperty("data").GetProperty("games").EnumerateArray())
        {
            if (item.GetProperty("biz").GetString() == biz)
            {
                return item.TryGetProperty("display", out var display) ? UrlOf(display, "background") : null;
            }
        }

        return null;
    }

    private static string? UrlOf(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var node) && node.ValueKind == JsonValueKind.Object
            && node.TryGetProperty("url", out var url) && url.GetString() is { Length: > 0 } value
            ? value
            : null;
}

/// <summary>一张官方背景：本地图片，加上可选的视频（动态背景）地址。</summary>
public sealed record GameBackgroundItem(string Id, string ImagePath, string? VideoUrl, string? ThemePath = null)
{
    public bool HasVideo => !string.IsNullOrWhiteSpace(VideoUrl);
}
