using System.Collections.Concurrent;
using System.Collections;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace TerminalNotes.Services;

/// <summary>
/// Owns one Windows pseudoconsole and its PowerShell process tree.
/// </summary>
/// <remarks>
/// Assumptions: Windows 10 1809 or newer is available; each instance is started at
/// most once; callers pass terminal keystrokes (including control sequences) to
/// <see cref="Write"/> and marshal event callbacks to the UI thread themselves.
/// Setting <c>launchCodex</c> starts a fixed encoded bootstrap in the selected
/// working directory and keeps PowerShell available after Codex exits.
/// A non-empty <c>startupCommand</c> takes precedence and is transferred as UTF-8
/// Base64 through the child-only environment, never interpolated into a script.
/// ConPTY's byte stream is UTF-8, so decoding is incremental to preserve characters
/// split across native reads (including Korean text). Output is delivered verbatim;
/// ANSI/VT sequences are intentionally retained for a terminal renderer such as xterm.js.
/// </remarks>
public sealed class ConPtySession : IDisposable
{
    // ConPTY transports UTF-8, but Windows PowerShell's .NET Console writer can
    // otherwise replace characters using the inherited OEM code page first.
    private const string Utf8BootstrapScript = """
        [Console]::InputEncoding = [Text.UTF8Encoding]::new($false)
        [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
        $OutputEncoding = [Console]::OutputEncoding
        """;
    private const string CodexBootstrapScript = """
        $codexCommand = Get-Command codex -CommandType Application,ExternalScript -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($null -eq $codexCommand) {
            Write-Host 'Codex CLI를 찾을 수 없습니다. Codex CLI was not found on PATH.' -ForegroundColor Red
        }
        else {
            try {
                & $codexCommand.Source
            }
            catch {
                Write-Host ('Codex 실행에 실패했습니다. Failed to launch Codex: ' + $_.Exception.Message) -ForegroundColor Red
            }
        }
        """;
    private const string CustomCommandBootstrapScript = """
        $encodedCommand = [Environment]::GetEnvironmentVariable('TERMINALNOTES_STARTUP_COMMAND_B64', 'Process')
        Remove-Item Env:TERMINALNOTES_STARTUP_COMMAND_B64 -ErrorAction SilentlyContinue
        try {
            if ([string]::IsNullOrEmpty($encodedCommand)) {
                throw 'The encoded startup command is missing.'
            }
            $commandBytes = [Convert]::FromBase64String($encodedCommand)
            $commandText = [Text.Encoding]::UTF8.GetString($commandBytes)
            $commandScript = [ScriptBlock]::Create($commandText)
            $previousErrorAction = $ErrorActionPreference
            try {
                $ErrorActionPreference = 'Stop'
                & $commandScript
            }
            finally {
                $ErrorActionPreference = $previousErrorAction
            }
        }
        catch {
            Write-Host ('시작 명령 실행에 실패했습니다. Startup command failed: ' + $_.Exception.Message) -ForegroundColor Red
        }
        """;

    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint CreateSuspended = 0x00000004;
    private const uint StartfUseStdHandles = 0x00000100;
    private const nuint ProcThreadAttributePseudoConsole = 0x00020016;
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;
    private const uint Infinite = 0xFFFFFFFF;
    private const uint WaitObject0 = 0;
    private const uint InputFailureExitSettleMilliseconds = 100;

    private readonly object _gate = new();
    private readonly ConcurrentQueue<string> _pendingOutput = new();
    private static readonly Encoding TerminalEncoding = new UTF8Encoding(false);
    private readonly ConPtyLifecycleWorker _lifecycleWorker = new();

    private FileStream? _input;
    private TerminalInputBuffer? _inputQueue;
    private IOException? _inputFailure;
    private FileStream? _output;
    private SafePseudoConsoleHandle? _pseudoConsole;
    private SafeKernelObjectHandle? _process;
    private readonly TaskCompletionSource _processExited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _closeTask;
    private uint? _processId;

    internal async Task StopAndWaitAsync()
    {
        await CloseAsync();
        await _processExited.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }
    private SafeKernelObjectHandle? _job;
    private bool _startAttempted;
    private bool _running;
    private int _disposed;
    private int _outputDispatchScheduled;

    public event Action<string>? OutputReceived;

    public event Action<int>? Exited;

    public event Action<Exception>? InputFailed;

    public event Action<ConPtyLifecycleEvent>? LifecycleChanged
    {
        add => _lifecycleWorker.LifecycleChanged += value;
        remove => _lifecycleWorker.LifecycleChanged -= value;
    }

    /// <summary>Process ID while this session still owns its ConPTY process handle.</summary>
    public uint? ProcessId
    {
        get { lock (_gate) return _processId; }
    }

    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _running && Volatile.Read(ref _disposed) == 0;
            }
        }
    }

    public void Start(
        string workingDirectory,
        short columns = 120,
        short rows = 30,
        bool launchCodex = false,
        string? startupCommand = null,
        string? openCodeConfigContent = null,
        bool exitOnStartupCommandCompletion = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ValidateSize(columns, rows);

        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
        {
            throw new PlatformNotSupportedException("ConPTY requires Windows 10 version 1809 or newer.");
        }

        string fullWorkingDirectory = Path.GetFullPath(workingDirectory);
        if (!Directory.Exists(fullWorkingDirectory))
        {
            throw new DirectoryNotFoundException($"The terminal working directory does not exist: {fullWorkingDirectory}");
        }

        lock (_gate)
        {
            ThrowIfDisposed();
            if (_startAttempted)
            {
                throw new InvalidOperationException("A ConPtySession instance can only be started once.");
            }

            _startAttempted = true;
            StartCore(fullWorkingDirectory, columns, rows, launchCodex, startupCommand, openCodeConfigContent, exitOnStartupCommandCompletion);
        }
    }

    public void Write(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0)
        {
            return;
        }

        byte[] bytes = TerminalEncoding.GetBytes(text);
        lock (_gate)
        {
            ThrowIfDisposed();
            if (!_running || _inputQueue is null)
            {
                throw new InvalidOperationException("The pseudoconsole is not running.");
            }

            if (_inputFailure is { } failure)
            {
                throw new IOException("The terminal input writer has failed.", failure);
            }

            if (!_inputQueue.TryWrite(bytes))
            {
                throw new IOException("The terminal input queue is no longer available.", _inputQueue.Failure);
            }
        }
    }

    public void Resize(short columns, short rows)
    {
        ValidateSize(columns, rows);
        SafePseudoConsoleHandle pseudoConsole;
        bool addedReference = false;
        lock (_gate)
        {
            ThrowIfDisposed();
            if (!_running || _pseudoConsole is null)
            {
                throw new InvalidOperationException("The pseudoconsole is not running.");
            }
            pseudoConsole = _pseudoConsole;
            pseudoConsole.DangerousAddRef(ref addedReference);
        }

        try { ResizePseudoConsoleCore(pseudoConsole, columns, rows); }
        finally { if (addedReference) pseudoConsole.DangerousRelease(); }
    }

    /// <summary>Queues the latest size without waiting for the native resize call.</summary>
    public void RequestResize(short columns, short rows)
    {
        ValidateSize(columns, rows);
        lock (_gate)
        {
            ThrowIfDisposed();
            if (!_running || _pseudoConsole is null)
                throw new InvalidOperationException("The pseudoconsole is not running.");
        }
        _lifecycleWorker.RequestResize(columns, rows, ResizeNativeForLifecycleWorker);
    }

    private void ResizeNativeForLifecycleWorker(short columns, short rows)
    {
        SafePseudoConsoleHandle? pseudoConsole;
        bool addedReference = false;
        lock (_gate)
        {
            if (Volatile.Read(ref _disposed) != 0 || !_running || _pseudoConsole is null) return;
            pseudoConsole = _pseudoConsole;
            pseudoConsole.DangerousAddRef(ref addedReference);
        }
        if (!addedReference || pseudoConsole is null) return;
        try { ResizePseudoConsoleCore(pseudoConsole, columns, rows); }
        finally { pseudoConsole.DangerousRelease(); }
    }

    private static void ResizePseudoConsoleCore(SafePseudoConsoleHandle pseudoConsole, short columns, short rows)
    {
        int result = ResizePseudoConsole(pseudoConsole, new Coord(columns, rows));
        if (result < 0) Marshal.ThrowExceptionForHR(result);
    }

    public Task CloseAsync()
    {
        FileStream? input;
        TerminalInputBuffer? inputQueue;
        FileStream? output;
        SafePseudoConsoleHandle? pseudoConsole;
        SafeKernelObjectHandle? process;
        SafeKernelObjectHandle? job;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        lock (_gate)
        {
            if (_closeTask is not null) return _closeTask;
            _closeTask = completion.Task;
            Interlocked.Exchange(ref _disposed, 1);

            _running = false;
            _processId = null;
            input = Take(ref _input);
            inputQueue = Take(ref _inputQueue);
            output = Take(ref _output);
            pseudoConsole = Take(ref _pseudoConsole);
            process = Take(ref _process);
            job = Take(ref _job);
            inputQueue?.Complete();
        }

        while (_pendingOutput.TryDequeue(out _))
        {
        }
        Task cleanup = _lifecycleWorker.CloseAsync(() => CloseHandles(job, input, pseudoConsole, output, process));
        _ = cleanup.ContinueWith(task =>
        {
            if (task.IsFaulted) completion.TrySetException(task.Exception!.InnerExceptions);
            else if (task.IsCanceled) completion.TrySetCanceled();
            else completion.TrySetResult();
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return completion.Task;
    }

    private void CloseHandles(
        SafeKernelObjectHandle? job,
        FileStream? input,
        SafePseudoConsoleHandle? pseudoConsole,
        FileStream? output,
        SafeKernelObjectHandle? process)
    {
        // Closing this job kills only the process tree created by this session. Keep
        // the output reader alive while closing HPCON: ClosePseudoConsole can wait for
        // pending terminal output, and the dedicated reader prevents a pipe deadlock.
        List<Exception>? failures = null;
        DisposeAndCapture(job, ref failures);
        DisposeAndCapture(input, ref failures);
        DisposeAndCapture(pseudoConsole, ref failures);
        DisposeAndCapture(output, ref failures);
        DisposeAndCapture(process, ref failures);
        if (process is null) _processExited.TrySetResult();

        GC.SuppressFinalize(this);
        if (failures is { Count: 1 }) throw failures[0];
        if (failures is { Count: > 1 }) throw new AggregateException(failures);
    }

    private static void DisposeAndCapture(IDisposable? resource, ref List<Exception>? failures)
    {
        if (resource is null) return;
        try { resource.Dispose(); }
        catch (Exception exception) { (failures ??= []).Add(exception); }
    }

    public void Dispose()
    {
        CloseAsync().GetAwaiter().GetResult();
    }

    private void StartCore(
        string workingDirectory,
        short columns,
        short rows,
        bool launchCodex,
        string? startupCommand,
        string? openCodeConfigContent,
        bool exitOnStartupCommandCompletion)
    {
        SafeFileHandle? pseudoConsoleInput = null;
        SafeFileHandle? terminalInput = null;
        SafeFileHandle? terminalOutput = null;
        SafeFileHandle? pseudoConsoleOutput = null;
        SafePseudoConsoleHandle? pseudoConsole = null;
        SafeKernelObjectHandle? process = null;
        SafeKernelObjectHandle? thread = null;
        SafeKernelObjectHandle? job = null;
        FileStream? inputStream = null;
        TerminalInputBuffer? inputQueue = null;
        FileStream? outputStream = null;
        ProcThreadAttributeList? attributeList = null;
        bool assignedToJob = false;
        bool ownershipTransferred = false;

        try
        {
            CreatePipeChecked(out pseudoConsoleInput, out terminalInput, "creating the ConPTY input pipe");
            CreatePipeChecked(out terminalOutput, out pseudoConsoleOutput, "creating the ConPTY output pipe");

            int createResult = CreatePseudoConsole(
                new Coord(columns, rows),
                pseudoConsoleInput,
                pseudoConsoleOutput,
                0,
                out IntPtr pseudoConsoleValue);
            if (createResult < 0)
            {
                Marshal.ThrowExceptionForHR(createResult);
            }

            pseudoConsole = new SafePseudoConsoleHandle(pseudoConsoleValue, ownsHandle: true);

            // HPCON owns its references to these two ends after successful creation.
            pseudoConsoleInput.Dispose();
            pseudoConsoleInput = null;
            pseudoConsoleOutput.Dispose();
            pseudoConsoleOutput = null;

            attributeList = new ProcThreadAttributeList(pseudoConsole);
            job = CreateKillOnCloseJob();

            string powerShellPath = GetPowerShellPath();
            var startupInfo = new StartupInfoEx
            {
                StartupInfo = new StartupInfo
                {
                    Cb = Marshal.SizeOf<StartupInfoEx>(),
                    // Null explicit standard handles prevent CreateProcess from
                    // duplicating redirected parent handles into the console client.
                    Flags = StartfUseStdHandles
                },
                AttributeList = attributeList.Pointer
            };

            string commandLine = BuildPowerShellCommandLine(
                powerShellPath,
                launchCodex,
                startupCommand,
                exitOnStartupCommandCompletion,
                out string? encodedStartupCommand);
            ProcessInformation processInformation;
            IntPtr environment = CreateChildEnvironment(encodedStartupCommand, openCodeConfigContent);
            try
            {
                if (!CreateProcess(
                    powerShellPath,
                    new StringBuilder(commandLine),
                    IntPtr.Zero,
                    IntPtr.Zero,
                    false,
                    ExtendedStartupInfoPresent | CreateUnicodeEnvironment | CreateSuspended,
                    environment,
                    workingDirectory,
                    ref startupInfo,
                    out processInformation))
                {
                    ThrowLastWin32Error("creating PowerShell in the pseudoconsole");
                }
            }
            finally
            {
                Marshal.FreeHGlobal(environment);
            }

            process = new SafeKernelObjectHandle(processInformation.Process, ownsHandle: true);
            thread = new SafeKernelObjectHandle(processInformation.Thread, ownsHandle: true);

            if (!AssignProcessToJobObject(job, process))
            {
                ThrowLastWin32Error("assigning PowerShell to its private job object");
            }

            assignedToJob = true;

            // Anonymous pipes are synchronous handles. A dedicated blocking reader is
            // deliberate: it continuously drains VT output without consuming a UI or
            // thread-pool thread, and it remains active during ClosePseudoConsole.
            inputStream = new FileStream(terminalInput, FileAccess.Write, 4096, isAsync: false);
            terminalInput = null;
            outputStream = new FileStream(terminalOutput, FileAccess.Read, 8192, isAsync: false);
            terminalOutput = null;
            inputQueue = new TerminalInputBuffer();
            StartInputWriter(inputStream, inputQueue);
            StartOutputReader(outputStream);

            uint resumeResult = ResumeThread(thread);
            if (resumeResult == uint.MaxValue)
            {
                ThrowLastWin32Error("resuming PowerShell");
            }

            thread.Dispose();
            thread = null;

            _input = inputStream;
            _inputQueue = inputQueue;
            _output = outputStream;
            _pseudoConsole = pseudoConsole;
            _process = process;
            _processId = processInformation.ProcessId;
            _job = job;
            _running = true;

            inputStream = null;
            inputQueue = null;
            outputStream = null;
            pseudoConsole = null;
            process = null;
            job = null;
            ownershipTransferred = true;

            StartExitWatcher(_process);
        }
        catch
        {
            _running = false;

            if (ownershipTransferred)
            {
                // StartExitWatcher is the only operation that can fail after transfer.
                Take(ref _job)?.Dispose();
                Take(ref _inputQueue)?.Complete();
                Take(ref _input)?.Dispose();
                Take(ref _pseudoConsole)?.Dispose();
                Take(ref _output)?.Dispose();
                Take(ref _process)?.Dispose();
            }
            else
            {
                if (process is not null && !assignedToJob)
                {
                    _ = TerminateProcess(process, 1);
                }

                job?.Dispose();
                inputQueue?.Complete();
                inputStream?.Dispose();
                pseudoConsole?.Dispose();
                outputStream?.Dispose();
                process?.Dispose();
            }

            throw;
        }
        finally
        {
            attributeList?.Dispose();
            thread?.Dispose();
            pseudoConsoleInput?.Dispose();
            terminalInput?.Dispose();
            terminalOutput?.Dispose();
            pseudoConsoleOutput?.Dispose();
        }
    }

    private void StartOutputReader(FileStream output)
    {
        _ = Task.Factory.StartNew(
            () => ReadOutput(output),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    private void StartInputWriter(FileStream input, TerminalInputBuffer inputQueue)
    {
        _ = Task.Factory.StartNew(
            () => WriteInput(input, inputQueue),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    private void WriteInput(FileStream input, TerminalInputBuffer inputQueue)
    {
        try
        {
            while (inputQueue.Reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
            {
                while (inputQueue.Reader.TryRead(out byte[]? bytes))
                {
                    input.Write(bytes, 0, bytes.Length);
                    input.Flush();
                }
            }
        }
        catch (IOException exception)
        {
            if (!inputQueue.TryFail(exception)) return;

            if (DidOwnedProcessExitDuringInputFailureSettle(inputQueue)) return;

            bool reportFailure;
            lock (_gate)
            {
                reportFailure = Volatile.Read(ref _disposed) == 0 && _running && ReferenceEquals(_inputQueue, inputQueue);
                if (reportFailure) _inputFailure = exception;
            }

            if (reportFailure) RaiseInputFailed(exception);
        }
        catch (ObjectDisposedException) when (Volatile.Read(ref _disposed) != 0)
        {
        }
    }

    private bool DidOwnedProcessExitDuringInputFailureSettle(TerminalInputBuffer inputQueue)
    {
        SafeKernelObjectHandle? process;
        bool addedReference = false;
        lock (_gate)
        {
            if (Volatile.Read(ref _disposed) != 0 || !_running || !ReferenceEquals(_inputQueue, inputQueue))
            {
                return true;
            }

            process = _process;
            if (process is null || process.IsClosed || process.IsInvalid)
            {
                return true;
            }

            process.DangerousAddRef(ref addedReference);
        }

        if (!addedReference) return true;

        try
        {
            // This runs on the dedicated writer thread, never on the WPF caller.
            _ = WaitForSingleObject(process.DangerousGetHandle(), InputFailureExitSettleMilliseconds);

            lock (_gate)
            {
                if (Volatile.Read(ref _disposed) != 0 || !_running ||
                    !ReferenceEquals(_inputQueue, inputQueue) || !ReferenceEquals(_process, process))
                {
                    return true;
                }

                // Recheck under the lifecycle lock to close the watcher-lag race.
                return WaitForSingleObject(process.DangerousGetHandle(), 0) == WaitObject0;
            }
        }
        finally
        {
            process.DangerousRelease();
        }
    }

    private void RaiseInputFailed(Exception exception)
    {
        Delegate[] handlers = InputFailed?.GetInvocationList() ?? [];
        foreach (var handler in handlers)
        {
            try { ((Action<Exception>)handler)(exception); }
            catch { /* Input-failure observers are outside the session lifetime boundary. */ }
        }
    }

    private void ReadOutput(FileStream output)
    {
        var decoder = new UTF8Encoding(
            encoderShouldEmitUTF8Identifier: false,
            throwOnInvalidBytes: false).GetDecoder();
        byte[] bytes = new byte[8192];
        char[] characters = new char[Encoding.UTF8.GetMaxCharCount(bytes.Length)];

        try
        {
            while (true)
            {
                int count = output.Read(bytes, 0, bytes.Length);
                if (count == 0)
                {
                    break;
                }

                int byteOffset = 0;
                while (byteOffset < count)
                {
                    decoder.Convert(
                        bytes.AsSpan(byteOffset, count - byteOffset),
                        characters,
                        flush: false,
                        out int bytesUsed,
                        out int charactersUsed,
                        out _);
                    byteOffset += bytesUsed;
                    if (charactersUsed > 0)
                    {
                        QueueOutput(new string(characters, 0, charactersUsed));
                    }
                }
            }

            decoder.Convert(
                ReadOnlySpan<byte>.Empty,
                characters,
                flush: true,
                out _,
                out int finalCharacters,
                out _);
            if (finalCharacters > 0)
            {
                QueueOutput(new string(characters, 0, finalCharacters));
            }
        }
        catch (IOException) when (Volatile.Read(ref _disposed) != 0)
        {
        }
        catch (ObjectDisposedException) when (Volatile.Read(ref _disposed) != 0)
        {
        }
    }

    private void QueueOutput(string text)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        _pendingOutput.Enqueue(text);
        if (Interlocked.CompareExchange(ref _outputDispatchScheduled, 1, 0) == 0)
        {
            ThreadPool.UnsafeQueueUserWorkItem(
                static session => session.DispatchOutput(),
                this,
                preferLocal: false);
        }
    }

    private void DispatchOutput()
    {
        while (true)
        {
            while (_pendingOutput.TryDequeue(out string? text))
            {
                if (Volatile.Read(ref _disposed) != 0)
                {
                    continue;
                }

                try
                {
                    OutputReceived?.Invoke(text);
                }
                catch
                {
                    // A subscriber must not stop terminal drainage or other subscribers.
                }
            }

            Volatile.Write(ref _outputDispatchScheduled, 0);
            if (_pendingOutput.IsEmpty ||
                Interlocked.CompareExchange(ref _outputDispatchScheduled, 1, 0) != 0)
            {
                return;
            }
        }
    }

    private void StartExitWatcher(SafeKernelObjectHandle process)
    {
        bool addedReference = false;
        process.DangerousAddRef(ref addedReference);

        try
        {
            _ = Task.Factory.StartNew(
                () =>
                {
                    int exitCode = -1;
                    try
                    {
                        if (WaitForSingleObject(process.DangerousGetHandle(), Infinite) == WaitObject0 &&
                            GetExitCodeProcess(process.DangerousGetHandle(), out uint nativeExitCode))
                        {
                            exitCode = unchecked((int)nativeExitCode);
                        }
                    }
                    finally
                    {
                        process.DangerousRelease();
                    }

                    HandleProcessExit(exitCode);
                },
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
        }
        catch
        {
            if (addedReference)
            {
                process.DangerousRelease();
            }

            throw;
        }
    }

    private void HandleProcessExit(int exitCode)
    {
        _processExited.TrySetResult();
        lock (_gate)
        {
            _running = false;
            _inputQueue?.Complete();
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }
        }

        ThreadPool.UnsafeQueueUserWorkItem(
            static state =>
            {
                if (Volatile.Read(ref state.Session._disposed) != 0)
                {
                    return;
                }

                try
                {
                    state.Session.Exited?.Invoke(state.ExitCode);
                }
                catch
                {
                    // Event handlers are outside the lifetime-management boundary.
                }
            },
            (Session: this, ExitCode: exitCode),
            preferLocal: false);
    }

    private static SafeKernelObjectHandle CreateKillOnCloseJob()
    {
        SafeKernelObjectHandle job = CreateJobObject(IntPtr.Zero, null);
        if (job.IsInvalid)
        {
            ThrowLastWin32Error("creating a private job object");
        }

        var limits = new JobObjectExtendedLimitInformation
        {
            BasicLimitInformation = new JobObjectBasicLimitInformation
            {
                LimitFlags = JobObjectLimitKillOnJobClose
            }
        };

        int size = Marshal.SizeOf<JobObjectExtendedLimitInformation>();
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(limits, buffer, fDeleteOld: false);
            if (!SetInformationJobObject(job, JobObjectInfoType.ExtendedLimitInformation, buffer, (uint)size))
            {
                ThrowLastWin32Error("configuring the private job object");
            }

            return job;
        }
        catch
        {
            job.Dispose();
            throw;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static string GetPowerShellPath()
    {
        string path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32",
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        return File.Exists(path) ? path : "powershell.exe";
    }

    private static string BuildPowerShellCommandLine(
        string powerShellPath,
        bool launchCodex,
        string? startupCommand,
        bool exitOnStartupCommandCompletion,
        out string? encodedStartupCommand)
    {
        encodedStartupCommand = null;
        if (!string.IsNullOrWhiteSpace(startupCommand))
        {
            encodedStartupCommand = Convert.ToBase64String(TerminalEncoding.GetBytes(startupCommand));
            string customEncodedBootstrap = Convert.ToBase64String(
                Encoding.Unicode.GetBytes(Utf8BootstrapScript + "\n" + CustomCommandBootstrapScript));
            // A native OpenCode pane's lifetime must follow OpenCode, not an idle
            // PowerShell prompt left behind after launch failure or process exit.
            string lifecycleOptions = exitOnStartupCommandCompletion ? "-NoProfile" : "-NoExit";
            return $"\"{powerShellPath}\" -NoLogo {lifecycleOptions} -EncodedCommand {customEncodedBootstrap}";
        }

        if (!launchCodex)
        {
            string shellBootstrap = Convert.ToBase64String(Encoding.Unicode.GetBytes(Utf8BootstrapScript));
            return $"\"{powerShellPath}\" -NoLogo -NoExit -EncodedCommand {shellBootstrap}";
        }

        // EncodedCommand keeps the bootstrap fixed and independent of the selected
        // working directory. CreateProcess supplies that directory separately.
        string encodedBootstrap = Convert.ToBase64String(
            Encoding.Unicode.GetBytes(Utf8BootstrapScript + "\n" + CodexBootstrapScript));
        return $"\"{powerShellPath}\" -NoLogo -NoExit -EncodedCommand {encodedBootstrap}";
    }

    private static IntPtr CreateChildEnvironment(string? encodedStartupCommand, string? openCodeConfigContent)
    {
        var variables = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string name && entry.Value is string value)
            {
                variables[name] = value;
            }
        }

        variables["TERM"] = "xterm-256color";
        variables["COLORTERM"] = "truecolor";
        if (encodedStartupCommand is not null)
        {
            variables["TERMINALNOTES_STARTUP_COMMAND_B64"] = encodedStartupCommand;
        }
        if (openCodeConfigContent is not null)
        {
            variables["OPENCODE_CONFIG_CONTENT"] = openCodeConfigContent;
        }

        var block = new StringBuilder();
        foreach ((string name, string value) in variables)
        {
            block.Append(name).Append('=').Append(value).Append('\0');
        }

        block.Append('\0');
        return Marshal.StringToHGlobalUni(block.ToString());
    }

    private static void CreatePipeChecked(
        out SafeFileHandle readPipe,
        out SafeFileHandle writePipe,
        string operation)
    {
        if (!CreatePipe(out readPipe, out writePipe, IntPtr.Zero, 0))
        {
            ThrowLastWin32Error(operation);
        }
    }

    private static void ValidateSize(short columns, short rows)
    {
        if (columns <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(columns), "Terminal columns must be positive.");
        }

        if (rows <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(rows), "Terminal rows must be positive.");
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }

    private static T? Take<T>(ref T? value) where T : class
    {
        T? result = value;
        value = null;
        return result;
    }

    private static void ThrowLastWin32Error(string operation)
    {
        throw new Win32Exception(Marshal.GetLastWin32Error(), $"Failed while {operation}.");
    }

    private sealed class ProcThreadAttributeList : IDisposable
    {
        private IntPtr _pointer;

        public ProcThreadAttributeList(SafePseudoConsoleHandle pseudoConsole)
        {
            nuint size = 0;
            _ = InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
            if (size == 0)
            {
                ThrowLastWin32Error("sizing the process attribute list");
            }

            _pointer = Marshal.AllocHGlobal(checked((nint)size));
            if (!InitializeProcThreadAttributeList(_pointer, 1, 0, ref size))
            {
                int error = Marshal.GetLastWin32Error();
                Marshal.FreeHGlobal(_pointer);
                _pointer = IntPtr.Zero;
                throw new Win32Exception(error, "Failed while initializing the process attribute list.");
            }

            try
            {
                if (!UpdateProcThreadAttribute(
                        _pointer,
                        0,
                        ProcThreadAttributePseudoConsole,
                        pseudoConsole.DangerousGetHandle(),
                        (nuint)IntPtr.Size,
                        IntPtr.Zero,
                        IntPtr.Zero))
                {
                    ThrowLastWin32Error("adding the pseudoconsole process attribute");
                }
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public IntPtr Pointer => _pointer;

        public void Dispose()
        {
            IntPtr pointer = Interlocked.Exchange(ref _pointer, IntPtr.Zero);
            if (pointer == IntPtr.Zero)
            {
                return;
            }

            DeleteProcThreadAttributeList(pointer);
            Marshal.FreeHGlobal(pointer);
        }
    }

    private sealed class SafeKernelObjectHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeKernelObjectHandle()
            : base(ownsHandle: true)
        {
        }

        public SafeKernelObjectHandle(IntPtr handle, bool ownsHandle)
            : base(ownsHandle)
        {
            SetHandle(handle);
        }

        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    private sealed class SafePseudoConsoleHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafePseudoConsoleHandle()
            : base(ownsHandle: true)
        {
        }

        public SafePseudoConsoleHandle(IntPtr handle, bool ownsHandle)
            : base(ownsHandle)
        {
            SetHandle(handle);
        }

        protected override bool ReleaseHandle()
        {
            ClosePseudoConsole(handle);
            return true;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct Coord
    {
        public Coord(short x, short y)
        {
            X = x;
            Y = y;
        }

        public readonly short X;
        public readonly short Y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Cb;
        public IntPtr Reserved;
        public IntPtr Desktop;
        public IntPtr Title;
        public uint X;
        public uint Y;
        public uint XSize;
        public uint YSize;
        public uint XCountChars;
        public uint YCountChars;
        public uint FillAttribute;
        public uint Flags;
        public ushort ShowWindow;
        public ushort Reserved2;
        public IntPtr Reserved2Pointer;
        public IntPtr StandardInput;
        public IntPtr StandardOutput;
        public IntPtr StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public IntPtr AttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct ProcessInformation
    {
        public readonly IntPtr Process;
        public readonly IntPtr Thread;
        public readonly uint ProcessId;
        public readonly uint ThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    private enum JobObjectInfoType
    {
        ExtendedLimitInformation = 9
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreatePipe(
        out SafeFileHandle readPipe,
        out SafeFileHandle writePipe,
        IntPtr pipeAttributes,
        uint size);

    [DllImport("kernel32.dll")]
    private static extern int CreatePseudoConsole(
        Coord size,
        SafeFileHandle input,
        SafeFileHandle output,
        uint flags,
        out IntPtr pseudoConsole);

    [DllImport("kernel32.dll")]
    private static extern int ResizePseudoConsole(SafePseudoConsoleHandle pseudoConsole, Coord size);

    [DllImport("kernel32.dll")]
    private static extern void ClosePseudoConsole(IntPtr pseudoConsole);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitializeProcThreadAttributeList(
        IntPtr attributeList,
        int attributeCount,
        uint flags,
        ref nuint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateProcThreadAttribute(
        IntPtr attributeList,
        uint flags,
        nuint attribute,
        IntPtr value,
        nuint size,
        IntPtr previousValue,
        IntPtr returnSize);

    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr attributeList);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcess(
        string applicationName,
        StringBuilder commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags,
        IntPtr environment,
        string currentDirectory,
        ref StartupInfoEx startupInfo,
        out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeKernelObjectHandle CreateJobObject(IntPtr jobAttributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(
        SafeKernelObjectHandle job,
        JobObjectInfoType informationClass,
        IntPtr information,
        uint informationLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(
        SafeKernelObjectHandle job,
        SafeKernelObjectHandle process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(SafeKernelObjectHandle thread);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(SafeKernelObjectHandle process, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
