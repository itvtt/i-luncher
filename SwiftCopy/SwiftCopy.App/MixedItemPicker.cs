using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace SwiftCopy.App;

/// <summary>Native Explorer dialog whose Add action accepts both files and folders.</summary>
[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
internal sealed class MixedItemPicker : IFileDialogEvents
{
    private IFileOpenDialog? dialog;
    private IntPtr handle;
    private readonly SubclassProc callback;
    private IReadOnlyList<string> selection = [];
    private bool accepting;
    private bool confirmed;
    private MixedItemPicker() => callback = WindowProc;

    public static IReadOnlyList<string> Show(Window owner)
    {
        var picker = new MixedItemPicker();
        return picker.ShowCore(new WindowInteropHelper(owner).Handle);
    }

    private IReadOnlyList<string> ShowCore(IntPtr owner)
    {
        dialog = (IFileOpenDialog)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7"))!)!;
        uint cookie = 0;
        bool advised = false;
        try
        {
            dialog.SetOptions(0x200 | 0x40 | 0x800 | 0x8 | 0x02000000); // multiselect, filesystem, existing path
            dialog.SetTitle("파일·폴더 선택 · Ctrl 키로 함께 선택");
            dialog.SetOkButtonLabel("추가");
            dialog.SetFileNameLabel("선택 항목:");
            dialog.Advise(this, out cookie); advised = true;
            int result = dialog.Show(owner);
            if (confirmed) return selection;
            if (result == unchecked((int)0x800704C7)) return []; // Cancel
            Marshal.ThrowExceptionForHR(result);
            return selection;
        }
        finally
        {
            if (handle != IntPtr.Zero) RemoveWindowSubclass(handle, callback, 1);
            if (advised) dialog.Unadvise(cookie);
            Marshal.FinalReleaseComObject(dialog);
            dialog = null;
            GC.KeepAlive(callback);
        }
    }

    private void HookWindow()
    {
        if (handle != IntPtr.Zero || dialog == null) return;
        ((IOleWindow)dialog).GetWindow(out var hwnd);
        if (hwnd != IntPtr.Zero && SetWindowSubclass(hwnd, callback, 1, 0)) handle = hwnd;
    }

    private bool ReadSelection()
    {
        if (dialog == null) return false;
        int hr = dialog.GetSelectedItems(out var items);
        if (hr < 0) return false;
        try
        {
            items.GetCount(out uint count);
            var paths = new List<string>();
            for (uint i = 0; i < count; i++)
            {
                items.GetItemAt(i, out var item);
                try
                {
                    item.GetDisplayName(0x80058000, out var name); // SIGDN_FILESYSPATH
                    try
                    {
                        string? path = Marshal.PtrToStringUni(name);
                        if (path == null || (!File.Exists(path) && !Directory.Exists(path))) return false;
                        paths.Add(path);
                    }
                    finally { Marshal.FreeCoTaskMem(name); }
                }
                finally { Marshal.ReleaseComObject(item); }
            }
            if (paths.Count == 0) return false;
            selection = paths;
            return true;
        }
        finally { Marshal.ReleaseComObject(items); }
    }

    private IntPtr WindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, nuint id, nuint data)
    {
        // The normal Open action navigates into selected folders. Intercept only the
        // dialog's explicit Add command; double-clicking the Explorer view still navigates.
        if (message == 0x111 && (wParam.ToInt64() & 0xffff) == 1 && !accepting)
        {
            accepting = true;
            try
            {
                if (ReadSelection())
                {
                    confirmed = true;
                    // Dismiss after capturing our own result. Letting the shell process
                    // IDOK would navigate into a selected folder instead of accepting it.
                    PostMessage(hwnd, 0x10, IntPtr.Zero, IntPtr.Zero);
                    return (IntPtr)1;
                }
            }
            catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException) { }
            finally { accepting = false; }
        }
        if (message == 0x82) { RemoveWindowSubclass(hwnd, callback, 1); handle = IntPtr.Zero; }
        return DefSubclassProc(hwnd, message, wParam, lParam);
    }

    public int OnFileOk(IFileOpenDialog current)
    {
        try { return ReadSelection() ? 0 : 1; }
        catch (COMException) { return 1; }
    }
    public int OnFolderChanging(IFileOpenDialog current, IShellItem folder) => 0;
    public int OnFolderChange(IFileOpenDialog current) { HookWindow(); return 0; }
    public int OnSelectionChange(IFileOpenDialog current) { HookWindow(); return 0; }
    public int OnShareViolation(IFileOpenDialog current, IShellItem item, out uint response) { response = 0; return 0; }
    public int OnTypeChange(IFileOpenDialog current) => 0;
    public int OnOverwrite(IFileOpenDialog current, IShellItem item, out uint response) { response = 0; return 0; }

    private delegate IntPtr SubclassProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, nuint id, nuint data);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("comctl32.dll")][return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(IntPtr hwnd, SubclassProc callback, nuint id, nuint data);
    [DllImport("comctl32.dll")][return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(IntPtr hwnd, SubclassProc callback, nuint id);
    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
}

[ComImport, Guid("D57C7288-D4AD-4768-BE02-9D969532D960"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IFileOpenDialog
{
    [PreserveSig] int Show(IntPtr owner);
    void SetFileTypes(uint count, IntPtr specs);
    void SetFileTypeIndex(uint index);
    void GetFileTypeIndex(out uint index);
    void Advise(IFileDialogEvents events, out uint cookie);
    void Unadvise(uint cookie);
    void SetOptions(uint options);
    void GetOptions(out uint options);
    void SetDefaultFolder(IShellItem folder);
    void SetFolder(IShellItem folder);
    void GetFolder(out IShellItem folder);
    void GetCurrentSelection(out IShellItem item);
    void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
    void GetFileName(out IntPtr name);
    void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
    void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string text);
    void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string text);
    void GetResult(out IShellItem item);
    void AddPlace(IShellItem item, uint alignment);
    void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string extension);
    void Close(int result);
    void SetClientGuid(ref Guid guid);
    void ClearClientData();
    void SetFilter(IntPtr filter);
    void GetResults(out IShellItemArray items);
    [PreserveSig] int GetSelectedItems(out IShellItemArray items);
}

[ComVisible(true), Guid("973510DB-7D7F-452B-8975-74A85828D354"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IFileDialogEvents
{
    [PreserveSig] int OnFileOk(IFileOpenDialog dialog);
    [PreserveSig] int OnFolderChanging(IFileOpenDialog dialog, IShellItem folder);
    [PreserveSig] int OnFolderChange(IFileOpenDialog dialog);
    [PreserveSig] int OnSelectionChange(IFileOpenDialog dialog);
    [PreserveSig] int OnShareViolation(IFileOpenDialog dialog, IShellItem item, out uint response);
    [PreserveSig] int OnTypeChange(IFileOpenDialog dialog);
    [PreserveSig] int OnOverwrite(IFileOpenDialog dialog, IShellItem item, out uint response);
}

[ComImport, Guid("00000114-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IOleWindow
{
    void GetWindow(out IntPtr handle);
    void ContextSensitiveHelp([MarshalAs(UnmanagedType.Bool)] bool enterMode);
}

[ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellItem
{
    void BindToHandler(IntPtr context, ref Guid handler, ref Guid iid, out IntPtr result);
    void GetParent(out IShellItem parent);
    void GetDisplayName(uint type, out IntPtr name);
    void GetAttributes(uint mask, out uint attributes);
    void Compare(IShellItem other, uint hint, out int order);
}

[ComImport, Guid("B63EA76D-1F85-456F-A19C-48159EFA858B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellItemArray
{
    void BindToHandler(IntPtr context, ref Guid handler, ref Guid iid, out IntPtr result);
    void GetPropertyStore(int flags, ref Guid iid, out IntPtr result);
    void GetPropertyDescriptionList(IntPtr key, ref Guid iid, out IntPtr result);
    void GetAttributes(uint flags, uint mask, out uint attributes);
    void GetCount(out uint count);
    void GetItemAt(uint index, out IShellItem item);
    void EnumItems(out IntPtr enumerator);
}
