using System.Security.AccessControl;
using System.Security.Principal;
using Sparxie.Desktop.ViewModels;
using Sparxie.Infrastructure.Plugins;

namespace Desktop.Tests;

/// <summary>数据目录写不进去（装在受保护目录）时，导入一开始就给出权限提示，而不是解压完才在最后一步失败。</summary>
public sealed class PluginImportDeniedTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sparxie-import-denied-" + Guid.NewGuid().ToString("N"));
    private FileSystemAccessRule? _deny;

    public void Dispose()
    {
        if (_deny is not null && OperatingSystem.IsWindows())
        {
            var security = new DirectoryInfo(_root).GetAccessControl();
            security.RemoveAccessRuleAll(_deny);
            new DirectoryInfo(_root).SetAccessControl(security);
        }

        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void 数据目录不可写_一开始就提示权限_不解压不留暂存()
    {
        if (!OperatingSystem.IsWindows()) { return; }

        Directory.CreateDirectory(_root);
        var source = Path.Combine(Path.GetTempPath(), "sparxie-import-denied-src-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "plugin.json"),
            """{ "id": "ext.acme.denied", "displayName": "拒绝", "abiMajor": 1, "exeNames": ["D.exe"], "caps": [] }""");
        File.WriteAllText(Path.Combine(source, "module.dll"), "dll");

        var security = new DirectoryInfo(_root).GetAccessControl();
        _deny = new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.CreateDirectories | FileSystemRights.CreateFiles,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Deny);
        security.AddAccessRule(_deny);
        new DirectoryInfo(_root).SetAccessControl(security);

        var trust = new PluginTrustStore(Path.Combine(Path.GetTempPath(), "denied-trust-" + Guid.NewGuid().ToString("N") + ".json"));
        var vm = new PluginsViewModel(new PluginCatalog(Path.Combine(_root, "plugins"), trust), trust, _root);

        vm.ImportAsync(source).GetAwaiter().GetResult();

        Assert.Contains("没有权限", vm.Message);
        Assert.Contains("管理员", vm.Message);
        Assert.True(vm.ShowStandaloneMessage);
        Assert.Empty(vm.Items);
        try { Directory.Delete(source, recursive: true); } catch (IOException) { }
    }
}
