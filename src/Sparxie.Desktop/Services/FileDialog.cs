using System.Runtime.InteropServices;

namespace Sparxie.Desktop.Services;

/// <summary>One entry of the file-type drop-down: a name the person reads and the pattern(s) it matches, for example ("可执行文件 (*.exe)", "*.exe").</summary>
public sealed record FileDialogFilter(string Name, string Pattern);

public enum FileDialogKind
{
    Open,
    Save,
    Folder,
}

/// <summary>
/// The Win32 common item dialogs (IFileOpenDialog / IFileSaveDialog), owned by a window handle.
/// Sparxie.Desktop can run elevated, and the WinRT pickers (Windows.Storage.Pickers) do not work in an elevated process;
/// these COM dialogs do, the way Collapse's Hi3Helper.Win32 FileDialog wrapper uses them.
/// Call from the window's UI thread (a single-threaded apartment). The dialogs are modal and return when the person picks or cancels.
/// </summary>
public static class FileDialog
{
    private const int ErrorCancelled = unchecked((int)0x800704C7);  // HRESULT_FROM_WIN32(ERROR_CANCELLED): the person closed the dialog

    private const uint FosOverwritePrompt = 0x00000002;
    private const uint FosNoChangeDir = 0x00000008;
    private const uint FosPickFolders = 0x00000020;
    private const uint FosForceFileSystem = 0x00000040;
    private const uint FosPathMustExist = 0x00000800;
    private const uint FosFileMustExist = 0x00001000;

    private const uint SigdnFileSysPath = 0x80058000;

    private static readonly Guid ClsidFileOpenDialog = new("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7");
    private static readonly Guid ClsidFileSaveDialog = new("C0B4E2F3-BA21-4773-8DBA-335EC946EB8B");
    private static readonly Guid IidShellItem = new("43826D1E-E718-42EE-BC55-A1E261C37BFE");

    /// <summary>Picks one existing file. Null when the person cancels.</summary>
    public static string? PickFile(nint owner, string? title, IReadOnlyList<FileDialogFilter> filters, string? initialFolder = null) =>
        Run(FileDialogKind.Open, owner, title, filters, initialFolder, null);

    /// <summary>Picks a file name to write. Null when the person cancels.</summary>
    public static string? PickSaveFile(nint owner, string? title, IReadOnlyList<FileDialogFilter> filters, string? initialFolder = null, string? fileName = null) =>
        Run(FileDialogKind.Save, owner, title, filters, initialFolder, fileName);

    /// <summary>Picks one folder. Null when the person cancels.</summary>
    public static string? PickFolder(nint owner, string? title, string? initialFolder = null) =>
        Run(FileDialogKind.Folder, owner, title, [], initialFolder, null);

    /// <summary>The option flags of a kind of dialog.</summary>
    public static uint OptionsFor(FileDialogKind kind) => kind switch
    {
        FileDialogKind.Open => FosForceFileSystem | FosPathMustExist | FosFileMustExist | FosNoChangeDir,
        FileDialogKind.Save => FosForceFileSystem | FosPathMustExist | FosOverwritePrompt | FosNoChangeDir,
        _ => FosForceFileSystem | FosPathMustExist | FosPickFolders | FosNoChangeDir,
    };

    /// <summary>Only an existing folder is passed to the dialog; anything else leaves the dialog to start where Windows remembers.</summary>
    public static string? UsableFolder(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
        {
            return null;
        }

        try
        {
            return Directory.Exists(folder) ? folder : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>The folder of a file path when that folder exists, else null (the start folder of "pick the exe again").</summary>
    public static string? FolderOf(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return null;
        }

        try
        {
            return UsableFolder(Path.GetDirectoryName(filePath));
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// The dialog with everything set and nothing shown. <see cref="Run"/> uses it; the tests build one without showing it
    /// (the only part of the wrapper that is not a modal call).
    /// </summary>
    public static ConfiguredDialog Configure(FileDialogKind kind, string? title, IReadOnlyList<FileDialogFilter> filters, string? initialFolder, string? fileName)
    {
        var clsid = kind == FileDialogKind.Save ? ClsidFileSaveDialog : ClsidFileOpenDialog;
        var type = Type.GetTypeFromCLSID(clsid) ?? throw new InvalidOperationException("文件对话框不可用");
        var dialog = (IFileDialog)Activator.CreateInstance(type)!;
        try
        {
            dialog.GetOptions(out var current);  // keep the defaults Windows sets (for example the strict file types), add ours
            dialog.SetOptions(current | OptionsFor(kind));
            if (!string.IsNullOrWhiteSpace(title))
            {
                dialog.SetTitle(title);
            }

            if (kind != FileDialogKind.Folder && filters.Count > 0)
            {
                var specs = filters.Select(f => new ComdlgFilterSpec { Name = f.Name, Spec = f.Pattern }).ToArray();
                dialog.SetFileTypes((uint)specs.Length, specs);
                dialog.SetFileTypeIndex(1);
            }

            if (kind == FileDialogKind.Save && !string.IsNullOrWhiteSpace(fileName))
            {
                dialog.SetFileName(fileName);
            }

            if (UsableFolder(initialFolder) is { } folder)
            {
                var iid = IidShellItem;
                SHCreateItemFromParsingName(folder, IntPtr.Zero, ref iid, out var item);
                try
                {
                    dialog.SetFolder(item);
                }
                finally
                {
                    Marshal.ReleaseComObject(item);
                }
            }

            return new ConfiguredDialog(dialog);
        }
        catch
        {
            Marshal.ReleaseComObject(dialog);
            throw;
        }
    }

    private static string? Run(FileDialogKind kind, nint owner, string? title, IReadOnlyList<FileDialogFilter> filters, string? initialFolder, string? fileName)
    {
        using var configured = Configure(kind, title, filters, initialFolder, fileName);
        return configured.Show(owner);
    }

    /// <summary>A dialog that is ready to show. Disposing releases the COM object.</summary>
    public sealed class ConfiguredDialog : IDisposable
    {
        private IFileDialog? _dialog;

        internal ConfiguredDialog(IFileDialog dialog)
        {
            _dialog = dialog;
        }

        /// <summary>The option flags the dialog holds (the tests compare them with <see cref="OptionsFor"/>).</summary>
        public uint Options
        {
            get
            {
                Dialog.GetOptions(out var options);
                return options;
            }
        }

        public uint FileTypeIndex
        {
            get
            {
                Dialog.GetFileTypeIndex(out var index);
                return index;
            }
        }

        private IFileDialog Dialog => _dialog ?? throw new ObjectDisposedException(nameof(ConfiguredDialog));

        /// <summary>Shows the dialog modally over <paramref name="owner"/>. The chosen path, or null when the person cancels.</summary>
        public string? Show(nint owner)
        {
            var hr = Dialog.Show(owner);
            if (hr == ErrorCancelled)
            {
                return null;
            }

            Marshal.ThrowExceptionForHR(hr);
            Dialog.GetResult(out var item);
            try
            {
                item.GetDisplayName(SigdnFileSysPath, out var buffer);
                try
                {
                    return Marshal.PtrToStringUni(buffer);
                }
                finally
                {
                    Marshal.FreeCoTaskMem(buffer);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(item);
            }
        }

        public void Dispose()
        {
            if (_dialog is { } dialog)
            {
                _dialog = null;
                Marshal.ReleaseComObject(dialog);
            }
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string path, IntPtr bindContext, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItem item);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct ComdlgFilterSpec
    {
        [MarshalAs(UnmanagedType.LPWStr)]
        public string Name;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string Spec;
    }

    // IFileDialog (42f85136-db7e-439c-85f1-e4075d135fc8) with IModalWindow::Show first. IFileOpenDialog and IFileSaveDialog add methods after these 24, which are not called here;
    // the objects answer this interface for both. The order of the methods is the order of the vtable.
    [ComImport]
    [Guid("42F85136-DB7E-439C-85F1-E4075D135FC8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IFileDialog
    {
        [PreserveSig]
        int Show(IntPtr parent);

        void SetFileTypes(uint count, [MarshalAs(UnmanagedType.LPArray)] ComdlgFilterSpec[] filterSpec);

        void SetFileTypeIndex(uint index);

        void GetFileTypeIndex(out uint index);

        void Advise(IntPtr events, out uint cookie);

        void Unadvise(uint cookie);

        void SetOptions(uint options);

        void GetOptions(out uint options);

        void SetDefaultFolder([MarshalAs(UnmanagedType.Interface)] IShellItem item);

        void SetFolder([MarshalAs(UnmanagedType.Interface)] IShellItem item);

        void GetFolder([MarshalAs(UnmanagedType.Interface)] out IShellItem item);

        void GetCurrentSelection([MarshalAs(UnmanagedType.Interface)] out IShellItem item);

        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);

        void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string name);

        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);

        void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string text);

        void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string text);

        void GetResult([MarshalAs(UnmanagedType.Interface)] out IShellItem item);

        void AddPlace([MarshalAs(UnmanagedType.Interface)] IShellItem item, uint placement);

        void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string extension);

        void Close(int hr);

        void SetClientGuid(ref Guid guid);

        void ClearClientData();

        void SetFilter(IntPtr filter);
    }

    // IShellItem (43826d1e-e718-42ee-bc55-a1e261c37bfe)
    [ComImport]
    [Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IShellItem
    {
        void BindToHandler(IntPtr bindContext, ref Guid handler, ref Guid riid, out IntPtr result);

        void GetParent([MarshalAs(UnmanagedType.Interface)] out IShellItem parent);

        void GetDisplayName(uint form, out IntPtr name);

        void GetAttributes(uint mask, out uint attributes);

        void Compare([MarshalAs(UnmanagedType.Interface)] IShellItem other, uint hint, out int order);
    }
}
