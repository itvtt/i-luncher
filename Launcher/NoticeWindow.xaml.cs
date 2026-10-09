using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ILauncher.Models;

namespace ILauncher;

public partial class NoticeWindow : Window
{
    private readonly Action<Notice> _markRead;
    private readonly Func<Task> _refresh;
    private bool _updating;

    public NoticeWindow(IReadOnlyList<Notice> notices, bool refreshFailed,
        Action<Notice> markRead, Func<Task> refresh)
    {
        _markRead = markRead;
        _refresh = refresh;
        InitializeComponent();
        Width = Math.Min(Width, SystemParameters.WorkArea.Width);
        Height = Math.Min(Height, SystemParameters.WorkArea.Height);
        SetNotices(notices, refreshFailed);
    }

    public void SetNotices(IReadOnlyList<Notice> notices, bool refreshFailed)
    {
        string? selectedId = (NoticeList.SelectedItem as NoticeListItem)?.Notice.Id;
        _updating = true;
        NoticeList.ItemsSource = notices.Select(notice => new NoticeListItem(notice)).ToArray();
        NoticeList.SelectedItem = NoticeList.Items.Cast<NoticeListItem>()
            .FirstOrDefault(item => item.Notice.Id == selectedId) ?? NoticeList.Items.Cast<NoticeListItem>().FirstOrDefault();
        _updating = false;
        StatusText.Text = refreshFailed
            ? notices.Count == 0 ? "공지를 불러오지 못했습니다. 잠시 후 다시 시도해 주세요." : "최근에 저장된 공지입니다."
            : notices.Count == 0 ? "아직 공지가 없습니다." : string.Empty;
        StatusText.Visibility = StatusText.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        ShowSelectedNotice();
    }

    public void SelectNotice(string id)
    {
        NoticeListItem? item = NoticeList.Items.Cast<NoticeListItem>().FirstOrDefault(item => item.Notice.Id == id);
        if (item is null) return;
        NoticeList.SelectedItem = item;
        NoticeList.ScrollIntoView(item);
    }

    private void ShowSelectedNotice()
    {
        if (_updating) return;
        var item = NoticeList.SelectedItem as NoticeListItem;
        NoticeTitle.Text = item?.Title ?? string.Empty;
        NoticeDate.Text = item?.DateText ?? string.Empty;
        NoticeBody.Text = item?.Notice.Body ?? string.Empty;
        NoticeBody.ScrollToHome();
        if (IsLoaded && item is not null) _markRead(item.Notice);
    }

    private void Window_Loaded(object sender, RoutedEventArgs e) => ShowSelectedNotice();
    private void NoticeList_SelectionChanged(object sender, SelectionChangedEventArgs e) => ShowSelectedNotice();

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        RefreshNoticesButton.IsEnabled = false;
        try { await _refresh(); }
        finally { RefreshNoticesButton.IsEnabled = true; }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        Close();
    }

    private sealed record NoticeListItem(Notice Notice)
    {
        public string Title => Notice.Title;
        public string DateText => Notice.PublishedAt.ToLocalTime().ToString("yyyy.MM.dd");
    }
}
