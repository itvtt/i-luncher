using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace FolderCompareWPF;

public partial class MainWindow
{
    private static FileEnumerationResult GetAllFiles(string root, CancellationToken cancellationToken, Action fileDiscovered,
        Action<string, long>? fileFound = null)
    {
        const int workers = 8;
        using var directories = new BlockingCollection<string>();
        directories.Add(root);
        int outstanding = 1, skipped = 0;
        var files = new HashSet<string>[workers];
        string prefix = Path.GetFullPath(root);
        if (!Path.EndsInDirectorySeparator(prefix)) prefix += Path.DirectorySeparatorChar;
        var enumerationOptions = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = false,
            ReturnSpecialDirectories = false,
            AttributesToSkip = FileAttributes.System | FileAttributes.ReparsePoint,
            BufferSize = 64 * 1024
        };
        var tasks = Enumerable.Range(0, workers).Select(worker => Task.Factory.StartNew(() =>
        {
            var found = files[worker] = new HashSet<string>(RelativePathComparer);
            foreach (string directory in directories.GetConsumingEnumerable(cancellationToken))
            {
                try
                {
                    // FileSystemInfo attributes come from enumeration: no extra stat per path,
                    // and files + subdirectories are discovered with one directory pass.
                    foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos("*", enumerationOptions))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if ((entry.Attributes & FileAttributes.Directory) != 0)
                        {
                            Interlocked.Increment(ref outstanding);
                            directories.Add(entry.FullName, cancellationToken);
                        }
                        else
                        {
                            string relativePath = entry.FullName[prefix.Length..];
                            if (found.Add(relativePath))
                            {
                                fileDiscovered();
                                fileFound?.Invoke(relativePath, ((FileInfo)entry).Length);
                            }
                        }
                    }
                }
                catch (Exception ex) when (IsSkippableFileSystemException(ex)) { Interlocked.Increment(ref skipped); }
                finally
                {
                    if (Interlocked.Decrement(ref outstanding) == 0) directories.CompleteAdding();
                }
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
        try { Task.WaitAll(tasks); }
        catch (AggregateException) when (cancellationToken.IsCancellationRequested)
        { cancellationToken.ThrowIfCancellationRequested(); throw; }
        var result = new FileEnumerationResult { SkippedPathCount = skipped };
        foreach (var found in files) result.Files.UnionWith(found);
        return result;
    }
}
