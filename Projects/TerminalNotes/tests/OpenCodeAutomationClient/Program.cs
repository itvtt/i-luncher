using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using TerminalNotes.Services;

namespace TerminalNotes.Services;

internal static class Program
{
    private static readonly Uri ServerUri = new("http://127.0.0.1:43812/");
    private static readonly string ProjectDirectory = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "TerminalNotes automation fake project"));
    private const string SessionId = "ses_role_123";
    private const string RolePrompt = "exact role prompt body";

    private static async Task<int> Main()
    {
        string? oldPassword = Environment.GetEnvironmentVariable("OPENCODE_SERVER_PASSWORD");
        string? oldUsername = Environment.GetEnvironmentVariable("OPENCODE_SERVER_USERNAME");
        try
        {
            Environment.SetEnvironmentVariable("OPENCODE_SERVER_PASSWORD", "automation-test-password");
            Environment.SetEnvironmentVariable("OPENCODE_SERVER_USERNAME", "automation-test");
            await VerifyNativeCreateSelectAndSubmitContractAsync();
            await VerifyDefaultModelIsOmittedAsync();
            await VerifyStaleAssistantDoesNotCompleteOrCountAsync();
            await VerifyBusyPartialAssistantIsPendingAsync();
            await VerifyPermissionRequestBlocksCompletionAsync();
            await VerifyMalformedFinalJsonFailsAsync();
            await VerifyReviewerOutputContractAndProseParsingAsync();
            await VerifyReviewerCompletionRequiresPositiveEvidenceAsync();
            await VerifyBlankSummaryFailsAsync();
            await VerifyPlanningRequiresExpandedPlanAsync();
            await VerifyNativeAssistantErrorFailsAsync();
            await VerifyNativeToolErrorFailsAsync();
            await VerifyWrongSessionMessageFailsAsync();
            await VerifyTransientReadsRecoverWithoutResubmissionAsync();
            await VerifyAmbiguousPromptPostIsReconciledWithoutRetryAsync();
            await VerifyStalledTurnRecoveryAsync();
            await VerifyProlongedRetryAndOutageAsync();
            await VerifyOwnedTuiRebindingAsync();
            await VerifyProgressAndRecoveryGuardsAsync();
            await VerifyToolsAndDescendantsAsync();
            await VerifyAmbiguousAbortAsync();
            await VerifyEscalationGuardsAsync();
            await VerifyIdleWithOrphanedToolAsync();
            await VerifyActivityEvidenceAsync();
            await VerifyCoordinatorInterventionAsync();
            Console.WriteLine("PASS: native OpenCode automation stays in the exact visible root session and only accepts its terminal JSON response.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("FAIL: " + exception);
            return 1;
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENCODE_SERVER_PASSWORD", oldPassword);
            Environment.SetEnvironmentVariable("OPENCODE_SERVER_USERNAME", oldUsername);
        }
    }

    private static async Task VerifyNativeCreateSelectAndSubmitContractAsync()
    {
        var state = new FakeState();
        using var handler = new FakeHandler(state);
        using var http = new HttpClient(handler);
        using var client = new OpenCodeAutomationClient(ServerUri, ProjectDirectory, http);
        int callbackCount = 0;
        OpenCodeAutomationDispatch dispatch = await client.StartAsync(
            "Coordinator · stage 2",
            RolePrompt,
            "provider/model-id",
            beforeSubmit: () =>
            {
                callbackCount++;
                Require(handler.Requests.Count == 4, "The final submit guard did not run after the TUI selected the new session and immediately before prompt_async.");
            });

        Require(callbackCount == 1, "The final submit guard was not called exactly once.");
        Require(dispatch.SessionId == SessionId && dispatch.Prompt == RolePrompt && dispatch.MessageId.StartsWith("msg_", StringComparison.Ordinal),
            "StartAsync did not return the exact created session, generated message ID, and prompt.");
        RecordedRequest[] requests = handler.Requests.ToArray();
        Require(requests.Length == 5, "Session creation, TUI selection, and prompt submission did not each occur exactly once.");
        Require(requests[0].Method == HttpMethod.Get && requests[0].Uri.AbsolutePath == "/session/status", "The native readiness read did not precede mutation.");
        Require(requests[1].Method == HttpMethod.Post && requests[1].Uri.AbsolutePath == "/session", "The client did not create a dedicated session through POST /session.");
        Require(requests[2].Method == HttpMethod.Get && requests[2].Uri.AbsolutePath == "/session/" + SessionId, "The created root session was not verified by exact ID.");
        Require(requests[3].Method == HttpMethod.Post && requests[3].Uri.AbsolutePath == "/tui/select-session", "The client did not select the exact session in the visible TUI.");
        Require(requests[4].Method == HttpMethod.Post && requests[4].Uri.AbsolutePath == "/session/" + SessionId + "/prompt_async", "The client did not submit the prompt asynchronously to the exact root session.");
        Require(requests.All(request => request.Uri.Query.Contains("directory=" + Uri.EscapeDataString(ProjectDirectory), StringComparison.Ordinal)),
            "A native automation request omitted its directory scope.");
        Require(requests.All(request => request.Authorization == "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("automation-test:automation-test-password"))),
            "OpenCode basic authentication was not applied to every native request.");

        using JsonDocument create = JsonDocument.Parse(requests[1].Body);
        Require(create.RootElement.GetProperty("title").GetString() == "Coordinator · stage 2", "The dedicated session title was not preserved.");
        using JsonDocument selection = JsonDocument.Parse(requests[3].Body);
        Require(selection.RootElement.GetProperty("sessionID").GetString() == SessionId, "TUI selection targeted a different session.");
        using JsonDocument prompt = JsonDocument.Parse(requests[4].Body);
        Require(prompt.RootElement.GetProperty("messageID").GetString() == dispatch.MessageId, "The prompt did not use its returned exact message ID.");
        Require(prompt.RootElement.GetProperty("parts")[0].GetProperty("text").GetString() == RolePrompt, "The exact role prompt was not sent as a native text part.");
        Require(prompt.RootElement.GetProperty("model").GetProperty("providerID").GetString() == "provider" &&
                prompt.RootElement.GetProperty("model").GetProperty("modelID").GetString() == "model-id",
            "The optional provider/model pair used an unexpected API schema.");
    }

    private static async Task VerifyDefaultModelIsOmittedAsync()
    {
        var state = new FakeState();
        using var handler = new FakeHandler(state);
        using var http = new HttpClient(handler);
        using var client = new OpenCodeAutomationClient(ServerUri, ProjectDirectory, http);
        _ = await client.StartAsync("Implementer · stage 1", RolePrompt, null);
        using JsonDocument prompt = JsonDocument.Parse(handler.Requests[^1].Body);
        Require(!prompt.RootElement.TryGetProperty("model", out _), "An absent model was serialized as null instead of being omitted.");
    }

    private static async Task VerifyStaleAssistantDoesNotCompleteOrCountAsync()
    {
        using StartedClient started = await StartClientAsync();
        started.State.StatusJson = "{}";
        started.State.MessagesJson = JsonSerializer.Serialize(new[]
        {
            User("msg_old_user", SessionId, "older prompt", 1000),
            Assistant("msg_old_assistant", SessionId, "msg_old_user", "{\"summary\":\"stale\",\"instruction\":\"stale\",\"complete\":true}", 2000, 3000, "stop", 900),
            User(started.Dispatch.MessageId, SessionId, RolePrompt, 4000),
            Assistant("msg_current_assistant", SessionId, started.Dispatch.MessageId, "{\"summary\":\"current result\",\"instruction\":\"inspect the change\",\"complete\":false}", 5000, 6000, "stop", 37)
        });

        AutomationStageOutput? output = await started.Client.ReadCompletionAsync(started.Dispatch);
        Require(output is { Success: true, Summary: "current result", Instruction: "inspect the change", Complete: false, Tokens: 37 },
            "A stale assistant response was accepted or its token usage leaked into the exact role result.");
    }

    private static async Task VerifyBusyPartialAssistantIsPendingAsync()
    {
        using StartedClient started = await StartClientAsync();
        started.State.StatusJson = "{\"" + SessionId + "\":{\"type\":\"busy\"}}";
        started.State.MessagesJson = JsonSerializer.Serialize(new[]
        {
            User(started.Dispatch.MessageId, SessionId, RolePrompt, 4000),
            Assistant("msg_partial_assistant", SessionId, started.Dispatch.MessageId, "{\"summary\":", 5000, null, null, 12)
        });

        AutomationStageOutput? output = await started.Client.ReadCompletionAsync(started.Dispatch);
        Require(output is null, "A busy partial assistant message was treated as a completed response.");
    }

    private static async Task VerifyPermissionRequestBlocksCompletionAsync()
    {
        using StartedClient started = await StartClientAsync();
        started.State.StatusJson = "{}";
        started.State.PermissionsJson = "[{\"id\":\"perm-1\",\"sessionID\":\"" + SessionId + "\"}]";
        started.State.MessagesJson = JsonSerializer.Serialize(new[]
        {
            User(started.Dispatch.MessageId, SessionId, RolePrompt, 4000),
            Assistant("msg_current_assistant", SessionId, started.Dispatch.MessageId, "{\"summary\":\"done\",\"instruction\":\"next\",\"complete\":true}", 5000, 6000, "stop", 12)
        });

        AutomationStageOutput? output = await started.Client.ReadCompletionAsync(started.Dispatch);
        Require(output is null, "A native permission request did not block completion.");
    }

    private static async Task VerifyMalformedFinalJsonFailsAsync()
    {
        using StartedClient started = await StartClientAsync();
        started.State.MessagesJson = FinalMessages(started.Dispatch, "{ summary: invalid }");
        AutomationStageOutput? output = await started.Client.ReadCompletionAsync(started.Dispatch);
        Require(output is { Success: false } && output.Summary.Contains("valid standalone JSON", StringComparison.Ordinal),
            "Malformed final assistant JSON was accepted as a stage result.");
    }

    private static async Task VerifyReviewerOutputContractAndProseParsingAsync()
    {
        using StartedClient started = await StartClientAsync(AutomationRole.Reviewer);
        started.State.MessagesJson = FinalMessages(started.Dispatch,
            "Review checked this result. Earlier sample: {\"summary\":\"Verified criteria: all plan items\",\"instruction\":\"\",\"complete\":true}.\n```json\n" +
            "{\"summary\":\"Unmet criterion: the saved plan survives restart\\nFailing evidence: reopening the project loses the edited plan\",\"instruction\":\"Implementer action: persist the plan before acknowledging save and verify it after reload\",\"complete\":false,\"diagnostic\":{\"source\":\"review\"}}\n" +
            "```\nThe extra diagnostic field is informational.");

        AutomationStageOutput? output = await started.Client.ReadCompletionAsync(started.Dispatch);
        Require(started.Dispatch.Role == AutomationRole.Reviewer &&
                output is { Success: true, Complete: false } &&
                output.Summary.StartsWith("Unmet criterion:", StringComparison.Ordinal) &&
                output.Instruction.StartsWith("Implementer action:", StringComparison.Ordinal),
            $"The Reviewer contract rejected safe prose/fenced JSON or accepted an incomplete review without concrete evidence. Role={started.Dispatch.Role}; output={output}.");
    }

    private static async Task VerifyBlankSummaryFailsAsync()
    {
        using StartedClient started = await StartClientAsync();
        started.State.MessagesJson = FinalMessages(started.Dispatch,
            "{\"summary\":\"  \",\"instruction\":\"continue\",\"complete\":false,\"extra\":true}");
        AutomationStageOutput? output = await started.Client.ReadCompletionAsync(started.Dispatch);
        Require(output is { Success: false } && output.Summary.Contains("summary", StringComparison.OrdinalIgnoreCase),
            "A blank stage summary or an otherwise valid object with an extra field was accepted.");
    }

    private static async Task VerifyReviewerCompletionRequiresPositiveEvidenceAsync()
    {
        using StartedClient started = await StartClientAsync(AutomationRole.Reviewer);
        started.State.MessagesJson = FinalMessages(started.Dispatch,
            "{\"summary\":\"Verified criteria: plan edit persists after reload\\nVerification evidence: reopened project and confirmed the saved contents\",\"instruction\":\"\",\"complete\":true,\"extra\":1}");

        AutomationStageOutput? output = await started.Client.ReadCompletionAsync(started.Dispatch);
        Require(output is { Success: true, Complete: true },
            "A Reviewer result with verified criteria and positive evidence was rejected.");

        using StartedClient unsupported = await StartClientAsync(AutomationRole.Reviewer);
        unsupported.State.MessagesJson = FinalMessages(unsupported.Dispatch,
            "{\"summary\":\"The plan is complete\",\"instruction\":\"\",\"complete\":true}");
        AutomationStageOutput? unsupportedOutput = await unsupported.Client.ReadCompletionAsync(unsupported.Dispatch);
        Require(unsupportedOutput is { Success: true, Complete: true },
            "The Reviewer completion was rejected only because its useful summary omitted fixed English labels.");
    }

    private static async Task VerifyNativeAssistantErrorFailsAsync()
    {
        using StartedClient started = await StartClientAsync();
        started.State.MessagesJson = JsonSerializer.Serialize(new[]
        {
            User(started.Dispatch.MessageId, SessionId, RolePrompt, 4000),
            Assistant("msg_error_assistant", SessionId, started.Dispatch.MessageId, "{\"summary\":\"looks complete\",\"instruction\":\"\",\"complete\":true}", 5000, 6000, "stop", 12,
                new { name = "ProviderError", data = new { message = "rate limit" } })
        });
        AutomationStageOutput? output = await started.Client.ReadCompletionAsync(started.Dispatch);
        Require(output is { Success: false } && output.Summary.Contains("ProviderError", StringComparison.Ordinal),
            "A native assistant error was hidden by plausible response text.");
    }

    private static async Task VerifyNativeToolErrorFailsAsync()
    {
        using StartedClient started = await StartClientAsync();
        started.State.MessagesJson = JsonSerializer.Serialize(new object[]
        {
            User(started.Dispatch.MessageId, SessionId, RolePrompt, 4000),
            new
            {
                info = new
                {
                    id = "msg_tool_error_assistant",
                    sessionID = SessionId,
                    role = "assistant",
                    parentID = started.Dispatch.MessageId,
                    time = new { created = 5000L, completed = (long?)6000 },
                    finish = "stop",
                    tokens = new { total = 12L },
                    error = (object?)null
                },
                parts = new object[]
                {
                    new { type = "tool", state = new { status = "error" } },
                    new { type = "text", text = "{\"summary\":\"looks complete\",\"instruction\":\"inspect\",\"complete\":true}" }
                }
            }
        });

        AutomationStageOutput? output = await started.Client.ReadCompletionAsync(started.Dispatch);
        Require(output is { Success: false } && output.Summary.Contains("tool error", StringComparison.OrdinalIgnoreCase),
            "A tool error in the native role response was hidden by plausible final JSON.");
    }

    private static async Task VerifyWrongSessionMessageFailsAsync()
    {
        using StartedClient started = await StartClientAsync();
        started.State.MessagesJson = JsonSerializer.Serialize(new[]
        {
            User(started.Dispatch.MessageId, "ses_wrong_session", RolePrompt, 4000)
        });
        AutomationStageOutput? output = await started.Client.ReadCompletionAsync(started.Dispatch);
        Require(output is null && started.Client.LastReadWarning?.Contains("different session", StringComparison.Ordinal) == true,
            "A message from another OpenCode session was accepted as this role's completion.");
        started.State.MessagesJson = JsonSerializer.Serialize(new[]
        {
            User(started.Dispatch.MessageId, SessionId, RolePrompt, 4000),
            User(started.Dispatch.MessageId, SessionId, RolePrompt, 4000)
        });
        Require(await started.Client.ReadCompletionAsync(started.Dispatch) is null &&
                started.Client.LastReadWarning?.Contains("more than once", StringComparison.Ordinal) == true && !started.Client.IsStalled,
            "Duplicate request identity escaped validation or left stale evidence eligible for recovery.");
    }

    private static async Task VerifyTransientReadsRecoverWithoutResubmissionAsync()
    {
        foreach (string failure in new[] { "disconnect", "timeout", "server503" })
        {
            using StartedClient started = await StartClientAsync();
            started.State.MessagesJson = FinalMessages(started.Dispatch, "{\"summary\":\"recovered\",\"instruction\":\"continue\",\"complete\":true}");
            started.State.TransientReadFailure = failure;
            started.State.TransientReadFailuresRemaining = 1;

            AutomationStageOutput? pending = await started.Client.ReadCompletionAsync(started.Dispatch);
            Require(pending is null, $"A transient native {failure} read was treated as a failed automation stage.");
            Require(!string.IsNullOrWhiteSpace(started.Client.LastReadWarning), $"A transient native {failure} read had no diagnostic warning.");
            Require(started.Handler.Requests.Count(request => request.Method == HttpMethod.Post && request.Uri.AbsolutePath == "/session/" + SessionId + "/prompt_async") == 1,
                $"A transient native {failure} read caused an additional prompt submission.");

            AutomationStageOutput? recovered = await started.Client.ReadCompletionAsync(started.Dispatch);
            Require(recovered is { Success: true, Summary: "recovered", Tokens: 12 }, $"The native {failure} read did not recover on the next poll.");
            Require(started.Client.LastReadWarning is null, "A recovered read kept a stale transport warning.");
            Require(started.Handler.Requests.Count(request => request.Method == HttpMethod.Post && request.Uri.AbsolutePath == "/session/" + SessionId + "/prompt_async") == 1,
                $"Recovering from a native {failure} read submitted the role prompt more than once.");
        }
    }

    private static async Task VerifyPlanningRequiresExpandedPlanAsync()
    {
        var state = new FakeState();
        using var handler = new FakeHandler(state);
        using var http = new HttpClient(handler);
        using var client = new OpenCodeAutomationClient(ServerUri, ProjectDirectory, http);
        var dispatch = await client.StartAsync("Coordinator planning",
            "Terminal Notes 자동화 · Coordinator · 회차 1\nWorkflow phase: Planning", null);
        Require(dispatch.WorkflowPhase == AutomationWorkflowPhase.Planning && dispatch.Role == AutomationRole.Coordinator,
            "The native dispatch lost its exact planning phase/role.");
        state.MessagesJson = FinalMessages(dispatch, "{\"summary\":\"검토하세요\",\"instruction\":\"review\",\"complete\":false}");
        var incomplete = await client.ReadCompletionAsync(dispatch);
        Require(incomplete is { Success: false } && incomplete.Summary.Contains("planning", StringComparison.OrdinalIgnoreCase),
            "A one-line coordinator handoff bypassed native planning validation.");
        state.MessagesJson = FinalMessages(dispatch, JsonSerializer.Serialize(new
        {
            summary = "계산기 설계와 검증 계획을 정리했습니다.", instruction = "Reviewer verification: 각 설계 결정을 검토하세요.",
            complete = false, planning = new
            {
                goalsAndScope = "기본 사칙 연산 웹 계산기; 계정/결제 기능은 범위 밖",
                ideasAndChoices = new[] { "키보드 입력과 클릭 입력을 같은 연산 로직에 연결한다." },
                designFilesTasks = new[] { "calc.js 순수 연산; index.html 화면; test.js 회귀 테스트" },
                orderedImplementation = new[] { "연산 함수를 먼저 구현하고 테스트한 뒤 화면을 연결한다." },
                risksAndEdgeCases = new[] { "0 나누기 오류 표시 뒤 새 숫자로 초기화한다." },
                acceptanceTests = new[] { "node test.js에서 소수점·연속 연산·0 나누기를 검증한다." },
                serviceDemo = "프로젝트에서 로컬 서버 실행 후 실제 URL을 열어 클릭·키보드 연산을 확인한다."
            }
        }));
        var expanded = await client.ReadCompletionAsync(dispatch);
        Require(expanded is { Success: true, PlanningPlan: not null }, "Structured native planning output was not retained.");
        Require(handler.Requests.Count(request => request.Method == HttpMethod.Post && request.Uri.AbsolutePath.EndsWith("/prompt_async")) == 1,
            "Planning result validation posted another prompt instead of returning a result.");
    }

    private static async Task VerifyAmbiguousPromptPostIsReconciledWithoutRetryAsync()
    {
        var state = new FakeState { ThrowOnPrompt = true };
        using var handler = new FakeHandler(state);
        using var http = new HttpClient(handler);
        using var client = new OpenCodeAutomationClient(ServerUri, ProjectDirectory, http);
        OpenCodeAutomationDispatch dispatch = await client.StartAsync("Reviewer · stage 3", RolePrompt, null);
        Require(handler.Requests.Count(request => request.Method == HttpMethod.Post && request.Uri.AbsolutePath == "/session/" + SessionId + "/prompt_async") == 1,
            "An ambiguous prompt_async POST was retried.");

        // The transport response can be lost after OpenCode accepted the exact
        // message. Reconcile that identity on the read path rather than creating a
        // second session/message that could execute the role twice.
        state.MessagesJson = FinalMessages(
            dispatch,
            "{\"summary\":\"reconciled\",\"instruction\":\"\",\"complete\":true}");
        AutomationStageOutput? output = await client.ReadCompletionAsync(dispatch);
        Require(output is { Success: true, Summary: "reconciled" },
            "An ambiguously acknowledged prompt was not reconciled from its exact native message.");
        Require(handler.Requests.Count(request => request.Method == HttpMethod.Post && request.Uri.AbsolutePath == "/session/" + SessionId + "/prompt_async") == 1,
            "Reconciling an ambiguous prompt submitted it again.");
    }

    private static async Task VerifyProlongedRetryAndOutageAsync()
    {
        var clock = new TestClock();
        using StartedClient retry = await StartClientAsync(AutomationRole.Implementer, clock);
        SetBusy(retry, "");
        retry.State.StatusJson = JsonSerializer.Serialize(new Dictionary<string, object> { [SessionId] = new { type = "retry" } });
        await PollForAsync(retry, clock, 40);
        Require(!retry.Client.IsStalled && retry.Client.LastActivityAt is null, "Short retry or empty assistant was treated as work/stall.");
        await PollForAsync(retry, clock, 160);
        Require(retry.Client.IsStalled, "Provider retry could suppress the watchdog indefinitely.");
        await retry.Client.RecoverStalledAsync(retry.Dispatch, () => true);
        Require(AbortCount(retry) == 1 && PromptCount(retry) == 1, "Prolonged retry did not abort the exact owned turn without replay.");

        foreach (string failure in new[] { "server503", "disconnect", "timeout" })
        {
            using StartedClient outage = await StartClientAsync(AutomationRole.Implementer, clock);
            SetBusy(outage);
            await outage.Client.ReadCompletionAsync(outage.Dispatch);
            outage.State.TransientReadFailure = failure;
            outage.State.TransientReadFailuresRemaining = 1000;
            await PollForAsync(outage, clock, 190);
            Require(outage.Client.CanRestartUnresponsiveProcess && !outage.Client.IsStalled,
                "Sustained local API outage did not authorize owned-process recovery: " + failure);
            Require(AbortCount(outage) == 0 && PromptCount(outage) == 1, "Uncertain API reads submitted side effects.");
            outage.State.TransientReadFailuresRemaining = 0;
            await outage.Client.ReadCompletionAsync(outage.Dispatch);
            Require(!outage.Client.CanRestartUnresponsiveProcess, "A healthy snapshot retained outage escalation.");
            outage.State.QuestionsJson = JsonSerializer.Serialize(new[] { new { id = "q", sessionID = SessionId } });
            await outage.Client.ReadCompletionAsync(outage.Dispatch);
            outage.State.TransientReadFailuresRemaining = 1000;
            await PollForAsync(outage, clock, 190);
            Require(!outage.Client.CanRestartUnresponsiveProcess, "Outage overrode a known question wait.");
        }
        Console.WriteLine("PASS prolonged provider retry and local outages recover without manual continue or duplicate prompts");
    }

    private static async Task VerifyOwnedTuiRebindingAsync()
    {
        var clock = new TestClock();
        using StartedClient started = await StartClientAsync(null, clock);
        SetBusy(started);
        await started.Client.ReadCompletionAsync(started.Dispatch);
        int Selects() => started.Handler.Requests.Count(request => request.Uri.AbsolutePath == "/tui/select-session");
        await started.Client.RefreshTuiSessionAsync(started.Dispatch, () => true);
        Require(Selects() == 2 && PromptCount(started) == 1, "TUI startup rebind resent a prompt or selected no session.");
        await started.Client.RefreshTuiSessionAsync(started.Dispatch, () => true);
        Require(Selects() == 2, "TUI refresh floods each poll.");
        clock.Advance(16);
        await started.Client.RefreshTuiSessionAsync(started.Dispatch, () => false);
        Require(Selects() == 2, "TUI selection ignored ownership loss.");
        await started.Client.RefreshTuiSessionAsync(started.Dispatch, () => true);
        Require(Selects() == 3 && PromptCount(started) == 1, "Owned TUI did not rebind periodically.");
        started.State.QuestionsJson = JsonSerializer.Serialize(new[] { new { id = "q", sessionID = SessionId } });
        await started.Client.ReadCompletionAsync(started.Dispatch);
        clock.Advance(16);
        await started.Client.RefreshTuiSessionAsync(started.Dispatch, () => true);
        Require(Selects() == 3, "TUI rebind interrupted a question.");
        Console.WriteLine("PASS exact-session terminal rebinding respects ownership/input and never resends work");
    }

    private static async Task VerifyStalledTurnRecoveryAsync()
    {
        var clock = new TestClock();
        using StartedClient started = await StartClientAsync(AutomationRole.Implementer, clock);
        SetBusy(started);
        await PollForAsync(started, clock, 190);
        Require(started.Client.IsStalled, "A constant busy status concealed a stalled exact role turn.");
        Require(await started.Client.RecoverStalledAsync(started.Dispatch, () => true) is null,
            "An abort POST alone was treated as proof of quiescence.");
        Require(started.Client.IsRecovering && AbortCount(started) == 1, "The exact stalled root was not aborted once.");
        Require(await started.Client.ReadCompletionAsync(started.Dispatch) is null, "Recovery skipped the stable idle confirmation.");
        clock.Advance(1);
        Require(await started.Client.ReadCompletionAsync(started.Dispatch) is { Success: false, Interrupted: true },
            "A confirmed stopped turn did not produce the interrupted-work handoff.");
        Require(PromptCount(started) == 1, "Recovery replayed the mutating prompt.");
        Console.WriteLine("PASS stalled busy turn aborts once and hands off only after stable native idle");
    }

    private static async Task VerifyProgressAndRecoveryGuardsAsync()
    {
        var clock = new TestClock();
        using StartedClient started = await StartClientAsync(null, clock);
        SetBusy(started);
        for (int iteration = 0; iteration < 25; iteration++)
        {
            await PollForAsync(started, clock, 100);
            SetBusy(started, "reasoning " + iteration);
            await started.Client.ReadCompletionAsync(started.Dispatch);
            Require(!started.Client.IsStalled, "Substantive response progress was stopped by an overall duration cap.");
        }
        await PollForAsync(started, clock, 190);
        SetBusy(started, "new progress before abort");
        await started.Client.RecoverStalledAsync(started.Dispatch, () => true);
        Require(AbortCount(started) == 0 && !started.Client.IsStalled, "Progress was not rechecked before abort.");

        await PollForAsync(started, clock, 190);
        started.State.PermissionsJson = JsonSerializer.Serialize(new[] { new { id = "permission", sessionID = SessionId } });
        await started.Client.RecoverStalledAsync(started.Dispatch, () => true);
        Require(AbortCount(started) == 0, "A permission request arriving before abort was interrupted.");
        await PollForAsync(started, clock, 200);
        started.State.PermissionsJson = "[]";
        await started.Client.ReadCompletionAsync(started.Dispatch);
        Require(!started.Client.IsStalled, "Time waiting for permission consumed the inactivity window.");

        await PollForAsync(started, clock, 190);
        await started.Client.RecoverStalledAsync(started.Dispatch, () => false);
        Require(AbortCount(started) == 0, "Loss of scheduler ownership did not block abort.");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try
        {
            await started.Client.RecoverStalledAsync(started.Dispatch, () => true, cancellation.Token);
            throw new InvalidOperationException("Manual cancellation was ignored.");
        }
        catch (OperationCanceledException) { }
        Require(AbortCount(started) == 0, "Manual cancellation raced into a native abort.");

        started.State.TransientReadFailure = "server503";
        started.State.TransientReadFailuresRemaining = 1;
        await started.Client.ReadCompletionAsync(started.Dispatch);
        Require(!started.Client.IsStalled, "A failed snapshot authorized recovery from stale evidence.");
        await started.Client.ReadCompletionAsync(started.Dispatch);
        Require(!started.Client.IsStalled, "Reconnection inherited stale elapsed inactivity.");
        await PollForAsync(started, clock, 190);
        started.State.StatusJson = JsonSerializer.Serialize(new Dictionary<string, object> { [SessionId] = new { type = "retry" } });
        await started.Client.RecoverStalledAsync(started.Dispatch, () => true);
        Require(AbortCount(started) == 0, "A native provider retry was interrupted.");
        Console.WriteLine("PASS progress, late permission, cancellation, ownership, retry, and disconnected reads block stale aborts");
    }

    private static async Task VerifyToolsAndDescendantsAsync()
    {
        var clock = new TestClock();
        using StartedClient started = await StartClientAsync(null, clock);
        SetBusy(started);
        started.State.MessagesJson = JsonSerializer.Serialize(new[]
        {
            User(started.Dispatch.MessageId, SessionId, started.Dispatch.Prompt, 4000),
            ToolAssistant(started.Dispatch, "running")
        });
        await PollForAsync(started, clock, 200);
        Require(!started.Client.IsStalled, "A silent active build did not receive the longer tool inactivity window.");
        await PollForAsync(started, clock, 420);
        Require(started.Client.IsStalled, "A hung tool could block orchestration forever.");
        // An error is not completion while the provider is still working.
        started.State.MessagesJson = JsonSerializer.Serialize(new[]
        {
            User(started.Dispatch.MessageId, SessionId, started.Dispatch.Prompt, 4000),
            ToolAssistant(started.Dispatch, "error")
        });
        Require(await started.Client.ReadCompletionAsync(started.Dispatch) is null,
            "A busy turn was released when an intermediate tool failed.");

        using StartedClient child = await StartClientAsync(null, clock);
        const string childId = "ses_child";
        SetBusy(child);
        child.State.ChildrenJson = JsonSerializer.Serialize(new[] { new { id = childId, parentID = SessionId, directory = ProjectDirectory } });
        child.State.StatusJson = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            [SessionId] = new { type = "busy" }, [childId] = new { type = "busy" }
        });
        child.State.ChildMessagesJson = JsonSerializer.Serialize(new[] { Assistant("msg_child", childId, "msg_child_user", "working", 5000, null, null, 1) });
        await PollForAsync(child, clock, 590);
        child.State.ChildMessagesJson = JsonSerializer.Serialize(new[] { Assistant("msg_child", childId, "msg_child_user", "new tool result", 5000, null, null, 2) });
        await PollForAsync(child, clock, 30);
        Require(!child.Client.IsStalled, "A child making progress was aborted because its root was silent.");
        await PollForAsync(child, clock, 610);
        child.State.QuestionsJson = JsonSerializer.Serialize(new[] { new { id = "q", sessionID = childId } });
        await child.Client.RecoverStalledAsync(child.Dispatch, () => true);
        Require(AbortCount(child) == 0, "A descendant question did not block recovery.");
        child.State.QuestionsJson = "[]";
        await PollForAsync(child, clock, 620);
        await child.Client.RecoverStalledAsync(child.Dispatch, () => true);
        Require(child.Handler.Requests.Last().Uri.AbsolutePath == "/session/" + childId + "/abort", "Recovery did not stop descendants before the root.");
        await child.Client.RecoverStalledAsync(child.Dispatch, () => true);
        Require(AbortCount(child) == 2 && PromptCount(child) == 1, "The entire exact tree was not stopped without resubmission.");
        Console.WriteLine("PASS tool and descendant progress protect active work; frozen trees stop children before root");
    }

    private static async Task VerifyAmbiguousAbortAsync()
    {
        var clock = new TestClock();
        using StartedClient started = await StartClientAsync(null, clock);
        SetBusy(started);
        started.State.ThrowOnAbort = true;
        await PollForAsync(started, clock, 190);
        await started.Client.RecoverStalledAsync(started.Dispatch, () => true);
        for (int index = 0; index < 5; index++)
        {
            clock.Advance(2);
            Require(await started.Client.ReadCompletionAsync(started.Dispatch) is null, "An uncertain abort allowed a new stage.");
            await started.Client.RecoverStalledAsync(started.Dispatch, () => true);
        }
        Require(AbortCount(started) == 1 && PromptCount(started) == 1, "An uncertain POST was blindly replayed.");
        started.State.StatusJson = "{}";
        await started.Client.ReadCompletionAsync(started.Dispatch);
        clock.Advance(2);
        Require(await started.Client.ReadCompletionAsync(started.Dispatch) is { Interrupted: true }, "Lost abort response was not reconciled through native idle.");
        Console.WriteLine("PASS lost abort response keeps ownership until observed quiescence");
    }

    private static object ToolAssistant(OpenCodeAutomationDispatch dispatch, string status) => new
    {
        info = new { id = "msg_tool", sessionID = SessionId, role = "assistant", parentID = dispatch.MessageId, time = new { created = 5000 }, tokens = new { total = 1 } },
        parts = new[] { new { type = "tool", callID = "call_build", tool = "bash", state = new { status, input = new { command = "fake build" } } } }
    };

    private static async Task VerifyEscalationGuardsAsync()
    {
        var clock = new TestClock();
        using StartedClient started = await StartClientAsync(null, clock);
        SetBusy(started);
        started.State.ThrowOnAbort = true;
        await PollForAsync(started, clock, 190);
        await started.Client.RecoverStalledAsync(started.Dispatch, () => true);
        await PollForAsync(started, clock, 70);
        Require(started.Client.CanRestartStalledProcess && started.Client.ObservedTokens == 1,
            "An ineffective abort did not allow recovery of the exact stalled process with retained usage.");
        started.State.QuestionsJson = JsonSerializer.Serialize(new[] { new { id = "q", sessionID = SessionId } });
        await started.Client.ReadCompletionAsync(started.Dispatch);
        Require(!started.Client.CanRestartStalledProcess, "Process recovery ignored a late native question.");
        started.State.QuestionsJson = "[]";
        await PollForAsync(started, clock, 70);
        started.State.MessagesJson = JsonSerializer.Serialize(new[] { User(started.Dispatch.MessageId, SessionId, "replaced prompt", 4000) });
        await started.Client.ReadCompletionAsync(started.Dispatch);
        await PollForAsync(started, clock, 70);
        Require(!started.Client.CanRestartStalledProcess, "Process recovery ignored a changed prompt identity.");
        started.State.StatusJson = "{}";
        Require(await started.Client.ReadCompletionAsync(started.Dispatch) is null, "Recovery handed off after the owned prompt was replaced.");
        clock.Advance(2);
        Require(await started.Client.ReadCompletionAsync(started.Dispatch) is null, "Stable idle overrode a changed prompt identity.");
        Console.WriteLine("PASS process escalation respects fresh input/identity evidence and preserves observed tokens");
    }

    private static void SetBusy(StartedClient started, string text = "thinking")
    {
        started.State.StatusJson = JsonSerializer.Serialize(new Dictionary<string, object> { [SessionId] = new { type = "busy" } });
        started.State.MessagesJson = JsonSerializer.Serialize(new[]
        {
            User(started.Dispatch.MessageId, SessionId, started.Dispatch.Prompt, 4000),
            Assistant("msg_partial", SessionId, started.Dispatch.MessageId, text, 5000, null, null, 1)
        });
    }

    private static async Task VerifyIdleWithOrphanedToolAsync()
    {
        foreach (bool initiallyIdle in new[] { false, true })
        {
        var clock = new TestClock();
        using StartedClient started = await StartClientAsync(null, clock);
        SetBusy(started);
        if (initiallyIdle) started.State.StatusJson = "{}";
        started.State.MessagesJson = JsonSerializer.Serialize(new[]
        {
            User(started.Dispatch.MessageId, SessionId, started.Dispatch.Prompt, 4000),
            ToolAssistant(started.Dispatch, "running")
        });
        await PollForAsync(started, clock, 620);
        await started.Client.RecoverStalledAsync(started.Dispatch, () => true);
        started.State.StatusJson = "{}"; // Real 1.18.32 failure: abort becomes idle while bash remains running.
        await PollForAsync(started, clock, 70);
        Require(started.Client.CanRestartStalledProcess, "Idle with an orphaned running tool prevented exact-process recovery forever.");
        Require(await started.Client.ReadCompletionAsync(started.Dispatch) is null, "An orphaned tool allowed handoff before process exit.");
        started.State.QuestionsJson = JsonSerializer.Serialize(new[] { new { id = "q", sessionID = SessionId } });
        await started.Client.ReadCompletionAsync(started.Dispatch);
        Require(!started.Client.CanRestartStalledProcess, "Orphaned tool recovery ignored an actual question.");
        Require(AbortCount(started) == 1, "The stalled tool caused repeated abort POSTs.");
        }
        Console.WriteLine("PASS idle after abort with a lingering running tool permits guarded process replacement, not early handoff");
    }

    private static async Task VerifyActivityEvidenceAsync()
    {
        var clock = new TestClock();
        using StartedClient started = await StartClientAsync(AutomationRole.Implementer, clock);
        SetBusy(started);
        started.State.MessagesJson = JsonSerializer.Serialize(new[] { User(started.Dispatch.MessageId, SessionId, started.Dispatch.Prompt, 4000) });
        await started.Client.ReadCompletionAsync(started.Dispatch);
        Require(started.Client.LastActivityAt is null, "A submitted prompt or busy flag became actual response activity.");
        SetBusy(started, "response changed");
        await started.Client.ReadCompletionAsync(started.Dispatch);
        DateTimeOffset? observed = started.Client.LastActivityAt;
        Require(observed == clock.GetUtcNow(), "Assistant evidence did not produce an activity signal.");
        clock.Advance(45); // Watchdog grace resets after an observation gap.
        await started.Client.ReadCompletionAsync(started.Dispatch);
        Require(started.Client.LastActivityAt == observed, "An unchanged response or observation gap faked UI activity.");
        started.State.PermissionsJson = JsonSerializer.Serialize(new[] { new { id = "permission_wait", sessionID = SessionId } });
        clock.Advance(10);
        await started.Client.ReadCompletionAsync(started.Dispatch);
        Require(started.Client.LastActivityAt == observed, "Approval wait faked response activity.");
        started.State.PermissionsJson = "[]";
        SetBusy(started, "another response change");
        await started.Client.ReadCompletionAsync(started.Dispatch);
        Require(started.Client.LastActivityAt == clock.GetUtcNow(), "New response evidence did not restore the activity signal.");
        Console.WriteLine("PASS activity reflects response evidence, never repeated busy/approval/watchdog grace");
    }

    private static async Task VerifyCoordinatorInterventionAsync()
    {
        var clock = new TestClock();
        using StartedClient started = await StartClientAsync(AutomationRole.Implementer, clock);
        SetBusy(started, "healthy but wrong approach");
        await started.Client.ReadCompletionAsync(started.Dispatch);
        Require(!started.Client.IsStalled, "Intervention fixture was already stalled.");
        started.State.PermissionsJson = JsonSerializer.Serialize(new[] { new { id = "pending", sessionID = SessionId } });
        await started.Client.RequestCoordinatorInterventionAsync(started.Dispatch, "Review the current diff before more writes.", () => true);
        Require(AbortCount(started) == 0, "Coordinator intervention overrode pending permission.");
        started.State.PermissionsJson = "[]";
        await started.Client.RequestCoordinatorInterventionAsync(started.Dispatch, "Review the current diff before more writes.", () => false);
        Require(AbortCount(started) == 0, "A stale coordinator overrode lost ownership.");
        await started.Client.RequestCoordinatorInterventionAsync(started.Dispatch, "Review the current diff before more writes.", () => true);
        Require(AbortCount(started) == 1 && started.Client.IsRecovering, "An explicit coordinator correction did not abort the exact healthy worker once.");
        await started.Client.RequestCoordinatorInterventionAsync(started.Dispatch, "Review the current diff before more writes.", () => true);
        Require(AbortCount(started) == 1, "Coordinator polling repeated a side-effect request.");
        started.State.StatusJson = "{}";
        Require(await started.Client.ReadCompletionAsync(started.Dispatch) is null, "An abort reply alone released the worker.");
        clock.Advance(2);
        var recovered = await started.Client.ReadCompletionAsync(started.Dispatch);
        Require(recovered is { Interrupted: true } && recovered.Summary.Contains("Review the current diff"), "Confirmed intervention lost the commander's correction before review.");
        Console.WriteLine("PASS explicit coordinator intervention respects permission/ownership and confirms exact abort before preserving correction");
    }

    private static async Task PollForAsync(StartedClient started, TestClock clock, int seconds)
    {
        await started.Client.ReadCompletionAsync(started.Dispatch);
        for (int elapsed = 0; elapsed < seconds; elapsed += 10)
        {
            clock.Advance(10);
            await started.Client.ReadCompletionAsync(started.Dispatch);
        }
    }

    private static int AbortCount(StartedClient started) => started.Handler.Requests.Count(request => request.Uri.AbsolutePath.EndsWith("/abort"));
    private static int PromptCount(StartedClient started) => started.Handler.Requests.Count(request => request.Uri.AbsolutePath.EndsWith("/prompt_async"));
    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(int seconds) => _now += TimeSpan.FromSeconds(seconds);
    }

    private static async Task<StartedClient> StartClientAsync(AutomationRole? role = null, TimeProvider? clock = null)
    {
        var state = new FakeState();
        var handler = new FakeHandler(state);
        var http = new HttpClient(handler);
        var client = new OpenCodeAutomationClient(ServerUri, ProjectDirectory, http, clock);
        OpenCodeAutomationDispatch dispatch = await client.StartAsync((role?.ToString() ?? "Coordinator") + " · stage 2", PromptFor(role), null);
        return new StartedClient(state, handler, http, client, dispatch);
    }

    private static string PromptFor(AutomationRole? role) => role is null
        ? RolePrompt
        : $"Terminal Notes 자동화 · {role} · 회차 1\nexact role prompt body";

    private static string FinalMessages(OpenCodeAutomationDispatch dispatch, string text) => JsonSerializer.Serialize(new[]
    {
        User(dispatch.MessageId, SessionId, dispatch.Prompt, 4000),
        Assistant("msg_current_assistant", SessionId, dispatch.MessageId, text, 5000, 6000, "stop", 12)
    });

    private static object User(string id, string sessionId, string text, long created) => new
    {
        info = new { id, sessionID = sessionId, role = "user", time = new { created } },
        parts = new[] { new { type = "text", text } }
    };

    private static object Assistant(
        string id,
        string sessionId,
        string parentID,
        string text,
        long created,
        long? completed,
        string? finish,
        long tokens,
        object? error = null) => new
    {
        info = new
        {
            id,
            sessionID = sessionId,
            role = "assistant",
            parentID,
            time = new { created, completed },
            finish,
            tokens = new { total = tokens },
            error
        },
        parts = new[] { new { type = "text", text } }
    };

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class FakeState
    {
        public string Title { get; set; } = "";
        public string MessageId { get; set; } = "";
        public string Prompt { get; set; } = "";
        public string StatusJson { get; set; } = "{}";
        public string ChildrenJson { get; set; } = "[]";
        public string PermissionsJson { get; set; } = "[]";
        public string QuestionsJson { get; set; } = "[]";
        public string MessagesJson { get; set; } = "[]";
        public string ChildMessagesJson { get; set; } = "[]";
        public bool ThrowOnAbort { get; set; }
        public bool ThrowOnPrompt { get; init; }
        public string? TransientReadFailure { get; set; }
        public int TransientReadFailuresRemaining { get; set; }
    }

    private sealed record RecordedRequest(HttpMethod Method, Uri Uri, string Body, string? Authorization);

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly FakeState _state;
        private readonly ConcurrentQueue<RecordedRequest> _requests = new();

        public FakeHandler(FakeState state) => _state = state;
        public IReadOnlyList<RecordedRequest> Requests => _requests.ToArray();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uri uri = request.RequestUri ?? throw new InvalidOperationException("Fake request had no URI.");
            string body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            var recorded = new RecordedRequest(request.Method, uri, body, request.Headers.Authorization?.ToString());
            _requests.Enqueue(recorded);
            string path = uri.AbsolutePath;

            if (request.Method == HttpMethod.Get && path == "/session/status" && _state.TransientReadFailuresRemaining > 0)
            {
                _state.TransientReadFailuresRemaining--;
                return _state.TransientReadFailure switch
                {
                    "disconnect" => throw new HttpRequestException("simulated transient disconnect"),
                    "timeout" => throw new TaskCanceledException("simulated transient read timeout"),
                    "server503" => JsonResponse(request, "temporarily unavailable", HttpStatusCode.ServiceUnavailable),
                    _ => throw new InvalidOperationException("Unknown fake transient failure.")
                };
            }

            if (request.Method == HttpMethod.Get && path == "/session/status")
                return JsonResponse(request, _state.StatusJson);
            if (request.Method == HttpMethod.Post && path == "/session")
            {
                using JsonDocument payload = JsonDocument.Parse(body);
                _state.Title = payload.RootElement.GetProperty("title").GetString() ?? "";
                return JsonResponse(request, SessionJson(_state.Title));
            }
            if (request.Method == HttpMethod.Get && path == "/session/" + SessionId)
                return JsonResponse(request, SessionJson(_state.Title));
            if (request.Method == HttpMethod.Post && path == "/tui/select-session")
                return JsonResponse(request, "true");
            if (request.Method == HttpMethod.Post && path == "/session/" + SessionId + "/prompt_async")
            {
                if (_state.ThrowOnPrompt) throw new HttpRequestException("simulated lost prompt response");
                using JsonDocument payload = JsonDocument.Parse(body);
                _state.MessageId = payload.RootElement.GetProperty("messageID").GetString() ?? "";
                _state.Prompt = payload.RootElement.GetProperty("parts")[0].GetProperty("text").GetString() ?? "";
                return new HttpResponseMessage(HttpStatusCode.NoContent) { RequestMessage = request };
            }
            if (request.Method == HttpMethod.Get && path == "/session/" + SessionId + "/children")
                return JsonResponse(request, _state.ChildrenJson);
            if (request.Method == HttpMethod.Get && path == "/session/" + SessionId + "/message")
                return JsonResponse(request, _state.MessagesJson);
            if (request.Method == HttpMethod.Get && path == "/session/ses_child/children")
                return JsonResponse(request, "[]");
            if (request.Method == HttpMethod.Get && path == "/session/ses_child/message")
                return JsonResponse(request, _state.ChildMessagesJson);
            if (request.Method == HttpMethod.Post && path.EndsWith("/abort", StringComparison.Ordinal))
            {
                if (_state.ThrowOnAbort) throw new HttpRequestException("lost abort response");
                using JsonDocument statuses = JsonDocument.Parse(_state.StatusJson);
                string stopped = path.Split('/')[2];
                _state.StatusJson = JsonSerializer.Serialize(statuses.RootElement.EnumerateObject()
                    .Where(property => property.Name != stopped).ToDictionary(property => property.Name, property => property.Value.Clone()));
                return JsonResponse(request, "true");
            }
            if (request.Method == HttpMethod.Get && path == "/permission")
                return JsonResponse(request, _state.PermissionsJson);
            if (request.Method == HttpMethod.Get && path == "/question")
                return JsonResponse(request, _state.QuestionsJson);
            return new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request };
        }

        private static string SessionJson(string title) => JsonSerializer.Serialize(new
        {
            id = SessionId,
            directory = ProjectDirectory,
            title,
            time = new { created = 1000L, updated = 1000L }
        });

        private static HttpResponseMessage JsonResponse(HttpRequestMessage request, string json, HttpStatusCode statusCode = HttpStatusCode.OK) => new(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
            RequestMessage = request
        };
    }

    private sealed class StartedClient : IDisposable
    {
        public StartedClient(FakeState state, FakeHandler handler, HttpClient http, OpenCodeAutomationClient client, OpenCodeAutomationDispatch dispatch)
        {
            State = state;
            Handler = handler;
            Http = http;
            Client = client;
            Dispatch = dispatch;
        }

        public FakeState State { get; }
        public FakeHandler Handler { get; }
        public HttpClient Http { get; }
        public OpenCodeAutomationClient Client { get; }
        public OpenCodeAutomationDispatch Dispatch { get; }

        public void Dispose()
        {
            Client.Dispose();
            Http.Dispose();
            Handler.Dispose();
        }
    }
}
