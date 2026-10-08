using System.ComponentModel;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using ILauncher.Models;

namespace ILauncher.Services;

internal sealed class AppInstaller
{
    private const string CompletionMarker = ".install-complete";
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> SharedPayloadLocks =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly string _appsBaseDirectory;
    private readonly string _startMenuDirectory;

    internal AppInstaller(string appsBaseDirectory, string startMenuDirectory)
    {
        _appsBaseDirectory = Path.GetFullPath(appsBaseDirectory);
        _startMenuDirectory = Path.GetFullPath(startMenuDirectory);
    }

    internal async Task<string> InstallAsync(LauncherEntry entry, string? executablePath = null)
    {
        var definition = entry.InstallDefinition
            ?? throw new InvalidOperationException($"{entry.Name}에는 설치 방법이 구성되어 있지 않습니다.");

        if (definition.Type == LauncherInstallType.Command)
            return await InstallCommandAsync(entry, definition).ConfigureAwait(false);

        if (definition.Type == LauncherInstallType.SharedPayload)
            return await Task.Run(() => InstallSharedPayload(entry), CancellationToken.None).ConfigureAwait(false);

        return await Task.Run(() => InstallPortable(entry, executablePath ?? entry.ExecutablePath),
            CancellationToken.None).ConfigureAwait(false);
    }

    internal Task<string> UninstallAsync(LauncherEntry entry) =>
        Task.Run(() => UninstallManagedInstall(entry), CancellationToken.None);

    private string UninstallManagedInstall(LauncherEntry entry)
    {
        var installType = entry.InstallDefinition?.Type;
        if (installType is not (LauncherInstallType.Portable or LauncherInstallType.SharedPayload))
            throw new InvalidOperationException("This installer does not own an uninstallable app receipt.");
        if (entry.InstalledAppDirectory is null || entry.InstalledExecutableRelativePath is null)
            throw new InvalidOperationException("The app's managed install paths are incomplete.");

        string appDirectory = Path.GetFullPath(entry.InstalledAppDirectory);
        EnsureContained(_appsBaseDirectory, appDirectory, "app install receipt directory");
        EnsureExistingAncestorsAreNotReparsePoints(_appsBaseDirectory);
        if (!Directory.Exists(appDirectory))
            return $"{entry.Name} is already removed.";
        EnsureNoReparsePoints(_appsBaseDirectory, appDirectory);

        string versionReceipt = Path.Combine(appDirectory, "current.version");
        EnsureContained(appDirectory, versionReceipt, "install version receipt");
        EnsureNoReparsePoints(appDirectory, versionReceipt);
        if (!File.Exists(versionReceipt))
            return $"{entry.Name} is already removed.";

        byte[] versionReceiptBytes = File.ReadAllBytes(versionReceipt);
        string version = new UTF8Encoding(false, true).GetString(versionReceiptBytes).Trim();
        if (version.Length != 64 || version.Any(character => !Uri.IsHexDigit(character)))
            throw new InvalidDataException("The app install receipt contains an invalid version hash.");

        string payloadDirectory = installType == LauncherInstallType.SharedPayload
            ? entry.InstalledSharedPayloadDirectory ?? throw new InvalidDataException("The shared payload directory is missing.")
            : appDirectory;
        string versionDirectory = Path.Combine(payloadDirectory, version);
        EnsureContained(payloadDirectory, versionDirectory, "installed app version");
        string completionMarker = Path.Combine(versionDirectory, CompletionMarker);
        EnsureContained(versionDirectory, completionMarker, "installed completion marker");
        EnsureNoReparsePoints(payloadDirectory, completionMarker);
        if (!File.Exists(completionMarker) ||
            !string.Equals(File.ReadAllText(completionMarker), "complete", StringComparison.Ordinal))
            throw new InvalidDataException("The installed app version is incomplete; its receipt was preserved.");

        string installedExecutable = entry.TryGetInstalledExecutablePath()
            ?? throw new InvalidDataException("The install receipt does not resolve to an executable; it was preserved.");
        string expectedExecutable = Path.GetFullPath(entry.InstalledExecutableRelativePath, versionDirectory);
        if (!Path.GetFullPath(installedExecutable).Equals(expectedExecutable, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The install receipt resolves to an unexpected executable.");
        EnsureNoReparsePoints(payloadDirectory, installedExecutable);
        string workingDirectory = ResolveInstalledWorkingDirectory(entry, versionDirectory);
        IReadOnlyList<string> arguments = installType == LauncherInstallType.SharedPayload
            ? entry.InstallShortcutArguments : entry.Arguments;
        string expectedArguments = arguments.Count == 0 ? "" : BuildWindowsCommandLine(arguments);

        string shortcutNameReceipt = Path.Combine(appDirectory, "shortcut.name");
        EnsureContained(appDirectory, shortcutNameReceipt, "shortcut name receipt");
        EnsureNoReparsePoints(appDirectory, shortcutNameReceipt);
        string shortcutFileName = File.Exists(shortcutNameReceipt)
            ? File.ReadAllText(shortcutNameReceipt).Trim()
            : SafeShortcutName(entry.Name, entry.Id) + " (" + SafeShortcutName(entry.Id, entry.Id) + ").lnk";
        if (!shortcutFileName.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetFileName(shortcutFileName), shortcutFileName, StringComparison.Ordinal))
            throw new InvalidDataException("The shortcut receipt contains an unsafe file name.");

        EnsureExistingAncestorsAreNotReparsePoints(_startMenuDirectory);
        string shortcutPath = Path.Combine(_startMenuDirectory, shortcutFileName);
        EnsureContained(_startMenuDirectory, shortcutPath, "Start Menu shortcut");
        EnsureNoReparsePoints(_startMenuDirectory, shortcutPath);
        byte[]? shortcutBytes = null;
        if (File.Exists(shortcutPath))
        {
            VerifyOwnedShortcut(shortcutPath, installedExecutable, workingDirectory, expectedArguments);
            shortcutBytes = File.ReadAllBytes(shortcutPath);
        }

        var receiptBackups = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
        {
            [versionReceipt] = versionReceiptBytes
        };
        if (File.Exists(shortcutNameReceipt))
            receiptBackups[shortcutNameReceipt] = File.ReadAllBytes(shortcutNameReceipt);
        foreach (string iconPath in Directory.EnumerateFiles(appDirectory, "shortcut-icon.*", SearchOption.TopDirectoryOnly))
        {
            EnsureContained(appDirectory, iconPath, "shortcut icon receipt");
            EnsureNoReparsePoints(appDirectory, iconPath);
            receiptBackups[iconPath] = File.ReadAllBytes(iconPath);
        }

        try
        {
            if (shortcutBytes is not null) File.Delete(shortcutPath);
            foreach (string receiptPath in receiptBackups.Keys
                         .Where(path => !path.Equals(versionReceipt, StringComparison.OrdinalIgnoreCase)))
                File.Delete(receiptPath);
            File.Delete(versionReceipt);
        }
        catch (Exception uninstallError)
        {
            var rollbackErrors = new List<Exception>();
            foreach ((string path, byte[] contents) in receiptBackups)
            {
                try
                {
                    if (!File.Exists(path))
                    {
                        EnsureContained(appDirectory, path, "install receipt rollback path");
                        EnsureNoReparsePoints(_appsBaseDirectory, appDirectory);
                        WriteBytesAtomically(path, contents);
                    }
                }
                catch (Exception rollbackError) { rollbackErrors.Add(rollbackError); }
            }
            if (shortcutBytes is not null)
            {
                try
                {
                    if (!File.Exists(shortcutPath))
                    {
                        EnsureContained(_startMenuDirectory, shortcutPath, "shortcut rollback path");
                        EnsureNoReparsePoints(_startMenuDirectory, shortcutPath);
                        WriteBytesAtomically(shortcutPath, shortcutBytes);
                    }
                }
                catch (Exception rollbackError) { rollbackErrors.Add(rollbackError); }
            }
            if (rollbackErrors.Count > 0)
                throw new AggregateException("Uninstall failed and its receipt or shortcut could not be fully restored.",
                    [uninstallError, .. rollbackErrors]);
            throw;
        }

        try
        {
            if (!Directory.EnumerateFileSystemEntries(appDirectory).Any())
                Directory.Delete(appDirectory, recursive: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An empty receipt directory is optional; payloads and unknown files remain untouched.
        }

        return $"{entry.Name} was removed from the Start Menu. Installed files and user data were preserved.";
    }

    private static void VerifyOwnedShortcut(string shortcutPath, string expectedExecutable,
        string expectedWorkingDirectory, string expectedArguments)
    {
        object? shell = null;
        object? shortcut = null;
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell", throwOnError: true)
                ?? throw new PlatformNotSupportedException("Windows Script Host is unavailable.");
            shell = Activator.CreateInstance(shellType)
                ?? throw new InvalidOperationException("Could not create Windows Script Host.");
            shortcut = shellType.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod,
                null, shell, [shortcutPath]);
            var shortcutType = shortcut?.GetType() ?? throw new InvalidDataException("The recorded shortcut is invalid.");
            string target = (string?)shortcutType.InvokeMember("TargetPath", System.Reflection.BindingFlags.GetProperty,
                null, shortcut, null) ?? "";
            string working = (string?)shortcutType.InvokeMember("WorkingDirectory", System.Reflection.BindingFlags.GetProperty,
                null, shortcut, null) ?? "";
            string arguments = (string?)shortcutType.InvokeMember("Arguments", System.Reflection.BindingFlags.GetProperty,
                null, shortcut, null) ?? "";
            if (!Path.IsPathRooted(target) || !Path.IsPathRooted(working) ||
                !Path.GetFullPath(target).Equals(Path.GetFullPath(expectedExecutable), StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFullPath(working).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    .Equals(Path.GetFullPath(expectedWorkingDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                        StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(arguments, expectedArguments, StringComparison.Ordinal))
                throw new InvalidDataException("The recorded Start Menu shortcut does not match this app; it was preserved.");
        }
        finally
        {
            if (shortcut is not null && Marshal.IsComObject(shortcut)) Marshal.FinalReleaseComObject(shortcut);
            if (shell is not null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
        }
    }

    private async Task<string> InstallCommandAsync(LauncherEntry entry, LauncherInstallDefinition definition)
    {
        var executablePath = definition.ExecutablePath
            ?? throw new InvalidOperationException("명령 설치 실행 파일이 지정되지 않았습니다.");
        ValidateExecutable(executablePath, Path.GetDirectoryName(executablePath)!);

        var startInfo = new ProcessStartInfo(executablePath)
        {
            WorkingDirectory = Path.GetDirectoryName(executablePath)!,
            UseShellExecute = true
        };
        foreach (var argument in definition.Arguments)
            startInfo.ArgumentList.Add(argument);

        Process process;
        try
        {
            process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("설치 프로세스를 시작하지 못했습니다.");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            throw new OperationCanceledException("설치가 취소되었습니다.", ex);
        }

        using (process)
        {
            await process.WaitForExitAsync().ConfigureAwait(false);
            if (process.ExitCode == 1223)
                throw new OperationCanceledException("설치가 취소되었습니다.");
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"{entry.Name} 설치가 종료 코드 {process.ExitCode}(으)로 실패했습니다.");
        }

        return $"{entry.Name} 설치가 완료되었습니다.";
    }

    private string InstallPortable(LauncherEntry entry, string sourceExecutable)
    {
        if (entry.InstalledAppDirectory is null || entry.InstalledExecutableRelativePath is null)
            throw new InvalidOperationException("portable 설치 경로가 준비되지 않았습니다.");

        var sourceDirectory = Path.GetDirectoryName(sourceExecutable)
            ?? throw new InvalidOperationException("게시 폴더를 확인할 수 없습니다.");
        ValidateExecutable(sourceExecutable, sourceDirectory);
        EnsureNoReparsePoints(sourceDirectory, sourceExecutable);
        if (PathsOverlap(sourceDirectory, entry.InstalledAppDirectory))
            throw new InvalidDataException("설치 대상 폴더와 게시 원본 폴더가 겹칩니다.");

        string version = ComputeDirectoryHash(sourceDirectory, excludeInstallState: true);
        string versionDirectory = Path.Combine(entry.InstalledAppDirectory, version);
        EnsureContained(entry.InstalledAppDirectory, versionDirectory, "버전 설치 폴더");

        EnsureExistingAncestorsAreNotReparsePoints(_appsBaseDirectory);
        Directory.CreateDirectory(_appsBaseDirectory);
        EnsureExistingAncestorsAreNotReparsePoints(_appsBaseDirectory);
        Directory.CreateDirectory(entry.InstalledAppDirectory);
        EnsureContained(_appsBaseDirectory, entry.InstalledAppDirectory, "앱 설치 폴더");
        EnsureNoReparsePoints(_appsBaseDirectory, entry.InstalledAppDirectory);

        bool promoted = false;
        string? stagingDirectory = null;
        try
        {
            if (Directory.Exists(versionDirectory))
            {
                EnsureNoReparsePoints(entry.InstalledAppDirectory, versionDirectory);
                string completionMarker = Path.Combine(versionDirectory, CompletionMarker);
                if (!File.Exists(completionMarker)
                    || !string.Equals(version, ComputeDirectoryHash(versionDirectory), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"기존 설치 버전의 내용이 손상되었습니다: {versionDirectory}");
            }
            else
            {
                stagingDirectory = Path.Combine(entry.InstalledAppDirectory, ".staging-" + Guid.NewGuid().ToString("N"));
                EnsureContained(entry.InstalledAppDirectory, stagingDirectory, "임시 설치 폴더");
                Directory.CreateDirectory(stagingDirectory);
                CopyPublishDirectory(sourceDirectory, stagingDirectory);
                var stagedExecutable = Path.GetFullPath(entry.InstalledExecutableRelativePath, stagingDirectory);
                EnsureContained(stagingDirectory, stagedExecutable, "설치 실행 파일");
                if (!File.Exists(stagedExecutable))
                    throw new FileNotFoundException("복사된 설치 실행 파일이 없습니다.", stagedExecutable);
                if (!string.Equals(version, ComputeDirectoryHash(stagingDirectory), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The source publish output changed while it was being installed.");
                using (var marker = new FileStream(Path.Combine(stagingDirectory, CompletionMarker),
                           FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    marker.Write("complete"u8);
                Directory.Move(stagingDirectory, versionDirectory);
                promoted = true;
            }

            var installedExecutable = Path.GetFullPath(entry.InstalledExecutableRelativePath, versionDirectory);
            EnsureContained(versionDirectory, installedExecutable, "설치 실행 파일");
            var installedWorkingDirectory = ResolveInstalledWorkingDirectory(entry, versionDirectory);
            CommitInstalledShortcut(entry, installedExecutable, installedWorkingDirectory, entry.Arguments,
                entry.InstalledAppDirectory, version);
            return $"{entry.Name} 설치가 완료되었습니다.";
        }
        finally
        {
            if (!promoted && stagingDirectory is not null && Directory.Exists(stagingDirectory))
            {
                EnsureContained(entry.InstalledAppDirectory, stagingDirectory, "임시 설치 폴더");
                EnsureTreeHasNoReparsePoints(stagingDirectory);
                Directory.Delete(stagingDirectory, recursive: true);
            }
        }
    }

    private string InstallSharedPayload(LauncherEntry entry)
    {
        if (entry.InstallSourceDirectory is null || entry.InstalledSharedPayloadDirectory is null
            || entry.InstalledAppDirectory is null || entry.InstalledExecutableRelativePath is null)
            throw new InvalidOperationException("Shared payload install paths are incomplete.");

        string sourceDirectory = Path.GetFullPath(entry.InstallSourceDirectory);
        string sharedDirectory = Path.GetFullPath(entry.InstalledSharedPayloadDirectory);
        string appDirectory = Path.GetFullPath(entry.InstalledAppDirectory);
        string sourceExecutable = Path.GetFullPath(entry.InstalledExecutableRelativePath, sourceDirectory);
        ValidateExecutable(sourceExecutable, sourceDirectory);
        EnsureNoReparsePoints(sourceDirectory, sourceExecutable);
        string expectedHash = ComputeDirectoryHash(sourceDirectory, excludeInstallState: true);
        string expectedVersionDirectory = Path.Combine(sharedDirectory, expectedHash);
        var sharedLock = SharedPayloadLocks.GetOrAdd(expectedVersionDirectory, static _ => new SemaphoreSlim(1, 1));
        sharedLock.Wait();
        try
        {
        string? reusableSourceVersion = null;
        if (IsPathWithin(sharedDirectory, sourceDirectory))
        {
            string? sourceParent = Path.GetDirectoryName(sourceDirectory);
            if (sourceParent is null || !sourceParent.Equals(sharedDirectory, StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(sourceDirectory).Equals(expectedHash, StringComparison.OrdinalIgnoreCase)
                || !IsCompleteSharedPayload(sourceDirectory, expectedHash))
                throw new InvalidDataException("The source overlaps the shared install store but is not a complete shared payload.");
            reusableSourceVersion = sourceDirectory;
        }
        else if (PathsOverlap(sourceDirectory, sharedDirectory))
        {
            throw new InvalidDataException("The shared install destination overlaps its source payload.");
        }

        string versionDirectory = reusableSourceVersion ?? Path.Combine(sharedDirectory, expectedHash);
        EnsureContained(sharedDirectory, versionDirectory, "shared payload version");

        EnsureExistingAncestorsAreNotReparsePoints(_appsBaseDirectory);
        Directory.CreateDirectory(_appsBaseDirectory);
        EnsureExistingAncestorsAreNotReparsePoints(_appsBaseDirectory);
        Directory.CreateDirectory(sharedDirectory);
        EnsureContained(_appsBaseDirectory, sharedDirectory, "shared payload directory");
        EnsureNoReparsePoints(_appsBaseDirectory, sharedDirectory);

        if (reusableSourceVersion is null && !IsCompleteSharedPayload(versionDirectory, expectedHash))
        {
            string stagingDirectory = Path.Combine(sharedDirectory, ".staging-" + Guid.NewGuid().ToString("N"));
            EnsureContained(sharedDirectory, stagingDirectory, "shared payload staging directory");
            Directory.CreateDirectory(stagingDirectory);
            bool promoted = false;
            try
            {
                CopyPublishDirectory(sourceDirectory, stagingDirectory);
                string stagedExecutable = Path.GetFullPath(entry.InstalledExecutableRelativePath, stagingDirectory);
                EnsureContained(stagingDirectory, stagedExecutable, "shared payload executable");
                if (!File.Exists(stagedExecutable))
                    throw new FileNotFoundException("The copied shared executable is missing.", stagedExecutable);
                string copiedHash = ComputeDirectoryHash(stagingDirectory);
                if (!string.Equals(expectedHash, copiedHash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The source payload changed while it was being installed.");
                if (Directory.Exists(versionDirectory))
                {
                    EnsureNoReparsePoints(sharedDirectory, versionDirectory);
                    if (!IsCompleteSharedPayload(versionDirectory, expectedHash))
                        throw new InvalidDataException($"An incomplete shared payload already occupies {versionDirectory}");
                }
                else
                {
                    using (var marker = new FileStream(Path.Combine(stagingDirectory, CompletionMarker),
                               FileMode.CreateNew, FileAccess.Write, FileShare.None))
                        marker.Write("complete"u8);
                    try
                    {
                        Directory.Move(stagingDirectory, versionDirectory);
                        promoted = true;
                    }
                    catch (IOException) when (Directory.Exists(versionDirectory)
                        && IsCompleteSharedPayload(versionDirectory, expectedHash))
                    {
                        // Another launcher instance atomically published the same immutable version.
                    }
                }
            }
            finally
            {
                if (!promoted && Directory.Exists(stagingDirectory))
                {
                    EnsureContained(sharedDirectory, stagingDirectory, "shared payload staging directory");
                    EnsureTreeHasNoReparsePoints(stagingDirectory);
                    Directory.Delete(stagingDirectory, recursive: true);
                }
            }
        }

        string installedExecutable = Path.GetFullPath(entry.InstalledExecutableRelativePath, versionDirectory);
        EnsureContained(versionDirectory, installedExecutable, "shared payload executable");
        if (!File.Exists(installedExecutable))
            throw new FileNotFoundException("The installed shared executable is missing.", installedExecutable);

        EnsureExistingAncestorsAreNotReparsePoints(appDirectory);
        Directory.CreateDirectory(appDirectory);
        EnsureContained(_appsBaseDirectory, appDirectory, "per-app install receipt directory");
        EnsureNoReparsePoints(_appsBaseDirectory, appDirectory);
        string installedWorkingDirectory = ResolveInstalledWorkingDirectory(entry, versionDirectory);
        CommitInstalledShortcut(entry, installedExecutable, installedWorkingDirectory,
            entry.InstallShortcutArguments, appDirectory, expectedHash);
        return $"{entry.Name} installation completed.";
        }
        finally
        {
            sharedLock.Release();
        }
    }

    internal string RefreshInstalledShortcut(LauncherEntry entry)
    {
        string executable = entry.TryGetInstalledExecutablePath()
            ?? throw new InvalidOperationException($"{entry.Name} has no complete installed version.");
        string versionDirectory = Path.GetDirectoryName(executable)!;
        if (entry.InstalledWorkingDirectoryRelativePath is not null)
        {
            while (!File.Exists(Path.Combine(versionDirectory, CompletionMarker)))
                versionDirectory = Path.GetDirectoryName(versionDirectory)
                    ?? throw new InvalidDataException("Installed version marker was not found.");
        }
        EnsureNoReparsePoints(versionDirectory, executable);
        string workingDirectory = ResolveInstalledWorkingDirectory(entry, versionDirectory);
        var arguments = entry.InstallDefinition?.Type == LauncherInstallType.SharedPayload
            ? entry.InstallShortcutArguments : entry.Arguments;
        CreateShortcut(entry, executable, workingDirectory, arguments, entry.InstalledAppDirectory!);
        return $"{entry.Name} installation is already current; shortcut refreshed.";
    }

    private static bool IsCompleteSharedPayload(string versionDirectory, string expectedHash)
    {
        string completionMarker = Path.Combine(versionDirectory, CompletionMarker);
        if (!File.Exists(completionMarker)) return false;
        EnsureNoReparsePoints(Path.GetDirectoryName(versionDirectory)!, completionMarker);
        string installedHash = ComputeDirectoryHash(versionDirectory, excludeInstallState: true);
        return string.Equals(expectedHash, installedHash, StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveInstalledWorkingDirectory(LauncherEntry entry, string versionDirectory)
    {
        if (entry.InstalledWorkingDirectoryRelativePath is null)
            return Path.GetDirectoryName(Path.GetFullPath(entry.InstalledExecutableRelativePath!, versionDirectory))!;

        string executableDirectory = Path.GetDirectoryName(
            Path.GetFullPath(entry.InstalledExecutableRelativePath!, versionDirectory))!;
        var workingDirectory = Path.GetFullPath(entry.InstalledWorkingDirectoryRelativePath, executableDirectory);
        EnsureContained(versionDirectory, workingDirectory, "설치 작업 폴더");
        EnsureNoReparsePoints(versionDirectory, workingDirectory);
        return workingDirectory;
    }

    private void CreateShortcut(LauncherEntry entry, string executablePath, string workingDirectory,
        IReadOnlyList<string> arguments, string receiptDirectory)
    {
        EnsureExistingAncestorsAreNotReparsePoints(_startMenuDirectory);
        Directory.CreateDirectory(_startMenuDirectory);
        EnsureExistingAncestorsAreNotReparsePoints(_startMenuDirectory);
        EnsureContained(_appsBaseDirectory, receiptDirectory, "app install receipt directory");
        EnsureNoReparsePoints(_appsBaseDirectory, receiptDirectory);
        string shortcutFileName = ResolveShortcutFileName(entry, receiptDirectory);
        var shortcutPath = Path.Combine(_startMenuDirectory, shortcutFileName);
        EnsureContained(_startMenuDirectory, shortcutPath, "시작 메뉴 바로 가기");

        var temporaryShortcutPath = Path.Combine(_startMenuDirectory,
            Path.GetFileNameWithoutExtension(shortcutFileName) + ".tmp-" + Guid.NewGuid().ToString("N") + ".lnk");
        object? shell = null;
        object? shortcut = null;
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell", throwOnError: true)
                ?? throw new PlatformNotSupportedException("Windows Script Host를 사용할 수 없습니다.");
            shell = Activator.CreateInstance(shellType)
                ?? throw new InvalidOperationException("바로 가기 생성기를 시작하지 못했습니다.");
            shortcut = shellType.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell, [temporaryShortcutPath]);
            var shortcutType = shortcut?.GetType() ?? throw new InvalidOperationException("바로 가기를 만들지 못했습니다.");
            shortcutType.InvokeMember("TargetPath", System.Reflection.BindingFlags.SetProperty, null, shortcut, [executablePath]);
            shortcutType.InvokeMember("WorkingDirectory", System.Reflection.BindingFlags.SetProperty, null, shortcut, [workingDirectory]);
            shortcutType.InvokeMember("Description", System.Reflection.BindingFlags.SetProperty, null, shortcut, [$"{entry.Name} 실행"]);
            if (arguments.Count > 0)
                shortcutType.InvokeMember("Arguments", System.Reflection.BindingFlags.SetProperty, null, shortcut, [BuildWindowsCommandLine(arguments)]);
            string iconPath = ResolveShortcutIconPath(entry, receiptDirectory, executablePath);
            shortcutType.InvokeMember("IconLocation", System.Reflection.BindingFlags.SetProperty, null, shortcut,
                [iconPath]);
            shortcutType.InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod, null, shortcut, null);
            if (!File.Exists(temporaryShortcutPath))
                throw new InvalidOperationException("The temporary Start Menu shortcut was not created.");
            File.Move(temporaryShortcutPath, shortcutPath, overwrite: true);
            WriteAtomically(Path.Combine(receiptDirectory, "shortcut.name"), shortcutFileName);
        }
        finally
        {
            if (shortcut is not null && Marshal.IsComObject(shortcut)) Marshal.FinalReleaseComObject(shortcut);
            if (shell is not null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
            if (File.Exists(temporaryShortcutPath)) File.Delete(temporaryShortcutPath);
        }
    }

    private void CommitInstalledShortcut(LauncherEntry entry, string executablePath, string workingDirectory,
        IReadOnlyList<string> arguments, string receiptDirectory, string version)
    {
        string shortcutFileName = ResolveShortcutFileName(entry, receiptDirectory);
        string shortcutPath = Path.Combine(_startMenuDirectory, shortcutFileName);
        string shortcutNameReceipt = Path.Combine(receiptDirectory, "shortcut.name");
        bool hadShortcut = File.Exists(shortcutPath);
        byte[]? previousShortcut = null;
        if (hadShortcut)
        {
            EnsureNoReparsePoints(_startMenuDirectory, shortcutPath);
            previousShortcut = File.ReadAllBytes(shortcutPath);
        }

        bool hadShortcutName = File.Exists(shortcutNameReceipt);
        string? previousShortcutName = null;
        if (hadShortcutName)
        {
            EnsureNoReparsePoints(receiptDirectory, shortcutNameReceipt);
            previousShortcutName = File.ReadAllText(shortcutNameReceipt);
        }

        try
        {
            CreateShortcut(entry, executablePath, workingDirectory, arguments, receiptDirectory);
            WriteAtomically(Path.Combine(receiptDirectory, "current.version"), version);
        }
        catch (Exception installError)
        {
            try
            {
                if (hadShortcut)
                    WriteBytesAtomically(shortcutPath, previousShortcut!);
                else if (File.Exists(shortcutPath))
                    File.Delete(shortcutPath);

                if (hadShortcutName)
                    WriteAtomically(shortcutNameReceipt, previousShortcutName!);
                else if (File.Exists(shortcutNameReceipt))
                    File.Delete(shortcutNameReceipt);
            }
            catch (Exception rollbackError)
            {
                throw new AggregateException("Install receipt commit failed and the previous shortcut could not be restored.",
                    installError, rollbackError);
            }

            throw;
        }
    }

    private static void WriteBytesAtomically(string path, byte[] contents)
    {
        string temporaryPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllBytes(temporaryPath, contents);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private string ResolveShortcutFileName(LauncherEntry entry, string receiptDirectory)
    {
        string receiptPath = Path.Combine(receiptDirectory, "shortcut.name");
        if (File.Exists(receiptPath))
        {
            string existingName = File.ReadAllText(receiptPath).Trim();
            if (existingName.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)
                && string.Equals(Path.GetFileName(existingName), existingName, StringComparison.Ordinal))
            {
                string recordedPath = Path.Combine(_startMenuDirectory, existingName);
                EnsureContained(_startMenuDirectory, recordedPath, "recorded start menu shortcut");
                EnsureNoReparsePoints(_startMenuDirectory, recordedPath);
                return existingName;
            }
        }

        string uniqueName = SafeShortcutName(entry.Name, entry.Id) + " (" + SafeShortcutName(entry.Id, entry.Id) + ").lnk";
        EnsureContained(_startMenuDirectory, Path.Combine(_startMenuDirectory, uniqueName), "unique start menu shortcut");
        return uniqueName;
    }

    private static string ResolveShortcutIconPath(LauncherEntry entry, string receiptDirectory, string executablePath)
    {
        string sourceIcon = entry.IconPath;
        string? sourceRoot = entry.InstallIconSourceDirectory;
        if (!string.IsNullOrWhiteSpace(sourceIcon) && sourceRoot is not null && File.Exists(sourceIcon))
        {
            try
            {
                EnsureContained(sourceRoot, sourceIcon, "shortcut icon source");
                EnsureNoReparsePoints(sourceRoot, sourceIcon);
                string extension = Path.GetExtension(sourceIcon);
                if (extension.Length is < 2 or > 12 || extension[1..].Any(character => !char.IsAsciiLetterOrDigit(character)))
                    extension = ".ico";
                string destination = Path.Combine(receiptDirectory, "shortcut-icon" + extension);
                EnsureContained(receiptDirectory, destination, "shortcut icon receipt");
                if (!Path.GetFullPath(sourceIcon).Equals(Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
                {
                    string temporary = Path.Combine(receiptDirectory,
                        "shortcut-icon.tmp-" + Guid.NewGuid().ToString("N") + extension);
                    try
                    {
                        File.Copy(sourceIcon, temporary, overwrite: false);
                        File.Move(temporary, destination, overwrite: true);
                    }
                    finally
                    {
                        if (File.Exists(temporary)) File.Delete(temporary);
                    }
                }
                EnsureNoReparsePoints(receiptDirectory, destination);
                return destination;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
            {
                // An unavailable or unsafe optional icon falls back to the installed executable's icon.
            }
        }

        foreach (string extension in new[] { ".ico", ".png", ".bmp" })
        {
            string existingIcon = Path.Combine(receiptDirectory, "shortcut-icon" + extension);
            if (!File.Exists(existingIcon)) continue;
            EnsureNoReparsePoints(receiptDirectory, existingIcon);
            return existingIcon;
        }
        return executablePath;
    }

    private static void CopyPublishDirectory(string sourceDirectory, string destinationDirectory)
    {
        EnsureDirectoryIsNotReparsePoint(sourceDirectory);
        var pending = new Stack<string>();
        pending.Push(sourceDirectory);
        while (pending.Count > 0)
        {
            var currentDirectory = pending.Pop();
            foreach (var path in Directory.EnumerateFileSystemEntries(currentDirectory))
            {
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException($"게시 폴더에 reparse point가 포함되어 있습니다: {path}");

                var relativePath = Path.GetRelativePath(sourceDirectory, path);
                if (string.Equals(Path.GetFileName(relativePath), CompletionMarker, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"게시 폴더에 예약된 설치 표식 파일이 포함되어 있습니다: {path}");
                if (ShouldExclude(relativePath))
                    continue;

                var destination = Path.GetFullPath(relativePath, destinationDirectory);
                EnsureContained(destinationDirectory, destination, "복사 대상");
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    Directory.CreateDirectory(destination);
                    pending.Push(path);
                }
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    File.Copy(path, destination, overwrite: false);
                }
            }
        }
    }

    private static bool ShouldExclude(string relativePath)
    {
        foreach (var segment in relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (segment.StartsWith(".build-staging-", StringComparison.OrdinalIgnoreCase) ||
                segment.StartsWith("app.previous-", StringComparison.OrdinalIgnoreCase) ||
                segment.Contains(".previous-", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals("deskcal.db", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals("deskcal.db-wal", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals("deskcal.db-shm", StringComparison.OrdinalIgnoreCase) ||
                segment.Contains(".migrating-", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static string ComputeDirectoryHash(string directory, bool excludeInstallState = false)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var files = EnumerateFilesWithoutReparsePoints(directory)
            .Where(path => !string.Equals(Path.GetFileName(path), CompletionMarker, StringComparison.OrdinalIgnoreCase))
            .Where(path => !excludeInstallState || !ShouldExclude(Path.GetRelativePath(directory, path)))
            .OrderBy(path => Path.GetRelativePath(directory, path), StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            var relativePath = Path.GetRelativePath(directory, file).Replace('\\', '/');
            hash.AppendData(Encoding.UTF8.GetBytes(relativePath));
            hash.AppendData([0]);
            using var stream = File.OpenRead(file);
            var buffer = new byte[128 * 1024];
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                hash.AppendData(buffer.AsSpan(0, read));
            hash.AppendData([0]);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static IReadOnlyList<string> EnumerateFilesWithoutReparsePoints(string root)
    {
        EnsureDirectoryIsNotReparsePoint(root);
        var files = new List<string>();
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException($"설치 파일에 reparse point가 포함되어 있습니다: {path}");
                if ((attributes & FileAttributes.Directory) != 0)
                    pending.Push(path);
                else
                    files.Add(path);
            }
        }
        return files;
    }

    private static void WriteAtomically(string path, string contents)
    {
        var temporaryPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(temporaryPath, contents, new UTF8Encoding(false));
        File.Move(temporaryPath, path, overwrite: true);
    }

    private static void ValidateExecutable(string executablePath, string expectedDirectory)
    {
        EnsureExistingAncestorsAreNotReparsePoints(executablePath);
        EnsureContained(expectedDirectory, executablePath, "실행 파일");
        EnsureNoReparsePoints(expectedDirectory, executablePath);
        if (!string.Equals(Path.GetExtension(executablePath), ".exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("실행 파일은 .exe 파일이어야 합니다.");
        if (!File.Exists(executablePath))
            throw new FileNotFoundException("실행 파일이 없습니다.", executablePath);
    }

    internal static void EnsureContained(string baseDirectory, string path, string label)
    {
        var root = Path.GetFullPath(baseDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(path);
        var basePath = root.TrimEnd(Path.DirectorySeparatorChar);
        if (!string.Equals(candidate.TrimEnd(Path.DirectorySeparatorChar), basePath, StringComparison.OrdinalIgnoreCase) &&
            !candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"{label} 경로가 허용된 폴더를 벗어납니다: {path}");
    }

    internal static void EnsureNoReparsePoints(string baseDirectory, string path)
    {
        EnsureDirectoryIsNotReparsePoint(baseDirectory);
        var relativePath = Path.GetRelativePath(baseDirectory, path);
        var current = Path.GetFullPath(baseDirectory);
        foreach (var segment in relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (segment is "" or ".") continue;
            current = Path.Combine(current, segment);
            if (!File.Exists(current) && !Directory.Exists(current)) break;
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"reparse point 경로는 사용할 수 없습니다: {current}");
        }
    }

    private static void EnsureDirectoryIsNotReparsePoint(string path)
    {
        if (Directory.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException($"reparse point 폴더는 사용할 수 없습니다: {path}");
    }

    private static bool PathsOverlap(string first, string second)
    {
        static string WithSeparator(string path) =>
            Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var firstPath = WithSeparator(first);
        var secondPath = WithSeparator(second);
        return firstPath.StartsWith(secondPath, StringComparison.OrdinalIgnoreCase) ||
               secondPath.StartsWith(firstPath, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPathWithin(string parent, string candidate)
    {
        var parentPath = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var candidatePath = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return candidatePath.StartsWith(parentPath, StringComparison.OrdinalIgnoreCase);
    }

    private static void EnsureExistingAncestorsAreNotReparsePoints(string path)
    {
        var existing = Path.GetFullPath(path);
        while (!File.Exists(existing) && !Directory.Exists(existing))
        {
            existing = Path.GetDirectoryName(existing)
                ?? throw new InvalidDataException($"경로의 기존 상위 폴더를 확인할 수 없습니다: {path}");
        }

        for (var current = new DirectoryInfo(existing); current is not null; current = current.Parent)
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"reparse point 폴더는 사용할 수 없습니다: {current.FullName}");
    }

    private static void EnsureTreeHasNoReparsePoints(string root)
    {
        EnsureDirectoryIsNotReparsePoint(root);
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException($"reparse point가 포함된 임시 폴더는 삭제하지 않습니다: {path}");
                if ((attributes & FileAttributes.Directory) != 0)
                    pending.Push(path);
            }
        }
    }

    private static string SafeShortcutName(string name, string fallback)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var value = new string(name.Select(character => invalid.Contains(character) ? '_' : character).ToArray()).Trim().TrimEnd('.');
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }

    private static string BuildWindowsCommandLine(IReadOnlyList<string> arguments) =>
        string.Join(" ", arguments.Select(QuoteWindowsArgument));

    private static string QuoteWindowsArgument(string argument)
    {
        if (argument.Length > 0 && argument.All(character => !char.IsWhiteSpace(character) && character != '"'))
            return argument;
        var result = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var character in argument)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }
            if (character == '"')
                result.Append('\\', backslashes * 2 + 1).Append('"');
            else
                result.Append('\\', backslashes).Append(character);
            backslashes = 0;
        }
        return result.Append('\\', backslashes * 2).Append('"').ToString();
    }
}
