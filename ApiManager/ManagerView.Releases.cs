using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace ApiManager;

/// <summary>
/// 릴리스 화면: 앱 목록(과 서버 설정 LAUNCHER_GITHUB_REPOS)의 저장소마다 마지막 릴리스와 다음 버전(자동)을 보여 주고,
/// 버전을 고쳐 릴리스함. 서버(relay 2.15+)가 저장소의 .github/workflows/release.yml 을 그 버전으로 실행함.
/// </summary>
public partial class ManagerView
{
    private List<ReleaseRow> _releases = [];
    // 진행 중인 릴리스가 있을 때만 이 화면을 10초마다 새로 읽음
    private DispatcherTimer? _releaseTimer;

    private async Task LoadReleasesAsync()
    {
        var answer = (await Admin.GetAsync("admin/api/releases")).AsObject();
        var rows = answer["releases"]!.AsArray().OfType<JsonObject>().Select(data => new ReleaseRow(data)).ToList();
        // 고쳐 둔 버전은 마지막 릴리스가 그대로인 동안 남김
        foreach (var row in rows)
            if (_releases.FirstOrDefault(old => old.Repository == row.Repository) is { Edited: true } old && old.LatestTag == row.LatestTag && !row.IsVersionFixed)
                row.KeepVersion(old.Version);
        int selected = ReleasesList.SelectedIndex;
        _releases = rows;
        ReleasesList.ItemsSource = _releases;
        if (selected >= 0 && selected < _releases.Count) ReleasesList.SelectedIndex = selected;
        UpdateReleaseTimer();
    }

    private void UpdateReleaseTimer()
    {
        if (_releaseTimer is null)
        {
            _releaseTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
            _releaseTimer.Tick += async (_, _) =>
            {
                if (_busy || CurrentSection != "Releases") return;
                await Run(LoadReleasesAsync, quiet: true);
            };
        }
        if (_releases.Any(row => row.Active) && IsVisible) _releaseTimer.Start();
        else _releaseTimer.Stop();
    }

    private async void ReleaseButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ReleaseRow row || !row.CanRelease) return;
        string version = row.Version.Trim().TrimStart('v', 'V');
        if (MessageBox.Show(OwnerWindow, $"{row.Name} v{version}을(를) 릴리스할까요?", "API Manager",
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        row.Starting = true;
        bool started = await Run(() => Admin.SendAsync(HttpMethod.Post, "admin/api/releases",
            new JsonObject { ["repository"] = row.Repository, ["version"] = version }), done: $"{row.Name} v{version} 릴리스를 시작했습니다.");
        row.Starting = false;
        if (started) await Run(LoadReleasesAsync, quiet: true);
    }

    private void ReleaseStatusButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ReleaseRow { RunUrl.Length: > 0 } row) return;
        try { Process.Start(new ProcessStartInfo(row.RunUrl) { UseShellExecute = true })?.Dispose(); }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException) { SetMessage(error.Message, warning: true); }
    }
}

/// <summary>릴리스 화면의 한 줄 (서버의 /admin/api/releases 항목)</summary>
internal sealed partial class ReleaseRow : INotifyPropertyChanged
{
    [GeneratedRegex(@"^\s*[vV]?(\d{1,5})\.(\d{1,5})\.(\d{1,6})\s*$")]
    private static partial Regex VersionPattern();

    private string _version;
    private bool _starting;

    public ReleaseRow(JsonObject data)
    {
        Repository = Text(data["repository"]);
        Name = Text(data["name"]) is { Length: > 0 } name ? name : Repository;
        LatestTag = Text(data["latestTag"]);
        LatestVersion = Text(data["latest"]);
        Next = Text(data["next"]);
        Floor = Text(data["floor"]);
        Error = Text(data["error"]);
        ChooseVersion = (bool?)data["chooseVersion"] == true;
        if (data["run"] is JsonObject run)
        {
            RunStatus = Text(run["status"]);
            RunConclusion = Text(run["conclusion"]);
            RunEvent = Text(run["event"]);
            RunUrl = Text(run["url"]);
            RunVersion = Text(run["version"]);
            if (DateTimeOffset.TryParse(Text(run["createdAt"]), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)) RunAt = at.ToLocalTime();
        }
        _version = Next;
    }

    public string Repository { get; }
    public string Name { get; }
    public string LatestTag { get; }
    public string LatestVersion { get; }
    public string Next { get; }
    public string Floor { get; }
    public string Error { get; }
    public bool ChooseVersion { get; }
    public string RunStatus { get; } = "";
    public string RunConclusion { get; } = "";
    public string RunEvent { get; } = "";
    public string RunUrl { get; } = "";
    public string RunVersion { get; } = "";
    public DateTimeOffset? RunAt { get; }
    public bool Edited { get; private set; }

    public string Latest => LatestTag.Length > 0 ? LatestTag : "—";

    /// <summary>버전을 받지 않는 예전 워크플로는 다음 버전으로만 릴리스</summary>
    public bool IsVersionFixed => !ChooseVersion || Error.Length > 0;

    public string Version
    {
        get => _version;
        set
        {
            if (_version == value) return;
            _version = value;
            Edited = true;
            Changed(nameof(Version), nameof(CanRelease));
        }
    }

    public void KeepVersion(string version)
    {
        _version = version;
        Edited = true;
    }

    public bool Starting
    {
        get => _starting;
        set { _starting = value; Changed(nameof(CanRelease), nameof(Status), nameof(StatusBrush)); }
    }

    public bool Active => RunStatus is "requested" or "queued" or "pending" or "waiting" or "in_progress";

    public bool CanRelease => !_starting && Error.Length == 0 && !Active && Parse(_version) is { } wanted &&
        (Parse(LatestVersion) is not { } latest || wanted > latest);

    public string Status => _starting ? "시작하는 중…"
        : Error.Length > 0 ? Error
        : RunStatus switch
        {
            "" => "—",
            "requested" => "요청함" + (RunVersion.Length > 0 ? $" · v{RunVersion}" : ""),
            "queued" or "pending" or "waiting" => "대기 중",
            "in_progress" => "릴리스 중…",
            _ => RunConclusion switch
            {
                "success" => "완료",
                "cancelled" => "취소됨",
                "" => RunStatus,
                _ => "실패",
            } + (RunAt is { } at ? " · " + at.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture) : ""),
        };

    public Brush StatusBrush => (Brush)ManagerHost.Resource(
        _starting || Active ? "AccentBrush"
        : Error.Length > 0 || RunStatus == "completed" && RunConclusion is not ("success" or "") ? "WarnBrush"
        : RunConclusion == "success" ? "GoodBrush" : "MutedTextBrush");

    public System.Windows.Input.Cursor StatusCursor => RunUrl.Length > 0 ? System.Windows.Input.Cursors.Hand : System.Windows.Input.Cursors.Arrow;

    public string? StatusTip => Error.Length > 0 ? Error
        : RunUrl.Length == 0 ? null
        : (RunEvent == "push" ? "푸시로 실행됨" : RunEvent == "workflow_dispatch" ? "직접 실행됨" : RunEvent) + " · 누르면 GitHub 실행 기록";

    public string Tip => Repository + (Next.Length > 0 ? $"\n다음 버전 {Next}" + (Floor.Length > 0 ? $" (프로젝트 파일 {Floor})" : "") : "") +
        (IsVersionFixed && Error.Length == 0 ? "\n이 저장소의 릴리스 워크플로는 버전을 받지 않아 다음 버전으로만 릴리스합니다." : "");

    private static Version? Parse(string text) =>
        VersionPattern().Match(text ?? "") is { Success: true } match
            ? new Version(int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture),
                int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture))
            : null;

    private static string Text(JsonNode? node) => node is JsonValue value && value.TryGetValue(out string? text) ? text : "";

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Changed(params string[] names)
    {
        foreach (string name in names) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public override string ToString() => $"{Name} · {Latest} · {Status}";
}
