using System.Security.AccessControl;
using System.Security.Principal;
using Sparxie.Desktop.Services;
using Sparxie.LauncherCore;

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
    public void 目录可写时探测通过且不留探测文件()
    {
        var dir = Path.Combine(_root, "writable");

        Assert.True(AppPaths.CanWrite(dir));
        Assert.Empty(Directory.GetFiles(dir));
    }

    [Fact]
    public void 目录不可写时探测失败_不会悄悄换位置()
    {
        if (!OperatingSystem.IsWindows()) { return; }

        var dir = Deny("protected");

        Assert.False(AppPaths.CanWrite(dir));
        Assert.Empty(Directory.GetFileSystemEntries(dir));
    }

    [Fact]
    public void 数据目录始终是程序目录_配置文件在其下()
    {
        Assert.Equal(Sparxie.Infrastructure.Configuration.AppDataDirectory.Resolve(), AppPaths.DataRoot);
        Assert.Equal(Path.Combine(AppPaths.DataRoot, "config.json"), AppPaths.ConfigFilePath());
        Assert.StartsWith(AppPaths.DataRoot, AppPaths.LogsDirectory);
    }

    [Fact]
    public void 启动问题用同一条错误条显示()
    {
        Directory.CreateDirectory(_root);
        var data = new DesktopDataService(Path.Combine(_root, "config.json"));
        data.Load();
        var home = new Sparxie.Desktop.ViewModels.HomeViewModel(data, new LauncherService());

        home.ShowStartupProblem(new Sparxie.Desktop.ViewModels.LaunchErrorInfo("程序目录不可写", "请以管理员身份运行", AppPaths.DataRoot));

        Assert.True(home.HasLaunchError);
        Assert.Contains("不可写", home.LaunchError!.Title);
    }
}
