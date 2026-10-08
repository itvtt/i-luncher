using System.ComponentModel;
using System.Buffers;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DiskAtlas.Core;

internal static class WindowsFileInspector
{
    private const FileAttributes CloudOrRecallAttributes =
        (FileAttributes)0x00040000 |
        (FileAttributes)0x00100000 |
        (FileAttributes)0x00400000;

    private const FileAttributes UnsafeAttributes =
        FileAttributes.System |
        FileAttributes.ReparsePoint |
        FileAttributes.Offline |
        CloudOrRecallAttributes;

    private const int ErrorHandleEof = 38;
    private const int ErrorInvalidFunction = 1;
    private const int ErrorNotSupported = 50;

    internal sealed record Snapshot(
        long Size,
        DateTime LastWriteTimeUtc,
        string Identity,
        string CanonicalPath,
        FileAttributes Attributes,
        uint LinkCount);

    internal sealed class OpenFile : IDisposable
    {
        private readonly FileStream _stream;

        internal OpenFile(FileStream stream, Snapshot snapshot)
        {
            _stream = stream;
            Snapshot = snapshot;
        }

        internal Snapshot Snapshot { get; }
        internal Stream Stream => _stream;

        public void Dispose() => _stream.Dispose();
    }

    internal static OpenFile OpenEligible(string path, FileShare share)
    {
        string fullPath = NormalizeAndValidatePath(path);
        ValidateFreshPathPolicy(fullPath);

        var stream = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            share,
            bufferSize: 128 * 1024,
            FileOptions.SequentialScan);

        try
        {
            Snapshot snapshot = ReadSnapshot(stream.SafeFileHandle);
            ValidateSnapshot(fullPath, snapshot);
            ValidateFreshPathPolicy(snapshot.CanonicalPath);
            EnsureNoAlternateDataStreams(fullPath);
            return new OpenFile(stream, snapshot);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    internal static Snapshot RefreshSnapshot(OpenFile file) => ReadSnapshot(((FileStream)file.Stream).SafeFileHandle);

    internal static void EnsureMatches(
        OpenFile file,
        DuplicateFile expected,
        bool verifyHash,
        CancellationToken cancellationToken = default)
    {
        Snapshot current = RefreshSnapshot(file);
        ValidateSnapshot(expected.FullPath, current);
        ValidateFreshPathPolicy(expected.FullPath);
        ValidateFreshPathPolicy(current.CanonicalPath);
        EnsureNoAlternateDataStreams(expected.FullPath);

        if (current.Size != expected.Size ||
            current.LastWriteTimeUtc != expected.LastWriteTimeUtc ||
            !StringComparer.Ordinal.Equals(current.Identity, expected.Identity))
        {
            throw new IOException($"파일이 중복 검사 이후 변경되었습니다: {expected.FullPath}");
        }

        if (verifyHash)
        {
            string hash = ComputeSha256(file.Stream, cancellationToken);
            if (!StringComparer.Ordinal.Equals(hash, expected.ContentHash))
                throw new IOException($"파일 내용이 중복 검사 이후 변경되었습니다: {expected.FullPath}");
        }
    }

    internal static string ComputeSha256(Stream stream, CancellationToken cancellationToken)
    {
        stream.Position = 0;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
        try
        {
            using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(
                System.Security.Cryptography.HashAlgorithmName.SHA256);
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int read = stream.Read(buffer, 0, buffer.Length);
                if (read == 0)
                    break;
                hash.AppendData(buffer, 0, read);
            }
            return Convert.ToHexString(hash.GetHashAndReset());
        }
        finally
        {
            stream.Position = 0;
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    internal static async Task<string> ComputeSha256Async(Stream stream, CancellationToken cancellationToken)
    {
        stream.Position = 0;
        using var sha = System.Security.Cryptography.SHA256.Create();
        byte[] hash = await sha.ComputeHashAsync(stream, cancellationToken).ConfigureAwait(false);
        stream.Position = 0;
        return Convert.ToHexString(hash);
    }

    internal static string NormalizeAndValidatePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new IOException("완전한 파일 경로가 필요합니다.");

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new IOException("유효하지 않은 파일 경로입니다.", error);
        }

        if (fullPath.EndsWith(Path.DirectorySeparatorChar) || fullPath.EndsWith(Path.AltDirectorySeparatorChar))
            throw new IOException("파일 경로가 디렉터리를 가리킵니다.");

        return fullPath;
    }

    private static void ValidateFreshPathPolicy(string path)
    {
        string fullPath = NormalizeExtendedPath(path);
        string? current = fullPath;
        bool first = true;

        while (!string.IsNullOrEmpty(current))
        {
            FileAttributes attributes = File.GetAttributes(current);
            bool isVolumeRoot = IsVolumeRoot(current);
            var freshNode = new ScanNode
            {
                FullPath = current,
                Name = Path.GetFileName(current),
                IsDirectory = !first,
                Attributes = attributes
            };

            // Windows marks the volume root itself as System/Hidden. Those flags
            // describe the volume mount, not every descendant on the volume.
            FileAttributes unsafeAtThisLevel = isVolumeRoot
                ? UnsafeAttributes & ~FileAttributes.System
                : UnsafeAttributes;
            if ((!isVolumeRoot && SearchPolicy.IsExcluded(freshNode)) ||
                (attributes & unsafeAtThisLevel) != 0 ||
                (first && (attributes & FileAttributes.Directory) != 0))
                throw new UnauthorizedAccessException($"보호되거나 안전하지 않은 경로는 처리할 수 없습니다: {path}");

            string? parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || StringComparer.OrdinalIgnoreCase.Equals(parent, current))
                break;

            current = parent;
            first = false;
        }
    }

    private static bool IsVolumeRoot(string path)
    {
        string? root = Path.GetPathRoot(path);
        return !string.IsNullOrEmpty(root) &&
            StringComparer.OrdinalIgnoreCase.Equals(
                path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
    }

    private static void ValidateSnapshot(string path, Snapshot snapshot)
    {
        if ((snapshot.Attributes & (UnsafeAttributes | FileAttributes.Directory)) != 0)
            throw new UnauthorizedAccessException($"보호되거나 안전하지 않은 파일은 처리할 수 없습니다: {path}");
        if (snapshot.Size <= 0)
            throw new IOException($"빈 파일은 중복 삭제 대상이 아닙니다: {path}");
        if (snapshot.LinkCount != 1)
            throw new IOException($"하드 링크가 있는 파일은 중복 삭제 대상이 아닙니다: {path}");
    }

    private static Snapshot ReadSnapshot(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out BY_HANDLE_FILE_INFORMATION info))
            throw new Win32Exception(Marshal.GetLastWin32Error());

        long size = ((long)info.FileSizeHigh << 32) | info.FileSizeLow;
        long fileTime = ((long)info.LastWriteTimeHigh << 32) | info.LastWriteTimeLow;
        DateTime lastWrite = DateTime.FromFileTimeUtc(fileTime);
        string identity = $"{info.VolumeSerialNumber:X8}:{info.FileIndexHigh:X8}{info.FileIndexLow:X8}";
        string canonicalPath = GetCanonicalPath(handle);
        return new Snapshot(size, lastWrite, identity, canonicalPath, (FileAttributes)info.FileAttributes, info.NumberOfLinks);
    }

    private static string GetCanonicalPath(SafeFileHandle handle)
    {
        int capacity = 512;
        while (true)
        {
            var buffer = new char[capacity];
            uint length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Length, 0);
            if (length == 0)
                throw new Win32Exception(Marshal.GetLastWin32Error());
            if (length < buffer.Length)
                return NormalizeExtendedPath(new string(buffer, 0, checked((int)length)));
            capacity = checked((int)length + 1);
        }
    }

    private static string NormalizeExtendedPath(string path)
    {
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            return @"\\" + path[8..];
        if (path.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase))
            return path[4..];
        return path;
    }

    private static void EnsureNoAlternateDataStreams(string path)
    {
        IntPtr find = FindFirstStreamW(path, 0, out WIN32_FIND_STREAM_DATA data, 0);
        if (find == new IntPtr(-1))
        {
            int error = Marshal.GetLastWin32Error();
            if (error is ErrorHandleEof or ErrorInvalidFunction or ErrorNotSupported)
                return;
            throw new Win32Exception(error);
        }

        try
        {
            do
            {
                if (!StringComparer.OrdinalIgnoreCase.Equals(data.StreamName, "::$DATA"))
                    throw new IOException($"대체 데이터 스트림이 있는 파일은 처리할 수 없습니다: {path}");
            }
            while (FindNextStreamW(find, out data));

            int error = Marshal.GetLastWin32Error();
            if (error != ErrorHandleEof)
                throw new Win32Exception(error);
        }
        finally
        {
            FindClose(find);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BY_HANDLE_FILE_INFORMATION
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

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WIN32_FIND_STREAM_DATA
    {
        internal long StreamSize;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 296)]
        internal string StreamName;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle file,
        out BY_HANDLE_FILE_INFORMATION information);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(
        SafeFileHandle file,
        [Out] char[] path,
        uint pathLength,
        uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindFirstStreamW(
        string fileName,
        int infoLevel,
        out WIN32_FIND_STREAM_DATA findStreamData,
        uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindNextStreamW(IntPtr findStream, out WIN32_FIND_STREAM_DATA findStreamData);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindClose(IntPtr findFile);
}
