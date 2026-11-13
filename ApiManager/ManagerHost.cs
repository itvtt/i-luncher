using System.Reflection;
using System.Windows;

namespace ApiManager;

/// <summary>i-luncher 가 API Manager 를 여는 곳. 마스터 계정 확인은 런처가 함 (MASTER_USERS)</summary>
public static class ManagerHost
{
    private static ManagerWindow? _window;
    private static ResourceDictionary? _resources;

    /// <summary>관리할 서버 주소를 따로 정할 때만 (예: https://api.example.com). 비우면 이 PC 서버의 relay.py SETTINGS(RELAY_HOST·RELAY_PORT)</summary>
    public static string ApiServer { get; set; } = "";

    public static string LauncherVersion => Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "";

    internal static object Resource(string key) =>
        (_resources ??= new ResourceDictionary { Source = new Uri("pack://application:,,,/ApiManager;component/ManagerResources.xaml") })[key];

    /// <summary>창을 열거나, 열려 있으면 앞으로</summary>
    public static void Open()
    {
        if (_window is { } open)
        {
            if (open.WindowState == WindowState.Minimized) open.WindowState = WindowState.Normal;
            open.Activate();
            return;
        }
        _window = new ManagerWindow();
        _window.Closed += (_, _) => _window = null;
        _window.Show();
    }

    /// <summary>
    /// 창 없이: 이 PC 가 서버 PC 면(relay.py 가 배포돼 있으면) 런처에 든 relay.py 가 더 새 버전일 때 바꾸고, 서버가 꺼져 있으면 시작.
    /// 서버 PC 가 아니면 아무것도 하지 않음. 실패는 조용히 넘김 (창을 열면 자세히 보임)
    /// </summary>
    public static async Task ApplyAsync()
    {
        try
        {
            if (!File.Exists(ServerManager.DeployedScript) || !File.Exists(ServerManager.BundledScript)) return;
            string? python = ServerManager.FindPython();
            if (python is null) return;
            string? deployed = await ServerManager.DeployIfNewerAsync(python);
            if (deployed is null && await ServerManager.IsHealthyAsync(ServerManager.ReadAddress(ServerManager.DeployedScript))) return;
            await ServerManager.RestartAsync(python);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or OperationCanceledException or System.ComponentModel.Win32Exception) { }
    }
}
