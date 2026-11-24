using System.ComponentModel;
using System.Globalization;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace ApiManager;

/// <summary>
/// API 화면: 서버의 API 하나하나 (서버 3.0 의 /admin/api/functions). 켜고 끄기 (끈 API 는 503 으로 거절), 오늘 · 30일 호출, 실패, 평균 시간,
/// 고른 API 의 설명 · 날짜별 막대 · 마지막 실패. 관리자 메뉴가 쓰는 API 와 상태 확인은 끄지 못함
/// </summary>
public partial class ManagerView
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
            FunctionsSummaryText.Text = "이 서버는 API 관리를 모릅니다 (예전 Python 서버). 서버를 C# 판(3.0 이상)으로 바꾸면 보입니다: 서버 PC 에서 새 런처를 켜면 저절로 바뀝니다.";
            return;
        }
        string? selected = (FunctionsList.SelectedItem as FunctionRow)?.Id;
        _functions = (listing["functions"] as JsonArray ?? []).OfType<JsonObject>().Select(item => new FunctionRow(item)).ToList();
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
            $" · 오늘 호출 {today:N0}" + (failures > 0 ? $" · 실패 {failures:N0}" : "") + " · 끈 API 는 앱에 503 으로 거절합니다.";
    }

    private void FunctionsList_SelectionChanged(object sender, SelectionChangedEventArgs e) => ShowFunction(FunctionsList.SelectedItem as FunctionRow);

    private void ShowFunction(FunctionRow? row)
    {
        FunctionDetail.Visibility = row is null ? Visibility.Collapsed : Visibility.Visible;
        if (row is null) return;
        FunctionTitleText.Text = row.Title;
        FunctionRouteText.Text = row.Route;
        FunctionDescriptionText.Text = row.Description;
        FunctionDescriptionText.Visibility = row.Description.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        FunctionStateText.Text = !row.Switchable ? "관리자 메뉴가 쓰는 API 라 늘 켜져 있습니다." : row.On ? "켜짐" : "꺼짐: 이 API 를 부르는 앱은 503 오류를 받습니다.";
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

    public FunctionRow(JsonObject data)
    {
        Id = Text(data["id"]);
        Group = Text(data["group"]);
        Update(data);
    }

    public void Update(JsonObject data)
    {
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
        Days = (data["perDay"] as JsonArray ?? []).OfType<JsonObject>()
            .Select(day => (Text(day["day"]), (long?)day["calls"] ?? 0, (long?)day["failures"] ?? 0)).ToList();
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
