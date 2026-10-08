using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TerminalNotes.Services;

public enum TerminalInputMode
{
    Question,
    Plan,
    Build,
    Review,
    Command
}

public enum WorkJournalStatus
{
    Submitted,
    Completed,
    Failed
}

public enum JournalSubmissionKind
{
    NotPersisted,
    Reserved,
    Duplicate
}

public enum JournalSubmissionDisposition
{
    NotPersisted,
    Reserved,
    Duplicate
}

public sealed record WorkJournalEntry(
    Guid Id,
    TerminalInputMode Mode,
    string OriginalText,
    int Slot,
    DateTimeOffset SubmittedAt,
    WorkJournalStatus Status,
    DateTimeOffset? FinishedAt,
    string? Detail,
    string DocumentPath,
    bool IsAutomation);

public sealed record JournalSubmissionResult(
    JournalSubmissionKind Kind,
    WorkJournalEntry? Entry,
    WorkJournalEntry? DuplicateEntry,
    bool CanForceManualRerun)
{
    public JournalSubmissionDisposition Disposition => (JournalSubmissionDisposition)Kind;

    public static JournalSubmissionResult QuestionNotPersisted { get; } =
        new(JournalSubmissionKind.NotPersisted, null, null, false);
}

/// <summary>
/// Persists non-question terminal input in a project-local, human-readable journal.
/// Question text is returned before any directory, hash, or ledger data is created.
/// </summary>
public sealed class ProjectWorkJournal
{
    private const int LedgerVersion = 1;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> ProcessGates =
        new(PathComparer);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _projectDirectory;
    private readonly string _journalDirectory;
    private readonly string _ledgerPath;
    private readonly string _lockPath;
    private readonly SemaphoreSlim _processGate;

    public ProjectWorkJournal(string projectDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectDirectory);
        _projectDirectory = Path.GetFullPath(projectDirectory);
        _journalDirectory = Path.Combine(_projectDirectory, "terminal-notes");
        _ledgerPath = Path.Combine(_journalDirectory, "work-journal.json");
        _lockPath = Path.Combine(_journalDirectory, ".work-journal.lock");
        _processGate = ProcessGates.GetOrAdd(_journalDirectory, static _ => new SemaphoreSlim(1, 1));
    }

    public string ProjectDirectory => _projectDirectory;
    public string JournalDirectory => _journalDirectory;

    public async Task<JournalSubmissionResult> SubmitAsync(
        TerminalInputMode mode,
        string text,
        int slot,
        bool isAutomation = false,
        bool forceManualRerun = false,
        DateTimeOffset? submittedAt = null,
        CancellationToken cancellationToken = default)
    {
        if (mode == TerminalInputMode.Question)
        {
            return JournalSubmissionResult.QuestionNotPersisted;
        }

        ArgumentNullException.ThrowIfNull(text);
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException("Journal text cannot be empty.", nameof(text));
        }

        if (slot < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(slot), "The terminal slot cannot be negative.");
        }

        if (forceManualRerun && isAutomation)
        {
            throw new InvalidOperationException("Automation cannot force a duplicate journal entry.");
        }

        string normalizedText = NormalizeForDuplicateComparison(text);
        DateTimeOffset timestamp = submittedAt ?? DateTimeOffset.UtcNow;

        return await WithExclusiveAccessAsync(async () =>
        {
            (JournalLedger ledger, byte[]? originalBytes) = await ReadLedgerAsync(cancellationToken);
            WorkJournalEntry? duplicate = ledger.Entries.LastOrDefault(entry =>
                entry.Mode == mode &&
                string.Equals(
                    NormalizeForDuplicateComparison(entry.OriginalText),
                    normalizedText,
                    StringComparison.Ordinal));

            if (duplicate is not null && !forceManualRerun)
            {
                return new JournalSubmissionResult(
                    JournalSubmissionKind.Duplicate,
                    null,
                    duplicate,
                    CanForceManualRerun: !isAutomation);
            }

            Guid id = Guid.NewGuid();
            string category = mode == TerminalInputMode.Plan ? "plans" : "commands";
            string categoryDirectory = Path.Combine(_journalDirectory, category);
            Directory.CreateDirectory(categoryDirectory);

            string documentPath = CreateJournalDocument(
                categoryDirectory,
                id,
                mode,
                slot,
                timestamp,
                text,
                isAutomation);
            string relativeDocumentPath = Path.GetRelativePath(_projectDirectory, documentPath);
            var entry = new WorkJournalEntry(
                id,
                mode,
                text,
                slot,
                timestamp,
                WorkJournalStatus.Submitted,
                FinishedAt: null,
                Detail: null,
                relativeDocumentPath,
                isAutomation);

            ledger.Entries.Add(entry);
            try
            {
                await WriteLedgerAsync(ledger, originalBytes, cancellationToken);
            }
            catch
            {
                TryDeleteGeneratedDocument(documentPath);
                throw;
            }

            return new JournalSubmissionResult(
                JournalSubmissionKind.Reserved,
                entry,
                null,
                CanForceManualRerun: false);
        }, cancellationToken);
    }

    public async Task<WorkJournalEntry> UpdateStatusAsync(
        Guid entryId,
        WorkJournalStatus status,
        string? detail = null,
        DateTimeOffset? at = null,
        CancellationToken cancellationToken = default)
    {
        if (status == WorkJournalStatus.Submitted)
        {
            throw new ArgumentException("A submitted entry can only finish as Completed or Failed.", nameof(status));
        }

        return await WithExclusiveAccessAsync(async () =>
        {
            (JournalLedger ledger, byte[]? originalBytes) = await ReadLedgerAsync(cancellationToken);
            int index = ledger.Entries.FindIndex(entry => entry.Id == entryId);
            if (index < 0)
            {
                throw new KeyNotFoundException($"Journal entry was not found: {entryId}");
            }

            WorkJournalEntry current = ledger.Entries[index];
            if (current.Status != WorkJournalStatus.Submitted)
            {
                if (current.Status == status)
                {
                    return current;
                }

                throw new InvalidOperationException("A completed journal entry cannot change status.");
            }

            WorkJournalEntry updated = current with
            {
                Status = status,
                FinishedAt = at ?? DateTimeOffset.UtcNow,
                Detail = detail
            };
            ledger.Entries[index] = updated;
            await WriteLedgerAsync(ledger, originalBytes, cancellationToken);
            return updated;
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<WorkJournalEntry>> LoadAsync(
        CancellationToken cancellationToken = default) =>
        await WithExclusiveAccessAsync(async () =>
        {
            (JournalLedger ledger, _) = await ReadLedgerAsync(cancellationToken);
            return ledger.Entries.ToArray();
        }, cancellationToken);

    private async Task<T> WithExclusiveAccessAsync<T>(
        Func<Task<T>> action,
        CancellationToken cancellationToken)
    {
        await _processGate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(_journalDirectory);
            await using FileStream fileLock = await AcquireFileLockAsync(cancellationToken);
            return await action();
        }
        finally
        {
            _processGate.Release();
        }
    }

    private async Task<FileStream> AcquireFileLockAsync(CancellationToken cancellationToken)
    {
        for (int attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(
                    _lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.Asynchronous);
            }
            catch (IOException) when (attempt < 200)
            {
                await Task.Delay(25, cancellationToken);
            }
        }
    }

    private async Task<(JournalLedger Ledger, byte[]? OriginalBytes)> ReadLedgerAsync(
        CancellationToken cancellationToken)
    {
        if (!File.Exists(_ledgerPath))
        {
            return (new JournalLedger(), null);
        }

        byte[] bytes = await File.ReadAllBytesAsync(_ledgerPath, cancellationToken);
        try
        {
            JournalLedger? ledger = JsonSerializer.Deserialize<JournalLedger>(bytes, JsonOptions);
            if (ledger is null || ledger.Version != LedgerVersion || ledger.Entries is null)
            {
                throw new InvalidDataException("The work journal ledger has an unsupported structure.");
            }

            ValidateLedger(ledger);
            return (ledger, bytes);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"The work journal ledger is malformed and was left unchanged: {_ledgerPath}",
                exception);
        }
    }

    private async Task WriteLedgerAsync(
        JournalLedger ledger,
        byte[]? originalBytes,
        CancellationToken cancellationToken)
    {
        VerifyUnchanged(_ledgerPath, originalBytes);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(ledger, JsonOptions);
        await AtomicWriteAsync(_ledgerPath, bytes, cancellationToken);
    }

    private static void ValidateLedger(JournalLedger ledger)
    {
        var ids = new HashSet<Guid>();
        foreach (WorkJournalEntry entry in ledger.Entries)
        {
            if (entry.Id == Guid.Empty || !ids.Add(entry.Id) ||
                !Enum.IsDefined(entry.Mode) ||
                !Enum.IsDefined(entry.Status) ||
                entry.Mode == TerminalInputMode.Question ||
                string.IsNullOrWhiteSpace(entry.OriginalText) ||
                entry.Slot < 0 ||
                string.IsNullOrWhiteSpace(entry.DocumentPath) ||
                Path.IsPathRooted(entry.DocumentPath) ||
                !entry.DocumentPath.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ||
                !IsWithinJournalDirectory(entry.DocumentPath) ||
                (entry.Status == WorkJournalStatus.Submitted) != (entry.FinishedAt is null))
            {
                throw new InvalidDataException("The work journal ledger contains an invalid entry.");
            }
        }
    }

    private static bool IsWithinJournalDirectory(string relativePath)
    {
        // Ledger entries are always rooted at the project and must remain below terminal-notes.
        string projectProbe = Path.Combine(Path.GetTempPath(), "terminal-notes-path-probe");
        string journalProbe = Path.GetFullPath(Path.Combine(projectProbe, "terminal-notes")) +
            Path.DirectorySeparatorChar;
        string candidate = Path.GetFullPath(Path.Combine(projectProbe, relativePath));
        return candidate.StartsWith(
            journalProbe,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    private static string CreateJournalDocument(
        string categoryDirectory,
        Guid id,
        TerminalInputMode mode,
        int slot,
        DateTimeOffset submittedAt,
        string text,
        bool isAutomation)
    {
        string fileName = $"{submittedAt.UtcDateTime:yyyyMMdd-HHmmssfff}-{mode.ToString().ToLowerInvariant()}-{id:N}.md";
        string path = Path.Combine(categoryDirectory, fileName);
        string slotLabel = slot == 0 ? "App" : $"Terminal {slot}";
        string body = $"""
            # {mode} · {slotLabel}

            - Submitted: {submittedAt:O}
            - Source: {(isAutomation ? "Automation" : "Manual")}
            - Status: Submitted

            ## Input

            {text}
            """;

        using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 16 * 1024,
            FileOptions.WriteThrough);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true);
        writer.Write(body);
        writer.Flush();
        stream.Flush(flushToDisk: true);
        return path;
    }

    private static string NormalizeForDuplicateComparison(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Trim()
            .Normalize(NormalizationForm.FormC);

    private static void VerifyUnchanged(string path, byte[]? originalBytes)
    {
        if (originalBytes is null)
        {
            if (File.Exists(path))
            {
                throw new JournalConflictException(path, "The ledger appeared while it was being updated.");
            }

            return;
        }

        if (!File.Exists(path) || !File.ReadAllBytes(path).AsSpan().SequenceEqual(originalBytes))
        {
            throw new JournalConflictException(path, "The ledger changed outside Terminal Notes.");
        }
    }

    internal static async Task AtomicWriteAsync(
        string destination,
        byte[] bytes,
        CancellationToken cancellationToken)
    {
        string directory = Path.GetDirectoryName(destination)
            ?? throw new DirectoryNotFoundException(destination);
        string temporaryPath = Path.Combine(directory, $".terminalnotes-{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 64 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(destination))
            {
                File.Move(temporaryPath, destination, overwrite: true);
            }
            else
            {
                File.Move(temporaryPath, destination);
            }

            temporaryPath = string.Empty;
        }
        finally
        {
            if (temporaryPath.Length > 0)
            {
                try { File.Delete(temporaryPath); } catch (IOException) { }
            }
        }
    }

    private static void TryDeleteGeneratedDocument(string path)
    {
        try { File.Delete(path); } catch (IOException) { }
    }

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private sealed class JournalLedger
    {
        public int Version { get; set; } = LedgerVersion;
        public List<WorkJournalEntry> Entries { get; set; } = [];
    }
}

public sealed class JournalConflictException : IOException
{
    public JournalConflictException(string filePath, string message) : base(message) =>
        FilePath = filePath;

    public string FilePath { get; }
}
