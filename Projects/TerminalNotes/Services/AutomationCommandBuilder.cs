using System.IO;
using System.Text;
using System.Text.Json;

namespace TerminalNotes.Services;

public sealed record AutomationCommand(string Directory, string Command, string ResultPath, string CompletionPath, string EventsPath, AutomationRole Role)
{
    // Additive metadata lets completion parsing enforce the Planning contract while
    // preserving the existing positional constructor used by older callers.
    public AutomationWorkflowPhase WorkflowPhase { get; init; }
}
public sealed record AutomationStageOutput(bool Success, string Summary, string Instruction, bool Complete, long Tokens)
{
    // Set only by the native supervisor after it confirms that work stopped.
    public bool Interrupted { get; init; }
    public bool PlanApproved { get; init; }
    public string? DeliveryEvidence { get; init; }
    public AutomationPlanningOutput? PlanningPlan { get; init; }
}

/// <summary>Transfers prompts through files, never through shell interpolation. Each dispatch has its own completion marker.</summary>
public static class AutomationCommandBuilder
{
    private static readonly JsonSerializerOptions WireJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
    private const int MaxCliFailureDetailLength = 900;
    private const int MaxCliFailureLineLength = 320;

    public static string QuotePowerShell(string value) => "'" + value.Replace("'", "''") + "'";

    public static Task<AutomationCommand> PrepareAsync(string project, AutomationStepClaim claim, string previousResult) =>
        PrepareCoreAsync(project, claim, previousResult, nativeSession: false);

    public static Task<AutomationCommand> PrepareNativeAsync(string project, AutomationStepClaim claim, string previousResult) =>
        PrepareCoreAsync(project, claim, previousResult, nativeSession: true);

    private static async Task<AutomationCommand> PrepareCoreAsync(string project, AutomationStepClaim claim, string previousResult, bool nativeSession)
    {
        string directory = Path.Combine(project, "terminal-notes", "runs", claim.RunId.ToString("N"), claim.DispatchToken.ToString("N"));
        Directory.CreateDirectory(directory);
        string promptPath = Path.Combine(directory, "prompt.md"), schemaPath = Path.Combine(directory, "schema.json");
        string resultPath = Path.Combine(directory, "result.json"), completionPath = Path.Combine(directory, "completion.json"), eventsPath = Path.Combine(directory, "events.jsonl");
        string scriptPath = Path.Combine(directory, "run.ps1");
        string? model = claim.Options.ModelFor(claim.Role);
        if (!string.IsNullOrWhiteSpace(model) && (model.IndexOf('/') <= 0 || model.EndsWith('/')))
            throw new InvalidOperationException("이 실행의 모델 설정은 OpenCode 형식이 아닙니다. 새 플랜에서 모델을 비워 OpenCode 기본값을 사용하거나 provider/model로 지정한 뒤 다시 승인하세요.");
        string role = RoleInstructions(claim);
        const string resultContract = """{"summary":"작업 요약과 검증 결과","instruction":"다음 역할에 줄 지시","complete":false,"planApproved":false,"deliveryEvidence":null,"planning":null}""";
        string prompt = $"""
            Terminal Notes 자동화 · {claim.Role} · 회차 {claim.Iteration}
            Workflow phase: {claim.WorkflowPhase}

            {ContinuousImprovementInstructions(claim)}

            당신은 {role}
            별도의 에이전트를 만들지 마세요. Terminal Notes가 3개 역할을 순서대로 실행합니다.
            프로젝트 조사에서 terminal-notes의 실행 로그·과거 프롬프트·내부 관리 파일은 제외하고 지정된 plan.md와 terminal-notes/terminal-inputs.md(있을 때)만 읽으세요. 추가 입력의 최신 요청과 정정 내용을 반영하고 과거 요청을 순서대로 다시 실행하지 마세요. 자동화 맥락은 아래에 제공됩니다. 구현 파일이 없는 새 프로젝트라면 같은 폴더를 반복 조사하지 말고 구체적인 초기 계획을 작성하세요.
            plan.md는 사용자가 작성한 원문이며 모든 역할이 보존해야 합니다. 상세 지휘 계획은 프로그램이 별도로 저장하고 아래 '구조화된 지휘 계획'으로 전달합니다. 계획이 원본 plan.md에 없다는 이유로 반려하거나 그 파일에 상세 계획을 쓰라고 지시하지 마세요. 이전 지시가 이를 요구했다면 이 저장 계약으로 바로잡고 전달된 구조화된 계획 자체를 검토하세요.
            사용자의 판단이나 권한이 반드시 필요하면 OpenCode의 실제 질문·승인 기능으로 기다리고, 진행할 수 없는 이유를 명시하세요. 승인된 범위를 임의로 넓히지 마세요.
            지휘자가 계획 검토, 구현·빌드, 결과 검증, 수정, 서비스 실행을 끝까지 통제합니다. 검토 결과는 반드시 지휘자에게 돌아갑니다. 동일한 지적이 반복되면 원인과 접근 방법을 다시 검토하고 구체적인 수정 지시를 개선하세요. 사용자의 중단이나 실제 권한·정보 부족이 없으면 프로젝트가 완성될 때까지 진행합니다.
            재개 시 처음부터 다시 시작하지 마세요. 현재 코드·변경분·native todo·전달된 실행 결과를 먼저 대조해 완료·부분 구현·미완료를 구분하세요. 이미 구현되어 정상 동작하는 기능은 보존하고 다시 구현하지 마세요. 중간에 끊긴 변경은 현재 파일을 확인해 필요한 보완과 검증만 하세요. 완료 여부가 불명확하면 검토로 확인하고, 기존 구현의 구체적인 결함이나 변경 요청 없이 재구현하지 마세요.
            아래 지휘 계획·검토 결과와 승인된 플랜을 workflow 기준으로 사용하세요. 자동화의 상태 파일, 실행 스크립트, 완료 마커는 수정하지 마세요.
            OpenCode의 실제 native todo 기능을 사용해 역할 작업을 시작할 때 해야 할 일과 검증 항목을 만들거나 갱신하세요. 끝나지 않은 항목을 완료로 표시하지 마세요. 실제로 마친 항목만 완료로 바꾸고, 남은 문제와 막힌 이유를 최종 JSON에 정직하게 보고하세요.
            서비스처럼 계속 실행될 프로세스는 도구 호출과 분리해 시작하고 즉시 PID를 반환한 뒤 별도 요청으로 상태·HTTP 응답을 확인하세요. 서버를 포그라운드로 실행하거나 종료까지 기다리지 마세요. Windows에서 보조 프로세스는 숨기세요. OpenCode 도구 안의 PowerShell Start-Process에 -RedirectStandardOutput/-RedirectStandardError를 직접 연결하면 서버가 살아 있는 동안 도구 반환이 묶일 수 있으므로 피하세요. Node가 있다면 별도 실행 스크립트에서 child_process.spawn에 detached:true, stdio:'ignore', windowsHide:true를 지정하고 child.unref()를 호출하는 식으로 stdin/stdout/stderr를 분리하세요. 로그가 필요하면 파이프 대신 전용 파일 핸들을 사용하세요. 실행 중인 다른 프로젝트의 서버를 중단하거나 같은 서비스를 중복 실행하지 마세요.
            웹의 핵심 입력은 계산 엔진 단위 테스트나 HTML 소스 확인만으로 통과 처리하지 마세요. 실제 브라우저의 키보드·클릭 이벤트로 사용자 입력, 화면 갱신, 오류·복구를 확인하고 관찰한 결과를 보고하세요. 독립적인 브라우저 검증을 수행할 수 없다면 그 사실을 남기고 확인한 척하지 마세요.
            승인된 플랜에 Persisted plan Markdown 경로가 있으면 각 역할을 시작할 때 그 파일을 직접 다시 읽고 최신 저장 내용을 따르세요. 파일이 없거나 삭제·비어 있음·읽기 실패이면 이전 스냅샷으로 진행하지 말고 중단 사유를 보고하세요. 승인된 플랜 스냅샷은 실행 시작 시점의 기록입니다.
            최종 응답은 설명이나 코드 블록 없이 다음 JSON 객체 하나만 출력하세요. 값은 한국어로 작성하세요.
            {resultContract}
            complete는 검토자만 true로 설정할 수 있습니다. PlanReview에서는 complete=false로 두고 계획 승인 여부만 planApproved로 반환하세요. Verification의 complete=true는 구현 검증 통과이며 아직 프로젝트 완료가 아닙니다. 실제 서비스 실행과 사용자에게 표시한 결과를 FinalVerification에서 다시 검증해야 전체 완료됩니다.
            Planning 단계의 Coordinator는 반드시 planning 객체를 채워 반환하세요. planning에는 goalsAndScope(목표와 범위 문자열), ideasAndChoices(구체적인 아이디어·개선안·선택지 배열), designFilesTasks(설계·변경 파일·작업 배열), orderedImplementation(순서가 있는 구현 단계 배열), risksAndEdgeCases(위험·경계 사례 배열), acceptanceTests(실행할 인수 테스트 배열), serviceDemo(실제로 실행해 사용자에게 보여줄 명령·주소·창·정상 동작 확인 방법 문자열)를 모두 포함해야 합니다. 각 문자열과 배열 항목은 비어 있으면 안 됩니다. 이 구조는 내용의 충분함을 검사하기 위한 것이며 항목 수나 글자 수를 임의로 제한하지 않습니다. 다른 단계에서는 planning을 null로 둘 수 있습니다.
            deliveryEvidence는 실제 실행 명령·작업 폴더·프로세스/PID·접속 주소 또는 앱 창·관찰한 정상 응답과 사용자에게 표시한 결과를 담은 문자열입니다. 실행하지 않았거나 확인하지 못했다면 null로 두세요. 임의 주소, 예정된 명령, 빌드 성공만으로 실행 근거를 대신하지 마세요.

            ## 승인된 플랜 스냅샷 (실행 시작 시점 기록)
            {claim.ApprovedPlanSnapshot}

            ## 지휘 계획 및 인수 기준
            {claim.CoordinatorPlan ?? "아직 작성되지 않았습니다."}

            ## 구조화된 지휘 계획
            {(claim.CoordinatorPlanningPlan is null ? "아직 구조화된 계획이 작성되지 않았습니다." : JsonSerializer.Serialize(claim.CoordinatorPlanningPlan, WireJsonOptions))}

            ## 최근 검토 결과
            {claim.LastReviewSummary ?? "아직 검토되지 않았습니다."}

            ## 실제 서비스 실행 근거
            {claim.DeliveryEvidence ?? "아직 실행을 확인하지 않았습니다."}

            ## 이전 단계 결과
            {previousResult}
            """;
        const string schema = """
            {"type":"object","properties":{"summary":{"type":"string"},"instruction":{"type":"string"},"complete":{"type":"boolean"},"planApproved":{"type":"boolean"},"deliveryEvidence":{"type":["string","null"]},"planning":{"type":["object","null"],"properties":{"goalsAndScope":{"type":"string"},"ideasAndChoices":{"type":"array","items":{"type":"string"}},"designFilesTasks":{"type":"array","items":{"type":"string"}},"orderedImplementation":{"type":"array","items":{"type":"string"}},"risksAndEdgeCases":{"type":"array","items":{"type":"string"}},"acceptanceTests":{"type":"array","items":{"type":"string"}},"serviceDemo":{"type":"string"}},"required":["goalsAndScope","ideasAndChoices","designFilesTasks","orderedImplementation","risksAndEdgeCases","acceptanceTests","serviceDemo"],"additionalProperties":false}},"required":["summary","instruction","complete","planApproved","deliveryEvidence","planning"],"additionalProperties":false}
            """;
        if (nativeSession)
        {
            await File.WriteAllTextAsync(promptPath, prompt, new UTF8Encoding(false));
            await File.WriteAllTextAsync(schemaPath, schema, new UTF8Encoding(false));
            return new(directory, prompt, resultPath, completionPath, eventsPath, claim.Role)
            {
                WorkflowPhase = claim.WorkflowPhase
            };
        }
        string qProject = QuotePowerShell(project), qPrompt = QuotePowerShell(promptPath);
        string script = $$"""
            $ErrorActionPreference = 'Stop'
            $OutputEncoding = [Text.UTF8Encoding]::new($false)
            [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
            $stageExit = -1
            $stageError = ''
            try {
                Set-Location -LiteralPath {{qProject}}
                $agentCommand = Get-Command opencode -CommandType Application,ExternalScript -ErrorAction Stop | Select-Object -First 1
                $stagePrompt = [IO.File]::ReadAllText({{qPrompt}}, [Text.Encoding]::UTF8)
                $agentArguments = @('run', '--format', 'json', '--auto')
                {{(string.IsNullOrWhiteSpace(model) ? "" : "$agentArguments += @('--model', " + QuotePowerShell(model) + ")")}}
                $ErrorActionPreference = 'Continue'
                $stagePrompt | & $agentCommand.Source @agentArguments 2>&1 | ForEach-Object {
                    $stageLine = $_.ToString()
                    [IO.File]::AppendAllText({{QuotePowerShell(eventsPath)}}, $stageLine + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
                    Write-Output $stageLine
                }
                $stageExit = $LASTEXITCODE
                if ($null -eq $stageExit) { $stageExit = -1 }
            }
            catch { $stageError = $_.Exception.Message; Write-Output $stageError }
            finally {
                $stageCompletion = @{ exitCode = $stageExit; error = $stageError } | ConvertTo-Json -Compress
                [IO.File]::WriteAllText({{QuotePowerShell(completionPath + ".tmp")}}, $stageCompletion, [Text.UTF8Encoding]::new($false))
                [IO.File]::Move({{QuotePowerShell(completionPath + ".tmp")}}, {{QuotePowerShell(completionPath)}})
            }
            """;
        await File.WriteAllTextAsync(promptPath, prompt, new UTF8Encoding(false));
        await File.WriteAllTextAsync(schemaPath, schema, new UTF8Encoding(false));
        await File.WriteAllTextAsync(scriptPath, script, new UTF8Encoding(true)); // Windows PowerShell 5.1 needs BOM for Korean paths.
        return new(directory, "powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File " + QuotePowerShell(scriptPath), resultPath, completionPath, eventsPath, claim.Role)
        {
            WorkflowPhase = claim.WorkflowPhase
        };
    }

    private static string ContinuousImprovementInstructions(AutomationStepClaim claim)
    {
        if (!claim.Options.ContinuousImprovement) return "";
        string history = string.Join("\n", claim.ImprovementHistory.TakeLast(12).Select(item =>
            $"주기 {item.Cycle}: {item.Goal}\n검증 결과: {item.Summary}\n실행 근거: {item.Evidence}"));
        return $"""
            지속 개선 모드 · 개선 주기 {claim.ImprovementCycle}
            사용자가 중단할 때까지 프로젝트를 발전시키는 작업입니다. '계속할까요'를 묻거나 '계속' 입력을 기다리지 마세요.
            첫 주기는 사용자의 MD 목표를 달성하세요. 이후에는 최신 MD의 목적과 제약을 지키면서 현재 코드·실제 오류·이전 검토를 근거로 다음 개선을 선정하세요.
            지휘자는 매 주기 버그 수정, 안정성·성능·사용성 개선, 관련 기능 추가 후보를 비교하고 실제 가치와 새 검증 기준이 있는 작은 목표 하나를 정하세요.
            단순 재검토 반복, 이름만 바꾼 동일 작업, 검증된 코드의 불필요한 재작성은 진행으로 인정하지 마세요. 조사에서 새 문제가 없으면 경계 사례와 회귀 테스트의 실제 공백을 찾아 보완하세요.
            검토자는 이전 완료 목표와 비교해 새 계획의 가치·중복·범위를 먼저 확인하고, 해당 주기의 목표와 기존 기능 회귀를 독립 검증하세요.
            이전 주기가 완료된 뒤의 구현·검증·최종 판정은 이번 지휘 계획의 인수 기준 및 원래 MD의 유지 조건을 함께 적용하세요.
            complete=true는 현재 주기의 검증 완료입니다. 다음 주기 여부는 프로그램이 최신 설정·종료 시간을 확인해 결정합니다. 종료 시간이 지나거나 사용자가 지속 개선을 해제해도 이번 주기는 최종 검토까지 마무리하세요.
            설정된 종료 시간: {claim.Options.ImprovementUntil?.ToString("yyyy-MM-dd HH:mm zzz") ?? "제한 없음"}. 관련 없는 기능이나 외부 배포·결제·파괴적 변경으로 범위를 넓히지 마세요.
            최근 검증을 통과한 개선 기록:
            {history}
            """;
    }

    private static string RoleInstructions(AutomationStepClaim claim) => (claim.Role, claim.WorkflowPhase) switch
    {
        (AutomationRole.Coordinator, AutomationWorkflowPhase.Planning) =>
            "지휘자입니다. 사용자의 원본 plan.md를 읽기 전용으로만 읽고 절대 수정·덮어쓰기하지 마세요. 프로젝트 소스와 설정도 조사해 원본 요구사항을 목표와 범위로 분해하고, 빠진 요구사항·구체적인 아이디어·개선안·대안과 선택 이유를 확장하세요. planning 객체의 goalsAndScope에는 달성 목표·범위·비범위를, ideasAndChoices에는 구체적인 개선·추가·수정안과 선택지를, designFilesTasks에는 설계·변경할 파일/모듈·작업을, orderedImplementation에는 의존성을 반영한 순서를, risksAndEdgeCases에는 실패·경계 사례와 완화책을, acceptanceTests에는 실제 실행할 인수 테스트와 관찰 결과를, serviceDemo에는 실제 서비스를 실행해 사용자에게 보여줄 명령·작업 폴더·주소/창·핵심 동작 확인 방법을 기록하세요. 각 항목은 프로젝트에 근거한 구체적인 내용이어야 하며 빈 배열이나 요약 한 줄로 대체하지 마세요. summary에도 계획의 핵심과 인수 기준을 남기되 영어 표제 형식에 의존하지 마세요. 이전 계획 검토의 지적을 반영하세요. instruction은 `Reviewer verification:`으로 시작해 계획을 충분히 검토하도록 지시하세요. 계획이 승인되기 전에는 파일 수정·구현·빌드·테스트·서비스 실행을 하지 마세요. enriched planning 결과는 이 단계의 구조화된 결과로만 반환하며 원본 plan.md에는 쓰지 마세요. terminal-notes 자동화 이력은 열지 말고 아래에 전달된 맥락을 사용하세요.",
        (AutomationRole.Reviewer, AutomationWorkflowPhase.PlanReview) =>
            "계획 검토자입니다. 파일을 수정하거나 구현·빌드·테스트·서비스 실행을 하지 마세요. 최신 MD와 지휘 계획을 대조해 요구사항 누락, 설계의 타당성, 구현 가능성, 예외 처리, 인수 기준과 검증 방법, 실제 실행·표시 방법을 충분히 검토하세요. 계획이 원본 MD의 반복, 일반론, 빈 선택지, 실행 근거 없는 약속, 검증할 수 없는 인수 기준처럼 구체적이지 않으면 planApproved=false로 하고 어떤 결정을 어떻게 보완할지 근거와 함께 지휘자에게 돌려보내세요. 계획을 충분히 검토해 구현 가능한 상태일 때만 planApproved=true로 하고 검토 근거를 summary에 남기세요. complete는 항상 false입니다.",
        (AutomationRole.Coordinator, AutomationWorkflowPhase.Delivery) =>
            "지휘자입니다. 구현 검증 결과와 인수 기준을 확인하고 완성된 서비스를 실제 실행해 사용자에게 보여주도록 구체적으로 지시하세요. instruction은 `Implementer action:`으로 시작하세요. 해당 프로젝트의 실제 실행 방법, 정상 동작 확인, 브라우저 또는 앱 창 표시와 유지 방법을 지시하세요. 직접 파일 수정이나 실행은 하지 마세요. 실행 근거가 확보되기 전에는 완료라고 보고하지 마세요.",
        (AutomationRole.Coordinator, _) =>
            "지휘자입니다. 프로젝트가 완성될 때까지 최근 구현·검토 결과와 최신 MD를 비교하고 다음 작업을 통제하세요. 직접 파일 수정·빌드·테스트는 하지 마세요. 한 번에 하나의 검증 가능한 작은 변경 단위를 지시하고 변경 파일, 중단점, 즉시 검토할 기준을 구체적으로 정하세요. 큰 구현을 한 번에 맡기지 말고 작은 구현과 독립 검토 사이에서 계속 다음 지시를 내리세요. 통과한 기준을 보존하고 누락·오류의 원인과 개선안을 검토해 구체적인 수정 범위 및 성공 조건을 `Implementer action:`으로 지시하세요. 현재 구현의 독립적인 확인만 필요하면 `Reviewer verification:`으로 지시하세요. 같은 검토 지적이 반복되면 이전 접근의 실패 이유와 바꿀 방법을 명확히 하세요. 승인받은 계획을 보존하고 아직 확인하지 않은 결과를 성공이라고 표현하지 마세요.",
        (AutomationRole.Implementer, AutomationWorkflowPhase.Delivery) =>
            "서비스 실행 담당 구현자입니다. 검증된 프로젝트를 실제 실행하세요. 이미 실행 중이면 해당 프로젝트의 프로세스·경로·응답을 확인해 재사용하고 중복 서버를 만들지 마세요. 웹 서비스는 로컬 정상 응답과 핵심 화면을 확인하고 실제 주소를 브라우저로 여세요. 데스크톱 앱은 실행해 실제 창을 표시하세요. CLI라면 사용자가 볼 수 있는 터미널에서 실제 동작을 시연하세요. 사용자에게 보여줄 창만 표시하고 보조 프로세스는 숨기세요. 실행한 서비스를 사용자가 확인할 수 있도록 유지하세요. 실행 명령, 작업 폴더, PID, 실제 주소/창, 정상 응답과 표시 근거를 deliveryEvidence와 summary에 남기세요. 실행 실패·필수 설정 누락은 사실대로 보고하고 근거를 만들지 마세요. 외부 배포·유료 서비스 가입은 이 단계에 포함되지 않습니다. instruction은 `Reviewer verification:`으로 시작해 실제 서비스 재확인을 요청하세요.",
        (AutomationRole.Implementer, _) =>
            "구현자입니다. 검토를 통과한 지휘 계획과 최신 MD, 지휘자의 구체적인 작업 범위 안에서 구현·수정하세요. 현재 파일과 작업 결과를 먼저 확인하고 이미 끝난 일을 반복하지 마세요. 이번에 지휘자가 정한 작은 변경 단위까지만 수행하고 검증한 뒤 즉시 검토자에게 넘기세요. 남은 큰 작업을 스스로 계속 확장하지 마세요. 지휘자의 중간 개입으로 멈췄다면 새 지시와 현재 변경분을 다시 대조하세요. 변경했다면 해당 프로젝트의 빌드와 필요한 테스트를 실제로 실행하세요. 관련 변경이 없고 검증 결과가 여전히 유효하면 불필요한 재실행 대신 근거를 적으세요. 빌드 방법이 없거나 실행할 수 없으면 사유를 보고하고 성공했다고 표현하지 마세요. summary에는 변경 파일, 실행한 명령과 실제 결과, 남은 문제를 적으세요. instruction은 `Reviewer verification:`으로 시작하고 검토자가 확인할 기준을 적으세요.",
        (AutomationRole.Reviewer, AutomationWorkflowPhase.FinalVerification) =>
            "최종 검토자입니다. 파일을 수정하지 마세요. 최신 MD의 모든 인수 기준, 실제 빌드·테스트 결과와 서비스 실행 근거를 대조하세요. 보고된 서비스가 지금도 정상 실행되는지 실제 주소 응답/프로세스/앱 창과 핵심 동작을 독립적으로 확인하고 사용자에게 실제 서비스가 표시되었는지 검증하세요. 직접 확인한 실행·표시 근거를 deliveryEvidence에 남기세요. 전부 통과했을 때만 complete=true로 하고 summary에 `Verified criteria:`와 `Verification evidence:` 및 실행 주소/창과 확인 결과를 적으세요. 실패·누락은 complete=false로 구체적인 실패 근거와 수정안을 지휘자에게 돌려보내세요. 빌드 성공이나 구현자의 주장만으로 완료하지 마세요.",
        _ =>
            "검토자입니다. 파일을 수정하지 말고 최신 MD와 지휘 계획의 각 인수 기준을 구현 결과와 실제 빌드·테스트 근거에 대조하세요. 필요한 독립 검증과 핵심 동작 확인을 실제 수행하세요. 모든 기준과 검증이 통과하면 complete=true로 하고 summary에 `Verified criteria:`와 `Verification evidence:`를 적으세요. 이 판정은 서비스 실행 전 검증 통과일 뿐 전체 완료는 아닙니다. 미충족 기준은 complete=false로 하고 `Unmet criterion:`과 `Failing evidence:`에 구체적인 근거를 남기고 지휘자가 수정 작업을 지시할 수 있게 조치를 instruction에 적으세요. 권한·정보 부족도 정확히 보고하세요."
    };

    public static async Task<AutomationStageOutput?> ReadCompletionAsync(AutomationCommand command)
    {
        if (!File.Exists(command.CompletionPath)) return null;
        long tokens = 0;
        try
        {
            using var marker = JsonDocument.Parse(await File.ReadAllTextAsync(command.CompletionPath));
            int exit = marker.RootElement.GetProperty("exitCode").GetInt32();
            string? finalResponse = null;
            string? agentError = null;
            var tokenParts = new HashSet<string>(StringComparer.Ordinal);
            if (File.Exists(command.EventsPath))
            {
                using var stream = new StreamReader(command.EventsPath);
                while (await stream.ReadLineAsync() is { } line)
                {
                    try
                    {
                        using var json = JsonDocument.Parse(line);
                        var evt = json.RootElement;
                        if (evt.ValueKind != JsonValueKind.Object || !evt.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String) continue;
                        string? eventType = type.GetString();
                        if (eventType == "error") agentError = evt.TryGetProperty("error", out var error) ? error.ToString() : "OpenCode 실행 오류";
                        if (evt.TryGetProperty("part", out var part) && part.ValueKind == JsonValueKind.Object)
                        {
                            if (eventType == "text" && part.TryGetProperty("text", out var response) && response.ValueKind == JsonValueKind.String) finalResponse = response.GetString();
                            if (eventType == "step_finish" && part.TryGetProperty("tokens", out var usage) && usage.ValueKind == JsonValueKind.Object)
                            {
                                string fingerprint = part.TryGetProperty("id", out var partId) ? partId.ToString() : line;
                                if (tokenParts.Add(fingerprint))
                                {
                                    long? total = Number(usage, "total");
                                    if (total is not null) tokens += total.Value;
                                    else
                                    {
                                        tokens += (Number(usage, "input") ?? 0) + (Number(usage, "output") ?? 0) + (Number(usage, "reasoning") ?? 0);
                                        if (usage.TryGetProperty("cache", out var cache) && cache.ValueKind == JsonValueKind.Object) tokens += (Number(cache, "read") ?? 0) + (Number(cache, "write") ?? 0);
                                    }
                                }
                            }
                        }
                        // Historical results remain readable after changing the default runner to OpenCode.
                        if (eventType == "turn.completed" && evt.TryGetProperty("usage", out var legacyUsage) && legacyUsage.ValueKind == JsonValueKind.Object)
                        {
                            tokens += (Number(legacyUsage, "input_tokens") ?? 0) + (Number(legacyUsage, "output_tokens") ?? 0);
                        }
                    }
                    catch (JsonException) { }
                }
            }
            if (exit != 0)
            {
                string scriptError = marker.RootElement.TryGetProperty("error", out var markerError) &&
                    markerError.ValueKind == JsonValueKind.String
                    ? markerError.GetString() ?? ""
                    : "";
                string cliOutput = ReadPlainCliFailureOutput(command.EventsPath);
                string detail = BoundFailureText(string.Join(" · ", new[] { scriptError, agentError, cliOutput }
                    .Where(value => !string.IsNullOrWhiteSpace(value))));
                string summary = $"CLI 종료 코드 {exit}" + (detail.Length == 0 ? "" : " · " + detail);
                return new(false, summary, "", false, tokens);
            }
            if (agentError is not null) return new(false, "OpenCode 오류 · " + agentError, "", false, tokens);
            if (!string.IsNullOrWhiteSpace(finalResponse))
            {
                if (!AutomationStageOutputContract.TryParse(
                    finalResponse,
                    command.Role,
                    command.WorkflowPhase,
                    out AutomationStageOutput? parsedOutput,
                    out string? selectedJson,
                    out string? parseError))
                {
                    return new(false, parseError ?? "OpenCode returned an invalid stage result.", "", false, tokens);
                }

                // Output text is never executed as a shell command. Persist only the selected result object.
                await File.WriteAllTextAsync(command.ResultPath, selectedJson!, new UTF8Encoding(false));
                return parsedOutput! with { Tokens = tokens };
            }
            if (!File.Exists(command.ResultPath)) return new(false, "결과 파일이 없어 다음 단계를 실행하지 않았습니다.", "", false, tokens);
            if (!AutomationStageOutputContract.TryParse(
                await File.ReadAllTextAsync(command.ResultPath),
                command.Role,
                command.WorkflowPhase,
                out AutomationStageOutput? resultOutput,
                out _,
                out string? resultError))
            {
                return new(false, resultError ?? "The saved stage result is invalid.", "", false, tokens);
            }

            return resultOutput! with { Tokens = tokens };
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or IOException or UnauthorizedAccessException)
        {
            return new(false, "단계 결과를 확인하지 못해 다음 작업을 멈췄습니다 · " + ex.Message, "", false, tokens);
        }
    }

    private static long? Number(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) && number >= 0 ? number : null;

    private static string ReadPlainCliFailureOutput(string eventsPath)
    {
        if (!File.Exists(eventsPath)) return "";
        try
        {
            using var stream = new FileStream(eventsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            const int tailLength = 16 * 1024;
            long start = Math.Max(0, stream.Length - tailLength);
            stream.Seek(start, SeekOrigin.Begin);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 1024, leaveOpen: false);
            if (start > 0) _ = reader.ReadLine(); // Discard a possibly partial line at the tail boundary.

            var lines = new Queue<string>();
            while (reader.ReadLine() is { } line)
            {
                if (string.IsNullOrWhiteSpace(line) || IsJsonOutputLine(line)) continue;
                string cleaned = BoundFailureText(line, MaxCliFailureLineLength);
                if (cleaned.Length == 0) continue;
                if (lines.Count == 5) lines.Dequeue();
                lines.Enqueue(cleaned);
            }
            return BoundFailureText(string.Join(" · ", lines));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }

    private static bool IsJsonOutputLine(string line)
    {
        try
        {
            using var json = JsonDocument.Parse(line);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string BoundFailureText(string value, int maxLength = MaxCliFailureDetailLength)
    {
        var cleaned = new StringBuilder(Math.Min(value.Length, maxLength));
        foreach (char character in value)
        {
            if (char.IsControl(character))
            {
                if (character is '\r' or '\n' or '\t')
                    cleaned.Append(' ');
                continue;
            }
            cleaned.Append(character);
            if (cleaned.Length >= maxLength) break;
        }
        return cleaned.ToString().Trim();
    }
}

/// <summary>Shared validation for native and CLI role output, including plan approval and delivery evidence.</summary>
public static class AutomationStageOutputContract
{
    private const int MaximumJsonDepth = 24;
    private static readonly JsonSerializerOptions WireJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static bool TryParse(
        string text,
        AutomationRole? role,
        out AutomationStageOutput? output,
        out string? selectedJson,
        out string? error)
        => TryParse(text, role, null, out output, out selectedJson, out error);

    /// <summary>
    /// Parses a stage result and, when the phase is Planning, requires the
    /// structured Coordinator planning object. The phase-less overload remains
    /// compatible with historical callers and non-planning output fixtures.
    /// </summary>
    public static bool TryParse(
        string text,
        AutomationRole? role,
        AutomationWorkflowPhase? phase,
        out AutomationStageOutput? output,
        out string? selectedJson,
        out string? error)
    {
        output = null;
        selectedJson = null;
        error = null;
        string? lastValidJson = null;
        AutomationStageOutput? lastValidOutput = null;

        foreach (string candidate in FindObjectCandidates(text))
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(candidate, new JsonDocumentOptions { MaxDepth = MaximumJsonDepth });
                JsonElement root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object ||
                    !TryGetSingleProperty(root, "summary", out JsonElement summary) || summary.ValueKind != JsonValueKind.String ||
                    !TryGetSingleProperty(root, "instruction", out JsonElement instruction) || instruction.ValueKind != JsonValueKind.String ||
                    !TryGetSingleProperty(root, "complete", out JsonElement complete) || complete.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    continue;
                }

                bool planApproved = false;
                string? deliveryEvidence = null;
                AutomationPlanningOutput? planningPlan = null;
                if (root.TryGetProperty("planApproved", out _))
                {
                    if (!TryGetSingleProperty(root, "planApproved", out JsonElement approval) ||
                        approval.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) continue;
                    planApproved = approval.GetBoolean();
                }
                if (root.TryGetProperty("deliveryEvidence", out _))
                {
                    if (!TryGetSingleProperty(root, "deliveryEvidence", out JsonElement evidence) ||
                        evidence.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)) continue;
                    deliveryEvidence = evidence.ValueKind == JsonValueKind.String ? evidence.GetString()?.Trim() : null;
                }
                if (root.TryGetProperty("planning", out _))
                {
                    if (!TryGetSingleProperty(root, "planning", out JsonElement planning) ||
                        (planning.ValueKind != JsonValueKind.Object && planning.ValueKind != JsonValueKind.Null)) continue;
                    if (planning.ValueKind == JsonValueKind.Object &&
                        !TryParsePlanningOutput(planning, out planningPlan)) continue;
                }
                lastValidJson = candidate;
                lastValidOutput = new AutomationStageOutput(
                    true,
                    summary.GetString() ?? "",
                    instruction.GetString() ?? "",
                    complete.GetBoolean(),
                    0)
                {
                    PlanApproved = role == AutomationRole.Reviewer && planApproved,
                    DeliveryEvidence = string.IsNullOrWhiteSpace(deliveryEvidence) ? null : deliveryEvidence,
                    PlanningPlan = planningPlan
                };
            }
            catch (JsonException)
            {
                // Prose may contain braces or examples; only parseable JSON objects are candidates.
            }
        }

        if (lastValidOutput is null || lastValidJson is null)
        {
            error = "The response does not contain valid standalone JSON with string summary, string instruction, and boolean complete fields.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(lastValidOutput.Summary))
        {
            error = "The stage summary must contain meaningful text.";
            return false;
        }

        if (phase == AutomationWorkflowPhase.Planning && role == AutomationRole.Coordinator)
        {
            if (lastValidOutput.PlanningPlan is null)
            {
                error = "Planning Coordinator output must contain a structured planning object.";
                return false;
            }

            if (!lastValidOutput.PlanningPlan.TryValidate(out string? planningError))
            {
                error = planningError;
                return false;
            }
        }

        // Keep the wire contract strict, but do not fail useful model work because a
        // human-readable label was translated, omitted, or formatted differently.
        // Normalize routing locally so the persisted workflow still has one explicit
        // next role. Only Reviewer may complete a run.
        AutomationStageOutput normalized = NormalizeRoleOutput(lastValidOutput, role);
        output = normalized;
        selectedJson = JsonSerializer.Serialize(new
        {
            summary = normalized.Summary,
            instruction = normalized.Instruction,
            complete = normalized.Complete,
            planApproved = normalized.PlanApproved,
            deliveryEvidence = normalized.DeliveryEvidence,
            planning = normalized.PlanningPlan
        }, WireJsonOptions);
        return true;
    }

    private static bool TryParsePlanningOutput(JsonElement value, out AutomationPlanningOutput? planning)
    {
        planning = null;
        if (!TryGetString(value, "goalsAndScope", out string? goalsAndScope) ||
            !TryGetStringArray(value, "ideasAndChoices", out IReadOnlyList<string>? ideasAndChoices) ||
            !TryGetStringArray(value, "designFilesTasks", out IReadOnlyList<string>? designFilesTasks) ||
            !TryGetStringArray(value, "orderedImplementation", out IReadOnlyList<string>? orderedImplementation) ||
            !TryGetStringArray(value, "risksAndEdgeCases", out IReadOnlyList<string>? risksAndEdgeCases) ||
            !TryGetStringArray(value, "acceptanceTests", out IReadOnlyList<string>? acceptanceTests) ||
            !TryGetString(value, "serviceDemo", out string? serviceDemo))
        {
            return false;
        }

        var candidate = new AutomationPlanningOutput(
            goalsAndScope!,
            ideasAndChoices!,
            designFilesTasks!,
            orderedImplementation!,
            risksAndEdgeCases!,
            acceptanceTests!,
            serviceDemo!);
        if (!candidate.TryValidate(out _)) return false;
        planning = candidate;
        return true;
    }

    private static bool TryGetString(JsonElement root, string name, out string? value)
    {
        value = null;
        return TryGetSingleProperty(root, name, out JsonElement element) &&
            element.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(value = element.GetString());
    }

    private static bool TryGetStringArray(JsonElement root, string name, out IReadOnlyList<string>? values)
    {
        values = null;
        if (!TryGetSingleProperty(root, name, out JsonElement element) || element.ValueKind != JsonValueKind.Array)
            return false;

        var parsed = new List<string>();
        foreach (JsonElement item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()))
                return false;
            parsed.Add(item.GetString()!);
        }

        values = parsed;
        return true;
    }

    private static AutomationStageOutput NormalizeRoleOutput(
        AutomationStageOutput output,
        AutomationRole? role)
    {
        string instruction = output.Instruction.Trim();
        return role switch
        {
            AutomationRole.Coordinator => output with
            {
                Instruction = NormalizeCoordinatorInstruction(instruction),
                Complete = false
            },
            AutomationRole.Implementer => output with
            {
                Instruction = NormalizeInstruction(
                    instruction,
                    "Reviewer verification",
                    "승인된 플랜의 각 기준을 현재 구현과 실제 검증 결과에 대조하세요."),
                Complete = false
            },
            AutomationRole.Reviewer when !output.Complete => output with
            {
                Instruction = NormalizeInstruction(
                    instruction,
                    "Implementer action",
                    "검토 요약의 미충족 사항을 수정하고 필요한 검증을 다시 실행하세요.")
            },
            _ => output
        };
    }

    private static string NormalizeCoordinatorInstruction(string instruction)
    {
        if (StartsWithLabeledValue(instruction, "Implementer action") ||
            StartsWithLabeledValue(instruction, "Reviewer verification"))
            return instruction;

        string lowered = instruction.ToLowerInvariant();
        bool implementation = lowered.Contains("implement", StringComparison.Ordinal) ||
            instruction.Contains("구현", StringComparison.Ordinal) ||
            instruction.Contains("수정", StringComparison.Ordinal) ||
            instruction.Contains("개선", StringComparison.Ordinal);
        bool review = lowered.Contains("review", StringComparison.Ordinal) ||
            lowered.Contains("verify", StringComparison.Ordinal) ||
            instruction.Contains("검토", StringComparison.Ordinal) ||
            instruction.Contains("검증", StringComparison.Ordinal) ||
            instruction.Contains("확인", StringComparison.Ordinal);

        if (!implementation && review)
            return NormalizeInstruction(instruction, "Reviewer verification",
                "승인된 플랜의 각 기준을 현재 구현과 실제 검증 결과에 대조하세요.");
        return NormalizeInstruction(instruction, "Implementer action",
            "승인된 플랜의 요구사항을 구현하고 필요한 검증을 실행하세요.");
    }

    private static string NormalizeInstruction(string instruction, string label, string fallback) =>
        StartsWithLabeledValue(instruction, label)
            ? instruction
            : label + ": " + (string.IsNullOrWhiteSpace(instruction) ? fallback : instruction);

    public static AutomationRole? RoleFromPrompt(string prompt)
    {
        const string headingPrefix = "Terminal Notes 자동화 · ";
        string firstLine = prompt.Split('\n', 2)[0].TrimEnd('\r');
        if (!firstLine.StartsWith(headingPrefix, StringComparison.Ordinal)) return null;

        int roleStart = headingPrefix.Length;
        int roleEnd = firstLine.IndexOf(" · ", roleStart, StringComparison.Ordinal);
        return roleEnd > roleStart && Enum.TryParse(firstLine[roleStart..roleEnd], ignoreCase: false, out AutomationRole role)
            ? role
            : null;
    }

    /// <summary>
    /// Reads the explicit workflow phase embedded in a generated prompt. This is
    /// only a compatibility fallback for callers that do not retain dispatch
    /// metadata; it is not used as semantic evidence of stage completion.
    /// </summary>
    public static AutomationWorkflowPhase? PhaseFromPrompt(string prompt)
    {
        foreach (string rawLine in prompt.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r').Trim();
            const string prefix = "Workflow phase:";
            if (!line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            string value = line[prefix.Length..].Trim();
            return Enum.TryParse(value, ignoreCase: true, out AutomationWorkflowPhase phase)
                ? phase
                : null;
        }

        return null;
    }

    private static IEnumerable<string> FindObjectCandidates(string text)
    {
        for (int start = 0; start < text.Length; start++)
        {
            if (text[start] != '{') continue;
            int depth = 0;
            bool inString = false;
            bool escaped = false;
            for (int end = start; end < text.Length; end++)
            {
                char character = text[end];
                if (inString)
                {
                    if (escaped) escaped = false;
                    else if (character == '\\') escaped = true;
                    else if (character == '"') inString = false;
                    continue;
                }

                if (character == '"') inString = true;
                else if (character == '{') depth++;
                else if (character == '}' && --depth == 0)
                {
                    yield return text[start..(end + 1)];
                    start = end;
                    break;
                }
            }
        }
    }

    private static bool TryGetSingleProperty(JsonElement root, string name, out JsonElement value)
    {
        value = default;
        int count = 0;
        foreach (JsonProperty property in root.EnumerateObject())
        {
            if (!string.Equals(property.Name, name, StringComparison.Ordinal)) continue;
            value = property.Value;
            count++;
        }
        return count == 1;
    }

    private static bool HasLabeledValue(string text, string label)
    {
        string prefix = label + ":";
        foreach (string line in text.Split('\n'))
        {
            string candidate = line.Trim().TrimEnd('\r');
            if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            string value = candidate[prefix.Length..].Trim();
            if (value.Length == 0 || value.StartsWith('<') || value.EndsWith('>')) return false;
            return !value.Equals("none", StringComparison.OrdinalIgnoreCase) &&
                !value.Equals("n/a", StringComparison.OrdinalIgnoreCase) &&
                !value.Equals("unknown", StringComparison.OrdinalIgnoreCase) &&
                !value.Equals("tbd", StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    private static bool StartsWithLabeledValue(string text, string label)
    {
        string prefix = label + ":";
        if (!text.TrimStart().StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        string value = text.TrimStart()[prefix.Length..].Trim();
        return value.Length > 0 && !value.StartsWith('<') && !value.EndsWith('>') &&
            !value.Equals("none", StringComparison.OrdinalIgnoreCase) &&
            !value.Equals("n/a", StringComparison.OrdinalIgnoreCase) &&
            !value.Equals("unknown", StringComparison.OrdinalIgnoreCase) &&
            !value.Equals("tbd", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasNumberedAcceptanceChecks(string summary)
    {
        string[] lines = summary.Split('\n');
        int section = Array.FindIndex(lines, line =>
            string.Equals(line.Trim().TrimEnd('\r'), "Acceptance checks:", StringComparison.OrdinalIgnoreCase));
        if (section < 0) return false;

        for (int index = section + 1; index < lines.Length; index++)
        {
            string line = lines[index].Trim().TrimEnd('\r');
            if (line.Length == 0) continue;
            int cursor = 0;
            while (cursor < line.Length && char.IsDigit(line[cursor])) cursor++;
            if (cursor == 0 || cursor + 1 >= line.Length || line[cursor] != '.' || !char.IsWhiteSpace(line[cursor + 1]))
                return false;
            return !string.IsNullOrWhiteSpace(line[(cursor + 1)..]);
        }
        return false;
    }
}
