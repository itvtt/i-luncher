using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DiskAtlas.Core;
using Microsoft.Win32;

namespace DiskAtlas.App;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private readonly DiskScanner _scanner = new();
    private readonly ContentSearchService _searchService = new();
    private readonly DuplicateFinder _duplicateFinder;
    private readonly DuplicateDeletionService _duplicateDeletionService;
    private readonly Func<int, long, bool>? _duplicateConfirmation;
    private readonly Stack<ScanNode> _navigation = new();
    private readonly WindowBounds _windowBounds;
    private CancellationTokenSource? _scanCancellation;
    private CancellationTokenSource? _searchCancellation;
    private CancellationTokenSource? _duplicateCancellation;
    private int _scanGeneration;
    private int _searchGeneration;
    private int _viewGeneration;
    private int _metricGeneration;
    private int _duplicateGeneration;
    private bool _duplicateBusy;
    private bool _duplicatePipelineActive;
    private bool _duplicateSelectionWarning;
    private bool _duplicateLayoutActive;
    private GridLength _savedContentRowHeight;
    private GridLength _savedMapRowHeight;
    private double _savedMapRowMinHeight;
    private GridLength _savedResultsColumnWidth;
    private GridLength _savedExtensionColumnWidth;
    private bool _useAllocated;
    private bool _settingMetric;
    private ScanNode? _currentViewNode;
    private ScanNode? _selectedNode;
    private IReadOnlyList<FileRow> _largeFiles = [];
    private IReadOnlyList<ExtensionRow> _extensions = [];
    private IReadOnlyList<SearchResultRow> _searchResults = [];
    private IReadOnlyList<DuplicateFileRow> _duplicateRows = [];
    private IReadOnlyList<DuplicateGroup> _duplicateGroups = [];
    private IReadOnlyList<string> _duplicateFailures = [];
    private string _logicalSizeText = "—", _allocatedSizeText = "—", _fileCountText = "—", _elapsedText = "—", _scanModeText = "준비";

    public MainWindow() : this(new WindowsFileRecycler(), null) { }

    public MainWindow(IFileRecycler duplicateRecycler, Func<int, long, bool>? duplicateConfirmation = null)
    {
        ArgumentNullException.ThrowIfNull(duplicateRecycler);
        _duplicateFinder = new DuplicateFinder();
        _duplicateDeletionService = new DuplicateDeletionService(duplicateRecycler);
        _duplicateConfirmation = duplicateConfirmation;
        SearchModes = [new("파일 이름", SearchMode.FileName), new("파일 내용", SearchMode.Contents), new("이름 또는 내용", SearchMode.NameOrContents)];
        MetricChoices = [new("논리 크기 기준", false), new("할당 크기 기준", true)];
        Resources.MergedDictionaries.Add(CreateAppResources());
        InitializeComponent();
        _windowBounds = WindowBounds.Attach(this);
        DataContext = this;
        LoadDrives();
        Loaded += MainWindow_Loaded;
        PreviewKeyDown += Window_PreviewKeyDown;
        Closing += MainWindow_Closing;
        Closing += (_, _) => { _scanCancellation?.Cancel(); _searchCancellation?.Cancel(); _duplicateCancellation?.Cancel(); Interlocked.Increment(ref _duplicateGeneration); };
        StateChanged += (_, _) => MaximizeButton.Content = WindowState == WindowState.Maximized ? "❐" : "□";
    }

    internal static ResourceDictionary CreateAppResources() => new()
    {
        Source = new Uri("/DiskAtlas;component/AppResources.xaml", UriKind.Relative)
    };

    public ObservableCollection<FolderRow> FolderRows { get; } = [];
    public IReadOnlyList<FileRow> LargeFiles { get => _largeFiles; private set => Set(ref _largeFiles, value); }
    public IReadOnlyList<ExtensionRow> Extensions { get => _extensions; private set => Set(ref _extensions, value); }
    public IReadOnlyList<SearchResultRow> SearchResults { get => _searchResults; private set => Set(ref _searchResults, value); }
    public IReadOnlyList<DuplicateFileRow> DuplicateRows { get => _duplicateRows; private set => Set(ref _duplicateRows, value); }
    public IReadOnlyList<string> DuplicateFailures { get => _duplicateFailures; private set => Set(ref _duplicateFailures, value); }
    public IReadOnlyList<SearchModeChoice> SearchModes { get; }
    public IReadOnlyList<MetricBasisChoice> MetricChoices { get; }
    public ScanResult? CurrentScan { get; private set; }
    public string LogicalSizeText { get => _logicalSizeText; private set => Set(ref _logicalSizeText, value); }
    public string AllocatedSizeText { get => _allocatedSizeText; private set => Set(ref _allocatedSizeText, value); }
    public string FileCountText { get => _fileCountText; private set => Set(ref _fileCountText, value); }
    public string ElapsedText { get => _elapsedText; private set => Set(ref _elapsedText, value); }
    public string ScanModeText { get => _scanModeText; private set => Set(ref _scanModeText, value); }

    public event PropertyChangedEventHandler? PropertyChanged;

    public async Task ScanPathAsync(string path)
    {
        if (_optimizationBusy) return;
        if (string.IsNullOrWhiteSpace(path)) { ShowMessage("분석할 드라이브나 폴더를 선택하세요.", false); return; }
        string fullPath;
        try { fullPath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'))); }
        catch (Exception ex) { ShowMessage($"올바른 경로가 아닙니다: {ex.Message}", false); return; }
        if (!Directory.Exists(fullPath)) { ShowMessage("선택한 폴더가 없거나 접근할 수 없습니다.", false); return; }

        var generation = Interlocked.Increment(ref _scanGeneration);
        Interlocked.Increment(ref _metricGeneration);
        CancelDuplicateWork(clearResults: true);
        var previousMetrics = (LogicalSizeText, AllocatedSizeText, FileCountText, ElapsedText, ScanModeText);
        _scanCancellation?.Cancel();
        _searchCancellation?.Cancel();
        _scanCancellation?.Dispose();
        _scanCancellation = new();
        var token = _scanCancellation.Token;

        SetScanBusy(true);
        HideMessage();
        PathBox.Text = fullPath;
        StatusText.Text = $"{fullPath} 분석 준비 중…";
        var acceptingProgress = true;
        var progress = new Progress<ScanProgress>(p =>
        {
            if (!acceptingProgress || generation != _scanGeneration) return;
            ScanModeText = NormalizeMode(p.Mode);
            StatusText.Text = $"파일 {p.Files:N0}개 · 폴더 {p.Directories:N0}개 · {Format.Bytes(p.Bytes)} · {CompactPath(p.CurrentPath)}";
            FileCountText = p.Files.ToString("N0");
            LogicalSizeText = Format.Bytes(p.Bytes);
            ElapsedText = Format.Duration(p.Elapsed);
        });

        try
        {
            var result = await _scanner.ScanAsync(fullPath, progress, token);
            if (generation != _scanGeneration || token.IsCancellationRequested) return;

            var useAllocated = !result.Summary.AllocationIsEstimated && result.Summary.Mode.Contains("MFT", StringComparison.OrdinalIgnoreCase);
            var prepared = await Task.Run(() => PrepareScan(result, useAllocated), token);
            if (generation != _scanGeneration || token.IsCancellationRequested) return;
            acceptingProgress = false;
            ApplyScan(result, prepared, useAllocated);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            if (generation == _scanGeneration)
            {
                (LogicalSizeText, AllocatedSizeText, FileCountText, ElapsedText, ScanModeText) = previousMetrics;
                StatusText.Text = CurrentScan is null ? "분석을 취소했습니다. 위치를 선택해 다시 시도하세요." : "분석을 취소했습니다. 이전 결과는 그대로 유지됩니다.";
            }
        }
        catch (Exception ex)
        {
            if (generation == _scanGeneration)
            {
                (LogicalSizeText, AllocatedSizeText, FileCountText, ElapsedText, ScanModeText) = previousMetrics;
                ShowMessage($"분석하지 못했습니다: {ex.Message}", false);
                StatusText.Text = CurrentScan is null ? "불러온 분석 결과가 없습니다." : "분석에 실패했습니다. 이전 결과는 그대로 유지됩니다.";
            }
        }
        finally
        {
            acceptingProgress = false;
            if (generation == _scanGeneration) SetScanBusy(false);
        }
    }

    public async Task SearchAsync(string query, SearchMode mode)
    {
        if (_optimizationBusy) return;
        MainTabs.SelectedIndex = 2;
        if (!ScanButton.IsEnabled) { SearchStatusText.Text = "디스크 분석이 끝난 뒤 검색할 수 있습니다."; return; }
        if (string.IsNullOrWhiteSpace(query)) { SearchStatusText.Text = "파일 이름이나 내용 검색어를 입력하세요."; SearchBox.Focus(); return; }
        try { QueryExpression.Parse(query); }
        catch (ArgumentException error)
        {
            SearchStatusText.Text = error.Message;
            SearchStatusText.ToolTip = error.Message;
            SearchBox.Focus();
            return;
        }
        SearchStatusText.ClearValue(ToolTipProperty);
        string requestedPath;
        try { requestedPath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(PathBox.Text.Trim().Trim('"'))); }
        catch { SearchStatusText.Text = "위에서 검색할 드라이브나 폴더를 선택하세요."; return; }
        static bool SamePath(string first, string second) => string.Equals(Path.TrimEndingDirectorySeparator(first), Path.TrimEndingDirectorySeparator(second), StringComparison.OrdinalIgnoreCase);
        if (CurrentScan is null || !SamePath(CurrentScan.Root.FullPath, requestedPath))
        {
            var previousScan = CurrentScan;
            SearchStatusText.Text = "검색할 위치의 파일 목록을 준비합니다. 분석을 취소하면 검색도 중단됩니다.";
            await ScanPathAsync(requestedPath);
            if (CurrentScan is null || ReferenceEquals(CurrentScan, previousScan) || !SamePath(CurrentScan.Root.FullPath, requestedPath))
            {
                SearchStatusText.Text = "파일 목록을 준비하지 못했거나 취소했습니다. 위치를 확인하고 다시 검색하세요.";
                return;
            }
            MainTabs.SelectedIndex = 2;
        }
        var scan = CurrentScan;

        var generation = Interlocked.Increment(ref _searchGeneration);
        _searchCancellation?.Cancel();
        _searchCancellation?.Dispose();
        _searchCancellation = new();
        var token = _searchCancellation.Token;
        SearchButton.IsEnabled = false; SearchCancelButton.IsEnabled = true;
        FindDuplicatesButton.IsEnabled = false;
        SearchStatusText.Text = $"{scan.Root.FullPath} 검색 중…";
        BusyProgress.Visibility = Visibility.Visible;
        var acceptingProgress = true;
        var progress = new Progress<SearchProgress>(p =>
        {
            if (acceptingProgress && generation == _searchGeneration)
                SearchStatusText.Text = $"{p.Total:N0}개 중 {p.Examined:N0}개 확인 · 일치 {p.Matches:N0}개";
        });

        try
        {
            var options = new SearchOptions(query.Trim(), mode, MatchCaseBox.IsChecked == true, 10000);
            var report = await _searchService.SearchAsync(scan.Files, options, progress, token);
            if (generation != _searchGeneration || token.IsCancellationRequested || !ReferenceEquals(scan, CurrentScan)) return;
            var rows = await Task.Run(() => report.Hits.Select(h => new SearchResultRow(h)).ToArray(), token);
            if (generation != _searchGeneration || token.IsCancellationRequested || !ReferenceEquals(scan, CurrentScan)) return;
            SearchResults = rows;
            acceptingProgress = false;
            var limit = report.LimitReached ? " · 표시 한도 10,000개 도달" : "";
            SearchStatusText.Text = $"결과 {report.Hits.Count:N0}개 · 확인 {report.Examined:N0}개 · 시스템 제외 {report.Excluded:N0}개 · {Format.Duration(report.Elapsed)}{limit}";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            if (generation == _searchGeneration) SearchStatusText.Text = "검색을 취소했습니다. 이전 결과는 그대로 유지됩니다.";
        }
        catch (Exception ex)
        {
            if (generation == _searchGeneration) SearchStatusText.Text = $"검색하지 못했습니다: {ex.Message}";
        }
        finally
        {
            acceptingProgress = false;
            if (generation == _searchGeneration)
            {
                SearchButton.IsEnabled = ScanButton.IsEnabled; SearchCancelButton.IsEnabled = false;
                FindDuplicatesButton.IsEnabled = ScanButton.IsEnabled && !_duplicatePipelineActive;
                if (_scanCancellation is null || _scanCancellation.IsCancellationRequested || ScanButton.IsEnabled) BusyProgress.Visibility = Visibility.Collapsed;
            }
        }
    }

    private void ApplyScan(ScanResult scan, PreparedScan prepared, bool useAllocated)
    {
        CurrentScan = scan;
        _useAllocated = useAllocated;
        _settingMetric = true;
        MetricBox.SelectedItem = MetricChoices[useAllocated ? 1 : 0];
        MetricBox.ToolTip = scan.Summary.AllocationIsEstimated
            ? "현재 폴더 스캔의 할당 크기는 클러스터 단위 추정값입니다. 표와 디스크 맵의 크기 기준을 선택합니다."
            : "표와 디스크 맵의 면적 및 정렬 기준";
        _settingMetric = false;
        LogicalSizeText = Format.Bytes(scan.Root.Size);
        AllocatedSizeText = Format.Bytes(scan.Root.AllocatedSize);
        FileCountText = scan.Summary.Files.ToString("N0");
        ElapsedText = Format.Duration(scan.Summary.Elapsed);
        ScanModeText = NormalizeMode(scan.Summary.Mode);
        LargeFiles = prepared.LargeFiles;
        Extensions = prepared.Extensions;
        SearchResults = [];
        SearchStatusText.Text = "선택한 위치에서 검색 · 시스템 파일 제외";
        _navigation.Clear();
        ShowFolder(scan.Root, false);

        StatusText.Text = $"분석 완료 · 폴더 {scan.Summary.Directories:N0}개 · 파일 {scan.Summary.Files:N0}개";
        HideMessage();
    }

    private static long Metric(ScanNode node, bool allocated) => allocated ? node.AllocatedSize : node.Size;

    private static PreparedScan PrepareScan(ScanResult scan, bool useAllocated)
    {
        var files = scan.Files.OrderByDescending(f => Metric(f, useAllocated)).Take(10000).Select(f => new FileRow(f)).ToArray();
        var total = Math.Max(1L, scan.Files.Sum(f => Math.Max(0, Metric(f, useAllocated))));
        var extensions = scan.Files.GroupBy(f => string.IsNullOrEmpty(f.Extension) ? "(확장자 없음)" : f.Extension)
            .Select(g => new ExtensionRow { Extension = g.Key, Count = g.LongCount(), Size = g.Sum(f => Math.Max(0, Metric(f, useAllocated))), Percent = 100d * g.Sum(f => Math.Max(0, Metric(f, useAllocated))) / total })
            .OrderByDescending(x => x.Size).ThenBy(x => x.Extension).ToArray();
        return new(files, extensions);
    }

    private void ShowFolder(ScanNode node, bool rememberCurrent = true)
    {
        if (!node.IsDirectory) node = node.Parent ?? CurrentScan?.Root ?? node;
        if (rememberCurrent && _currentViewNode is not null && !ReferenceEquals(_currentViewNode, node)) _navigation.Push(_currentViewNode);
        _currentViewNode = node;
        BackButton.IsEnabled = _navigation.Count > 0;
        FolderRows.Clear();
        var rootRow = new FolderRow(node, 0) { IsExpanded = true };
        FolderRows.Add(rootRow);
        foreach (var child in SortedDirectories(node)) FolderRows.Add(new(child, 1));
        FolderGrid.SelectedItem = rootRow;
        UpdateBreadcrumbs(node);
        UpdateTreemap(node);
        _ = UpdateExtensionScopeAsync(node);
    }

    private async Task UpdateExtensionScopeAsync(ScanNode node)
    {
        var generation = Interlocked.Increment(ref _viewGeneration);
        bool useAllocated = _useAllocated;
        var rows = await Task.Run(() =>
        {
            var files = DescendantFiles(node).ToArray();
            var total = Math.Max(1L, files.Sum(f => Math.Max(0, Metric(f, useAllocated))));
            return files.GroupBy(f => string.IsNullOrEmpty(f.Extension) ? "(확장자 없음)" : f.Extension)
                .Select(g => new ExtensionRow { Extension = g.Key, Count = g.LongCount(), Size = g.Sum(f => Math.Max(0, Metric(f, useAllocated))), Percent = 100d * g.Sum(f => Math.Max(0, Metric(f, useAllocated))) / total })
                .OrderByDescending(x => x.Size).ThenBy(x => x.Extension).ToArray();
        });
        if (generation != _viewGeneration || useAllocated != _useAllocated || !ReferenceEquals(node, _currentViewNode)) return;
        Extensions = rows;
        ExtensionScopeText.Text = (ReferenceEquals(node, CurrentScan?.Root) ? "전체 파일" : node.Name) + (_useAllocated ? CurrentScan?.Summary.AllocationIsEstimated == true ? " · 할당(추정)" : " · 할당" : " · 논리");
    }

    private static IEnumerable<ScanNode> DescendantFiles(ScanNode node)
    {
        var stack = new Stack<ScanNode>(); stack.Push(node);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            foreach (var child in current.Children)
                if (child.IsDirectory) stack.Push(child); else yield return child;
        }
    }

    private void UpdateTreemap(ScanNode node)
    {
        Treemap.SetRoot(node, _useAllocated, _selectedNode);
        var basis = _useAllocated ? CurrentScan?.Summary.AllocationIsEstimated == true ? "할당 크기(추정)" : "할당 크기" : "논리 크기";
        TreemapScopeText.Text = $"{node.Name} · {basis} · 하위 파일 {node.FileCount:N0}개 · 최대 1,500개";
    }

    private void UpdateBreadcrumbs(ScanNode node)
    {
        BreadcrumbPanel.Children.Clear();
        var chain = new Stack<ScanNode>();
        for (var current = node; current is not null; current = current.Parent) chain.Push(current);
        while (chain.Count > 0)
        {
            var crumb = chain.Pop();
            var button = new Button { Content = string.IsNullOrWhiteSpace(crumb.Name) ? crumb.FullPath : crumb.Name, Tag = crumb, Style = (Style)FindResource("ActionButton"), Background = System.Windows.Media.Brushes.Transparent, BorderThickness = new Thickness(0), Width = double.NaN, Height = 30, Padding = new Thickness(8, 0, 8, 0) };
            button.Click += (_, _) => ShowFolder((ScanNode)button.Tag);
            BreadcrumbPanel.Children.Add(button);
            if (chain.Count > 0) BreadcrumbPanel.Children.Add(new TextBlock { Text = "›", Foreground = (System.Windows.Media.Brush)FindResource("MutedBrush"), VerticalAlignment = VerticalAlignment.Center });
        }
    }

    private IEnumerable<ScanNode> SortedDirectories(ScanNode node) => node.Children.Where(x => x.IsDirectory).OrderByDescending(NodeMetric).ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase);
    private long NodeMetric(ScanNode node) => _useAllocated ? node.AllocatedSize : node.Size;

    private void FolderExpander_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is not FolderRow row) return;
        var index = FolderRows.IndexOf(row); if (index < 0) return;
        if (row.IsExpanded)
        {
            row.IsExpanded = false;
            while (index + 1 < FolderRows.Count && FolderRows[index + 1].Level > row.Level) FolderRows.RemoveAt(index + 1);
        }
        else
        {
            row.IsExpanded = true;
            foreach (var child in SortedDirectories(row.Node).Reverse()) FolderRows.Insert(index + 1, new(child, row.Level + 1));
        }
        e.Handled = true;
    }

    private void SelectNode(ScanNode? node)
    {
        _selectedNode = node; Treemap.SelectNode(node);
        SelectionText.Text = node is null ? "" : $"{Format.Bytes(node.Size)} · {node.FullPath}";
    }

    private void LoadDrives()
    {
        var drives = new List<DriveItem>();
        try
        {
            foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady))
                drives.Add(new($"{drive.Name.TrimEnd('\\')}  {Format.Bytes(drive.AvailableFreeSpace)} 여유", drive.RootDirectory.FullName));
        }
        catch { }
        DriveBox.ItemsSource = drives;
        if (drives.Count > 0) DriveBox.SelectedIndex = 0;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        var args = Environment.GetCommandLineArgs().Skip(1).ToArray();
        string? path = null; var scan = false;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i].Equals("--path", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length) path = args[++i];
            else if (args[i].Equals("--scan", StringComparison.OrdinalIgnoreCase)) scan = true;
        }
        if (!string.IsNullOrWhiteSpace(path)) PathBox.Text = path;
        if (scan && !string.IsNullOrWhiteSpace(path)) await ScanPathAsync(path);
    }

    public async Task FindDuplicatesAsync()
    {
        if (_optimizationBusy || _duplicateBusy || _duplicatePipelineActive || !ScanButton.IsEnabled || !SearchButton.IsEnabled) return;
        MainTabs.SelectedItem = DuplicatesTab;
        _duplicatePipelineActive = true;
        try
        {
            string requestedPath;
            try { requestedPath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(PathBox.Text.Trim().Trim('"'))); }
            catch { DuplicateStatusText.Text = "중복을 찾을 위치를 선택하세요."; return; }
            if (!Directory.Exists(requestedPath))
            {
                DuplicateStatusText.Text = "선택한 폴더가 없거나 접근할 수 없습니다.";
                return;
            }

            static bool SamePath(string first, string second) => string.Equals(Path.TrimEndingDirectorySeparator(first), Path.TrimEndingDirectorySeparator(second), StringComparison.OrdinalIgnoreCase);
            var scan = CurrentScan;
            if (scan is null || !SamePath(scan.Root.FullPath, requestedPath))
            {
                var previousScan = scan;
                DuplicateStatusText.Text = "중복 검사를 위해 선택한 위치를 먼저 분석합니다. 분석을 취소하면 중복 검사도 중단됩니다.";
                await ScanPathAsync(requestedPath);
                if (CurrentScan is not { } completedScan || ReferenceEquals(completedScan, previousScan) || !SamePath(completedScan.Root.FullPath, requestedPath))
                {
                    DuplicateStatusText.Text = "파일 목록을 준비하지 못했거나 취소했습니다. 위치를 확인하고 다시 찾으세요.";
                    return;
                }
                scan = completedScan;
            }

            var generation = Interlocked.Increment(ref _duplicateGeneration);
            _duplicateCancellation?.Cancel();
            _duplicateCancellation?.Dispose();
            _duplicateCancellation = new();
            var token = _duplicateCancellation.Token;
            SetDuplicateBusy(true);
            DuplicateProgress.Visibility = Visibility.Visible;
            DuplicateProgress.IsIndeterminate = false;
            DuplicateProgress.Maximum = Math.Max(1, scan.Files.Count);
            DuplicateProgress.Value = 0;
            DuplicateStatusText.Text = $"파일 {scan.Files.Count:N0}개의 크기와 내용을 비교할 준비 중…";
            var acceptingProgress = true;
            var progress = new Progress<DuplicateProgress>(p =>
            {
                if (!acceptingProgress || generation != _duplicateGeneration) return;
                DuplicateProgress.Maximum = Math.Max(1, p.TotalFiles);
                DuplicateProgress.Value = Math.Min(DuplicateProgress.Maximum, Math.Max(0, p.ProcessedFiles));
                DuplicateStatusText.Text = $"{p.Stage} · {p.TotalFiles:N0}개 중 {p.ProcessedFiles:N0}개 · {Format.Bytes(p.ProcessedBytes)} · {CompactPath(p.CurrentPath)}";
            });

            try
            {
                var result = await _duplicateFinder.FindAsync(scan.Files, progress, token);
                if (generation != _duplicateGeneration || token.IsCancellationRequested || !ReferenceEquals(scan, CurrentScan)) return;
                var groups = result.Groups.OrderByDescending(group => group.ReclaimableBytes).ThenBy(group => group.Id, StringComparer.Ordinal).ToArray();
                var rows = groups.SelectMany((group, index) => group.Files
                    .OrderBy(file => file.FullPath, StringComparer.CurrentCultureIgnoreCase)
                    .Select(file => new DuplicateFileRow(file, group.Id, $"그룹 {index + 1} · {group.Files.Count}개", group.Files.Count)))
                    .ToArray();
                foreach (var row in rows) row.SelectionChanged += DuplicateRow_SelectionChanged;
                _duplicateGroups = groups;
                DuplicateRows = rows;
                DuplicateFailures = [];
                _duplicateSelectionWarning = false;
                acceptingProgress = false;
                var reclaimable = groups.Sum(group => group.ReclaimableBytes);
                DuplicateSummaryText.Text = $"그룹 {groups.Length:N0}개 · 파일 {rows.Length:N0}개 · 확보 가능 {Format.Bytes(reclaimable)}";
                DuplicateStatusText.Text = $"중복 검사 완료 · 제외/읽기 실패 {result.SkippedFiles:N0}개 · {Format.Duration(result.Elapsed)} · 시스템 파일 제외 적용 · 삭제할 파일을 직접 선택하세요.";
                DuplicateStatusText.ToolTip = DuplicateStatusText.Text;
                UpdateDuplicateSelection();
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                if (generation == _duplicateGeneration)
                    DuplicateStatusText.Text = DuplicateRows.Count == 0 ? "중복 검사를 취소했습니다." : "중복 검사를 취소했습니다. 이전 결과는 그대로 유지됩니다.";
            }
            catch (Exception ex)
            {
                if (generation == _duplicateGeneration) DuplicateStatusText.Text = $"중복 파일을 찾지 못했습니다: {ex.Message}";
            }
            finally
            {
                acceptingProgress = false;
                if (generation == _duplicateGeneration) SetDuplicateBusy(false);
            }
        }
        finally
        {
            _duplicatePipelineActive = false;
            if (!_duplicateBusy && IsInitialized) FindDuplicatesButton.IsEnabled = ScanButton.IsEnabled;
        }
    }

    public async Task DeleteSelectedDuplicatesAsync()
    {
        if (_optimizationBusy) return;
        if (_duplicateBusy) return;
        var selectedRows = DuplicateRows.Where(row => row.IsSelected).ToArray();
        if (selectedRows.Length == 0)
        {
            DuplicateStatusText.Text = "휴지통으로 이동할 파일을 먼저 선택하세요.";
            return;
        }

        var selectedPaths = selectedRows.Select(row => row.FullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unsafeGroups = _duplicateGroups.Where(group => group.Files.All(file => selectedPaths.Contains(file.FullPath))).ToArray();
        if (unsafeGroups.Length > 0)
        {
            _duplicateSelectionWarning = true;
            DuplicateStatusText.Text = $"그룹 {unsafeGroups.Length:N0}개에서 모든 파일이 선택되었습니다. 각 그룹에 보관할 파일을 최소 1개 남기세요.";
            return;
        }

        var selectedBytes = selectedRows.Sum(row => Math.Max(0, row.Size));
        if (!ConfirmDuplicateDeletion(selectedRows.Length, selectedBytes)) return;
        _duplicateSelectionWarning = false;

        var generation = Interlocked.Increment(ref _duplicateGeneration);
        _duplicateCancellation?.Cancel();
        _duplicateCancellation?.Dispose();
        _duplicateCancellation = new();
        var token = _duplicateCancellation.Token;
        var rescanPath = CurrentScan?.Root.FullPath;
        SetDuplicateBusy(true);
        DuplicateProgress.Visibility = Visibility.Visible;
        DuplicateProgress.IsIndeterminate = false;
        DuplicateProgress.Maximum = Math.Max(1, selectedRows.Length);
        DuplicateProgress.Value = 0;
        var acceptingProgress = true;
        var progress = new Progress<DuplicateProgress>(p =>
        {
            if (!acceptingProgress || generation != _duplicateGeneration) return;
            DuplicateProgress.Maximum = Math.Max(1, p.TotalFiles);
            DuplicateProgress.Value = Math.Min(DuplicateProgress.Maximum, Math.Max(0, p.ProcessedFiles));
            DuplicateStatusText.Text = $"{p.Stage} · {p.TotalFiles:N0}개 중 {p.ProcessedFiles:N0}개 · {CompactPath(p.CurrentPath)}";
        });
        string outcome;
        var refreshScan = false;

        try
        {
            var result = await _duplicateDeletionService.DeleteAsync(_duplicateGroups, selectedPaths, progress, token);
            if (generation != _duplicateGeneration) return;
            acceptingProgress = false;
            refreshScan = result.DeletedPaths.Count > 0;
            SetDuplicateFailures(result.Failures);
            if (!refreshScan) foreach (var row in DuplicateRows) row.Error = result.Failures.TryGetValue(row.FullPath, out var error) ? error : "";
            outcome = DeleteOutcome(result.DeletedPaths.Count, result.Failures, token.IsCancellationRequested);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            if (generation != _duplicateGeneration) return;
            refreshScan = true;
            outcome = "휴지통 이동을 취소했습니다. 취소 전에 일부 파일이 이동되었을 수 있어 현재 위치를 다시 분석합니다.";
        }
        catch (Exception ex)
        {
            if (generation != _duplicateGeneration) return;
            outcome = $"파일을 휴지통으로 이동하지 못했습니다: {ex.Message}";
        }
        finally
        {
            acceptingProgress = false;
            if (generation == _duplicateGeneration) SetDuplicateBusy(false);
        }

        DuplicateStatusText.Text = outcome;
        DuplicateStatusText.ToolTip = outcome;
        if (refreshScan && !string.IsNullOrWhiteSpace(rescanPath))
        {
            var failures = DuplicateFailures;
            ClearDuplicateResults();
            await ScanPathAsync(rescanPath);
            MainTabs.SelectedItem = DuplicatesTab;
            DuplicateFailures = failures;
            DuplicateStatusText.Text = outcome;
            DuplicateStatusText.ToolTip = outcome;
        }
    }

    private bool ConfirmDuplicateDeletion(int count, long bytes)
    {
        if (_duplicateConfirmation is not null) return _duplicateConfirmation(count, bytes);
        return MessageBox.Show(this,
            $"선택한 파일 {count:N0}개({Format.Bytes(bytes)})를 휴지통으로 이동할까요?\n각 중복 그룹에는 보관할 파일이 최소 1개 남습니다.",
            "중복 파일을 휴지통으로 이동", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) == MessageBoxResult.OK;
    }

    private static string DeleteOutcome(int deletedCount, IReadOnlyDictionary<string, string> failures, bool cancelled)
    {
        var summary = cancelled
            ? $"취소됨 · 취소 전 휴지통으로 이동 {deletedCount:N0}개"
            : $"휴지통으로 이동 {deletedCount:N0}개";
        if (failures.Count == 0) return summary + " · 현재 위치를 다시 분석합니다.";
        return $"{summary} · 실패 {failures.Count:N0}개 · 아래 ‘실패’ 목록에서 파일별 이유를 확인하세요.";
    }

    private void SetDuplicateFailures(IReadOnlyDictionary<string, string> failures) => DuplicateFailures = failures
        .OrderBy(pair => pair.Key, StringComparer.CurrentCultureIgnoreCase)
        .Select(pair => $"{pair.Key} — {pair.Value}")
        .ToArray();

    private void CancelDuplicateWork(bool clearResults)
    {
        Interlocked.Increment(ref _duplicateGeneration);
        _duplicateCancellation?.Cancel();
        _duplicateBusy = false;
        if (clearResults) ClearDuplicateResults();
    }

    private void ClearDuplicateResults()
    {
        foreach (var row in DuplicateRows) row.SelectionChanged -= DuplicateRow_SelectionChanged;
        DuplicateRows = [];
        _duplicateGroups = [];
        DuplicateFailures = [];
        _duplicateSelectionWarning = false;
        if (IsInitialized)
        {
            DuplicateSummaryText.Text = "선택한 위치에서 중복을 찾습니다 · 시스템 파일 제외";
            DuplicateStatusText.Text = "현재 분석 결과가 바뀌었습니다. 중복 파일을 다시 찾으세요.";
            DuplicateStatusText.ToolTip = DuplicateStatusText.Text;
            UpdateDuplicateSelection();
        }
    }

    private void SetDuplicateBusy(bool busy)
    {
        _duplicateBusy = busy;
        ScanButton.IsEnabled = !busy;
        CancelButton.IsEnabled = false;
        DriveBox.IsEnabled = !busy;
        PathBox.IsEnabled = !busy;
        BrowseButton.IsEnabled = !busy;
        SearchBox.IsEnabled = !busy;
        SearchModeBox.IsEnabled = !busy;
        MatchCaseBox.IsEnabled = !busy;
        SearchButton.IsEnabled = !busy;
        SearchCancelButton.IsEnabled = false;
        MetricBox.IsEnabled = !busy;
        FindDuplicatesButton.IsEnabled = !busy && !_duplicatePipelineActive;
        CancelDuplicatesButton.IsEnabled = busy;
        DuplicateGrid.IsEnabled = !busy;
        OpenDuplicateFolderButton.IsEnabled = !busy && DuplicateGrid.SelectedItem is DuplicateFileRow;
        DuplicateProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        UpdateDuplicateSelection();
    }

    private void DuplicateRow_SelectionChanged(object? sender, EventArgs e)
    {
        UpdateDuplicateSelection();
        if (_duplicateSelectionWarning && !HasFullySelectedDuplicateGroup())
        {
            _duplicateSelectionWarning = false;
            DuplicateStatusText.Text = DuplicateRows.Any(row => row.IsSelected)
                ? "선택한 파일은 확인 후 휴지통으로 이동합니다. 각 그룹에는 보관할 파일이 남습니다."
                : "삭제할 파일을 직접 선택하세요. 항목은 자동으로 선택되지 않습니다.";
            DuplicateStatusText.ToolTip = DuplicateStatusText.Text;
        }
    }

    private bool HasFullySelectedDuplicateGroup()
    {
        var selected = DuplicateRows.Where(row => row.IsSelected).Select(row => row.FullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return _duplicateGroups.Any(group => group.Files.Count > 0 && group.Files.All(file => selected.Contains(file.FullPath)));
    }

    private void MainTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized || !ReferenceEquals(e.Source, MainTabs)) return;
        var useExpandedLayout = ReferenceEquals(MainTabs.SelectedItem, DuplicatesTab) || ReferenceEquals(MainTabs.SelectedItem, OptimizationTab);
        if (useExpandedLayout == _duplicateLayoutActive) return;

        if (useExpandedLayout)
        {
            _savedContentRowHeight = MainContentRow.Height;
            _savedMapRowHeight = DiskMapRow.Height;
            _savedMapRowMinHeight = DiskMapRow.MinHeight;
            _savedResultsColumnWidth = ResultsColumn.Width;
            _savedExtensionColumnWidth = ExtensionColumn.Width;
            MainContentRow.Height = new GridLength(1, GridUnitType.Star);
            MapSplitterRow.Height = new GridLength(0);
            DiskMapRow.MinHeight = 0;
            DiskMapRow.Height = new GridLength(0);
            ResultsColumn.Width = new GridLength(1, GridUnitType.Star);
            ResultsSplitterColumn.Width = new GridLength(0);
            ExtensionColumn.Width = new GridLength(0);
            MapSplitter.Visibility = Visibility.Collapsed;
            DiskMapPanel.Visibility = Visibility.Collapsed;
            ResultsSplitter.Visibility = Visibility.Collapsed;
            ExtensionPanel.Visibility = Visibility.Collapsed;
        }
        else
        {
            MainContentRow.Height = _savedContentRowHeight;
            MapSplitterRow.Height = new GridLength(5);
            DiskMapRow.MinHeight = _savedMapRowMinHeight;
            DiskMapRow.Height = _savedMapRowHeight;
            ResultsColumn.Width = _savedResultsColumnWidth;
            ResultsSplitterColumn.Width = new GridLength(8);
            ExtensionColumn.Width = _savedExtensionColumnWidth;
            MapSplitter.Visibility = Visibility.Visible;
            DiskMapPanel.Visibility = Visibility.Visible;
            ResultsSplitter.Visibility = Visibility.Visible;
            ExtensionPanel.Visibility = Visibility.Visible;
        }
        _duplicateLayoutActive = useExpandedLayout;
    }

    private void UpdateDuplicateSelection()
    {
        if (!IsInitialized) return;
        var selected = DuplicateRows.Where(row => row.IsSelected).ToArray();
        var actionsAvailable = !_duplicateBusy && ScanButton.IsEnabled;
        DuplicateSelectionText.Text = $"선택 {selected.Length:N0}개 · {Format.Bytes(selected.Sum(row => Math.Max(0, row.Size)))}";
        ClearDuplicateSelectionButton.IsEnabled = actionsAvailable && selected.Length > 0;
        DeleteDuplicatesButton.IsEnabled = actionsAvailable && selected.Length > 0;
    }

    private void SetScanBusy(bool busy)
    {
        ScanButton.IsEnabled = !busy; CancelButton.IsEnabled = busy; DriveBox.IsEnabled = !busy; PathBox.IsEnabled = !busy; BrowseButton.IsEnabled = !busy;
        SearchBox.IsEnabled = !busy; SearchModeBox.IsEnabled = !busy; MatchCaseBox.IsEnabled = !busy; SearchButton.IsEnabled = !busy; SearchCancelButton.IsEnabled = false; MetricBox.IsEnabled = !busy;
        FindDuplicatesButton.IsEnabled = !busy && !_duplicatePipelineActive; CancelDuplicatesButton.IsEnabled = busy && _duplicatePipelineActive;
        DuplicateGrid.IsEnabled = !busy;
        OpenDuplicateFolderButton.IsEnabled = !busy && DuplicateGrid.SelectedItem is DuplicateFileRow;
        UpdateDuplicateSelection();
        BusyProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }
    private void ShowMessage(string message, bool _) { MessageText.Text = message; MessageBar.Visibility = Visibility.Visible; }
    private void HideMessage() { MessageBar.Visibility = Visibility.Collapsed; }
    private static string NormalizeMode(string? mode) => string.IsNullOrWhiteSpace(mode) ? "폴더 스캔" : mode.Contains("MFT", StringComparison.OrdinalIgnoreCase) ? "MFT" : "폴더 스캔";
    private static string CompactPath(string path) => path.Length <= 75 ? path : "…" + path[^74..];

    private async void Scan_Click(object sender, RoutedEventArgs e) => await ScanPathAsync(PathBox.Text);
    private void Cancel_Click(object sender, RoutedEventArgs e) => _scanCancellation?.Cancel();
    private async void Search_Click(object sender, RoutedEventArgs e) => await SearchAsync(SearchBox.Text, SearchModeBox.SelectedItem is SearchModeChoice choice ? choice.Mode : SearchMode.NameOrContents);
    private void ExtensionGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var extension = (ExtensionGrid.SelectedItem as ExtensionRow)?.Extension;
        Treemap.HighlightExtension(extension);
        ClearTypeHighlightButton.Visibility = extension is null ? Visibility.Collapsed : Visibility.Visible;
    }
    private void ClearTypeHighlight_Click(object sender, RoutedEventArgs e) => ExtensionGrid.SelectedItem = null;
    private void SearchCancel_Click(object sender, RoutedEventArgs e) => _searchCancellation?.Cancel();
    private async void FindDuplicates_Click(object sender, RoutedEventArgs e) => await FindDuplicatesAsync();
    private void CancelDuplicates_Click(object sender, RoutedEventArgs e) => CancelDuplicatePipeline();
    private void CancelDuplicatePipeline()
    {
        _duplicateCancellation?.Cancel();
        if (_duplicatePipelineActive) _scanCancellation?.Cancel();
    }
    private async void DeleteDuplicates_Click(object sender, RoutedEventArgs e) => await DeleteSelectedDuplicatesAsync();
    private void ClearDuplicateSelection_Click(object sender, RoutedEventArgs e) { foreach (var row in DuplicateRows) row.IsSelected = false; }
    private void DuplicateGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        OpenDuplicateFolderButton.IsEnabled = !_duplicateBusy && DuplicateGrid.SelectedItem is DuplicateFileRow;
        if (DuplicateGrid.SelectedItem is DuplicateFileRow row) SelectNode(row.Node);
    }
    private void OpenDuplicateFolder_Click(object sender, RoutedEventArgs e) { if (DuplicateGrid.SelectedItem is DuplicateFileRow row) OpenPath(row.Node); }
    private async void PathBox_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { e.Handled = true; await ScanPathAsync(PathBox.Text); } }
    private async void SearchBox_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { e.Handled = true; await SearchAsync(SearchBox.Text, SearchModeBox.SelectedItem is SearchModeChoice choice ? choice.Mode : SearchMode.NameOrContents); } }
    private async void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            OptionsToggle.IsChecked = false;
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            OptionsToggle.IsChecked = false;
            if (CancelButton.IsEnabled) _scanCancellation?.Cancel();
            if (SearchCancelButton.IsEnabled) _searchCancellation?.Cancel();
            if (CancelDuplicatesButton.IsEnabled) CancelDuplicatePipeline();
            e.Handled = true;
        }
        else if (e.Key == Key.F5 && ScanButton.IsEnabled)
        {
            e.Handled = true;
            await ScanPathAsync(PathBox.Text);
        }
    }
    private void DriveBox_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (DriveBox.SelectedItem is DriveItem d && ScanButton.IsEnabled) PathBox.Text = d.Path; }
    private async void MetricBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_optimizationBusy || _settingMetric || CurrentScan is not { } scan || MetricBox.SelectedItem is not MetricBasisChoice choice || choice.UseAllocated == _useAllocated) return;
        _useAllocated = choice.UseAllocated;
        var generation = Interlocked.Increment(ref _metricGeneration);
        MetricBox.IsEnabled = false;
        try
        {
            var prepared = await Task.Run(() => PrepareScan(scan, _useAllocated));
            if (generation != _metricGeneration || !ReferenceEquals(scan, CurrentScan)) return;
            LargeFiles = prepared.LargeFiles;
            var view = _currentViewNode ?? scan.Root;
            if (ReferenceEquals(view, scan.Root)) Extensions = prepared.Extensions;
            ShowFolder(view, false);
            StatusText.Text = $"크기 기준을 {(_useAllocated ? scan.Summary.AllocationIsEstimated ? "할당 크기(추정)" : "할당 크기" : "논리 크기")}로 변경했습니다.";
        }
        catch (Exception ex) { ShowMessage($"크기 기준을 변경하지 못했습니다: {ex.Message}", false); }
        finally { if (generation == _metricGeneration) MetricBox.IsEnabled = ScanButton.IsEnabled; }
    }
    private void Browse_Click(object sender, RoutedEventArgs e) { var dialog = new OpenFolderDialog { Title = "분석할 폴더 선택", InitialDirectory = Directory.Exists(PathBox.Text) ? PathBox.Text : null }; if (dialog.ShowDialog(this) == true) PathBox.Text = dialog.FolderName; }
    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Back_Click(object sender, RoutedEventArgs e) { if (_navigation.TryPop(out var node)) ShowFolder(node, false); }
    private void FolderGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (FolderGrid.SelectedItem is FolderRow row) SelectNode(row.Node); }
    private void FileGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (LargeFilesGrid.SelectedItem is FileRow row) SelectNode(row.Node); }
    private void SearchGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (SearchResultsGrid.SelectedItem is SearchResultRow row) SelectNode(row.Node); }
    private void FolderGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e) { if (FolderGrid.SelectedItem is FolderRow row) ShowFolder(row.Node); }
    private void Grid_MouseDoubleClick(object sender, MouseButtonEventArgs e) { if ((sender as DataGrid)?.SelectedItem is IPathItem item) OpenPath(item.Node); }
    private void Treemap_NodeSelected(object? sender, ScanNodeEventArgs e) => SelectNode(e.Node);
    private void Treemap_FolderInvoked(object? sender, ScanNodeEventArgs e) => ShowFolder(e.Node);

    private void OpenLocation_Click(object sender, RoutedEventArgs e) { if (ContextItem(sender) is { } item) OpenPath(item.Node); }
    private void SelectInExplorer_Click(object sender, RoutedEventArgs e) { if (ContextItem(sender) is { } item) SelectInExplorer(item.Node); }
    private void CopyPath_Click(object sender, RoutedEventArgs e) { if (ContextItem(sender) is { } item) try { Clipboard.SetText(item.Node.FullPath); StatusText.Text = "경로를 클립보드에 복사했습니다."; } catch (Exception ex) { StatusText.Text = $"경로를 복사하지 못했습니다: {ex.Message}"; } }
    private static IPathItem? ContextItem(object sender) => (sender as MenuItem)?.DataContext as IPathItem;
    private void OpenPath(ScanNode node)
    {
        try { var target = node.IsDirectory ? node.FullPath : Path.GetDirectoryName(node.FullPath); if (!string.IsNullOrWhiteSpace(target) && Directory.Exists(target)) Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); else StatusText.Text = "선택한 위치를 찾을 수 없습니다."; }
        catch (Exception ex) { ShowMessage($"위치를 열지 못했습니다: {ex.Message}", false); }
    }
    private void SelectInExplorer(ScanNode node)
    {
        try { if (node.IsDirectory) Process.Start(new ProcessStartInfo(node.FullPath) { UseShellExecute = true }); else Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true, Arguments = $"/select,\"{node.FullPath}\"" }); }
        catch (Exception ex) { ShowMessage($"탐색기에서 선택하지 못했습니다: {ex.Message}", false); }
    }
    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value; PropertyChanged?.Invoke(this, new(name));
    }

    private sealed record PreparedScan(IReadOnlyList<FileRow> LargeFiles, IReadOnlyList<ExtensionRow> Extensions);
}
