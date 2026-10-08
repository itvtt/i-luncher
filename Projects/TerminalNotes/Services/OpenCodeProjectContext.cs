using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TerminalNotes.Services;

/// <summary>
/// Holds the per-terminal OpenCode instruction file that points at its currently
/// bound project plan. OpenCode rereads configured instruction files before each
/// model request, so the file can be updated without restarting the TUI.
/// </summary>
public sealed class OpenCodeProjectContext : IDisposable
{
    // Hosts and integration tests can isolate process data without changing the user's profile.
    internal static string DataDirectory => AppContext.GetData("TerminalNotes.DataDirectory") is string directory
        ? Path.GetFullPath(directory)
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TerminalNotes");
    private readonly object _gate = new();
    private readonly string _workingDirectory;
    private string? _projectName;
    private bool _disposed;

    public string InstructionFilePath { get; }

    public OpenCodeProjectContext(string workingDirectory, string? planDocumentPath = null, string? projectName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        _workingDirectory = Path.GetFullPath(workingDirectory);
        string directory = Path.Combine(DataDirectory, "OpenCodeProjectContext");
        Directory.CreateDirectory(directory);
        InstructionFilePath = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".md");
        UpdatePlanDocumentContext(planDocumentPath, projectName);
    }

    /// <summary>
    /// Atomically changes the linked plan reference. The plan itself is never read
    /// or written by this service.
    /// </summary>
    public void UpdatePlanDocumentContext(string? planDocumentPath, string? projectName = null)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            string? nextProjectName = projectName ?? _projectName;
            string content = CreateInstructions(planDocumentPath, nextProjectName);
            string temporaryPath = InstructionFilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporaryPath, content, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                File.Move(temporaryPath, InstructionFilePath, overwrite: true);
                _projectName = nextProjectName;
            }
            finally
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
        }
    }

    /// <summary>
    /// Adds this pane's instruction file to OpenCode's documented inline config,
    /// preserving all valid inherited settings and configured instruction paths.
    /// </summary>
    public string CreateProcessConfigContent(string? existingConfigContent)
    {
        JsonObject config;
        if (string.IsNullOrWhiteSpace(existingConfigContent))
        {
            config = new JsonObject();
        }
        else
        {
            JsonNode? parsed;
            try
            {
                parsed = JsonNode.Parse(existingConfigContent, documentOptions: new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip
                });
            }
            catch (JsonException exception)
            {
                throw new InvalidOperationException(
                    "The inherited OPENCODE_CONFIG_CONTENT is not valid JSON/JSONC; TerminalNotes cannot safely add its plan instructions.",
                    exception);
            }

            config = parsed as JsonObject
                ?? throw new InvalidOperationException(
                    "The inherited OPENCODE_CONFIG_CONTENT must be a JSON object so TerminalNotes can preserve it and add plan instructions.");
        }

        string globalInstructions = GlobalAgentInstructions.EnsureFile();
        AddInstructionPath(config, globalInstructions);
        AddInstructionPath(config, InstructionFilePath);

        return config.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
    }

    private static void AddInstructionPath(JsonObject config, string path)
    {
        if (config.TryGetPropertyValue("instructions", out JsonNode? existingInstructions))
        {
            if (existingInstructions is not JsonArray instructionArray)
            {
                throw new InvalidOperationException(
                    "OPENCODE_CONFIG_CONTENT.instructions must be an array; TerminalNotes cannot safely add its instructions.");
            }

            if (!instructionArray.Any(node => node is JsonValue value && value.TryGetValue<string>(out string? existingPath) &&
                string.Equals(existingPath, path, StringComparison.OrdinalIgnoreCase)))
            {
                instructionArray.Add(path);
            }
        }
        else
        {
            config["instructions"] = new JsonArray(JsonValue.Create(path));
        }
    }

    private const string ResumeInstructions = """
        ## 작업 재개 및 추가 입력

        중단 후 재개하거나 새 세션으로 연결되어도 처음부터 다시 시작하지 마세요. 최신 요청과 계획의 요구사항을 항목별로 현재 코드·변경분·기존 대화/전달된 실행 결과·native todo와 대조해 완료, 일부 구현, 미완료를 구분하세요. pending todo나 계획에 항목이 남았다는 이유만으로 다시 구현하지 마세요. 이미 구현되어 정상 동작하는 기능은 보존하고 다시 구현하지 마세요. 모두 충족되었으면 근거를 확인하고 stale todo만 정리하며 코드 변경 없이 멈추세요. 일부 미충족이거나 구체 결함이 확인된 경우에만 그 부분을 최소 수정하세요. 유효한 기존 검증이 있으면 코드 변경 없이 다시 돌릴 필요가 없습니다. 구현 도중 끊겨 완료 여부가 불명확한 변경은 현재 파일을 확인한 뒤 이어서 보완하세요. 재구현이 꼭 필요하면 기존 구현의 구체적인 결함과 수정 이유를 먼저 밝히세요. 저장된 기록이나 idle 상태만으로 완료를 추정하지 마세요.
        """;

    private string CreateInstructions(string? planDocumentPath, string? projectName)
    {
        string? fullPath = string.IsNullOrWhiteSpace(planDocumentPath)
            ? null
            : Path.GetFullPath(planDocumentPath, _workingDirectory);

        if (fullPath is not null && !string.Equals(Path.GetExtension(fullPath), ".md", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The linked plan document must be a Markdown (.md) file.", nameof(planDocumentPath));

        if (fullPath is null)
        {
            return $"""
                # TerminalNotes linked plan context

                This terminal is bound to project {DisplayProjectName(projectName)} at directory {MarkdownCodeSpan(_workingDirectory)}. Keep all project file edits, builds, and commands within this directory. If the user names another project, ask them to select that project in TerminalNotes before acting.

                No project plan Markdown file is currently linked. Greetings and simple questions can be answered without a plan. Before implementation or build work, ask the user to select or save a nonempty linked plan. Do not invent plan content.

                {ResumeInstructions}

                Do not start work or run commands merely because this session opened. Wait for a user request.
                """;
        }

        return $"""
            # TerminalNotes linked plan context

            This terminal is bound to project {DisplayProjectName(projectName)} at directory {MarkdownCodeSpan(_workingDirectory)}. Keep all project file edits, builds, and commands within this directory. If the user names another project, ask them to select that project in TerminalNotes before acting.

            Treat the latest contents of the linked file as the project's implementation requirements, combined with the user's current request. A newer explicit correction in an appended request supersedes the corresponding older requirement; preserve requirements it does not change. Ask only when the intended result remains ambiguous. The plan is the Markdown file at this absolute path:

            {MarkdownCodeSpan(fullPath)}

            Before implementation or build work for each user task, read the latest contents of that file. Greetings and simple questions can be answered without a plan. If implementation/build work is requested and the file is missing, empty, or unreadable, ask the user to save a nonempty plan and do not invent plan content.

            For implementation or build work, create or update OpenCode's native todo list for the current request before doing the work, and keep its statuses accurate as work proceeds. Mark completed or cancelled items accordingly; never leave completed work pending. Do not create todos for greetings or simple questions. TerminalNotes uses native unfinished todos to resume interrupted work, so do not report completion by prose alone. Do not rewrite the linked plan merely to record progress.

            {ResumeInstructions}

            Do not start work or run commands merely because this session opened. Wait for a user request.
            """;
    }

    private static string DisplayProjectName(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "(unnamed)" : MarkdownCodeSpan(value);

    private static string MarkdownCodeSpan(string value)
    {
        int longestRun = 0;
        int currentRun = 0;
        foreach (char character in value)
        {
            if (character == '`')
            {
                currentRun++;
                longestRun = Math.Max(longestRun, currentRun);
            }
            else currentRun = 0;
        }
        string fence = new('`', longestRun + 1);
        bool needsPadding = value.StartsWith('`') || value.EndsWith('`') || value.StartsWith(' ') || value.EndsWith(' ');
        return needsPadding ? fence + " " + value + " " + fence : fence + value + fence;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            try
            {
                if (File.Exists(InstructionFilePath))
                    File.Delete(InstructionFilePath);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
