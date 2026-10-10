using ILauncher.Bootstrapper;
using ILauncher.Models;
using ILauncher.Services;

internal static class FullRemovalChecks
{
    internal static async Task RunAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), "iluncher-full-removal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string apps = Path.Combine(root, "Apps"), menu = Path.Combine(root, "menu"), source = Path.Combine(root, "source");
            Directory.CreateDirectory(source);
            File.WriteAllText(Path.Combine(source, "app.exe"), "isolated fake executable");
            var installer = new AppInstaller(apps, menu);
            LauncherEntry Entry(string id, bool shared = false) => new()
            {
                Id = id, Name = id, ExecutablePath = Path.Combine(source, "app.exe"), WorkingDirectory = source,
                InstallDefinition = new LauncherInstallDefinition(shared ? LauncherInstallType.SharedPayload : LauncherInstallType.Portable, null, []),
                InstallSourceDirectory = source, InstalledAppDirectory = Path.Combine(apps, id),
                InstalledSharedPayloadDirectory = Path.Combine(apps, "_shared-payloads"),
                InstalledExecutableRelativePath = "app.exe", InstallShortcutArguments = shared ? ["--app", id] : []
            };
            var first = Entry("first");
            await installer.InstallAsync(first);
            string receipt = Path.Combine(apps, first.Id, "current.version");
            string payload = Path.Combine(apps, first.Id, File.ReadAllText(receipt).Trim());
            string data = Path.Combine(payload, "saved-data.txt");
            File.WriteAllText(data, "inside managed installation");
            string external = Path.Combine(root, "documents.txt");
            File.WriteAllText(external, "external data");
            await installer.UninstallAsync(first);
            Require(File.Exists(data) && !File.Exists(receipt), "normal removal must keep payload and data");
            // Reinstall from unchanged source restores registration to the cached payload.
            File.Delete(data);
            await installer.InstallAsync(first);
            File.WriteAllText(data, "inside managed installation");
            using (File.Open(data, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                await Reject<IOException>(() => installer.UninstallAsync(first, true));
            Require(File.Exists(receipt) && File.Exists(data), "locked full removal must preserve payload and registration");
            string marker = Path.Combine(payload, ".install-complete");
            File.WriteAllText(marker, "foreign");
            await Reject<InvalidDataException>(() => installer.UninstallAsync(first, true));
            Require(File.Exists(receipt) && File.Exists(data), "foreign marker must fail closed");
            File.WriteAllText(marker, "complete");
            string junction = Path.Combine(payload, "linked-data");
            using (var linkProcess = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "cmd.exe", Arguments = "/d /c mklink /J \"" + junction + "\" \"" + source + "\"",
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
            }) ?? throw new InvalidOperationException("Could not create fixture junction."))
            {
                await linkProcess.WaitForExitAsync();
                Require(linkProcess.ExitCode == 0, "fixture junction creation failed");
            }
            try { await Reject<InvalidDataException>(() => installer.UninstallAsync(first, true)); }
            finally { Directory.Delete(junction); }
            Require(File.Exists(receipt) && File.Exists(Path.Combine(source, "app.exe")), "reparse refusal must preserve source");
            // Receipt is readable but cannot be deleted: staging must roll back.
            using (File.Open(receipt, FileMode.Open, FileAccess.Read, FileShare.Read))
                await Reject<IOException>(() => installer.UninstallAsync(first, true));
            Require(File.Exists(receipt) && File.Exists(data), "registration failure must restore staged payload");
            await installer.UninstallAsync(first, true);
            Require(!Directory.Exists(payload) && !File.Exists(receipt) && File.Exists(external) && File.Exists(Path.Combine(source, "app.exe")),
                "full removal must delete only owned payload and registration");

            await installer.InstallAsync(first);
            string nativeSameId = Path.Combine(root, "installed", first.Id, "current.receipt");
            Directory.CreateDirectory(Path.GetDirectoryName(nativeSameId)!);
            File.WriteAllText(nativeSameId, "native receipt owned by the other store");
            string nativeHash = new string('a', 64);
            string nativeVersion = Path.Combine(apps, first.Id, nativeHash);
            Directory.CreateDirectory(nativeVersion);
            File.WriteAllText(Path.Combine(nativeVersion, ".ready"), first.Id + "\n" + nativeHash + "\n\n");
            File.WriteAllText(Path.Combine(nativeVersion, "native.exe"), "native installation payload");
            await installer.UninstallAsync(first, true);
            Require(File.Exists(nativeSameId) && File.Exists(Path.Combine(nativeVersion, "native.exe")),
                "WPF full removal must preserve same-ID native registration and usable payload");
            File.Delete(nativeSameId);
            Directory.Delete(nativeVersion, recursive: true);

            var sharedOne = Entry("shared-one", true);
            var sharedTwo = Entry("shared-two", true);
            await installer.InstallAsync(sharedOne);
            await installer.InstallAsync(sharedTwo);
            string sharedReceipt = Path.Combine(apps, sharedOne.Id, "current.version");
            string sharedPayload = Path.Combine(apps, "_shared-payloads", File.ReadAllText(sharedReceipt).Trim());
            await installer.UninstallAsync(sharedOne, true);
            Require(Directory.Exists(sharedPayload) && File.Exists(Path.Combine(apps, sharedTwo.Id, "current.version")),
                "another WPF reference must retain shared payload");
            string nativeForeign = Path.Combine(root, "installed", "foreign", "current.receipt");
            Directory.CreateDirectory(Path.GetDirectoryName(nativeForeign)!);
            File.WriteAllText(nativeForeign, "malformed but potentially referenced");
            await installer.UninstallAsync(sharedTwo, true);
            Require(Directory.Exists(sharedPayload), "uncertain native reference must retain WPF shared payload");
            File.Delete(nativeForeign);
            await installer.InstallAsync(sharedTwo);
            await installer.UninstallAsync(sharedTwo, true);
            Require(!Directory.Exists(sharedPayload), "last proven WPF reference must clean shared payload");

            await installer.InstallAsync(sharedTwo);
            string sameIdSharedReceipt = Path.Combine(root, "installed", sharedTwo.Id, "current.receipt");
            Directory.CreateDirectory(Path.GetDirectoryName(sameIdSharedReceipt)!);
            File.WriteAllText(sameIdSharedReceipt, "same-ID native reference");
            await installer.UninstallAsync(sharedTwo, true);
            Require(Directory.Exists(sharedPayload), "same-ID native reference must retain WPF shared payload");

            using (await ManagedPayloadRemoval.AcquireStoreLockAsync(root, CancellationToken.None))
            using (var cancellation = new CancellationTokenSource(150))
                await Reject<OperationCanceledException>(async () =>
                {
                    using (await ManagedPayloadRemoval.AcquireStoreLockAsync(root, cancellation.Token)) { }
                });

            // A new lock acquired AFTER reversible staging can still defeat purge.
            // Verify the real cleanup helper reports its leftover path and does not
            // silently restore an original payload path after commit.
            string lateLockPayload = Path.Combine(root, "late-lock-payload");
            Directory.CreateDirectory(lateLockPayload);
            File.WriteAllText(Path.Combine(lateLockPayload, "locked.txt"), "remaining cache");
            var committedRemoval = new ManagedPayloadRemoval();
            committedRemoval.Add(root, lateLockPayload, null!);
            committedRemoval.Stage();
            string remaining = Directory.GetDirectories(root, "late-lock-payload.removing-*").Single();
            using (File.Open(Path.Combine(remaining, "locked.txt"), FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                try { committedRemoval.Purge(); throw new InvalidOperationException("Expected post-stage purge failure."); }
                catch (IOException error)
                {
                    Require(error.Message.Contains(remaining, StringComparison.Ordinal) && error.Message.Contains("registration was removed", StringComparison.Ordinal),
                        "post-commit cleanup error must explain actual state and leftover path");
                }
            }
            Require(!Directory.Exists(lateLockPayload) && Directory.Exists(remaining),
                "failed committed purge must leave recoverable quarantine rather than a misleading installed payload");
            committedRemoval.Purge();
            Require(!Directory.Exists(remaining), "remaining managed quarantine can be cleaned once the lock closes");

            var removal = new ManagedPayloadRemoval();
            await Reject<InvalidDataException>(() => { removal.Add(apps, source, null!); return Task.CompletedTask; });
            await Reject<InvalidDataException>(() => { removal.Add(root, source, Path.Combine(source, "app.exe")); return Task.CompletedTask; });
            Require(File.Exists(Path.Combine(source, "app.exe")), "foreign/source directories must survive");
            Console.WriteLine("Full removal checks passed: 16 isolated safety scenarios.");
        }
        finally
        {
            SafeFiles.EnsureTreeHasNoReparsePoints(root);
            Directory.Delete(root, recursive: true);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    private static async Task Reject<T>(Func<Task> operation) where T : Exception
    {
        try { await operation(); }
        catch (T) { return; }
        throw new InvalidOperationException("Expected refusal: " + typeof(T).Name);
    }
}
