using System.Reflection;
using System.Windows;

namespace ApiManager;

/// <summary>i-luncher 가 관리자 메뉴를 여는 곳. 마스터 계정 확인은 런처가 함 (MASTER_USERS, 비밀번호 없이 계정 이름으로)</summary>
public static class ManagerHost
{
    private static ResourceDictionary? _resources;

    /// <summary>
    /// 런처가 쓰는 API 서버 (마스터가 서버 화면에서 바꾼 주소, 없으면 빌드 설정 API_SERVER). 런처가 정함.
    /// 이 PC 가 아니면 관리자 메뉴는 이 PC 에 서버를 두거나 켜지 않고 그 서버만 고침. 비었거나 이 PC 면 이 PC 서버의 relay.py SETTINGS(RELAY_HOST·RELAY_PORT)
    /// </summary>
    public static string ApiServer { get; set; } = "";

    /// <summary>빌드 설정의 API_SERVER (주소 칸을 비우면 이것). 런처가 정함</summary>
    public static string DefaultApiServer { get; set; } = "";

    /// <summary>서버 화면에서 주소를 바꿈 ("" = 빌드 설정의 주소로). 런처가 저장하고 목록을 다시 읽음. 틀린 주소면 null, 아니면 지금 쓰는 주소</summary>
    public static Func<string, string?>? ChangeApiServer { get; set; }

    /// <summary>API 서버가 이 PC 에 있음 (주소가 없거나 이 PC 의 이름 · 주소): 관리자 메뉴가 서버를 설치 · 시작 · 중지</summary>
    public static bool IsLocalServer => ApiServer.Length == 0 || IsThisPc(ApiServer);

    internal static bool IsThisPc(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return true;
        string host = uri.Host.Trim('[', ']');
        if (uri.IsLoopback || host.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase)) return true;
        if (!System.Net.IPAddress.TryParse(host, out var address)) return false;
        try
        {
            return System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                .SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses).Any(unicast => unicast.Address.Equals(address));
        }
        catch (System.Net.NetworkInformation.NetworkInformationException) { return false; }
    }

    public static string LauncherVersion => Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "";

    internal static object Resource(string key) =>
        (_resources ??= new ResourceDictionary { Source = new Uri("pack://application:,,,/ApiManager;component/ManagerResources.xaml") })[key];

    /// <summary>런처 오른쪽 화면에 넣을 관리자 메뉴. 런처가 하나 만들어 두고 다시 씀</summary>
    public static ManagerView CreateView() => new();

    /// <summary>런처 공지(GitHub 공지 저장소의 공지.txt)의 지금 내용. 런처가 정함 (실패하면 InvalidOperationException)</summary>
    public static Func<Task<string>>? LoadLauncherNotices { get; set; }

    /// <summary>런처 공지를 GitHub 에서 고치는 페이지 주소. 런처가 정함</summary>
    public static Func<Task<string?>>? LauncherNoticesEditUrl { get; set; }

    /// <summary>관리자 메뉴에서 앱 목록을 저장함: 런처가 목록을 다시 읽음</summary>
    public static event Action? CatalogSaved;

    internal static void RaiseCatalogSaved() => CatalogSaved?.Invoke();

    /// <summary>
    /// 화면 없이: 이 PC 가 서버 PC 면(relay.py 가 배포돼 있으면) 런처에 든 relay.py 가 더 새 버전일 때 바꾸고, 서버가 꺼져 있으면 시작.
    /// 서버 PC 가 아니거나 런처가 다른 PC 의 서버를 쓰면 아무것도 하지 않음. 실패는 조용히 넘김 (관리자 메뉴에서 자세히 보임)
    /// </summary>
    public static async Task ApplyAsync()
    {
        try
        {
            if (!IsLocalServer || !ServerManager.IsDeployed || !File.Exists(ServerManager.BundledScript)) return;
            string? python = ServerManager.FindPython();
            if (python is null) return;
            string? deployed = await ServerManager.DeployIfNewerAsync(python);
            if (deployed is null && await ServerManager.IsHealthyAsync(ServerManager.ReadAddress(ServerManager.DeployedScript))) return;
            await ServerManager.RestartAsync(python);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or OperationCanceledException or System.ComponentModel.Win32Exception) { }
    }
}
