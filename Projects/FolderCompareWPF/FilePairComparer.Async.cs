using System.Buffers;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace FolderCompareWPF;

internal static partial class FilePairComparer
{
    internal const int FileConcurrency = 32;
    private const int LargeBufferSize = 1024 * 1024;
    private static readonly SemaphoreSlim TextSlots = new(4, 4);

    public static ValueTask<FileContentComparison> CompareDiscoveredAsync(string pathA, string pathB,
        bool smallFileHint, bool ignoreWhitespace, CancellationToken cancellationToken)
    {
        if (!smallFileHint) return CompareAsync(pathA, pathB, ignoreWhitespace, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        // Enumeration already supplied sizes. Avoid a separate metadata query or async tasks
        // for small files; validate real lengths on the opened handles before allocating.
        using (var a = Open(pathA))
        using (var b = Open(pathB))
        {
            long lengthA = a.Length, lengthB = b.Length;
            if (Math.Max(lengthA, lengthB) <= SmallFileBytes)
                return new(CompareSmallFiles(a, b, (int)lengthA, (int)lengthB, ignoreWhitespace, cancellationToken));
        }
        // The file may have grown since enumeration: preserve the large-file memory limit.
        return CompareAsync(pathA, pathB, ignoreWhitespace, cancellationToken);
    }

    public static async ValueTask<FileContentComparison> CompareAsync(string pathA, string pathB,
        bool ignoreWhitespace, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var a = OpenAsync(pathA);
        using var b = OpenAsync(pathB);
        long lengthA = a.Length, lengthB = b.Length;
        if (Math.Max(lengthA, lengthB) <= SmallFileBytes)
        {
            byte[] ba = ArrayPool<byte>.Shared.Rent(Math.Max(1, (int)lengthA));
            byte[] bb = ArrayPool<byte>.Shared.Rent(Math.Max(1, (int)lengthB));
            try
            {
                // Start both reads before awaiting; never return pooled buffers before both finish.
                await Task.WhenAll(a.ReadExactlyAsync(ba.AsMemory(0, (int)lengthA), cancellationToken).AsTask(),
                    b.ReadExactlyAsync(bb.AsMemory(0, (int)lengthB), cancellationToken).AsTask()).ConfigureAwait(false);
                if (ba.AsSpan(0, (int)lengthA).SequenceEqual(bb.AsSpan(0, (int)lengthB)))
                    return FileContentComparison.Identical();
                await TextSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return FileContentComparer.Compare(ba.AsSpan(0, (int)lengthA), bb.AsSpan(0, (int)lengthB), ignoreWhitespace);
                }
                finally { TextSlots.Release(); }
            }
            finally { ArrayPool<byte>.Shared.Return(ba); ArrayPool<byte>.Shared.Return(bb); }
        }
        if (lengthA == lengthB && await SameBytesAsync(a, b, cancellationToken).ConfigureAwait(false))
            return FileContentComparison.Identical();
        if (Math.Max(lengthA, lengthB) > MaxTextBytes) return FileContentComparison.BinaryDifferent();

        // 32 file reads may run at once, but only four large text decodes may allocate.
        await TextSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            a.Position = b.Position = 0;
            byte[] ba = new byte[checked((int)lengthA)], bb = new byte[checked((int)lengthB)];
            await Task.WhenAll(a.ReadExactlyAsync(ba, cancellationToken).AsTask(),
                b.ReadExactlyAsync(bb, cancellationToken).AsTask()).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return FileContentComparer.Compare(ba, bb, ignoreWhitespace);
        }
        finally { TextSlots.Release(); }
    }

    private static FileStream OpenAsync(string path) => new(path, new FileStreamOptions
    {
        Mode = FileMode.Open, Access = FileAccess.Read, Share = FileShare.Read,
        BufferSize = 1, Options = FileOptions.Asynchronous | FileOptions.SequentialScan
    });

    private static async ValueTask<bool> SameBytesAsync(FileStream a, FileStream b, CancellationToken cancellationToken)
    {
        byte[] ba = ArrayPool<byte>.Shared.Rent(LargeBufferSize), bb = ArrayPool<byte>.Shared.Rent(LargeBufferSize);
        try
        {
            while (true)
            {
                var readA = a.ReadAtLeastAsync(ba.AsMemory(0, LargeBufferSize), LargeBufferSize, false, cancellationToken).AsTask();
                var readB = b.ReadAtLeastAsync(bb.AsMemory(0, LargeBufferSize), LargeBufferSize, false, cancellationToken).AsTask();
                await Task.WhenAll(readA, readB).ConfigureAwait(false);
                int countA = readA.Result, countB = readB.Result;
                if (countA != countB || !ba.AsSpan(0, countA).SequenceEqual(bb.AsSpan(0, countB))) return false;
                if (countA == 0) return true;
            }
        }
        finally { ArrayPool<byte>.Shared.Return(ba); ArrayPool<byte>.Shared.Return(bb); }
    }
}
