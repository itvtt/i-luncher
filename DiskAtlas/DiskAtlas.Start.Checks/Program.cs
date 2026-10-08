using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Security.Principal;
using DiskAtlas.Startup;

if (args is ["--verify-package", var packageDirectory])
{
    PackageManifest package = await PackageManifest.LoadAsync(packageDirectory);
    await package.VerifyAsync();
    var declared = package.Entries.Select(entry => entry.RelativePath.Replace('\\', '/')).ToHashSet(StringComparer.OrdinalIgnoreCase);
    var actual = Directory.EnumerateFiles(packageDirectory, "*", SearchOption.AllDirectories)
        .Select(path => Path.GetRelativePath(packageDirectory, path).Replace('\\', '/'))
        .Where(path => !path.Equals("package.sha256", StringComparison.OrdinalIgnoreCase)).ToHashSet(StringComparer.OrdinalIgnoreCase);
    if (!declared.SetEquals(actual)) throw new Exception("Published manifest does not cover exact payload files.");
    Console.WriteLine($"PASS published package: {declared.Count} verified files, fingerprint {package.Fingerprint}");
    return;
}

string root = Path.Combine(Path.GetTempPath(), "DiskAtlas-Start-Checks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
int passed = 0;
try
{
    string payload = Path.Combine(root, "source");
    Directory.CreateDirectory(Path.Combine(payload, "ko"));
    await File.WriteAllTextAsync(Path.Combine(payload, "DiskAtlas.exe"), "test executable");
    await File.WriteAllTextAsync(Path.Combine(payload, "ko", "resource.dll"), "resource");
    string manifestText = string.Join("\n", new[] { "DiskAtlas.exe", "ko/resource.dll" }.Select(relative =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(payload, relative)))).ToLowerInvariant() + "  " + relative)) + "\n";
    await File.WriteAllTextAsync(Path.Combine(payload, "package.sha256"), manifestText, new UTF8Encoding(false));
    PackageManifest manifest = await PackageManifest.LoadAsync(payload);
    await manifest.VerifyAsync();
    Check(manifest.Entries.Count == 2 && manifest.Fingerprint.Length == 64, "valid package and fingerprint");
    string copy = Path.Combine(root, "copy");
    await PackageCopier.CopyVerifiedAsync(manifest, copy);
    Check((await PackageManifest.LoadAsync(copy)).Fingerprint == manifest.Fingerprint &&
        File.ReadAllText(Path.Combine(copy, "ko", "resource.dll")) == "resource", "verified nested copy");
    await Reject(() => PackageCopier.CopyVerifiedAsync(manifest, copy), "refuse overwrite existing destination");
    await File.AppendAllTextAsync(Path.Combine(payload, "DiskAtlas.exe"), "tampered");
    await Reject(() => manifest.VerifyAsync(), "reject tampered source");
    await Reject(() => PackageCopier.CopyVerifiedAsync(manifest, Path.Combine(root, "bad-copy")), "reject tampered copy");

    foreach (string path in new[] { "../escape", "a/../../escape", "C:/escape", "/escape", "a:stream", "a//b", "a/./b", "a./b", "a /b", "a\\..\\b" })
        await Reject(() => { PathPolicy.ResolveManifestPath(payload, path); return Task.CompletedTask; }, "reject path " + path);
    Check(!PathPolicy.IsContainedBy(payload + "-outside/file", payload) &&
        PathPolicy.IsContainedBy(Path.Combine(payload, "ko", "resource.dll"), payload), "directory containment boundary");

    string hash = new('a', 64);
    foreach ((string name, string text) in new[]
    {
        ("traversal manifest", hash + "  DiskAtlas.exe\n" + hash + "  ../outside\n"),
        ("duplicate case manifest", hash + "  DiskAtlas.exe\n" + hash + "  DISKATLAS.EXE\n"),
        ("reserved manifest", hash + "  DiskAtlas.exe\n" + hash + "  package.sha256\n"),
        ("missing executable", hash + "  library.dll\n"),
        ("malformed hash", "invalid  DiskAtlas.exe\n"),
    })
    {
        await File.WriteAllTextAsync(Path.Combine(payload, "package.sha256"), text);
        await Reject(async () => { await PackageManifest.LoadAsync(payload); }, name);
    }

    string sid = "S-1-5-21-636594793-3044708515-3187671748-1001";
    string stageTest = Path.Combine(root, "stage-acl");
    Directory.CreateDirectory(stageTest);
    await File.WriteAllTextAsync(Path.Combine(stageTest, "test.txt"), "staged bytes");
    string currentSid = WindowsIdentity.GetCurrent().User!.Value;
    try
    {
        AclService.ProtectStage(stageTest, currentSid);
        Check(File.ReadAllText(Path.Combine(stageTest, "test.txt")) == "staged bytes", "nonelevated stage ACL permits reads");
        await Reject(() => File.AppendAllTextAsync(Path.Combine(stageTest, "test.txt"), "bad"), "sealed stage rejects ordinary writes");
    }
    finally { AclService.RestoreStageAccess(stageTest, currentSid); }
    Check(true, "stage ACL restored for cleanup");

    string preparedStage;
    using (SetupStage stage = await SetupStage.CreateInstallStageAsync(await PackageManifest.LoadAsync(copy), currentSid))
    {
        preparedStage = stage.RootPath;
        Check(File.Exists(stage.BootstrapPath) && File.Exists(Path.Combine(stage.RootPath, "app", "DiskAtlas.exe")), "local setup stage includes helper and verified payload");
        await Reject(() => File.WriteAllTextAsync(stage.BootstrapPath, "replacement"), "staged helper cannot be replaced while waiting for UAC");
        await Reject(() => { Directory.Move(stage.RootPath, stage.RootPath + "-moved"); return Task.CompletedTask; }, "staged directory cannot be moved while waiting for UAC");
    }
    Check(!Directory.Exists(preparedStage), "setup stage cleanup after helper lifetime");

    string taskSddl = $"O:BAG:SYD:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;GRGX;;;{sid})";
    TaskSecurityValidator.ValidateTask(taskSddl, sid);
    Check(true, "task SDDL allows user read and run only");
    foreach (string invalid in new[]
    {
        taskSddl.Replace("O:BA", "O:" + sid),
        taskSddl.Replace("D:P", "D:"),
        taskSddl.Replace("GRGX", "GA"),
        taskSddl.Replace("GRGX", "GRGWGX"),
        taskSddl + "(A;;GA;;;WD)",
    })
        await Reject(() => { TaskSecurityValidator.ValidateTask(invalid, sid); return Task.CompletedTask; }, "reject writable or altered task ACL");
    var paths = new InstallationPaths(sid);
    string executable = paths.GetAppExecutable(hash);
    Check(PathPolicy.IsContainedBy(executable, paths.UserRoot) && paths.TaskPath.EndsWith(sid), "per-account protected paths");
    await Reject(() => { paths.GetAppExecutable("../../bad"); return Task.CompletedTask; }, "invalid version rejected");
    var valid = new TaskDefinitionSnapshot(sid, 3, 1, executable, "", Path.GetDirectoryName(executable)!, true, true, false, false, 2, "PT0S", 0, 1);
    TaskDefinitionValidator.Validate(valid, sid, executable);
    Check(true, "valid fixed demand-start task");
    foreach (TaskDefinitionSnapshot invalid in new[]
    {
        valid with { ExecutablePath = Path.Combine(root, "user-writable.exe") },
        valid with { Arguments = "$(Arg0)" },
        valid with { UserId = "S-1-5-18" },
        valid with { RunLevel = 0 },
        valid with { LogonType = 5 },
        valid with { WorkingDirectory = root },
        valid with { TriggerCount = 1 },
        valid with { ActionCount = 2 },
        valid with { DisallowStartIfOnBatteries = true },
        valid with { ExecutionTimeLimit = "PT72H" },
    })
        await Reject(() => { TaskDefinitionValidator.Validate(invalid, sid, executable); return Task.CompletedTask; }, "reject altered task definition");
    Console.WriteLine($"PASS: {passed} startup checks; no privileged state changed.");
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception);
    throw;
}
finally
{
    string full = Path.GetFullPath(root);
    if (PathPolicy.IsContainedBy(full, Path.GetTempPath()) && Path.GetFileName(full).StartsWith("DiskAtlas-Start-Checks-", StringComparison.Ordinal))
    {
        PathPolicy.RejectExistingReparsePoints(full);
        try { Directory.Delete(full, true); }
        catch (UnauthorizedAccessException exception) { Console.Error.WriteLine("Fixture cleanup: " + exception.Message); }
    }
}

void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAIL: " + name);
    passed++;
    Console.WriteLine("PASS: " + name);
}

async Task Reject(Func<Task> action, string name)
{
    try { await action(); }
    catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or InvalidOperationException or UnauthorizedAccessException)
    {
        Check(true, name);
        return;
    }
    throw new Exception("FAIL: did not reject " + name);
}
