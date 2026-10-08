using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using TerminalNotes.Services;

if (args.Length > 0 && args[0] == "--fake-opencode")
{
    Environment.Exit(RunFakeOpenCode(args[1..]));
    return;
}

if (!OperatingSystem.IsWindows())
{
    Console.WriteLine("Automation command smoke test skipped: Windows is required.");
    return;
}

string fixture = Path.Combine(Path.GetTempPath(), "TerminalNotes Automation 한글 'quote' " + Guid.NewGuid().ToString("N"));
string project = Path.Combine(fixture, "project path 'quoted'");
string fakeBin = Path.Combine(fixture, "fake bin");
Directory.CreateDirectory(project);
Directory.CreateDirectory(fakeBin);

string originalPath = Environment.GetEnvironmentVariable("PATH") ?? "";
try
{
    WriteFakeOpenCode(fakeBin);
    Environment.SetEnvironmentVariable("PATH", fakeBin + Path.PathSeparator + originalPath);

    string attackPath = Path.Combine(fixture, "PROMPT_WAS_EXECUTED.txt");
    string injectionText = "한글 입력 😀 ' \" ; & | ` $([IO.File]::WriteAllText('" + attackPath + "','OWNED'))";
    var claim = Claim(project, injectionText);
    AutomationCommand native = await AutomationCommandBuilder.PrepareNativeAsync(project,
        claim with { DispatchToken = Guid.NewGuid() }, "이전 역할의 native 결과");
    Require(!File.Exists(Path.Combine(native.Directory, "run.ps1")) &&
            native.Command == await File.ReadAllTextAsync(Path.Combine(native.Directory, "prompt.md")) &&
            native.Command.Contains(injectionText, StringComparison.Ordinal),
        "Native automation must preserve the role prompt and must not create a shell script.");
    Require(native.Command.Contains("처음부터 다시 시작하지 마세요") && native.Command.Contains("정상 동작하는 기능은 보존") && native.Command.Contains("terminal-inputs.md"),
        "Every role must reconcile existing implementation and additional requests before resuming.");
    AutomationCommand command = await AutomationCommandBuilder.PrepareAsync(project, claim, "이전 결과 'quoted' 한글");

    byte[] scriptBytes = await File.ReadAllBytesAsync(Path.Combine(command.Directory, "run.ps1"));
    Require(scriptBytes.Length >= 3 && scriptBytes[0] == 0xEF && scriptBytes[1] == 0xBB && scriptBytes[2] == 0xBF,
        "The Windows PowerShell 5.1 wrapper script does not have a UTF-8 BOM.");
    Require(command.Command.Contains(AutomationCommandBuilder.QuotePowerShell(Path.Combine(command.Directory, "run.ps1")), StringComparison.Ordinal),
        "The wrapper script path was not PowerShell-quoted.");
    string implementerPrompt = await File.ReadAllTextAsync(Path.Combine(command.Directory, "prompt.md"));
    Require(implementerPrompt.Contains("이미 끝난 일을 반복하지 마세요", StringComparison.Ordinal),
        "The Implementer prompt omitted the no-repeat guidance.");
    Require(implementerPrompt.Contains("detached:true, stdio:'ignore', windowsHide:true", StringComparison.Ordinal) &&
            implementerPrompt.Contains("child.unref()", StringComparison.Ordinal) &&
            implementerPrompt.Contains("실제 브라우저의 키보드·클릭 이벤트", StringComparison.Ordinal),
        "Roles must detach long-running services and verify real browser input, not only engine/source tests.");
    AutomationCommand coordinatorCommand = await AutomationCommandBuilder.PrepareAsync(
        project, Claim(project, "coordinator prompt", role: AutomationRole.Coordinator), "이전 검토 결과");
    string coordinatorPrompt = await File.ReadAllTextAsync(Path.Combine(coordinatorCommand.Directory, "prompt.md"));
    Require(coordinatorPrompt.Contains("Workflow phase: Planning", StringComparison.Ordinal) &&
            coordinatorPrompt.Contains("인수 기준", StringComparison.Ordinal) &&
            coordinatorPrompt.Contains("프로젝트가 완성될 때까지", StringComparison.Ordinal),
        "The Coordinator prompt must retain acceptance checks and control the entire workflow.");
    Require(coordinatorPrompt.Contains("native todo", StringComparison.OrdinalIgnoreCase) &&
            coordinatorPrompt.Contains("terminal-notes 자동화 이력은 열지 말고", StringComparison.Ordinal),
        "The role prompt omitted native todo tracking or the Coordinator's history restriction.");

    AutomationCommand planningContract = await AutomationCommandBuilder.PrepareNativeAsync(
        project,
        Claim(project, "planning contract", role: AutomationRole.Coordinator),
        "previous planning result");
    await WriteCliResponseAsync(planningContract,
        "{\"summary\":\"short plan\",\"instruction\":\"Reviewer verification: inspect\",\"complete\":false}");
    Require(await AutomationCommandBuilder.ReadCompletionAsync(planningContract) is { Success: false } invalidPlanning &&
            invalidPlanning.Summary.Contains("structured planning", StringComparison.OrdinalIgnoreCase),
        "A Planning Coordinator result without the structured plan was allowed through parsing.");
    await WriteCliResponseAsync(planningContract,
        "{\"summary\":\"rich plan\",\"instruction\":\"Reviewer verification: inspect\",\"complete\":false,\"planning\":{\"goalsAndScope\":\"fixture scope\",\"ideasAndChoices\":[\"fixture idea\"],\"designFilesTasks\":[\"fixture design\"],\"orderedImplementation\":[\"fixture step\"],\"risksAndEdgeCases\":[\"fixture risk\"],\"acceptanceTests\":[\"fixture test\"],\"serviceDemo\":\"fixture demo\"}}");
    Require(await AutomationCommandBuilder.ReadCompletionAsync(planningContract) is { Success: true, PlanningPlan: not null } validPlanning,
        "A structurally valid Planning Coordinator result was rejected.");
    const string roundTripPlanningJson = "{\"summary\":\"rich plan\",\"instruction\":\"Reviewer verification: inspect\",\"complete\":false,\"planning\":{\"goalsAndScope\":\"fixture scope\",\"ideasAndChoices\":[\"fixture idea\"],\"designFilesTasks\":[\"fixture design\"],\"orderedImplementation\":[\"fixture step\"],\"risksAndEdgeCases\":[\"fixture risk\"],\"acceptanceTests\":[\"fixture test\"],\"serviceDemo\":\"fixture demo\"}}";
    Require(AutomationStageOutputContract.TryParse(roundTripPlanningJson, AutomationRole.Coordinator,
        AutomationWorkflowPhase.Planning, out _, out string? selectedPlanningJson, out _) &&
        selectedPlanningJson is not null &&
        AutomationStageOutputContract.TryParse(selectedPlanningJson, AutomationRole.Coordinator,
            AutomationWorkflowPhase.Planning, out AutomationStageOutput? roundTripped, out _, out _) &&
        roundTripped?.PlanningPlan is not null &&
        selectedPlanningJson.Contains("goalsAndScope", StringComparison.Ordinal),
        "The structured planning result did not round-trip through its normalized JSON contract.");

    var reviewedClaim = Claim(project, "reviewed plan", role: AutomationRole.Reviewer) with
    {
        WorkflowPhase = AutomationWorkflowPhase.PlanReview,
        CoordinatorPlan = "Acceptance checks: requirements and service startup",
        LastReviewSummary = "previous design issue"
    };
    var planReview = await AutomationCommandBuilder.PrepareNativeAsync(project, reviewedClaim, "plan");
    Require(planReview.Command.Contains("Workflow phase: PlanReview", StringComparison.Ordinal) &&
            planReview.Command.Contains(reviewedClaim.CoordinatorPlan, StringComparison.Ordinal) &&
            planReview.Command.Contains("previous design issue", StringComparison.Ordinal) &&
            planReview.Command.Contains("구현·빌드·테스트·서비스 실행을 하지 마세요", StringComparison.Ordinal),
        "Pre-build review must receive the commander plan and prohibit implementation and execution.");
    var delivery = await AutomationCommandBuilder.PrepareNativeAsync(project,
        Claim(project, "launch", role: AutomationRole.Implementer) with { WorkflowPhase = AutomationWorkflowPhase.Delivery }, "verified");
    Require(delivery.Command.Contains("브라우저로 여세요", StringComparison.Ordinal) &&
            delivery.Command.Contains("deliveryEvidence", StringComparison.Ordinal) &&
            delivery.Command.Contains("중복 서버를 만들지 마세요", StringComparison.Ordinal),
        "Delivery must actually launch/show the service and avoid duplicate processes.");
    var finalReview = await AutomationCommandBuilder.PrepareNativeAsync(project,
        reviewedClaim with { DispatchToken = Guid.NewGuid(), WorkflowPhase = AutomationWorkflowPhase.FinalVerification,
            DeliveryEvidence = "fixture service PID 1234 is responding" }, "launched");
    Require(finalReview.Command.Contains("fixture service PID 1234 is responding", StringComparison.Ordinal) &&
            finalReview.Command.Contains("독립적으로 확인", StringComparison.Ordinal),
        "Final review must receive launch evidence and independently verify the running service.");

    const string approvalJson = """{"summary":"design checked","instruction":"build approved plan","complete":false,"planApproved":true,"deliveryEvidence":null}""";
    Require(AutomationStageOutputContract.TryParse(approvalJson, AutomationRole.Reviewer, out var approval, out _, out _) &&
            approval is { PlanApproved: true, DeliveryEvidence: null }, "Plan approval must survive native result parsing.");
    Require(AutomationStageOutputContract.TryParse(approvalJson, AutomationRole.Coordinator, out var unauthorizedApproval, out _, out _) &&
            unauthorizedApproval is { PlanApproved: false }, "Only the reviewer can approve a plan.");
    const string evidenceJson = """{"summary":"service checked","instruction":"","complete":true,"planApproved":false,"deliveryEvidence":"PID 1234; HTTP 200; browser visible"}""";
    Require(AutomationStageOutputContract.TryParse(evidenceJson, AutomationRole.Reviewer, out var evidence, out var savedEvidence, out _) &&
            evidence?.DeliveryEvidence == "PID 1234; HTTP 200; browser visible" && savedEvidence!.Contains("deliveryEvidence", StringComparison.Ordinal),
        "Independently verified delivery evidence must survive result normalization.");
    Require(!AutomationStageOutputContract.TryParse(approvalJson.Replace("\"planApproved\":true", "\"planApproved\":\"true\""),
        AutomationRole.Reviewer, out _, out _, out _), "A string must not be accepted as a plan approval boolean.");
    Require(!AutomationStageOutputContract.TryParse(evidenceJson.Replace("\"deliveryEvidence\":", "\"deliveryEvidence\":null,\"deliveryEvidence\":"),
        AutomationRole.Reviewer, out _, out _, out _), "Duplicate delivery evidence fields must be rejected.");

    SetFakeEnvironment(command, project, "success");
    using (var terminal = new TerminalProbe())
    {
        terminal.Start(project);
        await terminal.WaitForAsync("PS ", TimeSpan.FromSeconds(10));
        terminal.Session.Write(command.Command + "\r");
        await WaitForFileAsync(command.CompletionPath, TimeSpan.FromSeconds(15));
        await terminal.WaitForAsync("FAKE_DONE_한글", TimeSpan.FromSeconds(10));
    }

    AutomationStageOutput? output = await AutomationCommandBuilder.ReadCompletionAsync(command);
    Require(output is { Success: true, Complete: false, Tokens: 168 },
        $"Unexpected successful stage result: {output}");
    Require(output!.Summary == "가짜 결과 한글" && output.Instruction == "Reviewer verification: 다음 단계",
        "The UTF-8 structured result was corrupted.");
    Require(!File.Exists(attackPath), "Prompt contents were evaluated as PowerShell.");
    string recordedEvents = await File.ReadAllTextAsync(command.EventsPath, Encoding.UTF8);
    Require(recordedEvents.Contains("이벤트 한글", StringComparison.Ordinal),
        "PowerShell 5.1 JSONL output was not preserved as UTF-8. Events: " + recordedEvents);

    AutomationCommand inheritedModel = await AutomationCommandBuilder.PrepareAsync(project, Claim(project, "inherit configured model", model: null), "");
    SetFakeEnvironment(inheritedModel, project, "success", model: null);
    using (var terminal = new TerminalProbe())
    {
        terminal.Start(project);
        await terminal.WaitForAsync("PS ", TimeSpan.FromSeconds(10));
        terminal.Session.Write(inheritedModel.Command + "\r");
        await WaitForFileAsync(inheritedModel.CompletionPath, TimeSpan.FromSeconds(15));
    }
    Require(await AutomationCommandBuilder.ReadCompletionAsync(inheritedModel) is { Success: true, Tokens: 168 },
        "An empty model did not inherit the OpenCode configured default.");

    bool legacyModelRejected = false;
    try
    {
        _ = await AutomationCommandBuilder.PrepareAsync(project, Claim(project, "legacy bare model", model: "gpt-6-astra"), "");
    }
    catch (InvalidOperationException exception) when (exception.Message.Contains("provider/model", StringComparison.Ordinal) && exception.Message.Contains("새 플랜", StringComparison.Ordinal))
    {
        legacyModelRejected = true;
    }
    Require(legacyModelRejected, "A legacy bare model was accepted without clear reapproval guidance.");

    AutomationCommand reviewerOutput = await AutomationCommandBuilder.PrepareAsync(
        project, Claim(project, "review contract", role: AutomationRole.Reviewer), "구현 결과");
    string reviewerResponse = "The review is below.\n```json\n" +
        "{\"summary\":\"Unmet criterion: plan edits persist across restart\\nFailing evidence: the saved value resets after reopening\",\"instruction\":\"Implementer action: persist the latest value and verify it after reload\",\"complete\":false,\"extra\":\"safe to ignore\"}\n" +
        "```\nI also checked the current repository state.";
    await WriteCliResponseAsync(reviewerOutput, reviewerResponse);
    AutomationStageOutput? parsedReviewer = await AutomationCommandBuilder.ReadCompletionAsync(reviewerOutput);
    Require(parsedReviewer is { Success: true, Complete: false } && parsedReviewer.Instruction.StartsWith("Implementer action:", StringComparison.Ordinal),
        "The CLI parser did not accept the last valid Reviewer object with prose and extra fields.");

    AutomationCommand unsupportedReviewer = await AutomationCommandBuilder.PrepareAsync(
        project, Claim(project, "incomplete reviewer result", role: AutomationRole.Reviewer), "구현 결과");
    await WriteCliResponseAsync(unsupportedReviewer,
        "{\"summary\":\"Unmet criterion: reload preservation\",\"instruction\":\"Implementer action: persist the saved value\",\"complete\":false}");
    AutomationStageOutput? unsupportedReviewerOutput = await AutomationCommandBuilder.ReadCompletionAsync(unsupportedReviewer);
    Require(unsupportedReviewerOutput is { Success: true, Complete: false } &&
            unsupportedReviewerOutput.Instruction.StartsWith("Implementer action:", StringComparison.Ordinal),
        "The CLI parser rejected useful Reviewer feedback only because an optional evidence label was absent.");

    AutomationCommand verifiedReviewer = await AutomationCommandBuilder.PrepareAsync(
        project, Claim(project, "complete reviewer result", role: AutomationRole.Reviewer), "구현 결과");
    await WriteCliResponseAsync(verifiedReviewer,
        "{\"summary\":\"Verified criteria: saved plan survives reload\\nVerification evidence: reopened the project and confirmed the saved value\",\"instruction\":\"\",\"complete\":true,\"extra\":true}");
    AutomationStageOutput? verifiedReviewerOutput = await AutomationCommandBuilder.ReadCompletionAsync(verifiedReviewer);
    Require(verifiedReviewerOutput is { Success: true, Complete: true },
        "The CLI parser rejected a Reviewer completion with criteria and passing evidence.");

    AutomationCommand coordinatorAction = await AutomationCommandBuilder.PrepareAsync(
        project, Claim(project, "coordinator instruction", role: AutomationRole.Coordinator) with
        {
            WorkflowPhase = AutomationWorkflowPhase.Implementation
        }, "검토 결과");
    await WriteCliResponseAsync(coordinatorAction,
        "{\"summary\":\"Acceptance checks:\\n1. Plan edits survive reload.\",\"instruction\":\"Implementer action: persist the plan before reporting save success\",\"complete\":false}");
    Require(await AutomationCommandBuilder.ReadCompletionAsync(coordinatorAction) is { Success: true },
        "The Coordinator action prefix was rejected.");
    await WriteCliResponseAsync(coordinatorAction,
        "{\"summary\":\"Acceptance checks:\\n1. Plan edits survive reload.\",\"instruction\":\"Reviewer verification: check each acceptance criterion against the current state\",\"complete\":false}");
    Require(await AutomationCommandBuilder.ReadCompletionAsync(coordinatorAction) is { Success: true },
        "The Coordinator verification prefix was rejected.");
    await WriteCliResponseAsync(coordinatorAction,
        "{\"summary\":\"Acceptance checks:\\n1. Plan edits survive reload.\",\"instruction\":\"do more planning\",\"complete\":false}");
    Require(await AutomationCommandBuilder.ReadCompletionAsync(coordinatorAction) is { Success: true } normalizedCoordinator &&
            normalizedCoordinator.Instruction.StartsWith("Implementer action:", StringComparison.Ordinal),
        "The Coordinator instruction without a routing prefix was not normalized to Implementer.");
    await WriteCliResponseAsync(coordinatorAction,
        "{\"summary\":\"The task is ready.\",\"instruction\":\"Implementer action: persist the plan\",\"complete\":false}");
    Require(await AutomationCommandBuilder.ReadCompletionAsync(coordinatorAction) is { Success: true },
        "The Coordinator result was rejected only because its useful summary omitted a display label.");

    AutomationCommand coordinatorCannotComplete = await AutomationCommandBuilder.PrepareAsync(
        project, Claim(project, "coordinator cannot complete", role: AutomationRole.Coordinator) with
        {
            WorkflowPhase = AutomationWorkflowPhase.Implementation
        }, "plan");
    await WriteCliResponseAsync(coordinatorCannotComplete,
        "{\"summary\":\"Acceptance checks:\\n1. Plan edits survive reload.\",\"instruction\":\"Implementer action: persist the plan\",\"complete\":true}");
    Require(await AutomationCommandBuilder.ReadCompletionAsync(coordinatorCannotComplete) is { Success: true, Complete: false },
        "The Coordinator complete flag was not safely normalized to false.");

    AutomationCommand implementerCannotComplete = await AutomationCommandBuilder.PrepareAsync(
        project, Claim(project, "implementer cannot complete"), "task");
    await WriteCliResponseAsync(implementerCannotComplete,
        "{\"summary\":\"Implementation and build finished.\",\"instruction\":\"Reviewer verification: check the saved result\",\"complete\":true}");
    Require(await AutomationCommandBuilder.ReadCompletionAsync(implementerCannotComplete) is { Success: true, Complete: false },
        "The Implementer complete flag was not safely normalized to false.");

    AutomationCommand unlabeledReviewer = await AutomationCommandBuilder.PrepareAsync(
        project, Claim(project, "complete reviewer without English labels", role: AutomationRole.Reviewer), "구현 결과");
    await WriteCliResponseAsync(unlabeledReviewer,
        "{\"summary\":\"승인된 플랜의 모든 기준과 테스트 결과를 확인했고 이상이 없습니다.\",\"instruction\":\"\",\"complete\":true}");
    Require(await AutomationCommandBuilder.ReadCompletionAsync(unlabeledReviewer) is { Success: true, Complete: true },
        "A complete Reviewer result was rejected only because it used a natural-language summary.");

    AutomationCommand blankSummary = await AutomationCommandBuilder.PrepareAsync(
        project, Claim(project, "blank summary"), "previous result");
    await WriteCliResponseAsync(blankSummary,
        "{\"summary\":\"  \",\"instruction\":\"review changes\",\"complete\":false}");
    AutomationStageOutput? blankSummaryOutput = await AutomationCommandBuilder.ReadCompletionAsync(blankSummary);
    Require(blankSummaryOutput is { Success: false } && blankSummaryOutput.Summary.Contains("summary", StringComparison.OrdinalIgnoreCase),
        "The CLI parser accepted a blank summary.");

    AutomationCommand errorCommand = await AutomationCommandBuilder.PrepareAsync(project, Claim(project, "OpenCode error"), "");
    SetFakeEnvironment(errorCommand, project, "error");
    using (var terminal = new TerminalProbe())
    {
        terminal.Start(project);
        await terminal.WaitForAsync("PS ", TimeSpan.FromSeconds(10));
        terminal.Session.Write(errorCommand.Command + "\r");
        await WaitForFileAsync(errorCommand.CompletionPath, TimeSpan.FromSeconds(15));
    }
    AutomationStageOutput? errorOutput = await AutomationCommandBuilder.ReadCompletionAsync(errorCommand);
    Require(errorOutput is { Success: false } && errorOutput.Summary.Contains("OpenCode 오류", StringComparison.Ordinal),
        "An OpenCode error event did not fail the stage.");

    AutomationCommand cliStartupFailure = await AutomationCommandBuilder.PrepareAsync(project, Claim(project, "CLI startup diagnostic"), "");
    await File.WriteAllTextAsync(cliStartupFailure.CompletionPath, "{\"exitCode\":1,\"error\":\"\"}", new UTF8Encoding(false));
    await File.WriteAllTextAsync(cliStartupFailure.EventsPath,
        "unknown option --unexpected-flag\n" + new string('x', 5000) + "\n", new UTF8Encoding(false));
    AutomationStageOutput? cliStartupFailureOutput = await AutomationCommandBuilder.ReadCompletionAsync(cliStartupFailure);
    Require(cliStartupFailureOutput is { Success: false } &&
            cliStartupFailureOutput.Summary.Contains("unknown option --unexpected-flag", StringComparison.Ordinal),
        "A non-JSON OpenCode startup diagnostic was not surfaced for a nonzero CLI exit.");
    Require(cliStartupFailureOutput!.Summary.Length <= 940,
        "The surfaced CLI startup diagnostic was not bounded in length.");

    AutomationCommand interrupted = await AutomationCommandBuilder.PrepareAsync(project, Claim(project, "Ctrl+C 검증"), "");
    SetFakeEnvironment(interrupted, project, "wait");
    using (var terminal = new TerminalProbe())
    {
        terminal.Start(project);
        await terminal.WaitForAsync("PS ", TimeSpan.FromSeconds(10));
        terminal.Session.Write(interrupted.Command + "\r");
        await terminal.WaitForAsync("FAKE_WAITING", TimeSpan.FromSeconds(10));
        terminal.Session.Write("\x03");
        await WaitForFileAsync(interrupted.CompletionPath, TimeSpan.FromSeconds(10));
    }

    AutomationStageOutput? interruptedOutput = await AutomationCommandBuilder.ReadCompletionAsync(interrupted);
    Require(interruptedOutput is { Success: false }, "Ctrl+C did not produce a failed completion result.");
    Require(!File.Exists(interrupted.CompletionPath + ".tmp"), "Ctrl+C left only a temporary completion marker.");

    AutomationCommand malformedResult = await AutomationCommandBuilder.PrepareAsync(project, Claim(project, "malformed result"), "");
    await File.WriteAllTextAsync(malformedResult.CompletionPath, "{\"exitCode\":0,\"error\":\"\"}", new UTF8Encoding(false));
    await File.WriteAllTextAsync(malformedResult.EventsPath,
        "{\"type\":\"step_finish\",\"part\":{\"id\":\"bad\",\"tokens\":{\"input\":\"123\",\"output\":null}}}\n" +
        "{\"type\":null,\"usage\":null}\n", new UTF8Encoding(false));
    await File.WriteAllTextAsync(malformedResult.ResultPath, "{invalid result", new UTF8Encoding(false));
    AutomationStageOutput? malformedResultOutput = await AutomationCommandBuilder.ReadCompletionAsync(malformedResult);
    Require(malformedResultOutput is { Success: false, Tokens: 0 },
        "Malformed result or nonnumeric usage escaped as an exception or invented tokens.");

    AutomationCommand malformedMarker = await AutomationCommandBuilder.PrepareAsync(project, Claim(project, "malformed marker"), "");
    await File.WriteAllTextAsync(malformedMarker.CompletionPath, "{invalid marker", new UTF8Encoding(false));
    AutomationStageOutput? malformedMarkerOutput = await AutomationCommandBuilder.ReadCompletionAsync(malformedMarker);
    Require(malformedMarkerOutput is { Success: false, Tokens: 0 },
        "Malformed completion marker escaped as an exception.");

    Console.WriteLine("OpenCode automation PowerShell 5.1 smoke tests passed.");
}
finally
{
    Environment.SetEnvironmentVariable("PATH", originalPath);
    Environment.SetEnvironmentVariable("TN_EXPECTED_PROMPT", null);
    Environment.SetEnvironmentVariable("TN_EXPECTED_PROJECT", null);
    Environment.SetEnvironmentVariable("TN_FAKE_MODE", null);
    Environment.SetEnvironmentVariable("TN_EXPECTED_MODEL", null);
    if (Directory.Exists(fixture)) Directory.Delete(fixture, recursive: true);
}

static AutomationStepClaim Claim(string project, string plan, string? model = "fake/provider-model", AutomationRole role = AutomationRole.Implementer) => new(
    Guid.NewGuid(), Guid.NewGuid(), 1, role, 1, project, plan,
    new AutomationExecutionOptions(ImplementerModel: model), 0, null, null, DateTimeOffset.UtcNow);

static async Task WriteCliResponseAsync(AutomationCommand command, string response)
{
    await File.WriteAllTextAsync(command.CompletionPath, "{\"exitCode\":0,\"error\":\"\"}", new UTF8Encoding(false));
    string eventLine = JsonSerializer.Serialize(new { type = "text", part = new { text = response } });
    await File.WriteAllTextAsync(command.EventsPath, eventLine + Environment.NewLine, new UTF8Encoding(false));
}

static void SetFakeEnvironment(AutomationCommand command, string project, string mode, string? model = "fake/provider-model")
{
    Environment.SetEnvironmentVariable("TN_EXPECTED_PROMPT", Path.Combine(command.Directory, "prompt.md"));
    Environment.SetEnvironmentVariable("TN_EXPECTED_PROJECT", project);
    Environment.SetEnvironmentVariable("TN_FAKE_MODE", mode);
    Environment.SetEnvironmentVariable("TN_EXPECTED_MODEL", model);
}

static void WriteFakeOpenCode(string directory)
{
    string executable = Environment.ProcessPath ?? throw new InvalidOperationException("The test executable path is unavailable.");
    File.WriteAllText(Path.Combine(directory, "opencode.cmd"),
        "@echo off\r\n\"" + executable + "\" --fake-opencode %*\r\nexit /b %ERRORLEVEL%\r\n",
        Encoding.ASCII);
}

static int RunFakeOpenCode(string[] fakeArgs)
{
    Console.InputEncoding = new UTF8Encoding(false);
    Console.OutputEncoding = new UTF8Encoding(false);
    string received = Console.In.ReadToEnd();
    string expected = File.ReadAllText(Environment.GetEnvironmentVariable("TN_EXPECTED_PROMPT")!, Encoding.UTF8);
    if (received != expected && received != expected + Environment.NewLine)
    {
        Console.Error.WriteLine("STDIN_MISMATCH");
        return 41;
    }

    var expectedArguments = new List<string> { "run", "--format", "json", "--auto" };
    string? expectedModel = Environment.GetEnvironmentVariable("TN_EXPECTED_MODEL");
    if (!string.IsNullOrEmpty(expectedModel)) { expectedArguments.Add("--model"); expectedArguments.Add(expectedModel); }
    if (!fakeArgs.SequenceEqual(expectedArguments, StringComparer.Ordinal))
    {
        Console.Error.WriteLine("ARGUMENT_MISMATCH: " + string.Join('|', fakeArgs));
        return 42;
    }
    if (!string.Equals(Environment.CurrentDirectory, Environment.GetEnvironmentVariable("TN_EXPECTED_PROJECT"), StringComparison.OrdinalIgnoreCase))
    {
        Console.Error.WriteLine("WORKING_DIRECTORY_MISMATCH: " + Environment.CurrentDirectory);
        return 43;
    }
    if (Environment.GetEnvironmentVariable("TN_FAKE_MODE") == "wait")
    {
        Console.WriteLine("FAKE_WAITING");
        while (true) Thread.Sleep(100);
    }

    if (Environment.GetEnvironmentVariable("TN_FAKE_MODE") == "error")
    {
        Console.WriteLine("{\"type\":\"error\",\"sessionID\":\"error-session\",\"error\":{\"name\":\"FakeError\",\"message\":\"가짜 오류\"}}");
        return 0;
    }

    string report = JsonSerializer.Serialize("{\"summary\":\"가짜 결과 한글\",\"instruction\":\"Reviewer verification: 다음 단계\",\"complete\":false,\"planning\":{\"goalsAndScope\":\"fixture scope\",\"ideasAndChoices\":[\"fixture choice\"],\"designFilesTasks\":[\"fixture design\"],\"orderedImplementation\":[\"fixture step\"],\"risksAndEdgeCases\":[\"fixture risk\"],\"acceptanceTests\":[\"fixture test\"],\"serviceDemo\":\"fixture service demo\"}}");
    Console.WriteLine("{\"type\":\"text\",\"sessionID\":\"fake-session\",\"part\":{\"id\":\"text-part\",\"type\":\"text\",\"text\":" + report + "}}");
    Console.WriteLine("{\"type\":\"step_finish\",\"sessionID\":\"fake-session\",\"label\":\"이벤트 한글\",\"part\":{\"id\":\"usage-part\",\"tokens\":{\"input\":123,\"output\":45,\"reasoning\":0,\"cache\":{\"read\":0,\"write\":0}}}}");
    Console.WriteLine("FAKE_DONE_한글");
    return 0;
}

static async Task WaitForFileAsync(string path, TimeSpan timeout)
{
    using var cancellation = new CancellationTokenSource(timeout);
    while (!File.Exists(path)) await Task.Delay(50, cancellation.Token);
}

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

sealed class TerminalProbe : IDisposable
{
    private readonly ConcurrentQueue<string> _chunks = new();
    private readonly SemaphoreSlim _changed = new(0);
    public ConPtySession Session { get; } = new();
    public string Text => string.Concat(_chunks);

    public TerminalProbe()
    {
        Session.OutputReceived += text =>
        {
            _chunks.Enqueue(text);
            try { _changed.Release(); } catch (ObjectDisposedException) { }
        };
    }

    public void Start(string workingDirectory) => Session.Start(workingDirectory, 100, 30);

    public async Task WaitForAsync(string marker, TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        while (!Text.Contains(marker, StringComparison.Ordinal))
        {
            try { await _changed.WaitAsync(cancellation.Token); }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                throw new TimeoutException($"Timed out waiting for {marker}. Output: {Text.Replace("\x1b", "<ESC>", StringComparison.Ordinal)}");
            }
        }
    }

    public void Dispose()
    {
        Session.Dispose();
        _changed.Dispose();
    }
}
