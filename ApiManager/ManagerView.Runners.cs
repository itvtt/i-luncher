using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace ApiManager;

/// <summary>
/// 러너 화면: 이 PC의 GitHub Actions 러너 (서버 relay 2.18+, relay.py SETTINGS 의 RUNNERS_ROOT). 고른 러너를 시작·멈추기·다시 시작·삭제하고
/// .env(러너가 작업에 넘기는 환경 변수)를 고침. + 로 새 러너를 저장소에 등록. 로그온 작업 하나(GitHub Runners)가 켜 둔 러너를 시작하고,
/// 멈춘 러너는 꺼 둔 것으로 남음. 바꾸는 요청은 서버가 서버 PC 에서 온 것만 받음.
/// </summary>
public partial class ManagerView
{
    // 등록(파일 준비·config.cmd)·멈추기(작업 정리)는 몇십 초 걸릴 수 있음
    private static readonly TimeSpan RunnerTimeout = TimeSpan.FromMinutes(5);
    private List<RunnerRow> _runners = [];
    private JsonArray _runnerRepositories = [];
    private DispatcherTimer? _runnerTimer;
    private string? _runnerId;
    private string _runnerEnvSaved = "";
    private string _templateFor = "";
    private int _runnerLegacyTasks;
    private bool _runnerBusy, _runnerLoading, _addingRunner, _selectingRunner;

    private bool RunnerEnvDirty => _runnerId is not null && RunnerEnvBox.Text.Replace("\r\n", "\n") != _runnerEnvSaved;

    private RunnerRow? CurrentRunner => _runners.FirstOrDefault(row => row.Id == _runnerId);

    private async Task LoadRunnersAsync()
    {
        if (_runnerLoading) return;
        _runnerLoading = true;
        try
        {
            var answer = (await Admin.GetAsync("admin/api/runners")).AsObject();
            bool available = (bool?)answer["available"] == true;
            RunnersMessageText.Text = Str(answer["message"]);
            RunnersMessageText.Visibility = available ? Visibility.Collapsed : Visibility.Visible;
            RunnersBody.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
            ShowRunnerTask(answer["task"] as JsonObject, Str(answer["root"]));
            _runnerRepositories = answer["repositories"] as JsonArray ?? [];
            _runners = (answer["runners"] as JsonArray ?? []).OfType<JsonObject>().Select(data => new RunnerRow(data)).ToList();
            var row = _runners.FirstOrDefault(item => item.Id == _runnerId) ?? (_addingRunner ? null : _runners.FirstOrDefault());
            _selectingRunner = true;
            RunnersList.ItemsSource = _runners;
            RunnersList.SelectedItem = row;
            _selectingRunner = false;
            if (!_addingRunner) ShowRunner(row);
        }
        finally
        {
            _runnerLoading = false;
            _selectingRunner = false;
            UpdateRunnerTimer();
        }
    }

    /// <summary>러너 화면을 보는 동안 10초마다 상태를 새로 읽음 (GitHub 상태는 서버가 30초 동안 기억)</summary>
    private void UpdateRunnerTimer()
    {
        if (_runnerTimer is null)
        {
            _runnerTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
            _runnerTimer.Tick += async (_, _) =>
            {
                if (_runnerBusy || _busy || CurrentSection != "Runners" || !IsVisible) return;
                await Run(LoadRunnersAsync, quiet: true);
            };
        }
        if (IsVisible && CurrentSection == "Runners" && RunnersBody.Visibility == Visibility.Visible) _runnerTimer.Start();
        else _runnerTimer.Stop();
    }

    private void ShowRunnerTask(JsonObject? task, string root)
    {
        if (task is null)
        {
            RunnerTaskBar.Visibility = Visibility.Collapsed;
            return;
        }
        bool exists = (bool?)task["exists"] == true, current = (bool?)task["current"] == true;
        _runnerLegacyTasks = (task["legacy"] as JsonArray)?.Count ?? 0;
        var problems = new List<string>();
        if (!exists) problems.Add("로그온 작업 없음");
        else if (!current) problems.Add("로그온 작업 경로가 다름");
        if (_runnerLegacyTasks > 0) problems.Add($"예전 러너 작업 {_runnerLegacyTasks}개");
        RunnerTaskText.Text = problems.Count == 0 ? $"로그온 작업 {Str(task["name"])}" : string.Join(" · ", problems);
        RunnerTaskText.Foreground = problems.Count == 0 ? Muted : Warn;
        RunnerTaskText.ToolTip = root;
        RunnerTaskButton.Visibility = problems.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        RunnerTaskBar.Visibility = Visibility.Visible;
    }

    private void ShowRunner(RunnerRow? row)
    {
        bool same = row is not null && row.Id == _runnerId;
        bool keepEdits = same && RunnerEnvDirty;
        _runnerId = row?.Id;
        RunnerDetail.Visibility = row is null || _addingRunner ? Visibility.Collapsed : Visibility.Visible;
        if (row is null) return;
        RunnerStateDot.Fill = row.DotBrush;
        RunnerStateText.Text = row.State;
        RunnerGitHubText.Text = row.GitHubText;
        RunnerGitHubText.ToolTip = row.GitHubError.Length > 0 ? row.GitHubError : null;
        RunnerRepositoryLink.Content = row.Repository.Length > 0 ? row.Repository : "—";
        RunnerRepositoryLink.IsEnabled = row.Url.Length > 0;
        RunnerNameText.Text = row.Name.Length > 0 ? row.Name + (row.Version.Length > 0 ? $"   v{row.Version}" : "") : "—";
        RunnerFolderLink.Content = row.Folder;
        // 새로 읽어도 고치던 .env 는 그대로
        if (!keepEdits)
        {
            _runnerEnvSaved = row.Env;
            RunnerEnvBox.Text = row.Env.Replace("\n", "\r\n");
        }
        UpdateRunnerButtons();
    }

    private void UpdateRunnerButtons()
    {
        var row = CurrentRunner;
        StartRunnerButton.IsEnabled = !_runnerBusy && row is { Registered: true, Running: false };
        StopRunnerButton.IsEnabled = !_runnerBusy && row is not null && (row.Running || row.On);
        RestartRunnerButton.IsEnabled = !_runnerBusy && row is { Running: true };
        SaveRunnerEnvButton.IsEnabled = !_runnerBusy && RunnerEnvDirty;
        AddRunnerButton.IsEnabled = RegisterRunnerButton.IsEnabled = RunnerTaskButton.IsEnabled = !_runnerBusy;
    }

    private void RunnerEnvBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateRunnerButtons();

    private bool ConfirmDiscardRunnerEnv() =>
        MessageBox.Show(OwnerWindow, "저장하지 않은 .env를 버릴까요?", "러너", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;

    private void RunnersList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_selectingRunner || RunnersList.SelectedItem is not RunnerRow row || row.Id == _runnerId && !_addingRunner) return;
        if (RunnerEnvDirty && !ConfirmDiscardRunnerEnv())
        {
            _selectingRunner = true;
            RunnersList.SelectedItem = CurrentRunner;
            _selectingRunner = false;
            return;
        }
        _addingRunner = false;
        RunnerAddForm.Visibility = Visibility.Collapsed;
        _runnerId = null;
        ShowRunner(row);
    }

    /// <summary>서버에 러너 일 하나를 시키고 (시작·멈추기 등) 결과 문장을 아래에 보여 준 뒤 목록을 새로 읽음</summary>
    private async Task RunnerActionAsync(string working, Func<Task<JsonNode>> action, Action? done = null)
    {
        if (_runnerBusy) return;
        _runnerBusy = true;
        UpdateRunnerButtons();
        Cursor = Cursors.Wait;
        SetMessage(working);
        try
        {
            await Run(async () =>
            {
                var result = await action();
                done?.Invoke();
                SetMessage(Str(result["message"]) is { Length: > 0 } message ? message : "했습니다.");
            });
        }
        finally
        {
            _runnerBusy = false;
            Cursor = null;
        }
        await Run(LoadRunnersAsync, quiet: true);
        UpdateRunnerButtons();
    }

    private static string RunnerPath(RunnerRow row, string tail = "") => "admin/api/runners/" + Uri.EscapeDataString(row.Id) + tail;

    private async void StartRunnerButton_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentRunner is not { } row) return;
        await RunnerActionAsync($"{row.Id} 시작하는 중…", () => Admin.SendAsync(HttpMethod.Post, RunnerPath(row, "/start"), new JsonObject(), RunnerTimeout));
    }

    /// <summary>작업 중이면 빌드가 취소된다고 묻고, 그래도 하면 force</summary>
    private bool? ConfirmBusyRunner(RunnerRow row, string verb)
    {
        if (!row.Busy) return false;
        return MessageBox.Show(OwnerWindow, $"{row.Id}이(가) 작업 중입니다.\n\n{verb} 그 빌드가 취소됩니다.", "러너", MessageBoxButton.OKCancel,
            MessageBoxImage.Warning, MessageBoxResult.Cancel) == MessageBoxResult.OK ? true : null;
    }

    private async void StopRunnerButton_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentRunner is not { } row || ConfirmBusyRunner(row, "멈추면") is not { } force) return;
        await RunnerActionAsync($"{row.Id} 멈추는 중…",
            () => Admin.SendAsync(HttpMethod.Post, RunnerPath(row, "/stop"), new JsonObject { ["force"] = force }, RunnerTimeout));
    }

    private async void RestartRunnerButton_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentRunner is not { } row || ConfirmBusyRunner(row, "다시 시작하면") is not { } force) return;
        await RunnerActionAsync($"{row.Id} 다시 시작하는 중…",
            () => Admin.SendAsync(HttpMethod.Post, RunnerPath(row, "/restart"), new JsonObject { ["force"] = force }, RunnerTimeout));
    }

    private async void DeleteRunnerButton_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentRunner is not { } row) return;
        if (MessageBox.Show(OwnerWindow, $"{row.Id} 러너를 지울까요?\n\nGitHub 등록을 지우고 폴더를 삭제합니다.\n{row.Folder}" +
                (row.Busy ? "\n\n작업 중인 빌드는 취소됩니다." : ""), "러너", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;
        await RunnerActionAsync($"{row.Id} 지우는 중…",
            () => Admin.SendAsync(HttpMethod.Delete, RunnerPath(row, row.Busy ? "?force=1" : ""), null, RunnerTimeout),
            done: () => { _runnerId = null; _runnerEnvSaved = ""; RunnerEnvBox.Text = ""; });
    }

    private async void SaveRunnerEnvButton_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentRunner is not { } row || !RunnerEnvDirty) return;
        string env = RunnerEnvBox.Text.Replace("\r\n", "\n");
        await RunnerActionAsync($"{row.Id} .env 저장하는 중…",
            () => Admin.SendAsync(HttpMethod.Put, RunnerPath(row), new JsonObject { ["env"] = env }, RunnerTimeout),
            // 저장한 뒤에는 서버가 정리한 내용으로 다시 채움
            done: () => _runnerEnvSaved = RunnerEnvBox.Text.Replace("\r\n", "\n"));
    }

    private void RunnerRepositoryLink_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentRunner is not { Url.Length: > 0 } row) return;
        try { Process.Start(new ProcessStartInfo(row.Url) { UseShellExecute = true })?.Dispose(); }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException) { SetMessage(error.Message, warning: true); }
    }

    private void RunnerFolderLink_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentRunner is { } row) OpenWith("explorer.exe", row.Folder);
    }

    private async void RunnerTaskButton_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(OwnerWindow, "로그온 작업을 GitHub Runners 하나로 정리할까요?" +
                (_runnerLegacyTasks > 0 ? $"\n\n예전 러너 작업 {_runnerLegacyTasks}개는 지웁니다. 실행 중인 러너는 그대로입니다." : ""),
                "러너", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes) != MessageBoxResult.Yes) return;
        await RunnerActionAsync("로그온 작업 정리하는 중…", () => Admin.SendAsync(HttpMethod.Post, "admin/api/runners/task", new JsonObject(), RunnerTimeout));
    }

    // ── 새 러너 ──────────────────────────────────────────────────────────────────────────────────────

    private void AddRunnerButton_Click(object sender, RoutedEventArgs e)
    {
        if (RunnerEnvDirty && !ConfirmDiscardRunnerEnv()) return;
        _addingRunner = true;
        _runnerId = null;
        _selectingRunner = true;
        RunnersList.SelectedItem = null;
        _selectingRunner = false;
        RunnerDetail.Visibility = Visibility.Collapsed;
        RunnerAddForm.Visibility = Visibility.Visible;
        NewRunnerRepositoryBox.Text = NewRunnerFolderBox.Text = NewRunnerNameBox.Text = NewRunnerEnvBox.Text = "";
        _templateFor = "";
        UpdateRunnerButtons();
        NewRunnerRepositoryBox.Focus();
    }

    private void CancelRunnerButton_Click(object sender, RoutedEventArgs e)
    {
        _addingRunner = false;
        RunnerAddForm.Visibility = Visibility.Collapsed;
        var row = _runners.FirstOrDefault();
        _selectingRunner = true;
        RunnersList.SelectedItem = row;
        _selectingRunner = false;
        ShowRunner(row);
    }

    private void RunnerRepositoryMenuButton_Click(object sender, RoutedEventArgs e)
    {
        RunnerRepositoryMenu.Items.Clear();
        foreach (var entry in _runnerRepositories.OfType<JsonObject>())
        {
            string repository = Str(entry["repository"]);
            var item = new MenuItem { Header = repository, ToolTip = Str(entry["name"]) };
            item.Click += async (_, _) =>
            {
                NewRunnerRepositoryBox.Text = repository;
                await FillRunnerTemplateAsync();
            };
            RunnerRepositoryMenu.Items.Add(item);
        }
        RunnerRepositoryMenu.PlacementTarget = RunnerRepositoryMenuButton;
        RunnerRepositoryMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        RunnerRepositoryMenu.IsOpen = RunnerRepositoryMenu.Items.Count > 0;
    }

    private async void NewRunnerRepositoryBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => await FillRunnerTemplateAsync();

    private async void NewRunnerRepositoryBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await FillRunnerTemplateAsync();
    }

    /// <summary>저장소를 고르면 서버가 정한 폴더·러너 이름·.env (그 저장소의 릴리스 워크플로가 읽는 변수)로 채움</summary>
    private async Task FillRunnerTemplateAsync()
    {
        string repository = NewRunnerRepositoryBox.Text.Trim();
        if (repository.Length == 0 || repository == _templateFor) return;
        _templateFor = repository;
        await Run(async () =>
        {
            var template = (await Admin.GetAsync("admin/api/runners/template?repository=" + Uri.EscapeDataString(repository))).AsObject();
            if (!_addingRunner || NewRunnerRepositoryBox.Text.Trim() != repository) return;
            _templateFor = Str(template["repository"]);
            NewRunnerRepositoryBox.Text = _templateFor;
            NewRunnerFolderBox.Text = Str(template["folder"]);
            NewRunnerNameBox.Text = Str(template["name"]);
            NewRunnerEnvBox.Text = Str(template["env"]).Replace("\n", "\r\n");
        });
    }

    private async void RegisterRunnerButton_Click(object sender, RoutedEventArgs e)
    {
        string repository = NewRunnerRepositoryBox.Text.Trim();
        if (repository.Length == 0)
        {
            SetMessage("저장소를 고르세요.", warning: true);
            NewRunnerRepositoryBox.Focus();
            return;
        }
        var body = new JsonObject
        {
            ["repository"] = repository, ["folder"] = NewRunnerFolderBox.Text.Trim(), ["name"] = NewRunnerNameBox.Text.Trim(),
            ["env"] = NewRunnerEnvBox.Text.Replace("\r\n", "\n"),
        };
        string? added = null;
        await RunnerActionAsync($"{repository} 러너 등록하는 중…", async () =>
        {
            var result = await Admin.SendAsync(HttpMethod.Post, "admin/api/runners", body, RunnerTimeout);
            added = Str(result["id"]);
            return result;
        }, done: () =>
        {
            _addingRunner = false;
            RunnerAddForm.Visibility = Visibility.Collapsed;
            _runnerId = added;
            _runnerEnvSaved = "";
            RunnerEnvBox.Text = "";
        });
    }
}

/// <summary>러너 화면의 한 러너 (서버의 /admin/api/runners 항목)</summary>
internal sealed class RunnerRow
{
    public RunnerRow(JsonObject data)
    {
        Id = Text(data["id"]);
        Name = Text(data["name"]);
        Repository = Text(data["repository"]);
        Url = Text(data["url"]);
        Folder = Text(data["folder"]);
        Version = Text(data["version"]);
        Env = Text(data["env"]);
        GitHubError = Text(data["githubError"]);
        Registered = (bool?)data["registered"] == true;
        On = (bool?)data["on"] == true;
        Running = (bool?)data["running"] == true;
        Busy = (bool?)data["busy"] == true;
        if (data["github"] is JsonObject github)
        {
            GitHubStatus = Text(github["status"]);
            GitHubBusy = (bool?)github["busy"] == true;
        }
    }

    public string Id { get; }
    public string Name { get; }
    public string Repository { get; }
    public string Url { get; }
    public string Folder { get; }
    public string Version { get; }
    public string Env { get; }
    public string GitHubStatus { get; } = "";
    public string GitHubError { get; }
    public bool Registered { get; }
    public bool On { get; }
    public bool Running { get; }
    public bool Busy { get; }
    public bool GitHubBusy { get; }

    /// <summary>멈춤 = 켜 둔 러너가 돌지 않음 (살펴볼 것), 꺼짐 = 멈추기로 꺼 둠</summary>
    public string State => !Registered ? "등록 안 됨" : Busy ? "작업 중" : Running ? "실행 중" : On ? "멈춤" : "꺼짐";

    public string GitHubText => GitHubError.Length > 0 ? "GitHub 확인 못 함"
        : GitHubStatus switch { "online" => "GitHub 온라인", "offline" => "GitHub 오프라인", "missing" => "GitHub에 없음", _ => "" };

    public string Detail => Repository.Length > 0 ? $"{State} · {Repository}" : State;

    public Brush DotBrush => (Brush)ManagerHost.Resource(Busy ? "AccentBrush" : Running ? "GoodBrush" : On && Registered ? "WarnBrush" : "MutedTextBrush");

    private static string Text(JsonNode? node) => node is JsonValue value && value.TryGetValue(out string? text) ? text : "";

    public override string ToString() => $"{Id} · {State}";
}
