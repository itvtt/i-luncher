namespace DiskAtlas.Core;

using System.ComponentModel;

public sealed class DuplicateDeletionService
{
    private readonly IFileRecycler _recycler;

    public DuplicateDeletionService(IFileRecycler recycler)
    {
        _recycler = recycler ?? throw new ArgumentNullException(nameof(recycler));
    }

    public Task<DuplicateDeleteResult> DeleteAsync(
        IReadOnlyList<DuplicateGroup> groups,
        IReadOnlyCollection<string> selectedPaths,
        IProgress<DuplicateProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(groups);
        ArgumentNullException.ThrowIfNull(selectedPaths);
        return Task.Run(
            () => DeleteCoreAsync(groups, selectedPaths, progress, cancellationToken),
            cancellationToken);
    }

    private async Task<DuplicateDeleteResult> DeleteCoreAsync(
        IReadOnlyList<DuplicateGroup> groups,
        IReadOnlyCollection<string> selectedPaths,
        IProgress<DuplicateProgress>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var indexed = IndexAndValidateGroups(groups);
        var selected = NormalizeSelection(selectedPaths);
        foreach (string path in selected)
        {
            if (!indexed.ContainsKey(path))
                throw new ArgumentException($"중복 검사 결과에 없는 파일이 선택되었습니다: {path}", nameof(selectedPaths));
        }

        foreach (DuplicateGroup group in groups)
        {
            if (group.Files.Count > 0 && group.Files.All(file => selected.Contains(Normalize(file.FullPath))))
                throw new ArgumentException($"중복 그룹의 모든 파일을 삭제할 수 없습니다: {group.Id}", nameof(selectedPaths));
        }

        var deleted = new List<string>();
        var failures = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        long completed = 0;
        long bytes = 0;

        foreach (DuplicateGroup group in groups)
        {
            if (cancellationToken.IsCancellationRequested)
                return ReturnOrThrowCancellation(deleted, failures, cancellationToken);
            DuplicateFile[] targets = group.Files
                .Where(file => selected.Contains(Normalize(file.FullPath)))
                .ToArray();
            if (targets.Length == 0)
                continue;

            DuplicateFile? keeper = null;
            WindowsFileInspector.OpenFile? keeperHandle = null;
            foreach (DuplicateFile candidate in group.Files.Where(file => !selected.Contains(Normalize(file.FullPath))))
            {
                WindowsFileInspector.OpenFile? candidateHandle = null;
                try
                {
                    candidateHandle = WindowsFileInspector.OpenEligible(candidate.FullPath, FileShare.Read);
                    progress?.Report(new DuplicateProgress(
                        completed,
                        selected.Count,
                        bytes,
                        candidate.FullPath,
                        "보관본 검증"));
                    WindowsFileInspector.EnsureMatches(
                        candidateHandle,
                        candidate,
                        verifyHash: true,
                        cancellationToken);
                    keeper = candidate;
                    keeperHandle = candidateHandle;
                    candidateHandle = null;
                    break;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return ReturnOrThrowCancellation(deleted, failures, cancellationToken);
                }
                catch (Exception error) when (IsFileFailure(error))
                {
                    // Try another unselected copy. A group can have more than one possible keeper.
                }
                finally
                {
                    candidateHandle?.Dispose();
                }
            }

            if (keeper is null || keeperHandle is null)
            {
                foreach (DuplicateFile target in targets)
                {
                    failures[target.FullPath] = "검증된 보관본을 확보할 수 없어 삭제하지 않았습니다.";
                    completed++;
                    progress?.Report(new DuplicateProgress(
                        completed,
                        selected.Count,
                        bytes,
                        target.FullPath,
                        "보관본 검증 실패"));
                }
                continue;
            }

            DuplicateFile verifiedKeeper = keeper;
            using (keeperHandle)
            {
                foreach (DuplicateFile target in targets)
                {
                    if (cancellationToken.IsCancellationRequested)
                        return ReturnOrThrowCancellation(deleted, failures, cancellationToken);
                    WindowsFileInspector.OpenFile? selectedHandle = null;
                    bool callbackInvoked = false;
                    try
                    {
                        await _recycler.RecycleAsync(target.FullPath, () =>
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            selectedHandle?.Dispose();
                            selectedHandle = WindowsFileInspector.OpenEligible(
                                target.FullPath,
                                FileShare.Read | FileShare.Delete);
                            progress?.Report(new DuplicateProgress(
                                completed,
                                selected.Count,
                                bytes,
                                target.FullPath,
                                "삭제 전 내용 확인"));
                            WindowsFileInspector.EnsureMatches(
                                keeperHandle,
                                verifiedKeeper,
                                verifyHash: false,
                                cancellationToken);
                            WindowsFileInspector.EnsureMatches(
                                selectedHandle,
                                target,
                                verifyHash: true,
                                cancellationToken);
                            callbackInvoked = true;
                        }, cancellationToken).ConfigureAwait(false);

                        if (!callbackInvoked)
                            throw new InvalidOperationException("재활용기가 삭제 직전 검증 콜백을 호출하지 않았습니다.");

                        deleted.Add(target.FullPath);
                        bytes = checked(bytes + target.Size);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        return ReturnOrThrowCancellation(deleted, failures, cancellationToken);
                    }
                    catch (Exception error)
                    {
                        failures[target.FullPath] = error.Message;
                    }
                    finally
                    {
                        selectedHandle?.Dispose();
                    }

                    completed++;
                    progress?.Report(new DuplicateProgress(completed, selected.Count, bytes, target.FullPath, "휴지통으로 이동"));
                }
            }
        }

        return new DuplicateDeleteResult(deleted, failures);
    }

    private static Dictionary<string, DuplicateFile> IndexAndValidateGroups(IReadOnlyList<DuplicateGroup> groups)
    {
        var indexed = new Dictionary<string, DuplicateFile>(StringComparer.OrdinalIgnoreCase);
        foreach (DuplicateGroup group in groups)
        {
            ArgumentNullException.ThrowIfNull(group);
            if (group.Files is null || group.Files.Count < 2)
                throw new ArgumentException("중복 그룹에는 파일이 두 개 이상 있어야 합니다.", nameof(groups));

            DuplicateFile first = group.Files[0];
            if (first.Size <= 0 || string.IsNullOrWhiteSpace(first.ContentHash))
                throw new ArgumentException("중복 그룹 정보가 올바르지 않습니다.", nameof(groups));

            foreach (DuplicateFile file in group.Files)
            {
                if (file.Size != first.Size || !StringComparer.Ordinal.Equals(file.ContentHash, first.ContentHash))
                    throw new ArgumentException("서로 다른 파일이 같은 중복 그룹에 포함되어 있습니다.", nameof(groups));
                string path = Normalize(file.FullPath);
                if (!indexed.TryAdd(path, file))
                    throw new ArgumentException($"중복 파일 경로가 반복되었습니다: {file.FullPath}", nameof(groups));
            }
        }
        return indexed;
    }

    private static HashSet<string> NormalizeSelection(IReadOnlyCollection<string> selectedPaths)
    {
        var selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in selectedPaths)
        {
            string normalized = Normalize(path);
            if (!selected.Add(normalized))
                throw new ArgumentException($"삭제 선택에 같은 경로가 반복되었습니다: {path}", nameof(selectedPaths));
        }
        return selected;
    }

    private static string Normalize(string path) => WindowsFileInspector.NormalizeAndValidatePath(path);

    private static bool IsFileFailure(Exception error) =>
        error is IOException or UnauthorizedAccessException or System.Security.SecurityException or
            Win32Exception or ArgumentException or NotSupportedException;

    private static DuplicateDeleteResult ReturnOrThrowCancellation(
        List<string> deleted,
        Dictionary<string, string> failures,
        CancellationToken cancellationToken)
    {
        if (deleted.Count == 0 && failures.Count == 0)
            throw new OperationCanceledException(cancellationToken);
        return new DuplicateDeleteResult(deleted, failures);
    }
}
