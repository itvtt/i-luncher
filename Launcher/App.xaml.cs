using System.Windows;
using System.ComponentModel;
using ILauncher.Services;

namespace ILauncher;

public partial class App : Application
{
    private readonly IntegratedAppProvider _integratedApps = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Length == 0)
        {
            var launcher = new MainWindow(new LauncherCatalog(_integratedApps));
            MainWindow = launcher;
            launcher.Show();
            return;
        }

        if (e.Args.Length == 2 && string.Equals(e.Args[0], "--app", StringComparison.OrdinalIgnoreCase))
        {
            string appId = e.Args[1];
            if (_integratedApps.Contains(appId))
            {
                if (string.Equals(appId, IntegratedAppProvider.DiskAtlasId, StringComparison.OrdinalIgnoreCase) &&
                    !IntegratedAppProcessLauncher.IsCurrentProcessElevated())
                {
                    try
                    {
                        using System.Diagnostics.Process? process = System.Diagnostics.Process.Start(
                            IntegratedAppProcessLauncher.CreateElevatedStartInfo(appId));
                        if (process is null)
                        {
                            FailStartup("DiskAtlas 관리자 프로세스를 시작하지 못했습니다.");
                            return;
                        }
                    }
                    catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
                    {
                        Shutdown();
                        return;
                    }
                    catch (Exception ex)
                    {
                        FailStartup($"DiskAtlas 관리자 프로세스를 시작하지 못했습니다.\n{ex.Message}");
                        return;
                    }
                    Shutdown();
                    return;
                }

                _integratedApps.OpenInProcess(appId);
                return;
            }

            FailStartup($"알 수 없는 앱 선택자입니다: {appId}");
            return;
        }

        FailStartup("사용법: i-luncher.exe [--app <folder-compare|swift-copy|disk-atlas|deskcal>]");
    }

    private void FailStartup(string message)
    {
        MessageBox.Show(message, "i-luncher", MessageBoxButton.OK, MessageBoxImage.Error);
        Shutdown(2);
    }
}
