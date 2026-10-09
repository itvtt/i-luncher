using System;
using System.Configuration.Install;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.ServiceProcess;
using System.Windows.Forms;
using file_shared;


namespace file_shared
{
    [System.ComponentModel.RunInstaller(true)]
    public class ProjectInstaller : Installer
    {
        public ProjectInstaller()
        {
            var processInstaller = new ServiceProcessInstaller();
            processInstaller.Account = ServiceAccount.LocalSystem;

            var serviceInstaller = new ServiceInstaller();
            serviceInstaller.ServiceName = "FileShareService";
            serviceInstaller.DisplayName = "File Share Service";
            serviceInstaller.StartType = ServiceStartMode.Automatic;

            Installers.Add(processInstaller);
            Installers.Add(serviceInstaller);
        }
    }

    public class FileShareWindowsService : ServiceBase
    {
        private HttpServer _server;

        public FileShareWindowsService()
        {
            ServiceName = "FileShareService";
        }

        protected override void OnStart(string[] args)
        {
            _server = new HttpServer();
            _server.Start();
        }

        protected override void OnStop()
        {
            _server?.Stop();
        }

        private const string LOG_FILE = @"c:\tem\install_log.txt";

        private static void Log(string msg)
        {
            try
            {
                Directory.CreateDirectory(@"c:\tem");
                File.AppendAllText(LOG_FILE, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {msg}\r\n");
            }
            catch { }
        }

        private static bool IsServiceInstalled(string serviceName)
        {
            return ServiceController.GetServices()
                .Any(s => s.ServiceName.Equals(serviceName, StringComparison.OrdinalIgnoreCase));
        }

        private static string GetServiceExePath()
        {
            return @"c:\tem\service\file_shared.exe";
        }

        private static void InstallService()
        {
            string sourcePath = Assembly.GetExecutingAssembly().Location;
            string targetPath = GetServiceExePath();

            Directory.CreateDirectory(Path.GetDirectoryName(targetPath));

            // 이미 서비스가 있으면 정지 후 복사(파일 잠금 해제)
            if (IsServiceInstalled("FileShareService"))
            {
                try
                {
                    using (var sc = new ServiceController("FileShareService"))
                    {
                        if (sc.Status != ServiceControllerStatus.Stopped)
                        {
                            sc.Stop();
                            sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(15));
                        }
                    }
                }
                catch (Exception ex) { Log($"기존 서비스 정지 오류: {ex}"); }

                File.Copy(sourcePath, targetPath, true);
                return; // 이미 등록되어 있으니 InstallHelper 재실행 불필요
            }

            File.Copy(sourcePath, targetPath, true);
            ManagedInstallerClass.InstallHelper(new[] { targetPath });
        }

        private static void StartServiceInstance(string serviceName)
        {
            using (var sc = new ServiceController(serviceName))
            {
                if (sc.Status != ServiceControllerStatus.Running)
                {
                    sc.Start();
                    sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(15));
                }
            }
        }

        [STAThread]
        static void Main(string[] args)
        {
            // 관리자 권한 자식 프로세스로 재실행된 경우: 설치/시작만 하고 종료
            if (args.Length > 0 && args[0] == "--install")
            {
                try
                {
                    Log("[관리자 프로세스] 서비스 설치 시도...");
                    InstallService();
                    Log("[관리자 프로세스] 서비스 설치 완료, 시작 시도...");
                    StartServiceInstance("FileShareService");
                    Log("[관리자 프로세스] 서비스 시작 완료");
                }
                catch (Exception ex)
                {
                    Log($"[관리자 프로세스] 설치/시작 오류: {ex}");
                }
                return;
            }

            if (Environment.UserInteractive)
            {
                Log("프로그램 시작 (UserInteractive)");

                bool installed;
                try
                {
                    installed = IsServiceInstalled("FileShareService");
                    Log($"서비스 설치 여부: {installed}");
                }
                catch (Exception ex)
                {
                    Log($"설치 여부 확인 오류: {ex}");
                    MessageBox.Show("서비스 확인 오류: " + ex.Message + "\n로그: " + LOG_FILE,
                        "오류", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                if (!installed)
                {
                    try
                    {
                        Log("관리자 권한 설치 프로세스 실행 요청...");
                        var psi = new ProcessStartInfo
                        {
                            FileName = Assembly.GetExecutingAssembly().Location,
                            Arguments = "--install",
                            UseShellExecute = true,
                            Verb = "runas"
                        };
                        var proc = Process.Start(psi);
                        proc.WaitForExit();
                        Log("관리자 권한 설치 프로세스 종료됨");

                        if (!IsServiceInstalled("FileShareService"))
                        {
                            MessageBox.Show("서비스 설치에 실패했습니다.\n로그: " + LOG_FILE,
                                "오류", MessageBoxButtons.OK, MessageBoxIcon.Error);
                            return;
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"설치 프로세스 실행 오류: {ex}");
                        MessageBox.Show("서비스 설치 실패: " + ex.Message + "\n로그: " + LOG_FILE,
                            "오류", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return;
                    }
                }
                else
                {
                    try
                    {
                        StartServiceInstance("FileShareService");
                        Log("서비스 실행 확인/시작 완료");
                    }
                    catch (Exception ex)
                    {
                        // 일반 권한으로 서비스 제어 실패 시 관리자 프로세스로 재시도
                        Log($"서비스 시작 오류(권한 부족 가능): {ex}");
                        try
                        {
                            var psi = new ProcessStartInfo
                            {
                                FileName = Assembly.GetExecutingAssembly().Location,
                                Arguments = "--install",
                                UseShellExecute = true,
                                Verb = "runas"
                            };
                            var proc = Process.Start(psi);
                            proc.WaitForExit();
                        }
                        catch (Exception ex2)
                        {
                            Log($"관리자 재시도 오류: {ex2}");
                        }
                    }
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new Form1());
            }
            else
            {
                ServiceBase.Run(new FileShareWindowsService());
            }
        }
    }
}