using System.Text.Json;
using Sparxie.Contracts.Rpc;
using Xunit;

namespace Broker.Tests;

/// <summary>可选能力清单：Ping 报告的能力、包内 capabilities.json 与 RpcContract 必须是同一份。</summary>
public class CapabilityManifestTests
{
    private static string RepoRoot()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && root.Name != "Sparxie")
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        return root.FullName;
    }

    [Fact]
    public void 能力名称非空且互不重复()
    {
        Assert.All(RpcContract.Capabilities, c => Assert.False(string.IsNullOrWhiteSpace(c)));
        Assert.Equal(RpcContract.Capabilities.Count, RpcContract.Capabilities.Distinct().Count());
    }

    [Fact]
    public void 包内capabilities_json与契约一致()
    {
        var path = Path.Combine(RepoRoot(), "build", "capabilities.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(1, document.RootElement.GetProperty("schema").GetInt32());
        var listed = document.RootElement.GetProperty("capabilities").EnumerateArray().Select(e => e.GetString()).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        Assert.Equal(RpcContract.Capabilities.OrderBy(x => x, StringComparer.Ordinal).ToArray(), listed);
    }
}
