using System.IO;
using System.Collections.Concurrent;
using System.Text.Json;

namespace TerminalNotes.Services;

public sealed record ProjectArchiveLocation(int Version, string OriginalDirectory, string ArchivedDirectory,
    string DisplayName, bool MultiMode);

/// <summary>Moves a whole project by same-volume rename, never merging or overwriting another project.</summary>
public static class ProjectArchiveService
{
    public const string FolderName = "archive";
    private const string LocationFile = ".project-location.json";
    private static readonly ConcurrentDictionary<string, string> MovedDirectories = new(StringComparer.OrdinalIgnoreCase);
    private static string Canonical(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    private static bool Same(string left, string right) => string.Equals(Canonical(left), Canonical(right), StringComparison.OrdinalIgnoreCase);
    private static string LocationPath(string project) => Path.Combine(project, "terminal-notes", LocationFile);

    internal static void ThrowIfRelocated(string project)
    {
        if (MovedDirectories.TryGetValue(Canonical(project), out var destination) && !Directory.Exists(project))
            throw new DirectoryNotFoundException("프로젝트가 이동되었습니다 · " + destination);
    }

    public static bool IsArchived(string workspace, string project) =>
        Path.GetDirectoryName(Canonical(project)) is { } parent && Same(parent, Path.Combine(workspace, FolderName));

    public static string Destination(string workspace, string project)
    {
        workspace = Canonical(workspace); project = Canonical(project);
        string archive = Path.Combine(workspace, FolderName);
        if (!IsArchived(workspace, project) && !Same(Path.GetDirectoryName(project) ?? project, workspace))
            throw new InvalidOperationException("작업 폴더 또는 아카이브 바로 아래의 프로젝트만 이동할 수 있습니다.");
        if (Same(project, archive)) throw new InvalidOperationException("아카이브 폴더 자체는 이동할 수 없습니다.");
        string destination = Path.Combine(IsArchived(workspace, project) ? workspace : archive, Path.GetFileName(project));
        EnsureRealDirectory(workspace);
        EnsureRealDirectory(project);
        if (Directory.Exists(archive)) EnsureRealDirectory(archive);
        if (File.Exists(destination) || Directory.Exists(destination))
            throw new IOException("이동할 위치에 같은 이름의 프로젝트가 있습니다 · " + destination);
        return destination;
    }

    public static ProjectArchiveLocation? ReadLocation(string project)
    {
        string path = LocationPath(project);
        if (!File.Exists(path)) return null;
        EnsureOrdinaryFile(path);
        var location = JsonSerializer.Deserialize<ProjectArchiveLocation>(File.ReadAllText(path));
        if (location is null || location.Version != 1 ||
            !Path.IsPathFullyQualified(location.OriginalDirectory) || !Path.IsPathFullyQualified(location.ArchivedDirectory) ||
            (!Same(project, location.OriginalDirectory) && !Same(project, location.ArchivedDirectory)) ||
            !Same(Path.Combine(Path.GetDirectoryName(location.OriginalDirectory)!, FolderName, Path.GetFileName(location.OriginalDirectory)), location.ArchivedDirectory))
            throw new InvalidDataException("프로젝트 이동 기록이 올바르지 않습니다 · " + path);
        return location;
    }

    // Original snapshots and evidence stay byte-for-byte intact. Only the live project binding changes.
    internal static void RebindRuns(string project, List<AutomationRun> runs)
    {
        if (runs.All(run => Same(run.ProjectDirectory, project))) return;
        var location = ReadLocation(project);
        if (location is null) return; // Normal store validation rejects unregistered relocations.
        for (int index = 0; index < runs.Count; index++)
            if (Same(runs[index].ProjectDirectory, location.OriginalDirectory) || Same(runs[index].ProjectDirectory, location.ArchivedDirectory))
                runs[index] = runs[index] with { ProjectDirectory = Canonical(project) };
    }

    public static async Task<string> MoveAsync(string workspace, string project, string displayName, bool multiMode)
    {
        workspace = Canonical(workspace); project = Canonical(project);
        string destination = Destination(workspace, project);
        string workspaceLock = Path.Combine(workspace, ".terminalnotes-archive.lock");
        if (File.Exists(workspaceLock)) EnsureOrdinaryFile(workspaceLock);
        using var moveLock = new FileStream(workspaceLock, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var store = new AutomationStore(project);
        string storeDirectory = Path.Combine(project, "terminal-notes");
        if (Directory.Exists(storeDirectory)) EnsureRealDirectory(storeDirectory);
        byte[]? ledger = File.Exists(store.StorePath) ? await File.ReadAllBytesAsync(store.StorePath) : null;
        var runs = await store.LoadAsync();
        if (runs.Any(run => run.State is AutomationState.Running or AutomationState.Scheduled))
            throw new InvalidOperationException("실행·예약 중인 작업을 중단한 뒤 이동하세요.");
        EnsureRealDirectory(storeDirectory);
        // Verify exclusive access before moving; Windows requires child handles to close before parent rename.
        using var automationLock = LockStore(Path.Combine(storeDirectory, ".automation-runs.lock"));
        using var journalLock = LockStore(Path.Combine(storeDirectory, ".work-journal.lock"));
        byte[]? current = File.Exists(store.StorePath) ? await File.ReadAllBytesAsync(store.StorePath) : null;
        if ((ledger is null) != (current is null) || ledger is not null && !ledger.AsSpan().SequenceEqual(current))
            throw new IOException("실행 기록이 변경되었습니다. 잠시 후 다시 이동하세요.");
        _ = ReadLocation(project); // Never overwrite an unrelated or malformed metadata file.
        var location = IsArchived(workspace, project)
            ? new ProjectArchiveLocation(1, destination, project, displayName, multiMode)
            : new ProjectArchiveLocation(1, project, destination, displayName, multiMode);
        string metadata = LocationPath(project);
        byte[]? previous = File.Exists(metadata) ? await File.ReadAllBytesAsync(metadata) : null;
        string temporary = metadata + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, JsonSerializer.SerializeToUtf8Bytes(location));
            File.Move(temporary, metadata, overwrite: true);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            // Re-resolve both allowed parents and reject collisions immediately before the exact rename.
            destination = Destination(workspace, project);
            MovedDirectories[project] = destination;
            automationLock.Dispose(); journalLock.Dispose();
            Directory.Move(project, destination);
            MovedDirectories.TryRemove(destination, out _);
            return destination;
        }
        catch
        {
            if (Directory.Exists(project))
            {
                MovedDirectories.TryRemove(project, out _);
                if (previous is null) File.Delete(metadata);
                else File.WriteAllBytes(metadata, previous);
            }
            throw;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static FileStream LockStore(string path)
    {
        if (File.Exists(path)) EnsureOrdinaryFile(path);
        return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Delete);
    }
    private static void EnsureOrdinaryFile(string path)
    {
        if ((File.GetAttributes(path) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
            throw new IOException("일반 파일이 아닌 이동 기록은 사용할 수 없습니다 · " + path);
    }
    private static void EnsureRealDirectory(string path)
    {
        if (!Directory.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("실제 프로젝트 폴더만 이동할 수 있습니다 · " + path);
    }
}
