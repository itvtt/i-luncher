using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ILauncher.Models;
using ILauncher.Services;

namespace ILauncher;

/// <summary>
/// 앱 페이지 (앱스토어처럼, 스토어에서만): 스토어 줄을 누르면 열림. 아이콘·이름·버튼 하나, 버전·분류·크기,
/// 스크린샷(기본 목록은 런처에 든 그림, 그 밖은 API 서버에 올린 그림), 상세 설명, 새로운 기능(최신 릴리스 노트).
/// 설치됨 탭의 아이콘은 페이지 없이 바로 실행.
/// </summary>
public partial class MainWindow
{
    private AppCard? _detail;
    private ScreenshotService? _screenshots;
    private CancellationTokenSource? _detailLoad;

    private ScreenshotService Screenshots =>
        _screenshots ??= new ScreenshotService(Path.Combine(_settings.DataDirectory, "screenshots"), () => _settings.ApiServerUrl);

    private bool OnDetail => _detail is not null && !OnPage;

    /// <summary>스토어 줄 (오른쪽 단추 말고): 앱 페이지</summary>
    private void StoreRow_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not AppCard card) return;
        e.Handled = true;
        ShowDetail(card);
    }

    internal void ShowDetail(AppCard card)
    {
        if (_detail is not null) _detail.PropertyChanged -= DetailCard_PropertyChanged;
        _detail = card;
        card.PropertyChanged += DetailCard_PropertyChanged;
        DetailPane.DataContext = card;
        ShotViewer.Visibility = Visibility.Collapsed;
        FillDetail(card);
        DetailScrollViewer.ScrollToTop();
        DetailShotsScroller.ScrollToLeftEnd();
        ShowPage();
        _ = LoadScreenshotsAsync(card);
    }

    private void CloseDetail()
    {
        if (_detail is null) return;
        _detail.PropertyChanged -= DetailCard_PropertyChanged;
        _detail = null;
        _detailLoad?.Cancel();
        ShotViewer.Visibility = Visibility.Collapsed;
        DetailShots.ItemsSource = null;
        ShowPage();
    }

    private void DetailBack_Click(object sender, RoutedEventArgs e) => CloseDetail();

    // 릴리스 확인·설치가 끝나면 버전·노트를 다시 채움 (진행 글자·막대는 바인딩으로 바뀜)
    private void DetailCard_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is AppCard card && ReferenceEquals(card, _detail) && e.PropertyName is "" or null)
            Dispatcher.BeginInvoke(() => { if (ReferenceEquals(card, _detail)) FillDetail(card); });
    }

    private void FillDetail(AppCard card)
    {
        var release = card.Latest;
        var facts = new List<DetailFact>();
        string version = release?.Version is { } latest ? "v" + latest : release?.Tag ?? (card.Installed?.Version is { } own ? "v" + own : "—");
        facts.Add(new DetailFact("버전", version,
            card.Installed is { } installed ? (card.UpdateAvailable ? $"설치됨 v{installed.Version}" : "설치됨") : "설치 안 됨"));
        facts.Add(new DetailFact("분류", card.Category, ""));
        if (release is not null && AppManager.ResolveKind(card.App, release) is var kind &&
            (kind == InstallKinds.Zip ? AppManager.SelectZipAsset(card.App, release) : AppManager.SelectSetupAsset(card.App, release)) is { Size: > 0 } asset)
            facts.Add(new DetailFact("크기", $"{asset.Size / 1048576.0:0.#} MB", kind == InstallKinds.Zip ? "zip" : "설치 프로그램"));
        DetailFacts.ItemsSource = facts;

        string details = card.App.Details?.Trim() ?? "";
        DetailDescription.Text = details;
        DetailDescription.Visibility = details.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        string notes = release is null ? "" : ReleaseNotes(release.Body);
        DetailNewsSection.Visibility = release is null ? Visibility.Collapsed : Visibility.Visible;
        DetailNewsVersion.Text = release is null ? "" : version;
        DetailNewsWhen.Text = release?.PublishedAt is { } at ? When(at.ToLocalTime()) : "";
        DetailNews.Text = notes;
        DetailNews.Visibility = notes.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>오늘 · 어제 · n일 전 · 날짜</summary>
    private static string When(DateTimeOffset moment)
    {
        int days = (DateTime.Today - moment.Date).Days;
        return days <= 0 ? "오늘" : days == 1 ? "어제" : days < 7 ? $"{days}일 전" : moment.ToString("M월 d일", CultureInfo.InvariantCulture);
    }

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex BlankLines();

    /// <summary>릴리스 노트(마크다운)를 읽기 쉬운 글로: 제목 기호·굵게·코드 표시를 빼고 목록은 •</summary>
    internal static string ReleaseNotes(string? body)
    {
        var lines = (body ?? "").Replace("\r\n", "\n").Split('\n').Select(line =>
        {
            string text = line.Trim();
            if (text.StartsWith('#')) text = text.TrimStart('#').Trim();
            text = text.Replace("**", "").Replace("__", "").Replace("`", "");
            if (text.StartsWith("- ", StringComparison.Ordinal) || text.StartsWith("* ", StringComparison.Ordinal)) text = "• " + text[2..];
            return text;
        });
        return BlankLines().Replace(string.Join("\n", lines), "\n\n").Trim();
    }

    private async Task LoadScreenshotsAsync(AppCard card)
    {
        _detailLoad?.Cancel();
        var cancel = _detailLoad = new CancellationTokenSource();
        var shots = card.App.Screenshots?.Where(shot => !string.IsNullOrWhiteSpace(shot)).Select(shot => shot.Trim()).ToList() ?? [];
        var items = new ObservableCollection<ScreenshotItem>();
        DetailShots.ItemsSource = items;
        DetailShotsSection.Visibility = shots.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (string shot in shots)
        {
            var image = await Screenshots.LoadAsync(shot, 640, cancel.Token);
            if (cancel.IsCancellationRequested || !ReferenceEquals(card, _detail)) return;
            if (image is not null) items.Add(new ScreenshotItem(shot, image, 300));
        }
        if (items.Count == 0) DetailShotsSection.Visibility = Visibility.Collapsed;
    }

    private async void Shot_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string shot) return;
        ShotViewerImage.Source = (sender as FrameworkElement)?.DataContext is ScreenshotItem item ? item.Image : null;
        ShotViewer.Visibility = Visibility.Visible;
        // 원래 크기로 다시 읽어 선명하게
        if (await Screenshots.LoadAsync(shot, 0, CancellationToken.None) is { } full && ShotViewer.Visibility == Visibility.Visible) ShotViewerImage.Source = full;
    }

    private void ShotViewer_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => ShotViewer.Visibility = Visibility.Collapsed;

    /// <summary>스크린샷 줄 위의 휠: 그냥 돌리면 페이지가 위아래로, Shift 와 함께면 옆으로</summary>
    private void DetailShots_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) DetailShotsScroller.ScrollToHorizontalOffset(DetailShotsScroller.HorizontalOffset - e.Delta);
        else DetailScrollViewer.ScrollToVerticalOffset(DetailScrollViewer.VerticalOffset - e.Delta);
    }

    /// <summary>Esc: 크게 본 스크린샷 → 앱 페이지 순으로 닫음. 마우스 뒤로 단추도 앱 페이지를 닫음</summary>
    private bool HandleDetailKey(Key key)
    {
        if (key != Key.Escape || !OnDetail) return false;
        if (ShotViewer.Visibility == Visibility.Visible) ShotViewer.Visibility = Visibility.Collapsed;
        else CloseDetail();
        return true;
    }

    private void Window_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.XButton1 || !OnDetail) return;
        e.Handled = true;
        CloseDetail();
    }
}

/// <summary>앱 페이지 정보 줄의 한 칸</summary>
public sealed record DetailFact(string Label, string Value, string Note);
