using System.ComponentModel;
using System.IO;
using System.Windows;

namespace FolderCompareWPF;

public partial class MainWindow
{
    private void OpenFolderA_Click(object sender, RoutedEventArgs e) => OpenFolder(TxtDirA.Text);
    private void OpenFolderB_Click(object sender, RoutedEventArgs e) => OpenFolder(TxtDirB.Text);

    private static void OpenFolder(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path)) throw new IOException("폴더를 먼저 선택해주세요.");
            string fullPath = NormalizeComparisonRoot(path);
            if (!Directory.Exists(fullPath)) throw new IOException("폴더가 존재하지 않습니다.");
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(fullPath) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or Win32Exception or NotSupportedException)
        { System.Windows.MessageBox.Show(ex.Message, "폴더 열기"); }
    }

    private void UpdateCopyButton()
    {
        int count = _selectedFiles.Values.Count(file => file.CanCopy);
        BtnCopySelected.IsEnabled = count > 0 && _comparisonCancellation == null && !_isCopying;
        BtnCopySelected.Content = $"선택 파일 A → B 덮어쓰기 ({count})";
        BtnCopySelected.ToolTip = "Ctrl+클릭: 추가/해제 · Shift+클릭: 범위 선택. A에 있는 선택 파일만 복사합니다.";
    }

    private void IgnoreWhitespace_Click(object sender, RoutedEventArgs e)
    {
        if (_lastResult != null) BtnCompare_Click(BtnCompare, new RoutedEventArgs());
        else _ignoreWhitespace = ChkIgnoreWhitespace.IsChecked == true;
        if (_comparisonCancellation == null) ChkIgnoreWhitespace.IsChecked = _ignoreWhitespace;
    }

    private async void BtnCopySelected_Click(object sender, RoutedEventArgs e)
    {
        if (_isCopying || _comparisonCancellation != null || !_selectedFiles.Values.Any(file => file.CanCopy)) return;
        if (!ConfirmPendingChanges()) return;
        string[] selected = GetSelectedCopyPaths();
        string preview = string.Join("\n", selected.Take(12));
        if (selected.Length > 12) preview += $"\n외 {selected.Length - 12}개";
        if (System.Windows.MessageBox.Show(
            $"선택한 {selected.Length}개 파일을 복사합니다. B에 있는 파일은 덮어씁니다.\n\nA: {_dirA}\nB: {_dirB}\n\n{preview}",
            "선택 파일 A → B", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        _isCopying = true;
        BtnCompare.IsEnabled = ChkIgnoreWhitespace.IsEnabled = TvFileList.IsEnabled = false;
        BtnBrowseA.IsEnabled = BtnBrowseB.IsEnabled = TxtDirA.IsEnabled = TxtDirB.IsEnabled = false;
        BtnSave.IsEnabled = false;
        RtbDiffA.IsReadOnly = RtbDiffB.IsReadOnly = true;
        UpdateCopyButton();
        var failures = new List<string>();
        int copied = 0;
        try
        {
            await System.Threading.Tasks.Task.Run(() =>
            {
                foreach (string rel in selected)
                {
                    try { SelectedFileCopier.Copy(_dirA, _dirB, rel); copied++; }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                    { failures.Add($"{rel}: {ex.Message}"); }
                }
            });
        }
        finally
        {
            _isCopying = false;
            BtnCompare.IsEnabled = ChkIgnoreWhitespace.IsEnabled = TvFileList.IsEnabled = true;
            BtnBrowseA.IsEnabled = BtnBrowseB.IsEnabled = TxtDirA.IsEnabled = TxtDirB.IsEnabled = true;
            _isComparisonStale = true;
            _lastReport = null;
            ResetCurrentEditor();
            UpdateCopyButton();
        }
        System.Windows.MessageBox.Show($"복사 완료: {copied}개 · 실패: {failures.Count}개\n" + string.Join("\n", failures.Take(10)), "복사 결과");
        TxtDirA.Text = _dirA;
        TxtDirB.Text = _dirB;
        BtnCompare_Click(BtnCompare, new RoutedEventArgs());
    }
}
