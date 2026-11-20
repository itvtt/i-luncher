// i-luncher AppKit: 앱 사용량(실제로 쓰는 시간과 작업량)을 API 서버로 모으는 파일 하나짜리 집계기 (.NET 6 이상과 .NET Framework 4.8).
// 원본은 i-luncher 저장소의 AppKit\AppUsage.cs 입니다. AppUpdater.cs 없이도 컴파일되므로 이 파일만 복사해 써도 됩니다.
//
// 쓰는 법 (앱 시작 때 한 번):
//   ILuncher.AppKit.AppUsage.Start();                                  // 화면에서 쓰는 앱: 사용 시간
//   ILuncher.AppKit.AppUsage.Start(ILuncher.AppKit.UsageKind.Resident); // 상주·감시 앱: 사용 시간 + 가동 시간
//   ILuncher.AppKit.AppUsage.Count("capture");                         // 작업 한 건
//   ILuncher.AppKit.AppUsage.Count("copy-bytes", bytes);                // 작업 한 건 + 양 (바이트·파일 수 등)
//   using (ILuncher.AppKit.AppUsage.Measure("copy-time")) { ... }      // 작업 한 건 + 걸린 초
//
// 자동으로 재는 것 (10초마다):
//   active  = 사용 시간(초): 맨 앞 창이 이 앱이고 마지막 키보드·마우스 입력이 2분 안
//   running = 가동 시간(초, Resident 만): 앱이 떠 있고 사용자가 PC 앞에 있음 (잠금 아님, 입력 5분 안)
//   launch  = 실행 횟수
// 모은 값은 날짜별로 5분마다 <API 서버>/launcher/usage 로 보내고, 못 보내면 %LOCALAPPDATA%\<앱>\usage-pending.json 에 두었다가 다시 보냄.
// API 서버 주소: 설치된 i-luncher 의 launcher.env (API_SERVER), 없으면 앱 옆 update.json 의 Api 가 .../api/v3 일 때 그 앞부분.
// 사용자는 PC 이름·계정의 해시 앞 12자리로만 구분합니다 (이름은 보내지 않음). bin\Debug·bin\Release 에서 도는 개발 빌드는 보내지도,
// usage-pending.json 에 남기지도 않습니다 (남기면 같은 PC 에 설치된 앱이 나중에 개발 중 기록까지 보냄).
// 다른 DLL 을 쓰지 않습니다 (JSON 도 직접 읽고 씀): 실행 파일만 복사해 도는 서비스 사본에서도 동작.
#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32;

namespace ILuncher.AppKit
{
    /// <summary>Interactive = 화면에서 쓰는 앱 (사용 시간), Resident = 상주·감시 앱 (사용 시간 + 가동 시간)</summary>
    public enum UsageKind { Interactive, Resident }

    public static class AppUsage
    {
        public const string Active = "active", Running = "running", Launch = "launch";
        public static readonly TimeSpan Tick = TimeSpan.FromSeconds(10);
        public static readonly TimeSpan SendInterval = TimeSpan.FromMinutes(5);
        internal static readonly TimeSpan ActiveIdle = TimeSpan.FromMinutes(2), PresentIdle = TimeSpan.FromMinutes(5);
        private const int KeepDays = 90;
        private static readonly Regex MetricName = new Regex("^[a-z0-9][a-z0-9._-]{0,39}$");

        private static readonly object Gate = new object();
        private static Dictionary<string, Dictionary<string, double[]>> _days = new Dictionary<string, Dictionary<string, double[]>>();
        private static System.Threading.Timer? _timer;
        private static Stopwatch? _clock;
        private static TimeSpan _lastTick, _lastSend;
        private static int _sending;
        private static UsageKind _kind;
        private static string _app = "", _pendingPath = "";

        /// <summary>테스트·특수한 경우: 이 주소로 보냄 (설정하면 개발 빌드도 보냄)</summary>
        public static string? ServerOverride { get; set; }
        /// <summary>테스트용: (이 앱 창이 맨 앞인지, 마지막 입력 뒤 지난 시간, 화면이 잠겼는지)</summary>
        internal static Func<(bool Foreground, TimeSpan Idle, bool Locked)>? Probe { get; set; }
        internal static Func<DateTime>? Today { get; set; }
        public static string App { get { lock (Gate) return _app; } }
        public static bool Started => _timer != null;

        /// <summary>앱 시작 때 한 번. app 을 비우면 update.json 의 InstallName, 없으면 실행 파일 이름</summary>
        public static void Start(UsageKind kind = UsageKind.Interactive, string? app = null)
        {
            lock (Gate)
            {
                if (_timer != null) return;
                _kind = kind;
                _app = Clean(app) ?? Clean(ReadUpdateJson("InstallName")) ?? Clean(Assembly.GetEntryAssembly()?.GetName().Name) ?? "App";
                _pendingPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), _app, "usage-pending.json");
                _clock = Stopwatch.StartNew();
                _lastTick = _lastSend = TimeSpan.Zero;
                AddLocked(Launch, 1, 0);
                _timer = new System.Threading.Timer(_ => OnTick(), null, Tick, Tick);
            }
            AppDomain.CurrentDomain.ProcessExit += (_, _) => Stop();
            // 지난번에 못 보낸 것은 조금 뒤 함께 보냄
            ThreadPool.QueueUserWorkItem(_ => { Thread.Sleep(TimeSpan.FromSeconds(15)); TrySend(TimeSpan.FromSeconds(20)); });
        }

        /// <summary>작업 한 건 (amount = 그 작업의 양: 바이트·파일 수 등, 없으면 0)</summary>
        public static void Count(string metric, double amount = 0)
        {
            if (!MetricName.IsMatch(metric ?? "") || double.IsNaN(amount) || double.IsInfinity(amount)) return;
            lock (Gate) AddLocked(metric!, 1, Math.Max(0, amount));
        }

        /// <summary>작업 한 건 + 걸린 시간(초). using 으로 감싸면 끝날 때 기록</summary>
        public static IDisposable Measure(string metric) => new Measurement(metric);

        private sealed class Measurement : IDisposable
        {
            private readonly string _metric;
            private readonly Stopwatch _watch = Stopwatch.StartNew();
            private int _done;
            public Measurement(string metric) => _metric = metric;
            public void Dispose() { if (Interlocked.Exchange(ref _done, 1) == 0) Count(_metric, _watch.Elapsed.TotalSeconds); }
        }

        /// <summary>지금까지 모은 것을 바로 보내 봄 (못 보내면 파일로 둠)</summary>
        public static void Flush() => TrySend(TimeSpan.FromSeconds(5));

        /// <summary>종료: 마지막 시간을 더하고 짧게 보내 본 뒤 못 보낸 것은 파일로</summary>
        public static void Stop()
        {
            System.Threading.Timer? timer;
            lock (Gate) { timer = _timer; _timer = null; }
            if (timer == null) return;
            timer.Dispose();
            TickOnce();
            TrySend(TimeSpan.FromSeconds(1.5));
        }

        private static void OnTick()
        {
            try
            {
                TickOnce();
                TimeSpan now = _clock?.Elapsed ?? TimeSpan.Zero;
                if (now - _lastSend >= SendInterval)
                {
                    _lastSend = now;
                    TrySend(TimeSpan.FromSeconds(20));
                }
            }
            catch { }  // 집계가 앱을 멈추게 하지 않음
        }

        internal static void TickOnce()
        {
            var (foreground, idle, locked) = (Probe ?? ProbeWindows)();
            lock (Gate)
            {
                if (_clock == null) return;
                TimeSpan now = _clock.Elapsed;
                // 절전·멈춤으로 틱이 늦으면 그 사이는 세지 않음 (한 틱의 두 배까지만)
                double seconds = Math.Min((now - _lastTick).TotalSeconds, Tick.TotalSeconds * 2);
                _lastTick = now;
                if (seconds <= 0 || locked) return;
                if (foreground && idle < ActiveIdle) AddLocked(Active, 0, seconds);
                if (_kind == UsageKind.Resident && idle < PresentIdle) AddLocked(Running, 0, seconds);
            }
        }

        private static void AddLocked(string metric, double count, double amount)
        {
            string day = (Today?.Invoke() ?? DateTime.Now).ToString("yyyy-MM-dd");
            if (!_days.TryGetValue(day, out var metrics)) _days[day] = metrics = new Dictionary<string, double[]>();
            if (!metrics.TryGetValue(metric, out var value)) metrics[metric] = value = new double[2];
            value[0] += count;
            value[1] += amount;
        }

        // ── 보내기 ─────────────────────────────────────────────────────────────────────────────

        internal static bool TrySend(TimeSpan timeout)
        {
            if (Interlocked.Exchange(ref _sending, 1) == 1) return false;
            try
            {
                Dictionary<string, Dictionary<string, double[]>> batch;
                string app;
                lock (Gate)
                {
                    if (_app.Length == 0) return false;
                    batch = _days;
                    _days = new Dictionary<string, Dictionary<string, double[]>>();
                    app = _app;
                }
                // 개발 빌드: 버림. 같은 PC 에 설치된 앱의 대기 파일도 건드리지 않음
                if (IsDevelopmentBuild()) return true;
                using (var mutex = new Mutex(false, @"Local\AppUsage-" + app))
                {
                    bool owned = false;
                    try { owned = mutex.WaitOne(TimeSpan.FromSeconds(3)); } catch (AbandonedMutexException) { owned = true; }
                    try
                    {
                        Merge(batch, TakePendingFile());
                        Trim(batch);
                        bool sent = batch.Count == 0;
                        string? server = Server();
                        if (!sent && server != null)
                        {
                            try { sent = Post(server, app, batch, timeout); }
                            catch { sent = false; }
                        }
                        if (!sent && batch.Count > 0) SavePendingFile(batch);
                        return sent;
                    }
                    finally { if (owned) mutex.ReleaseMutex(); }
                }
            }
            catch { return false; }
            finally { Interlocked.Exchange(ref _sending, 0); }
        }

        internal static string Payload(string app, Dictionary<string, Dictionary<string, double[]>> days) =>
            "{\"app\":\"" + Safe(app) + "\",\"version\":\"" + Safe(Version()) + "\",\"user\":\"" + UserId() + "\",\"days\":" + DaysJson(days, 3) + "}";

        /// <summary>{"YYYY-MM-DD":{"지표":[건수,양]}} (보낼 때와 대기 파일이 같은 모양)</summary>
        private static string DaysJson(Dictionary<string, Dictionary<string, double[]>> days, int? digits)
        {
            var text = new StringBuilder("{");
            foreach (var day in days.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                if (text.Length > 1) text.Append(',');
                text.Append('"').Append(day.Key).Append("\":{");
                bool first = true;
                foreach (var metric in day.Value)
                {
                    if (!first) text.Append(',');
                    first = false;
                    text.Append('"').Append(metric.Key).Append("\":[").Append(Number(metric.Value[0], digits)).Append(',').Append(Number(metric.Value[1], digits)).Append(']');
                }
                text.Append('}');
            }
            return text.Append('}').ToString();
        }

        private static string Number(double value, int? digits) =>
            (digits is int places ? Math.Round(value, places) : value).ToString("R", CultureInfo.InvariantCulture);

        /// <summary>이름·버전에는 영문·숫자·.-_+ 만 (따옴표 등이 JSON 을 깨지 않게)</summary>
        private static string Safe(string value) => Regex.Replace(value ?? "", "[^A-Za-z0-9._+-]", "");

        private static bool Post(string server, string app, Dictionary<string, Dictionary<string, double[]>> days, TimeSpan timeout)
        {
            using var client = new HttpClient { Timeout = timeout };
            using var content = new StringContent(Payload(app, days), Encoding.UTF8, "application/json");
            client.DefaultRequestHeaders.UserAgent.ParseAdd(Regex.Replace(app, "[^A-Za-z0-9._-]", "") + "/" + Regex.Replace(Version(), "[^A-Za-z0-9._-]", ""));
            using var response = client.PostAsync(server.TrimEnd('/') + "/launcher/usage", content).GetAwaiter().GetResult();
            int status = (int)response.StatusCode;
            // 4xx (형식 거절)는 다시 보내도 같으므로 버림. 5xx·연결 실패만 다시 보냄
            return status < 500;
        }

        // ── 못 보낸 것 (파일) ──────────────────────────────────────────────────────────────────

        private static Dictionary<string, Dictionary<string, double[]>> TakePendingFile()
        {
            var days = new Dictionary<string, Dictionary<string, double[]>>();
            try
            {
                if (_pendingPath.Length == 0 || !File.Exists(_pendingPath)) return days;
                days = ParseDays(File.ReadAllText(_pendingPath));
                File.Delete(_pendingPath);
            }
            catch { }
            return days;
        }

        private static readonly Regex DayBlock = new Regex("\"(\\d{4}-\\d{2}-\\d{2})\"\\s*:\\s*\\{([^{}]*)\\}");
        private static readonly Regex MetricPair = new Regex("\"([a-z0-9][a-z0-9._-]{0,39})\"\\s*:\\s*\\[\\s*([-+0-9.eE]+)\\s*,\\s*([-+0-9.eE]+)\\s*\\]");

        /// <summary>DaysJson 이 쓴 모양을 다시 읽음 (맞지 않는 부분은 건너뜀)</summary>
        internal static Dictionary<string, Dictionary<string, double[]>> ParseDays(string json)
        {
            var days = new Dictionary<string, Dictionary<string, double[]>>();
            foreach (Match day in DayBlock.Matches(json ?? ""))
            {
                var metrics = new Dictionary<string, double[]>();
                foreach (Match metric in MetricPair.Matches(day.Groups[2].Value))
                    if (double.TryParse(metric.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double count) &&
                        double.TryParse(metric.Groups[3].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double amount))
                        metrics[metric.Groups[1].Value] = new[] { count, amount };
                if (metrics.Count > 0) days[day.Groups[1].Value] = metrics;
            }
            return days;
        }

        private static void SavePendingFile(Dictionary<string, Dictionary<string, double[]>> days)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_pendingPath)!);
                string temp = _pendingPath + ".tmp";
                File.WriteAllText(temp, DaysJson(days, null), new UTF8Encoding(false));
                if (File.Exists(_pendingPath)) File.Delete(_pendingPath);
                File.Move(temp, _pendingPath);
            }
            catch { }
        }

        internal static void Merge(Dictionary<string, Dictionary<string, double[]>> into, Dictionary<string, Dictionary<string, double[]>> from)
        {
            foreach (var day in from)
            {
                if (!into.TryGetValue(day.Key, out var metrics)) into[day.Key] = metrics = new Dictionary<string, double[]>();
                foreach (var metric in day.Value)
                {
                    if (!metrics.TryGetValue(metric.Key, out var value)) metrics[metric.Key] = value = new double[2];
                    value[0] += metric.Value[0];
                    value[1] += metric.Value[1];
                }
            }
        }

        private static void Trim(Dictionary<string, Dictionary<string, double[]>> days)
        {
            string oldest = (Today?.Invoke() ?? DateTime.Now).AddDays(-KeepDays).ToString("yyyy-MM-dd");
            foreach (string day in days.Keys.Where(day => string.CompareOrdinal(day, oldest) < 0).ToList()) days.Remove(day);
        }

        // ── 서버 주소 · 사용자 · 버전 ──────────────────────────────────────────────────────────

        /// <summary>보낼 API 서버 (끝의 / 없음). 없으면 null (파일에만 모아 둠)</summary>
        public static string? Server()
        {
            if (!string.IsNullOrWhiteSpace(ServerOverride)) return ServerOverride!.Trim().TrimEnd('/');
            if (IsDevelopmentBuild()) return null;
            return LauncherServer() ?? UpdateServer();
        }

        /// <summary>bin\Debug · bin\Release 에서 도는 개발 빌드 (ServerOverride 를 정하면 아님): 보내지도 파일에 두지도 않음</summary>
        internal static bool IsDevelopmentBuild()
        {
            if (!string.IsNullOrWhiteSpace(ServerOverride)) return false;
            string baseDirectory = AppContext.BaseDirectory.Replace('/', '\\');
            return baseDirectory.IndexOf(@"\bin\Debug\", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   baseDirectory.IndexOf(@"\bin\Release\", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>설치된 i-luncher 의 launcher.env 의 API_SERVER</summary>
        internal static string? LauncherServer()
        {
            var folders = new List<string>();
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\i-luncher");
                if (key?.GetValue("InstallLocation") is string location && location.Trim('"').Length > 0) folders.Add(location.Trim('"'));
            }
            catch { }
            folders.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "i-luncher"));
            foreach (string folder in folders)
            {
                try
                {
                    string path = Path.Combine(folder, "launcher.env");
                    if (!File.Exists(path)) continue;
                    foreach (string line in File.ReadAllLines(path))
                    {
                        string text = line.Trim().TrimStart('﻿');
                        if (!text.StartsWith("API_SERVER=", StringComparison.Ordinal)) continue;
                        string value = text.Substring("API_SERVER=".Length).Trim().Trim('"').TrimEnd('/');
                        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && (uri.Scheme == "http" || uri.Scheme == "https")) return value;
                    }
                }
                catch { }
            }
            return null;
        }

        /// <summary>update.json 의 Api 가 API 서버 중계(.../api/v3, github 주소 아님)일 때 그 서버</summary>
        internal static string? UpdateServer()
        {
            string api = (ReadUpdateJson("Api") ?? "").TrimEnd('/');
            if (!api.EndsWith("/api/v3", StringComparison.OrdinalIgnoreCase)) return null;
            string server = api.Substring(0, api.Length - "/api/v3".Length);
            if (!Uri.TryCreate(server, UriKind.Absolute, out var uri) || uri.Host.IndexOf("github", StringComparison.OrdinalIgnoreCase) >= 0) return null;
            return server;
        }

        /// <summary>앱 옆 update.json 의 문자열 값 하나 (AppKit.targets 가 만드는 한 줄짜리 파일)</summary>
        private static string? ReadUpdateJson(string name)
        {
            try
            {
                string path = Path.Combine(AppContext.BaseDirectory, "update.json");
                if (!File.Exists(path)) return null;
                var match = Regex.Match(File.ReadAllText(path), "\"" + Regex.Escape(name) + "\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
                return match.Success ? Regex.Unescape(match.Groups[1].Value) : null;
            }
            catch { return null; }
        }

        private static string? Clean(string? name)
        {
            string value = Regex.Replace((name ?? "").Trim(), "[^A-Za-z0-9._-]", "");
            return value.Length == 0 ? null : value.Length > 64 ? value.Substring(0, 64) : value;
        }

        /// <summary>PC 이름·계정의 SHA-256 앞 12자리 (사람 이름은 보내지 않음)</summary>
        internal static string UserId()
        {
            using var sha = SHA256.Create();
            byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes((Environment.MachineName + "\\" + Environment.UserName).ToLowerInvariant()));
            return string.Concat(hash.Take(6).Select(b => b.ToString("x2")));
        }

        private static string Version()
        {
            var assembly = Assembly.GetEntryAssembly() ?? typeof(AppUsage).Assembly;
            string? informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            return !string.IsNullOrWhiteSpace(informational) ? informational!.Split('+')[0].Trim() : (assembly.GetName().Version ?? new Version(0, 0)).ToString();
        }

        // ── Windows: 맨 앞 창 · 입력 · 잠금 ─────────────────────────────────────────────────────

        private static readonly int ProcessId = GetProcessId();
        private static int GetProcessId() { using var process = Process.GetCurrentProcess(); return process.Id; }

        private static (bool, TimeSpan, bool) ProbeWindows()
        {
            IntPtr window = GetForegroundWindow();
            // 잠금 화면·로그인 화면에서는 맨 앞 창이 없음
            if (window == IntPtr.Zero) return (false, TimeSpan.MaxValue, true);
            GetWindowThreadProcessId(window, out uint owner);
            var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf(typeof(LASTINPUTINFO)) };
            TimeSpan idle = GetLastInputInfo(ref info) ? TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - info.dwTime)) : TimeSpan.Zero;
            return (owner == (uint)ProcessId, idle, false);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }

        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
        [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LASTINPUTINFO info);

        /// <summary>테스트용: 모은 값 그대로 (날짜 → 지표 → [건수, 양])</summary>
        internal static Dictionary<string, Dictionary<string, double[]>> Snapshot()
        {
            lock (Gate) return _days.ToDictionary(day => day.Key, day => day.Value.ToDictionary(metric => metric.Key, metric => (double[])metric.Value.Clone()));
        }

        /// <summary>테스트용: 처음 상태로</summary>
        internal static void ResetForTests(string app, UsageKind kind)
        {
            lock (Gate)
            {
                _timer?.Dispose();
                _timer = null;
                _days = new Dictionary<string, Dictionary<string, double[]>>();
                _app = app;
                _kind = kind;
                _pendingPath = Path.Combine(Path.GetTempPath(), "AppUsageChecks-" + app, "usage-pending.json");
                _clock = Stopwatch.StartNew();
                _lastTick = _lastSend = TimeSpan.Zero;
            }
        }

        internal static string PendingPath { get { lock (Gate) return _pendingPath; } }
    }
}
