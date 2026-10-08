using System.ComponentModel;
using System.Security.Cryptography;
using System.Threading.Channels;

namespace SwiftCopy.Core;

public static class TransferEngine
{
    private sealed record WorkItem(string Source, string Destination, long Length);
    private sealed record DirectoryItem(string Source, string Destination);

    public static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    private static bool Within(string path, string parent) => path.Equals(parent, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(Path.EndsInDirectorySeparator(parent) ? parent : parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static bool HasSelectedAncestor(string path, HashSet<string> selectedPaths)
    {
        string? current = path;
        while (current != null)
        {
            if (selectedPaths.Contains(current)) return true;
            string? parent = Path.GetDirectoryName(current);
            if (parent == current) break;
            current = parent;
        }
        return false;
    }

    public static IReadOnlyList<string> Validate(TransferOptions options)
    {
        if (options.Workers is < 1 or > 32) throw new ArgumentException("병렬 작업 수는 1~32여야 합니다.");
        if (options.Sources.Count == 0) throw new ArgumentException("복사할 파일 또는 폴더를 추가하세요.");
        if (string.IsNullOrWhiteSpace(options.Destination)) throw new ArgumentException("대상 폴더를 선택하세요.");
        string destination = Normalize(options.Destination);
        EnsureNoLinks(destination);
        if (File.Exists(destination)) throw new ArgumentException("대상은 폴더여야 합니다.");
        var roots = options.Sources.Select(Normalize).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x.Length).ToList();
        var selected = new List<string>();
        var selectedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string source in roots)
        {
            bool isDirectory = Directory.Exists(source);
            if (!isDirectory && !File.Exists(source)) throw new FileNotFoundException("원본을 찾을 수 없습니다.", source);
            EnsureNoLinks(source);
            string? parent = Path.GetDirectoryName(source);
            if (parent != null && HasSelectedAncestor(parent, selectedDirectories)) continue;
            string name = Path.GetFileName(source);
            if (string.IsNullOrEmpty(name)) throw new ArgumentException("드라이브 전체 대신 드라이브 안의 폴더를 선택하세요.");
            string target = Path.Combine(destination, name);
            if (Within(destination, source) || target.Equals(source, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("대상은 원본 자신 또는 원본 폴더 내부일 수 없습니다.");
            if (!targets.Add(target)) throw new ArgumentException($"이름이 같은 원본이 있습니다: {name}. 각각 다른 대상에 전송하세요.");
            selected.Add(source);
            if (isDirectory) selectedDirectories.Add(source);
        }
        var selectedPaths = new HashSet<string>(selected, StringComparer.OrdinalIgnoreCase);
        foreach (string source in selected)
            if (HasSelectedAncestor(Path.Combine(destination, Path.GetFileName(source)), selectedPaths))
                throw new ArgumentException("대상 경로가 다른 원본과 겹칩니다.");
        return selected;
    }

    private static void EnsureNoLinks(string path)
    {
        for (string? current = path; current != null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException($"안전한 전송을 위해 링크/정션 경로는 지원하지 않습니다: {current}");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    public static async Task<TransferResult> RunAsync(TransferOptions options, TransferSession session, CancellationToken token = default)
    {
        var roots = Validate(options);
        string destination = Normalize(options.Destination);
        Directory.CreateDirectory(destination);
        session.InitializeWorkers(options.Workers);
        var queue = Channel.CreateBounded<WorkItem>(new BoundedChannelOptions(2048) { SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
        var directories = new List<DirectoryItem>();
        session.Log("START", destination, $"Workers={options.Workers}; Move={options.Move}; Verify={options.Verify || options.Move}; Conflict={options.Conflict}");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token);
        var runToken = linked.Token;
        var producer = Task.Run(async () =>
        {
            try
            {
                foreach (string root in roots)
                {
                    session.Wait(runToken);
                    string target = Path.Combine(destination, Path.GetFileName(root));
                    if (Directory.Exists(root))
                    {
                        // A whole directory can be renamed without reading its hundreds of thousands
                        // of entries. Existing target folders use the normal per-file merge pipeline.
                        bool renamed = false;
                        if (options.Move && !Directory.Exists(target) && !File.Exists(target))
                        {
                            try
                            {
                                EnsureNoLinks(root); EnsureNoLinks(target);
                                renamed = NativeFiles.TryRename(root, target, false);
                                if (renamed)
                                {
                                    Interlocked.Increment(ref session.MovedDirectories);
                                    session.Log("RENAME_DIRECTORY", root, target);
                                }
                            }
                            catch (Win32Exception ex) when (ex.NativeErrorCode is 80 or 183) { }
                            catch (Win32Exception ex) { session.Error(root, ex); continue; }
                        }
                        if (!renamed) await Walk(root, target);
                    }
                    else await Enqueue(new FileInfo(root), target);
                }
                session.ScanComplete = true;
            }
            finally { queue.Writer.TryComplete(); }

            async Task Enqueue(FileInfo file, string target)
            {
                session.Wait(runToken);
                long length = file.Length;
                Interlocked.Increment(ref session.FoundFiles);
                Interlocked.Add(ref session.FoundBytes, length);
                await queue.Writer.WriteAsync(new WorkItem(file.FullName, target, length), runToken);
            }
            async Task Walk(string root, string target)
            {
                var pending = new Stack<DirectoryItem>();
                pending.Push(new(root, target));
                while (pending.TryPop(out var dir))
                {
                    session.Wait(runToken);
                    try
                    {
                        EnsureNoLinks(dir.Source);
                        EnsureNoLinks(dir.Destination);
                        Directory.CreateDirectory(dir.Destination);
                        directories.Add(dir);
                        foreach (var entry in new DirectoryInfo(dir.Source).EnumerateFileSystemInfos())
                        {
                            session.Wait(runToken);
                            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                            {
                                Interlocked.Increment(ref session.Skipped);
                                session.Log("SKIP_LINK", entry.FullName);
                                continue;
                            }
                            string output = Path.Combine(dir.Destination, entry.Name);
                            if (entry is DirectoryInfo) pending.Push(new(entry.FullName, output));
                            else
                            {
                                try { await Enqueue((FileInfo)entry, output); }
                                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { session.Error(entry.FullName, ex); }
                            }
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { session.Error(dir.Source, ex); }
                }
            }
        }, runToken);

        var workers = Enumerable.Range(0, options.Workers).Select(id => Task.Factory.StartNew(() =>
        {
            try
            {
                while (queue.Reader.WaitToReadAsync(runToken).AsTask().GetAwaiter().GetResult())
                {
                    while (queue.Reader.TryRead(out var item))
                    {
                        session.BeginWorker(id, item.Source, item.Destination, item.Length);
                        session.Wait(runToken);
                        try
                        {
                            var state = Transfer(item, options, session, id, runToken);
                            session.FinishWorkerFile(id, state);
                        }
                        catch (OperationCanceledException)
                        {
                            session.SetWorkerState(id, TransferWorkerState.Cancelled);
                            throw;
                        }
                        catch (Exception ex)
                        {
                            session.Error(item.Source, ex);
                            session.FinishWorkerFile(id, TransferWorkerState.Failed);
                        }
                        finally { Interlocked.Add(ref session.ResolvedBytes, item.Length); }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                linked.Cancel();
                throw;
            }
            catch
            {
                session.SetWorkerState(id, TransferWorkerState.Failed);
                linked.Cancel();
                throw;
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();

        bool cancelled = false;
        try { await Task.WhenAll(workers.Append(producer)); }
        catch (OperationCanceledException) when (runToken.IsCancellationRequested) { cancelled = true; }
        finally { session.Watch.Stop(); }

        if (options.Move && !cancelled)
        {
            foreach (var dir in directories.AsEnumerable().Reverse())
            {
                if (token.IsCancellationRequested) { cancelled = true; break; }
                try
                {
                    EnsureNoLinks(dir.Source);
                    // Never recurse: skipped, failed, or newly-created files keep their parent intact.
                    if (!Directory.EnumerateFileSystemEntries(dir.Source).Any()) Directory.Delete(dir.Source, false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { session.Error(dir.Source, ex); }
            }
        }
        if (cancelled) session.CancelUnfinishedWorkers();
        var snapshot = session.Snapshot();
        session.Log(cancelled ? "CANCELLED" : "FINISHED", destination,
            $"Copied={snapshot.CopiedFiles}; Bytes={snapshot.CopiedBytes}; RenamedDirectories={snapshot.MovedDirectories}; Skipped={snapshot.Skipped}; Errors={snapshot.Errors}");
        session.FinishLog();
        return new(snapshot, cancelled);
    }

    private static TransferWorkerState Transfer(WorkItem item, TransferOptions options, TransferSession session,
        int workerId, CancellationToken token)
    {
        EnsureNoLinks(item.Source);
        EnsureNoLinks(item.Destination);
        if (File.Exists(item.Destination) && options.Conflict == ConflictPolicy.Skip)
        {
            Interlocked.Increment(ref session.Skipped);
            session.Log("SKIP_EXISTS", item.Source);
            return TransferWorkerState.Skipped;
        }
        string folder = Path.GetDirectoryName(item.Destination)!;
        if (options.Move)
        {
            session.SetWorkerState(workerId, TransferWorkerState.Moving);
            var moveState = TryFastMove(item, options, session, workerId, token);
            if (moveState.HasValue) return moveState.Value;
        }
        string temporary = Path.Combine(folder, $".swiftcopy-{Guid.NewGuid():N}.partial");
        long transferred = 0;
        try
        {
            using var source = NativeFiles.OpenSource(item.Source, options.Move);
            long length = source.Length;
            session.SetWorkerTotal(workerId, length);
            session.SetWorkerState(workerId, TransferWorkerState.Copying);
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    NativeFiles.Copy(item.Source, temporary, options.UnbufferedLargeFiles && length >= 256L * 1024 * 1024,
                        session, value =>
                        {
                            Interlocked.Add(ref session.InFlightBytes, value - transferred);
                            transferred = value;
                            session.SetWorkerProgress(workerId, value);
                        }, token);
                    break;
                }
                catch (Win32Exception ex) when (attempt < 2 && ex.NativeErrorCode is 32 or 33 or 64 or 121)
                {
                    RemoveTemporary(temporary);
                    Interlocked.Add(ref session.InFlightBytes, -transferred); transferred = 0;
                    session.SetWorkerProgress(workerId, 0);
                    session.Log("RETRY", item.Source, ex.Message);
                    if (token.WaitHandle.WaitOne(300 * (attempt + 1))) token.ThrowIfCancellationRequested();
                }
            }
            if (new FileInfo(temporary).Length != length) throw new IOException("복사한 파일의 크기가 일치하지 않습니다.");
            if (options.Verify || options.Move)
            {
                session.SetWorkerState(workerId, TransferWorkerState.Verifying);
                source.Position = 0;
                byte[] original = Hash(source, session, token);
                using var target = new FileStream(temporary, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
                if (!original.AsSpan().SequenceEqual(Hash(target, session, token))) throw new IOException("SHA-256 검증에 실패했습니다. 원본을 보존합니다.");
            }
            session.Wait(token);
            EnsureNoLinks(item.Destination);
            string committed = item.Destination;
            if (options.Conflict == ConflictPolicy.KeepBoth)
            {
                int suffix = 1;
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    try { File.Move(temporary, committed, false); break; }
                    catch (IOException) when (File.Exists(committed) || Directory.Exists(committed))
                    {
                        committed = Path.Combine(folder, $"{Path.GetFileNameWithoutExtension(item.Destination)} ({suffix++}){Path.GetExtension(item.Destination)}");
                        session.SetWorkerDestination(workerId, committed);
                    }
                }
            }
            else
            {
                try { File.Move(temporary, committed, options.Conflict == ConflictPolicy.Overwrite); }
                catch (IOException) when (options.Conflict == ConflictPolicy.Skip && File.Exists(committed))
                {
                    Interlocked.Increment(ref session.Skipped);
                    session.Log("SKIP_EXISTS", item.Source);
                    return TransferWorkerState.Skipped;
                }
            }
            // Cancellation after commit deliberately leaves the original in place.
            token.ThrowIfCancellationRequested();
            if (options.Move) NativeFiles.DeleteSource(source);
            Interlocked.Increment(ref session.CopiedFiles);
            Interlocked.Add(ref session.CopiedBytes, length);
            return TransferWorkerState.Completed;
        }
        finally
        {
            Interlocked.Add(ref session.InFlightBytes, -transferred);
            try { RemoveTemporary(temporary); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { session.Error(temporary, ex); }
        }
    }

    private static byte[] Hash(Stream stream, TransferSession session, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(1024 * 1024);
        try
        {
            int read;
            while (true)
            {
                session.Wait(token);
                read = stream.Read(buffer, 0, buffer.Length);
                if (read == 0) break;
                hash.AppendData(buffer, 0, read);
            }
            return hash.GetHashAndReset();
        }
        finally { System.Buffers.ArrayPool<byte>.Shared.Return(buffer); }
    }

    private static TransferWorkerState? TryFastMove(WorkItem item, TransferOptions options, TransferSession session,
        int workerId, CancellationToken token)
    {
        string target = item.Destination;
        int suffix = 1;
        while (true)
        {
            session.Wait(token);
            session.SetWorkerDestination(workerId, target);
            try
            {
                if (!NativeFiles.TryRename(item.Source, target, options.Conflict == ConflictPolicy.Overwrite)) return null;
                Interlocked.Increment(ref session.CopiedFiles);
                Interlocked.Add(ref session.CopiedBytes, item.Length);
                return TransferWorkerState.Completed;
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode is 80 or 183)
            {
                if (options.Conflict == ConflictPolicy.Skip)
                {
                    Interlocked.Increment(ref session.Skipped); session.Log("SKIP_EXISTS", item.Source); return TransferWorkerState.Skipped;
                }
                if (options.Conflict != ConflictPolicy.KeepBoth) throw;
                target = Path.Combine(Path.GetDirectoryName(item.Destination)!, $"{Path.GetFileNameWithoutExtension(item.Destination)} ({suffix++}){Path.GetExtension(item.Destination)}");
            }
        }
    }

    private static void RemoveTemporary(string path)
    {
        if (!File.Exists(path)) return;
        File.SetAttributes(path, FileAttributes.Normal);
        File.Delete(path);
    }
}
