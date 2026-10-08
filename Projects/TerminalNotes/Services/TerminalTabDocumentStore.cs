using System.Collections.Concurrent;
using System.IO;

namespace TerminalNotes.Services;

public sealed record TabDocumentSnapshot(string Path, string Text, bool Exists)
{
    internal MarkdownDocument? Document { get; init; }
}

/// <summary>Project-level Plan documents, with legacy per-slot paths for other modes.</summary>
public sealed class TerminalTabDocumentStore
{
    public const string ProjectPlanRelativePath = "terminal-notes/plan.md";
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _directory;
    private readonly string _planDocumentPath;

    public static string GetProjectPlanPath(string projectDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectDirectory);
        return System.IO.Path.GetFullPath(System.IO.Path.Combine(
            System.IO.Path.GetFullPath(projectDirectory), ProjectPlanRelativePath));
    }

    public TerminalTabDocumentStore(string projectDirectory, string? planDocumentPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectDirectory);
        string fullProjectDirectory = System.IO.Path.GetFullPath(projectDirectory);
        _directory = System.IO.Path.Combine(fullProjectDirectory, "terminal-notes", "tabs");
        // Keep the optional parameter for callers and saved terminal state from older versions.
        // The project Plan has one canonical path now; custom values are never a runtime binding.
        _ = planDocumentPath;
        _planDocumentPath = GetProjectPlanPath(fullProjectDirectory);
    }

    public string GetDocumentPath(int slot, TerminalInputMode mode)
    {
        if (slot < 1) throw new ArgumentOutOfRangeException(nameof(slot));
        if (mode == TerminalInputMode.Question || !Enum.IsDefined(mode))
            throw new ArgumentException("질문은 파일로 저장하지 않습니다.", nameof(mode));
        if (mode == TerminalInputMode.Plan && _planDocumentPath is not null)
            return _planDocumentPath;
        return System.IO.Path.Combine(_directory, $"terminal-{slot:00}", $"{mode.ToString().ToLowerInvariant()}.md");
    }

    public Task<TabDocumentSnapshot> LoadAsync(int slot, TerminalInputMode mode, CancellationToken cancellationToken = default)
    {
        string path = GetDocumentPath(slot, mode);
        return Task.Run(() => Read(path), cancellationToken);
    }

    public async Task<TabDocumentSnapshot> SaveAsync(int slot, TerminalInputMode mode, string text,
        TabDocumentSnapshot expected, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        string path = GetDocumentPath(slot, mode);
        if (!string.Equals(path, expected.Path, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("다른 탭의 문서는 저장할 수 없습니다.", nameof(expected));
        var gate = Gates.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            return await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var current = Read(path);
                if (current.Exists != expected.Exists || current.Text != expected.Text)
                    throw new IOException("MD 파일이 외부에서 변경되었습니다. 초안을 복사한 뒤 다시 불러와 병합하세요.");
                if (current.Exists && string.Equals(current.Text, text, StringComparison.Ordinal)) return current;
                var document = expected.Document ?? current.Document ?? MarkdownDocument.Create();
                document.Text = text;
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
                document.Save(path);
                return Read(path);
            }, cancellationToken);
        }
        finally { gate.Release(); }
    }

    public static string BuildExecutionPrompt(TabDocumentSnapshot snapshot, TerminalInputMode mode)
    {
        if (mode == TerminalInputMode.Question || !Enum.IsDefined(mode) ||
            !(mode == TerminalInputMode.Plan
                ? string.Equals(System.IO.Path.GetExtension(snapshot.Path), ".md", StringComparison.OrdinalIgnoreCase)
                : string.Equals(System.IO.Path.GetFileName(snapshot.Path), $"{mode.ToString().ToLowerInvariant()}.md", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("실행할 기능 탭과 MD 파일이 일치하지 않습니다.", nameof(mode));
        if (!snapshot.Exists || string.IsNullOrWhiteSpace(snapshot.Text))
            throw new InvalidOperationException("실행할 MD 내용을 먼저 저장하세요.");
        string action = mode switch
        {
            TerminalInputMode.Plan => "요구사항을 현재 코드·변경분·검증 근거와 항목별로 대조해 미충족된 작업 한 가지를 구현하고 관련 검증을 실행하세요. 이미 충족된 요구사항은 근거를 확인하고 반복하지 마세요.",
            TerminalInputMode.Build => "요구사항에 맞게 프로젝트 코드를 구현하고 검증하세요.",
            TerminalInputMode.Review => "프로젝트 코드와 요구사항을 비교하여 검토하고 문제와 검증 결과를 보고하세요.",
            TerminalInputMode.Command => "문서에 명시된 작업을 수행하고 결과를 확인하세요.",
            _ => throw new ArgumentException("질문에는 MD 실행 지침을 만들지 않습니다.", nameof(mode))
        };
        return $"다음 MD 파일을 지금 직접 읽고 최신 내용을 이 작업의 지침으로 사용하세요: \"{snapshot.Path}\"\n" +
            "현재 코드·변경분·기존 결과를 요구사항별로 대조하세요. pending todo나 MD에 남은 항목만으로 미완료를 판단하지 말고, 이미 충족된 항목은 확인 근거와 todo 상태만 정리하세요. 모두 충족되었으면 코드 변경 없이 멈추세요. 일부 누락이나 확인된 결함만 최소 수정하고, 새 요구나 결함이 없으면 유효한 기존 검증을 재사용하세요. 처음부터 다시 시작하거나 정상 구현을 다시 만들지 마세요. 추가 입력은 최신 플랜 요청과 정정을 반영하세요.\n" +
            action + "\n각 작업 단계에 들어가기 전에 같은 파일을 다시 읽어 수정·삭제된 요구사항을 반영하세요. " +
            "파일이 없거나 비어 있거나 읽을 수 없으면 이전 내용으로 진행하지 말고 알려주세요. " +
            "다른 터미널이나 기능 탭의 MD를 이 작업의 지침으로 사용하지 마세요.";
    }

    private static TabDocumentSnapshot Read(string path)
    {
        try
        {
            var document = MarkdownDocument.Load(path);
            return new(path, document.Text, true) { Document = document };
        }
        catch (FileNotFoundException) { return new(path, "", false); }
        catch (DirectoryNotFoundException) { return new(path, "", false); }
    }
}
