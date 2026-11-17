using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;

namespace ILauncher;

/// <summary>
/// 트레이 창 (트레이 아이콘을 한 번 누르면): 작업 표시줄 바로 위에 뜨는 작은 창. 자주 쓰는 앱 8칸(즐겨찾기 → 많이 연 순),
/// 검색, 업데이트 줄, 큰 창 · 설정. 칸을 누르면 바로 실행하고 닫힘. 바깥을 누르거나 Esc 면 닫힘 (숨김, 다음에 다시 씀).
/// 앱이 아무리 많아도 8칸 그대로, 나머지는 검색이나 전체 보기(큰 창)로
/// </summary>
public partial class TrayPopup : Window
{
    private const int Cells = 8, UpdateRows = 3;
    private const double Gap = 12;
    private readonly MainWindow _main;
    private DateTime _hiddenAt = DateTime.MinValue;
    private bool _closing;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);

    public TrayPopup(MainWindow main)
    {
        _main = main;
        InitializeComponent();
        IsVisibleChanged += (_, _) => { if (!IsVisible) _hiddenAt = DateTime.UtcNow; };
    }

    /// <summary>방금 닫혔음: 트레이 아이콘을 눌러 바깥을 누른 것이면 같은 누름으로 다시 열지 않게</summary>
    public bool JustClosed => DateTime.UtcNow - _hiddenAt < TimeSpan.FromMilliseconds(400);

    /// <summary>작업 표시줄 쪽 화면 끝(보통 오른쪽 아래)에 띄우고 검색 칸에 글자 커서</summary>
    public void ShowNearTray()
    {
        SearchBox.Text = "";
        Refresh();
        var area = SystemParameters.WorkArea;
        Left = area.Left > 0 ? area.Left + Gap : area.Right - Width - Gap;  // 작업 표시줄이 왼쪽에 있으면 왼쪽
        // 처음 띄우기 전에는 창 크기를 모르므로 내용을 재어 둠 (창은 테두리가 없어 내용 높이 = 창 높이)
        Frame.Measure(new Size(Width, double.PositiveInfinity));
        PlaceVertically(Frame.DesiredSize.Height);
        Show();
        Activate();
        var handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero) SetForegroundWindow(handle);
        SearchBox.Focus();
    }

    private void PlaceVertically(double height)
    {
        if (height <= 0) return;
        var area = SystemParameters.WorkArea;
        Top = area.Top > 0 ? area.Top + Gap : area.Bottom - height - Gap;  // 작업 표시줄이 위에 있으면 위
    }

    // 검색 · 업데이트 줄로 높이가 바뀌어도 작업 표시줄에 붙어 있게
    private void Window_SizeChanged(object sender, SizeChangedEventArgs e) => PlaceVertically(ActualHeight);

    /// <summary>칸과 업데이트 줄을 다시 채움</summary>
    public void Refresh()
    {
        string search = SearchBox.Text.Trim();
        var items = _main.QuickItems(search, Cells);
        Items.ItemsSource = items;
        NoResults.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        List<AppCard> updates = search.Length == 0 ? [.. _main.QuickUpdates().Take(UpdateRows)] : [];
        Updates.ItemsSource = updates;
        UpdatesPanel.Visibility = updates.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => Refresh();

    private async void Item_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is { } item) await OpenAsync(item);
    }

    private async Task OpenAsync(object item)
    {
        Hide();
        await _main.QuickOpenAsync(item);
    }

    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not AppCard card) return;
        Hide();
        await _main.QuickUpdateAsync(card);
    }

    private void Full_Click(object sender, RoutedEventArgs e) => _main.ShowFromTray();

    private void Settings_Click(object sender, RoutedEventArgs e) => _main.ShowSettingsFromTray();

    // Esc: 닫기, 검색 칸에서 Enter: 첫 칸 열기
    private async void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Hide();
        }
        else if (e.Key == Key.Enter && SearchBox.IsKeyboardFocused && Items.Items.Count > 0 && Items.Items[0] is { } first)
        {
            e.Handled = true;
            await OpenAsync(first);
        }
    }

    // 바깥을 누르면 닫힘
    private void Window_Deactivated(object? sender, EventArgs e)
    {
        if (IsVisible) Hide();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // Windows 11: 둥근 모서리(DWMWCP_ROUND)와 테두리 #3A3B41(COLORREF 0x00BBGGRR)을 DWM 이 그림. 글자는 그대로 ClearType.
        // 안 되는 Windows 에서는 WPF 테두리 그대로
        var handle = new WindowInteropHelper(this).Handle;
        int round = 2, border = 0x00413B3A;
        if (DwmSetWindowAttribute(handle, 33, ref round, sizeof(int)) == 0 && DwmSetWindowAttribute(handle, 34, ref border, sizeof(int)) == 0)
            Frame.BorderThickness = new Thickness(0);
    }

    /// <summary>Alt+F4 등으로 닫아도 숨기기만 (런처가 끝날 때만 정말 닫음)</summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_closing)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnClosing(e);
    }

    public void CloseForGood()
    {
        _closing = true;
        Close();
    }
}
