using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using ILauncher.Services;

namespace ILauncher;

/// <summary>즐겨찾기: 이 PC 에 설치된 일반 프로그램을 카드로 두고 누르면 실행. 추가(설치된 프로그램 목록·찾아보기·끌어 놓기), 편집, 삭제</summary>
public partial class MainWindow
{
    private void RenderFavorites()
    {
        string search = SearchBox.Text.Trim();
        var visible = _favorites.Select(program => new FavoriteCard(program)).Where(card => card.Matches(search)).ToList();
        FavoriteItems.ItemsSource = visible;
        SectionTitle.Text = "즐겨찾기";
        CountText.Text = $"{visible.Count}개 프로그램";
        RenderCategories();
        UpdateCounts();
        UpdateAllButton.Visibility = Visibility.Collapsed;

        bool empty = visible.Count == 0;
        EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        CardScrollViewer.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        if (!empty) return;
        if (_favorites.Count == 0)
        {
            EmptyStateTitle.Text = "즐겨찾는 프로그램이 없습니다";
            EmptyStateText.Text = "이 PC 에 설치된 프로그램을 추가하면 여기서 바로 실행합니다.\n실행 파일이나 바로 가기를 이 창에 끌어 놓아도 됩니다.";
            EmptyStateButton.Content = "프로그램 추가";
            EmptyStateButton.Tag = "favorite-add";
        }
        else
        {
            EmptyStateTitle.Text = "일치하는 프로그램이 없습니다";
            EmptyStateText.Text = "";
            EmptyStateButton.Content = "검색 지우기";
            EmptyStateButton.Tag = "clear";
        }
    }

    /// <summary>추가(null) 또는 편집</summary>
    private void EditFavorite(FavoriteProgram? editing)
    {
        var editor = new FavoriteEditorWindow(editing) { Owner = this };
        if (editor.ShowDialog() != true || editor.Result is not { } result) return;
        int index = editing is null ? -1 : _favorites.FindIndex(item => item.Id == editing.Id);
        if (index >= 0) _favorites[index] = result; else _favorites.Add(result);
        SaveFavorites(index >= 0 ? $"{result.Name} 수정함" : $"{result.Name} 추가함");
    }

    private void SaveFavorites(string done)
    {
        try
        {
            _favoriteStore.Save(_favorites);
            StatusText.Text = done;
            HideError();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            ShowError("즐겨찾기를 저장하지 못했습니다: " + error.Message);
        }
        if (_filter != "favorites") _filter = "favorites";
        Render();
    }

    private void FavoriteCard_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is FavoriteCard card) RunFavorite(card);
    }

    private void FavoriteRunMenu_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is FavoriteCard card) RunFavorite(card);
    }

    private void FavoriteEditMenu_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is FavoriteCard card) EditFavorite(card.Program);
    }

    private void FavoriteFolderMenu_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not FavoriteCard card) return;
        string path = card.Program.Path;
        try
        {
            if (File.Exists(path)) Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            else if (Directory.Exists(path)) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            else if (Path.GetDirectoryName(path) is { } folder && Directory.Exists(folder)) Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
            else ShowError("폴더가 없습니다: " + path);
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException) { ShowError("폴더를 열지 못했습니다: " + error.Message); }
    }

    private void FavoriteDeleteMenu_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not FavoriteCard card) return;
        if (MessageBox.Show(this, $"{card.Name}을(를) 즐겨찾기에서 뺄까요?\n프로그램은 지우지 않습니다.", "즐겨찾기",
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        _favorites.RemoveAll(item => item.Id == card.Program.Id);
        SaveFavorites($"{card.Name} 뺌");
    }

    private void RunFavorite(FavoriteCard card)
    {
        var program = card.Program;
        if (!card.Exists)
        {
            ShowError($"{card.Name}: 파일이 없습니다. 우클릭 → 편집에서 경로를 고치세요. ({program.Path})");
            return;
        }
        try
        {
            var start = new ProcessStartInfo(program.Path) { UseShellExecute = true, Arguments = program.Arguments };
            if (File.Exists(program.Path) && Path.GetDirectoryName(program.Path) is { } folder) start.WorkingDirectory = folder;
            Process.Start(start);
            StatusText.Text = $"{card.Name} 실행";
            HideError();
        }
        catch (Win32Exception error) when (error.NativeErrorCode == 1223)
        {
            StatusText.Text = $"{card.Name} 실행을 취소했습니다.";  // UAC 취소
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException)
        {
            ShowError($"{card.Name}을(를) 실행하지 못했습니다: {error.Message}");
        }
    }

    // ── 끌어 놓기: 실행 파일·바로 가기·파일을 창에 놓으면 즐겨찾기에 추가 ──

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Link : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0) return;
        var added = new List<string>();
        foreach (string path in paths.Where(path => File.Exists(path) || Directory.Exists(path)))
        {
            if (_favorites.Any(item => string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase))) continue;
            string name = Directory.Exists(path) ? Path.GetFileName(path.TrimEnd('\\')) : Path.GetFileNameWithoutExtension(path);
            _favorites.Add(new FavoriteProgram { Name = name, Path = path });
            added.Add(name);
        }
        _filter = "favorites";
        if (added.Count > 0) SaveFavorites(added.Count == 1 ? $"{added[0]} 추가함" : $"{added.Count}개 추가함");
        else Render();
    }
}
