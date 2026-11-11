namespace Sparxie.Desktop.Services;

/// <summary>新建配置的默认名称：按常见安装来源依次取第一个还没被同一游戏的其他配置用掉的。</summary>
public static class ProfileNames
{
    public static readonly string[] Defaults = ["国服", "国际服", "Steam", "B 服"];

    public static string NextDefault(IEnumerable<string> existingNames)
    {
        var used = existingNames.Select(name => name.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var name in Defaults)
        {
            if (!used.Contains(name))
            {
                return name;
            }
        }

        for (var n = 2; ; n++)
        {
            var name = $"配置 {n}";
            if (!used.Contains(name))
            {
                return name;
            }
        }
    }
}
