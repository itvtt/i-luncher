using System.Buffers;
using System.IO;
using System.Threading;

namespace FolderCompareWPF;

internal static partial class FilePairComparer
{
    private const int BufferSize = 128 * 1024;
    internal const int SmallFileBytes = 256 * 1024;
    private const long MaxTextBytes = 16L * 1024 * 1024;

    public static FileContentComparison Compare(string pathA, string pathB, bool ignoreWhitespace, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var a = Open(pathA);
        using var b = Open(pathB);
        long lengthA = a.Length, lengthB = b.Length;
        // Small files are read once. Different text used to be stat'ed and opened again
        // after the byte comparison, repeating most of the work for small source files.
        if (Math.Max(lengthA, lengthB) <= SmallFileBytes)
            return CompareSmallFiles(a, b, checked((int)lengthA), checked((int)lengthB), ignoreWhitespace, cancellationToken);
        if (lengthA == lengthB && SameBytes(a, b, cancellationToken)) return FileContentComparison.Identical();
        if (Math.Max(lengthA, lengthB) > MaxTextBytes) return FileContentComparison.BinaryDifferent();
        a.Position = 0;
        b.Position = 0;
        return FileContentComparer.Compare(ReadTextBytes(a, cancellationToken), ReadTextBytes(b, cancellationToken), ignoreWhitespace);
    }

    private static FileContentComparison CompareSmallFiles(FileStream a, FileStream b, int lengthA, int lengthB,
        bool ignoreWhitespace, CancellationToken cancellationToken)
    {
        byte[] bufferA = ArrayPool<byte>.Shared.Rent(Math.Max(1, lengthA));
        byte[] bufferB = ArrayPool<byte>.Shared.Rent(Math.Max(1, lengthB));
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            a.ReadExactly(bufferA.AsSpan(0, lengthA));
            cancellationToken.ThrowIfCancellationRequested();
            b.ReadExactly(bufferB.AsSpan(0, lengthB));
            if (bufferA.AsSpan(0, lengthA).SequenceEqual(bufferB.AsSpan(0, lengthB)))
                return FileContentComparison.Identical();
            return FileContentComparer.Compare(bufferA.AsSpan(0, lengthA), bufferB.AsSpan(0, lengthB), ignoreWhitespace);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(bufferA);
            ArrayPool<byte>.Shared.Return(bufferB);
        }
    }

    private static FileStream Open(string path) => new(path, new FileStreamOptions
    {
        Mode = FileMode.Open, Access = FileAccess.Read, Share = FileShare.Read,
        BufferSize = 1, Options = FileOptions.SequentialScan
    });

    private static byte[] ReadTextBytes(FileStream stream, CancellationToken cancellationToken)
    {
        if (stream.Length > MaxTextBytes) throw new IOException("비교 중 파일 크기가 변경되었습니다.");
        byte[] bytes = new byte[checked((int)stream.Length)];
        for (int offset = 0; offset < bytes.Length;)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int size = Math.Min(BufferSize, bytes.Length - offset);
            stream.ReadExactly(bytes.AsSpan(offset, size));
            offset += size;
        }
        return bytes;
    }

    private static bool SameBytes(FileStream a, FileStream b, CancellationToken cancellationToken)
    {
        byte[] bufferA = ArrayPool<byte>.Shared.Rent(BufferSize), bufferB = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int countA = a.ReadAtLeast(bufferA.AsSpan(0, BufferSize), BufferSize, throwOnEndOfStream: false);
                int countB = b.ReadAtLeast(bufferB.AsSpan(0, BufferSize), BufferSize, throwOnEndOfStream: false);
                if (countA != countB || !bufferA.AsSpan(0, countA).SequenceEqual(bufferB.AsSpan(0, countB))) return false;
                if (countA == 0) return true;
            }
        }
        finally { ArrayPool<byte>.Shared.Return(bufferA); ArrayPool<byte>.Shared.Return(bufferB); }
    }
}
