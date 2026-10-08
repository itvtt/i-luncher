using TerminalNotes.Services;

namespace TerminalNotes;

public enum AutoInstallStatus
{
    NotConnected
}

public sealed record AutoInstallResult(AutoInstallStatus Status, string Message);

public sealed record AutoInstallDetectionResult(
    bool IsInstalled,
    string Message,
    string? Path = null,
    string? Version = null);

public static class AutoInstall
{
    private static readonly OpenCodeInstallationService InstallationService = new();

    /// <summary>Checks PATH and the per-user Terminal Notes location for a working OpenCode command.</summary>
    public static Task<AutoInstallDetectionResult> CheckAsync(CancellationToken cancellationToken = default) =>
        InstallationService.CheckAsync(cancellationToken);

    /// <summary>Custom installer integration point. No installation is performed until an installer is supplied.</summary>
    public static Task<AutoInstallResult> RunAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // 자동 설치 코드는 여기에 작성합니다. 설치가 끝난 뒤 반환하세요.
        // 시작 화면은 반환 후 OpenCode 실행 가능 여부를 다시 확인합니다.
        return Task.FromResult(new AutoInstallResult(
            AutoInstallStatus.NotConnected,
            "자동 설치 기능이 아직 연결되지 않았습니다."));
    }
}
