// i-luncher AppKit: 앱이 스스로 업데이트하게 하는 파일 하나짜리 업데이터 (.NET 6 이상과 .NET Framework 4.8, WPF·WinForms·콘솔 공통).
// 원본은 i-luncher 저장소의 AppKit\AppUpdater.cs 입니다. 앱 저장소에는 AppKit\ 폴더째 복사해 쓰세요 (AppKit\README.md).
//
// 동작:
//   1. 앱 옆의 update.json (빌드할 때 .env 의 UPDATE_* 또는 Actions 저장소에서 만들어짐)에서 릴리스 저장소를 읽음
//   2. "설치된 사본"(Windows "앱" 목록 HKCU\...\Uninstall\<InstallName> 의 InstallLocation 이 이 폴더)일 때만,
//      시작 몇 초 뒤 GitHub(GitHub Enterprise 포함) 최신 릴리스를 확인
//   3. 지금보다 높은 버전이면 <InstallName>-win-x64.zip 을 받아 두고, 앱이 끝날 때 파일을 바꿈 (다음 실행부터 새 버전).
//      onReady 를 넘기면 "지금 다시 시작"을 고를 수 있음
// i-luncher 로 설치한 앱도, 앱의 설치 프로그램으로 설치한 앱도 같은 규칙이라 둘 다 이 업데이터가 알아봄.
// 개발 중 bin\ 에서 실행하면 설치된 사본이 아니므로 아무것도 하지 않음.
#nullable enable
using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace ILuncher.AppKit
{
    /// <summary>릴리스를 찾을 곳. update.json 형식: { "Api": "...", "Repository": "owner/name", "Token": "", "Asset": "X-win-x64.zip", "InstallName": "X" }</summary>
    public sealed record UpdateSettings(string Api, string Repository, string? Token, string Asset, string InstallName)
    {
        public static UpdateSettings? Load(string? path = null)
        {
            path ??= Path.Combine(AppContext.BaseDirectory, "update.json");
            try
            {
                if (!File.Exists(path)) return null;
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                var root = document.RootElement;
                string Read(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()!.Trim() : "";
                string repository = Read("Repository");
                if (repository.Length == 0 || repository.Count(c => c == '/') != 1) return null;
                string api = Read("Api");
                string installName = Read("InstallName");
                if (installName.Length == 0) installName = Path.GetFileNameWithoutExtension(Platform.ProcessPath ?? "App");
                string asset = Read("Asset");
                if (asset.Length == 0) asset = installName + "-win-x64.zip";
                string token = Read("Token");
                return new UpdateSettings(api.Length == 0 ? "https://api.github.com" : api.TrimEnd('/'), repository,
                    token.Length == 0 ? null : token, asset, installName);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
            {
                return null;
            }
        }
    }

    public sealed record AvailableUpdate(string Version, string Tag, string AssetUrl, long Size, string? Notes, string? HtmlUrl);

    /// <summary>새 버전을 받아 둔 상태. RestartNow() 를 부른 뒤 앱을 끝내면 바로 바꾸고 다시 시작</summary>
    public sealed class UpdateReady
    {
        internal UpdateReady(AvailableUpdate update, string package, string installName) { Update = update; Package = package; InstallName = installName; }
        public AvailableUpdate Update { get; }
        public string Package { get; }
        public string InstallName { get; }
        public string CurrentVersion => AppUpdater.CurrentVersion;
        /// <summary>앱이 끝나면 다시 시작함 (호출한 뒤 앱을 끝내세요: Application.Current.Shutdown())</summary>
        public void RestartNow() => AppUpdater.ScheduleApply(Package, Update.Version, restart: true, InstallName);
    }

    public static class AppUpdater
    {
        public static readonly TimeSpan FirstCheckDelay = TimeSpan.FromSeconds(5);
        private static readonly object Gate = new object();
        private static string? _scheduledPackage, _scheduledVersion, _scheduledInstallName;
        private static bool _scheduledRestart, _exitHooked;
        private static int _started;

        /// <summary>
        /// 앱 시작 시 한 번 부르면 됨. 설치된 사본이고 update.json 이 있으면 백그라운드에서 확인·다운로드하고
        /// 앱이 끝날 때 적용. onReady 는 받은 직후 (백그라운드 스레드에서) 불림: UI 스레드로 넘겨 물어보세요.
        /// log 는 진단용 (선택).
        /// </summary>
        public static void StartBackground(Action<UpdateReady>? onReady = null, Action<string>? log = null, UpdateSettings? settings = null)
        {
            if (Interlocked.Exchange(ref _started, 1) == 1) return;
            settings ??= UpdateSettings.Load();
            if (settings is null) { log?.Invoke("update.json 없음: 자동 업데이트 꺼짐"); return; }
            if (!IsInstalledCopy(settings.InstallName)) { log?.Invoke("설치된 사본이 아님: 자동 업데이트 꺼짐"); return; }
            SyncInstalledVersion(settings.InstallName);
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(FirstCheckDelay).ConfigureAwait(false);
                    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(15));
                    var update = await CheckAsync(settings, timeout.Token).ConfigureAwait(false);
                    if (update is null) { log?.Invoke("최신 버전 " + CurrentVersion); return; }
                    log?.Invoke($"새 버전 {update.Version} 받는 중");
                    string package = await DownloadAsync(settings, update, timeout.Token).ConfigureAwait(false);
                    ScheduleApply(package, update.Version, restart: false, settings.InstallName);
                    log?.Invoke($"새 버전 {update.Version}: 앱을 끝내면 적용");
                    onReady?.Invoke(new UpdateReady(update, package, settings.InstallName));
                }
                catch (Exception error)
                {
                    // 오프라인·권한 없음 등: 지금 버전을 계속 쓰고 다음 실행 때 다시 확인
                    log?.Invoke("업데이트 확인 실패: " + error.Message);
                }
            });
        }

        /// <summary>지금 실행 중인 버전 (InformationalVersion 의 +빌드정보 제외)</summary>
        public static string CurrentVersion
        {
            get
            {
                var assembly = Assembly.GetEntryAssembly() ?? typeof(AppUpdater).Assembly;
                string? informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
                if (!string.IsNullOrWhiteSpace(informational)) return informational!.Split('+')[0].Trim();
                var version = assembly.GetName().Version ?? new Version(0, 0, 0);
                return version.Revision > 0 ? version.ToString(4) : version.ToString(3);
            }
        }

        private static string UninstallKey(string installName) => @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + installName;

        /// <summary>이 프로세스가 설치 폴더(Windows "앱" 목록의 InstallLocation)에서 실행 중인지</summary>
        public static bool IsInstalledCopy(string installName)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(UninstallKey(installName));
                return key?.GetValue("InstallLocation") is string location &&
                    string.Equals(Path.GetFullPath(location.Trim('"')).TrimEnd('\\'), AppContext.BaseDirectory.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception error) when (error is System.Security.SecurityException or UnauthorizedAccessException or IOException or ArgumentException)
            {
                return false;
            }
        }

        /// <summary>앱이 스스로 파일을 바꾸므로 "앱" 목록의 버전을 실제 실행 중인 버전과 맞춤</summary>
        public static void SyncInstalledVersion(string installName)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(UninstallKey(installName), writable: true);
                if (key != null && !string.Equals(key.GetValue("DisplayVersion") as string, CurrentVersion, StringComparison.Ordinal))
                    key.SetValue("DisplayVersion", CurrentVersion);
            }
            catch (Exception error) when (error is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        }

        private static HttpClient CreateClient(UpdateSettings settings, string accept)
        {
            var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(Regex.Replace(settings.InstallName, "[^A-Za-z0-9._-]", "") is { Length: > 0 } name ? name : "App",
                Regex.Replace(CurrentVersion, "[^A-Za-z0-9._-]", "")));
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
            if (settings.Token is { Length: > 0 } token) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return client;
        }

        /// <summary>최신 릴리스가 지금보다 높으면 돌려줌</summary>
        public static async Task<AvailableUpdate?> CheckAsync(UpdateSettings settings, CancellationToken cancellationToken = default)
        {
            using var client = CreateClient(settings, "application/vnd.github+json");
            using var response = await client.GetAsync($"{settings.Api}/repos/{settings.Repository}/releases/latest", cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;  // 릴리스 없음 (또는 읽을 수 없음)
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
            var root = document.RootElement;
            string tag = root.TryGetProperty("tag_name", out var tagName) ? tagName.GetString() ?? "" : "";
            if (!VersionText.TryParse(tag, out var latest) || !VersionText.TryParse(CurrentVersion, out var current) || VersionText.Compare(latest, current) <= 0)
                return null;
            foreach (var asset in root.GetProperty("assets").EnumerateArray())
            {
                if (!string.Equals(asset.GetProperty("name").GetString(), settings.Asset, StringComparison.OrdinalIgnoreCase)) continue;
                string url = asset.GetProperty("url").GetString() ?? "";
                long size = asset.TryGetProperty("size", out var length) ? length.GetInt64() : 0;
                return url.Length == 0 ? null : new AvailableUpdate(VersionText.Display(latest), tag, url, size,
                    root.TryGetProperty("body", out var body) ? body.GetString() : null,
                    root.TryGetProperty("html_url", out var html) ? html.GetString() : null);
            }
            return null;
        }

        /// <summary>패키지를 받아 확인한 뒤 zip 경로를 돌려줌 (%LOCALAPPDATA%\&lt;InstallName&gt;\updates)</summary>
        public static async Task<string> DownloadAsync(UpdateSettings settings, AvailableUpdate update, CancellationToken cancellationToken = default)
        {
            string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), settings.InstallName, "updates");
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, $"{settings.InstallName}-{update.Version}.zip");
            string partial = path + ".part";
            using (var client = CreateClient(settings, "application/octet-stream"))
            using (var response = await client.GetAsync(update.AssetUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                using var source = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                using var target = File.Create(partial);
                await source.CopyToAsync(target, 81920, cancellationToken).ConfigureAwait(false);
            }
            if (update.Size > 0 && new FileInfo(partial).Length != update.Size) throw new IOException("업데이트 패키지가 끝까지 받아지지 않았습니다.");
            string executable = Path.GetFileName(Platform.ProcessPath ?? "");
            using (var archive = ZipFile.OpenRead(partial))
            {
                if (executable.Length > 0 && !archive.Entries.Any(entry => string.Equals(Path.GetFileName(entry.FullName), executable, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidDataException($"업데이트 패키지에 {executable}이(가) 없습니다.");
            }
            Platform.MoveOverwrite(partial, path);
            return path;
        }

        /// <summary>앱이 끝나면 패키지를 설치 폴더에 풀도록 예약 (restart 면 새 버전을 다시 시작)</summary>
        public static void ScheduleApply(string package, string version, bool restart, string? installName = null)
        {
            lock (Gate)
            {
                _scheduledPackage = package;
                _scheduledVersion = version;
                _scheduledInstallName = installName ?? _scheduledInstallName;
                _scheduledRestart |= restart;
                if (_exitHooked) return;
                _exitHooked = true;
                AppDomain.CurrentDomain.ProcessExit += (_, _) =>
                {
                    if (_scheduledPackage is { } zip) LaunchUpdater(zip, _scheduledVersion ?? "", _scheduledRestart, installName: _scheduledInstallName);
                };
            }
        }

        /// <summary>
        /// 이 프로세스가 끝나길 기다렸다가 zip 을 설치 폴더에 덮어쓰는 숨은 PowerShell 을 시작 (스크립트 파일이 아니라
        /// 인코딩된 명령이라 실행 정책에 막히지 않음). 설치 폴더 밖의 데이터와 dotnet\ 실행 환경은 건드리지 않음.
        /// </summary>
        public static void LaunchUpdater(string zipPath, string version, bool restart, string? installDirectory = null, string? installName = null)
        {
            string target = (installDirectory ?? AppContext.BaseDirectory).TrimEnd('\\');
            string executable = Platform.ProcessPath ?? Path.Combine(target, "app.exe");
            string name = installName ?? UpdateSettings.Load()?.InstallName ?? Path.GetFileNameWithoutExtension(executable);
            string log = Path.Combine(Path.GetDirectoryName(zipPath)!, "update.log");
            string script = BuildUpdaterScript(zipPath, target, Path.GetFileName(executable), Platform.ProcessId, log, version, UninstallKey(name), restart);
            string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            var start = new ProcessStartInfo("powershell.exe", $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -EncodedCommand {encoded}")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetTempPath()
            };
            Process.Start(start)?.Dispose();
        }

        public static string BuildUpdaterScript(string zipPath, string installDirectory, string executableName, int processId, string logPath,
            string version, string uninstallKey, bool restart)
        {
            static string Quote(string value) => "'" + value.Replace("'", "''") + "'";
            string start = restart ? "Start-Process -FilePath (Join-Path $target $exe) -WorkingDirectory $target" : "";
            return string.Join("\n",
                "$ErrorActionPreference = 'Stop'",
                $"$zip = {Quote(zipPath)}; $target = {Quote(installDirectory)}; $exe = {Quote(executableName)}; $log = {Quote(logPath)}",
                $"$version = {Quote(version)}; $key = {Quote("HKCU:\\" + uninstallKey)}",
                "function Log($m) { try { Add-Content -LiteralPath $log -Value ((Get-Date -Format s) + ' ' + $m) -Encoding UTF8 } catch { } }",
                "try {",
                $"  Log 'waiting for process {processId}'",
                $"  Wait-Process -Id {processId} -Timeout 600 -ErrorAction SilentlyContinue",
                "  Add-Type -AssemblyName System.IO.Compression.FileSystem",
                "  $staging = Join-Path $env:TEMP ('AppUpdate_' + [guid]::NewGuid().ToString('N'))",
                "  [IO.Compression.ZipFile]::ExtractToDirectory($zip, $staging)",
                "  $root = $staging",
                "  if (-not (Test-Path -LiteralPath (Join-Path $root $exe))) {",
                "    $sub = Get-ChildItem -LiteralPath $staging -Directory | Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName $exe) } | Select-Object -First 1",
                "    if ($sub) { $root = $sub.FullName } else { throw ('package has no ' + $exe) }",
                "  }",
                "  $ok = $false",
                "  for ($i = 0; $i -lt 20 -and -not $ok; $i++) {",
                "    & robocopy.exe $root $target /E /R:2 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null",
                "    if ($LASTEXITCODE -lt 8) { $ok = $true } else { Start-Sleep -Seconds 1 }",
                "  }",
                "  if (-not $ok) { throw ('copy failed, robocopy exit ' + $LASTEXITCODE) }",
                "  if ($version -and (Test-Path -LiteralPath $key)) { Set-ItemProperty -LiteralPath $key -Name DisplayVersion -Value $version }",
                "  Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue",
                "  Remove-Item -LiteralPath $zip -Force -ErrorAction SilentlyContinue",
                "  Log ('updated to ' + $version)",
                "  " + start,
                "} catch {",
                "  Log ('failed: ' + $_.Exception.Message)",
                "  " + start,
                "}");
        }
    }

    /// <summary>.NET 6+ 와 .NET Framework 4.8 차이 (.NET Framework 앱: SDK 형식 프로젝트, LangVersion latest, System.Text.Json 패키지)</summary>
    internal static class Platform
    {
#if NETFRAMEWORK
        public static string? ProcessPath { get { using var process = Process.GetCurrentProcess(); return process.MainModule?.FileName; } }
        public static int ProcessId { get { using var process = Process.GetCurrentProcess(); return process.Id; } }
        public static void MoveOverwrite(string source, string destination)
        {
            if (File.Exists(destination)) File.Delete(destination);
            File.Move(source, destination);
        }
#else
        public static string? ProcessPath => Environment.ProcessPath;
        public static int ProcessId => Environment.ProcessId;
        public static void MoveOverwrite(string source, string destination) => File.Move(source, destination, overwrite: true);
#endif
    }

    /// <summary>v1.2.3 / 1.2.3.4 / 1.2.3-beta.1 / 1.2.3+build 비교 (빌드 정보는 무시, 정식 &gt; 프리릴리스)</summary>
    internal static class VersionText
    {
        private static readonly Regex Pattern = new(@"(?<![0-9A-Za-z])[vV]?(?<num>\d+(?:\.\d+){0,3})(?:-(?<pre>[0-9A-Za-z][0-9A-Za-z.\-]*))?(?:\+[0-9A-Za-z.\-]+)?$");

        public static bool TryParse(string? text, out (int[] Numbers, string Pre) version)
        {
            version = (new int[4], "");
            var match = Pattern.Match((text ?? "").Trim());
            if (!match.Success) return false;
            string[] parts = match.Groups["num"].Value.Split('.');
            for (int i = 0; i < parts.Length; i++)
                if (!int.TryParse(parts[i], out version.Numbers[i])) return false;
            version.Pre = match.Groups["pre"].Success ? match.Groups["pre"].Value : "";
            return true;
        }

        public static int Compare((int[] Numbers, string Pre) left, (int[] Numbers, string Pre) right)
        {
            for (int i = 0; i < 4; i++)
                if (left.Numbers[i] != right.Numbers[i]) return left.Numbers[i].CompareTo(right.Numbers[i]);
            if (left.Pre.Length == 0 || right.Pre.Length == 0) return (right.Pre.Length > 0).CompareTo(left.Pre.Length > 0);
            string[] a = left.Pre.Split('.'), b = right.Pre.Split('.');
            for (int i = 0; i < Math.Min(a.Length, b.Length); i++)
            {
                bool an = long.TryParse(a[i], out long av), bn = long.TryParse(b[i], out long bv);
                int c = an && bn ? av.CompareTo(bv) : an ? -1 : bn ? 1 : string.CompareOrdinal(a[i], b[i]);
                if (c != 0) return Math.Sign(c);
            }
            return a.Length.CompareTo(b.Length);
        }

        public static string Display((int[] Numbers, string Pre) version)
        {
            string core = version.Numbers[3] != 0 ? string.Join(".", version.Numbers) : $"{version.Numbers[0]}.{version.Numbers[1]}.{version.Numbers[2]}";
            return version.Pre.Length > 0 ? core + "-" + version.Pre : core;
        }
    }
}

#if NETFRAMEWORK
// record 의 init 접근자용 (.NET Framework 에는 없음)
namespace System.Runtime.CompilerServices { internal static class IsExternalInit { } }
#endif
