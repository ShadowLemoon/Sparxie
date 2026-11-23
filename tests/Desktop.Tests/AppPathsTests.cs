using System.Security.AccessControl;
using System.Security.Principal;
using Sparxie.Desktop.Services;

namespace Desktop.Tests;

/// <summary>用户数据根目录：程序目录可写就用它；程序目录不可写（受保护目录）时回退到 AppData，配置/日志/缓存都要能写。</summary>
public sealed class AppPathsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sparxie-apppaths-" + Guid.NewGuid().ToString("N"));
    private readonly List<(string Path, FileSystemAccessRule Rule)> _denied = [];

    public void Dispose()
    {
        if (OperatingSystem.IsWindows())
        {
            foreach (var (path, rule) in _denied)
            {
                var security = new DirectoryInfo(path).GetAccessControl();
                security.RemoveAccessRuleAll(rule);
                new DirectoryInfo(path).SetAccessControl(security);
            }
        }

        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private string Deny(string name)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        var security = new DirectoryInfo(dir).GetAccessControl();
        var rule = new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.CreateDirectories | FileSystemRights.CreateFiles,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Deny);
        security.AddAccessRule(rule);
        new DirectoryInfo(dir).SetAccessControl(security);
        _denied.Add((dir, rule));
        return dir;
    }

    [Fact]
    public void 程序目录可写时用程序目录()
    {
        var install = Path.Combine(_root, "install");
        Directory.CreateDirectory(install);

        Assert.Equal(install, AppPaths.ResolveRoot(install, Path.Combine(_root, "appdata")));
        Assert.Empty(Directory.GetFiles(install)); // 探测文件已删除
    }

    [Fact]
    public void 程序目录不可写时回退到AppData下的Sparxie()
    {
        if (!OperatingSystem.IsWindows()) { return; }

        var install = Deny("protected");
        var appData = Path.Combine(_root, "appdata");

        var resolved = AppPaths.ResolveRoot(install, appData);

        Assert.Equal(Path.Combine(appData, "Sparxie"), resolved);
    }
}
