using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace IApi;

/// <summary>Windows 함수 (프로세스 목록 · 시작 · 끝내기, 콘솔, 사용자 환경). Windows 에서만 부름</summary>
internal static class Native
{
    private const uint Th32csSnapProcess = 0x2;
    private const uint ProcessQueryLimitedInformation = 0x1000, ProcessTerminate = 0x0001, TokenQuery = 0x0008;
    public const uint CreateNewConsole = 0x10, CreateUnicodeEnvironment = 0x400, CreateBreakawayFromJob = 0x01000000,
        DetachedProcess = 0x8, CreateNewProcessGroup = 0x200, CreateNoWindow = 0x08000000;
    private const int StartfUseShowWindow = 0x1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint Size, Usage, ProcessId;
        public IntPtr DefaultHeapId;
        public uint ModuleId, Threads, ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExeFile;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size;
        public string? Reserved, Desktop, Title;
        public int X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public short ShowWindow, Reserved2Size;
        public IntPtr Reserved2, StdInput, StdOutput, StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process, Thread;
        public int ProcessId, ThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool Process32FirstW(IntPtr snapshot, ref ProcessEntry32 entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool Process32NextW(IntPtr snapshot, ref ProcessEntry32 entry);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, StringBuilder name, ref uint size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateProcess(IntPtr process, uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GetCurrentProcess();
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("userenv.dll", SetLastError = true)] private static extern bool CreateEnvironmentBlock(out IntPtr block, IntPtr token, bool inherit);
    [DllImport("userenv.dll", SetLastError = true)] private static extern bool DestroyEnvironmentBlock(IntPtr block);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessW(string? application, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles,
        uint flags, IntPtr environment, string? currentDirectory, ref StartupInfo startup, out ProcessInformation information);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CreateHardLinkW(string link, string existing, IntPtr security);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool AttachConsole(int processId);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool FreeConsole();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GenerateConsoleCtrlEvent(uint ctrlEvent, uint processGroupId);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetConsoleCtrlHandler(ConsoleHandler? handler, bool add);
    [DllImport("kernel32.dll")] public static extern uint GetOEMCP();
    [DllImport("kernel32.dll")] public static extern uint GetACP();
    [DllImport("shell32.dll")] private static extern bool IsUserAnAdmin();

    private delegate bool ConsoleHandler(uint ctrlType);

    /// <summary>[(pid, 부모 pid, 프로그램 파일 이름)] (Windows 가 아니면 빈 목록)</summary>
    public static List<(int Pid, int Parent, string Name)> Processes()
    {
        var found = new List<(int, int, string)>();
        if (!OperatingSystem.IsWindows()) return found;
        IntPtr snapshot = CreateToolhelp32Snapshot(Th32csSnapProcess, 0);
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1)) return found;
        try
        {
            var entry = new ProcessEntry32 { Size = (uint)Marshal.SizeOf<ProcessEntry32>() };
            for (bool more = Process32FirstW(snapshot, ref entry); more; more = Process32NextW(snapshot, ref entry))
                found.Add(((int)entry.ProcessId, (int)entry.ParentProcessId, entry.ExeFile ?? ""));
        }
        finally { CloseHandle(snapshot); }
        return found;
    }

    /// <summary>프로세스가 돌리는 프로그램 파일 (정션을 따라간 경로). 알 수 없으면 ""</summary>
    public static string ProcessImage(int pid)
    {
        if (!OperatingSystem.IsWindows()) return "";
        IntPtr handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero) return "";
        try
        {
            var buffer = new StringBuilder(32768);
            uint size = (uint)buffer.Capacity;
            return QueryFullProcessImageNameW(handle, 0, buffer, ref size) ? buffer.ToString() : "";
        }
        finally { CloseHandle(handle); }
    }

    public static bool Kill(int pid)
    {
        if (!OperatingSystem.IsWindows()) return false;
        IntPtr handle = OpenProcess(ProcessTerminate, false, pid);
        if (handle == IntPtr.Zero) return false;
        try { return TerminateProcess(handle, 1); }
        finally { CloseHandle(handle); }
    }

    public static bool IsAdministrator()
    {
        try { return OperatingSystem.IsWindows() && IsUserAnAdmin(); }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException) { return false; }
    }

    /// <summary>새 로그온이 받을 이 계정의 환경 (서버가 시작한 뒤 바꾼 변수도). 만들 수 없으면 이 프로세스의 것</summary>
    public static Dictionary<string, string> UserEnvironment()
    {
        var own = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
            .ToDictionary(entry => (string)entry.Key, entry => (string?)entry.Value ?? "", StringComparer.OrdinalIgnoreCase);
        if (!OperatingSystem.IsWindows()) return own;
        if (!OpenProcessToken(GetCurrentProcess(), TokenQuery, out IntPtr token)) return own;
        try
        {
            if (!CreateEnvironmentBlock(out IntPtr block, token, false)) return own;
            try
            {
                var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                int offset = 0;
                while (true)
                {
                    string? entry = Marshal.PtrToStringUni(block + offset);
                    if (string.IsNullOrEmpty(entry)) break;
                    offset += (entry.Length + 1) * 2;
                    int equals = entry.IndexOf('=', 1);
                    if (equals > 0) environment[entry[..equals]] = entry[(equals + 1)..];
                }
                return environment.Count > 0 ? environment : own;
            }
            finally { DestroyEnvironmentBlock(block); }
        }
        finally { CloseHandle(token); }
    }

    /// <summary>환경 블록 (UTF-16, 이름순, 두 번 0 으로 끝남)</summary>
    private static IntPtr EnvironmentBlock(IReadOnlyDictionary<string, string> environment)
    {
        var text = new StringBuilder();
        foreach (var pair in environment.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)) text.Append(pair.Key).Append('=').Append(pair.Value).Append('\0');
        text.Append('\0');
        return Marshal.StringToHGlobalUni(text.ToString());
    }

    /// <summary>
    /// 프로그램을 따로 시작 (부모의 핸들을 물려주지 않음: 시작한 쪽이 출력을 읽고 있어도 기다리지 않게). 창 없이, 가능하면 부모의 작업 개체 밖에서. pid
    /// </summary>
    public static int Start(string commandLine, string? folder, uint flags, IReadOnlyDictionary<string, string>? environment = null, bool hidden = true)
    {
        IntPtr block = environment is null ? IntPtr.Zero : EnvironmentBlock(environment);
        try
        {
            foreach (uint extra in new[] { CreateBreakawayFromJob, 0u })
            {
                var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>() };
                if (hidden)
                {
                    startup.Flags = StartfUseShowWindow;
                    startup.ShowWindow = 0;
                }
                uint all = flags | extra | (block != IntPtr.Zero ? CreateUnicodeEnvironment : 0);
                if (CreateProcessW(null, new StringBuilder(commandLine), IntPtr.Zero, IntPtr.Zero, false, all, block, folder, ref startup, out var information))
                {
                    CloseHandle(information.Thread);
                    CloseHandle(information.Process);
                    return information.ProcessId;
                }
                if (extra == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            throw new Win32Exception();
        }
        finally
        {
            if (block != IntPtr.Zero) Marshal.FreeHGlobal(block);
        }
    }

    public static bool HardLink(string link, string existing) => OperatingSystem.IsWindows() && CreateHardLinkW(link, existing, IntPtr.Zero);

    /// <summary>
    /// i-api.exe --ctrl-break &lt;pid&gt;: 그 프로세스의 (숨은) 콘솔에 Ctrl+Break. 콘솔에 붙는 것은 프로세스 전체라 짧은 도우미 프로세스로 함 (서버는 그동안 요청을 받음).
    /// Ctrl+Break 는 Ctrl+C 를 무시하게 시작한 프로세스에도 닿음. 러너는 GitHub 세션을 닫고 끝남
    /// </summary>
    public static int CtrlBreakHelper(int pid)
    {
        FreeConsole();
        if (!AttachConsole(pid)) return 2;
        ConsoleHandler keep = _ => true;
        SetConsoleCtrlHandler(keep, true);
        bool sent = GenerateConsoleCtrlEvent(1, 0);
        Thread.Sleep(500);
        FreeConsole();
        GC.KeepAlive(keep);
        return sent ? 0 : 3;
    }

    /// <summary>Ctrl+Break 를 그 프로세스의 콘솔로 (이 프로그램을 도우미로 띄움)</summary>
    public static bool SendCtrlBreak(int pid)
    {
        try
        {
            var start = new ProcessStartInfo(Program.ExecutablePath) { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("--ctrl-break");
            start.ArgumentList.Add(pid.ToString(System.Globalization.CultureInfo.InvariantCulture));
            using var helper = Process.Start(start)!;
            if (!helper.WaitForExit(30_000))
            {
                try { helper.Kill(); } catch (InvalidOperationException) { }
                return false;
            }
            return helper.ExitCode == 0;
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException) { return false; }
    }

    /// <summary>Windows 콘솔 프로그램의 출력 (schtasks, config.cmd): UTF-8, 아니면 콘솔 · Windows 코드 페이지</summary>
    public static string ConsoleText(byte[] data)
    {
        try { return new UTF8Encoding(false, true).GetString(data); }
        catch (DecoderFallbackException) { }
        if (OperatingSystem.IsWindows())
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            foreach (uint page in new[] { GetOEMCP(), GetACP() })
            {
                try { return Encoding.GetEncoding((int)page, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback).GetString(data); }
                catch (Exception error) when (error is DecoderFallbackException or ArgumentException or NotSupportedException) { }
            }
        }
        return Encoding.UTF8.GetString(data);
    }

    /// <summary>짧은 명령을 창 없이 돌림: 종료 코드와 출력 (돌리지 못하면 코드 null)</summary>
    public static (int? Code, string Output) Run(string program, IEnumerable<string> arguments, int seconds = 60, IReadOnlyDictionary<string, string>? environment = null,
        string? folder = null)
    {
        try
        {
            var start = new ProcessStartInfo(program)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
                WorkingDirectory = folder ?? "",
            };
            foreach (string argument in arguments) start.ArgumentList.Add(argument);
            if (environment is not null)
            {
                start.Environment.Clear();
                foreach (var (key, value) in environment) start.Environment[key] = value;
            }
            using var process = Process.Start(start)!;
            process.StandardInput.Close();
            var output = new MemoryStream();
            var error = new MemoryStream();
            var reading = Task.WhenAll(process.StandardOutput.BaseStream.CopyToAsync(output), process.StandardError.BaseStream.CopyToAsync(error));
            if (!process.WaitForExit(seconds * 1000))
            {
                try { process.Kill(true); } catch (InvalidOperationException) { }
                return (null, "");
            }
            reading.Wait(5000);
            return (process.ExitCode, ConsoleText(output.ToArray().Concat(error.ToArray()).ToArray()));
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException or IOException) { return (null, error.Message); }
    }
}
