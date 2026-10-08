using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

const BindingFlags PrivateStatic = BindingFlags.NonPublic | BindingFlags.Static;
string? receiverArgument = ReadOption(args, "--receiver", "-ReceiverAssembly");
string receiverAssemblyPath = receiverArgument is null
    ? FindReceiverAssembly()
    : Path.GetFullPath(receiverArgument);
string? senderArgument = ReadOption(args, "--sender", "-SenderAssembly");
string senderAssemblyPath = senderArgument is null
    ? FindSenderAssembly()
    : Path.GetFullPath(senderArgument);
string resultsPath = Path.GetFullPath(ReadOption(args, "--results", "-ResultsPath") ?? "latest-results.json");

if (!OperatingSystem.IsWindows())
{
    Console.Error.WriteLine("These checks exercise Windows filesystem behavior and must run on Windows.");
    return 2;
}

Assembly receiverAssembly = Assembly.LoadFrom(receiverAssemblyPath);
Assembly senderAssembly = Assembly.LoadFrom(senderAssemblyPath);
Type serviceType = receiverAssembly.GetType("GitPullApp.GitPullService", throwOnError: true)!;
MethodInfo swap = serviceType.GetMethod(
    "SwapPreparedDirectory",
    PrivateStatic,
    binder: null,
    [typeof(string), typeof(string), typeof(string), typeof(Action<string>)],
    modifiers: null) ?? throw new MissingMethodException(serviceType.FullName, "SwapPreparedDirectory");

string workspace = Path.Combine(Path.GetTempPath(), "ReceiveCompatibilityChecks", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(workspace);
List<CheckResult> results = [];

try
{
    Run("actual Sender excludes dist/bin/obj", SenderExclusionRoundTrip);
    Run("plain generated directories", PlainGeneratedDirectories);
    Run("read-only and hidden generated directories", ReadOnlyHiddenGeneratedDirectories);
    Run("generated directory path longer than 260 characters", DeepGeneratedDirectories);
    Run("exclusive open file inside generated directory", ExclusiveOpenFile);
    Run("open directory handle inside generated directory", OpenDirectoryHandle);
    Run("current directory inside generated directory", CurrentDirectoryInsideClone);
    Run("loaded executable inside generated directory", LoadedExecutable);
    Run("exclusive open build output outside clone", ExternalBuildDirectory);
    Run("missing incoming .git rolls back", MissingIncomingGitRollsBack);
}
finally
{
    TryCleanup(workspace);
}

var report = new
{
    generatedAt = DateTimeOffset.Now,
    os = RuntimeInformation.OSDescription,
    framework = RuntimeInformation.FrameworkDescription,
    receiverAssembly = receiverAssemblyPath,
    receiverAssemblySha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(receiverAssemblyPath))),
    senderAssembly = senderAssemblyPath,
    senderAssemblySha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(senderAssemblyPath))),
    method = serviceType.FullName + "." + swap.Name,
    results
};
Directory.CreateDirectory(Path.GetDirectoryName(resultsPath)!);
File.WriteAllText(resultsPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));

Console.WriteLine();
Console.WriteLine($"Receiver: {receiverAssemblyPath}");
Console.WriteLine($"Sender:   {senderAssemblyPath}");
Console.WriteLine($"Results:  {resultsPath}");
int failures = results.Count(result => !result.Passed);
Console.WriteLine(failures == 0 ? "PASS: all compatibility assertions held." : $"FAIL: {failures} compatibility assertion(s) failed.");
return failures == 0 ? 0 : 1;

void Run(string name, Func<Observation> check)
{
    Stopwatch timer = Stopwatch.StartNew();
    try
    {
        Observation observation = check();
        results.Add(new CheckResult(name, true, observation.Outcome, timer.ElapsedMilliseconds, observation.ExceptionType,
            observation.ExceptionMessage, observation.Reports, observation.Details));
        Console.WriteLine($"PASS | {name} | {observation.Outcome}");
    }
    catch (Exception exception)
    {
        results.Add(new CheckResult(name, false, "assertion failed", timer.ElapsedMilliseconds, exception.GetType().FullName,
            exception.Message, [], new Dictionary<string, object?>()));
        Console.WriteLine($"FAIL | {name} | {exception.GetType().Name}: {exception.Message}");
    }
}

Observation SenderExclusionRoundTrip()
{
    string root = Path.Combine(workspace, "sender-exclusion");
    string sourceWithGenerated = Path.Combine(root, "source-with-generated");
    string sourceWithoutGenerated = Path.Combine(root, "source-without-generated");
    Directory.CreateDirectory(Path.Combine(sourceWithGenerated, "Launcher"));
    Directory.CreateDirectory(Path.Combine(sourceWithoutGenerated, "Launcher"));
    File.WriteAllText(Path.Combine(sourceWithGenerated, "Launcher", "Program.cs"), "identical source");
    File.WriteAllText(Path.Combine(sourceWithoutGenerated, "Launcher", "Program.cs"), "identical source");
    File.WriteAllText(Path.Combine(sourceWithGenerated, "README.md"), "identical readme");
    File.WriteAllText(Path.Combine(sourceWithoutGenerated, "README.md"), "identical readme");
    CreateGeneratedTrees(sourceWithGenerated);

    string packageWithGenerated = Path.Combine(root, "with-generated.payload");
    string packageWithoutGenerated = Path.Combine(root, "without-generated.payload");
    string extractWithGenerated = Path.Combine(root, "extract-with-generated");
    string extractWithoutGenerated = Path.Combine(root, "extract-without-generated");
    string key = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));

    Type senderPackage = senderAssembly.GetType("PullverseSenderService.EncryptedTransferPackage", throwOnError: true)!;
    MethodInfo create = senderPackage.GetMethod("Create", BindingFlags.Public | BindingFlags.Static)
        ?? throw new MissingMethodException(senderPackage.FullName, "Create");
    Type receiverPackage = receiverAssembly.GetType("GitPullApp.EncryptedTransferPackage", throwOnError: true)!;
    MethodInfo extract = receiverPackage.GetMethod("Extract", BindingFlags.Public | BindingFlags.Static)
        ?? throw new MissingMethodException(receiverPackage.FullName, "Extract");

    InvokeAndUnwrap(create, sourceWithGenerated, packageWithGenerated, key);
    InvokeAndUnwrap(create, sourceWithoutGenerated, packageWithoutGenerated, key);
    InvokeAndUnwrap(extract, packageWithGenerated, extractWithGenerated, key);
    InvokeAndUnwrap(extract, packageWithoutGenerated, extractWithoutGenerated, key);

    Dictionary<string, string> first = SnapshotFiles(extractWithGenerated);
    Dictionary<string, string> second = SnapshotFiles(extractWithoutGenerated);
    Assert(first.Count == 2, "Sender exclusion fixture extracted an unexpected file count.");
    Assert(first.OrderBy(pair => pair.Key).SequenceEqual(second.OrderBy(pair => pair.Key)),
        "Sender output differed when generated directories were present in its source.");
    foreach (string generated in new[] { "dist", "bin", "obj" })
        Assert(!Directory.Exists(Path.Combine(extractWithGenerated, generated)), $"Sender included excluded {generated} output.");

    return new Observation(
        "both encrypted packages extracted to identical source-only file trees",
        null,
        null,
        [],
        new Dictionary<string, object?>
        {
            ["sourceWithGeneratedFileCount"] = Directory.EnumerateFiles(sourceWithGenerated, "*", SearchOption.AllDirectories).Count(),
            ["sourceWithoutGeneratedFileCount"] = Directory.EnumerateFiles(sourceWithoutGenerated, "*", SearchOption.AllDirectories).Count(),
            ["extractedFileCount"] = first.Count,
            ["extractedPaths"] = first.Keys.Order().ToArray()
        });
}

Observation PlainGeneratedDirectories()
{
    Fixture fixture = CreateFixture("plain");
    CreateGeneratedTrees(fixture.Repository);
    Invocation invocation = InvokeSwap(fixture);
    Assert(invocation.Exception is null, "Plain generated directories prevented the swap.");
    AssertInstalled(fixture);
    Assert(!Directory.Exists(fixture.Backup), "Plain backup was not cleaned.");
    return Observe("swap succeeded and old dist/bin/obj were removed", invocation, fixture);
}

Observation ReadOnlyHiddenGeneratedDirectories()
{
    Fixture fixture = CreateFixture("attributes");
    CreateGeneratedTrees(fixture.Repository);
    string directory = Path.Combine(fixture.Repository, "dist");
    string file = Path.Combine(directory, "bundle.js");
    File.SetAttributes(file, File.GetAttributes(file) | FileAttributes.ReadOnly | FileAttributes.Hidden);
    File.SetAttributes(directory, File.GetAttributes(directory) | FileAttributes.ReadOnly | FileAttributes.Hidden);
    Invocation invocation = InvokeSwap(fixture);
    Assert(invocation.Exception is null, "Read-only/hidden entries prevented the swap.");
    AssertInstalled(fixture);
    Assert(!Directory.Exists(fixture.Backup), "Attributed backup was not cleaned.");
    return Observe("swap succeeded; receiver cleared attributes during cleanup", invocation, fixture);
}

Observation DeepGeneratedDirectories()
{
    Fixture fixture = CreateFixture("deep");
    string deep = Path.Combine(fixture.Repository, "dist", new string('a', 70), new string('b', 70), new string('c', 70), new string('d', 70));
    Directory.CreateDirectory(deep);
    string deepFile = Path.Combine(deep, "bundle.js");
    File.WriteAllText(deepFile, "deep generated artifact");
    Assert(deepFile.Length > 260, "Long-path fixture did not exceed 260 characters.");
    Invocation invocation = InvokeSwap(fixture);
    Assert(invocation.Exception is null, "Long generated path prevented the swap.");
    AssertInstalled(fixture);
    Assert(!Directory.Exists(fixture.Backup), "Long-path backup was not cleaned.");
    return Observe("swap and cleanup succeeded", invocation, fixture, ("deepPathLength", deepFile.Length));
}

Observation ExclusiveOpenFile()
{
    Fixture fixture = CreateFixture("file-lock");
    CreateGeneratedTrees(fixture.Repository);
    string lockedPath = Path.Combine(fixture.Repository, "dist", "bundle.js");
    Invocation invocation;
    using (FileStream held = new(lockedPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
    {
        invocation = InvokeSwap(fixture);
        AssertSafeOutcome(fixture, invocation);
    }
    bool backupDuringLock = Directory.Exists(fixture.Backup);
    TryDeleteOwnedBackup(fixture.Backup);
    return Observe(OutcomeFor(invocation, backupDuringLock), invocation, fixture, ("backupPresentWhileLocked", backupDuringLock));
}

Observation OpenDirectoryHandle()
{
    Fixture fixture = CreateFixture("directory-handle");
    CreateGeneratedTrees(fixture.Repository);
    string heldDirectory = Path.Combine(fixture.Repository, "bin");
    using SafeFileHandle handle = OpenDirectoryWithoutDeleteSharing(heldDirectory);
    Invocation invocation = InvokeSwap(fixture);
    AssertSafeOutcome(fixture, invocation);
    bool backupDuringLock = Directory.Exists(fixture.Backup);
    handle.Dispose();
    TryDeleteOwnedBackup(fixture.Backup);
    return Observe(OutcomeFor(invocation, backupDuringLock), invocation, fixture, ("backupPresentWhileHandleOpen", backupDuringLock));
}

Observation CurrentDirectoryInsideClone()
{
    Fixture fixture = CreateFixture("current-directory");
    CreateGeneratedTrees(fixture.Repository);
    string heldDirectory = Path.Combine(fixture.Repository, "obj");
    string originalCurrentDirectory = Environment.CurrentDirectory;
    Invocation invocation;
    try
    {
        Environment.CurrentDirectory = heldDirectory;
        invocation = InvokeSwap(fixture);
    }
    finally
    {
        Environment.CurrentDirectory = originalCurrentDirectory;
    }
    AssertSafeOutcome(fixture, invocation);
    bool backupAfterRestore = Directory.Exists(fixture.Backup);
    TryDeleteOwnedBackup(fixture.Backup);
    return Observe(OutcomeFor(invocation, backupAfterRestore), invocation, fixture, ("backupPresentAfterCwdRestore", backupAfterRestore));
}

Observation LoadedExecutable()
{
    Fixture fixture = CreateFixture("loaded-executable");
    CreateGeneratedTrees(fixture.Repository);
    string executable = Path.Combine(fixture.Repository, "bin", "held-cmd.exe");
    File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), executable);
    using Process process = StartHeldExecutable(executable);
    Invocation invocation = InvokeSwap(fixture);
    AssertSafeOutcome(fixture, invocation);
    bool backupWhileRunning = Directory.Exists(fixture.Backup);
    process.StandardInput.Close();
    if (!process.WaitForExit(3000)) process.Kill(entireProcessTree: true);
    TryDeleteOwnedBackup(fixture.Backup);
    return Observe(OutcomeFor(invocation, backupWhileRunning), invocation, fixture,
        ("backupPresentWhileExecutableRunning", backupWhileRunning), ("processId", process.Id));
}

Observation ExternalBuildDirectory()
{
    Fixture fixture = CreateFixture("external-build");
    string source = Path.Combine(fixture.Repository, "Launcher", "Program.cs");
    Directory.CreateDirectory(Path.GetDirectoryName(source)!);
    File.WriteAllText(source, "source stays in clone");
    string externalRoot = Path.Combine(fixture.Root, "build-outside-clone", "Launcher", "bin");
    Directory.CreateDirectory(externalRoot);
    string externalArtifact = Path.Combine(externalRoot, "launcher.dll");
    File.WriteAllText(externalArtifact, "external artifact");
    Invocation invocation;
    using (FileStream held = new(externalArtifact, FileMode.Open, FileAccess.Read, FileShare.None))
    {
        invocation = InvokeSwap(fixture);
        Assert(invocation.Exception is null, "A lock outside the clone prevented the swap.");
        AssertInstalled(fixture);
        Assert(File.Exists(externalArtifact), "External build output was altered.");
        Assert(!Directory.Exists(fixture.Backup), "External-build fixture left a backup.");
    }
    return Observe("swap succeeded while external build output was exclusively open", invocation, fixture,
        ("externalArtifactPreserved", File.Exists(externalArtifact)));
}

Observation MissingIncomingGitRollsBack()
{
    Fixture fixture = CreateFixture("rollback", incomingHasGit: false);
    CreateGeneratedTrees(fixture.Repository);
    Invocation invocation = InvokeSwap(fixture);
    Assert(invocation.Exception is IOException, "Missing incoming .git did not produce the expected IOException.");
    AssertOriginalPreserved(fixture);
    Assert(Directory.Exists(fixture.Incoming), "Rejected incoming tree disappeared.");
    Assert(!Directory.Exists(fixture.Backup), "Rollback left the original in the backup path.");
    return Observe("IOException followed by successful rollback", invocation, fixture);
}

Fixture CreateFixture(string name, bool incomingHasGit = true)
{
    string root = Path.Combine(workspace, name);
    string repository = Path.Combine(root, "clone", "i-luncher");
    string incoming = Path.Combine(root, "incoming");
    string backup = Path.Combine(root, "backup");
    Directory.CreateDirectory(repository);
    Directory.CreateDirectory(incoming);
    File.WriteAllText(Path.Combine(repository, "original.txt"), "preserve original on failure");
    File.WriteAllText(Path.Combine(incoming, "received.txt"), "new incoming snapshot");
    if (incomingHasGit) Directory.CreateDirectory(Path.Combine(incoming, ".git"));
    return new Fixture(root, repository, incoming, backup);
}

static void CreateGeneratedTrees(string repository)
{
    foreach ((string directory, string file) in new[] { ("dist", "bundle.js"), ("bin", "app.dll"), ("obj", "cache.bin") })
    {
        string path = Path.Combine(repository, directory);
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, file), "old generated artifact");
    }
}

Invocation InvokeSwap(Fixture fixture)
{
    List<string> reports = [];
    try
    {
        swap.Invoke(null, [fixture.Repository, fixture.Incoming, fixture.Backup, (Action<string>)reports.Add]);
        return new Invocation(null, reports);
    }
    catch (TargetInvocationException exception) when (exception.InnerException is not null)
    {
        return new Invocation(exception.InnerException, reports);
    }
}

static void AssertSafeOutcome(Fixture fixture, Invocation invocation)
{
    if (invocation.Exception is null)
    {
        AssertInstalled(fixture);
        return;
    }

    Assert(invocation.Exception is IOException or UnauthorizedAccessException,
        "Lock failure had an unexpected exception type: " + invocation.Exception.GetType().FullName);
    AssertOriginalPreserved(fixture);
    Assert(Directory.Exists(fixture.Incoming), "Failed swap consumed the incoming tree.");
    Assert(!Directory.Exists(fixture.Backup), "Failed swap did not restore the original path.");
}

static void AssertInstalled(Fixture fixture)
{
    Assert(File.ReadAllText(Path.Combine(fixture.Repository, "received.txt")) == "new incoming snapshot", "Incoming snapshot was not installed.");
    Assert(!File.Exists(Path.Combine(fixture.Repository, "original.txt")), "Old clone contents remained at the live path.");
    foreach (string generated in new[] { "dist", "bin", "obj" })
        Assert(!Directory.Exists(Path.Combine(fixture.Repository, generated)), $"Old {generated} remained in the new incoming tree.");
}

static void AssertOriginalPreserved(Fixture fixture)
{
    Assert(File.ReadAllText(Path.Combine(fixture.Repository, "original.txt")) == "preserve original on failure", "Original marker was lost.");
    foreach (string generated in new[] { "dist", "bin", "obj" })
        Assert(Directory.Exists(Path.Combine(fixture.Repository, generated)), $"Original {generated} was lost after failure.");
}

static Dictionary<string, string> SnapshotFiles(string root) => Directory
    .EnumerateFiles(root, "*", SearchOption.AllDirectories)
    .ToDictionary(
        path => Path.GetRelativePath(root, path).Replace('\\', '/'),
        path => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))),
        StringComparer.Ordinal);

static void InvokeAndUnwrap(MethodInfo method, params object?[] parameters)
{
    try
    {
        method.Invoke(null, parameters);
    }
    catch (TargetInvocationException exception) when (exception.InnerException is not null)
    {
        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
    }
}

static Observation Observe(string outcome, Invocation invocation, Fixture fixture, params (string Key, object? Value)[] extras)
{
    Dictionary<string, object?> details = new()
    {
        ["repositoryExists"] = Directory.Exists(fixture.Repository),
        ["incomingExists"] = Directory.Exists(fixture.Incoming),
        ["backupExistsAtObservation"] = Directory.Exists(fixture.Backup)
    };
    foreach ((string key, object? value) in extras) details[key] = value;
    return new Observation(outcome, invocation.Exception?.GetType().FullName, invocation.Exception?.Message, invocation.Reports, details);
}

static string OutcomeFor(Invocation invocation, bool backupPresent) => invocation.Exception is not null
    ? "swap failed and original was preserved"
    : backupPresent
        ? "swap succeeded; locked old backup cleanup was deferred with a receiver warning"
        : "swap and cleanup succeeded despite the held resource";

static Process StartHeldExecutable(string executable)
{
    ProcessStartInfo startInfo = new(executable)
    {
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardInput = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true
    };
    startInfo.ArgumentList.Add("/d");
    startInfo.ArgumentList.Add("/q");
    startInfo.ArgumentList.Add("/c");
    startInfo.ArgumentList.Add("set /p held=");
    Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start held executable fixture.");
    Thread.Sleep(200);
    if (process.HasExited) throw new InvalidOperationException("Loaded executable fixture exited before the check.");
    return process;
}

static SafeFileHandle OpenDirectoryWithoutDeleteSharing(string path)
{
    SafeFileHandle handle = CreateFileW(
        path,
        0,
        FileShare.Read | FileShare.Write,
        IntPtr.Zero,
        FileMode.Open,
        0x02000000,
        IntPtr.Zero);
    if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not open directory fixture.");
    return handle;
}

static void TryDeleteOwnedBackup(string backup)
{
    if (!Directory.Exists(backup)) return;
    ClearAttributesRecursively(backup);
    Directory.Delete(backup, recursive: true);
}

static void TryCleanup(string root)
{
    string allowedRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "ReceiveCompatibilityChecks")) + Path.DirectorySeparatorChar;
    string resolved = Path.GetFullPath(root);
    if (!resolved.StartsWith(allowedRoot, StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("Refusing cleanup outside the diagnostic temp root: " + resolved);
    try
    {
        if (!Directory.Exists(resolved)) return;
        ClearAttributesRecursively(resolved);
        Directory.Delete(resolved, recursive: true);
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine("Fixture cleanup warning: " + exception.Message);
    }
}

static void ClearAttributesRecursively(string root)
{
    foreach (string entry in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories).Reverse())
    {
        try { File.SetAttributes(entry, FileAttributes.Normal); }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
    }
    File.SetAttributes(root, FileAttributes.Directory);
}

static string FindReceiverAssembly()
{
    for (DirectoryInfo? directory = new(Directory.GetCurrentDirectory()); directory is not null; directory = directory.Parent)
    {
        string candidate = Path.Combine(directory.FullName, "Send&Receive", "Receive", "Receive_source", "bin", "Release",
            "net8.0-windows", "win-x64", "GitPullApp.dll");
        if (File.Exists(candidate)) return candidate;
    }
    throw new FileNotFoundException("Pass --receiver with the compiled GitPullApp.dll path.");
}

static string FindSenderAssembly()
{
    for (DirectoryInfo? directory = new(Directory.GetCurrentDirectory()); directory is not null; directory = directory.Parent)
    {
        string candidate = Path.Combine(directory.FullName, "Send&Receive", "Send", "backend_source", "bin", "Release",
            "net8.0", "win-x64", "PullverseSenderService.dll");
        if (File.Exists(candidate)) return candidate;
    }
    throw new FileNotFoundException("Pass --sender or -SenderAssembly with the compiled PullverseSenderService.dll path.");
}

static string? ReadOption(string[] arguments, params string[] names)
{
    foreach (string name in names)
    {
        int index = Array.IndexOf(arguments, name);
        if (index >= 0 && index + 1 < arguments.Length) return arguments[index + 1];
    }
    return null;
}

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
static extern SafeFileHandle CreateFileW(
    string lpFileName,
    uint dwDesiredAccess,
    FileShare dwShareMode,
    IntPtr lpSecurityAttributes,
    FileMode dwCreationDisposition,
    uint dwFlagsAndAttributes,
    IntPtr hTemplateFile);

sealed record Fixture(string Root, string Repository, string Incoming, string Backup);
sealed record Invocation(Exception? Exception, List<string> Reports);
sealed record Observation(string Outcome, string? ExceptionType, string? ExceptionMessage, List<string> Reports,
    Dictionary<string, object?> Details);
sealed record CheckResult(string Name, bool Passed, string Outcome, long ElapsedMilliseconds, string? ExceptionType,
    string? ExceptionMessage, List<string> Reports, Dictionary<string, object?> Details);
