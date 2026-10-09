using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using WinForms = System.Windows.Forms;
using System.Threading.Tasks;

namespace FolderCompareWPF
{
    public enum DiffType { Same, Added, Removed, Modified }

    public partial class MainWindow : System.Windows.Window
    {
        private const long MaxEditableTextFileBytes = 16L * 1024 * 1024;

        private bool _ignoreWhitespace;
        private bool _isCopying;
        private string? _lastReport;
        private bool _isComparisonStale;
        private readonly System.Diagnostics.Stopwatch _comparisonElapsed = new();
        private TimeSpan _scanElapsed;
        private string _dirA = "";
        private string _dirB = "";
        private CompareResult? _lastResult;
        private List<DiffRow> _currentDiffRows = new();
        private PathSettings _pathSettings = new();

        private List<Paragraph?> _paragraphsA = new();
        private List<Paragraph?> _paragraphsB = new();

        private string? _currentRelativePath;
        private DecodedTextFile? _fileDataA;
        private DecodedTextFile? _fileDataB;
        private bool _isDirtyA;
        private bool _isDirtyB;
        private bool _isLoadingEditors;
        private bool _isRestoringTreeSelection;
        private TreeViewItem? _selectedTreeItem;
        private CancellationTokenSource? _comparisonCancellation;
        private TreeViewItem? _nodeOnlyA;
        private TreeViewItem? _nodeOnlyB;
        private TreeViewItem? _nodeIdentical;
        private TreeViewItem? _nodeDifferent;
        private TreeViewItem? _nodeUnreadable;
        private int _processedFileCount;
        private int _totalFileCount;
        private int _skippedPathCount;
        private int _discoveredFileCountA;
        private int _discoveredFileCountB;

        public MainWindow()
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            InitializeComponent();

            _pathSettings = PathSettings.Load();
            if (!string.IsNullOrEmpty(_pathSettings.LastDirA)) TxtDirA.Text = _pathSettings.LastDirA;
            if (!string.IsNullOrEmpty(_pathSettings.LastDirB)) TxtDirB.Text = _pathSettings.LastDirB;
        }

        private void BtnBrowseA_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            var path = BrowseFolder(TxtDirA.Text);
            if (path != null)
            {
                TxtDirA.Text = path;
                _pathSettings.LastDirA = path;
                _pathSettings.Save();
            }
        }

        private void BtnBrowseB_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            var path = BrowseFolder(TxtDirB.Text);
            if (path != null)
            {
                TxtDirB.Text = path;
                _pathSettings.LastDirB = path;
                _pathSettings.Save();
            }
        }

        private string? BrowseFolder(string? initialPath = null)
        {
            using var dialog = new WinForms.FolderBrowserDialog();
            if (!string.IsNullOrEmpty(initialPath) && Directory.Exists(initialPath))
                dialog.SelectedPath = initialPath;

            var result = dialog.ShowDialog();
            if (result == WinForms.DialogResult.OK)
                return dialog.SelectedPath;
            return null;
        }

        private static string NormalizeComparisonRoot(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("폴더 경로를 입력해주세요.");
            string trimmed = path.Trim();
            if (trimmed.Length == 1 && char.IsLetter(trimmed[0]))
                trimmed += @":\";
            else if (trimmed.Length == 2 && char.IsLetter(trimmed[0]) && trimmed[1] == ':')
                trimmed += @"\";

            return Path.GetFullPath(trimmed);
        }

        private void TxtDir_PreviewDragOver(object sender, System.Windows.DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop)
                ? System.Windows.DragDropEffects.Copy
                : System.Windows.DragDropEffects.None;
            e.Handled = true;
        }

        private string? GetDroppedFolderPath(System.Windows.DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop)) return null;
            if (e.Data.GetData(System.Windows.DataFormats.FileDrop) is not string[] paths || paths.Length == 0)
                return null;

            string dropped = paths[0];

            if (Directory.Exists(dropped))
                return dropped;

            if (File.Exists(dropped))
            {
                string? parent = Path.GetDirectoryName(dropped);
                if (parent != null && Directory.Exists(parent))
                    return parent;
            }

            return null;
        }

        private void TxtDirA_Drop(object sender, System.Windows.DragEventArgs e)
        {
            string? path = GetDroppedFolderPath(e);
            if (path != null)
            {
                TxtDirA.Text = path;
                _pathSettings.LastDirA = path;
                _pathSettings.Save();
            }
        }

        private void TxtDirB_Drop(object sender, System.Windows.DragEventArgs e)
        {
            string? path = GetDroppedFolderPath(e);
            if (path != null)
            {
                TxtDirB.Text = path;
                _pathSettings.LastDirB = path;
                _pathSettings.Save();
            }
        }

        private async void BtnCompare_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            if (_comparisonCancellation != null || _isCopying) return;
            string dirA;
            string dirB;
            try
            {
                dirA = NormalizeComparisonRoot(TxtDirA.Text);
                dirB = NormalizeComparisonRoot(TxtDirB.Text);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException)
            {
                System.Windows.MessageBox.Show("폴더 또는 드라이브 경로 형식이 올바르지 않습니다.\n\n" + ex.Message);
                return;
            }

            if (!Directory.Exists(dirA))
            {
                System.Windows.MessageBox.Show("폴더/드라이브 A 경로가 존재하지 않거나 사용할 수 없습니다.");
                return;
            }
            if (!Directory.Exists(dirB))
            {
                System.Windows.MessageBox.Show("폴더/드라이브 B 경로가 존재하지 않거나 사용할 수 없습니다.");
                return;
            }

            TxtDirA.Text = dirA;
            TxtDirB.Text = dirB;

            if (!ConfirmPendingChanges())
                return;

            _pathSettings.LastDirA = dirA;
            _pathSettings.LastDirB = dirB;
            _pathSettings.Save();
            _comparisonElapsed.Restart();
            _scanElapsed = TimeSpan.Zero;
            _comparisonCancellation?.Dispose();
            var comparisonCancellation = new CancellationTokenSource();
            _comparisonCancellation = comparisonCancellation;
            CancellationToken cancellationToken = comparisonCancellation.Token;

            _ignoreWhitespace = ChkIgnoreWhitespace.IsChecked == true;
            ChkIgnoreWhitespace.IsEnabled = false;
            BtnCopySelected.IsEnabled = false;
            ClearFileSelection();
            BtnCompare.IsEnabled = false;
            BtnCompare.Content = "비교 중...";
            BtnBrowseA.IsEnabled = false;
            BtnBrowseB.IsEnabled = false;
            TxtDirA.IsEnabled = false;
            TxtDirB.IsEnabled = false;
            BtnSave.IsEnabled = false;

            _lastReport = null;
            _dirA = dirA;
            _dirB = dirB;
            _lastResult = new CompareResult();
            _isComparisonStale = false;
            _processedFileCount = 0;
            _totalFileCount = 0;
            _skippedPathCount = 0;
            _discoveredFileCountA = 0;
            _discoveredFileCountB = 0;
            ResetCurrentEditor();
            InitializeTree();

            ShowMessage("파일 목록을 확인하고 있습니다. 완료된 파일은 바로 선택할 수 있습니다.");
            TxtLoadingMessage.Visibility = Visibility.Collapsed;
            BorderCompareProgress.Visibility = Visibility.Visible;
            TxtCompareProgress.Text = "파일 목록 확인 중... · 표시된 항목은 완료 순서로 고정됩니다.";

            try
            {
                await RunComparisonPipelineAsync(dirA, dirB, cancellationToken);

                if (_currentRelativePath == null)
                    ShowMessage("비교가 완료되었습니다. 파일을 선택하세요.");

                BtnSave.IsEnabled = !_isComparisonStale;
                BtnSave.ToolTip = _isComparisonStale
                    ? "파일 내용이 변경되었습니다. 다시 비교하면 결과를 저장할 수 있습니다."
                    : null;
            }
            catch (OperationCanceledException)
            {
                TxtCompareProgress.Text = "비교가 취소되었습니다. 이미 표시된 항목은 계속 사용할 수 있습니다.";
                _isComparisonStale = true;
            }
            catch (Exception ex)
            {
                TxtCompareProgress.Text = "비교 중 오류가 발생했습니다. 이미 표시된 항목은 계속 사용할 수 있습니다.";
                _isComparisonStale = true;
                System.Windows.MessageBox.Show(
                    "폴더를 비교하는 중 오류가 발생했습니다.\n\n" + ex.Message,
                    "비교 오류",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            finally
            {
                _comparisonElapsed.Stop();
                ChkIgnoreWhitespace.IsEnabled = true;
                UpdateCopyButton();
                BtnCompare.IsEnabled = true;
                BtnCompare.Content = "비교";
                BtnBrowseA.IsEnabled = true;
                BtnBrowseB.IsEnabled = true;
                TxtDirA.IsEnabled = true;
                TxtDirB.IsEnabled = true;

                if (ReferenceEquals(_comparisonCancellation, comparisonCancellation))
                {
                    _comparisonCancellation.Dispose();
                    _comparisonCancellation = null;
                    UpdateCopyButton();
                }
            }
        }

        private async void BtnSave_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            if (_lastResult == null || _isComparisonStale)
            {
                System.Windows.MessageBox.Show(
                    _isComparisonStale
                        ? "파일 내용이 변경되었습니다. 다시 비교해주세요."
                        : "먼저 비교를 실행해주세요.");
                return;
            }

            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                FileName = "compare_result.txt",
                Filter = "텍스트 파일 (*.txt)|*.txt"
            };

            if (dialog.ShowDialog() == true)
            {
                string originalContent = BtnSave.Content?.ToString() ?? "결과 저장";
                BtnSave.IsEnabled = false;
                BtnSave.Content = "보고서 생성 중...";
                Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;
                try
                {
                    _lastReport ??= await Task.Run(() => BuildReport(_lastResult, _dirA, _dirB));
                    File.WriteAllText(dialog.FileName, _lastReport, new UTF8Encoding(true));
                    System.Windows.MessageBox.Show("저장 완료: " + dialog.FileName);
                }
                catch (Exception ex)
                {
                    System.Windows.MessageBox.Show(
                        "비교 보고서를 저장하지 못했습니다.\n\n" + ex.Message,
                        "저장 오류",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }
                finally
                {
                    Mouse.OverrideCursor = null;
                    BtnSave.Content = originalContent;
                    BtnSave.IsEnabled = _lastResult != null && !_isComparisonStale;
                }
            }
        }

        // ===================== 트리 / 상세 비교 UI =====================

        private void InitializeTree()
        {
            _treeVersion++;
            _resultBuckets.Clear();
            _deferTreeMaterialization = false;
            TvFileList.Items.Clear();

            _nodeOnlyA = new System.Windows.Controls.TreeViewItem
            {
                Header = "A에만 있는 파일 (0)"
            };
            TvFileList.Items.Add(_nodeOnlyA);

            _nodeOnlyB = new System.Windows.Controls.TreeViewItem
            {
                Header = "B에만 있는 파일 (0)"
            };
            TvFileList.Items.Add(_nodeOnlyB);

            _nodeIdentical = new System.Windows.Controls.TreeViewItem
            {
                Header = "동일한 파일 (0)"
            };
            TvFileList.Items.Add(_nodeIdentical);

            _nodeDifferent = new System.Windows.Controls.TreeViewItem
            {
                Header = "다른 파일 (0)"
            };
            TvFileList.Items.Add(_nodeDifferent);

            _nodeUnreadable = new System.Windows.Controls.TreeViewItem
            {
                Header = "읽기 실패 (0)"
            };
            TvFileList.Items.Add(_nodeUnreadable);
        }

        private async Task AppendInitialResultsAsync(CompareResult result, CancellationToken cancellationToken)
        {
            int count = 0;
            foreach (string rel in result.OnlyInA)
            {
                AppendTreeItem(_nodeOnlyA, "OnlyInA", rel);
                if (++count % 1024 == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
                }
            }
            foreach (string rel in result.OnlyInB)
            {
                AppendTreeItem(_nodeOnlyB, "OnlyInB", rel);
                if (++count % 1024 == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
                }
            }
            UpdateTreeHeaders(result);
        }

        private void DrainCompletedComparisons(
            CompareResult result,
            ConcurrentQueue<CommonFileComparisonResult> completed,
            int maxItems)
        {
            int drained = 0;
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            while (drained < maxItems && completed.TryDequeue(out CommonFileComparisonResult? file))
            {
                if (file.ErrorMessage != null)
                {
                    result.Unreadable.Add(new UnreadableFileResult
                    {
                        RelPath = file.RelPath,
                        ErrorMessage = file.ErrorMessage
                    });
                    AppendTreeItem(_nodeUnreadable, "Unreadable", file.RelPath);
                }
                else if (file.IsIdentical)
                {
                    result.Identical.Add(file.RelPath);
                    AppendTreeItem(_nodeIdentical, "Identical", file.RelPath);
                }
                else
                {
                    result.Different.Add(new DifferentFileResult
                    {
                        RelPath = file.RelPath,
                        IsBinary = file.IsBinary
                    });
                    AppendTreeItem(_nodeDifferent, "Different", file.RelPath);
                }

                drained++;
                _processedFileCount++;
                if (System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds >= 8) break;
            }

            if (drained > 0)
                UpdateTreeHeaders(result);
        }

        private void UpdateTreeHeaders(CompareResult result)
        {
            if (_nodeOnlyA != null) _nodeOnlyA.Header = $"A에만 있는 파일 ({result.OnlyInA.Count})";
            if (_nodeOnlyB != null) _nodeOnlyB.Header = $"B에만 있는 파일 ({result.OnlyInB.Count})";
            if (_nodeIdentical != null) _nodeIdentical.Header = $"동일한 파일 ({result.Identical.Count})";
            if (_nodeDifferent != null) _nodeDifferent.Header = $"다른 파일 ({result.Different.Count})";
            if (_nodeUnreadable != null) _nodeUnreadable.Header = $"읽기 실패 ({result.Unreadable.Count})";
        }

        private void UpdateComparisonProgress(bool isComplete)
        {
            string skipped = _skippedPathCount > 0
                ? $" · 접근 불가/연결 경로 {_skippedPathCount:N0}개 건너뜀"
                : string.Empty;
            const string excludedSystem = " · 시스템/연결 항목 제외";
            string timing = $"검색 {_scanElapsed.TotalSeconds:F1}초 · 전체 {_comparisonElapsed.Elapsed.TotalSeconds:F1}초";

            if (isComplete)
            {
                TxtCompareProgress.Text =
                    $"비교 완료 · {_processedFileCount:N0}/{_totalFileCount:N0}개{skipped}{excludedSystem}\n{timing}";
                return;
            }

            double percent = _totalFileCount == 0
                ? 0
                : _processedFileCount * 100.0 / _totalFileCount;
            TxtCompareProgress.Text =
                $"병렬 비교 중 {_processedFileCount:N0}/{_totalFileCount:N0}개 ({percent:0}%){skipped}{excludedSystem}\n{timing}";
        }

        private void TvFileList_SelectedItemChanged(object sender,
    System.Windows.RoutedPropertyChangedEventArgs<object> e)
        {
            if (_isRestoringTreeSelection)
                return;

            if (ResolveFileContainer(e.NewValue) is not TreeViewItem item ||
                item.Tag is not (string category, string rel))
            {
                return;
            }

            if (_lastResult == null)
            {
                ShowMessage("파일을 선택하세요.");
                return;
            }

            if (_currentRelativePath != null &&
                !RelativePathComparer.Equals(_currentRelativePath, rel) &&
                !ConfirmPendingChanges())
            {
                RestorePreviousTreeSelection();
                return;
            }

            if (!_isApplyingFileSelection) SetFileSelection(item, false);

            if (category == "Unreadable")
            {
                UnreadableFileResult? unreadable = _lastResult.Unreadable.FirstOrDefault(
                    file => RelativePathComparer.Equals(file.RelPath, rel));
                ResetCurrentEditor();
                ShowMessage(unreadable == null
                    ? "파일을 읽지 못했습니다."
                    : $"파일을 읽지 못했습니다.\n{unreadable.ErrorMessage}");
                _selectedTreeItem = item;
                return;
            }

            try
            {
                LoadFileIntoEditors(category, rel, item);
            }
            catch (Exception ex)
            {
                ResetCurrentEditor();
                ShowMessage("파일을 여는 중 오류가 발생했습니다.\n" + ex.Message);
            }
        }

        private void LoadFileIntoEditors(string category, string rel, TreeViewItem item)
        {
            _ = category;
            string pathA = Path.Combine(_dirA, rel);
            string pathB = Path.Combine(_dirB, rel);
            bool existsA = File.Exists(pathA);
            bool existsB = File.Exists(pathB);

            if (existsA && new FileInfo(pathA).Length > MaxEditableTextFileBytes)
            {
                ResetCurrentEditor();
                ShowMessage("A 파일이 16MB보다 커서 비교 편집기에서 열 수 없습니다.");
                return;
            }

            if (existsB && new FileInfo(pathB).Length > MaxEditableTextFileBytes)
            {
                ResetCurrentEditor();
                ShowMessage("B 파일이 16MB보다 커서 비교 편집기에서 열 수 없습니다.");
                return;
            }

            DecodedTextFile? dataA = null;
            DecodedTextFile? dataB = null;

            if (existsA)
            {
                if (!FileContentComparer.TryReadText(File.ReadAllBytes(pathA), out DecodedTextFile decodedA))
                {
                    ResetCurrentEditor();
                    ShowMessage("A 파일은 바로 편집할 수 없는 바이너리 파일입니다.");
                    return;
                }
                dataA = decodedA;
            }

            if (existsB)
            {
                if (!FileContentComparer.TryReadText(File.ReadAllBytes(pathB), out DecodedTextFile decodedB))
                {
                    ResetCurrentEditor();
                    ShowMessage("B 파일은 바로 편집할 수 없는 바이너리 파일입니다.");
                    return;
                }
                dataB = decodedB;
            }

            if (!existsA && !existsB)
            {
                ResetCurrentEditor();
                ShowMessage("파일이 더 이상 존재하지 않습니다. 다시 비교해주세요.");
                return;
            }

            _currentRelativePath = rel;
            _fileDataA = dataA;
            _fileDataB = dataB;
            _isDirtyA = false;
            _isDirtyB = false;
            _selectedTreeItem = item;

            TxtInfoMessage.Visibility = Visibility.Collapsed;
            GridDiffPanel.Visibility = Visibility.Visible;
            GridDiffPanel.UpdateLayout();

            ShowDiffRows(BuildRowsForEditors(
                dataA?.NormalizedText,
                dataB?.NormalizedText));

            RtbDiffA.IsReadOnly = dataA == null;
            RtbDiffB.IsReadOnly = dataB == null;
            RtbDiffA.ToolTip = dataA == null ? "A 폴더에는 이 파일이 없습니다." : pathA;
            RtbDiffB.ToolTip = dataB == null ? "B 폴더에는 이 파일이 없습니다." : pathB;
            UpdateEditorState();
        }

        private List<DiffRow> BuildRowsForEditors(string? textA, string? textB)
        {
            if (textA != null && textB != null)
                return BuildDifflibStyleDiff(textA.Split('\n'), textB.Split('\n'));

            if (textA != null)
            {
                return textA.Split('\n')
                    .Select((line, index) => new DiffRow
                    {
                        LineNoA = index + 1,
                        A = line,
                        Type = DiffType.Removed
                    })
                    .ToList();
            }

            return (textB ?? string.Empty).Split('\n')
                .Select((line, index) => new DiffRow
                {
                    LineNoB = index + 1,
                    B = line,
                    Type = DiffType.Added
                })
                .ToList();
        }

        private void RestorePreviousTreeSelection()
        {
            if (_selectedTreeItem == null)
                return;

            Dispatcher.BeginInvoke(new Action(() =>
            {
                _isRestoringTreeSelection = true;
                var previous = ResolveFileContainer(_selectedTreeItem.DataContext, bringIntoView: true) ?? _selectedTreeItem;
                previous.IsSelected = true;
                previous.BringIntoView();
                _isRestoringTreeSelection = false;
            }));
        }

        private void ShowMessage(string msg)
        {
            TxtInfoMessage.Text = msg;
            TxtInfoMessage.Visibility = System.Windows.Visibility.Visible;
            GridDiffPanel.Visibility = System.Windows.Visibility.Collapsed;
        }

        private void ShowDiffRows(List<DiffRow> rows)
        {
            ResetScrollSynchronization();
            _currentDiffRows = rows;

            _isLoadingEditors = true;
            try
            {
                DiffDocumentBuilder.Build(rows, out var docA, out var docB, out var lineNumsA, out var lineNumsB,
                    out var parasA, out var parasB);
                RtbDiffA.Document = docA;
                RtbDiffB.Document = docB;
                LnDiffA.Text = lineNumsA;
                LnDiffB.Text = lineNumsB;

                _paragraphsA = parasA;
                _paragraphsB = parasB;
            }
            finally
            {
                _isLoadingEditors = false;
            }

            RtbDiffA.UpdateLayout();
            RtbDiffB.UpdateLayout();

            RecomputeAndDrawMarkers();
        }

        private void RtbDiffA_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isLoadingEditors || _currentRelativePath == null || _fileDataA == null)
                return;

            _isDirtyA = true;
            DiffDocumentBuilder.SetMinimumWidth(RtbDiffA.Document, GetDocumentText(RtbDiffA.Document).Split('\n'));
            LnDiffA.Text = BuildLineNumbers(GetDocumentText(RtbDiffA.Document));
            CanvasScrollMarkers.Children.Clear();
            UpdateEditorState();
        }

        private void RtbDiffB_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isLoadingEditors || _currentRelativePath == null || _fileDataB == null)
                return;

            _isDirtyB = true;
            DiffDocumentBuilder.SetMinimumWidth(RtbDiffB.Document, GetDocumentText(RtbDiffB.Document).Split('\n'));
            LnDiffB.Text = BuildLineNumbers(GetDocumentText(RtbDiffB.Document));
            CanvasScrollMarkers.Children.Clear();
            UpdateEditorState();
        }

        private void BtnSaveFileA_Click(object sender, RoutedEventArgs e)
        {
            if (SaveCurrentFile(isSideA: true))
                RefreshDiffFromEditors();
        }

        private void BtnSaveFileB_Click(object sender, RoutedEventArgs e)
        {
            if (SaveCurrentFile(isSideA: false))
                RefreshDiffFromEditors();
        }

        private bool SaveCurrentFile(bool isSideA)
        {
            if (_currentRelativePath == null)
                return false;

            DecodedTextFile? fileData = isSideA ? _fileDataA : _fileDataB;
            if (fileData == null)
                return false;

            string path = Path.Combine(isSideA ? _dirA : _dirB, _currentRelativePath);
            System.Windows.Controls.RichTextBox editor = isSideA ? RtbDiffA : RtbDiffB;
            string text = GetDocumentText(editor.Document);

            try
            {
                File.WriteAllBytes(path, fileData.Encode(text));
                fileData.SetNormalizedText(text);
                if (isSideA) _isDirtyA = false;
                else _isDirtyB = false;

                // 기존 비교 보고서는 방금 저장한 파일의 상태를 반영하지 않으므로 무효화한다.
                _lastReport = null;
                _isComparisonStale = true;
                BtnSave.IsEnabled = false;
                BtnSave.ToolTip = "파일 내용이 변경되었습니다. 다시 비교하면 결과를 저장할 수 있습니다.";
                UpdateEditorState();
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException ||
                                       ex is EncoderFallbackException)
            {
                System.Windows.MessageBox.Show(
                    $"{(isSideA ? "A" : "B")} 파일을 저장하지 못했습니다.\n\n{ex.Message}",
                    "저장 오류",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return false;
            }
        }

        private bool SaveDirtyFiles()
        {
            if (_isDirtyA && !SaveCurrentFile(isSideA: true))
                return false;
            if (_isDirtyB && !SaveCurrentFile(isSideA: false))
                return false;
            return true;
        }

        private bool ConfirmPendingChanges()
        {
            if (!_isDirtyA && !_isDirtyB)
                return true;

            MessageBoxResult result = System.Windows.MessageBox.Show(
                "현재 파일에 저장하지 않은 변경 내용이 있습니다. 저장하시겠습니까?",
                "변경 내용 저장",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Warning);

            if (result == MessageBoxResult.Cancel)
                return false;
            if (result == MessageBoxResult.No)
            {
                _isDirtyA = false;
                _isDirtyB = false;
                return true;
            }

            return SaveDirtyFiles();
        }

        private void RefreshDiffFromEditors()
        {
            string? textA = _fileDataA == null ? null : GetDocumentText(RtbDiffA.Document);
            string? textB = _fileDataB == null ? null : GetDocumentText(RtbDiffB.Document);
            bool dirtyA = _isDirtyA;
            bool dirtyB = _isDirtyB;

            ShowDiffRows(BuildRowsForEditors(textA, textB));

            _isDirtyA = dirtyA;
            _isDirtyB = dirtyB;
            RtbDiffA.IsReadOnly = _fileDataA == null;
            RtbDiffB.IsReadOnly = _fileDataB == null;
            UpdateEditorState();
        }

        private void UpdateEditorState()
        {
            string rel = _currentRelativePath ?? "파일 없음";
            TxtFileA.Text = _fileDataA == null ? "A · 파일 없음" : $"A · {rel}{(_isDirtyA ? " *" : "")}";
            TxtFileB.Text = _fileDataB == null ? "B · 파일 없음" : $"B · {rel}{(_isDirtyB ? " *" : "")}";
            BtnSaveFileA.IsEnabled = _fileDataA != null && _isDirtyA;
            BtnSaveFileB.IsEnabled = _fileDataB != null && _isDirtyB;
        }

        private static string GetDocumentText(FlowDocument document)
        {
            string text = new TextRange(document.ContentStart, document.ContentEnd).Text;

            // FlowDocument는 마지막 문단 뒤에 표시용 줄바꿈 하나를 항상 돌려준다.
            // 그 한 개만 제거하면 사용자가 만든 마지막 빈 줄은 그대로 보존된다.
            if (text.EndsWith("\r\n", StringComparison.Ordinal))
                text = text[..^2];
            else if (text.EndsWith('\r') || text.EndsWith('\n'))
                text = text[..^1];

            return FileContentComparer.NormalizeLineEndings(text);
        }

        private static string BuildLineNumbers(string text)
        {
            int count = FileContentComparer.NormalizeLineEndings(text).Split('\n').Length;
            return string.Join('\n', Enumerable.Range(1, count));
        }

        private void ResetCurrentEditor()
        {
            ResetScrollSynchronization();
            _isLoadingEditors = true;
            try
            {
                RtbDiffA.Document = CreateEmptyDocument();
                RtbDiffB.Document = CreateEmptyDocument();
                LnDiffA.Text = string.Empty;
                LnDiffB.Text = string.Empty;
            }
            finally
            {
                _isLoadingEditors = false;
            }

            _currentRelativePath = null;
            _fileDataA = null;
            _fileDataB = null;
            _isDirtyA = false;
            _isDirtyB = false;
            _selectedTreeItem = null;
            _currentDiffRows.Clear();
            _paragraphsA.Clear();
            _paragraphsB.Clear();
            CanvasScrollMarkers.Children.Clear();
            RtbDiffA.IsReadOnly = true;
            RtbDiffB.IsReadOnly = true;
            RtbDiffA.ToolTip = null;
            RtbDiffB.ToolTip = null;
            UpdateEditorState();
        }

        private static FlowDocument CreateEmptyDocument()
        {
            var document = new FlowDocument
            {
                PagePadding = new Thickness(0),
                FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                FontSize = 13
            };
            document.Blocks.Add(new Paragraph
            {
                Margin = new Thickness(0),
                LineHeight = DiffDocumentBuilder.DiffLineHeight
            });
            return document;
        }

        private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key != Key.S || !Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
                return;

            if (RtbDiffA.IsKeyboardFocusWithin)
            {
                if (_isDirtyA)
                    BtnSaveFileA_Click(BtnSaveFileA, new RoutedEventArgs());
            }
            else if (RtbDiffB.IsKeyboardFocusWithin)
            {
                if (_isDirtyB)
                    BtnSaveFileB_Click(BtnSaveFileB, new RoutedEventArgs());
            }
            else
            {
                if ((_isDirtyA || _isDirtyB) && SaveDirtyFiles())
                    RefreshDiffFromEditors();
            }

            e.Handled = true;
        }

        private void Window_Closing(object? sender, CancelEventArgs e)
        {
            if (_isCopying) { e.Cancel = true; return; }
            if (!ConfirmPendingChanges())
            {
                e.Cancel = true;
                return;
            }

            _comparisonCancellation?.Cancel();
        }

        private class DiffLinePair
        {
            public int LineNo { get; set; }
            public string A { get; set; } = "";
            public string B { get; set; } = "";
        }

        private List<DiffLinePair> BuildPairedDiff(string[] linesA, string[] linesB)
        {
            var pairs = new List<DiffLinePair>();
            int max = Math.Max(linesA.Length, linesB.Length);

            for (int i = 0; i < max; i++)
            {
                string a = i < linesA.Length ? linesA[i] : "(없음)";
                string b = i < linesB.Length ? linesB[i] : "(없음)";

                if (a != b)
                    pairs.Add(new DiffLinePair { LineNo = i + 1, A = a, B = b });
            }

            return pairs;
        }

        public class DiffRow
        {
            public int? LineNoA { get; set; }
            public int? LineNoB { get; set; }
            public string A { get; set; } = "";
            public string B { get; set; } = "";
            public DiffType Type { get; set; }
            public DiffType? BackgroundTypeA { get; set; }
            public DiffType? BackgroundTypeB { get; set; }

            public List<CharPiece>? CharDiffA { get; set; }
            public List<CharPiece>? CharDiffB { get; set; }

            public List<CharPiece> DisplayPiecesA =>
                CharDiffA ?? new List<CharPiece> { new CharPiece { Text = A, Kind = "same" } };

            public List<CharPiece> DisplayPiecesB =>
                CharDiffB ?? new List<CharPiece> { new CharPiece { Text = B, Kind = "same" } };
        }

        public class CharPiece
        {
            public string Text { get; set; } = "";
            public string Kind { get; set; } = "same"; // same / removed / added
        }

        /// <summary>
        /// LCS(최장 공통 부분수열) 기반 diff. VS Code diff editor와 유사하게
        /// 동일/추가/삭제/변경(Modified)을 구분하고, 전체 줄을 순서대로 정렬해서 반환한다.
        /// </summary>
        private List<DiffRow> BuildLcsDiff(string[] a, string[] b)
        {
            int n = a.Length, m = b.Length;
            var dp = new int[n + 1, m + 1];

            for (int i = n - 1; i >= 0; i--)
            {
                for (int j = m - 1; j >= 0; j--)
                {
                    dp[i, j] = a[i] == b[j]
                        ? dp[i + 1, j + 1] + 1
                        : Math.Max(dp[i + 1, j], dp[i, j + 1]);
                }
            }

            var ops = new List<(char Op, int Ai, int Bi)>();
            int x = 0, y = 0;
            while (x < n && y < m)
            {
                if (a[x] == b[y]) { ops.Add(('S', x, y)); x++; y++; }
                else if (dp[x + 1, y] >= dp[x, y + 1]) { ops.Add(('R', x, -1)); x++; }
                else { ops.Add(('A', -1, y)); y++; }
            }
            while (x < n) { ops.Add(('R', x, -1)); x++; }
            while (y < m) { ops.Add(('A', -1, y)); y++; }

            var rows = new List<DiffRow>();
            int idx = 0;
            while (idx < ops.Count)
            {
                if (ops[idx].Op == 'S')
                {
                    var o = ops[idx];
                    rows.Add(new DiffRow
                    {
                        LineNoA = o.Ai + 1,
                        LineNoB = o.Bi + 1,
                        A = a[o.Ai],
                        B = b[o.Bi],
                        Type = DiffType.Same
                    });
                    idx++;
                    continue;
                }

                var removed = new List<int>();
                while (idx < ops.Count && ops[idx].Op == 'R') { removed.Add(ops[idx].Ai); idx++; }

                var added = new List<int>();
                while (idx < ops.Count && ops[idx].Op == 'A') { added.Add(ops[idx].Bi); idx++; }

                int pairCount = Math.Min(removed.Count, added.Count);
                for (int k = 0; k < pairCount; k++)
                {
                    rows.Add(new DiffRow
                    {
                        LineNoA = removed[k] + 1,
                        LineNoB = added[k] + 1,
                        A = a[removed[k]],
                        B = b[added[k]],
                        Type = DiffType.Modified
                    });
                }
                for (int k = pairCount; k < removed.Count; k++)
                {
                    rows.Add(new DiffRow
                    {
                        LineNoA = removed[k] + 1,
                        LineNoB = null,
                        A = a[removed[k]],
                        B = "",
                        Type = DiffType.Removed
                    });
                }
                for (int k = pairCount; k < added.Count; k++)
                {
                    rows.Add(new DiffRow
                    {
                        LineNoA = null,
                        LineNoB = added[k] + 1,
                        A = "",
                        B = b[added[k]],
                        Type = DiffType.Added
                    });
                }
            }

            return rows;
        }

        private List<DiffRow> BuildDifflibStyleDiff(string[] a, string[] b)
        {
            if (_ignoreWhitespace) return WhitespaceDiffBuilder.Build(a, b);
            var rows = new List<DiffRow>();
            var matcher = new SequenceMatcherLike();
            matcher.SetSeqs(a, b);

            foreach (var (tag, ai1, ai2, bj1, bj2) in matcher.GetOpcodes())
            {
                if (tag == "equal")
                {
                    for (int offset = 0; offset < ai2 - ai1; offset++)
                    {
                        rows.Add(new DiffRow
                        {
                            LineNoA = ai1 + offset + 1,
                            LineNoB = bj1 + offset + 1,
                            A = a[ai1 + offset],
                            B = b[bj1 + offset],
                            Type = DiffType.Same
                        });
                    }
                }
                else if (tag == "delete")
                {
                    for (int i = ai1; i < ai2; i++)
                    {
                        rows.Add(new DiffRow
                        {
                            LineNoA = i + 1,
                            LineNoB = null,
                            A = a[i],
                            Type = DiffType.Removed
                        });
                    }
                }
                else if (tag == "insert")
                {
                    for (int j = bj1; j < bj2; j++)
                    {
                        rows.Add(new DiffRow
                        {
                            LineNoA = null,
                            LineNoB = j + 1,
                            B = b[j],
                            Type = DiffType.Added
                        });
                    }
                }
                else if (tag == "replace")
                {
                    int pairCount = Math.Min(ai2 - ai1, bj2 - bj1);
                    for (int offset = 0; offset < pairCount; offset++)
                    {
                        string lineA = a[ai1 + offset];
                        string lineB = b[bj1 + offset];
                        var (charA, charB) = GetCharDiff(lineA, lineB);
                        rows.Add(new DiffRow
                        {
                            LineNoA = ai1 + offset + 1,
                            LineNoB = bj1 + offset + 1,
                            A = lineA,
                            B = lineB,
                            Type = DiffType.Modified,
                            CharDiffA = charA,
                            CharDiffB = charB
                        });
                    }

                    for (int i = ai1 + pairCount; i < ai2; i++)
                    {
                        rows.Add(new DiffRow
                        {
                            LineNoA = i + 1,
                            A = a[i],
                            Type = DiffType.Removed
                        });
                    }

                    for (int j = bj1 + pairCount; j < bj2; j++)
                    {
                        rows.Add(new DiffRow
                        {
                            LineNoB = j + 1,
                            B = b[j],
                            Type = DiffType.Added
                        });
                    }
                }
            }

            return rows;
        }

        private (List<CharPiece> A, List<CharPiece> B) GetCharDiff(string str1, string str2)
        {
            var matcher = new SequenceMatcherLike();
            matcher.SetSeqsChar(str1, str2);

            var diff1 = new List<CharPiece>();
            var diff2 = new List<CharPiece>();

            foreach (var (tag, i1, i2, j1, j2) in matcher.GetOpcodes())
            {
                switch (tag)
                {
                    case "equal":
                        diff1.Add(new CharPiece { Text = str1.Substring(i1, i2 - i1), Kind = "same" });
                        diff2.Add(new CharPiece { Text = str2.Substring(j1, j2 - j1), Kind = "same" });
                        break;
                    case "delete":
                        diff1.Add(new CharPiece { Text = str1.Substring(i1, i2 - i1), Kind = "removed" });
                        break;
                    case "insert":
                        diff2.Add(new CharPiece { Text = str2.Substring(j1, j2 - j1), Kind = "added" });
                        break;
                    case "replace":
                        diff1.Add(new CharPiece { Text = str1.Substring(i1, i2 - i1), Kind = "removed" });
                        diff2.Add(new CharPiece { Text = str2.Substring(j1, j2 - j1), Kind = "added" });
                        break;
                }
            }

            return (diff1, diff2);
        }

        // ===================== 비교 로직 =====================

        private sealed class TreeFolderTag
        {
            public TreeFolderTag(string relativePath)
            {
                RelativePath = relativePath;
            }

            public string RelativePath { get; }
        }

        private class CompareResult
        {
            public List<string> OnlyInA { get; set; } = new();
            public List<string> OnlyInB { get; set; } = new();
            public List<string> Identical { get; set; } = new();
            public List<DifferentFileResult> Different { get; set; } = new();
            public List<UnreadableFileResult> Unreadable { get; set; } = new();
        }

        private class DifferentFileResult
        {
            public string RelPath { get; set; } = string.Empty;
            public bool IsBinary { get; set; }
        }

        private class UnreadableFileResult
        {
            public string RelPath { get; set; } = string.Empty;
            public string ErrorMessage { get; set; } = string.Empty;
        }

        private class PreparedComparison
        {
            public List<string> OnlyInA { get; set; } = new();
            public List<string> OnlyInB { get; set; } = new();
            public List<string> Common { get; set; } = new();
            public int SkippedPathsA { get; set; }
            public int SkippedPathsB { get; set; }
        }

        private class FileEnumerationResult
        {
            public HashSet<string> Files { get; } = new(RelativePathComparer);
            public int SkippedPathCount { get; set; }
        }

        private class CommonFileComparisonResult
        {
            public string RelPath { get; set; } = string.Empty;
            public bool IsIdentical { get; set; }
            public bool IsBinary { get; set; }
            public string? ErrorMessage { get; set; }
        }

        private static readonly StringComparer RelativePathComparer = StringComparer.OrdinalIgnoreCase;

        private static bool IsSkippableFileSystemException(Exception ex) =>
            ex is IOException || ex is UnauthorizedAccessException ||
            ex is System.Security.SecurityException;

        private PreparedComparison PrepareComparison(
            string dirA,
            string dirB,
            CancellationToken cancellationToken)
        {
            FileEnumerationResult? enumerationA = null;
            FileEnumerationResult? enumerationB = null;
            Parallel.Invoke(
                new ParallelOptions
                {
                    CancellationToken = cancellationToken,
                    MaxDegreeOfParallelism = 2
                },
                () => enumerationA = GetAllFiles(
                    dirA,
                    cancellationToken,
                    () => Interlocked.Increment(ref _discoveredFileCountA)),
                () => enumerationB = GetAllFiles(
                    dirB,
                    cancellationToken,
                    () => Interlocked.Increment(ref _discoveredFileCountB)));
            cancellationToken.ThrowIfCancellationRequested();
            FileEnumerationResult completedEnumerationA = enumerationA ??
                throw new InvalidOperationException("A 파일 목록을 읽지 못했습니다.");
            FileEnumerationResult completedEnumerationB = enumerationB ??
                throw new InvalidOperationException("B 파일 목록을 읽지 못했습니다.");
            HashSet<string> completedFilesA = completedEnumerationA.Files;
            HashSet<string> completedFilesB = completedEnumerationB.Files;

            return new PreparedComparison
            {
                OnlyInA = completedFilesA.Except(completedFilesB, RelativePathComparer)
                    .OrderBy(x => x, RelativePathComparer).ToList(),
                OnlyInB = completedFilesB.Except(completedFilesA, RelativePathComparer)
                    .OrderBy(x => x, RelativePathComparer).ToList(),
                Common = completedFilesA.Intersect(completedFilesB, RelativePathComparer)
                    .OrderBy(x => x, RelativePathComparer).ToList(),
                SkippedPathsA = completedEnumerationA.SkippedPathCount,
                SkippedPathsB = completedEnumerationB.SkippedPathCount
            };
        }

        private string BuildReport(CompareResult result, string dirA, string dirB)
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== 폴더 비교 결과 ===");
            sb.AppendLine($"A: {dirA}");
            sb.AppendLine($"B: {dirB}");
            sb.AppendLine($"공백·탭·줄바꿈 무시: {(_ignoreWhitespace ? "켜짐 (16MB 이하 텍스트)" : "꺼짐")}");
            sb.AppendLine();

            sb.AppendLine($"[A에만 있는 파일] ({result.OnlyInA.Count}개)");
            foreach (var f in result.OnlyInA) sb.AppendLine($"  - {f}");
            sb.AppendLine();

            sb.AppendLine($"[B에만 있는 파일] ({result.OnlyInB.Count}개)");
            foreach (var f in result.OnlyInB) sb.AppendLine($"  - {f}");
            sb.AppendLine();

            sb.AppendLine($"[동일한 파일] ({result.Identical.Count}개)");
            foreach (var f in result.Identical) sb.AppendLine($"  - {f}");
            sb.AppendLine();

            sb.AppendLine($"[다른 파일] ({result.Different.Count}개)");
            foreach (DifferentFileResult entry in result.Different)
            {
                sb.AppendLine($"  - {entry.RelPath}");

                if (entry.IsBinary)
                {
                    sb.AppendLine("      (바이너리 또는 16MB 초과 파일 - 내용 다름)");
                }
                else
                {
                    string pathA = Path.Combine(dirA, entry.RelPath);
                    string pathB = Path.Combine(dirB, entry.RelPath);
                    if (!FileContentComparer.TryReadText(File.ReadAllBytes(pathA), out DecodedTextFile textA) ||
                        !FileContentComparer.TryReadText(File.ReadAllBytes(pathB), out DecodedTextFile textB))
                    {
                        sb.AppendLine("      (텍스트 내용을 읽을 수 없음)");
                        sb.AppendLine();
                        continue;
                    }

                    List<DiffRow> diffRows = BuildDifflibStyleDiff(
                        textA.NormalizedText.Split('\n'),
                        textB.NormalizedText.Split('\n'));
                    foreach (var row in diffRows)
                    {
                        switch (row.Type)
                        {
                            case DiffType.Same:
                                sb.AppendLine($"      {row.A}");
                                break;
                            case DiffType.Removed:
                                sb.AppendLine($"    - A[{row.LineNoA}]: {row.A}");
                                break;
                            case DiffType.Added:
                                sb.AppendLine($"    + B[{row.LineNoB}]: {row.B}");
                                break;
                            case DiffType.Modified:
                                sb.AppendLine($"    - A[{row.LineNoA}]: {row.A}");
                                sb.AppendLine($"    + B[{row.LineNoB}]: {row.B}");
                                break;
                        }
                    }
                }
                sb.AppendLine();
            }

            sb.AppendLine($"[읽기 실패] ({result.Unreadable.Count}개)");
            foreach (UnreadableFileResult entry in result.Unreadable)
                sb.AppendLine($"  - {entry.RelPath}: {entry.ErrorMessage}");
            sb.AppendLine();

            return sb.ToString();
        }
    }
}
