using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SwiftCopy.Core;

internal static class NativeFiles
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileExW(string source, string destination, uint flags);

    internal static bool TryRename(string source, string destination, bool overwrite)
    {
        // COPY_ALLOWED is deliberately absent: cross-volume moves fall back to our verified pipeline.
        if (MoveFileExW(Extended(source), Extended(destination), overwrite ? 1u : 0u)) return true;
        int error = Marshal.GetLastWin32Error();
        if (error == 17) return false; // ERROR_NOT_SAME_DEVICE
        throw new Win32Exception(error);
    }

    internal delegate uint Progress(long total, long transferred, long streamSize, long streamTransferred,
        uint streamNumber, uint reason, IntPtr source, IntPtr destination, IntPtr data);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CopyFileExW(string source, string destination, Progress progress,
        IntPtr data, IntPtr cancel, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security,
        uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int infoClass,
        ref int info, uint size);

    internal static string Extended(string path) => path.StartsWith(@"\\?\", StringComparison.Ordinal)
        ? path : path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;

    // Deny writers/deletion until the committed copy has been verified. DELETE access lets
    // move mode delete the exact opened source, without a close-and-delete path race.
    internal static FileStream OpenSource(string path, bool move)
    {
        var handle = CreateFileW(Extended(path), 0x80000000u | (move ? 0x10000u : 0u), 1,
            IntPtr.Zero, 3, 0x08000000, IntPtr.Zero);
        if (handle.IsInvalid) { int error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(error); }
        return new FileStream(handle, FileAccess.Read, 1024 * 1024);
    }

    internal static void DeleteSource(FileStream stream)
    {
        int delete = 1;
        if (!SetFileInformationByHandle(stream.SafeFileHandle, 4, ref delete, sizeof(int)))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    internal static void Copy(string source, string destination, bool unbuffered,
        TransferSession session, Action<long> bytes, CancellationToken token)
    {
        Progress callback = (_, transferred, _, _, _, _, _, _, _) =>
        {
            try { session.Wait(token); bytes(transferred); return token.IsCancellationRequested ? 1u : 0u; }
            catch (OperationCanceledException) { return 1; }
        };
        uint flags = 1u | (unbuffered ? 0x1000u : 0u);
        if (!CopyFileExW(Extended(source), Extended(destination), callback, IntPtr.Zero, IntPtr.Zero, flags))
        {
            int error = Marshal.GetLastWin32Error();
            token.ThrowIfCancellationRequested();
            throw new Win32Exception(error);
        }
        GC.KeepAlive(callback);
        token.ThrowIfCancellationRequested();
    }
}
