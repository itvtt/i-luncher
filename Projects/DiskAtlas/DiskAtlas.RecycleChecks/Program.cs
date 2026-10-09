using DiskAtlas.App;
using DiskAtlas.Core;
using System.Reflection;

if (!OperatingSystem.IsWindows())
    throw new PlatformNotSupportedException("These checks exercise the Windows Recycle Bin.");

string fixtureDirectory = Path.Combine(
    Path.GetTempPath(),
    "DiskAtlas.RecycleChecks",
    Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(fixtureDirectory);

try
{
    const string fixtureContents = "owned by DiskAtlas.RecycleChecks";
    string? observedRecycledPath = null;
    string? observedRecycledContents = null;
    ConstructorInfo diagnosticConstructor = typeof(WindowsFileRecycler).GetConstructor(
        BindingFlags.Instance | BindingFlags.NonPublic,
        binder: null,
        [typeof(nint), typeof(Action<string>)],
        modifiers: null)
        ?? throw new InvalidOperationException("The recycle diagnostic constructor was not found.");
    var recycler = (WindowsFileRecycler)diagnosticConstructor.Invoke([
        (nint)0,
        (Action<string>)(recycledPath =>
        {
            observedRecycledPath = recycledPath;
            observedRecycledContents = File.ReadAllText(recycledPath);
        })
    ]);

    MethodInfo recycleFlagGuard = typeof(WindowsFileRecycler).GetMethod(
        "EnsureRecycleTransferFlag",
        BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("The recycle transfer guard was not found.");
    try
    {
        recycleFlagGuard.Invoke(null, [0u]);
        throw new InvalidOperationException("Transfer flags without the recycle bit were accepted.");
    }
    catch (TargetInvocationException exception) when (exception.InnerException is IOException)
    {
        // Expected: a callback that is not marked as recycling must fail closed.
    }

    string rejectedFile = Path.Combine(fixtureDirectory, "callback-reject.txt");
    await File.WriteAllTextAsync(rejectedFile, fixtureContents);
    int rejectedCallbackCount = 0;
    try
    {
        await recycler.RecycleAsync(
            rejectedFile,
            () =>
            {
                rejectedCallbackCount++;
                throw new FixtureRejectedException();
            },
            CancellationToken.None);
        throw new InvalidOperationException("A rejected validation unexpectedly succeeded.");
    }
    catch (FixtureRejectedException)
    {
        Check(rejectedCallbackCount == 1, "The rejecting callback must run exactly once.");
        Check(File.Exists(rejectedFile), "A callback rejection must leave the file untouched.");
    }

    int unsupportedCallbackCount = 0;
    try
    {
        await recycler.RecycleAsync(
            @"\\localhost\DiskAtlas.RecycleChecks\unsupported.txt",
            () => unsupportedCallbackCount++,
            CancellationToken.None);
        throw new InvalidOperationException("A UNC path unexpectedly reached the shell operation.");
    }
    catch (NotSupportedException)
    {
        Check(unsupportedCallbackCount == 0, "An unsupported path must fail before validation.");
    }

    string canceledFile = Path.Combine(fixtureDirectory, "pre-canceled.txt");
    await File.WriteAllTextAsync(canceledFile, fixtureContents);
    int canceledCallbackCount = 0;
    using (var canceled = new CancellationTokenSource())
    {
        canceled.Cancel();
        try
        {
            await recycler.RecycleAsync(
                canceledFile,
                () => canceledCallbackCount++,
                canceled.Token);
            throw new InvalidOperationException("A pre-canceled operation unexpectedly succeeded.");
        }
        catch (OperationCanceledException)
        {
            Check(canceledCallbackCount == 0, "A pre-canceled operation must not invoke validation.");
            Check(File.Exists(canceledFile), "A pre-canceled operation must leave the file untouched.");
        }
    }

    string replacedPath = Path.Combine(fixtureDirectory, "callback-replaced.txt");
    string movedOriginalPath = Path.Combine(fixtureDirectory, "callback-replaced-original.txt");
    const string replacementContents = "replacement owned by DiskAtlas.RecycleChecks";
    await File.WriteAllTextAsync(replacedPath, fixtureContents);
    int replacementCallbackCount = 0;
    try
    {
        await recycler.RecycleAsync(
            replacedPath,
            () =>
            {
                replacementCallbackCount++;
                File.Move(replacedPath, movedOriginalPath);
                File.WriteAllText(replacedPath, replacementContents);
            },
            CancellationToken.None);
        throw new InvalidOperationException("A path replacement during validation unexpectedly succeeded.");
    }
    catch (IOException)
    {
        Check(replacementCallbackCount == 1, "The path-replacement callback must run exactly once.");
        Check(File.Exists(replacedPath), "The generated replacement must remain at the selected path.");
        Check(File.ReadAllText(replacedPath) == replacementContents,
            "The generated replacement contents must remain unchanged.");
        Check(File.Exists(movedOriginalPath), "The generated original must remain at its moved path.");
        Check(File.ReadAllText(movedOriginalPath) == fixtureContents,
            "The generated original contents must remain unchanged.");
    }

    string recycledFile = Path.Combine(fixtureDirectory, "actual-recycle.txt");
    await File.WriteAllTextAsync(recycledFile, fixtureContents);
    int recycleCallbackCount = 0;
    bool existedDuringCallback = false;
    await recycler.RecycleAsync(
        recycledFile,
        () =>
        {
            recycleCallbackCount++;
            existedDuringCallback = File.Exists(recycledFile);
        },
        CancellationToken.None);

    Check(recycleCallbackCount == 1, "The acceptance callback must run exactly once.");
    Check(existedDuringCallback, "The generated file must exist during pre-delete validation.");
    Check(!File.Exists(recycledFile), "The generated file must leave its original location.");
    Check(observedRecycledPath is not null && File.Exists(observedRecycledPath),
        "The shell's returned Recycle Bin item must resolve to an accessible file.");
    Check(observedRecycledContents == fixtureContents,
        "The file reached through the returned Recycle Bin item must retain its contents.");

    observedRecycledPath = null;
    observedRecycledContents = null;
    string keeperFile = Path.Combine(fixtureDirectory, "core-keeper.txt");
    string selectedFile = Path.Combine(fixtureDirectory, "core-selected.txt");
    await File.WriteAllTextAsync(keeperFile, fixtureContents);
    await File.WriteAllTextAsync(selectedFile, fixtureContents);

    ScanNode keeperNode = CreateNode(keeperFile);
    ScanNode selectedNode = CreateNode(selectedFile);
    DuplicateScanResult scan = await new DuplicateFinder().FindAsync(
        [keeperNode, selectedNode],
        cancellationToken: CancellationToken.None);
    Check(scan.Groups.Count == 1 && scan.Groups[0].Files.Count == 2,
        "DuplicateFinder must produce one group for the generated pair.");

    DuplicateDeleteResult deletion = await new DuplicateDeletionService(recycler).DeleteAsync(
        scan.Groups,
        [selectedFile],
        cancellationToken: CancellationToken.None);
    Check(deletion.Failures.Count == 0, "Core deletion must not report a failure.");
    Check(deletion.DeletedPaths.Count == 1 &&
        string.Equals(deletion.DeletedPaths[0], selectedFile, StringComparison.OrdinalIgnoreCase),
        "Core deletion must report only the selected generated file.");
    Check(File.Exists(keeperFile), "Core deletion must keep the generated keeper file.");
    Check(!File.Exists(selectedFile), "Core deletion must recycle the generated selected file.");
    Check(observedRecycledPath is not null && File.Exists(observedRecycledPath),
        "Core deletion must expose an accessible returned Recycle Bin item.");
    Check(observedRecycledContents == fixtureContents,
        "Core deletion must preserve the generated selected file's contents in the Recycle Bin.");

    Console.WriteLine("PASS: callback rejection left its generated fixture in place.");
    Console.WriteLine("PASS: transfer flags without the recycle bit failed closed.");
    Console.WriteLine("PASS: unsupported UNC input failed before its callback.");
    Console.WriteLine("PASS: a pre-canceled operation left its generated fixture untouched.");
    Console.WriteLine("PASS: a callback-time path replacement was detected before recycling.");
    Console.WriteLine("PASS: accepted fixture data was verified through the returned Recycle Bin item.");
    Console.WriteLine("PASS: DuplicateFinder and DuplicateDeletionService recycled one selected fixture while keeping its verified peer open.");
}
finally
{
    // Delete only files created by this process that intentionally remained. The
    // successfully recycled fixture remains in the user's Recycle Bin for inspection.
    if (Directory.Exists(fixtureDirectory))
        Directory.Delete(fixtureDirectory, recursive: true);
}

static void Check(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

static ScanNode CreateNode(string path)
{
    var info = new FileInfo(path);
    return new ScanNode
    {
        Name = info.Name,
        FullPath = info.FullName,
        IsDirectory = false,
        Size = info.Length,
        AllocatedSize = info.Length,
        LastWriteTimeUtc = info.LastWriteTimeUtc,
        Attributes = info.Attributes
    };
}

sealed class FixtureRejectedException : Exception;
