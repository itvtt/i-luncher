using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using Microsoft.Win32;
using SwiftCopy.Core;

namespace SwiftCopy.App;

public sealed record SourceEntry(string FullPath)
{
    public string Name => (Directory.Exists(FullPath) ? "▣  " : "▤  ") + Path.GetFileName(FullPath);
}

public partial class MainWindow : Window
{
    public ObservableCollection<WorkerLaneViewModel> WorkerLanes { get; } = [];

    private void Minimize_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
    private void Maximize_Click(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
        else SystemCommands.MaximizeWindow(this);
    }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void Window_StateChanged(object? sender, EventArgs e)
    {
        if (MaximizeIcon == null) return;
        bool maximized = WindowState == WindowState.Maximized;
        MaximizeIcon.Data = System.Windows.Media.Geometry.Parse(maximized
            ? "M 3,1 L 11,1 11,9 M 1,3 L 9,3 9,11 1,11 Z"
            : "M 1,1 L 11,1 11,11 1,11 Z");
        string label = maximized ? "이전 크기로 복원" : "최대화";
        MaximizeButton.ToolTip = label;
        System.Windows.Automation.AutomationProperties.SetName(MaximizeButton, label);
    }

    private readonly ObservableCollection<SourceEntry> sources = [];
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private TransferSession? session;
    private CancellationTokenSource? cancellation;
    private bool running, closeWhenStopped, validating, transferIsMove;
    private enum WorkState { Ready, Validating, Scanning, Transferring, Paused, Cancelling, Complete, Cancelled, Failed }
    private WorkState? workState;
    private long lastBytes;
    private TimeSpan lastTime;
    private double speed;
    private long lastCopiedFiles;
    private double fileRate;
    private string? logPath;

    public MainWindow()
    {
        Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/SwiftCopy;component/AppResources.xaml", UriKind.Relative)
        });
        InitializeComponent();
        TaskbarItemInfo = new TaskbarItemInfo();
        SetWorkState(WorkState.Ready);
        SourceList.ItemsSource = sources;
        ResetWorkerLanes(SelectedWorkerCount());
        timer.Tick += (_, _) => Refresh();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control && Keyboard.FocusedElement is not TextBox && !running)
            {
                try { if (Clipboard.ContainsFileDropList()) AddSources(Clipboard.GetFileDropList().Cast<string>()); }
                catch (Exception ex) { ShowError(ex); }
                e.Handled = true;
            }
        };
    }

    private void AddItems_Click(object sender, RoutedEventArgs e)
    {
        try { AddSources(MixedItemPicker.Show(this)); }
        catch (Exception ex) { ShowError(ex); }
    }
    private void Destination_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "대상 폴더 선택" };
        string entered = CleanDestination(DestinationBox.Text);
        if (Directory.Exists(entered)) dialog.InitialDirectory = entered;
        if (dialog.ShowDialog(this) == true) DestinationBox.Text = dialog.FolderName;
    }
    private static string CleanDestination(string text) => Environment.ExpandEnvironmentVariables(text.Trim().Trim('"'));
    private void ClearDestination_Click(object sender, RoutedEventArgs e)
    {
        DestinationBox.Clear();
        DestinationBox.Focus();
    }
    private void AddSources(IEnumerable<string> paths)
    {
        if (running || validating) return;
        var existing = sources.Select(s => s.FullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string path in paths)
        {
            string full = TransferEngine.Normalize(path);
            if ((File.Exists(full) || Directory.Exists(full)) && existing.Add(full)) sources.Add(new(full));
        }
        UpdateSelection();
    }
    private void UpdateSelection()
    {
        SelectionCount.Text = sources.Count == 0 ? "" : $"{sources.Count:N0}개 선택";
        EmptyState.Visibility = sources.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SourceList.Visibility = sources.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }
    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        foreach (var entry in SourceList.SelectedItems.Cast<SourceEntry>().ToArray()) sources.Remove(entry);
        UpdateSelection();
    }
    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) AddSources(paths);
        e.Handled = true;
    }
    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = !running && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }
    private async void Copy_Click(object sender, RoutedEventArgs e) => await StartTransfer(false);
    private async void Move_Click(object sender, RoutedEventArgs e) => await StartTransfer(true);

    private async Task StartTransfer(bool move)
    {
        if (running || validating) return;
        int workerCount = SelectedWorkerCount();
        var options = new TransferOptions(sources.Select(s => s.FullPath).ToArray(), CleanDestination(DestinationBox.Text),
            Workers: workerCount, Move: move);
        ResetWorkerLanes(workerCount);
        validating = true;
        transferIsMove = move;
        SetWorkState(WorkState.Validating);
        TransferProgress.Value = 0;
        PercentText.Text = "확인 중";
        BytesText.Text = "0 B";
        FilesText.Text = "0 / 0";
        SpeedText.Text = "— MB/s";
        FileRateText.Text = "— 파일/초";
        EtaText.Text = "—";
        CurrentText.Text = $"동시 작업 {workerCount}개를 준비하고 있습니다.";
        CurrentText.ToolTip = null;
        DetailText.Text = "";
        TransferStatusRow.ToolTip = null;
        CopyButton.IsEnabled = MoveButton.IsEnabled = SetupPanel.IsEnabled = false;
        try { await Task.Run(() => TransferEngine.Validate(options)); }
        catch (Exception ex)
        {
            SetWorkState(WorkState.Failed);
            WorkStateTitle.Text = "전송을 시작할 수 없습니다";
            WorkStateDescription.Text = "보낼 항목과 대상 폴더를 확인한 뒤 다시 실행해주세요.";
            CurrentText.Text = ex.Message;
            CurrentText.ToolTip = ex.Message;
            PercentText.Text = "—";
            ShowError(ex);
            return;
        }
        finally { validating = false; CopyButton.IsEnabled = MoveButton.IsEnabled = SetupPanel.IsEnabled = true; }
        try
        {
            session?.Dispose();
            session = new TransferSession();
            logPath = session.LogPath;
            cancellation = new CancellationTokenSource();
            SetRunning(true);
            lastBytes = 0; lastCopiedFiles = 0; lastTime = TimeSpan.Zero; speed = 0; fileRate = 0;
            EtaText.Text = "계산 중";
            PercentText.Text = "탐색 중";
            CurrentText.Text = $"동시 작업 {workerCount}개가 파일을 기다리고 있습니다.";
            SetWorkState(WorkState.Scanning);
            timer.Start();
            var result = await Task.Run(() => TransferEngine.RunAsync(options, session, cancellation.Token));
            Refresh();
            SetWorkState(result.Cancelled ? WorkState.Cancelled : result.Snapshot.Errors > 0 ? WorkState.Failed : WorkState.Complete);
            if (!result.Cancelled)
            {
                var s = result.Snapshot;
                WorkStateDescription.Text = $"파일 {s.CopiedFiles:N0}개 · {FormatBytes(s.CopiedBytes)} · 건너뜀 {s.Skipped:N0}개 · 오류 {s.Errors:N0}개"
                    + (s.MovedDirectories > 0 ? $" · 폴더 {s.MovedDirectories:N0}개 즉시 이동" : "");
            }
            if (!result.Cancelled) { TransferProgress.Value = 100; PercentText.Text = "100%"; }
            TransferProgress.IsIndeterminate = false;
            EtaText.Text = "—";
            SpeedText.Text = "— MB/s";
            FileRateText.Text = result.Snapshot.Elapsed.TotalSeconds > 0
                ? $"{result.Snapshot.CopiedFiles / result.Snapshot.Elapsed.TotalSeconds:0.0} 파일/초"
                : "— 파일/초";
            CurrentText.Text = $"전송 기록: {logPath}";
            CurrentText.ToolTip = logPath;
        }
        catch (Exception ex)
        {
            if (running && session != null) Refresh();
            SetWorkState(WorkState.Failed);
            CurrentText.Text = ex.Message;
            CurrentText.ToolTip = ex.Message;
            ShowError(ex);
        }
        finally
        {
            timer.Stop();
            cancellation?.Dispose(); cancellation = null;
            SetRunning(false);
            if (closeWhenStopped) Close();
        }
    }

    private void SetWorkState(WorkState state)
    {
        if (workState == state) return;
        workState = state;
        string action = transferIsMove ? "이동" : "복사";
        var (label, title, description, accent, background, border, taskbar) = state switch
        {
            WorkState.Validating => ("확인 중", $"{action} 준비 중입니다", "선택한 항목과 대상 폴더를 확인하는 중입니다.", "#75B6FF", "#102E57", "#479CFF", TaskbarItemProgressState.Indeterminate),
            WorkState.Scanning => ("탐색 + 전송 중", $"파일을 {action}하고 있습니다", "파일을 찾는 대로 전송하고 있습니다. 전체 용량을 확인하면 진행률이 표시됩니다.", "#75B6FF", "#102E57", "#479CFF", TaskbarItemProgressState.Indeterminate),
            WorkState.Transferring => ("전송 중", $"파일을 {action}하고 있습니다", "아래에서 진행률과 전송 속도를 확인할 수 있습니다.", "#75B6FF", "#102E57", "#479CFF", TaskbarItemProgressState.Normal),
            WorkState.Paused => ("일시정지", "전송을 잠시 멈췄습니다", "계속 전송을 누르면 이어서 진행합니다.", "#F7C66B", "#342916", "#B98A36", TaskbarItemProgressState.Paused),
            WorkState.Cancelling => ("정리 중", "전송을 취소하고 있습니다", "진행 중인 임시 파일을 정리하고 있습니다. 잠시 기다려주세요.", "#F7C66B", "#342916", "#B98A36", TaskbarItemProgressState.Indeterminate),
            WorkState.Complete => ("완료", $"{action} 작업이 완료되었습니다", "처리 결과는 아래 수치와 기록 보기에서 확인할 수 있습니다.", "#67E8AF", "#102F2A", "#32B985", TaskbarItemProgressState.None),
            WorkState.Cancelled => ("취소됨", "전송이 취소되었습니다", "이미 완료한 파일은 유지됩니다. 처리 결과는 기록 보기에서 확인하세요.", "#F7C66B", "#342916", "#B98A36", TaskbarItemProgressState.None),
            WorkState.Failed => ("오류 있음", "전송 결과를 확인해주세요", "처리하지 못한 항목이 있습니다. 기록 보기에서 자세한 내용을 확인하세요.", "#FDA4AF", "#3B1D2A", "#E76F87", TaskbarItemProgressState.Error),
            _ => ("준비됨", "파일 전송 준비 완료", "보낼 항목과 대상 폴더를 선택한 뒤 복사 또는 이동을 눌러주세요.", "#75B6FF", "#102342", "#315680", TaskbarItemProgressState.None)
        };
        static Brush BrushFor(string color)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
            brush.Freeze();
            return brush;
        }
        var accentBrush = BrushFor(accent);
        StatusBadge.Text = "●  " + label;
        StatusBadge.Foreground = WorkStateTitle.Foreground = TransferProgress.Foreground = accentBrush;
        WorkStateTitle.Text = title;
        WorkStateDescription.Text = description;
        WorkStateCard.Background = BrushFor(background);
        WorkStateCard.BorderBrush = BrushFor(border);
        ProgressTitle.Text = state == WorkState.Ready ? "전송 대기" : label;
        TransferProgress.IsIndeterminate = state is WorkState.Validating or WorkState.Scanning or WorkState.Cancelling;
        TaskbarItemInfo.ProgressState = taskbar;
        if (state == WorkState.Validating) TaskbarItemInfo.ProgressValue = 0;
        Title = "SwiftCopy · " + label;
    }

    private void SetRunning(bool value)
    {
        running = value;
        SetupPanel.IsEnabled = CopyButton.IsEnabled = MoveButton.IsEnabled = !value;
        PauseButton.IsEnabled = CancelButton.IsEnabled = value;
        PauseButton.Visibility = CancelButton.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        LogButton.IsEnabled = logPath != null;
        PauseButton.Content = "일시정지";
    }

    private int SelectedWorkerCount()
    {
        if (WorkerCountBox?.SelectedItem is ComboBoxItem item && int.TryParse(item.Content?.ToString(), out int count)) return count;
        return 4;
    }

    private void WorkerCount_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!running && !validating) ResetWorkerLanes(SelectedWorkerCount());
    }

    private void ResetWorkerLanes(int count)
    {
        WorkerLanes.Clear();
        for (int id = 0; id < count; id++) WorkerLanes.Add(new WorkerLaneViewModel(id));
    }

    private void UpdateWorkerLanes(IReadOnlyList<TransferWorkerSnapshot> snapshots, bool paused)
    {
        for (int index = 0; index < snapshots.Count; index++)
        {
            var snapshot = snapshots[index];
            int lane = snapshot.Id >= 0 && snapshot.Id < WorkerLanes.Count ? snapshot.Id : index;
            if (lane >= 0 && lane < WorkerLanes.Count) WorkerLanes[lane].Update(snapshot, paused);
        }
    }

    private void Refresh()
    {
        if (session == null) return;
        var s = session.Snapshot();
        UpdateWorkerLanes(s.Workers, s.Paused);
        DetailText.Visibility = Visibility.Visible;
        long bytes = s.CopiedBytes + s.InFlightBytes;
        double seconds = (s.Elapsed - lastTime).TotalSeconds;
        if (seconds >= 0.2)
        {
            double sample = Math.Max(0, bytes - lastBytes) / seconds;
            speed = speed == 0 ? sample : speed * 0.75 + sample * 0.25;
            double fileSample = Math.Max(0, s.CopiedFiles - lastCopiedFiles) / seconds;
            fileRate = fileRate == 0 ? fileSample : fileRate * 0.75 + fileSample * 0.25;
            lastBytes = bytes; lastTime = s.Elapsed;
            lastCopiedFiles = s.CopiedFiles;
        }
        SetWorkState(cancellation?.IsCancellationRequested == true ? WorkState.Cancelling : s.Paused ? WorkState.Paused : s.ScanComplete ? WorkState.Transferring : WorkState.Scanning);
        long resolvedFiles = Math.Min(s.FoundFiles, s.CopiedFiles + s.Skipped);
        double percent = s.FoundBytes > 0
            ? Math.Clamp((s.ResolvedBytes + s.InFlightBytes) * 100.0 / s.FoundBytes, 0, 99.9)
            : s.FoundFiles > 0 ? Math.Clamp(resolvedFiles * 100.0 / s.FoundFiles, 0, 99.9) : 0;
        TransferProgress.Value = percent;
        TaskbarItemInfo.ProgressValue = percent / 100;
        PercentText.Text = s.ScanComplete ? $"{percent:F1}%" : "탐색 중";
        SpeedText.Text = s.Paused ? "일시정지" : $"{FormatBytes(speed)}/s";
        FileRateText.Text = s.Paused ? "일시정지" : $"{fileRate:0.0} 파일/초";
        BytesText.Text = FormatBytes(s.CopiedBytes);
        FilesText.Text = s.MovedDirectories > 0 && s.FoundFiles == 0 ? $"폴더 {s.MovedDirectories:N0}개" : $"{s.CopiedFiles:N0} / {s.FoundFiles:N0}";
        if (s.MovedDirectories > 0 && s.FoundBytes == 0) BytesText.Text = "즉시 이동";
        EtaText.Text = !s.Paused && s.ScanComplete && speed > 1 ? FormatTime(TimeSpan.FromSeconds(Math.Min(31536000, Math.Max(0, s.FoundBytes - s.ResolvedBytes - s.InFlightBytes) / speed))) : "계산 중";
        int activeWorkers = s.Workers.Count(worker => worker.State is TransferWorkerState.Copying or TransferWorkerState.Verifying or TransferWorkerState.Moving);
        CurrentText.Text = activeWorkers > 0
            ? $"동시 작업 {activeWorkers:N0}개 처리 중 · 파일 {s.CopiedFiles:N0}개 완료"
            : s.ScanComplete ? "남은 작업을 정리하고 있습니다…" : "폴더를 탐색하며 작업을 배분하고 있습니다…";
        CurrentText.ToolTip = CurrentText.Text;
        DetailText.Text = $"건너뜀 {s.Skipped:N0}  ·  오류 {s.Errors:N0}  ·  경과 {FormatTime(s.Elapsed)}" +
            (s.MovedDirectories > 0 ? $"  ·  폴더 {s.MovedDirectories:N0}개 통째로 이동 (내부 개수·크기 탐색 생략)" : $"  ·  발견 {FormatBytes(s.FoundBytes)}") + (s.ScanComplete ? "" : "  ·  탐색 중");
        TransferStatusRow.ToolTip = CurrentText.Text + Environment.NewLine + DetailText.Text;
    }
    private void Pause_Click(object sender, RoutedEventArgs e)
    {
        if (session == null) return;
        if (session.Snapshot().Paused) { session.Resume(); PauseButton.Content = "일시정지"; }
        else { session.Pause(); PauseButton.Content = "계속 전송"; }
        Refresh();
    }
    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        cancellation?.Cancel();
        PauseButton.IsEnabled = CancelButton.IsEnabled = false;
        SetWorkState(WorkState.Cancelling);
    }
    private void Log_Click(object sender, RoutedEventArgs e)
    {
        try { if (logPath != null) Process.Start(new ProcessStartInfo(logPath) { UseShellExecute = true }); }
        catch (Exception ex) { ShowError(ex); }
    }
    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (running)
        {
            e.Cancel = true;
            if (closeWhenStopped) return;
            if (MessageBox.Show(this, "진행 중인 전송을 취소하고 닫을까요? 완료한 파일은 유지됩니다.", "SwiftCopy", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            { closeWhenStopped = true; Cancel_Click(this, new RoutedEventArgs()); }
        }
        else if (validating) e.Cancel = true;
        else session?.Dispose();
    }
    private void ShowError(Exception ex) => MessageBox.Show(this, ex.Message, "SwiftCopy · 확인이 필요합니다", MessageBoxButton.OK, MessageBoxImage.Information);
    private static string FormatBytes(double bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        int index = 0;
        while (bytes >= 1024 && index < units.Length - 1) { bytes /= 1024; index++; }
        return $"{bytes:0.#} {units[index]}";
    }
    private static string FormatTime(TimeSpan time) => time.TotalHours >= 1 ? $"{(int)time.TotalHours}시간 {time.Minutes}분" : time.TotalMinutes >= 1 ? $"{(int)time.TotalMinutes}분 {time.Seconds}초" : $"{time.Seconds}초";
}

