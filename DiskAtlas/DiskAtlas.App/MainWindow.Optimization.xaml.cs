using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DiskAtlas.Core.Optimization;

namespace DiskAtlas.App;

public partial class MainWindow
{
    private StorageOptimizationService _optimizationService = new();
    private IReadOnlyList<OptimizationRow> _optimizationRows = [];
    private OptimizationReport? _optimizationPreview;
    private CancellationTokenSource? _optimizationCancellation;
    private List<(Control Control, bool WasEnabled)>? _optimizationControlStates;
    private bool _optimizationBusy;
    private bool _optimizationAcceptingProgress;
    private bool _closeAfterOptimization;

    // Internal so UI checks can inject a Core instance backed by an isolated fixture.
    internal StorageOptimizationService OptimizationService
    {
        get => _optimizationService;
        set => _optimizationService = value ?? throw new ArgumentNullException(nameof(value));
    }

    public IReadOnlyList<OptimizationRow> OptimizationRows
    {
        get => _optimizationRows;
        private set => Set(ref _optimizationRows, value);
    }

    private async void PreviewOptimization_Click(object sender, RoutedEventArgs e)
    {
        await AnalyzeOptimizationAsync();
    }

    private async void DeleteSelectedOptimization_Click(object sender, RoutedEventArgs e)
    {
        await DeleteOptimizationAsync(deleteAll: false);
    }

    private async void DeleteAllOptimization_Click(object sender, RoutedEventArgs e)
    {
        await DeleteOptimizationAsync(deleteAll: true);
    }

    private void SelectAllOptimization_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in OptimizationRows.Where(row => row.CanSelect)) row.IsSelected = true;
        UpdateOptimizationSelection();
    }

    private void ClearOptimizationSelection_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in OptimizationRows) row.IsSelected = false;
        UpdateOptimizationSelection();
    }

    private void OptimizationRow_SelectionChanged(object sender, RoutedEventArgs e) => UpdateOptimizationSelection();

    private void CancelOptimization_Click(object sender, RoutedEventArgs e)
    {
        _optimizationCancellation?.Cancel();
        OptimizationStatusText.Text = "취소 요청 중… 현재 파일 처리가 끝나면 멈춥니다.";
    }

    private void OptimizationOptions_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized || _optimizationBusy) return;
        InvalidateOptimizationPreview("옵션이 바뀌었습니다. 새 정리 대상 검사를 실행하세요.");
    }

    private void InvalidateOptimizationPreview(string message)
    {
        _optimizationPreview = null;
        SetOptimizationRows([]);
        OptimizationStatusText.Text = message;
        OptimizationCandidateCountText.Text = "후보 —개";
        OptimizationSelectedCountText.Text = "선택 —개";
        OptimizationSkippedCountText.Text = "건너뜀 —개";
        OptimizationFailedCountText.Text = "실패 —개";
        OptimizationCategorySummaryText.Text = "유형별 파일 수와 크기는 검사 후 표시됩니다.";
        OptimizationDeletedBytesText.Text = "후보 파일 크기: —";
        OptimizationScopeStatusText.Text = "옵션 변경 시 기존 목록은 지워집니다. 다시 검사한 뒤 후보 파일을 선택해 영구 삭제하세요.";
        OptimizationEmptyTitle.Text = "검사 목록이 없습니다";
        OptimizationEmptyDescription.Text = message;
        OptimizationEmptyState.Visibility = Visibility.Visible;
        UpdateOptimizationSelection();
    }

    private async Task AnalyzeOptimizationAsync()
    {
        if (!CanStartOptimization()) return;

        _optimizationPreview = null;
        SetOptimizationRows([]);
        OptimizationCandidateCountText.Text = "후보 확인 중";
        OptimizationSelectedCountText.Text = "선택 —개";
        OptimizationSkippedCountText.Text = "건너뜀 —개";
        OptimizationFailedCountText.Text = "실패 —개";
        OptimizationCategorySummaryText.Text = "유형별 파일 수와 크기를 확인하고 있습니다.";
        OptimizationDeletedBytesText.Text = "후보 파일 크기: 확인 중";
        OptimizationEmptyTitle.Text = "정리 대상을 검사하고 있습니다…";
        OptimizationEmptyDescription.Text = "목록을 확인하기 전에는 파일을 삭제하지 않습니다.";
        await RunOptimizationOperationAsync(async (progress, token) =>
        {
            var options = new OptimizationOptions
            {
                IncludeTemporarySubfolders = IncludeTemporarySubfoldersCheckBox.IsChecked == true,
                IncludeCrashDumps = IncludeCrashDumpsCheckBox.IsChecked == true,
                IncludeErrorReports = IncludeErrorReportsCheckBox.IsChecked == true
            };
            return await _optimizationService.AnalyzeAsync(options, progress, token);
        }, preview: true);
    }

    private async Task DeleteOptimizationAsync(bool deleteAll)
    {
        if (!CanStartOptimization() || _optimizationPreview is not { IsPreview: true, IsCanceled: false } preview)
        {
            OptimizationStatusText.Text = "먼저 정리 대상 검사를 완료하세요.";
            return;
        }

        var candidates = OptimizationRows.Where(row => row.CanSelect).ToArray();
        var selected = deleteAll ? candidates : candidates.Where(row => row.IsSelected).ToArray();
        if (selected.Length == 0)
        {
            OptimizationStatusText.Text = deleteAll ? "삭제할 후보가 없습니다." : "삭제할 후보를 먼저 선택하세요.";
            return;
        }

        var bytes = selected.Sum(row => Math.Max(0, row.SizeBytes));
        var description = deleteAll ? "검사 목록의 정리 가능 파일 전체" : "선택한 파일";
        var answer = MessageBox.Show(
            this,
            $"{description} {selected.Length:N0}개({Format.Bytes(bytes)})를 영구 삭제합니다.\n파일마다 다시 묻지 않습니다. 계속할까요?",
            "파일 영구 삭제",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning,
            MessageBoxResult.Cancel);
        if (answer != MessageBoxResult.OK) return;

        var selectedPaths = selected.Select(row => row.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        OptimizationDeletedBytesText.Text = "삭제한 파일 크기: 처리 중";
        OptimizationEmptyTitle.Text = "선택한 파일을 정리하고 있습니다…";
        OptimizationEmptyDescription.Text = "삭제 결과를 확인하고 있습니다.";
        await RunOptimizationOperationAsync(
            (progress, token) => _optimizationService.DeleteAsync(preview, selectedPaths, progress, token),
            preview: false);
    }

    private bool CanStartOptimization()
    {
        if (_optimizationBusy) return false;
        if (_duplicateBusy || !ScanButton.IsEnabled || SearchCancelButton.IsEnabled || !MetricBox.IsEnabled)
        {
            OptimizationStatusText.Text = "분석·검색·중복 검사 또는 크기 변경이 끝난 뒤 실행하세요.";
            return false;
        }
        return true;
    }

    private async Task RunOptimizationOperationAsync(
        Func<IProgress<OptimizationProgress>, CancellationToken, Task<OptimizationReport>> operation,
        bool preview)
    {
        SetOptimizationBusy(true);
        _optimizationCancellation?.Dispose();
        _optimizationCancellation = new CancellationTokenSource();
        var token = _optimizationCancellation.Token;
        _optimizationAcceptingProgress = true;
        OptimizationProgress.Visibility = Visibility.Visible;
        OptimizationProgress.IsIndeterminate = true;
        OptimizationProgress.Value = 0;
        OptimizationStatusText.Text = preview ? "정리 대상을 검사하고 있습니다…" : "선택한 파일을 삭제하고 있습니다…";
        var progress = new Progress<OptimizationProgress>(value =>
        {
            if (!_optimizationAcceptingProgress || !_optimizationBusy) return;
            var path = string.IsNullOrWhiteSpace(value.CurrentPath) ? "" : $" · {CompactPath(value.CurrentPath)}";
            var deleted = preview ? "" : $" · 삭제 {value.DeletedFiles:N0}개 · {Format.Bytes(value.DeletedBytes)}";
            OptimizationStatusText.Text = $"{value.Stage} · 확인 {value.ExaminedFiles:N0}개{deleted}{path}";
        });

        try
        {
            var report = await operation(progress, token);
            _optimizationAcceptingProgress = false;
            if (preview) _optimizationPreview = report.IsPreview && !report.IsCanceled ? report : null;
            else _optimizationPreview = null;
            ApplyOptimizationReport(report, preview);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            _optimizationAcceptingProgress = false;
            _optimizationPreview = null;
            SetOptimizationRows([]);
            OptimizationStatusText.Text = preview
                ? "검사를 취소했습니다. 부분 목록은 삭제할 수 없으므로 다시 검사하세요."
                : "취소 요청을 처리했습니다. 일부 파일이 이미 삭제됐을 수 있으니 다시 검사해 결과를 확인하세요.";
            OptimizationEmptyTitle.Text = "검사를 다시 실행하세요";
            OptimizationEmptyDescription.Text = OptimizationStatusText.Text;
            OptimizationEmptyState.Visibility = Visibility.Visible;
            OptimizationCandidateCountText.Text = "후보 —개";
            OptimizationSelectedCountText.Text = "선택 —개";
            OptimizationSkippedCountText.Text = "건너뜀 —개";
            OptimizationFailedCountText.Text = "실패 —개";
            OptimizationDeletedBytesText.Text = "삭제한 파일 크기: 결과 확인 필요";
            UpdateOptimizationSelection();
        }
        catch (Exception ex)
        {
            _optimizationAcceptingProgress = false;
            _optimizationPreview = null;
            SetOptimizationRows([]);
            OptimizationStatusText.Text = preview
                ? $"정리 대상을 검사하지 못했습니다: {ex.Message}"
                : $"삭제 결과를 완전히 확인하지 못했습니다. 다시 검사하세요: {ex.Message}";
            OptimizationEmptyTitle.Text = "정리 결과를 확인할 수 없습니다";
            OptimizationEmptyDescription.Text = OptimizationStatusText.Text;
            OptimizationEmptyState.Visibility = Visibility.Visible;
            OptimizationCandidateCountText.Text = "후보 —개";
            OptimizationSelectedCountText.Text = "선택 —개";
            OptimizationSkippedCountText.Text = "건너뜀 —개";
            OptimizationFailedCountText.Text = "실패 —개";
            OptimizationDeletedBytesText.Text = "삭제한 파일 크기: 결과 확인 필요";
            UpdateOptimizationSelection();
        }
        finally
        {
            _optimizationAcceptingProgress = false;
            OptimizationProgress.Visibility = Visibility.Collapsed;
            SetOptimizationBusy(false);
            _optimizationCancellation?.Dispose();
            _optimizationCancellation = null;
            if (_closeAfterOptimization)
            {
                _closeAfterOptimization = false;
                _ = Dispatcher.BeginInvoke(new Action(Close), DispatcherPriority.Background);
            }
        }
    }

    private void ApplyOptimizationReport(OptimizationReport report, bool preview)
    {
        var visibleItems = report.Items.Where(item =>
            preview && !report.IsCanceled && item.Status == OptimizationItemStatus.Candidate).ToArray();
        var rows = visibleItems.Select(item => new OptimizationRow(item, true, false)).ToArray();
        SetOptimizationRows(rows);

        OptimizationCandidateCountText.Text = $"후보 {report.CandidateCount:N0}개";
        OptimizationSkippedCountText.Text = $"건너뜀 {report.SkippedCount:N0}개";
        OptimizationFailedCountText.Text = $"실패 {report.FailedCount:N0}개";
        OptimizationDeletedBytesText.Text = preview
            ? $"후보 파일 크기: {Format.Bytes(report.CandidateBytes)}"
            : $"삭제한 파일 크기: {Format.Bytes(report.DeletedBytes)}";
        IEnumerable<OptimizationItemResult> summaryItems = preview ? visibleItems : report.Items;
        OptimizationCategorySummaryText.Text = string.Join("   ·   ", Enum.GetValues<OptimizationCategory>().Select(category =>
        {
            var items = summaryItems.Where(item => item.Category == category).ToArray();
            return $"{CategoryText(category)} {items.Length:N0}개 · {Format.Bytes(items.Sum(item => Math.Max(0, item.SizeBytes)))}";
        }));

        var totalSelected = rows.Count(row => row.CanSelect && row.IsSelected);
        OptimizationSelectedCountText.Text = preview ? $"선택 {totalSelected:N0}개" : "선택 —개";
        if (preview)
        {
            OptimizationStatusText.Text = report.IsCanceled
                ? $"검사 취소 · 후보 {report.CandidateCount:N0}개 · 부분 목록은 삭제할 수 없습니다."
                : report.CandidateCount == 0
                    ? "미리보기 완료 · 정리할 후보가 없습니다."
                    : $"미리보기 완료 · 후보 {report.CandidateCount:N0}개를 확인하고 삭제 대상을 선택하세요.";
            OptimizationEmptyTitle.Text = report.IsCanceled ? "검사가 취소되었습니다" : report.CandidateCount == 0 ? "정리할 후보가 없습니다" : "정리 후보 목록";
            OptimizationEmptyDescription.Text = report.IsCanceled
                ? "부분 검사 결과로는 파일을 삭제할 수 없습니다. 다시 검사하세요."
                : report.CandidateCount == 0 ? "이 설정에서 정리할 수 있는 파일을 찾지 못했습니다." : "아래 목록에서 삭제할 파일을 고르세요.";
            OptimizationDeletedBytesText.Text = $"후보 파일 크기: {Format.Bytes(report.CandidateBytes)}";
            OptimizationScopeStatusText.Text = "후보 파일은 삭제 버튼을 누른 뒤 한 번의 확인으로 영구 삭제됩니다.";
        }
        else
        {
            OptimizationStatusText.Text = report.IsCanceled
                ? $"삭제 취소 · 삭제 {report.DeletedCount:N0}개 · 건너뜀 {report.SkippedCount:N0}개 · 실패 {report.FailedCount:N0}개"
                : $"정리 완료 · 삭제 {report.DeletedCount:N0}개 · 건너뜀 {report.SkippedCount:N0}개 · 실패 {report.FailedCount:N0}개";
            OptimizationEmptyTitle.Text = report.IsCanceled ? "삭제가 취소되었습니다" : "정리 결과";
            OptimizationEmptyDescription.Text = "처리 결과는 위 요약에서 확인하세요. 다시 검사하면 남은 삭제 가능한 파일을 표시합니다.";
            OptimizationScopeStatusText.Text = "추가로 삭제할 파일을 확인하려면 다시 검사하세요.";
        }
        OptimizationEmptyState.Visibility = rows.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateOptimizationSelection();
    }

    private void SetOptimizationRows(IReadOnlyList<OptimizationRow> rows)
    {
        foreach (var row in OptimizationRows) row.PropertyChanged -= OptimizationRow_PropertyChanged;
        foreach (var row in rows) row.PropertyChanged += OptimizationRow_PropertyChanged;
        OptimizationRows = rows;
        OptimizationEmptyState.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateOptimizationSelection();
    }

    private void OptimizationRow_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(OptimizationRow.IsSelected)) UpdateOptimizationSelection();
    }

    private void UpdateOptimizationSelection()
    {
        if (!IsInitialized) return;
        var eligible = OptimizationRows.Count(row => row.CanSelect);
        var selected = OptimizationRows.Where(row => row.CanSelect && row.IsSelected).ToArray();
        OptimizationSelectedCountText.Text = _optimizationPreview is null
            ? "선택 —개"
            : $"선택 {selected.Length:N0}개 · {Format.Bytes(selected.Sum(row => Math.Max(0, row.SizeBytes)))}";
        var canDelete = !_optimizationBusy && _optimizationPreview is { IsPreview: true, IsCanceled: false };
        SelectAllOptimizationButton.IsEnabled = canDelete && eligible > 0;
        ClearOptimizationSelectionButton.IsEnabled = canDelete && selected.Length > 0;
        DeleteSelectedOptimizationButton.IsEnabled = canDelete && selected.Length > 0;
        DeleteAllOptimizationButton.IsEnabled = canDelete && eligible > 0;
    }

    private void SetOptimizationBusy(bool busy)
    {
        _optimizationBusy = busy;
        if (busy)
        {
            var controls = new Control[]
            {
                ScanButton, CancelButton, DriveBox, PathBox, BrowseButton, SearchBox, SearchModeBox, MatchCaseBox,
                SearchButton, SearchCancelButton, OptionsToggle, MetricBox, FindDuplicatesButton, CancelDuplicatesButton,
                OpenDuplicateFolderButton, DuplicateGrid, ClearDuplicateSelectionButton, DeleteDuplicatesButton,
                DuplicateFailuresToggle, PreviewOptimizationButton, SelectAllOptimizationButton,
                ClearOptimizationSelectionButton, DeleteSelectedOptimizationButton, DeleteAllOptimizationButton,
                IncludeTemporarySubfoldersCheckBox, IncludeCrashDumpsCheckBox, IncludeErrorReportsCheckBox
            };
            _optimizationControlStates = controls.Select(control => (control, control.IsEnabled)).ToList();
            foreach (var (control, _) in _optimizationControlStates) control.IsEnabled = false;
            CancelOptimizationButton.IsEnabled = true;
        }
        else
        {
            if (_optimizationControlStates is not null)
                foreach (var (control, wasEnabled) in _optimizationControlStates) control.IsEnabled = wasEnabled;
            _optimizationControlStates = null;
        }
        CancelOptimizationButton.IsEnabled = busy;
        UpdateOptimizationSelection();
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (!_optimizationBusy) return;
        e.Cancel = true;
        _closeAfterOptimization = true;
        _optimizationCancellation?.Cancel();
        OptimizationStatusText.Text = "창을 닫기 위해 현재 파일 처리가 끝나도록 취소 중입니다…";
    }

    private static string CategoryText(OptimizationCategory category) => category switch
    {
        OptimizationCategory.UserTemporary => "사용자 임시파일",
        OptimizationCategory.WindowsTemporary => "Windows 임시파일",
        OptimizationCategory.CrashDumps => "크래시 덤프",
        OptimizationCategory.ErrorReports => "오류 보고서",
        _ => category.ToString()
    };

    public sealed class OptimizationRow : INotifyPropertyChanged
    {
        private bool _isSelected;

        internal OptimizationRow(OptimizationItemResult item, bool canSelect, bool isDeleteResult)
        {
            Path = item.Path;
            ModifiedText = item.LastWriteTimeUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "—";
            CategoryText = MainWindow.CategoryText(item.Category);
            SizeBytes = item.SizeBytes;
            SizeText = Format.Bytes(item.SizeBytes);
            StatusText = isDeleteResult && item.Status == OptimizationItemStatus.Candidate ? "미선택" : item.Status switch
            {
                OptimizationItemStatus.Candidate => "후보",
                OptimizationItemStatus.Deleted => "삭제",
                OptimizationItemStatus.Skipped => "건너뜀",
                OptimizationItemStatus.Failed => "실패",
                _ => item.Status.ToString()
            };
            Reason = item.Reason;
            CanSelect = canSelect;
        }

        public string CategoryText { get; }
        public string Path { get; }
        public string Name => System.IO.Path.GetFileName(Path);
        public string ModifiedText { get; }
        public long SizeBytes { get; }
        public string SizeText { get; }
        public string StatusText { get; }
        public string Reason { get; }
        public bool CanSelect { get; }
        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (!CanSelect || _isSelected == value) return;
                _isSelected = value;
                PropertyChanged?.Invoke(this, new(nameof(IsSelected)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
