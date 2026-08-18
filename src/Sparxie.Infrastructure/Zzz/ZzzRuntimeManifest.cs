using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sparxie.Infrastructure.Zzz;

/// <summary>
/// build/zzz-runtime.json 契约：固定 ZZZ Runtime 版本、Release 资产名与 SHA-256。
/// 客户端不访问私库，CI 按该清单下载并校验后随启动器打包。
/// </summary>
public sealed class ZzzRuntimeManifest
{
    public string RuntimeVersion { get; set; } = string.Empty;

    public string ReleaseAsset { get; set; } = string.Empty;

    public string Sha256 { get; set; } = string.Empty;

    public List<string> Files { get; set; } = [];

    public List<string> ExeWhiteList { get; set; } = [];

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(RuntimeVersion))
        {
            throw new InvalidDataException("zzz-runtime.json: runtimeVersion 为空");
        }

        if (string.IsNullOrWhiteSpace(ReleaseAsset))
        {
            throw new InvalidDataException("zzz-runtime.json: releaseAsset 为空");
        }

        if (string.IsNullOrWhiteSpace(Sha256) || !IsHex(Sha256) || Sha256.Length != 64)
        {
            throw new InvalidDataException("zzz-runtime.json: sha256 必须是 64 位十六进制");
        }

        if (Files is null || Files.Count == 0)
        {
            throw new InvalidDataException("zzz-runtime.json: files 不能为空");
        }

        var fileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Files)
        {
            if (string.IsNullOrWhiteSpace(file) || Path.GetFileName(file) != file || !fileNames.Add(file))
            {
                throw new InvalidDataException($"zzz-runtime.json: 非法或重复文件名 {file}");
            }
        }

        // SessionHost 的 P/Invoke 固定加载该入口；缺失时发布包无法启动 ZZZ 会话。
        if (!fileNames.Contains("ZZZTouchCore.dll"))
        {
            throw new InvalidDataException("zzz-runtime.json: files 必须包含 ZZZTouchCore.dll");
        }

        if (ExeWhiteList is null || ExeWhiteList.Count == 0)
        {
            throw new InvalidDataException("zzz-runtime.json: exeWhiteList 不能为空");
        }
    }

    private static bool IsHex(string value)
    {
        foreach (var c in value)
        {
            if (!Uri.IsHexDigit(c))
            {
                return false;
            }
        }

        return true;
    }

    public static ZzzRuntimeManifest Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("zzz-runtime.json 不存在", path);
        }

        try
        {
            var manifest = JsonSerializer.Deserialize<ZzzRuntimeManifest>(File.ReadAllText(path), Options);
            if (manifest is null)
            {
                throw new InvalidDataException("zzz-runtime.json 内容为空");
            }

            manifest.Validate();
            return manifest;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"zzz-runtime.json 解析失败: {ex.Message}", ex);
        }
    }

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };
}
