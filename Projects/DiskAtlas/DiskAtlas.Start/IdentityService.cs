using System.Security.Principal;

namespace DiskAtlas.Startup;

internal static class IdentityService
{
    internal static string GetCurrentUserSid()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        return identity.User?.Value
            ?? throw new InvalidOperationException("현재 Windows 사용자 SID를 확인하지 못했습니다.");
    }

    internal static void RequireElevatedSameUser(string requestedSid)
    {
        if (!InstallationPaths.IsValidUserSid(requestedSid))
        {
            throw new InvalidOperationException("설치 요청의 사용자 SID가 올바르지 않습니다.");
        }

        string currentSid = GetCurrentUserSid();
        if (!StringComparer.OrdinalIgnoreCase.Equals(currentSid, requestedSid))
        {
            throw new InvalidOperationException(
                "다른 관리자 계정으로는 설치할 수 없습니다. 현재 사용자와 같은 계정의 관리자 승인을 사용해 주세요.");
        }

        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        if (!principal.IsInRole(WindowsBuiltInRole.Administrator))
        {
            throw new InvalidOperationException("관리자 권한이 필요합니다.");
        }
    }
}
