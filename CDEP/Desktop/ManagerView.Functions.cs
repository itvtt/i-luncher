using System.ComponentModel;
using System.Globalization;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace IApi.Manager;

/// <summary>
/// API 화면: 서버의 API 하나하나 (/admin/api/functions). 켜고 끄기 (끈 API 는 503 으로 거절), 오늘 · 30일 호출, 실패, 평균 시간,
/// 고른 API 의 설명 · 날짜별 막대 · 마지막 실패. 관리 창이 쓰는 API 와 상태 확인은 끄지 못함.
/// [+ API 추가] · [편집]: 사용자 API (고정 응답 · PowerShell · 다른 주소로 전달)를 이 화면에서 만들고 고치고 지움 (저장하면 바로 동작), [부르기]로 시험.
/// ⋯: 되돌리기 (서버가 바꿀 때마다 남기는 기록) · 내보내기 · 가져오기 (파일: 따로 보관하거나 다른 서버로 옮김)
/// </summary>
internal partial class ManagerView
{
    private List<FunctionRow> _functions = [];

    private async Task LoadFunctionsAsync()
    {
        JsonObject listing;
        try { listing = (await Admin.GetAsync("admin/api/functions?days=30")).AsObject(); }
        catch (AdminException error) when (error.Status == System.Net.HttpStatusCode.NotFound)
        {
            // 예전 Python 서버 (2.23 이하)
            _functions = [];
            FunctionsList.ItemsSource = null;
            ShowFunction(null);
            FunctionsSummaryText.Text = "돌고 있는 서버가 API 관리를 모릅니다 (예전 버전). [다시 시작]으로 이 버전의 서버를 켜세요.";
            return;
        }
        string? selected = (FunctionsList.SelectedItem as FunctionRow)?.Id;
        // 막대는 늘 최근 30일 (기록이 없는 날은 0): 기록이 하루뿐이어도 막대 너비가 같게
        int days = (int?)listing["days"] ?? 30;
        var span = DateTime.TryParse(listing["today"]?.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var today)
            ? Enumerable.Range(0, days).Select(back => today.AddDays(back - days + 1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).ToList() : [];
        _functions = (listing["functions"] as JsonArray ?? []).OfType<JsonObject>().Select(item => new FunctionRow(item, span)).ToList();
        var view = new ListCollectionView(_functions);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(FunctionRow.Group)));
        FunctionsList.ItemsSource = view;
        FunctionsList.SelectedItem = _functions.FirstOrDefault(row => row.Id == selected) ?? _functions.FirstOrDefault(row => row.Switchable);
        UpdateFunctionsSummary();
    }

    private void UpdateFunctionsSummary()
    {
        int off = _functions.Count(row => row.Switchable && !row.On);
        long today = _functions.Sum(row => row.TodayCalls), failures = _functions.Sum(row => row.TodayFailures);
        FunctionsSummaryText.Text = $"API {_functions.Count}개" + (off > 0 ? $" · 꺼 둔 API {off}개" : "") +
            $" · 오늘 호출 {today:N0}" + (failures > 0 ? $" · 실패 {failures:N0}" : "");
    }

    private void FunctionsList_SelectionChanged(object sender, SelectionChangedEventArgs e) => ShowFunction(FunctionsList.SelectedItem as FunctionRow);

    private void ShowFunction(FunctionRow? row)
    {
        if (FunctionEditor.Visibility == Visibility.Visible) return;  // 편집하는 동안은 편집 칸
        FunctionDetail.Visibility = row is null ? Visibility.Collapsed : Visibility.Visible;
        if (row is null) return;
        CustomActions.Visibility = row.Custom is null ? Visibility.Collapsed : Visibility.Visible;
        FunctionTitleText.Text = row.Title;
        FunctionRouteText.Text = row.Route;
        FunctionDescriptionText.Text = row.Description;
        FunctionDescriptionText.Visibility = row.Description.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        FunctionStateText.Text = !row.Switchable ? "관리 창이 쓰는 API 라 늘 켜져 있습니다." : row.On ? (row.Custom is null ? "켜짐 · 기본 API" : "켜짐 · 사용자 API")
            : "꺼짐: 이 API 를 부르는 앱은 503 오류를 받습니다.";
        FunctionStateText.Foreground = row.Switchable && !row.On ? (Brush)FindResource("WarnBrush") : (Brush)FindResource("MutedTextBrush");
        FunctionTodayText.Text = row.TodayCalls.ToString("N0", CultureInfo.InvariantCulture);
        FunctionPeriodText.Text = row.PeriodCalls.ToString("N0", CultureInfo.InvariantCulture);
        FunctionAverageText.Text = row.PeriodCalls > 0 ? $"{row.AverageMs:0.#} ms" : "—";
        long peak = Math.Max(1, row.Days.Count == 0 ? 1 : row.Days.Max(day => day.Calls));
        FunctionBars.ItemsSource = row.Days.Select(day => new DayBar(day.Day, day.Calls, day.Failures, peak)).ToList();
        FunctionBarsText.Text = row.Days.Count > 0 ? $"{row.Days[0].Day[5..]} ~ {row.Days[^1].Day[5..]} 날짜별 호출" : "";
        FunctionLastText.Text = row.LastCall.Length > 0 ? $"마지막 호출 {row.LastCall}" : "서버를 켠 뒤 아직 호출 없음";
        FunctionFailureText.Text = row.LastFailure;
        FunctionFailureText.Visibility = row.LastFailure.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>스위치: 끌 때는 물음 (그 API 를 쓰는 앱이 바로 503 을 받음)</summary>
    private async void FunctionSwitch_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { Tag: FunctionRow row } box) return;
        bool on = box.IsChecked == true;
        box.IsChecked = row.On;  // 서버가 바꾼 뒤에 다시 그림
        if (!on && MessageBox.Show(OwnerWindow, $"{row.Title} ({row.Route}) 을(를) 끌까요?\n\n이 API 를 부르는 앱 · 런처는 다시 켤 때까지 503 오류를 받습니다.",
                "API", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        await Run(async () =>
        {
            var answer = (await Admin.SendAsync(HttpMethod.Put, "admin/api/functions/" + Uri.EscapeDataString(row.Id), new JsonObject { ["on"] = on })).AsObject();
            row.Update(answer);
            if (FunctionsList.SelectedItem == row) ShowFunction(row);
            UpdateFunctionsSummary();
        }, done: on ? $"{row.Title}을(를) 켰습니다." : $"{row.Title}을(를) 껐습니다.");
    }

    // ── 사용자 API 편집 ─────────────────────────────────────────────────────────────────────────────

    private FunctionRow? _editing;
    private bool _editorDirty, _fillingEditor;

    private static readonly Dictionary<string, string> KindHints = new()
    {
        ["text"] = "돌려줄 JSON 또는 글. 주소의 {이름} 은 그 값으로 바뀝니다.",
        ["script"] = "PowerShell: $request.values.이름 (주소) · $request.query.이름 · $request.json (보낸 JSON) · $request.body. 출력한 글이 응답 (JSON 이면 JSON), exit 404 처럼 끝내면 그 상태 코드.",
        ["forward"] = "전달할 주소 (http://…). 주소의 {이름} 은 그 값으로 바뀌고 쿼리 · 본문은 그대로 보냅니다.",
    };

    private string EditorKind => EditorScript.IsChecked == true ? "script" : EditorForward.IsChecked == true ? "forward" : "text";

    private void AddFunctionButton_Click(object sender, RoutedEventArgs e) => OpenEditor(null);

    private void EditFunctionButton_Click(object sender, RoutedEventArgs e)
    {
        if (FunctionsList.SelectedItem is FunctionRow { Custom: not null } row) OpenEditor(row);
    }

    /// <summary>편집 칸 열기: row 가 없으면 새 API</summary>
    private void OpenEditor(FunctionRow? row)
    {
        _editing = row;
        _fillingEditor = true;
        var data = row?.Custom;
        string Text(string key) => data?[key]?.ToString() ?? "";
        EditorTitleText.Text = row is null ? "새 API" : "API 편집";
        EditorNameBox.Text = Text("title");
        EditorDescriptionBox.Text = Text("description");
        string method = data is null ? "GET" : Text("method");
        EditorGet.IsChecked = method == "GET";
        EditorPost.IsChecked = method == "POST";
        EditorPut.IsChecked = method == "PUT";
        EditorDelete.IsChecked = method == "DELETE";
        EditorPathBox.Text = Text("path");
        string access = data is null ? "key" : Text("access");
        EditorOpen.IsChecked = access == "open";
        EditorKey.IsChecked = access == "key";
        EditorAdmin.IsChecked = access == "admin";
        string kind = data is null ? "text" : Text("kind");
        EditorText.IsChecked = kind == "text";
        EditorScript.IsChecked = kind == "script";
        EditorForward.IsChecked = kind == "forward";
        EditorBodyBox.Text = Text("body").Replace("\r\n", "\n").Replace("\n", "\r\n");
        EditorBodyHint.Text = KindHints[kind];
        LimitAccess();
        EditorDeleteButton.Visibility = row is null ? Visibility.Collapsed : Visibility.Visible;
        TryPathBox.Text = SamplePath(Text("path"));
        TryBodyBox.Text = "";
        TryResultText.Text = "";
        TryResultBox.Visibility = Visibility.Collapsed;
        _fillingEditor = false;
        _editorDirty = false;
        EditorSaveButton.IsEnabled = row is null;
        FunctionDetail.Visibility = Visibility.Collapsed;
        FunctionEditor.Visibility = Visibility.Visible;
        EditorNameBox.Focus();
    }

    /// <summary>시험 주소: {이름} 자리를 test 로</summary>
    private static string SamplePath(string path) =>
        System.Text.RegularExpressions.Regex.Replace(path, @"\{[A-Za-z0-9_]+\}", "test");

    private void Editor_Changed(object sender, RoutedEventArgs e)
    {
        if (_fillingEditor || !IsLoaded) return;
        _editorDirty = true;
        EditorSaveButton.IsEnabled = true;
        if (ReferenceEquals(sender, EditorPathBox)) TryPathBox.Text = SamplePath(EditorPathBox.Text.Trim());
    }

    /// <summary>PowerShell 은 이 PC 에서 도는 스크립트라 누구나로 열 수 없음 (서버도 거절): 고르면 앱 키로</summary>
    private void LimitAccess()
    {
        bool script = EditorKind == "script";
        EditorOpen.IsEnabled = !script;
        EditorOpen.ToolTip = script ? "PowerShell API 는 누구나 부를 수 없습니다." : null;
        if (script && EditorOpen.IsChecked == true) EditorKey.IsChecked = true;
    }

    private void EditorKind_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        EditorBodyHint.Text = KindHints[EditorKind];
        LimitAccess();
        if (!_fillingEditor && EditorKind == "script" && EditorBodyBox.Text.Trim().Length == 0)
            EditorBodyBox.Text = "@{ message = \"안녕 \" + $request.values.name } | ConvertTo-Json";
        Editor_Changed(sender, e);
    }

    private void CloseEditor()
    {
        FunctionEditor.Visibility = Visibility.Collapsed;
        _editing = null;
        _editorDirty = false;
        ShowFunction(FunctionsList.SelectedItem as FunctionRow);
    }

    private void CancelEditButton_Click(object sender, RoutedEventArgs e)
    {
        if (_editorDirty && MessageBox.Show(OwnerWindow, "저장하지 않은 변경을 버릴까요?", ManagerHost.Title, MessageBoxButton.YesNo, MessageBoxImage.Question,
                MessageBoxResult.No) != MessageBoxResult.Yes) return;
        CloseEditor();
    }

    /// <summary>[저장]: 새 API 는 더하고 (POST), 있던 API 는 고침 (PUT). 서버가 바로 그 API 로 답함. 저장한 API 의 id (실패하면 null)</summary>
    private async Task<string?> SaveEditorAsync()
    {
        var body = new JsonObject
        {
            ["title"] = EditorNameBox.Text.Trim(), ["description"] = EditorDescriptionBox.Text.Trim(),
            ["method"] = EditorPost.IsChecked == true ? "POST" : EditorPut.IsChecked == true ? "PUT" : EditorDelete.IsChecked == true ? "DELETE" : "GET",
            ["path"] = EditorPathBox.Text.Trim(),
            ["access"] = EditorKey.IsChecked == true ? "key" : EditorAdmin.IsChecked == true ? "admin" : "open",
            ["kind"] = EditorKind, ["body"] = EditorBodyBox.Text.Replace("\r\n", "\n"),
        };
        string? id = null;
        bool saved = await Run(async () =>
        {
            var answer = _editing is null
                ? await Admin.SendAsync(HttpMethod.Post, "admin/api/custom", body)
                : await Admin.SendAsync(HttpMethod.Put, "admin/api/custom/" + Uri.EscapeDataString(_editing.Id), body);
            id = answer["id"]?.ToString();
        }, done: "저장했습니다. 바로 동작합니다.");
        if (!saved || id is null) return null;
        _editorDirty = false;
        EditorSaveButton.IsEnabled = false;
        await LoadFunctionsAsync();
        // 편집 칸은 그대로 (시험을 이어서): 저장한 API 를 고친 것으로
        _editing = _functions.FirstOrDefault(row => row.Id == id);
        EditorTitleText.Text = "API 편집";
        EditorDeleteButton.Visibility = Visibility.Visible;
        FunctionsList.SelectedItem = _editing;
        FunctionsList.ScrollIntoView(_editing);
        return id;
    }

    private async void SaveFunctionButton_Click(object sender, RoutedEventArgs e) => await SaveEditorAsync();

    private async void DeleteFunctionButton_Click(object sender, RoutedEventArgs e)
    {
        var row = _editing ?? FunctionsList.SelectedItem as FunctionRow;
        if (row?.Custom is null) return;
        if (MessageBox.Show(OwnerWindow, $"{row.Title} ({row.Route}) 을(를) 지울까요?\n\n이 API 를 부르는 곳은 바로 404 를 받습니다.", ManagerHost.Title,
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        bool deleted = await Run(async () => await Admin.SendAsync(HttpMethod.Delete, "admin/api/custom/" + Uri.EscapeDataString(row.Id), null),
            done: $"{row.Title}을(를) 지웠습니다.");
        if (!deleted) return;
        FunctionEditor.Visibility = Visibility.Collapsed;
        _editing = null;
        await LoadFunctionsAsync();
    }

    // ── 되돌리기 · 내보내기 · 가져오기 ──────────────────────────────────────────────────────────────

    /// <summary>⋯: '되돌리기' 제목 아래에 서버 기록 (새것부터 15개, 맨 위 = 지금 상태)을 넣고 메뉴를 엶</summary>
    private async void CustomMoreButton_Click(object sender, RoutedEventArgs e)
    {
        foreach (var old in CustomMenu.Items.OfType<MenuItem>().Where(item => Equals(item.Tag, "history")).ToList()) CustomMenu.Items.Remove(old);
        int at = CustomMenu.Items.IndexOf(CustomHistoryItem) + 1, added = 0;
        await Run(async () =>
        {
            var history = (await Admin.GetAsync("admin/api/custom/history"))["history"] as JsonArray ?? [];
            foreach (var entry in history.OfType<JsonObject>().Take(15))
            {
                string name = entry["name"]?.ToString() ?? "", when = LocalTime(entry["at"]);
                int count = (int?)entry["count"] ?? 0;
                var item = new MenuItem { Tag = "history", Header = $"{when} · {entry["change"]} · {count}개" + (added == 0 ? " (지금)" : ""), IsEnabled = added > 0 };
                item.Click += async (_, _) => await RestoreCustomAsync(name, when, count);
                CustomMenu.Items.Insert(at + added++, item);
            }
        }, quiet: true);
        CustomHistoryItem.Header = added > 1 ? "되돌리기" : "되돌릴 기록 없음";
        OpenBelow(CustomMenu, CustomMoreButton);
    }

    private async Task RestoreCustomAsync(string name, string when, int count)
    {
        if (FunctionEditor.Visibility == Visibility.Visible && _editorDirty)
        {
            SetMessage("편집 중인 API 를 저장하거나 취소한 뒤 되돌리세요.", warning: true);
            return;
        }
        if (MessageBox.Show(OwnerWindow, $"사용자 API 를 {when} 상태 ({count}개)로 되돌릴까요?\n\n지금 상태도 기록에 남아 다시 돌릴 수 있습니다.", ManagerHost.Title,
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        await Run(async () =>
        {
            var answer = await Admin.SendAsync(HttpMethod.Post, $"admin/api/custom/history/{Uri.EscapeDataString(name)}/restore", new JsonObject());
            if (FunctionEditor.Visibility == Visibility.Visible) CloseEditor();
            await LoadFunctionsAsync();
            ShowSkipped($"{when} 상태로 되돌렸습니다.", answer["skipped"] as JsonArray);
        });
    }

    /// <summary>결과 한 줄: 건너뛴 API 가 있으면 그 이름과 까닭을 함께 (경고로 남음)</summary>
    private void ShowSkipped(string done, JsonArray? skipped)
    {
        var lines = (skipped ?? []).OfType<JsonObject>().Select(item => $"{item["title"]} ({item["reason"]})").ToList();
        SetMessage(lines.Count == 0 ? done : $"{done} 건너뜀 {lines.Count}개: {string.Join(" · ", lines)}", warning: lines.Count > 0);
    }

    /// <summary>[내보내기…]: 사용자 API 전체를 JSON 파일로 (다른 서버의 [가져오기…]가 읽음)</summary>
    private async void ExportCustomButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "사용자 API 내보내기", Filter = "JSON (*.json)|*.json", DefaultExt = ".json",
            FileName = $"i-api-사용자API-{DateTime.Now:yyyyMMdd}.json",
        };
        if (dialog.ShowDialog(OwnerWindow) != true) return;
        await Run(async () =>
        {
            var exported = await Admin.GetAsync("admin/api/custom/export");
            int count = (exported["apis"] as JsonArray)?.Count ?? 0;
            await File.WriteAllTextAsync(dialog.FileName, exported.ToJsonString(new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }), new System.Text.UTF8Encoding(false));
            SetMessage($"사용자 API {count}개를 내보냈습니다: {dialog.FileName}");
        });
    }

    /// <summary>[가져오기…]: 내보낸 파일의 사용자 API 를 이 서버에 (같은 id 나 같은 방법 · 주소면 파일 내용으로 바뀜, [되돌리기]로 돌릴 수 있음)</summary>
    private async void ImportCustomButton_Click(object sender, RoutedEventArgs e)
    {
        if (FunctionEditor.Visibility == Visibility.Visible && _editorDirty)
        {
            SetMessage("편집 중인 API 를 저장하거나 취소한 뒤 가져오세요.", warning: true);
            return;
        }
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "사용자 API 가져오기", Filter = "JSON (*.json)|*.json|모든 파일 (*.*)|*.*" };
        if (dialog.ShowDialog(OwnerWindow) != true) return;
        JsonObject file;
        try
        {
            file = JsonNode.Parse(await File.ReadAllTextAsync(dialog.FileName)) as JsonObject ?? throw new FormatException();
            if (file["apis"] is not JsonArray) throw new FormatException();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or FormatException or System.Text.Json.JsonException)
        {
            SetMessage("i-api 에서 내보낸 사용자 API 파일이 아닙니다: " + Path.GetFileName(dialog.FileName), warning: true);
            return;
        }
        int count = ((JsonArray)file["apis"]!).Count;
        if (MessageBox.Show(OwnerWindow, $"파일의 사용자 API {count}개를 이 서버에 넣을까요?\n\n같은 API (같은 id, 또는 같은 방법 · 주소)는 파일 내용으로 바뀝니다. ⋯ → 되돌리기로 돌릴 수 있습니다.",
                ManagerHost.Title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        await Run(async () =>
        {
            var answer = await Admin.SendAsync(HttpMethod.Post, "admin/api/custom/import", new JsonObject { ["apis"] = file["apis"]!.DeepClone() });
            if (FunctionEditor.Visibility == Visibility.Visible) CloseEditor();
            await LoadFunctionsAsync();
            ShowSkipped($"가져왔습니다: 새로 {(int?)answer["added"] ?? 0}개 · 바뀜 {(int?)answer["changed"] ?? 0}개.", answer["skipped"] as JsonArray);
        });
    }

    /// <summary>[부르기]: 저장하지 않은 변경이 있으면 먼저 저장하고, 시험 주소로 그 방법 그대로 부름 (관리자 비밀번호 · 앱 키를 함께)</summary>
    private async void TryFunctionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_editorDirty || _editing is null)
        {
            if (await SaveEditorAsync() is null) return;
        }
        string method = EditorPost.IsChecked == true ? "POST" : EditorPut.IsChecked == true ? "PUT" : EditorDelete.IsChecked == true ? "DELETE" : "GET";
        string path = TryPathBox.Text.Trim();
        if (!path.StartsWith('/')) path = "/" + path;
        TryResultText.Text = $"{method} {path} 부르는 중…";
        try
        {
            var (status, text, milliseconds) = await Admin.TryAsync(method, path, method is "GET" ? "" : TryBodyBox.Text, ServerManager.Setting("RELAY_INGEST_KEY"));
            TryResultText.Text = $"{method} {path} → {status} · {milliseconds} ms";
            TryResultText.Foreground = status < 400 ? (Brush)FindResource("GoodBrush") : (Brush)FindResource("WarnBrush");
            TryResultBox.Text = text;
            TryResultBox.Visibility = Visibility.Visible;
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            TryResultText.Text = "부르지 못했습니다: " + error.Message;
            TryResultText.Foreground = (Brush)FindResource("WarnBrush");
        }
        await LoadFunctionsAsync();
    }
}

/// <summary>API 한 줄</summary>
internal sealed class FunctionRow : INotifyPropertyChanged
{
    private static readonly Brush Quiet = new SolidColorBrush(Color.FromRgb(0xA1, 0xA1, 0xA6)), Warning = new SolidColorBrush(Color.FromRgb(0xE6, 0xC0, 0x7A));

    public string Id { get; }
    public string Group { get; }
    public string Title { get; private set; } = "";
    public string Route { get; private set; } = "";
    public string Description { get; private set; } = "";
    public bool Switchable { get; private set; }
    public bool On { get; private set; }
    public long TodayCalls { get; private set; }
    public long TodayFailures { get; private set; }
    public long PeriodCalls { get; private set; }
    public long PeriodFailures { get; private set; }
    public double AverageMs { get; private set; }
    public string LastCall { get; private set; } = "";
    public string LastFailure { get; private set; } = "";
    public List<(string Day, long Calls, long Failures)> Days { get; private set; } = [];

    public string TodayText => TodayCalls.ToString("N0", CultureInfo.InvariantCulture);
    public string PeriodText => PeriodCalls.ToString("N0", CultureInfo.InvariantCulture);
    public string FailuresText => PeriodFailures > 0 ? PeriodFailures.ToString("N0", CultureInfo.InvariantCulture) : "—";
    public Brush FailuresBrush => PeriodFailures > 0 ? Warning : Quiet;
    /// <summary>끈 API 는 흐리게</summary>
    public double RowOpacity => Switchable && !On ? 0.45 : 1;

    public event PropertyChangedEventHandler? PropertyChanged;

    private readonly List<string> _span;

    public FunctionRow(JsonObject data, List<string> span)
    {
        Id = Text(data["id"]);
        Group = Text(data["group"]);
        _span = span;
        Update(data);
    }

    /// <summary>사용자 API 면 그 정의 (편집 칸이 씀), 기본 API 면 null</summary>
    public JsonObject? Custom { get; private set; }

    public void Update(JsonObject data)
    {
        Custom = data["custom"] is JsonObject custom ? (JsonObject)custom.DeepClone() : null;
        Title = Text(data["title"]);
        Route = $"{Text(data["method"])} {Text(data["path"])}";
        Description = Text(data["description"]);
        Switchable = (bool?)data["switchable"] ?? false;
        On = (bool?)data["on"] ?? true;
        TodayCalls = (long?)data["today"]?["calls"] ?? 0;
        TodayFailures = (long?)data["today"]?["failures"] ?? 0;
        PeriodCalls = (long?)data["period"]?["calls"] ?? 0;
        PeriodFailures = (long?)data["period"]?["failures"] ?? 0;
        AverageMs = (double?)data["period"]?["averageMs"] ?? 0;
        LastCall = Local(data["lastCall"]);
        LastFailure = data["lastFailure"] is JsonObject failure
            ? $"마지막 실패 {Local(failure["at"])} · {(int?)failure["status"] ?? 0} · {Text(failure["message"])}".TrimEnd(' ', '·')
            : "";
        var recorded = (data["perDay"] as JsonArray ?? []).OfType<JsonObject>()
            .ToDictionary(day => Text(day["day"]), day => ((long?)day["calls"] ?? 0, (long?)day["failures"] ?? 0));
        Days = _span.Count > 0
            ? _span.Select(day => recorded.TryGetValue(day, out var counts) ? (day, counts.Item1, counts.Item2) : (day, 0L, 0L)).ToList()
            : recorded.Select(pair => (pair.Key, pair.Value.Item1, pair.Value.Item2)).ToList();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }

    private static string Text(JsonNode? node) => node is JsonValue value && value.TryGetValue(out string? text) ? text : node?.ToString() ?? "";

    private static string Local(JsonNode? node) =>
        DateTimeOffset.TryParse(Text(node), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var moment)
            ? moment.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) : "";

    public override string ToString() => Title;
}

/// <summary>날짜 막대 하나 (높이는 가장 많은 날 기준, 64)</summary>
internal sealed record DayBar(string Day, long Calls, long Failures, long Peak)
{
    public double CallsHeight => Calls == 0 ? 0 : Math.Max(2, 64.0 * Calls / Peak);
    public double FailuresHeight => Failures == 0 ? 0 : Math.Max(2, 64.0 * Failures / Peak);
    public string Tip => $"{Day} · 호출 {Calls:N0}" + (Failures > 0 ? $" · 실패 {Failures:N0}" : "");
}
