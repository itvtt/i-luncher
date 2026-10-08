using System.Windows.Controls;
using TerminalNotes.Services;

namespace TerminalNotes;

public partial class MainWindow
{
    private sealed class AutomationProjectRuntime(string project)
    {
        public string Project { get; } = project;
        public Dictionary<AutomationRole, TabItem> Team { get; } = [];
        public TabItem? SingleTerminal;
        public bool StartingSingle;
        public string? SingleStartPhase;
        public CancellationTokenSource? SingleStartupCancellation;
        public bool SingleSubmitted;
        public bool SinglePlanInputNeeded;
        public bool SingleStopped;
        public string? SingleError;
        public ActiveAutomationStage? Stage;
        public (Guid RunId, TerminalPane Pane)? StartingNative;
        public AutomationStepClaim? Dispatching;
        public Queue<(AutomationStore Store, AutomationStepClaim Claim)> Pending { get; } = [];
        public bool Polling, StartingRun;
        public long IntentVersion;
        public bool MicroPolling;
        public Dictionary<AutomationRole, MicroManagementStage> MicroStages { get; } = [];
        public Dictionary<AutomationRole, DateTimeOffset> NextMicroCheck { get; } = [];
        public string? ReviewerAdvice, CoordinatorAdvice, RequestedIntervention;
    }

    private readonly Dictionary<string, AutomationProjectRuntime> _automationRuntimes = new(StringComparer.OrdinalIgnoreCase);
    private AutomationProjectRuntime RuntimeFor(string project)
    {
        project = CanonicalProjectPath(project);
        if (!_automationRuntimes.TryGetValue(project, out var runtime))
            _automationRuntimes.Add(project, runtime = new(project));
        return runtime;
    }
    private readonly SemaphoreSlim _automationProjectWriteGate = new(1, 1);
    private bool _automationPolling => _automationRuntimes.Values.Any(runtime => runtime.Polling || runtime.MicroPolling || runtime.StartingRun);
    private bool IsStartingNativePane(TerminalPane pane) => _automationRuntimes.Values.Any(runtime => runtime.StartingNative?.Pane == pane);
    private bool IsTaskTerminal(TabItem tab) => _automationRuntimes.Values.Any(runtime => runtime.Team.ContainsValue(tab) || runtime.SingleTerminal == tab);
    private int ManualTerminalCount => TerminalTabs.Items.Cast<TabItem>().Count(tab => !IsTaskTerminal(tab));
}
