using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Principal;
using System.Windows;
using ILauncher.Models;

namespace ILauncher.Services;

internal sealed class IntegratedAppProvider : IIntegratedAppProvider
{
    internal const string FolderCompareId = "folder-compare";
    internal const string SwiftCopyId = "swift-copy";
    internal const string DiskAtlasId = "disk-atlas";
    internal const string DeskCalId = "deskcal";

    private readonly Dictionary<string, Window> _openWindows = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<LauncherEntry> Entries { get; } =
    [
        CreateEntry(FolderCompareId, "FolderCompareWPF", "두 폴더의 파일과 내용을 비교합니다.", "파일 도구",
            "pack://application:,,,/i-luncher;component/Assets/BuiltIns/FolderCompare.png"),
        CreateEntry(SwiftCopyId, "SwiftCopy", "파일과 폴더를 빠르게 복사하고 이동합니다.", "파일 도구",
            "pack://application:,,,/i-luncher;component/Assets/BuiltIns/SwiftCopy-128.png"),
        CreateEntry(DiskAtlasId, "DiskAtlas", "디스크 용량을 한눈에 분석하고 파일 내용까지 검색합니다.", "디스크 분석",
            "pack://application:,,,/i-luncher;component/Assets/BuiltIns/DiskAtlas.png"),
        CreateEntry(DeskCalId, "DeskCal", "일정과 메모를 달력에서 관리합니다.", "일정",
            "pack://application:,,,/i-luncher;component/Assets/BuiltIns/DeskCal.ico")
    ];

    public Task LaunchAsync(string appId)
    {
        if (string.Equals(appId, DiskAtlasId, StringComparison.OrdinalIgnoreCase))
        {
            using Process? process = Process.Start(IntegratedAppProcessLauncher.CreateElevatedStartInfo(appId));
            if (process is null)
                throw new InvalidOperationException("DiskAtlas 관리자 프로세스를 시작하지 못했습니다.");
            return Task.CompletedTask;
        }

        if (Application.Current.Dispatcher.CheckAccess())
        {
            OpenInProcess(appId);
            return Task.CompletedTask;
        }

        return Application.Current.Dispatcher.InvokeAsync(() => OpenInProcess(appId)).Task;
    }

    internal bool Contains(string appId) => Entries.Any(entry =>
        string.Equals(entry.Id, appId, StringComparison.OrdinalIgnoreCase));

    internal void OpenInProcess(string appId)
    {
        if (!Contains(appId))
            throw new ArgumentException($"알 수 없는 통합 앱입니다: {appId}", nameof(appId));

        if (_openWindows.TryGetValue(appId, out Window? current))
        {
            if (current.WindowState == WindowState.Minimized)
                current.WindowState = WindowState.Normal;
            current.Show();
            current.Activate();
            return;
        }

        Window window = appId.ToLowerInvariant() switch
        {
            FolderCompareId => new FolderCompareWPF.MainWindow(),
            SwiftCopyId => new SwiftCopy.App.MainWindow(),
            DiskAtlasId => new DiskAtlas.App.MainWindow(),
            DeskCalId => new DeskCal.MainWindow(),
            _ => throw new ArgumentException($"알 수 없는 통합 앱입니다: {appId}", nameof(appId))
        };

        bool closed = false;
        window.Closed += (_, _) =>
        {
            closed = true;
            if (_openWindows.TryGetValue(appId, out Window? tracked) && ReferenceEquals(tracked, window))
                _openWindows.Remove(appId);
        };
        window.Show();
        if (!closed)
            _openWindows.Add(appId, window);
        window.Activate();
    }

    private static LauncherEntry CreateEntry(string id, string name, string description, string category,
        string iconPath) => new()
    {
        Id = id,
        Name = name,
        Description = description,
        Category = category,
        IsIntegrated = true,
        ExecutablePath = string.Empty,
        WorkingDirectory = AppContext.BaseDirectory,
        IconPath = iconPath
    };
}

internal static class IntegratedAppProcessLauncher
{
    internal static bool IsCurrentProcessElevated()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    internal static ProcessStartInfo CreateElevatedStartInfo(string appId)
    {
        string executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("현재 i-luncher 실행 파일 경로를 확인할 수 없습니다.");
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = AppContext.BaseDirectory
        };

        if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            string entryAssembly = Assembly.GetEntryAssembly()?.Location ?? string.Empty;
            if (string.IsNullOrWhiteSpace(entryAssembly))
                throw new InvalidOperationException("dotnet 호스트가 실행할 i-luncher 어셈블리를 확인할 수 없습니다.");
            startInfo.ArgumentList.Add(entryAssembly);
        }

        startInfo.ArgumentList.Add("--app");
        startInfo.ArgumentList.Add(appId);
        return startInfo;
    }
}
