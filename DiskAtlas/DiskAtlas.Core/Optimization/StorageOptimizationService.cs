using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;

namespace DiskAtlas.Core.Optimization;

public sealed class StorageOptimizationService
{
    private const int MaximumRecursiveDirectoryDepth = 128;
    private readonly IReadOnlyDictionary<OptimizationCategory, string[]>? _testRoots;
    private readonly Func<DateTime> _utcNow;
    private readonly Action<string>? _beforeInspect;
    private readonly Action<string>? _beforeDelete;
    private readonly ConditionalWeakTable<OptimizationReport, PreviewEvidence> _previews = new();

    public StorageOptimizationService() => _utcNow = () => DateTime.UtcNow;

    // Fixture-only seam. Production callers can only use fixed, known-folder roots.
    internal StorageOptimizationService(IReadOnlyDictionary<OptimizationCategory, string[]> roots,
        Func<DateTime>? utcNow = null, Action<string>? beforeInspect = null, Action<string>? beforeDelete = null)
    {
        _testRoots = roots ?? throw new ArgumentNullException(nameof(roots));
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _beforeInspect = beforeInspect;
        _beforeDelete = beforeDelete;
    }

    public Task<OptimizationReport> AnalyzeAsync(
        OptimizationOptions options,
        IProgress<OptimizationProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => Run(options, progress, cancellationToken), CancellationToken.None);

    public Task<OptimizationReport> OptimizeAsync(
        OptimizationOptions options,
        IProgress<OptimizationProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        AnalyzeAsync(options, progress, cancellationToken);

    public Task<OptimizationReport> DeleteAsync(
        OptimizationReport preview,
        IReadOnlyCollection<string> selectedPaths,
        IProgress<OptimizationProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => DeleteSelected(preview, selectedPaths, progress, cancellationToken), CancellationToken.None);

    private OptimizationReport Run(OptimizationOptions options, IProgress<OptimizationProgress>? progress, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(options);
        var items = new List<OptimizationItemResult>();
        int candidates = 0, deleted = 0, skipped = 0, failed = 0, examined = 0;
        long candidateBytes = 0, deletedBytes = 0;
        bool canceled = false;
        var evidence = new Dictionary<string, CandidateSnapshot>(StringComparer.OrdinalIgnoreCase);
        var lastProgress = Environment.TickCount64;

        void Report(string stage, string path, bool force = false)
        {
            long now = Environment.TickCount64;
            if (!force && examined % 32 != 0 && now - lastProgress < 200) return;
            lastProgress = now;
            try
            {
                progress?.Report(new OptimizationProgress
                {
                    Stage = stage, CurrentPath = path, ExaminedFiles = examined,
                    DeletedFiles = deleted, DeletedBytes = deletedBytes
                });
            }
            catch { /* A UI progress sink must not interrupt filesystem handling. */ }
        }

        void Add(string path, OptimizationCategory category, long size, OptimizationItemStatus status, string reason,
            DateTime? lastWriteUtc = null)
        {
            items.Add(new OptimizationItemResult { Path = path, Category = category, SizeBytes = size,
                LastWriteTimeUtc = lastWriteUtc, Status = status, Reason = reason });
            if (status == OptimizationItemStatus.Skipped) skipped++;
            if (status == OptimizationItemStatus.Failed) failed++;
        }

        try
        {
            foreach ((OptimizationCategory category, string root, bool recursive) in GetRoots(options))
            {
                token.ThrowIfCancellationRequested();
                ProcessRoot(root, category, recursive);
            }
        }
        catch (OperationCanceledException) { canceled = true; }

        Report(canceled ? "\uCDE8\uC18C\uB428" : "\uC644\uB8CC", "", force: true);
        var report = new OptimizationReport
        {
            Items = items.AsReadOnly(), IsPreview = true, IsCanceled = canceled,
            CandidateCount = candidates, CandidateBytes = candidateBytes,
            DeletedCount = deleted, DeletedBytes = deletedBytes,
            SkippedCount = skipped, FailedCount = failed
        };
        _previews.Add(report, new PreviewEvidence(evidence));
        return report;

        IEnumerable<(OptimizationCategory Category, string Root, bool Recursive)> GetRoots(OptimizationOptions selected)
        {
            if (_testRoots is not null)
            {
                foreach (var pair in _testRoots)
                    if (pair.Key is OptimizationCategory.UserTemporary or OptimizationCategory.WindowsTemporary ||
                        pair.Key == OptimizationCategory.CrashDumps && selected.IncludeCrashDumps ||
                        pair.Key == OptimizationCategory.ErrorReports && selected.IncludeErrorReports)
                        foreach (string testRoot in pair.Value)
                            yield return (pair.Key, testRoot, IsTemporary(pair.Key)
                                ? selected.IncludeTemporarySubfolders
                                : pair.Key == OptimizationCategory.ErrorReports);
                yield break;
            }

            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string common = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (!string.IsNullOrWhiteSpace(local)) yield return (OptimizationCategory.UserTemporary, Path.Combine(local, "Temp"), selected.IncludeTemporarySubfolders);
            if (!string.IsNullOrWhiteSpace(windows)) yield return (OptimizationCategory.WindowsTemporary, Path.Combine(windows, "Temp"), selected.IncludeTemporarySubfolders);
            if (selected.IncludeCrashDumps)
            {
                if (!string.IsNullOrWhiteSpace(local)) yield return (OptimizationCategory.CrashDumps, Path.Combine(local, "CrashDumps"), false);
                if (!string.IsNullOrWhiteSpace(windows)) yield return (OptimizationCategory.CrashDumps, Path.Combine(windows, "Minidump"), false);
            }
            if (selected.IncludeErrorReports)
            {
                if (!string.IsNullOrWhiteSpace(local)) yield return (OptimizationCategory.ErrorReports, Path.Combine(local, "Microsoft", "Windows", "WER", "ReportArchive"), true);
                if (!string.IsNullOrWhiteSpace(common)) yield return (OptimizationCategory.ErrorReports, Path.Combine(common, "Microsoft", "Windows", "WER", "ReportArchive"), true);
            }
        }

        void ProcessRoot(string root, OptimizationCategory category, bool recursive)
        {
            token.ThrowIfCancellationRequested();
            if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(root))
            {
                Add(root ?? "", category, 0, OptimizationItemStatus.Skipped, "\uACBD\uB85C\uB97C \uD655\uC778\uD560 \uC218 \uC5C6\uC2B5\uB2C8\uB2E4.");
                return;
            }
            string fullRoot;
            try { fullRoot = Path.GetFullPath(root); }
            catch { Add(root, category, 0, OptimizationItemStatus.Skipped, "\uACBD\uB85C\uAC00 \uC62C\uBC14\uB974\uC9C0 \uC54A\uC2B5\uB2C8\uB2E4."); return; }

            if (!PinnedDirectoryChain.TryOpen(fullRoot, _beforeInspect, _beforeDelete, out var chain))
            {
                Add(fullRoot, category, 0, OptimizationItemStatus.Skipped, "\uC548\uC804\uD558\uAC8C \uC5F4 \uC218 \uC5C6\uB294 \uD3F4\uB354\uC785\uB2C8\uB2E4.");
                return;
            }
            using (chain)
            {
                if (!chain.RootExists)
                {
                    Add(fullRoot, category, 0, OptimizationItemStatus.Skipped, "\uD3F4\uB354\uAC00 \uC5C6\uC2B5\uB2C8\uB2E4.");
                    return;
                }
                Walk(fullRoot, chain, category, recursive, 0);
            }
        }

        void Walk(string directory, PinnedDirectoryChain chain, OptimizationCategory category, bool recursive, int depth)
        {
            token.ThrowIfCancellationRequested();
            IEnumerable<string> entries;
            try { entries = Directory.EnumerateFileSystemEntries(directory); }
            catch
            {
                Add(directory, category, 0, OptimizationItemStatus.Skipped, "\uD3F4\uB354\uB97C \uC77D\uC744 \uC218 \uC5C6\uC2B5\uB2C8\uB2E4.");
                return;
            }

            using IEnumerator<string> enumerator = entries.GetEnumerator();
            while (true)
            {
                bool hasNext;
                try { hasNext = enumerator.MoveNext(); }
                catch
                {
                    Add(directory, category, 0, OptimizationItemStatus.Skipped, "\uD3F4\uB354\uB97C \uC644\uC804\uD788 \uC77D\uC9C0 \uBABB\uD588\uC2B5\uB2C8\uB2E4.");
                    break;
                }
                if (!hasNext) break;
                string path = enumerator.Current;
                token.ThrowIfCancellationRequested();
                examined++;
                Report("\uAC80\uC0AC \uC911", path);
                if (IsSystemComponentExtension(path)) continue;
                FileAttributes attrs;
                try { attrs = File.GetAttributes(path); }
                catch { continue; }

                if ((attrs & FileAttributes.System) != 0) continue;

                if ((attrs & FileAttributes.Directory) != 0)
                {
                    if (!recursive) continue;
                    if (!IsSafeDirectoryAttributes(attrs))
                    {
                        Add(path, category, 0, OptimizationItemStatus.Skipped, "\uC548\uC804\uD55C \uC77C\uBC18 \uD3F4\uB354\uAC00 \uC544\uB2D9\uB2C8\uB2E4.");
                        continue;
                    }
                    if (depth >= MaximumRecursiveDirectoryDepth)
                    {
                        Add(path, category, 0, OptimizationItemStatus.Skipped, "\uD558\uC704 \uD3F4\uB354 \uC911\uCCA9 \uAE4A\uC774 \uD55C\uB3C4\uC5D0 \uB3C4\uB2EC\uD588\uC2B5\uB2C8\uB2E4.");
                        continue;
                    }
                    if (!chain.TryPush(path))
                    {
                        Add(path, category, 0, OptimizationItemStatus.Skipped, "\uD3F4\uB354\uAC00 \uBC14\uB00C\uC5C8\uAC70\uB098 \uC548\uC804\uD558\uAC8C \uACE0\uC815\uD560 \uC218 \uC5C6\uC2B5\uB2C8\uB2E4.");
                        continue;
                    }
                    try { Walk(path, chain, category, recursive, depth + 1); }
                    finally { chain.Pop(); }
                    continue;
                }

                if (!IsTargetExtension(path, category)) continue;
                bool protectedName = HasProtectedName(Path.GetFileName(path));
                if (protectedName && category is not (OptimizationCategory.UserTemporary or OptimizationCategory.WindowsTemporary))
                {
                    Add(path, category, 0, OptimizationItemStatus.Skipped, "\uBCF4\uD638 \uB300\uC0C1 \uC774\uB984\uC785\uB2C8\uB2E4.");
                    continue;
                }

                bool isTemporary = IsTemporary(category);
                DateTime cutoff = isTemporary ? DateTime.MinValue : _utcNow().ToUniversalTime() - MinimumAge(category);
                if (!TryInspectAndProcess(path, category, chain, cutoff, out long size, out bool eligible,
                    out CandidateSnapshot? snapshot, out string reason, out bool omit, out bool classified, out DateTime? lastWriteUtc))
                {
                    if (!omit && classified)
                        Add(path, category, size, OptimizationItemStatus.Skipped, reason, lastWriteUtc);
                    continue;
                }
                if (omit) continue;
                if (!eligible)
                {
                    Add(path, category, size, OptimizationItemStatus.Skipped, reason, lastWriteUtc);
                    continue;
                }

                string candidateKey = NormalizeKey(path);
                if (evidence.ContainsKey(candidateKey)) continue;
                candidates++;
                candidateBytes += size;
                if (snapshot is not null) evidence[candidateKey] = snapshot;
                Add(path, category, size, OptimizationItemStatus.Candidate,
                    protectedName ? "\uD30C\uC77C\uBA85\uC774 \uBCF4\uD638 \uD56D\uBAA9\uC73C\uB85C \uBCF4\uC77C \uC218 \uC788\uC5B4 \uC0AD\uC81C \uC804 \uD655\uC778\uD558\uC138\uC694." : "\uC0AD\uC81C \uAC00\uB2A5\uD55C \uD56D\uBAA9\uC785\uB2C8\uB2E4.", lastWriteUtc);
                Report("\uBBF8\uB9AC\uBCF4\uAE30", path);
            }
        }
    }

    private OptimizationReport DeleteSelected(OptimizationReport preview, IReadOnlyCollection<string> selectedPaths,
        IProgress<OptimizationProgress>? progress, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentNullException.ThrowIfNull(selectedPaths);
        if (!_previews.TryGetValue(preview, out PreviewEvidence? evidence))
            throw new ArgumentException("Preview was not created by this service instance.", nameof(preview));
        if (!preview.IsPreview || preview.IsCanceled)
            throw new ArgumentException("Only a completed preview can be deleted.", nameof(preview));

        var items = new List<OptimizationItemResult>();
        int candidates = 0, deleted = 0, skipped = 0, failed = 0, examined = 0;
        long candidateBytes = 0, deletedBytes = 0;
        bool canceled = false;
        var selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string? path in selectedPaths)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            try { selected.Add(NormalizeKey(path)); } catch { }
        }
        long lastProgress = Environment.TickCount64;
        try
        {
            foreach (string key in selected)
            {
                token.ThrowIfCancellationRequested();
                if (!evidence.Candidates.TryGetValue(key, out CandidateSnapshot? candidate)) continue;
                candidates++;
                candidateBytes += candidate.Size;
                examined++;
                bool removed = TryDeleteCandidate(candidate, out string reason, out bool omit);
                if (omit) continue;
                if (removed)
                {
                    deleted++;
                    deletedBytes += candidate.Size;
                    items.Add(new OptimizationItemResult { Path = candidate.Path, Category = candidate.Category, SizeBytes = candidate.Size,
                        LastWriteTimeUtc = candidate.Write, Status = OptimizationItemStatus.Deleted, Reason = "\uC601\uAD6C \uC0AD\uC81C\uD588\uC2B5\uB2C8\uB2E4." });
                }
                else
                {
                    skipped++;
                    items.Add(new OptimizationItemResult { Path = candidate.Path, Category = candidate.Category, SizeBytes = candidate.Size,
                        LastWriteTimeUtc = candidate.Write, Status = OptimizationItemStatus.Skipped, Reason = reason });
                }
                long now = Environment.TickCount64;
                if (now - lastProgress >= 200 || examined % 16 == 0 || examined == 1)
                {
                    lastProgress = now;
                    try { progress?.Report(new OptimizationProgress { Stage = "\uC0AD\uC81C \uC911", CurrentPath = candidate.Path, ExaminedFiles = examined, DeletedFiles = deleted, DeletedBytes = deletedBytes }); } catch { }
                }
            }
        }
        catch (OperationCanceledException) { canceled = true; }
        try { progress?.Report(new OptimizationProgress { Stage = canceled ? "\uCDE8\uC18C\uB428" : "\uC644\uB8CC", ExaminedFiles = examined, DeletedFiles = deleted, DeletedBytes = deletedBytes }); } catch { }
        return new OptimizationReport { Items = items.AsReadOnly(), IsPreview = false, IsCanceled = canceled,
            CandidateCount = candidates, CandidateBytes = candidateBytes, DeletedCount = deleted,
            DeletedBytes = deletedBytes, SkippedCount = skipped, FailedCount = failed };
    }

    private bool TryDeleteCandidate(CandidateSnapshot candidate, out string reason, out bool omit)
    {
        omit = true;
        reason = "\uD504\uB9AC\uBDF0 \uC774\uD6C4 \uD30C\uC77C \uC0C1\uD0DC\uAC00 \uBC14\uB00C\uC5C8\uC2B5\uB2C8\uB2E4.";
        if (!IsUnderRoot(candidate.Root, candidate.Path) ||
            !PinnedDirectoryChain.TryOpen(Path.GetDirectoryName(candidate.Path) ?? "", _beforeInspect, null, out var chain)) return false;
        using (chain)
        {
            if (!chain.SameIdentities(candidate.Directories) ||
                !NativePathAndFileMatch(candidate, chain, out SafeFileHandle handle, out Native.FileInfo info, out omit)) return false;
            omit = false;
            using (handle)
            {
                try { _beforeDelete?.Invoke(candidate.Path); }
                catch
                {
                    if (!Native.GetFileInformationByHandle(handle, out var afterHook) ||
                        (((FileAttributes)afterHook.FileAttributes) & FileAttributes.System) != 0) omit = true;
                    else omit = false;
                    reason = "\uC0AD\uC81C \uC9C1\uC804 \uC548\uC804 \uD655\uC778\uC5D0 \uC2E4\uD328\uD588\uC2B5\uB2C8\uB2E4.";
                    return false;
                }
                if (!Native.GetFileInformationByHandle(handle, out var latest)) { omit = true; return false; }
                if ((((FileAttributes)latest.FileAttributes) & FileAttributes.System) != 0) { omit = true; return false; }
                if (!Matches(candidate, latest) ||
                    !Native.PathMatchesHandle(handle, candidate.Path) ||
                    Native.ToUtc(latest.CreationTime) != candidate.Creation || Native.ToUtc(latest.LastWriteTime) != candidate.Write ||
                    Native.ToUtc(latest.LastAccessTime) != candidate.Access || latest.NumberOfLinks != 1) return false;
                if (!IsTemporary(candidate.Category))
                {
                    DateTime cutoff = _utcNow().ToUniversalTime() - MinimumAge(candidate.Category);
                    if (Native.ToUtc(latest.CreationTime) > cutoff || Native.ToUtc(latest.LastWriteTime) > cutoff ||
                        Native.ToUtc(latest.LastAccessTime) > cutoff) return false;
                }
                if (!Native.SetDeleteDisposition(handle)) { reason = "\uD30C\uC77C\uC744 \uC0AD\uC81C\uD558\uC9C0 \uBABB\uD588\uC2B5\uB2C8\uB2E4."; return false; }
                reason = "";
                return true;
            }
        }
    }

    private static bool NativePathAndFileMatch(CandidateSnapshot candidate, PinnedDirectoryChain chain,
        out SafeFileHandle handle, out Native.FileInfo info, out bool omit)
    {
        handle = null!;
        info = default;
        omit = true;
        try { chain.BeforeInspect?.Invoke(candidate.Path); } catch { return false; }
        var opened = Native.CreateFile(candidate.Path, Native.FileReadAttributes | Native.DeleteAccess, 0,
            IntPtr.Zero, Native.OpenExisting, Native.OpenReparsePoint | Native.SequentialScan, IntPtr.Zero);
        if (opened.IsInvalid) { opened.Dispose(); return false; }
        if (!Native.GetFileInformationByHandle(opened, out info)) { opened.Dispose(); return false; }
        if ((((FileAttributes)info.FileAttributes) & FileAttributes.System) != 0)
        { omit = true; opened.Dispose(); return false; }
        omit = false;
        if (!Native.HasOnlyDefaultDataStream(opened) || !Matches(candidate, info) ||
            !chain.ContainsPath(Path.GetDirectoryName(candidate.Path) ?? "") || !Native.PathMatchesHandle(opened, candidate.Path))
        { opened.Dispose(); return false; }
        handle = opened;
        return true;
    }

    private static bool Matches(CandidateSnapshot candidate, Native.FileInfo info) =>
        info.VolumeSerialNumber == candidate.Volume && info.FileIndexHigh == candidate.IndexHigh && info.FileIndexLow == candidate.IndexLow &&
        info.FileSizeHigh == (uint)(candidate.Size >> 32) && info.FileSizeLow == (uint)candidate.Size && info.FileAttributes == candidate.Attributes &&
        Native.ToUtc(info.CreationTime) == candidate.Creation && Native.ToUtc(info.LastWriteTime) == candidate.Write &&
        Native.ToUtc(info.LastAccessTime) == candidate.Access && info.NumberOfLinks == 1 &&
        (((FileAttributes)info.FileAttributes & ~(FileAttributes.Normal | FileAttributes.Archive | FileAttributes.Hidden |
            FileAttributes.Temporary | FileAttributes.NotContentIndexed)) == 0);

    private static TimeSpan MinimumAge(OptimizationCategory category) =>
        category is OptimizationCategory.UserTemporary or OptimizationCategory.WindowsTemporary ? TimeSpan.FromDays(7) : TimeSpan.FromDays(30);

    private static bool IsTemporary(OptimizationCategory category) =>
        category is OptimizationCategory.UserTemporary or OptimizationCategory.WindowsTemporary;

    private static bool IsSystemComponentExtension(string path)
    {
        string extension = Path.GetExtension(path);
        return extension.Equals(".sys", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".dll", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".drv", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".ocx", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".cpl", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".efi", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsUnderRoot(string root, string path)
    {
        string basePath = Path.GetFullPath(root).TrimEnd('\\') + "\\";
        string fullPath = Path.GetFullPath(path);
        return fullPath.StartsWith(basePath, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeKey(string path) => Path.GetFullPath(path).TrimEnd('\\');

    private static bool IsTargetExtension(string path, OptimizationCategory category)
    {
        string extension = Path.GetExtension(path);
        return category switch
        {
            OptimizationCategory.UserTemporary or OptimizationCategory.WindowsTemporary => true,
            OptimizationCategory.CrashDumps => extension.Equals(".dmp", StringComparison.OrdinalIgnoreCase) || extension.Equals(".mdmp", StringComparison.OrdinalIgnoreCase),
            OptimizationCategory.ErrorReports => extension.Equals(".wer", StringComparison.OrdinalIgnoreCase) || extension.Equals(".dmp", StringComparison.OrdinalIgnoreCase) || extension.Equals(".mdmp", StringComparison.OrdinalIgnoreCase) || extension.Equals(".hdmp", StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }

    private static bool HasProtectedName(string name)
    {
        if (name.StartsWith('~')) return true;
        string lower = name.ToLowerInvariant();
        string lettersOnly = new(lower.Where(char.IsLetterOrDigit).ToArray());
        return new[] { "autosave", "recovery", "recover", "backup", "install", "setup", "update", "office", "lock" }.Any(lettersOnly.Contains) || lower.Contains(".lck", StringComparison.Ordinal);
    }

    private static bool IsSafeDirectoryAttributes(FileAttributes value) =>
        (value & (FileAttributes.ReparsePoint | FileAttributes.Hidden | FileAttributes.System | FileAttributes.Offline)) == 0;

    private bool TryInspectAndProcess(string path, OptimizationCategory category, PinnedDirectoryChain chain, DateTime cutoff,
        out long size, out bool eligible, out CandidateSnapshot? snapshot, out string reason,
        out bool omit, out bool classified, out DateTime? lastWriteUtc)
    {
        size = 0; eligible = false; snapshot = null; reason = "\uD30C\uC77C\uC744 \uC548\uC804\uD558\uAC8C \uC5F4 \uC218 \uC5C6\uC2B5\uB2C8\uB2E4.";
        omit = true; classified = false; lastWriteUtc = null;
        if (path.Contains(':', StringComparison.Ordinal) && !IsDriveColonOnly(path)) { reason = "\uB300\uCCB4 \uB370\uC774\uD130 \uC2A4\uD2B8\uB9BC\uC740 \uAC74\uB108\uB700\uB2C8\uB2E4."; return false; }
        try { _beforeInspect?.Invoke(path); }
        catch
        {
            if (TryClassifyPathWithoutHandle(path, out DateTime? currentWrite))
            {
                omit = false; classified = true; lastWriteUtc = currentWrite;
                reason = "\uAC80\uC0AC \uC9C1\uC804\uC5D0 \uC548\uC804 \uD655\uC778\uC5D0 \uC2E4\uD328\uD588\uC2B5\uB2C8\uB2E4.";
            }
            return false;
        }
        using SafeFileHandle handle = Native.CreateFile(path, Native.FileReadAttributes | Native.FileReadData, 0,
            IntPtr.Zero, Native.OpenExisting, Native.OpenReparsePoint | Native.SequentialScan, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            if (TryClassifyPathWithoutHandle(path, out DateTime? currentWrite))
            {
                omit = false; classified = true; lastWriteUtc = currentWrite;
                reason = "\uC0AC\uC6A9 \uC911\uC774\uAC70\uB098 \uC811\uADFC \uAD8C\uD55C\uC774 \uC5C6\uB294 \uD30C\uC77C\uC785\uB2C8\uB2E4.";
            }
            return false;
        }
        if (!Native.GetFileInformationByHandle(handle, out var info)) return false;
        FileAttributes attrs = (FileAttributes)info.FileAttributes;
        if ((attrs & FileAttributes.System) != 0) { omit = true; return false; }
        omit = false; classified = true;
        size = ((long)info.FileSizeHigh << 32) | info.FileSizeLow;
        DateTime creation = Native.ToUtc(info.CreationTime), write = Native.ToUtc(info.LastWriteTime), access = Native.ToUtc(info.LastAccessTime);
        if (write != DateTime.MinValue) lastWriteUtc = write;
        if (!Native.HasOnlyDefaultDataStream(handle))
        { reason = "\uB300\uCCB4 \uB370\uC774\uD130 \uC2A4\uD2B8\uB9BC\uC744 \uD655\uC778\uD560 \uC218 \uC5C6\uC5B4 \uAC74\uB108\uB701\uB2C8\uB2E4."; return false; }
        if ((attrs & FileAttributes.Directory) != 0) { reason = "\uD3F4\uB354\uB294 \uC0AD\uC81C\uD558\uC9C0 \uC54A\uC2B5\uB2C8\uB2E4."; return false; }
        if ((attrs & FileAttributes.ReadOnly) != 0) { reason = "\uC77D\uAE30 \uC804\uC6A9 \uD30C\uC77C\uC785\uB2C8\uB2E4."; return false; }
        // These file attributes describe benign storage/indexing hints and do not weaken path safety.
        if ((attrs & ~(FileAttributes.Normal | FileAttributes.Archive | FileAttributes.Hidden |
            FileAttributes.Temporary | FileAttributes.NotContentIndexed)) != 0)
        { reason = "\uBCF4\uD638 \uC18D\uC131\uC774\uAC70\uB098 \uC54C \uC218 \uC5C6\uB294 \uC18D\uC131\uC785\uB2C8\uB2E4."; return false; }
        if (info.NumberOfLinks != 1) { reason = "\uD558\uB4DC \uB9C1\uD06C \uD30C\uC77C\uC740 \uAC74\uB108\uB700\uB2C8\uB2E4."; return false; }
        if (!chain.ContainsPath(Path.GetDirectoryName(path) ?? "") || !Native.PathMatchesHandle(handle, path))
        { reason = "\uD30C\uC77C \uACBD\uB85C \uB610\uB294 \uC0C1\uC704 \uD3F4\uB354\uAC00 \uBC14\uB00C\uC5C8\uC2B5\uB2C8\uB2E4."; return false; }
        if (creation == DateTime.MinValue || write == DateTime.MinValue || access == DateTime.MinValue ||
            (!IsTemporary(category) && (creation > cutoff || write > cutoff || access > cutoff)))
        { reason = "\uC0DD\uC131\u00B7\uC218\uC815\u00B7\uC811\uADFC \uC2DC\uAC01 \uC911 \uCD5C\uADFC \uD56D\uBAA9\uC785\uB2C8\uB2E4."; return true; }
        eligible = true;
        snapshot = new CandidateSnapshot(path, chain.RootPath, category, info.FileAttributes, size,
            info.VolumeSerialNumber, info.FileIndexHigh, info.FileIndexLow, creation, write, access, chain.Identities.ToArray());
        reason = "\uC624\uB798\uB41C \uD56D\uBAA9\uC785\uB2C8\uB2E4.";
        return true;

        static bool IsDriveColonOnly(string value) => value.Length >= 2 && char.IsLetter(value[0]) && value[1] == ':' && value.IndexOf(':', 2) < 0;
    }

    private static bool TryClassifyPathWithoutHandle(string path, out DateTime? lastWriteUtc)
    {
        lastWriteUtc = null;
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.System) != 0) return false;
            DateTime write = File.GetLastWriteTimeUtc(path);
            if (write != DateTime.MinValue) lastWriteUtc = DateTime.SpecifyKind(write, DateTimeKind.Utc);
            return true;
        }
        catch { return false; }
    }

    private sealed record PreviewEvidence(IReadOnlyDictionary<string, CandidateSnapshot> Candidates);
    private sealed record CandidateSnapshot(string Path, string Root, OptimizationCategory Category, uint Attributes, long Size,
        uint Volume, uint IndexHigh, uint IndexLow, DateTime Creation, DateTime Write, DateTime Access,
        DirectoryIdentity[] Directories);
    private readonly record struct DirectoryIdentity(string Path, uint Volume, uint IndexHigh, uint IndexLow);

    private sealed class PinnedDirectoryChain : IDisposable
    {
        private readonly List<(string Path, SafeFileHandle Handle, Native.FileInfo Info)> _handles = new();
        public Action<string>? BeforeInspect { get; init; }
        public Action<string>? BeforeDelete { get; init; }
        public bool RootExists { get; private set; }
        public string RootPath { get; private set; } = string.Empty;
        public DirectoryIdentity[] Identities => _handles.Select(x => new DirectoryIdentity(Normalize(x.Path), x.Info.VolumeSerialNumber, x.Info.FileIndexHigh, x.Info.FileIndexLow)).ToArray();

        public string RootPathFor(string _) => RootPath;

        public bool SameIdentities(IReadOnlyList<DirectoryIdentity> expected)
        {
            DirectoryIdentity[] current = Identities;
            return current.Length == expected.Count && current.SequenceEqual(expected);
        }

        public static bool TryOpen(string root, Action<string>? beforeInspect, Action<string>? beforeDelete, out PinnedDirectoryChain chain)
        {
            chain = new PinnedDirectoryChain { BeforeInspect = beforeInspect, BeforeDelete = beforeDelete };
            try
            {
                chain.RootPath = Path.GetFullPath(root);
                string? volume = Path.GetPathRoot(root);
                if (string.IsNullOrEmpty(volume) || !volume.EndsWith("\\", StringComparison.Ordinal)) { chain.Dispose(); return false; }
                if (!chain.TryPush(volume)) { chain.Dispose(); return false; }
                string current = volume.TrimEnd('\\');
                foreach (string part in root[volume.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
                {
                    current = Path.Combine(current + "\\", part);
                    if (!chain.TryPush(current)) { chain.Dispose(); return false; }
                }
                chain.RootExists = true;
                return true;
            }
            catch { chain.Dispose(); return false; }
        }

        public bool TryPush(string path)
        {
            string full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
            if (full.Length == 2 && full[1] == ':') full += "\\";
            SafeFileHandle handle = Native.CreateFile(full, Native.FileReadAttributes,
                Native.ShareRead | Native.ShareWrite, IntPtr.Zero, Native.OpenExisting,
                Native.BackupSemantics | Native.OpenReparsePoint, IntPtr.Zero);
            if (handle.IsInvalid) { handle.Dispose(); return false; }
            if (!Native.GetFileInformationByHandle(handle, out var info) ||
                (((FileAttributes)info.FileAttributes & FileAttributes.Directory) == 0) ||
                (((FileAttributes)info.FileAttributes & FileAttributes.ReparsePoint) != 0) ||
                !Native.PathMatchesHandle(handle, full))
            { handle.Dispose(); return false; }
            _handles.Add((full, handle, info));
            return true;
        }

        public void Pop()
        {
            if (_handles.Count <= 1) return;
            var last = _handles[^1]; _handles.RemoveAt(_handles.Count - 1); last.Handle.Dispose();
        }

        public bool ContainsPath(string directory) => _handles.Count > 0 &&
            Normalize(_handles[^1].Path).Equals(Normalize(directory), StringComparison.OrdinalIgnoreCase);

        public void Dispose()
        {
            for (int i = _handles.Count - 1; i >= 0; i--) _handles[i].Handle.Dispose();
            _handles.Clear();
        }

        private static string Normalize(string path) => Path.GetFullPath(path).TrimEnd('\\');
    }

    private static class Native
    {
        internal const uint FileReadAttributes = 0x80, FileReadData = 0x1, DeleteAccess = 0x00010000;
        internal const uint ShareRead = 1, ShareWrite = 2, OpenExisting = 3;
        internal const uint BackupSemantics = 0x02000000, OpenReparsePoint = 0x00200000, SequentialScan = 0x08000000;
        private const int FileStreamInfoClass = 7, ErrorMoreData = 234, ErrorInsufficientBuffer = 122;
        private const int MaximumStreamInfoBufferBytes = 1024 * 1024;

        [StructLayout(LayoutKind.Sequential)] internal struct FileInfo
        {
            public uint FileAttributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, LastAccessTime, LastWriteTime;
            public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
        }
        [StructLayout(LayoutKind.Sequential)] private struct DispositionInfo { public byte DeleteFile; }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInfo info);
        [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int infoClass, [Out] byte[] buffer, uint bufferSize);
        [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, System.Text.StringBuilder path, uint length, uint flags);
        [DllImport("kernel32.dll", EntryPoint = "SetFileInformationByHandle", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetFileInformationByHandle(SafeFileHandle file, int infoClass, ref DispositionInfo info, uint size);

        internal static bool SetDeleteDisposition(SafeFileHandle handle)
        {
            var disposition = new DispositionInfo { DeleteFile = 1 };
            return SetFileInformationByHandle(handle, 4, ref disposition, 1);
        }

        internal static bool HasOnlyDefaultDataStream(SafeFileHandle handle)
        {
            for (int capacity = 4096; capacity <= MaximumStreamInfoBufferBytes; capacity *= 2)
            {
                byte[] buffer = new byte[capacity];
                if (!GetFileInformationByHandleEx(handle, FileStreamInfoClass, buffer, (uint)buffer.Length))
                {
                    int error = Marshal.GetLastWin32Error();
                    if ((error == ErrorMoreData || error == ErrorInsufficientBuffer) && capacity < MaximumStreamInfoBufferBytes)
                        continue;
                    return false;
                }

                int offset = 0;
                int recordCount = 0;
                while (true)
                {
                    if (offset < 0 || offset > buffer.Length - 24) return false;
                    uint nextOffset = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(offset, 4));
                    uint nameLength = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(offset + 4, 4));
                    if (nameLength == 0 || (nameLength & 1) != 0 || nameLength > int.MaxValue) return false;
                    long recordEnd = (long)offset + 24 + nameLength;
                    if (recordEnd > buffer.Length) return false;
                    string name = System.Text.Encoding.Unicode.GetString(buffer, offset + 24, (int)nameLength);
                    if (!name.Equals("::$DATA", StringComparison.OrdinalIgnoreCase)) return false;
                    recordCount++;
                    if (nextOffset == 0) return recordCount == 1;
                    if ((nextOffset & 7) != 0 || nextOffset < recordEnd - offset || nextOffset > buffer.Length - offset)
                        return false;
                    offset += (int)nextOffset;
                }
            }
            return false;
        }

        internal static bool PathMatchesHandle(SafeFileHandle handle, string expected)
        {
            var buffer = new System.Text.StringBuilder(512);
            uint length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
            if (length == 0) return false;
            if (length >= buffer.Capacity)
            {
                buffer = new System.Text.StringBuilder(checked((int)length + 1));
                length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
                if (length == 0 || length >= buffer.Capacity) return false;
            }
            string actual = NormalizeFinal(buffer.ToString());
            string target = NormalizeFinal(expected);
            return actual.Equals(target, StringComparison.OrdinalIgnoreCase);
        }

        internal static DateTime ToUtc(System.Runtime.InteropServices.ComTypes.FILETIME time)
        {
            try { long ticks = ((long)(uint)time.dwHighDateTime << 32) | (uint)time.dwLowDateTime; return DateTime.FromFileTimeUtc(ticks); }
            catch { return DateTime.MinValue; }
        }

        private static string NormalizeFinal(string path)
        {
            if (path.StartsWith("\\\\?\\UNC\\", StringComparison.OrdinalIgnoreCase)) path = "\\\\" + path[8..];
            else if (path.StartsWith("\\\\?\\", StringComparison.Ordinal)) path = path[4..];
            return Path.GetFullPath(path).TrimEnd('\\');
        }
    }
}
