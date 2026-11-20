using System.Windows;

namespace ILauncher;

/// <summary>
/// 앱 칸 · 스토어 줄 · 독의 단추. 앱 페이지(상세 페이지)는 없앰: 아이콘을 눌러도 바로 열기 · 받기 · 업데이트.
/// </summary>
public partial class MainWindow
{
    /// <summary>이름 아래 · 줄 오른쪽 상태 단추: 받기(설치 확인) · 업데이트 · 열기 · 실행 중(앞으로)</summary>
    private async void CellAction_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if ((sender as FrameworkElement)?.Tag is not AppCard card || card.IsBusy) return;
        if (card.UpdateAvailable) await UpdateCardAsync(card);
        else await OpenAsync(card);
    }

    /// <summary>설치됨 탭의 아이콘: 바로 실행 (받지 않았으면 받기, 새 버전이 있으면 업데이트 후 실행)</summary>
    private async void Cell_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if ((sender as FrameworkElement)?.Tag is AppCard { IsBusy: false } card) await OpenAsync(card);
    }

    /// <summary>독의 앱: 바로 실행, 설치, 업데이트 후 실행</summary>
    private async void StoreAction_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if ((sender as FrameworkElement)?.Tag is AppCard card) await OpenAsync(card);
    }
}
