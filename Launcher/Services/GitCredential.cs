using System.Diagnostics;
using System.IO;

namespace ILauncher.Services;

/// <summary>
/// 이 PC 의 git 자격 증명(Git Credential Manager 등, 저장소를 push 하는 그 계정)에 저장된 호스트의 토큰.
/// 창 · 입력 요청을 띄우지 않고, git 이 없거나 저장된 것이 없으면 null. 값은 화면 · 기록에 남기지 않음
/// (API 서버의 git_credential_token 과 같은 방식)
/// </summary>
public static class GitCredential
{
    public static string? Fill(string host, TimeSpan? timeout = null)
    {
        try
        {
            var start = new ProcessStartInfo("git", "credential fill")
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            start.Environment["GIT_TERMINAL_PROMPT"] = "0";
            start.Environment["GCM_INTERACTIVE"] = "never";
            using var process = Process.Start(start);
            if (process is null) return null;
            process.StandardInput.Write($"protocol=https\nhost={host}\n\n");
            process.StandardInput.Close();
            var output = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            if (!output.Wait(timeout ?? TimeSpan.FromSeconds(20)) || !process.WaitForExit(2000))
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                return null;
            }
            if (process.ExitCode != 0) return null;
            foreach (string line in output.Result.Split('\n'))
                if (line.StartsWith("password=", StringComparison.Ordinal) && line[9..].Trim() is { Length: > 0 } token) return token;
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or IOException or InvalidOperationException) { }
        return null;
    }
}
