using System.IO.Compression;
using Xunit;

namespace Config.Tests;

/// <summary>
/// 发布产物审计：桌面 ZIP 存在时验证发布门禁。
/// 本地未构建 Release 产物时跳过（不阻塞单元测试）。
/// </summary>
public class ReleaseArtifactAuditTests
{
    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        var root = new DirectoryInfo(dir);
        while (root is not null && root.Name != "Sparxie")
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        return root.FullName;
    }

    private static string? FindZip()
    {
        var directory = Path.Combine(RepoRoot(), "artifacts");
        return Directory.Exists(directory)
            ? Directory.GetFiles(directory, "Sparxie-desktop-*-win-x64.zip").OrderBy(f => f).LastOrDefault()
            : null;
    }

    [Fact]
    public void 发布ZIP关键文件齐全且不含PDB()
    {
        var zip = FindZip();
        if (zip is null)
        {
            return; // 未构建发布产物，跳过
        }

        using var archive = ZipFile.OpenRead(zip);
        var names = archive.Entries.Select(e => e.FullName.Replace('\\', '/')).ToHashSet();

        // 壳启动器 + version.ini 在根，其余都在 Sparxie/app-<版本>/ 下。
        Assert.Contains("Sparxie/Sparxie.exe", names);
        Assert.Contains("Sparxie/version.ini", names);
        var appPrefix = names.Where(n => n.StartsWith("Sparxie/app-", StringComparison.Ordinal))
            .Select(n => n[..(n.IndexOf('/', "Sparxie/".Length) + 1)])
            .FirstOrDefault();
        Assert.NotNull(appPrefix);

        foreach (var required in new[]
        {
            "Sparxie.Desktop.exe",
            "Sparxie.Broker.exe",
            "Sparxie.SessionHost.exe",
            "HoyoTouchCore.dll",
            "EndfieldCore.dll",
            "EndfieldRuntime.dll",
            "ZZZTouchCore.dll",
            "ZZZTouchRuntime.dll",
            "LICENSE",
            "THIRD-PARTY-NOTICES.md",
            "UPSTREAM-LICENSE-MIT.txt",
            "inih-LICENSE.txt",
            "RUNTIME-NOTICE.md",
        })
        {
            Assert.True(names.Contains(appPrefix + required), $"ZIP 缺少 {required}");
        }

        Assert.DoesNotContain(names, n => n.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void 发布ZIP不含凭据或私库痕迹()
    {
        var zip = FindZip();
        if (zip is null)
        {
            return;
        }

        var tmp = Path.Combine(Path.GetTempPath(), "sparxie-audit", Guid.NewGuid().ToString("N"));
        try
        {
            ZipFile.ExtractToDirectory(zip, tmp);
            var files = Directory.GetFiles(tmp, "*", SearchOption.AllDirectories);

            foreach (var file in files)
            {
                if (Path.GetExtension(file).Equals(".json", StringComparison.OrdinalIgnoreCase)
                    || Path.GetExtension(file).Equals(".md", StringComparison.OrdinalIgnoreCase)
                    || Path.GetExtension(file).Equals(".txt", StringComparison.OrdinalIgnoreCase)
                    || Path.GetExtension(file).Equals(".deps.json", StringComparison.OrdinalIgnoreCase))
                {
                    var content = File.ReadAllText(file);
                    Assert.DoesNotContain("PRIVATE_RUNTIME_TOKEN", content, StringComparison.OrdinalIgnoreCase);
                    Assert.DoesNotContain("ghp_", content, StringComparison.OrdinalIgnoreCase);
                    Assert.DoesNotContain("api.github.com/repos/", content, StringComparison.OrdinalIgnoreCase);
                }
            }
        }
        finally
        {
            try
            {
                Directory.Delete(tmp, recursive: true);
            }
            catch
            {
            }
        }
    }
}
