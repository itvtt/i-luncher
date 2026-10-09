using System.Runtime.InteropServices;
using System.Reflection;
using System.IO;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DiskAtlas.App;
using DiskAtlas.Core.Optimization;

namespace DiskAtlas.OptimizationChecks;

internal static class Program
{
    private static readonly DateTime Now = new(2026, 10, 8, 12, 0, 0, DateTimeKind.Utc);
    private static int _passed;
    private static int _failed;
    private static int _skipped;

    [STAThread]
    private static int Main()
    {
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        Exception? fatal = null;
        application.Dispatcher.BeginInvoke(new Action(async () =>
        {
            try { await RunChecks(); }
            catch (Exception error) { fatal = error; }
            finally { application.Shutdown(); }
        }), DispatcherPriority.Normal);
        application.Run();
        if (fatal is not null)
        {
            Console.Error.WriteLine(fatal);
            _failed++;
        }
        return _failed == 0 ? 0 : 1;
    }

    private static async Task RunChecks()
    {
        Console.WriteLine("DiskAtlas storage optimization safety checks");
        Console.WriteLine($"Fixture parent: {Path.Combine(Path.GetTempPath(), "DiskAtlasOptimizationChecks")}");
        Console.WriteLine($"UI image directory: {UiArtifactDirectory()}");
        await Check("preview is non-mutating and includes any direct temporary file", PreviewOnlyAndEligibility);
        await Check("legacy OptimizeAsync remains preview-only", LegacyOptimizeIsPreviewOnly);
        await Check("recent timestamps do not restrict direct temporary files", RecentTimestampDoesNotDisqualify);
        await Check("temporary subfolders are included by default", DefaultIncludesNestedFiles);
        await Check("temporary subfolders can be excluded explicitly", NamesExtensionsAndSubdirectories);
        await Check("selected nested deletion preserves folders and other files", SelectedNestedDeletion);
        await Check("nested system files and reparse directories stay excluded", NestedSystemAndReparseDirectoriesAreExcluded);
        await Check("system attributes and component extensions are omitted from every report", SystemFilesAreOmitted);
        await Check("optional dump and WER roots are disabled by default and included only by opt-in", OptionalRoots);
        await Check("subset deletion removes only the selected preview candidate", SelectedSubsetOnly);
        await Check("forged and cross-service previews are rejected", PreviewEvidenceIsPrivate);
        await Check("new files and changed content or identity survive stale previews", StaleFileEvidence);
        await Check("root and ancestor replacement after preview fail closed", StaleDirectoryEvidence);
        await Check("replacing a nested temporary ancestor after preview skips deletion", NestedAncestorReplacementSkipsDeletion);
        await Check("replacement attempts during pinned deletion fail closed", PinnedDeletionRace);
        await Check("locked and read-only files are skipped while hidden files remain eligible", LockedAndProtectedAttributes);
        await Check("hard-linked files are skipped", HardLinkIsSkipped);
        await Check("alternate data streams are not eligible", AlternateDataStreamIsSkipped);
        await Check("reparse-point ancestors are not traversed", ReparseAncestorIsSkipped);
        await Check("failed deletion does not stop later selected files", DeleteFailureContinues);
        await Check("pre-cancellation and mid-delete cancellation report accurate partial progress", CancellationReportsPartialWork);
        await Check("overlapping roots do not count or delete a path twice", OverlappingRootsAreDeduplicated);
        await Check("outside-root, prefix-trap, and traversal selections cannot delete files", OutsideSelectionsAreIgnored);
        await Check("WPF optimization UI stays preview-first and selects eligible rows only", OptimizationUiPreviewFirst);
        Console.WriteLine($"Results: {_passed} passed, {_failed} failed, {_skipped} skipped");
    }

    private static async Task Check(string name, Func<Task> action)
    {
        try
        {
            await action();
            _passed++;
            Console.WriteLine($"PASS {name}");
        }
        catch (SkipCheckException e)
        {
            _skipped++;
            Console.WriteLine($"SKIP {name}: {e.Message}");
        }
        catch (Exception e)
        {
            _failed++;
            Console.Error.WriteLine($"FAIL {name}: {e}");
        }
    }

    private static Task PreviewOnlyAndEligibility()
    {
        var f = new Fixture();
        string old = f.Write(OptimizationCategory.UserTemporary, "old.tmp", "old payload", DaysOld(20));
        string temp = f.Write(OptimizationCategory.UserTemporary, "also-old.TEMP", "second", DaysOld(20));
        string recent = f.Write(OptimizationCategory.UserTemporary, "recent.tmp", "recent", DaysOld(20), write: Now.AddDays(-1));
        string log = f.Write(OptimizationCategory.UserTemporary, "ordinary.log", "log", DaysOld(20));
        string unusual = f.Write(OptimizationCategory.UserTemporary, "unusual.extension", "unusual", DaysOld(20));
        string recovery = f.Write(OptimizationCategory.UserTemporary, "recovery.tmp", "recovery", DaysOld(20));
        string nested = f.Write(OptimizationCategory.UserTemporary, Path.Combine("child", "nested.tmp"), "nested", DaysOld(20));

        OptimizationReport preview = f.Analyze().GetAwaiter().GetResult();
        Assert(preview.IsPreview && preview.DeletedCount == 0, "Analyze must return a preview and never report deletion.");
        Assert(preview.CandidateCount == 7, $"Expected seven temporary candidates including the nested file, got {preview.CandidateCount}.");
        Assert(new[] { old, temp, recent, log, unusual, recovery, nested }.All(path => Contains(preview, path)), "A normal temporary file was excluded based on age, name, extension, or nesting.");
        Assert(preview.Items.Where(IsCandidate).All(item => item.LastWriteTimeUtc.HasValue), "Candidate results must expose their modification timestamp.");
        Assert(new[] { old, recent, log, unusual, recovery, nested }.All(File.Exists), "Preview changed fixture contents.");
        return Task.CompletedTask;
    }

    private static async Task LegacyOptimizeIsPreviewOnly()
    {
        var f = new Fixture();
        string path = f.Write(OptimizationCategory.UserTemporary, "compat.tmp", "payload", DaysOld(20));
        OptimizationReport report = await f.Service.OptimizeAsync(new OptimizationOptions());
        Assert(report.IsPreview && report.CandidateCount == 1 && report.DeletedCount == 0, "Legacy OptimizeAsync must preserve preview-only behavior.");
        Assert(File.Exists(path), "Legacy OptimizeAsync must never delete a candidate.");
    }

    private static async Task RecentTimestampDoesNotDisqualify()
    {
        foreach (string dimension in new[] { "creation", "write", "access" })
        {
            var f = new Fixture();
            var times = new FileTimes(DaysOld(20), DaysOld(20), DaysOld(20));
            times = dimension switch
            {
                "creation" => times with { Creation = Now.AddDays(-1) },
                "write" => times with { Write = Now.AddDays(-1) },
                _ => times with { Access = Now.AddDays(-1) }
            };
            string path = f.Write(OptimizationCategory.UserTemporary, $"recent-{dimension}.tmp", "payload", times.Creation, times.Write, times.Access);
            OptimizationReport preview = await f.Analyze();
            Assert(Contains(preview, path), $"A recent {dimension} timestamp must not exclude an ordinary direct temporary file.");
        }
    }

    private static async Task NamesExtensionsAndSubdirectories()
    {
        var f = new Fixture();
        string allowed = f.Write(OptimizationCategory.UserTemporary, "clean.tmp", "allowed", DaysOld(20));
        string[] newlyEligible = ["~document.tmp", "autosave.tmp", "auto-save.tmp", "recovery.tmp", "recover.tmp", "backup.tmp", "install.tmp", "setup.tmp", "update.tmp", "office.tmp", "data.lock.tmp", "lockfile.tmp", "ordinary.log", "dump.dmp", "report.wer", "archive.zip"];
        string[] eligiblePaths = newlyEligible.Select(name => f.Write(OptimizationCategory.UserTemporary, name, "keep", DaysOld(20))).ToArray();
        string nested = f.Write(OptimizationCategory.UserTemporary, Path.Combine("subfolder", "nested.tmp"), "keep", DaysOld(20));

        OptimizationReport preview = await f.Analyze(new OptimizationOptions { IncludeTemporarySubfolders = false });
        Assert(preview.CandidateCount == newlyEligible.Length + 1 && Contains(preview, allowed), "All ordinary direct files should qualify regardless of name or extension.");
        Assert(eligiblePaths.All(path => Contains(preview, path)), "A recovery, backup, install, lock, or unusual-extension file was excluded.");
        Assert(!Contains(preview, nested), "A nested file must remain outside the scan scope.");
        OptimizationReport deleted = await f.Service.DeleteAsync(preview, preview.Items.Where(IsCandidate).Select(item => item.Path).ToArray());
        Assert(deleted.DeletedCount == newlyEligible.Length + 1 && !File.Exists(allowed) && eligiblePaths.All(path => !File.Exists(path)), "Selected direct files of any name or extension should be deletable.");
        Assert(File.Exists(nested), "The nested file must remain untouched.");
    }

    private static async Task DefaultIncludesNestedFiles()
    {
        var f = new Fixture();
        string direct = f.Write(OptimizationCategory.UserTemporary, "ordinary.log", "direct", DaysOld(20));
        string nested = f.Write(OptimizationCategory.UserTemporary, Path.Combine("child", "nested.log"), "nested", DaysOld(20));
        OptimizationReport preview = await f.Analyze();
        Assert(Contains(preview, direct) && Contains(preview, nested), "Default options must include normal files in the temp root and its subfolders.");
        Assert(File.Exists(direct) && File.Exists(nested), "Recursive preview must remain non-mutating.");
    }

    private static async Task SelectedNestedDeletion()
    {
        var f = new Fixture();
        string selected = f.Write(OptimizationCategory.UserTemporary, Path.Combine("child", "grandchild", "selected.log"), "selected", DaysOld(20));
        string retainedNested = f.Write(OptimizationCategory.UserTemporary, Path.Combine("child", "retained.log"), "retained", DaysOld(20));
        string retainedDirect = f.Write(OptimizationCategory.UserTemporary, "retained-direct.log", "direct", DaysOld(20));
        string outside = WriteAged(Path.Combine(Path.GetDirectoryName(f.Root(OptimizationCategory.UserTemporary))!, "outside.log"), "outside");
        string childDirectory = Path.Combine(f.Root(OptimizationCategory.UserTemporary), "child");
        string grandchildDirectory = Path.GetDirectoryName(selected)!;

        OptimizationReport preview = await f.Analyze();
        Assert(preview.CandidateCount == 3 && Contains(preview, selected), "Recursive preview must include normal nested candidates while staying inside the temp root.");
        OptimizationReport deleted = await f.Service.DeleteAsync(preview, [selected, outside]);
        Assert(deleted.DeletedCount == 1 && !File.Exists(selected), "Only the explicitly selected nested candidate should be deleted.");
        Assert(File.Exists(retainedNested) && File.Exists(retainedDirect) && File.Exists(outside), "Unselected and out-of-root files must remain untouched.");
        Assert(Directory.Exists(childDirectory) && Directory.Exists(grandchildDirectory), "Candidate deletion must never remove its parent folders.");
    }

    private static async Task NestedSystemAndReparseDirectoriesAreExcluded()
    {
        var f = new Fixture();
        string systemDirectory = Path.Combine(f.Root(OptimizationCategory.UserTemporary), "system-directory");
        Directory.CreateDirectory(systemDirectory);
        File.SetAttributes(systemDirectory, File.GetAttributes(systemDirectory) | FileAttributes.System);
        string systemDirectoryFile = WriteAged(Path.Combine(systemDirectory, "ordinary.log"), "system directory child");
        string nestedSystemFile = f.Write(OptimizationCategory.UserTemporary, Path.Combine("nested", "system.bin"), "system file", DaysOld(20));
        File.SetAttributes(nestedSystemFile, File.GetAttributes(nestedSystemFile) | FileAttributes.System);
        string component = f.Write(OptimizationCategory.UserTemporary, Path.Combine("nested", "component.dll"), "component", DaysOld(20));
        string hidden = f.Write(OptimizationCategory.UserTemporary, Path.Combine("nested", "hidden.log"), "hidden normal file", DaysOld(20));
        File.SetAttributes(hidden, File.GetAttributes(hidden) | FileAttributes.Hidden);

        string outsideDirectory = Path.Combine(Path.GetDirectoryName(f.Root(OptimizationCategory.UserTemporary))!, "reparse-target");
        Directory.CreateDirectory(outsideDirectory);
        string outsideFile = WriteAged(Path.Combine(outsideDirectory, "outside.log"), "outside target");
        string reparseDirectory = Path.Combine(f.Root(OptimizationCategory.UserTemporary), "reparse-directory");
        CreateDirectoryLinkOrJunction(reparseDirectory, outsideDirectory);

        OptimizationReport preview = await f.Analyze();
        Assert(!HasAnyPath(preview, systemDirectoryFile) && !HasAnyPath(preview, nestedSystemFile) && !HasAnyPath(preview, component),
            "Files under a System directory, System files, and component extensions must be omitted from recursive scans.");
        Assert(Contains(preview, hidden), "A Hidden-only ordinary file remains selectable.");
        Assert(!HasAnyPath(preview, outsideFile), "Recursive scanning must not follow a reparse-point directory outside the temp root.");
        Assert(File.Exists(systemDirectoryFile) && File.Exists(nestedSystemFile) && File.Exists(component) && File.Exists(hidden) && File.Exists(outsideFile),
            "Recursive preview must preserve all fixtures.");
    }

    private static void CreateDirectoryLinkOrJunction(string link, string target)
    {
        try { Directory.CreateSymbolicLink(link, target); }
        catch (Exception symbolicError) when (symbolicError is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            try
            {
                using var process = Process.Start(new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    ArgumentList = { "/c", "mklink", "/J", link, target }
                }) ?? throw new InvalidOperationException("Could not start cmd.exe.");
                process.WaitForExit();
                if (process.ExitCode != 0 || !Directory.Exists(link)) throw new IOException($"mklink /J exited with {process.ExitCode}.");
            }
            catch (Exception junctionError) when (junctionError is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
            { throw new SkipCheckException($"Symbolic link and junction fixtures unavailable: {symbolicError.Message}; {junctionError.Message}"); }
        }
    }

    private static async Task SystemFilesAreOmitted()
    {
        var f = new Fixture();
        string[] componentExtensions = [".sys", ".dll", ".drv", ".ocx", ".cpl", ".efi"];
        string[] componentFiles = componentExtensions.Select(ext => f.Write(OptimizationCategory.UserTemporary, "component" + ext, "system component", DaysOld(20))).ToArray();
        string system = f.Write(OptimizationCategory.UserTemporary, "system-with-any-extension.bin", "system", DaysOld(20));
        File.SetAttributes(system, File.GetAttributes(system) | FileAttributes.System);
        string combined = f.Write(OptimizationCategory.UserTemporary, "combined.bin", "combined", DaysOld(20));
        File.SetAttributes(combined, File.GetAttributes(combined) | FileAttributes.System | FileAttributes.ReadOnly);
        string hiddenSystem = f.Write(OptimizationCategory.UserTemporary, "hidden-system.bin", "hidden system", DaysOld(20));
        File.SetAttributes(hiddenSystem, File.GetAttributes(hiddenSystem) | FileAttributes.Hidden | FileAttributes.System);
        string temporarySystem = f.Write(OptimizationCategory.UserTemporary, "temporary-system.log", "temporary system", DaysOld(20));
        File.SetAttributes(temporarySystem, File.GetAttributes(temporarySystem) | FileAttributes.Temporary | FileAttributes.System);
        string benignFlags = f.Write(OptimizationCategory.UserTemporary, "temporary-flags.log", "benign attributes", DaysOld(20));
        File.SetAttributes(benignFlags, File.GetAttributes(benignFlags) | FileAttributes.Temporary | FileAttributes.NotContentIndexed);

        OptimizationReport preview = await f.Analyze();
        string[] systemPaths = [.. componentFiles, system, combined, hiddenSystem, temporarySystem];
        Assert(systemPaths.All(path => !HasAnyPath(preview, path)), "System attributes and component extensions must be absent from the preview report.");
        Assert(systemPaths.All(File.Exists), "Preview must preserve system files.");
        Assert((File.GetAttributes(benignFlags) & (FileAttributes.Temporary | FileAttributes.NotContentIndexed)) ==
               (FileAttributes.Temporary | FileAttributes.NotContentIndexed), "The fixture must retain both benign attributes for this check.");
        Assert(Contains(preview, benignFlags), "Temporary and NotContentIndexed attributes must not disqualify an ordinary temporary file.");
        OptimizationReport systemDelete = await f.Service.DeleteAsync(preview, systemPaths);
        Assert(systemPaths.All(path => !HasAnyPath(systemDelete, path)) && systemDelete.DeletedCount == 0,
            "System attributes and component extensions must also be absent from deletion reports.");
        Assert(systemPaths.All(File.Exists), "Attempting to delete omitted system files must preserve them.");
        Assert((File.GetAttributes(system) & FileAttributes.System) != 0 &&
               (File.GetAttributes(combined) & (FileAttributes.System | FileAttributes.ReadOnly)) == (FileAttributes.System | FileAttributes.ReadOnly) &&
               (File.GetAttributes(hiddenSystem) & (FileAttributes.System | FileAttributes.Hidden)) == (FileAttributes.System | FileAttributes.Hidden) &&
               (File.GetAttributes(temporarySystem) & (FileAttributes.System | FileAttributes.Temporary)) == (FileAttributes.System | FileAttributes.Temporary),
            "Analysis and deletion must preserve system attribute combinations.");
        OptimizationReport benignDelete = await f.Service.DeleteAsync(preview, [benignFlags]);
        Assert(benignDelete.DeletedCount == 1 && !File.Exists(benignFlags),
            "A temporary file with Temporary and NotContentIndexed attributes must remain selectable and deletable.");

        // Simulate the file becoming a system file after the initial enumeration check and before native inspection.
        var renameRace = new Fixture();
        string race = renameRace.Write(OptimizationCategory.UserTemporary, "race.bin", "race", DaysOld(20));
        bool marked = false;
        var raceService = new StorageOptimizationService(renameRace.Roots, () => Now, beforeInspect: path =>
        {
            if (SamePath(path, race) && !marked)
            {
                marked = true;
                File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.System);
            }
        });
        OptimizationReport raceReport = await raceService.AnalyzeAsync(new OptimizationOptions());
        Assert(marked && !HasAnyPath(raceReport, race) && File.Exists(race), "A file marked System before native inspection must be omitted and preserved.");
        OptimizationReport raceDelete = await raceService.DeleteAsync(raceReport, [race]);
        Assert(!HasAnyPath(raceDelete, race) && File.Exists(race), "A system file omitted during inspection must remain absent from the delete report and on disk.");

        var afterPreview = new Fixture();
        string lateSystem = afterPreview.Write(OptimizationCategory.UserTemporary, "late-system.bin", "late system", DaysOld(20));
        string[] lateComponents = componentExtensions.Select(ext => afterPreview.Write(OptimizationCategory.UserTemporary, "late-component" + ext, "late component", DaysOld(20))).ToArray();
        OptimizationReport cleanPreview = await afterPreview.Analyze();
        Assert(Contains(cleanPreview, lateSystem) && lateComponents.All(path => !HasAnyPath(cleanPreview, path)), "Only the ordinary file should initially be eligible; system component extensions must be omitted.");
        File.SetAttributes(lateSystem, File.GetAttributes(lateSystem) | FileAttributes.System);
        OptimizationReport deleteResult = await afterPreview.Service.DeleteAsync(cleanPreview, [lateSystem, .. lateComponents]);
        Assert(deleteResult.DeletedCount == 0 && File.Exists(lateSystem), "A file that becomes System after preview must be skipped during deletion.");
        Assert(!HasAnyPath(deleteResult, lateSystem) && lateComponents.All(path => !HasAnyPath(deleteResult, path)), "System files must be omitted from deletion reports.");
        Assert(lateComponents.All(File.Exists), "System component files must remain on disk after deletion is attempted.");
    }

    private static async Task OptionalRoots()
    {
        var f = new Fixture(new Dictionary<OptimizationCategory, string[]>
        {
            [OptimizationCategory.UserTemporary] = ["temp"],
            [OptimizationCategory.CrashDumps] = ["dumps"],
            [OptimizationCategory.ErrorReports] = ["wer"]
        });
        string temp = f.Write(OptimizationCategory.UserTemporary, "old.tmp", "t", DaysOld(40));
        string dump = f.Write(OptimizationCategory.CrashDumps, "old.dmp", "d", DaysOld(40));
        string wer = f.Write(OptimizationCategory.ErrorReports, Path.Combine("Report", "old.wer"), "w", DaysOld(40));

        OptimizationReport defaults = await f.Analyze(new OptimizationOptions());
        Assert(Contains(defaults, temp) && !Contains(defaults, dump) && !Contains(defaults, wer), "Optional roots were scanned without explicit opt-in.");
        OptimizationReport enabled = await f.Analyze(new OptimizationOptions { IncludeCrashDumps = true, IncludeErrorReports = true });
        Assert(Contains(enabled, temp) && Contains(enabled, dump) && Contains(enabled, wer), "Opt-in dump/WER candidates were not included.");
        Assert(File.Exists(temp) && File.Exists(dump) && File.Exists(wer), "Analysis of optional roots must remain preview-only.");
    }

    private static async Task SelectedSubsetOnly()
    {
        var f = new Fixture();
        string first = f.Write(OptimizationCategory.UserTemporary, "first.tmp", "first", DaysOld(20));
        string second = f.Write(OptimizationCategory.UserTemporary, "second.tmp", "second", DaysOld(20));
        OptimizationReport preview = await f.Analyze();
        string selected = Contains(preview, first) ? first : second;
        string retained = selected == first ? second : first;

        OptimizationReport result = await f.Service.DeleteAsync(preview, [selected]);
        Assert(result.DeletedCount == 1 && result.CandidateCount == 1, "Deletion report must cover the selected item only.");
        Assert(!File.Exists(selected) && File.Exists(retained), "DeleteAsync must honor the selected subset exactly.");
    }

    private static async Task PreviewEvidenceIsPrivate()
    {
        var f = new Fixture();
        string path = f.Write(OptimizationCategory.UserTemporary, "candidate.tmp", "payload", DaysOld(20));
        OptimizationReport preview = await f.Analyze();
        var forged = new OptimizationReport
        {
            Items = preview.Items, IsPreview = preview.IsPreview, CandidateCount = preview.CandidateCount,
            CandidateBytes = preview.CandidateBytes, SkippedCount = preview.SkippedCount, FailedCount = preview.FailedCount
        };
        await AssertThrows<ArgumentException>(() => f.Service.DeleteAsync(forged, [path]), "A copied/forged report must not carry service-private evidence.");
        var otherService = new Fixture(f.Roots.ToDictionary(pair => pair.Key, pair => pair.Value));
        await AssertThrows<ArgumentException>(() => otherService.Service.DeleteAsync(preview, [path]), "A preview from another service instance must be rejected.");
        Assert(File.Exists(path), "Rejected previews must not mutate their candidate.");
    }

    private static async Task StaleFileEvidence()
    {
        // New path after preview is not in the private candidate set.
        var f = new Fixture();
        string old = f.Write(OptimizationCategory.UserTemporary, "old.tmp", "original", DaysOld(20));
        OptimizationReport preview = await f.Analyze();
        string late = f.Write(OptimizationCategory.UserTemporary, "late.tmp", "late", DaysOld(20));
        OptimizationReport lateResult = await f.Service.DeleteAsync(preview, [old, late]);
        Assert(!File.Exists(old) && File.Exists(late) && lateResult.DeletedCount == 1, "A file created after preview must not be deleted.");

        // Content/mtime change must invalidate the captured snapshot.
        var changed = new Fixture();
        string changedPath = changed.Write(OptimizationCategory.UserTemporary, "changed.tmp", "before", DaysOld(20));
        OptimizationReport changedPreview = await changed.Analyze();
        File.WriteAllText(changedPath, "after content changed");
        File.SetLastWriteTimeUtc(changedPath, DaysOld(20));
        File.SetLastAccessTimeUtc(changedPath, DaysOld(20));
        OptimizationReport changedResult = await changed.Service.DeleteAsync(changedPreview, [changedPath]);
        Assert(changedResult.DeletedCount == 0 && File.Exists(changedPath), "A candidate whose content/identity evidence changed must survive.");

        // Same name with a replacement file must not inherit preview authority.
        var replaced = new Fixture();
        string replacedPath = replaced.Write(OptimizationCategory.UserTemporary, "replacement.tmp", "original", DaysOld(20));
        OptimizationReport replacedPreview = await replaced.Analyze();
        string displaced = replacedPath + ".displaced";
        File.Move(replacedPath, displaced);
        File.WriteAllText(replacedPath, "new object");
        File.SetCreationTimeUtc(replacedPath, DaysOld(20));
        File.SetLastWriteTimeUtc(replacedPath, DaysOld(20));
        File.SetLastAccessTimeUtc(replacedPath, DaysOld(20));
        OptimizationReport replacedResult = await replaced.Service.DeleteAsync(replacedPreview, [replacedPath]);
        Assert(replacedResult.DeletedCount == 0 && File.Exists(replacedPath) && File.Exists(displaced), "A new file object at a preview path must survive.");

        // Replaying the same report cannot authorize a newly created object at its old path.
        var replay = new Fixture();
        string replayPath = replay.Write(OptimizationCategory.UserTemporary, "replay.tmp", "original", DaysOld(20));
        OptimizationReport replayPreview = await replay.Analyze();
        OptimizationReport firstDelete = await replay.Service.DeleteAsync(replayPreview, [replayPath]);
        Assert(firstDelete.DeletedCount == 1, "Replay fixture's original candidate should be deleted once.");
        WriteAged(replayPath, "new object at replayed path");
        OptimizationReport secondDelete = await replay.Service.DeleteAsync(replayPreview, [replayPath]);
        Assert(secondDelete.DeletedCount == 0 && File.Exists(replayPath), "Reusing preview evidence must not delete a replacement file object.");
    }

    private static async Task StaleDirectoryEvidence()
    {
        var rootChanged = new Fixture();
        string rootCandidate = rootChanged.Write(OptimizationCategory.UserTemporary, "root.tmp", "old", DaysOld(20));
        OptimizationReport rootPreview = await rootChanged.Analyze();
        string root = rootChanged.Root(OptimizationCategory.UserTemporary);
        string movedRoot = root + ".moved";
        Directory.Move(root, movedRoot);
        Directory.CreateDirectory(root);
        string replacement = Path.Combine(root, "root.tmp");
        WriteAged(replacement, "replacement");
        OptimizationReport rootResult = await rootChanged.Service.DeleteAsync(rootPreview, [rootCandidate]);
        Assert(rootResult.DeletedCount == 0 && File.Exists(replacement) && File.Exists(Path.Combine(movedRoot, "root.tmp")), "A replaced root must invalidate preview evidence.");

        var ancestorChanged = new Fixture(new Dictionary<OptimizationCategory, string[]> { [OptimizationCategory.ErrorReports] = ["wer"] });
        string nestedCandidate = ancestorChanged.Write(OptimizationCategory.ErrorReports, Path.Combine("Report", "old.wer"), "old", DaysOld(40));
        OptimizationReport nestedPreview = await ancestorChanged.Analyze(new OptimizationOptions { IncludeErrorReports = true });
        string nestedDir = Path.GetDirectoryName(nestedCandidate)!;
        string movedDir = nestedDir + ".moved";
        Directory.Move(nestedDir, movedDir);
        Directory.CreateDirectory(nestedDir);
        string nestedReplacement = Path.Combine(nestedDir, Path.GetFileName(nestedCandidate));
        WriteAged(nestedReplacement, "replacement");
        OptimizationReport nestedResult = await ancestorChanged.Service.DeleteAsync(nestedPreview, [nestedCandidate]);
        Assert(nestedResult.DeletedCount == 0 && File.Exists(nestedReplacement) && File.Exists(Path.Combine(movedDir, Path.GetFileName(nestedCandidate))), "A replaced ancestor directory must invalidate preview evidence.");
    }

    private static async Task NestedAncestorReplacementSkipsDeletion()
    {
        var f = new Fixture();
        string candidate = f.Write(OptimizationCategory.UserTemporary, Path.Combine("child", "nested.tmp"), "original", DaysOld(20));
        OptimizationReport preview = await f.Analyze();
        Assert(Contains(preview, candidate), "The default recursive preview must include the nested candidate.");

        string ancestor = Path.GetDirectoryName(candidate)!;
        string movedAncestor = ancestor + ".moved";
        Directory.Move(ancestor, movedAncestor);
        Directory.CreateDirectory(ancestor);
        string replacement = Path.Combine(ancestor, Path.GetFileName(candidate));
        WriteAged(replacement, "replacement");

        OptimizationReport result = await f.Service.DeleteAsync(preview, [candidate]);
        Assert(result.DeletedCount == 0 && File.Exists(replacement) && File.Exists(Path.Combine(movedAncestor, Path.GetFileName(candidate))),
            "Ancestor replacement detection must preserve both the new file and the original candidate.");
    }

    private static async Task PinnedDeletionRace()
    {
        string root = NewRoot();
        string path = WriteAged(Path.Combine(root, "race.tmp"), "original payload");
        string moved = path + ".moved";
        var roots = new Dictionary<OptimizationCategory, string[]> { [OptimizationCategory.UserTemporary] = [root] };
        var service = new StorageOptimizationService(roots, () => Now, beforeDelete: candidatePath =>
        {
            // DeleteAsync keeps a no-share file handle open here. The normal rename
            // API must fail while pinned; propagate that failure to exercise fail-closed handling.
            File.Move(candidatePath, moved);
        });
        OptimizationReport preview = await service.AnalyzeAsync(new OptimizationOptions());
        OptimizationReport result = await service.DeleteAsync(preview, [path]);
        Assert(result.DeletedCount == 0 && File.Exists(path) && !File.Exists(moved), "A rename attempt while the candidate is pinned must not remove or replace it.");
        Assert(result.SkippedCount == 1, "The pinned replacement race must be visible as a skipped item.");
    }

    private static async Task LockedAndProtectedAttributes()
    {
        var f = new Fixture();
        string locked = f.Write(OptimizationCategory.UserTemporary, "locked.tmp", "locked", DaysOld(20));
        string readOnly = f.Write(OptimizationCategory.UserTemporary, "readonly.tmp", "readonly", DaysOld(20));
        string hidden = f.Write(OptimizationCategory.UserTemporary, "hidden.tmp", "hidden", DaysOld(20));
        string system = f.Write(OptimizationCategory.UserTemporary, "system.tmp", "system", DaysOld(20));
        File.SetAttributes(readOnly, File.GetAttributes(readOnly) | FileAttributes.ReadOnly);
        File.SetAttributes(hidden, File.GetAttributes(hidden) | FileAttributes.Hidden);
        File.SetAttributes(system, File.GetAttributes(system) | FileAttributes.System);
        using var held = new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        OptimizationReport preview = await f.Analyze();
        string diagnostic = string.Join(", ", new[] { locked, readOnly, hidden, system }.Select(path =>
        {
            OptimizationItemResult? item = preview.Items.FirstOrDefault(entry => SamePath(entry.Path, path));
            return $"{Path.GetFileName(path)}={item?.Status.ToString() ?? "omitted"}/{File.GetAttributes(path)}";
        }));
        Assert(!Contains(preview, locked) && !Contains(preview, readOnly) && Contains(preview, hidden) && !HasAnyPath(preview, system),
            $"Locked/read-only files must be skipped, Hidden alone remains eligible, and System files are omitted. Report: {diagnostic}");
        Assert(File.Exists(locked) && File.Exists(readOnly) && File.Exists(hidden) && File.Exists(system), "Analysis changed fixture files.");
        OptimizationReport hiddenDelete = await f.Service.DeleteAsync(preview, [hidden]);
        Assert(hiddenDelete.DeletedCount == 1 && !File.Exists(hidden), "A Hidden-only file must remain manually deletable.");
        Assert(File.Exists(locked) && File.Exists(readOnly) && File.Exists(system), "Locked, read-only, and system files must remain untouched.");
    }

    private static async Task HardLinkIsSkipped()
    {
        var f = new Fixture();
        string path = f.Write(OptimizationCategory.UserTemporary, "linked.tmp", "linked payload", DaysOld(20));
        string second = path + ".link";
        if (!CreateHardLink(second, path, IntPtr.Zero))
            throw new SkipCheckException($"CreateHardLinkW unavailable (Win32 {Marshal.GetLastWin32Error()}).");
        OptimizationReport preview = await f.Analyze();
        Assert(!Contains(preview, path), "A file with more than one hard link must be excluded.");
        Assert(File.Exists(path) && File.Exists(second), "Hard-link analysis changed either link.");
    }

    private static async Task AlternateDataStreamIsSkipped()
    {
        var f = new Fixture();
        string path = f.Write(OptimizationCategory.UserTemporary, "with-stream.tmp", "base payload", DaysOld(20));
        try
        {
            File.WriteAllText(path + ":userdata", "named stream content");
            SetOldTimes(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        { throw new SkipCheckException($"Fixture volume does not support NTFS alternate streams: {e.Message}"); }
        OptimizationReport preview = await f.Analyze();
        Assert(!Contains(preview, path), "A file with an alternate data stream must be excluded from deletion.");
        Assert(File.Exists(path + ":userdata"), "Preview must preserve the alternate stream.");

        var afterPreview = new Fixture();
        string second = afterPreview.Write(OptimizationCategory.UserTemporary, "late-stream.tmp", "base payload", DaysOld(20));
        OptimizationReport secondPreview = await afterPreview.Analyze();
        try
        {
            File.WriteAllText(second + ":userdata", "late named stream");
            SetOldTimes(second);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        { throw new SkipCheckException($"Fixture volume does not support NTFS alternate streams: {e.Message}"); }
        OptimizationReport deletion = await afterPreview.Service.DeleteAsync(secondPreview, [second]);
        Assert(deletion.DeletedCount == 0 && File.Exists(second) && File.Exists(second + ":userdata"),
            "Adding an alternate stream after preview must invalidate deletion authority even when timestamps are restored.");
    }

    private static async Task ReparseAncestorIsSkipped()
    {
        var f = new Fixture(new Dictionary<OptimizationCategory, string[]> { [OptimizationCategory.ErrorReports] = ["wer"] });
        string targetDir = Path.Combine(Path.GetDirectoryName(f.Root(OptimizationCategory.ErrorReports))!, "outside-target");
        Directory.CreateDirectory(targetDir);
        string target = Path.Combine(targetDir, "outside.wer");
        WriteAged(target, "outside payload");
        string link = Path.Combine(f.Root(OptimizationCategory.ErrorReports), "ReportLink");
        try { Directory.CreateSymbolicLink(link, targetDir); }
        catch (Exception symbolicError) when (symbolicError is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            try
            {
                using var process = Process.Start(new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    ArgumentList = { "/c", "mklink", "/J", link, targetDir }
                }) ?? throw new InvalidOperationException("Could not start cmd.exe.");
                process.WaitForExit();
                if (process.ExitCode != 0 || !Directory.Exists(link))
                    throw new IOException($"mklink /J exited with {process.ExitCode}.");
            }
            catch (Exception junctionError) when (junctionError is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
            { throw new SkipCheckException($"Symbolic link and junction fixtures unavailable: {symbolicError.Message}; {junctionError.Message}"); }
        }
        OptimizationReport preview = await f.Analyze(new OptimizationOptions { IncludeErrorReports = true });
        Assert(!Contains(preview, target), "A file reached through a reparse-point ancestor must not be eligible.");
        Assert(File.Exists(target), "Reparse-point analysis changed the outside target.");
    }

    private static async Task DeleteFailureContinues()
    {
        string root = NewRoot();
        string bad = WriteAged(Path.Combine(root, "faulty.tmp"), "faulty");
        string good = WriteAged(Path.Combine(root, "good.tmp"), "good");
        var roots = new Dictionary<OptimizationCategory, string[]> { [OptimizationCategory.UserTemporary] = [root] };
        var service = new StorageOptimizationService(roots, () => Now, beforeDelete: path =>
        {
            if (Path.GetFileName(path).Equals("faulty.tmp", StringComparison.OrdinalIgnoreCase))
                throw new IOException("fixture injected failure");
        });
        OptimizationReport preview = await service.AnalyzeAsync(new OptimizationOptions());
        OptimizationReport result = await service.DeleteAsync(preview, preview.Items.Where(IsCandidate).Select(item => item.Path).ToArray());
        Assert(result.DeletedCount == 1 && File.Exists(bad) && !File.Exists(good), "A deletion failure should retain that file and allow the other selected file to finish.");
        Assert(result.SkippedCount == 1, "The failed safe-delete attempt should be reported as skipped.");
    }

    private static async Task CancellationReportsPartialWork()
    {
        var f = new Fixture();
        string[] candidates = Enumerable.Range(0, 4).Select(i => f.Write(OptimizationCategory.UserTemporary, $"{i}.tmp", "payload", DaysOld(20))).ToArray();
        using var alreadyCanceled = new CancellationTokenSource();
        alreadyCanceled.Cancel();
        OptimizationReport pre = await f.Service.AnalyzeAsync(new OptimizationOptions(), cancellationToken: alreadyCanceled.Token);
        Assert(pre.IsCanceled && pre.CandidateCount == 0 && candidates.All(File.Exists), "Pre-canceled analysis must report no work and preserve all candidates.");

        OptimizationReport preview = await f.Analyze();
        using var cancelAfterFirst = new CancellationTokenSource();
        var progress = new InlineProgress<OptimizationProgress>(value =>
        {
            if (value.DeletedFiles == 1) cancelAfterFirst.Cancel();
        });
        OptimizationReport partial = await f.Service.DeleteAsync(preview, candidates, progress, cancelAfterFirst.Token);
        Assert(partial.IsCanceled, "Cancellation raised after the first deletion must be reflected in the report.");
        Assert(partial.DeletedCount == 1 && partial.CandidateCount == 1 && partial.Items.Count == 1, "Partial cancellation counters must describe exactly the processed item.");
        Assert(partial.DeletedBytes == "payload".Length && partial.CandidateBytes == "payload".Length, "Partial byte totals must match the single processed fixture file.");
        Assert(candidates.Count(File.Exists) == 3, "Mid-delete cancellation must retain every unprocessed candidate.");
    }

    private static async Task OverlappingRootsAreDeduplicated()
    {
        var f = new Fixture(new Dictionary<OptimizationCategory, string[]>
        {
            [OptimizationCategory.UserTemporary] = ["shared"],
            [OptimizationCategory.WindowsTemporary] = ["shared"]
        });
        string path = f.Write(OptimizationCategory.UserTemporary, "one.tmp", "payload", DaysOld(20));
        OptimizationReport preview = await f.Analyze();
        Assert(preview.CandidateCount == 1 && preview.Items.Count(item => item.Status == OptimizationItemStatus.Candidate && SamePath(item.Path, path)) == 1,
            "The same physical path under overlapping roots must be counted once.");
        OptimizationReport deleted = await f.Service.DeleteAsync(preview, [path]);
        Assert(deleted.DeletedCount == 1 && File.Exists(path) == false, "An overlapping root must not cause duplicate deletion.");
    }

    private static async Task OutsideSelectionsAreIgnored()
    {
        var f = new Fixture();
        string candidate = f.Write(OptimizationCategory.UserTemporary, "inside.tmp", "inside", DaysOld(20));
        string sibling = f.Root(OptimizationCategory.UserTemporary) + "-escape";
        Directory.CreateDirectory(sibling);
        string outside = WriteAged(Path.Combine(sibling, "outside.tmp"), "outside");
        OptimizationReport preview = await f.Analyze();
        string traversal = Path.Combine(f.Root(OptimizationCategory.UserTemporary), "..", Path.GetFileName(sibling), "outside.tmp");
        OptimizationReport result = await f.Service.DeleteAsync(preview, [outside, traversal]);
        Assert(result.DeletedCount == 0 && File.Exists(candidate) && File.Exists(outside), "Outside-root, sibling-prefix, and traversal selections must not delete anything.");
    }

    private static async Task OptimizationUiPreviewFirst()
    {
        string fixtureRoot = NewRoot();
        string candidate = WriteAged(Path.Combine(fixtureRoot, "eligible.tmp"), "eligible payload");
        string backup = WriteAged(Path.Combine(fixtureRoot, "backup.tmp"), "backup payload");
        string protectedPath = WriteAged(Path.Combine(fixtureRoot, "readonly.tmp"), "read-only payload");
        string systemPath = WriteAged(Path.Combine(fixtureRoot, "component.dll"), "system component");
        File.SetAttributes(protectedPath, File.GetAttributes(protectedPath) | FileAttributes.ReadOnly);
        File.SetAttributes(systemPath, File.GetAttributes(systemPath) | FileAttributes.System);
        int inspected = 0;
        var fixtureService = new StorageOptimizationService(
            new Dictionary<OptimizationCategory, string[]> { [OptimizationCategory.UserTemporary] = [fixtureRoot] },
            () => Now, beforeInspect: _ => Interlocked.Increment(ref inspected));
        var window = new MainWindow();
        try
        {
            var serviceProperty = typeof(MainWindow).GetProperty("OptimizationService", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("MainWindow internal optimization service seam was not found.");
            serviceProperty.SetValue(window, fixtureService);
            Assert(window.MinWidth == 1050 && window.MinHeight == 700, "MainWindow minimum size must remain at least 1050×700.");

            var previewButton = Find<Button>(window, "PreviewOptimizationButton");
            var selectAllButton = Find<Button>(window, "SelectAllOptimizationButton");
            var clearButton = Find<Button>(window, "ClearOptimizationSelectionButton");
            var deleteSelectedButton = Find<Button>(window, "DeleteSelectedOptimizationButton");
            var deleteAllButton = Find<Button>(window, "DeleteAllOptimizationButton");
            var resultsGrid = Find<DataGrid>(window, "OptimizationResultsGrid");
            var temporarySubfoldersOption = Find<CheckBox>(window, "IncludeTemporarySubfoldersCheckBox");
            var tabs = Find<TabControl>(window, "MainTabs");
            var optimizationTab = Find<TabItem>(window, "OptimizationTab");
            Assert(temporarySubfoldersOption.IsChecked == true, "Recursive temporary-folder scanning must be enabled by default.");

            window.Show();
            tabs.SelectedItem = optimizationTab;
            await Task.Delay(100);
            Assert(window.OptimizationRows.Count == 0 && Volatile.Read(ref inspected) == 0,
                "Construction and selecting the optimization tab must not invoke the optimizer scan.");
            Assert(previewButton.IsEnabled && !selectAllButton.IsEnabled && !clearButton.IsEnabled && !deleteSelectedButton.IsEnabled && !deleteAllButton.IsEnabled,
                "Before preview, only the explicit preview action should be enabled.");
            Assert(File.Exists(candidate) && File.Exists(backup) && File.Exists(protectedPath) && File.Exists(systemPath), "Opening the optimization tab must not mutate fixture files.");

            previewButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitFor(() => window.OptimizationRows.Count == 2, TimeSpan.FromSeconds(15), "Preview did not populate exactly the eligible candidates.");
            Assert(Volatile.Read(ref inspected) > 0, "Explicit preview should use the injected fixture optimizer.");
            var candidateRow = window.OptimizationRows.Single(row => SamePath(row.Path, candidate));
            var backupRow = window.OptimizationRows.Single(row => SamePath(row.Path, backup));
            Assert(!window.OptimizationRows.Any(row => SamePath(row.Path, protectedPath) || SamePath(row.Path, systemPath)), "Read-only and system files must be absent from the UI list.");
            Assert(resultsGrid.Columns.Count == 6 && resultsGrid.Columns.All(column => !new[] { "Status", "Reason" }.Contains(Convert.ToString(column.Header), StringComparer.OrdinalIgnoreCase)), "The optimization list must retain checkbox, category, name, path, size, and modified columns without Status or Reason.");
            Assert(candidateRow.CanSelect && !candidateRow.IsSelected, "Preview candidates must start eligible but unchecked.");
            Assert(backupRow.CanSelect && !backupRow.IsSelected, "Ordinary backup-named files must start eligible but unchecked.");
            Assert(candidateRow.ModifiedText != "—", "The preview row must display the file modification date.");
            Assert(selectAllButton.IsEnabled && deleteAllButton.IsEnabled && !clearButton.IsEnabled && !deleteSelectedButton.IsEnabled,
                "After preview, bulk actions should reflect the unchecked candidate state.");
            Assert(File.Exists(candidate) && File.Exists(backup) && File.Exists(protectedPath) && File.Exists(systemPath), "Preview must not delete or mutate fixture files.");

            selectAllButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert(candidateRow.IsSelected && backupRow.IsSelected && window.OptimizationRows.Count == 2, "Select All must select eligible candidates only.");
            Assert(clearButton.IsEnabled && deleteSelectedButton.IsEnabled && deleteAllButton.IsEnabled,
                "Selection must enable selected deletion and clearing, while keeping delete-all available.");
            Assert(File.Exists(candidate) && File.Exists(backup) && File.Exists(protectedPath), "Select All must not itself mutate files.");

            var crashOption = Find<CheckBox>(window, "IncludeCrashDumpsCheckBox");
            crashOption.IsChecked = true;
            Assert(window.OptimizationRows.Count == 0, "Changing optional scan scope must invalidate the old preview rows.");
            Assert(!selectAllButton.IsEnabled && !clearButton.IsEnabled && !deleteSelectedButton.IsEnabled && !deleteAllButton.IsEnabled,
                "Changing scan options must disable actions that depend on stale preview evidence.");
            Assert(previewButton.IsEnabled && File.Exists(candidate) && File.Exists(protectedPath), "Option invalidation must preserve files and allow a fresh preview.");

            crashOption.IsChecked = false;
            previewButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitFor(() => window.OptimizationRows.Count == 2, TimeSpan.FromSeconds(15), "Fresh preview did not complete after options changed.");
            Assert(window.OptimizationRows.Single(row => SamePath(row.Path, candidate)).CanSelect,
                "A new preview should restore candidate action state after option invalidation.");

            temporarySubfoldersOption.IsChecked = false;
            Assert(window.OptimizationRows.Count == 0 && !selectAllButton.IsEnabled && !deleteAllButton.IsEnabled,
                "Turning off temporary subfolder scanning must invalidate the existing preview and disable deletion actions.");
            temporarySubfoldersOption.IsChecked = true;
            Assert(window.OptimizationRows.Count == 0 && !selectAllButton.IsEnabled && !deleteAllButton.IsEnabled,
                "Turning temporary subfolder scanning back on must keep stale preview actions disabled.");
            previewButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitFor(() => window.OptimizationRows.Count == 2, TimeSpan.FromSeconds(15), "Fresh preview did not complete after toggling recursive scanning.");

            var applyReport = typeof(MainWindow).GetMethod("ApplyOptimizationReport", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("MainWindow optimization report seam was not found.");
            OptimizationReport sourceReport = await fixtureService.AnalyzeAsync(new OptimizationOptions());
            var deletionReport = sourceReport with { IsPreview = false, CandidateCount = 2, DeletedCount = 2, SkippedCount = 1 };
            applyReport.Invoke(window, [deletionReport, false]);
            Assert(window.OptimizationRows.Count == 0, "Applying a deletion report must leave the candidate list empty.");
            Assert(Find<TextBlock>(window, "OptimizationCandidateCountText").Text.Contains("2") &&
                   Find<TextBlock>(window, "OptimizationSkippedCountText").Text.Contains("1"),
                "Deletion summary counts must remain visible when the list is empty.");
            var canceledPreview = sourceReport with { IsPreview = true, IsCanceled = true };
            applyReport.Invoke(window, [canceledPreview, true]);
            Assert(window.OptimizationRows.Count == 0, "A canceled preview must not expose selectable candidate rows.");

            window.Width = 1280;
            window.Height = 850;
            await window.Dispatcher.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.Render);
            Capture(window, "DiskAtlas-optimization-preview-full.png");
            window.Width = window.MinWidth;
            window.Height = window.MinHeight;
            await window.Dispatcher.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.Render);
            Rect optionBounds = temporarySubfoldersOption.TransformToAncestor(window).TransformBounds(new Rect(new Point(0, 0), temporarySubfoldersOption.RenderSize));
            Assert(temporarySubfoldersOption.IsVisible && optionBounds.Left >= 0 && optionBounds.Right <= window.ActualWidth,
                "The recursive-scan checkbox must remain visible inside the minimum-width window.");
            Capture(window, "DiskAtlas-optimization-preview-minimum.png");
        }
        finally
        {
            if (window.IsVisible) window.Close();
        }
        Assert(File.Exists(candidate) && File.Exists(backup) && File.Exists(protectedPath) && File.Exists(systemPath), "UI smoke checks must never call a deletion action.");
    }

    private static T Find<T>(MainWindow window, string name) where T : class =>
        window.FindName(name) as T ?? throw new InvalidOperationException($"WPF element '{name}' was not found as {typeof(T).Name}.");

    private static string UiArtifactDirectory() => Path.Combine(Path.GetTempPath(), "DiskAtlasOptimizationChecks", "ui-artifacts");

    private static void Capture(Window window, string fileName)
    {
        window.UpdateLayout();
        int width = Math.Max(1, (int)Math.Ceiling(window.ActualWidth));
        int height = Math.Max(1, (int)Math.Ceiling(window.ActualHeight));
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        string directory = UiArtifactDirectory();
        Directory.CreateDirectory(directory);
        using var output = File.Create(Path.Combine(directory, fileName));
        encoder.Save(output);
    }

    private static async Task WaitFor(Func<bool> condition, TimeSpan timeout, string message)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException(message);
            await Task.Delay(20);
        }
    }

    private static bool IsCandidate(OptimizationItemResult item) => item.Status == OptimizationItemStatus.Candidate;
    private static bool Contains(OptimizationReport report, string path) => report.Items.Any(item => item.Status == OptimizationItemStatus.Candidate && SamePath(item.Path, path));
    private static bool HasAnyPath(OptimizationReport report, string path) => report.Items.Any(item => SamePath(item.Path, path));
    private static bool SamePath(string left, string right) => Path.GetFullPath(left).Equals(Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
    private static DateTime DaysOld(int days) => Now.AddDays(-days);

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static async Task AssertThrows<TException>(Func<Task> action, string message) where TException : Exception
    {
        try { await action(); }
        catch (TException) { return; }
        throw new InvalidOperationException(message);
    }

    private static string NewRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "DiskAtlasOptimizationChecks", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static string WriteAged(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        File.SetCreationTimeUtc(path, DaysOld(40));
        File.SetLastWriteTimeUtc(path, DaysOld(40));
        File.SetLastAccessTimeUtc(path, DaysOld(40));
        return path;
    }

    private static void SetOldTimes(string path)
    {
        File.SetCreationTimeUtc(path, DaysOld(40));
        File.SetLastWriteTimeUtc(path, DaysOld(40));
        File.SetLastAccessTimeUtc(path, DaysOld(40));
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateHardLinkW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string newFileName, string existingFileName, IntPtr securityAttributes);

    private sealed class Fixture
    {
        private readonly Dictionary<OptimizationCategory, string[]> _roots;
        public IReadOnlyDictionary<OptimizationCategory, string[]> Roots => _roots;
        public StorageOptimizationService Service { get; }

        public Fixture(Dictionary<OptimizationCategory, string[]>? layout = null)
        {
            string parent = NewRoot();
            layout ??= new Dictionary<OptimizationCategory, string[]> { [OptimizationCategory.UserTemporary] = ["temp"] };
            _roots = layout.ToDictionary(pair => pair.Key, pair => pair.Value.Select(name =>
            {
                string path = Path.IsPathRooted(name) ? name : Path.Combine(parent, name);
                Directory.CreateDirectory(path);
                return path;
            }).ToArray());
            Service = new StorageOptimizationService(_roots, () => Now);
        }

        public string Root(OptimizationCategory category) => _roots[category][0];

        public string Write(OptimizationCategory category, string relativePath, string content, DateTime creation,
            DateTime? write = null, DateTime? access = null)
        {
            string path = Path.Combine(Root(category), relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
            File.SetCreationTimeUtc(path, creation);
            File.SetLastWriteTimeUtc(path, write ?? creation);
            File.SetLastAccessTimeUtc(path, access ?? creation);
            return path;
        }

        public string Write(OptimizationCategory category, string relativePath, string content, FileTimes times) =>
            Write(category, relativePath, content, times.Creation, times.Write, times.Access);

        public Task<OptimizationReport> Analyze(OptimizationOptions? options = null) => Service.AnalyzeAsync(options ?? new OptimizationOptions());
    }

    private sealed record FileTimes(DateTime Creation, DateTime Write, DateTime Access);

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed class SkipCheckException(string message) : Exception(message);
}
