using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using System.Windows;

namespace DiskAtlas.Startup;

internal static class Program
{
    private const int Cancelled = 1223;

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            return RunAsync(args).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            return Cancelled;
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                $"DiskAtlas를 시작하지 못했습니다.\n\n{exception.Message}",
                "DiskAtlas",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return 1;
        }
    }

    private static async Task<int> RunAsync(string[] args)
    {
        string sid = IdentityService.GetCurrentUserSid();
        var coordinator = new BootstrapCoordinator(sid);

        if (args.Length == 0)
        {
            return await coordinator.EnsureInstalledAndLaunchAsync().ConfigureAwait(false);
        }

        if (args is ["--install"])
        {
            return await coordinator.InstallElevatedAsync().ConfigureAwait(false);
        }

        if (args is ["--uninstall"])
        {
            return await coordinator.UninstallElevatedAsync().ConfigureAwait(false);
        }

        if (args is ["--elevated-install", var requestedSid])
        {
            IdentityService.RequireElevatedSameUser(requestedSid);
            await new InstallerService(requestedSid).InstallAsync().ConfigureAwait(false);
            return 0;
        }

        if (args is ["--elevated-uninstall", var uninstallSid])
        {
            IdentityService.RequireElevatedSameUser(uninstallSid);
            new InstallerService(uninstallSid).Uninstall();
            return 0;
        }

        throw new InvalidOperationException("지원하지 않는 실행 옵션입니다.");
    }

    internal static async Task<int> RunElevatedHelperAsync(string argument, string sid, SetupStage stage)
    {
        try
        {
            using Process process = Process.Start(new ProcessStartInfo
            {
                FileName = stage.BootstrapPath,
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = stage.RootPath,
                ArgumentList = { argument, sid },
            }) ?? throw new InvalidOperationException("관리자 설치 프로그램을 시작하지 못했습니다.");

            await process.WaitForExitAsync().ConfigureAwait(false);
            return process.ExitCode;
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == Cancelled)
        {
            return Cancelled;
        }
    }
}
