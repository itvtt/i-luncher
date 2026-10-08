using System.Collections;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;
using Microsoft.Win32.SafeHandles;

namespace TerminalNotes.TerminalTransport;

/// <summary>Windows terminal process backed by the Windows 10 1809+ ConPTY API.</summary>
internal sealed class WindowsConPtyProcess : ITerminalProcess
{
    private const uint ProcThreadAttributePseudoConsole = 0x00020016;
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint CreateSuspended = 0x00000004;
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;
    private const uint Infinite = 0xFFFFFFFF;
    private readonly object _gate = new();
    private SafePseudoConsoleHandle? _pseudoConsole;
    private SafeKernelHandle? _process;
    private SafeKernelHandle? _job;
    private FileStream? _input;
    private FileStream? _output;
    private Channel<byte[]>? _writes;
    private Task? _outputTask;
    private Task? _writeTask;
    private Task? _exitTask;
    private Task? _closeTask;
    private Exception? _inputError;
    private bool _started;
    private bool _running;
    private int _columns;
    private int _rows;

    public event Action<byte[]>? OutputReceived;
    public event Action<TerminalProcessExit>? Exited;
    public event Action<Exception>? InputFailed;
    public bool IsRunning { get { lock (_gate) return _running; } }

    public Task StartAsync(TerminalProcessOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateOptions(options);
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
            throw new PlatformNotSupportedException("ConPTY requires Windows 10 version 1809 or newer.");

        string workingDirectory = Path.GetFullPath(options.WorkingDirectory);
        if (!Directory.Exists(workingDirectory)) throw new DirectoryNotFoundException(workingDirectory);
        SortedDictionary<string, string> environment = CreateEffectiveEnvironment(options.Environment);
        string executablePath = ResolveExecutablePath(options.FileName, workingDirectory, environment);
        lock (_gate)
        {
            if (_started) throw new InvalidOperationException("A terminal process can be started only once.");
            _started = true;
            StartCore(options, workingDirectory, executablePath, environment);
        }
        return Task.CompletedTask;
    }

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        if (data.IsEmpty) return;
        Channel<byte[]> queue;
        lock (_gate)
        {
            if (!_running || _writes is null) throw new InvalidOperationException("The terminal process is not running.");
            if (_inputError is { } inputError) throw new IOException("The terminal input writer has failed.", inputError);
            queue = _writes;
        }
        try { await queue.Writer.WriteAsync(data.ToArray(), cancellationToken).ConfigureAwait(false); }
        catch (ChannelClosedException ex) { throw new IOException("The terminal input writer is no longer available.", ex.InnerException ?? ex); }
    }

    public void Resize(int columns, int rows)
    {
        ValidateSize(columns, rows);
        lock (_gate)
        {
            if (!_running || _pseudoConsole is null) throw new InvalidOperationException("The terminal process is not running.");
            _columns = columns;
            _rows = rows;
        }
        // ConPTY resize can block in the native API. Run it away from the UI and
        // coalesce bursts to the latest dimensions; CloseAsync owns another thread.
        EnsureResizeWorker();
    }

    public Task CloseAsync(CancellationToken cancellationToken = default)
    {
        Task close;
        lock (_gate)
        {
            if (_closeTask is null)
            {
                _running = false;
                _writes?.Writer.TryComplete();
                _closeTask = Task.Run(CloseCoreAsync);
            }
            close = _closeTask;
        }
        return cancellationToken.CanBeCanceled ? close.WaitAsync(cancellationToken) : close;
    }

    public ValueTask DisposeAsync() => new(CloseAsync());

    private void StartCore(TerminalProcessOptions options, string workingDirectory, string executablePath,
        IReadOnlyDictionary<string, string> environmentVariables)
    {
        SafeFileHandle? conIn = null, terminalIn = null, terminalOut = null, conOut = null;
        SafePseudoConsoleHandle? pseudo = null;
        SafeKernelHandle? process = null, thread = null, job = null;
        FileStream? input = null, output = null;
        AttributeList? attributes = null;
        bool assigned = false;
        try
        {
            CreatePipeChecked(out conIn, out terminalIn);
            CreatePipeChecked(out terminalOut, out conOut);
            int hr = CreatePseudoConsole(new Coord(ClampDimension(options.Columns), ClampDimension(options.Rows)), conIn, conOut, 0, out var hpcon);
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);
            pseudo = new SafePseudoConsoleHandle(hpcon);
            conIn.Dispose(); conIn = null;
            conOut.Dispose(); conOut = null;

            attributes = new AttributeList(pseudo);
            job = CreateKillOnCloseJob();
            var startup = new StartupInfoEx
            {
                StartupInfo = new StartupInfo { Cb = Marshal.SizeOf<StartupInfoEx>(), Flags = 0x100 },
                AttributeList = attributes.Pointer
            };
            string commandLine = string.Join(" ", new[] { executablePath }.Concat(options.Arguments).Select(QuoteArgument));
            IntPtr environment = CreateEnvironmentBlock(environmentVariables);
            try
            {
                if (!CreateProcess(executablePath, new StringBuilder(commandLine), IntPtr.Zero, IntPtr.Zero, false,
                    ExtendedStartupInfoPresent | CreateUnicodeEnvironment | CreateSuspended, environment,
                    workingDirectory, ref startup, out var info))
                    ThrowLastError("starting the terminal process");
                process = new SafeKernelHandle(info.Process);
                thread = new SafeKernelHandle(info.Thread);
                if (!AssignProcessToJobObject(job, process)) ThrowLastError("assigning the terminal process tree to its job");
                assigned = true;

                input = new FileStream(terminalIn, FileAccess.Write, 4096, isAsync: false); terminalIn = null;
                output = new FileStream(terminalOut, FileAccess.Read, 8192, isAsync: false); terminalOut = null;
                _columns = options.Columns; _rows = options.Rows;
                _pseudoConsole = pseudo; pseudo = null;
                _process = process; process = null;
                _job = job; job = null;
                _input = input; input = null;
                _output = output; output = null;
                _writes = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(64) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = false });
                _running = true;
                _outputTask = Task.Factory.StartNew(ReadOutput, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                _writeTask = Task.Factory.StartNew(WriteInput, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
                if (ResumeThread(thread) == uint.MaxValue) ThrowLastError("resuming the terminal process");
                thread.Dispose(); thread = null;
                var watchedProcess = _process!;
                bool watcherReference = false;
                watchedProcess.DangerousAddRef(ref watcherReference);
                try
                {
                    _exitTask = Task.Factory.StartNew(() => WatchExit(watchedProcess), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                    watcherReference = false; // WatchExit now owns the reference.
                }
                finally { if (watcherReference) watchedProcess.DangerousRelease(); }
            }
            finally { Marshal.FreeHGlobal(environment); }
        }
        catch
        {
            _running = false;
            if (process is not null && !assigned) _ = TerminateProcess(process, 1);
            job?.Dispose(); input?.Dispose(); output?.Dispose(); process?.Dispose(); pseudo?.Dispose();
            _job?.Dispose(); _job = null; _process?.Dispose(); _process = null;
            _input?.Dispose(); _input = null; _output?.Dispose(); _output = null;
            _pseudoConsole?.Dispose(); _pseudoConsole = null;
            _writes?.Writer.TryComplete(); _writes = null;
            throw;
        }
        finally
        {
            attributes?.Dispose(); thread?.Dispose(); conIn?.Dispose(); terminalIn?.Dispose(); terminalOut?.Dispose(); conOut?.Dispose();
        }
    }

    private async Task WriteInput()
    {
        var input = _input!;
        var reader = _writes!.Reader;
        try
        {
            await foreach (var bytes in reader.ReadAllAsync().ConfigureAwait(false))
            {
                input.Write(bytes, 0, bytes.Length);
                input.Flush();
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            bool notify;
            lock (_gate)
            {
                notify = _closeTask is null && _inputError is null;
                if (notify) _inputError = ex;
                if (notify) _writes?.Writer.TryComplete(ex);
            }
            if (notify)
                foreach (Action<Exception> handler in InputFailed?.GetInvocationList() ?? [])
                    try { handler(ex); } catch { }
        }
    }

    private Task? _resizeWorker;
    private bool _resizePending;
    private void EnsureResizeWorker()
    {
        lock (_gate)
        {
            _resizePending = true;
            if (_resizeWorker is { IsCompleted: false }) return;
            _resizeWorker = Task.Factory.StartNew(ResizeLoop, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }
    }

    private void ResizeLoop()
    {
        while (true)
        {
            SafePseudoConsoleHandle? console;
            int columns, rows;
            lock (_gate)
            {
                if (!_resizePending || !_running || _pseudoConsole is null) { _resizeWorker = null; return; }
                _resizePending = false;
                columns = _columns; rows = _rows; console = _pseudoConsole;
                console.DangerousAddRef(ref _resizeReferenceAdded);
            }
            try
            {
                if (_resizeReferenceAdded)
                {
                    int hr = ResizePseudoConsole(console!, new Coord(ClampDimension(columns), ClampDimension(rows)));
                    if (hr < 0) Marshal.ThrowExceptionForHR(hr);
                }
            }
            catch { /* Resize errors do not terminate process I/O; next resize can recover. */ }
            finally
            {
                if (_resizeReferenceAdded) { console!.DangerousRelease(); _resizeReferenceAdded = false; }
            }
        }
    }
    private bool _resizeReferenceAdded;

    private void ReadOutput()
    {
        byte[] buffer = new byte[8192];
        try
        {
            while (true)
            {
                int count = _output!.Read(buffer, 0, buffer.Length);
                if (count == 0) break;
                var bytes = buffer.AsSpan(0, count).ToArray();
                foreach (Action<byte[]> handler in OutputReceived?.GetInvocationList() ?? [])
                    try { handler(bytes); } catch { }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
    }

    private void WatchExit(SafeKernelHandle process)
    {
        int? code = null;
        try
        {
            if (WaitForSingleObject(process.DangerousGetHandle(), Infinite) == 0 && GetExitCodeProcess(process.DangerousGetHandle(), out uint nativeCode))
                code = unchecked((int)nativeCode);
        }
        catch { }
        finally { process.DangerousRelease(); }
        lock (_gate) _running = false;
        foreach (Action<TerminalProcessExit> handler in Exited?.GetInvocationList() ?? [])
            try { handler(new TerminalProcessExit(code)); } catch { }
    }

    private async Task CloseCoreAsync()
    {
        SafeKernelHandle? job, process;
        SafePseudoConsoleHandle? pseudo;
        FileStream? input, output;
        lock (_gate)
        {
            job = _job; _job = null; process = _process; _process = null;
            pseudo = _pseudoConsole; _pseudoConsole = null; input = _input; _input = null;
            output = _output; _output = null;
        }
        job?.Dispose(); // Kill only processes assigned to this session's private job.
        _writes?.Writer.TryComplete();
        input?.Dispose();
        pseudo?.Dispose(); // Reader stays alive while ConPTY drains output.
        output?.Dispose();
        process?.Dispose();
        if (_writeTask is not null) try { await _writeTask.ConfigureAwait(false); } catch { }
        if (_outputTask is not null) try { await _outputTask.ConfigureAwait(false); } catch { }
        if (_exitTask is not null) try { await _exitTask.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false); } catch { }
    }

    private static void ValidateOptions(TerminalProcessOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(options.FileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.WorkingDirectory);
        ValidateSize(options.Columns, options.Rows);
        ValidateArgument(options.FileName, nameof(options.FileName));
        foreach (var arg in options.Arguments) ValidateArgument(arg, nameof(options.Arguments));
        foreach (var (key, value) in options.Environment)
        {
            if (string.IsNullOrWhiteSpace(key) || key.Contains('=') || key.Contains('\0')) throw new ArgumentException("Invalid environment variable name.", nameof(options.Environment));
            if (value?.Contains('\0') == true) throw new ArgumentException("Environment values cannot contain NUL.", nameof(options.Environment));
        }
    }
    private static void ValidateArgument(string? value, string name)
    {
        if (value is null || value.Contains('\0')) throw new ArgumentException("Arguments must be non-null and cannot contain NUL.", name);
    }
    private static void ValidateSize(int columns, int rows)
    {
        if (columns is < 1 or > short.MaxValue) throw new ArgumentOutOfRangeException(nameof(columns));
        if (rows is < 1 or > short.MaxValue) throw new ArgumentOutOfRangeException(nameof(rows));
    }
    private static short ClampDimension(int value) => checked((short)Math.Clamp(value, 1, short.MaxValue));

    private static string QuoteArgument(string value)
    {
        if (value.Length != 0 && !value.Any(char.IsWhiteSpace) && !value.Contains('"')) return value;
        var result = new StringBuilder("\"");
        int slashes = 0;
        foreach (char c in value)
        {
            if (c == '\\') { slashes++; continue; }
            if (c == '"') { result.Append('\\', slashes * 2 + 1).Append('"'); slashes = 0; continue; }
            result.Append('\\', slashes).Append(c); slashes = 0;
        }
        result.Append('\\', slashes * 2).Append('"');
        return result.ToString();
    }

    private static SortedDictionary<string, string> CreateEffectiveEnvironment(IReadOnlyDictionary<string, string?> overrides)
    {
        var vars = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
            if (entry.Key is string key && entry.Value is string value) vars[key] = value;
        foreach (var (key, value) in overrides)
        {
            if (value is null) vars.Remove(key); else vars[key] = value;
        }
        vars.TryAdd("TERM", "xterm-256color");
        vars.TryAdd("COLORTERM", "truecolor");
        return vars;
    }

    private static IntPtr CreateEnvironmentBlock(IReadOnlyDictionary<string, string> vars)
    {
        var block = new StringBuilder();
        foreach (var (key, value) in vars) block.Append(key).Append('=').Append(value).Append('\0');
        block.Append('\0');
        return Marshal.StringToHGlobalUni(block.ToString());
    }

    private static string ResolveExecutablePath(string fileName, string workingDirectory,
        IReadOnlyDictionary<string, string> environment)
    {
        string extension = Path.GetExtension(fileName);
        if (extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".bat", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("Batch files are not launched implicitly; start cmd.exe with the batch path as an argument.");

        if (Path.IsPathRooted(fileName) || fileName.Contains('\\') || fileName.Contains('/'))
        {
            string explicitPath = Path.GetFullPath(fileName, workingDirectory);
            return FindExecutableAtPath(explicitPath, fileName);
        }

        if (TryFindExecutableInDirectory(workingDirectory, fileName, out string executable)) return executable;
        if (environment.TryGetValue("PATH", out string? path))
        {
            foreach (string entry in path.Split(';'))
            {
                string directory = entry.Trim().Trim('"');
                if (directory.Length == 0) directory = workingDirectory;
                else if (!Path.IsPathFullyQualified(directory)) directory = Path.GetFullPath(directory, workingDirectory);
                if (TryFindExecutableInDirectory(directory, fileName, out executable)) return executable;
            }
        }

        throw new FileNotFoundException(
            $"Could not find Windows terminal executable '{fileName}' in the working directory or effective child PATH.", fileName);
    }

    private static string FindExecutableAtPath(string path, string originalName)
    {
        if (File.Exists(path)) return path;
        if (Path.GetExtension(path).Length == 0 && File.Exists(path + ".exe")) return path + ".exe";
        throw new FileNotFoundException($"Could not find Windows terminal executable '{originalName}'.", originalName);
    }

    private static bool TryFindExecutableInDirectory(string directory, string fileName, out string executable)
    {
        string candidate = Path.GetFullPath(Path.Combine(directory, fileName));
        if (File.Exists(candidate)) { executable = candidate; return true; }
        if (Path.GetExtension(fileName).Length == 0 && File.Exists(candidate + ".exe"))
        {
            executable = candidate + ".exe";
            return true;
        }
        executable = string.Empty;
        return false;
    }

    private static SafeKernelHandle CreateKillOnCloseJob()
    {
        var job = CreateJobObject(IntPtr.Zero, null);
        if (job.IsInvalid) ThrowLastError("creating the process job");
        var limits = new JobObjectExtendedLimitInformation { BasicLimitInformation = new JobObjectBasicLimitInformation { LimitFlags = JobObjectLimitKillOnJobClose } };
        int size = Marshal.SizeOf<JobObjectExtendedLimitInformation>();
        IntPtr mem = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(limits, mem, false);
            if (!SetInformationJobObject(job, 9, mem, (uint)size)) ThrowLastError("setting process job limits");
            return job;
        }
        catch { job.Dispose(); throw; }
        finally { Marshal.FreeHGlobal(mem); }
    }
    private static void CreatePipeChecked(out SafeFileHandle read, out SafeFileHandle write)
    {
        if (!CreatePipe(out read, out write, IntPtr.Zero, 0)) ThrowLastError("creating a ConPTY pipe");
    }
    private static void ThrowLastError(string action) => throw new Win32Exception(Marshal.GetLastWin32Error(), $"Failed while {action}.");

    [StructLayout(LayoutKind.Sequential)] private readonly struct Coord(short x, short y) { public readonly short X = x; public readonly short Y = y; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct StartupInfo
    {
        public int Cb; public IntPtr Reserved, Desktop, Title; public uint X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags; public ushort ShowWindow, Reserved2; public IntPtr Reserved2Pointer, StandardInput, StandardOutput, StandardError;
    }
    [StructLayout(LayoutKind.Sequential)] private struct StartupInfoEx { public StartupInfo StartupInfo; public IntPtr AttributeList; }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInformation { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }
    [StructLayout(LayoutKind.Sequential)] private struct JobObjectBasicLimitInformation { public long PerProcessUserTimeLimit, PerJobUserTimeLimit; public uint LimitFlags; public nuint MinimumWorkingSetSize, MaximumWorkingSetSize; public uint ActiveProcessLimit; public nuint Affinity; public uint PriorityClass, SchedulingClass; }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount; }
    [StructLayout(LayoutKind.Sequential)] private struct JobObjectExtendedLimitInformation { public JobObjectBasicLimitInformation BasicLimitInformation; public IoCounters IoInfo; public nuint ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed; }

    private sealed class AttributeList : IDisposable
    {
        private IntPtr _pointer;
        public IntPtr Pointer => _pointer;
        public AttributeList(SafePseudoConsoleHandle console)
        {
            nuint bytes = 0; _ = InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref bytes);
            if (bytes == 0) ThrowLastError("sizing the process attribute list");
            _pointer = Marshal.AllocHGlobal(checked((nint)bytes));
            if (!InitializeProcThreadAttributeList(_pointer, 1, 0, ref bytes)) { int error = Marshal.GetLastWin32Error(); Dispose(); throw new Win32Exception(error); }
            if (!UpdateProcThreadAttribute(_pointer, 0, (nuint)ProcThreadAttributePseudoConsole, console.DangerousGetHandle(), (nuint)IntPtr.Size, IntPtr.Zero, IntPtr.Zero)) { int error = Marshal.GetLastWin32Error(); DeleteProcThreadAttributeList(_pointer); Marshal.FreeHGlobal(_pointer); _pointer = IntPtr.Zero; throw new Win32Exception(error); }
        }
        public void Dispose() { var pointer = Interlocked.Exchange(ref _pointer, IntPtr.Zero); if (pointer != IntPtr.Zero) { DeleteProcThreadAttributeList(pointer); Marshal.FreeHGlobal(pointer); } }
    }
    private sealed class SafeKernelHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeKernelHandle() : base(true) { }
        public SafeKernelHandle(IntPtr handle) : base(true) => SetHandle(handle);
        protected override bool ReleaseHandle() => CloseHandle(handle);
    }
    private sealed class SafePseudoConsoleHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafePseudoConsoleHandle() : base(true) { }
        public SafePseudoConsoleHandle(IntPtr handle) : base(true) => SetHandle(handle);
        protected override bool ReleaseHandle() { ClosePseudoConsole(handle); return true; }
    }

    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, IntPtr attributes, uint size);
    [DllImport("kernel32.dll")] private static extern int CreatePseudoConsole(Coord size, SafeFileHandle input, SafeFileHandle output, uint flags, out IntPtr console);
    [DllImport("kernel32.dll")] private static extern int ResizePseudoConsole(SafePseudoConsoleHandle console, Coord size);
    [DllImport("kernel32.dll")] private static extern void ClosePseudoConsole(IntPtr console);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, uint flags, ref nuint size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, nuint attribute, IntPtr value, nuint size, IntPtr previous, IntPtr returnSize);
    [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreateProcess(string app, StringBuilder command, IntPtr processAttributes, IntPtr threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint flags, IntPtr environment, string cwd, ref StartupInfoEx startup, out ProcessInformation info);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeKernelHandle CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetInformationJobObject(SafeKernelHandle job, int kind, IntPtr info, uint length);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AssignProcessToJobObject(SafeKernelHandle job, SafeKernelHandle process);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(SafeKernelHandle thread);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(IntPtr handle, uint timeout);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetExitCodeProcess(IntPtr process, out uint code);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool TerminateProcess(SafeKernelHandle process, uint code);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);
}
