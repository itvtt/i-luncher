using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using SwiftCopy.Core;

namespace SwiftCopy.App;

public sealed class WorkerLaneViewModel : INotifyPropertyChanged
{
    private string fileName = "다음 파일 대기 중";
    private string fullPath = "";
    private string stage = "대기";
    private string progressText = "대기";
    private string countText = "완료 0개";
    private string accent = "#5E7FA8";
    private double progress;
    private bool isIndeterminate;

    public WorkerLaneViewModel(int id)
    {
        Id = id;
        Label = $"작업 {id + 1}";
    }

    public int Id { get; }
    public string Label { get; }
    public string FileName { get => fileName; private set => Set(ref fileName, value); }
    public string FullPath { get => fullPath; private set => Set(ref fullPath, value); }
    public string Stage { get => stage; private set => Set(ref stage, value); }
    public string ProgressText { get => progressText; private set => Set(ref progressText, value); }
    public string CountText { get => countText; private set => Set(ref countText, value); }
    public string Accent { get => accent; private set => Set(ref accent, value); }
    public double Progress { get => progress; private set => Set(ref progress, value); }
    public bool IsIndeterminate { get => isIndeterminate; private set => Set(ref isIndeterminate, value); }

    public void Reset()
    {
        FileName = "다음 파일 대기 중";
        FullPath = "";
        Stage = "대기";
        ProgressText = "대기";
        CountText = "완료 0개";
        Accent = "#5E7FA8";
        Progress = 0;
        IsIndeterminate = false;
    }

    public void Update(TransferWorkerSnapshot snapshot, bool paused)
    {
        string source = snapshot.Source ?? string.Empty;
        FullPath = source;
        string name = string.IsNullOrWhiteSpace(source)
            ? string.Empty
            : Path.GetFileName(source.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) ?? string.Empty;
        FileName = !string.IsNullOrWhiteSpace(name) ? name
            : !string.IsNullOrWhiteSpace(source) ? source
            : "다음 파일 대기 중";
        CountText = $"완료 {snapshot.CompletedFiles:N0}개";

        bool active = snapshot.State is TransferWorkerState.Copying or TransferWorkerState.Verifying or TransferWorkerState.Moving;
        string stateText = snapshot.State switch
        {
            TransferWorkerState.Copying => "복사 중",
            TransferWorkerState.Verifying => "검증 중",
            TransferWorkerState.Moving => "이동 중",
            TransferWorkerState.Completed => "완료",
            TransferWorkerState.Skipped => "건너뜀",
            TransferWorkerState.Failed => "오류",
            TransferWorkerState.Cancelled => "취소됨",
            _ => "대기"
        };
        Stage = paused && active ? $"일시정지 · {stateText}" : stateText;
        Accent = snapshot.State switch
        {
            TransferWorkerState.Completed => "#67E8AF",
            TransferWorkerState.Failed => "#FDA4AF",
            TransferWorkerState.Cancelled or TransferWorkerState.Skipped => "#F7C66B",
            _ when paused && active => "#F7C66B",
            _ when active => "#75B6FF",
            _ => "#5E7FA8"
        };

        if (snapshot.State == TransferWorkerState.Verifying)
        {
            Progress = 0;
            IsIndeterminate = !paused;
            ProgressText = paused ? "검증 일시정지" : "검증 중";
        }
        else if (snapshot.TotalBytes > 0)
        {
            Progress = Math.Clamp(snapshot.TransferredBytes * 100.0 / snapshot.TotalBytes, 0, 100);
            IsIndeterminate = false;
            ProgressText = $"{Progress:F0}%  ·  {FormatBytes(snapshot.TransferredBytes)} / {FormatBytes(snapshot.TotalBytes)}";
        }
        else if (active)
        {
            Progress = 0;
            IsIndeterminate = !paused;
            ProgressText = paused ? "일시정지" : stateText;
        }
        else
        {
            Progress = snapshot.State == TransferWorkerState.Completed ? 100 : 0;
            IsIndeterminate = false;
            ProgressText = stateText;
        }
    }

    private static string FormatBytes(double bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        int index = 0;
        while (bytes >= 1024 && index < units.Length - 1) { bytes /= 1024; index++; }
        return $"{bytes:0.#} {units[index]}";
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? property = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
