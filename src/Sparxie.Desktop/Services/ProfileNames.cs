using System.Text.RegularExpressions;

namespace Sparxie.Desktop.Services;

/// <summary>
/// 配置名称。新建时是中性的“配置 N”，不预设区服（预设的名字可能和实际安装对不上）；
/// 填入游戏路径后，如果名称还是自动生成的，就按识别到的区服改名（国服 / 国际服 / B 服，重名加序号）。
/// 用户自己起过的名字不会被改动。
/// </summary>
public static partial class ProfileNames
{
    public static string NextDefault(IEnumerable<string> existingNames) => Unique("配置", existingNames, startAt: 1);

    /// <summary>名称是否是自动生成的（“配置 N”，或旧版本预填的 国服 / 国际服 / B 服 / Steam，可带序号）。</summary>
    public static bool IsAuto(string name) => AutoPattern().IsMatch(name.Trim());

    /// <summary>在已有名称里取不重复的名称：先试 <paramref name="label"/> 本身（<paramref name="startAt"/> 为 1 时直接带序号），再依次加 2、3…</summary>
    public static string Unique(string label, IEnumerable<string> existingNames, int startAt = 0)
    {
        var used = existingNames.Select(name => name.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (startAt == 0 && !used.Contains(label))
        {
            return label;
        }

        for (var n = startAt == 0 ? 2 : startAt; ; n++)
        {
            var name = $"{label} {n}";
            if (!used.Contains(name))
            {
                return name;
            }
        }
    }

    [GeneratedRegex(@"^(配置|国服|国际服|B 服|Steam)( \d+)?$")]
    private static partial Regex AutoPattern();
}
