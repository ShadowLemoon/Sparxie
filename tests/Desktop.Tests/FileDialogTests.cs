using Sparxie.Desktop.Services;

namespace Desktop.Tests;

public sealed class FileDialogTests
{
    private static T OnStaThread<T>(Func<T> work)
    {
        T result = default!;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = work();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            throw new Xunit.Sdk.XunitException(failure.ToString());
        }

        return result;
    }

    [Fact]
    public void OptionsOfEachKind()
    {
        const uint forceFileSystem = 0x40, pathMustExist = 0x800, fileMustExist = 0x1000, pickFolders = 0x20, overwritePrompt = 0x2;
        var open = FileDialog.OptionsFor(FileDialogKind.Open);
        Assert.Equal(forceFileSystem | pathMustExist | fileMustExist, open & (forceFileSystem | pathMustExist | fileMustExist));
        Assert.Equal(0u, open & pickFolders);
        var save = FileDialog.OptionsFor(FileDialogKind.Save);
        Assert.NotEqual(0u, save & overwritePrompt);
        Assert.Equal(0u, save & fileMustExist);
        Assert.NotEqual(0u, FileDialog.OptionsFor(FileDialogKind.Folder) & pickFolders);
    }

    [Fact]
    public void OnlyAnExistingFolderIsPassedOn()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sparxie-fd-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            Assert.Equal(dir, FileDialog.UsableFolder(dir));
            Assert.Equal(dir, FileDialog.FolderOf(Path.Combine(dir, "YuanShen.exe")));
            Assert.Null(FileDialog.UsableFolder(Path.Combine(dir, "missing")));
            Assert.Null(FileDialog.FolderOf(Path.Combine(dir, "missing", "a.exe")));
            Assert.Null(FileDialog.UsableFolder(""));
            Assert.Null(FileDialog.UsableFolder(null));
            Assert.Null(FileDialog.FolderOf(""));
            Assert.Null(FileDialog.FolderOf("a\0b"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // Smoke test of the COM wrapper: the real IFileOpenDialog / IFileSaveDialog objects are created and configured (options, title, file types, start folder, file name),
    // and read back. Nothing is shown, so it needs no desktop session and never touches the person using the machine.
    [Theory]
    [InlineData(FileDialogKind.Open)]
    [InlineData(FileDialogKind.Save)]
    [InlineData(FileDialogKind.Folder)]
    public void TheComDialogIsCreatedAndConfiguredWithoutShowing(FileDialogKind kind)
    {
        var dir = Path.Combine(Path.GetTempPath(), "sparxie-fd-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var (options, index) = OnStaThread(() =>
            {
                using var dialog = FileDialog.Configure(kind, "选择游戏 EXE", [new FileDialogFilter("可执行文件 (*.exe)", "*.exe"), new FileDialogFilter("所有文件", "*.*")], dir, "game.exe");
                return (dialog.Options, dialog.FileTypeIndex);
            });
            var wanted = FileDialog.OptionsFor(kind);
            Assert.Equal(wanted, options & wanted);
            if (kind != FileDialogKind.Folder)
            {
                Assert.Equal(1u, index);
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void AStartFolderThatIsMissingDoesNotFailTheDialog()
    {
        var made = OnStaThread(() =>
        {
            using var dialog = FileDialog.Configure(FileDialogKind.Open, null, [new FileDialogFilter("可执行文件 (*.exe)", "*.exe")], @"Z:\no\such\folder", null);
            return dialog.Options;
        });
        Assert.NotEqual(0u, made);
    }
}
