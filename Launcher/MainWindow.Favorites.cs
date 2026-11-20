using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using ILauncher.Services;

namespace ILauncher;

/// <summary>
/// 즐겨찾기: 이 PC 의 일반 프로그램(누르면 실행)과 웹사이트(누르면 브라우저). 추가(설치된 프로그램 목록·찾아보기·끌어 놓기, 웹사이트 주소), 편집, 삭제.
/// 웹사이트 아이콘은 그 사이트에서 받아 두고, 없으면 시작할 때 한 번 더 받아 봄
/// </summary>
public partial class MainWindow
{
    /// <summary>앱 카드 우클릭: 즐겨찾기에 추가 / 빼기</summary>
    private void FavoriteToggleMenu_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not AppCard card) return;
        string id = card.App.Id;
        bool removed = _favorites.RemoveAll(item => string.Equals(item.AppId, id, StringComparison.OrdinalIgnoreCase)) > 0;
        if (!removed) _favorites.Add(new FavoriteProgram { Name = card.Name, AppId = id });
        SaveFavorites(removed ? $"{card.Name} 즐겨찾기에서 뺌" : $"{card.Name} 즐겨찾기에 추가");
    }

    /// <summary>이름을 바꾼 앱(FormerIds): 예전 id 로 된 즐겨찾기를 새 id 로 옮겨 저장</summary>
    private void MigrateFavorites(IReadOnlyList<Models.AppDefinition> apps)
    {
        bool changed = false;
        foreach (var favorite in _favorites.Where(item => item.IsApp))
            if (!apps.Any(app => string.Equals(app.Id, favorite.AppId, StringComparison.OrdinalIgnoreCase)) &&
                apps.FirstOrDefault(app => app.HasId(favorite.AppId)) is { } renamed)
            {
                favorite.AppId = renamed.Id;
                changed = true;
            }
        if (changed) _favoriteStore.Save(_favorites);
    }

    /// <summary>추가(null) 또는 편집. web = 웹사이트 추가 (address = 넣어 둘 주소, 열면 바로 확인)</summary>
    private void EditFavorite(FavoriteProgram? editing, bool web = false, string? address = null)
    {
        var editor = new FavoriteEditorWindow(editing, Categories(), web) { Owner = this, InitialAddress = address };
        if (editor.ShowDialog() != true || editor.Result is not { } result) return;
        int index = editing is null ? -1 : _favorites.FindIndex(item => item.Id == editing.Id);
        if (index >= 0) _favorites[index] = result; else _favorites.Add(result);
        if (editing is not null && editing.Icon != result.Icon) CustomIcons.DeleteImported(editing.Icon);  // 바꾸기 전 그림 파일
        SaveFavorites(index >= 0 ? $"{result.Name} 수정함" : $"{result.Name} 추가함");
        if (result.IsWeb) _ = RefreshSiteIconsAsync();
    }

    // 이번 실행에서 아이콘을 받아 본 사이트 (못 받았어도 다시 묻지 않음)
    private readonly HashSet<string> _siteIconTried = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>자동 아이콘인 웹사이트 중 받아 둔 아이콘이 없는 것: 그 사이트에서 받아 카드를 다시 그림</summary>
    private async Task RefreshSiteIconsAsync()
    {
        var missing = _favorites.Where(item => item.IsWeb && !CustomIcons.IsCustom(item.Icon) && SiteInfo.Cached(item.Path) is null && _siteIconTried.Add(item.Path)).ToList();
        bool changed = false;
        foreach (var site in missing)
        {
            if (SiteInfo.Normalize(site.Path) is not { } address) continue;
            try
            {
                using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                if ((await SiteInfo.FetchAsync(address, cancel.Token)).IconPng is not { } png) continue;
                SiteInfo.SaveCache(site.Path, png);
                _programCards.Remove(site);
                changed = true;
            }
            catch (Exception error) when (error is System.Net.Http.HttpRequestException or TaskCanceledException or OperationCanceledException
                                              or IOException or UnauthorizedAccessException) { }
        }
        if (changed) Render();
    }

    /// <summary>저장하고 다시 그림 (보던 화면 그대로, 독에 바로 보임)</summary>
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
        if (MessageBox.Show(this, card.IsWeb ? $"{card.Name}을(를) 즐겨찾기에서 뺄까요?" : $"{card.Name}을(를) 즐겨찾기에서 뺄까요?\n프로그램은 지우지 않습니다.", "즐겨찾기",
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        _favorites.RemoveAll(item => item.Id == card.Program.Id);
        CustomIcons.DeleteImported(card.Program.Icon);
        SaveFavorites($"{card.Name} 뺌");
    }

    /// <summary>내 PC 프로그램 실행. 실행하지 못했으면 false (오류는 알림에)</summary>
    private bool RunFavorite(FavoriteCard card)
    {
        var program = card.Program;
        if (!card.Exists)
        {
            ShowError($"{card.Name}: 파일이 없습니다. 우클릭 → 편집에서 경로를 고치세요. ({program.Path})");
            return false;
        }
        // 이미 실행 중인 프로그램(.exe)은 그 창을 앞으로. Ctrl+클릭은 하나 더 실행
        if (!card.IsWeb && !Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && WindowFront.ActivateProgram(program.Path))
        {
            StatusText.Text = $"{card.Name} 앞으로 (Ctrl+클릭: 하나 더 실행)";
            HideError();
            _usage.Add(UsageStore.ProgramKey(program.Id));
            return true;
        }
        try
        {
            var start = new ProcessStartInfo(program.Path) { UseShellExecute = true };
            if (!card.IsWeb)
            {
                start.Arguments = program.Arguments;
                if (File.Exists(program.Path) && Path.GetDirectoryName(program.Path) is { } folder) start.WorkingDirectory = folder;
            }
            Process.Start(start);
            StatusText.Text = card.IsWeb ? $"{card.Name} 열기" : $"{card.Name} 실행";
            HideError();
            _usage.Add(UsageStore.ProgramKey(program.Id));
            return true;
        }
        catch (Win32Exception error) when (error.NativeErrorCode == 1223)
        {
            StatusText.Text = $"{card.Name} 실행을 취소했습니다.";  // UAC 취소
            return true;
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException)
        {
            ShowError($"{card.Name}을(를) 실행하지 못했습니다: {error.Message}");
            return false;
        }
    }

    // ── 끌어 놓기: 실행 파일·바로 가기·파일을 창에 놓으면 즐겨찾기에 추가. 브라우저의 주소(링크)를 놓으면 웹사이트 추가 창 ──

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) || DroppedAddress(e.Data) is not null ? DragDropEffects.Link : DragDropEffects.None;
        e.Handled = true;
    }

    /// <summary>끌어 온 링크 주소 (브라우저 주소 표시줄 · 링크), 아니면 null</summary>
    private static Uri? DroppedAddress(IDataObject data)
    {
        if (data.GetDataPresent(DataFormats.FileDrop)) return null;
        string? text = data.GetDataPresent("UniformResourceLocatorW") && data.GetData("UniformResourceLocatorW") is MemoryStream unicode
            ? System.Text.Encoding.Unicode.GetString(unicode.ToArray()).TrimEnd((char)0)
            : data.GetDataPresent(DataFormats.UnicodeText) ? data.GetData(DataFormats.UnicodeText) as string : null;
        text = text?.Trim();
        return text is { Length: > 0 } && SiteInfo.IsWebAddress(text) && !text.Contains((char)10) ? SiteInfo.Normalize(text) : null;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (DroppedAddress(e.Data) is { } address)
        {
            if (_favorites.Any(item => string.Equals(item.Path, address.AbsoluteUri, StringComparison.OrdinalIgnoreCase)))
            {
                StatusText.Text = "이미 즐겨찾기에 있는 사이트입니다.";
                return;
            }
            // 이름 · 아이콘을 그 사이트에서 가져오도록 주소를 넣은 웹사이트 추가 창
            Dispatcher.BeginInvoke(() => EditFavorite(null, web: true, address: address.AbsoluteUri));
            return;
        }
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0) return;
        var added = new List<string>();
        foreach (string path in paths.Where(path => File.Exists(path) || Directory.Exists(path)))
        {
            if (_favorites.Any(item => string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase))) continue;
            string name = Directory.Exists(path) ? Path.GetFileName(path.TrimEnd('\\')) : Path.GetFileNameWithoutExtension(path);
            _favorites.Add(new FavoriteProgram { Name = name, Path = path });
            added.Add(name);
        }
        if (added.Count > 0) SaveFavorites(added.Count == 1 ? $"{added[0]} 추가함" : $"{added.Count}개 추가함");
        else Render();
    }
}
