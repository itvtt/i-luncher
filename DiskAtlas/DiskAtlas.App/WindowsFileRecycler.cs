using System.IO;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DiskAtlas.App;

/// <summary>
/// Sends one local file to the Windows Recycle Bin without ever permitting a
/// permanent-delete fallback.
/// </summary>
public sealed class WindowsFileRecycler : DiskAtlas.Core.IFileRecycler
{
    private readonly nint ownerWindow;
    private readonly Action<string>? recycledItemObserver;

    private const uint FileOperationFlags =
        0x0004       // FOF_SILENT
        | 0x0010     // FOF_NOCONFIRMATION
        | 0x0400     // FOF_NOERRORUI
        | 0x2000     // FOF_NO_CONNECTED_ELEMENTS
        | 0x00080000 // FOFX_RECYCLEONDELETE
        | 0x00100000 // FOFX_EARLYFAILURE
        | 0x20000000;// FOFX_ADDUNDORECORD

    // Shell's copy engine reports this documented success status for a recycled
    // leaf file because it has no child items to process.
    private const int CopyEngineSuccessDontProcessChildren = 0x00270008;

    public WindowsFileRecycler(nint ownerWindow = 0)
        : this(ownerWindow, null)
    {
    }

    internal WindowsFileRecycler(nint ownerWindow, Action<string>? recycledItemObserver)
    {
        this.ownerWindow = ownerWindow;
        this.recycledItemObserver = recycledItemObserver;
    }

    public Task RecycleAsync(
        string path,
        Action validateImmediatelyBeforeRecycle,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(validateImmediatelyBeforeRecycle);

        string fullPath = ValidateCandidate(path);
        cancellationToken.ThrowIfCancellationRequested();

        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                RecycleOnStaThread(
                    fullPath,
                    validateImmediatelyBeforeRecycle,
                    cancellationToken,
                    ownerWindow,
                    recycledItemObserver);
                completion.SetResult();
            }
            catch (OperationCanceledException exception)
            {
                completion.SetCanceled(exception.CancellationToken);
            }
            catch (Exception exception)
            {
                completion.SetException(exception);
            }
        })
        {
            IsBackground = true,
            Name = "DiskAtlas recycle operation"
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static void RecycleOnStaThread(
        string fullPath,
        Action validation,
        CancellationToken cancellationToken,
        nint ownerWindow,
        Action<string>? recycledItemObserver)
    {
        int initializeResult = NativeMethods.CoInitializeEx(
            0,
            NativeMethods.CoinitApartmentThreaded | NativeMethods.CoinitDisableOle1Dde);
        if (initializeResult < 0)
            Marshal.ThrowExceptionForHR(initializeResult);

        IFileOperation? operation = null;
        IShellItem? item = null;
        DeleteProgressSink? sink = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            operation = (IFileOperation)Activator.CreateInstance(
                Type.GetTypeFromCLSID(NativeMethods.ClsidFileOperation, throwOnError: true)!)!;

            ThrowUnlessOk(operation.SetOperationFlags(FileOperationFlags));
            if (ownerWindow != 0)
                ThrowUnlessOk(operation.SetOwnerWindow(ownerWindow));

            Guid shellItemId = typeof(IShellItem).GUID;
            ThrowUnlessOk(NativeMethods.SHCreateItemFromParsingName(
                fullPath,
                0,
                ref shellItemId,
                out item));

            sink = new DeleteProgressSink(
                fullPath,
                validation,
                cancellationToken,
                recycledItemObserver);
            ThrowUnlessOk(operation.DeleteItem(item, sink));
            int performResult = operation.PerformOperations();

            // PerformOperations can report success for an operation canceled by the
            // user or system, so the abort result must always be queried as well.
            int abortedResult = operation.GetAnyOperationsAborted(out bool aborted);

            sink.ThrowIfCallbackFailed();
            ThrowUnlessOk(performResult);
            ThrowUnlessOk(abortedResult);

            if (aborted)
                throw new IOException("Windows가 휴지통 작업을 취소했습니다. 파일은 삭제되지 않았습니다.");
            if (!sink.ValidationRan)
                throw new IOException("Windows가 삭제 직전 필수 검증을 실행하지 않았습니다.");
            sink.ThrowIfDeleteFailed();
            if (File.Exists(fullPath) || Directory.Exists(fullPath))
                throw new IOException("Windows가 파일을 휴지통으로 이동하지 못했습니다.");

            GC.KeepAlive(sink);
        }
        finally
        {
            sink?.Dispose();
            if (item is not null && Marshal.IsComObject(item))
                Marshal.FinalReleaseComObject(item);
            if (operation is not null && Marshal.IsComObject(operation))
                Marshal.FinalReleaseComObject(operation);
            NativeMethods.CoUninitialize();
        }
    }

    private static string ValidateCandidate(string path)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 2))
            throw new PlatformNotSupportedException("휴지통 전용 삭제에는 Windows 8 이상이 필요합니다.");
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("파일 경로가 필요합니다.", nameof(path));
        if (!Path.IsPathFullyQualified(path))
            throw new ArgumentException("휴지통 작업에는 정규화된 로컬 절대 경로가 필요합니다.", nameof(path));

        string fullPath = Path.GetFullPath(path);
        string? root = Path.GetPathRoot(fullPath);
        if (root is null
            || root.Length != 3
            || !char.IsAsciiLetter(root[0])
            || root[1] != ':'
            || root[2] != Path.DirectorySeparatorChar)
        {
            throw new NotSupportedException("로컬 드라이브의 파일만 안전하게 휴지통으로 이동할 수 있습니다.");
        }

        // Exclude alternate data streams. The operation is defined for a single
        // ordinary filesystem file, and an ADS cannot be recycled independently.
        if (fullPath.AsSpan(2).Contains(':'))
            throw new NotSupportedException("대체 데이터 스트림은 안전하게 휴지통으로 이동할 수 없습니다.");

        DriveInfo drive;
        try
        {
            drive = new DriveInfo(root);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException)
        {
            throw new NotSupportedException("파일이 있는 로컬 드라이브를 사용할 수 없습니다.", exception);
        }

        if (!drive.IsReady || drive.DriveType != DriveType.Fixed)
            throw new NotSupportedException("사용 가능한 로컬 고정 드라이브의 파일만 안전하게 휴지통으로 이동할 수 있습니다.");

        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(fullPath);
        }
        catch (FileNotFoundException)
        {
            throw;
        }
        catch (DirectoryNotFoundException)
        {
            throw new FileNotFoundException("휴지통으로 이동할 파일이 없습니다.", fullPath);
        }

        if ((attributes & FileAttributes.Directory) != 0)
            throw new NotSupportedException("이 휴지통 서비스는 폴더를 처리하지 않습니다.");
        if ((attributes & FileAttributes.ReparsePoint) != 0)
            throw new NotSupportedException("링크와 재분석 지점 파일은 안전하게 휴지통으로 이동할 수 없습니다.");
        if ((attributes & (FileAttributes.System | FileAttributes.Device)) != 0)
            throw new NotSupportedException("시스템 파일과 장치 파일은 안전하게 휴지통으로 이동할 수 없습니다.");
        if (DiskAtlas.Core.SearchPolicy.IsExcluded(new DiskAtlas.Core.ScanNode
            {
                FullPath = fullPath,
                Attributes = attributes
            }))
        {
            throw new NotSupportedException("보호된 시스템 경로의 파일은 휴지통으로 이동할 수 없습니다.");
        }

        return fullPath;
    }

    private static void ThrowUnlessOk(int result)
    {
        if (result == 0)
            return;

        Exception? error = Marshal.GetExceptionForHR(result);
        throw new IOException(
            $"Windows Shell 작업이 성공 상태(S_OK)를 반환하지 않았습니다. HRESULT: 0x{result:X8}",
            error);
    }

    // Kept as a separate, side-effect-free guard so the smoke checks can prove
    // that a shell callback lacking the recycle transfer bit fails closed.
    internal static void EnsureRecycleTransferFlag(uint transferFlags)
    {
        if ((transferFlags & 0x80) == 0) // TSF_DELETE_RECYCLE_IF_POSSIBLE
            throw new IOException("Windows가 휴지통 이동으로 표시하지 않은 삭제 작업을 차단했습니다.");
    }

    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    private sealed class DeleteProgressSink(
        string expectedPath,
        Action validation,
        CancellationToken cancellationToken,
        Action<string>? recycledItemObserver) : IFileOperationProgressSink, IDisposable
    {
        private ExceptionDispatchInfo? sinkFailure;
        private int? deleteResult;
        private bool recycledItemConfirmed;
        private SafeFileHandle? verifiedSourceHandle;
        private FileIdentity verifiedSourceIdentity;

        public bool ValidationRan { get; private set; }

        public void Dispose()
        {
            verifiedSourceHandle?.Dispose();
            verifiedSourceHandle = null;
        }

        public void ThrowIfCallbackFailed() => sinkFailure?.Throw();

        public void ThrowIfDeleteFailed()
        {
            if (deleteResult is null)
                throw new IOException("Windows가 휴지통 작업 결과를 보고하지 않았습니다.");
            if (deleteResult.Value is not 0 and not CopyEngineSuccessDontProcessChildren)
                throw new IOException(
                    $"Windows가 파일을 휴지통으로 이동하지 못했습니다. HRESULT: 0x{deleteResult.Value:X8}",
                    Marshal.GetExceptionForHR(deleteResult.Value));
            if (!recycledItemConfirmed)
                throw new IOException("Windows가 휴지통에 생성된 파일을 확인하지 못했습니다.");
        }

        public int StartOperations() => 0;
        public int FinishOperations(int result) => 0;
        public int PreRenameItem(uint flags, IShellItem item, string newName) => 0;
        public int PostRenameItem(uint flags, IShellItem item, string newName, int result, IShellItem? newItem) => 0;
        public int PreMoveItem(uint flags, IShellItem item, IShellItem destinationFolder, string? newName) => 0;
        public int PostMoveItem(uint flags, IShellItem item, IShellItem destinationFolder, string? newName, int result, IShellItem? newItem) => 0;
        public int PreCopyItem(uint flags, IShellItem item, IShellItem destinationFolder, string? newName) => 0;
        public int PostCopyItem(uint flags, IShellItem item, IShellItem destinationFolder, string? newName, int result, IShellItem? newItem) => 0;

        public int PreDeleteItem(uint flags, IShellItem item)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                EnsureRecycleTransferFlag(flags);
                string actualPath = GetFileSystemPath(item);
                if (!string.Equals(expectedPath, actualPath, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Windows가 다른 파일을 삭제 대상으로 선택하여 작업을 취소했습니다.");

                ValidateCandidate(expectedPath);
                SafeFileHandle sourceHandle = OpenIdentityHandle(expectedPath);
                verifiedSourceHandle = sourceHandle;
                verifiedSourceIdentity = ReadIdentity(sourceHandle);
                validation();
                cancellationToken.ThrowIfCancellationRequested();

                // Validation is allowed to perform expensive content checks. Reopen
                // the path afterwards so an atomic replacement during that callback
                // cannot proceed to the Shell operation unnoticed.
                using (SafeFileHandle currentPathHandle = OpenIdentityHandle(expectedPath))
                {
                    if (ReadIdentity(currentPathHandle) != verifiedSourceIdentity)
                        throw new IOException("검증 중 삭제 대상 경로의 파일이 바뀌어 휴지통 작업을 취소했습니다.");
                }

                ValidationRan = true;
                return 0;
            }
            catch (Exception exception)
            {
                sinkFailure = ExceptionDispatchInfo.Capture(exception);
                return unchecked((int)0x80004004); // E_ABORT
            }
        }

        public int PostDeleteItem(uint flags, IShellItem item, int result, IShellItem? newItem)
        {
            deleteResult = result;
            if (result < 0 || newItem is null)
                return 0;

            try
            {
                EnsureRecycleTransferFlag(flags);
                // Microsoft documents this argument as the newly-created item in
                // the Recycle Bin; it is null after a permanent deletion. Reading
                // its absolute parsing name also proves that the returned item is
                // usable while the completion callback is active.
                string parsingName = GetDisplayName(newItem, 0x80028000); // SIGDN_DESKTOPABSOLUTEPARSING
                if (string.IsNullOrWhiteSpace(parsingName))
                    throw new IOException("Windows가 휴지통에 생성된 파일을 식별하지 못했습니다.");

                string recycledPath = GetFileSystemPath(newItem);
                if (!File.Exists(recycledPath))
                    throw new IOException("휴지통으로 이동된 파일을 파일 시스템에서 확인하지 못했습니다.");
                if (verifiedSourceHandle is null || verifiedSourceHandle.IsInvalid || verifiedSourceHandle.IsClosed)
                    throw new IOException("검증한 원본 파일의 식별 정보를 유지하지 못했습니다.");

                using (SafeFileHandle recycledHandle = OpenIdentityHandle(recycledPath))
                {
                    if (ReadIdentity(recycledHandle) != verifiedSourceIdentity)
                        throw new IOException("휴지통으로 이동된 파일이 검증한 삭제 대상과 일치하지 않습니다.");
                }

                recycledItemObserver?.Invoke(recycledPath);
                recycledItemConfirmed = true;
                return 0;
            }
            catch (Exception exception)
            {
                sinkFailure ??= ExceptionDispatchInfo.Capture(exception);
                return unchecked((int)0x80004004); // E_ABORT
            }
        }

        public int PreNewItem(uint flags, IShellItem destinationFolder, string newName) => 0;
        public int PostNewItem(uint flags, IShellItem destinationFolder, string newName, string? templateName, uint fileAttributes, int result, IShellItem? newItem) => 0;
        public int UpdateProgress(uint workTotal, uint workSoFar) => 0;
        public int ResetTimer() => 0;
        public int PauseTimer() => 0;
        public int ResumeTimer() => 0;

        private static string GetFileSystemPath(IShellItem item)
            => GetDisplayName(item, 0x80058000); // SIGDN_FILESYSPATH

        private static SafeFileHandle OpenIdentityHandle(string path) => File.OpenHandle(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete,
            FileOptions.None);

        private static FileIdentity ReadIdentity(SafeFileHandle handle)
        {
            if (!NativeMethods.GetFileInformationByHandle(handle, out NativeMethods.ByHandleFileInformation info))
                throw new IOException(
                    "파일 식별 정보를 읽지 못했습니다.",
                    new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
            return new FileIdentity(info.VolumeSerialNumber, info.FileIndexHigh, info.FileIndexLow);
        }

        private static string GetDisplayName(IShellItem item, uint displayNameType)
        {
            ThrowUnlessOk(item.GetDisplayName(displayNameType, out nint value));
            try
            {
                return Marshal.PtrToStringUni(value)
                    ?? throw new IOException("Windows가 Shell 항목의 경로를 제공하지 않았습니다.");
            }
            finally
            {
                Marshal.FreeCoTaskMem(value);
            }
        }

        private readonly record struct FileIdentity(uint VolumeSerialNumber, uint FileIndexHigh, uint FileIndexLow);
    }

    private static class NativeMethods
    {
        internal const uint CoinitApartmentThreaded = 0x2;
        internal const uint CoinitDisableOle1Dde = 0x4;
        internal static readonly Guid ClsidFileOperation = new("3AD05575-8857-4850-9277-11B85BDB8E09");

        [DllImport("ole32.dll")]
        internal static extern int CoInitializeEx(nint reserved, uint coInit);

        [DllImport("ole32.dll")]
        internal static extern void CoUninitialize();

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
        internal static extern int SHCreateItemFromParsingName(
            string path,
            nint bindContext,
            ref Guid interfaceId,
            [MarshalAs(UnmanagedType.Interface)] out IShellItem item);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetFileInformationByHandle(
            SafeFileHandle file,
            out ByHandleFileInformation information);

        [StructLayout(LayoutKind.Sequential)]
        internal struct ByHandleFileInformation
        {
            internal uint FileAttributes;
            internal uint CreationTimeLow;
            internal uint CreationTimeHigh;
            internal uint LastAccessTimeLow;
            internal uint LastAccessTimeHigh;
            internal uint LastWriteTimeLow;
            internal uint LastWriteTimeHigh;
            internal uint VolumeSerialNumber;
            internal uint FileSizeHigh;
            internal uint FileSizeLow;
            internal uint NumberOfLinks;
            internal uint FileIndexHigh;
            internal uint FileIndexLow;
        }
    }
}

[ComImport]
[Guid("947AAB5F-0A5C-4C13-B4D6-4BF7836FC9F8")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IFileOperation
{
    [PreserveSig] int Advise(IFileOperationProgressSink sink, out uint cookie);
    [PreserveSig] int Unadvise(uint cookie);
    [PreserveSig] int SetOperationFlags(uint operationFlags);
    [PreserveSig] int SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string message);
    [PreserveSig] int SetProgressDialog(nint operationsProgressDialog);
    [PreserveSig] int SetProperties(nint propertyChangeArray);
    [PreserveSig] int SetOwnerWindow(nint ownerWindow);
    [PreserveSig] int ApplyPropertiesToItem(IShellItem item);
    [PreserveSig] int ApplyPropertiesToItems(nint items);
    [PreserveSig] int RenameItem(IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string newName, IFileOperationProgressSink? sink);
    [PreserveSig] int RenameItems(nint items, [MarshalAs(UnmanagedType.LPWStr)] string newName);
    [PreserveSig] int MoveItem(IShellItem item, IShellItem destinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string? newName, IFileOperationProgressSink? sink);
    [PreserveSig] int MoveItems(nint items, IShellItem destinationFolder);
    [PreserveSig] int CopyItem(IShellItem item, IShellItem destinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string? copyName, IFileOperationProgressSink? sink);
    [PreserveSig] int CopyItems(nint items, IShellItem destinationFolder);
    [PreserveSig] int DeleteItem(IShellItem item, IFileOperationProgressSink? sink);
    [PreserveSig] int DeleteItems(nint items);
    [PreserveSig] int NewItem(IShellItem destinationFolder, uint fileAttributes, [MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.LPWStr)] string? templateName, IFileOperationProgressSink? sink);
    [PreserveSig] int PerformOperations();
    [PreserveSig] int GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool anyOperationsAborted);
}

[ComVisible(true)]
[Guid("04B0F1A7-9490-44BC-96E1-4296A31252E2")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IFileOperationProgressSink
{
    [PreserveSig] int StartOperations();
    [PreserveSig] int FinishOperations(int result);
    [PreserveSig] int PreRenameItem(uint flags, IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string newName);
    [PreserveSig] int PostRenameItem(uint flags, IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string newName, int result, IShellItem? newItem);
    [PreserveSig] int PreMoveItem(uint flags, IShellItem item, IShellItem destinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string? newName);
    [PreserveSig] int PostMoveItem(uint flags, IShellItem item, IShellItem destinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string? newName, int result, IShellItem? newItem);
    [PreserveSig] int PreCopyItem(uint flags, IShellItem item, IShellItem destinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string? newName);
    [PreserveSig] int PostCopyItem(uint flags, IShellItem item, IShellItem destinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string? newName, int result, IShellItem? newItem);
    [PreserveSig] int PreDeleteItem(uint flags, IShellItem item);
    [PreserveSig] int PostDeleteItem(uint flags, IShellItem item, int result, IShellItem? newItem);
    [PreserveSig] int PreNewItem(uint flags, IShellItem destinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string newName);
    [PreserveSig] int PostNewItem(uint flags, IShellItem destinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string newName, [MarshalAs(UnmanagedType.LPWStr)] string? templateName, uint fileAttributes, int result, IShellItem? newItem);
    [PreserveSig] int UpdateProgress(uint workTotal, uint workSoFar);
    [PreserveSig] int ResetTimer();
    [PreserveSig] int PauseTimer();
    [PreserveSig] int ResumeTimer();
}

[ComImport]
[Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellItem
{
    [PreserveSig] int BindToHandler(nint bindContext, ref Guid handler, ref Guid interfaceId, out nint result);
    [PreserveSig] int GetParent(out IShellItem parent);
    [PreserveSig] int GetDisplayName(uint displayNameType, out nint name);
    [PreserveSig] int GetAttributes(uint mask, out uint attributes);
    [PreserveSig] int Compare(IShellItem other, uint hint, out int order);
}
