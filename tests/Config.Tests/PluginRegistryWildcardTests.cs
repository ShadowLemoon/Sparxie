using Microsoft.Win32;
using Sparxie.Infrastructure.Plugins;
using Sparxie.Infrastructure.Processes;

namespace Config.Tests;

/// <summary>autoFind.registry 的 key 里可以用 * 枚举一层子键（例如鹰角启动器的 Launcher\&lt;哈希&gt;\install_path）。</summary>
public sealed class PluginRegistryWildcardTests : IDisposable
{
    private readonly string _root = @"Software\SparxieTest-" + Guid.NewGuid().ToString("N");

    public void Dispose()
    {
        if (OperatingSystem.IsWindows())
        {
            Registry.CurrentUser.DeleteSubKeyTree(_root, throwOnMissingSubKey: false);
        }
    }

    [Theory]
    [InlineData(@"Software\Hypergryph\Launcher\*", true)]
    [InlineData(@"Software\*\Launcher\*", true)]
    [InlineData(@"Software\*\*\*", false)]
    [InlineData(@"Software\Hyper*\Launcher", false)]
    [InlineData(@"Software\*x", false)]
    public void 通配符只能是完整的一段_最多两个(string key, bool valid)
    {
        var spec = new RegistryLookupSpec { Hive = "HKCU", Key = key, Value = "install_path" };

        Assert.Equal(valid, RegistryLookupSpec.IsValid(spec));
    }

    [Fact]
    public void 枚举哈希子键_只返回有该值的那个()
    {
        if (!OperatingSystem.IsWindows()) { return; }

        using (var launcher = Registry.CurrentUser.CreateSubKey(_root + @"\Launcher"))
        {
            using (var withPath = launcher.CreateSubKey("7cadd91e975e03634d178312268c57d6"))
            {
                withPath.SetValue("install_path", @"D:\Game\Hypergryph Launcher");
            }

            using var other = launcher.CreateSubKey("33a0a6296a20400d503c59ac0fd6341e");
            other.SetValue("language", "zh-cn");
        }

        var found = new SystemGameFinderEnvironment()
            .RegistryDirectories(new RegistryLookupSpec { Hive = "HKCU", Key = _root + @"\Launcher\*", Value = "install_path" })
            .ToList();

        Assert.Equal([@"D:\Game\Hypergryph Launcher"], found);
    }

    [Fact]
    public void 键不存在时返回空_不抛异常()
    {
        if (!OperatingSystem.IsWindows()) { return; }

        var found = new SystemGameFinderEnvironment()
            .RegistryDirectories(new RegistryLookupSpec { Hive = "HKCU", Key = _root + @"\Missing\*", Value = "x" });

        Assert.Empty(found);
    }
}
