using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.IO.Compression;
using DiskAtlas.Core;
using DiskAtlas.Core.Scanning;

var fixture = Path.Combine(Path.GetTempPath(), "DiskAtlas-Checks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(fixture);
var scanner = new DiskScanner();
var search = new ContentSearchService();
if (args.Length == 2 && args[0] == "--inspect-drive")
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
    var measured = await scanner.ScanAsync(args[1], cancellationToken: timeout.Token);
    var resultJson = JsonSerializer.Serialize(new { timestamp = DateTimeOffset.Now, root = measured.Root.FullPath, bytes = measured.Root.Size,
        measured.Summary.Files, measured.Summary.Directories, measured.Summary.Skipped, measured.Summary.Elapsed,
        measured.Summary.Mode, measured.Summary.AllocationIsEstimated, warningCount = measured.Summary.Warnings.Count,
        warnings = measured.Summary.Warnings }, new JsonSerializerOptions { WriteIndented = true });
    var resultDirectory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../artifacts"));
    Directory.CreateDirectory(resultDirectory);
    File.WriteAllText(Path.Combine(resultDirectory, "drive-check.json"), resultJson);
    Console.WriteLine(resultJson);
    return 0;
}
var outcomes = new List<object>();
var failed = 0;
async Task Check(string name, Func<Task> action)
{
    var timer = Stopwatch.StartNew();
    try
    {
        await action();
        outcomes.Add(new { name, passed = true, milliseconds = timer.Elapsed.TotalMilliseconds });
        Console.WriteLine($"PASS {name} ({timer.Elapsed.TotalMilliseconds:F1} ms)");
    }
    catch (Exception error)
    {
        failed++;
        outcomes.Add(new { name, passed = false, error = error.ToString() });
        Console.Error.WriteLine($"FAIL {name}: {error}");
    }
}
void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
string Write(string relative, string text, Encoding? encoding = null)
{
    var path = Path.Combine(fixture, relative);
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, text, encoding ?? new UTF8Encoding(false));
    return path;
}
ScanNode FileNode(string path) => new()
{
    Name = Path.GetFileName(path), FullPath = path, Size = new FileInfo(path).Length,
    Attributes = File.GetAttributes(path)
};
async Task AssertArgumentRejected(Func<Task> action, string message)
{
    try
    {
        await action();
        throw new InvalidOperationException(message);
    }
    catch (ArgumentException) { }
}

var smallRoot = Path.Combine(fixture, "scan");
Write("scan/a/one.txt", "abc");
Write("scan/a/한글.txt", "가나다라마바사");
Write("scan/b/two.bin", new string('x', 8193));
var systemFile = Write("scan/b/system.txt", "private-system-marker");
File.SetAttributes(systemFile, File.GetAttributes(systemFile) | FileAttributes.System);
Directory.CreateDirectory(Path.Combine(smallRoot, "empty"));

await Check("NTFS records: fixups, resident/nonresident sizes, sparse runs and corruption rejection", () =>
{
    ParserChecks.Run();
    return Task.CompletedTask;
});

await Check("scan totals, nested folders, empty folders, Unicode and system usage", async () =>
{
    var scan = await scanner.ScanAsync(smallRoot);
    long actual = Directory.EnumerateFiles(smallRoot, "*", SearchOption.AllDirectories).Sum(path => new FileInfo(path).Length);
    Assert(scan.Files.Count == 4 && scan.Summary.Files == 4, "All four files must be included in disk analysis");
    Assert(scan.Root.Size == actual && scan.Root.FileCount == 4, "Root totals mismatch");
    Assert(scan.Root.Children.Any(node => node.Name == "empty" && node.IsDirectory && node.Size == 0), "Empty folder missing");
    Assert(scan.Files.All(node => node.Parent != null && File.Exists(node.FullPath)), "Parent/path reconstruction failed");
    Assert(scan.Root.Children.Single(node => node.Name == "a").Size == 3 + Encoding.UTF8.GetByteCount("가나다라마바사"), "Nested total mismatch");
    Assert(scan.Summary.Elapsed >= TimeSpan.Zero && !string.IsNullOrWhiteSpace(scan.Summary.Mode), "Scan metadata missing");
});

await Check("scan warnings group repeated outcomes and retain exact counts with examples", () =>
{
    var warnings = new FileSystemScanner.WarningCollector(["MFT fallback detail"]);
    Parallel.For(0, 250, index => warnings.Add($"distinct failure {index}"));
    Parallel.For(0, 78, index => warnings.AddSkipped($@"C:\protected\{index}", new UnauthorizedAccessException("denied")));
    Parallel.For(0, 12, index => warnings.AddSkipped($@"C:\damaged\{index}", new IOException("device error")));
    Parallel.For(0, 45, index => warnings.AddReparsePoint($@"C:\links\{index}"));
    var snapshot = warnings.Snapshot();
    Assert(snapshot.Count == 106, "Warning limit or grouped warning count changed unexpectedly");
    Assert(snapshot.Count(item => item.StartsWith("Skipped 78 directories because access was denied.")) == 1,
        "Access-denied failures were not grouped with their exact count");
    Assert(snapshot.Count(item => item.StartsWith("Skipped 12 directories because of file-system I/O errors.")) == 1,
        "I/O failures were not grouped with their exact count");
    Assert(snapshot.Count(item => item.StartsWith("Did not traverse 45 reparse-point directories")) == 1,
        "Reparse-point advisories were not grouped with their exact count");
    Assert(snapshot.Single(item => item.StartsWith("Skipped 78 directories because access was denied."))
        .Contains("(+75 more).", StringComparison.Ordinal),
        "Grouped warning did not bound its path examples");
    Assert(snapshot.Contains("151 additional warnings were omitted."),
        "Concurrent general-warning cap did not retain the exact omitted count");
    return Task.CompletedTask;
});

await Check("empty root and pre-cancelled scan", async () =>
{
    var empty = Path.Combine(fixture, "empty-root");
    Directory.CreateDirectory(empty);
    var scan = await scanner.ScanAsync(empty);
    Assert(scan.Root.Size == 0 && scan.Files.Count == 0, "Empty root incorrect");
    using var cancelled = new CancellationTokenSource();
    cancelled.Cancel();
    try { await scanner.ScanAsync(smallRoot, cancellationToken: cancelled.Token); throw new Exception("Cancellation ignored"); }
    catch (OperationCanceledException) { }
});

await Check("in-flight scan cancellation and clean subsequent rescan", async () =>
{
    using var cancellation = new CancellationTokenSource();
    var progress = new InlineProgress<ScanProgress>(_ => cancellation.Cancel());
    try { await scanner.ScanAsync(smallRoot, progress, cancellation.Token); throw new Exception("In-flight cancellation ignored"); }
    catch (OperationCanceledException) { }
    var subsequent = await scanner.ScanAsync(smallRoot);
    Assert(subsequent.Files.Count == 4, "Cancelled scan leaked state into subsequent scan");
});

await Check("all search modes exclude system files and protected OS roots", async () =>
{
    var normal = Write("search/normal.txt", "private-system-marker");
    var nodes = new[] { FileNode(normal), FileNode(systemFile) };
    foreach (var mode in Enum.GetValues<SearchMode>())
    {
        var report = await search.SearchAsync(nodes, new SearchOptions(mode == SearchMode.FileName ? ".txt" : "private-system-marker", mode));
        Assert(report.Hits.Count == 1 && report.Hits[0].Node.FullPath == normal, $"System file escaped filter in {mode}");
    }
    foreach (var path in new[] { @"C:\Windows\notes.txt", @"C:\Windows.old\notes.txt", @"C:\Program Files\app\notes.txt",
        @"C:\ProgramData\notes.txt", @"C:\$Recycle.Bin\notes.txt", @"C:\System Volume Information\notes.txt", @"C:\pagefile.sys" })
        Assert(SearchPolicy.IsExcluded(new ScanNode { FullPath = path, Name = Path.GetFileName(path) }), $"Protected path accepted: {path}");
    Assert(!SearchPolicy.IsExcluded(new ScanNode { FullPath = @"C:\Users\me\Documents\Windows\notes.txt", Name = "notes.txt" }), "User Windows-named folder over-excluded");
    Assert(!SearchPolicy.IsExcluded(new ScanNode { FullPath = @"C:\WindowsProjects\notes.txt", Name = "notes.txt" }), "Path prefix boundary failed");
});

await Check("boolean query parsing, literal phrases, precedence and parentheses", async () =>
{
    var nodes = new[]
    {
        FileNode(Write("boolean/plain-phrase.txt", "여기에 코드 분석 결과가 있습니다")),
        FileNode(Write("boolean/separated-phrase.txt", "코드 상세 분석")),
        FileNode(Write("boolean/alpha-only.txt", "alpha")),
        FileNode(Write("boolean/beta-gamma.txt", "beta and then gamma")),
        FileNode(Write("boolean/beta-only.txt", "beta")),
        FileNode(Write("boolean/literal-or.txt", "left OR right")),
        FileNode(Write("boolean/separated-or.txt", "left then OR then right"))
    };

    var phrase = await search.SearchAsync(nodes, new SearchOptions("코드 분석", SearchMode.Contents));
    Assert(phrase.Hits.Count == 1 && phrase.Hits[0].Node.Name == "plain-phrase.txt",
        "A spaced query must remain one literal phrase");

    var precedence = await search.SearchAsync(nodes, new SearchOptions("alpha OR beta AND gamma", SearchMode.Contents));
    Assert(precedence.Hits.Select(hit => hit.Node.Name).Order().SequenceEqual(new[] { "alpha-only.txt", "beta-gamma.txt" }),
        "AND must bind more tightly than OR");

    var parentheses = await search.SearchAsync(nodes, new SearchOptions("(alpha OR beta) AND gamma", SearchMode.Contents));
    Assert(parentheses.Hits.Count == 1 && parentheses.Hits[0].Node.Name == "beta-gamma.txt",
        "Parentheses did not override precedence");

    var quoted = await search.SearchAsync(nodes, new SearchOptions("\"left OR right\"", SearchMode.Contents));
    Assert(quoted.Hits.Count == 1 && quoted.Hits[0].Node.Name == "literal-or.txt",
        "A quoted operator must be searched as literal phrase text");

    var operatorCase = await search.SearchAsync(nodes, new SearchOptions("BeTa aNd GaMmA", SearchMode.Contents));
    Assert(operatorCase.Hits.Count == 1 && operatorCase.Hits[0].Node.Name == "beta-gamma.txt",
        "Operators or default term matching were not case-insensitive");
});

await Check("boolean terms span lines, stream buffers, filenames and content", async () =>
{
    string splitPath = Write("boolean-stream/split.txt",
        "first-term\n" + new string('x', 40 * 1024) + "\nsecond-term");
    string combinedPath = Write("boolean-stream/name-token-file.txt", "body-token appears here");
    var contentNodes = new[] { FileNode(splitPath), FileNode(combinedPath) };

    var split = await search.SearchAsync(contentNodes,
        new SearchOptions("first-term AND second-term", SearchMode.Contents));
    Assert(split.Hits.Count == 1 && split.Hits[0].Node.FullPath == splitPath,
        "AND terms on different lines or buffers were not accumulated for one file");

    var nameAndBody = await search.SearchAsync(contentNodes,
        new SearchOptions("name-token AND body-token", SearchMode.NameOrContents));
    Assert(nameAndBody.Hits.Count == 1 && nameAndBody.Hits[0].Node.FullPath == combinedPath &&
           nameAndBody.Hits[0].MatchKind == "Contents",
        "NameOrContents did not combine filename and body term matches");

    var virtualNames = new[]
    {
        new ScanNode { Name = "red-report.txt", FullPath = Path.Combine(fixture, "missing", "red-report.txt") },
        new ScanNode { Name = "blue-notes.txt", FullPath = Path.Combine(fixture, "missing", "blue-notes.txt") },
        new ScanNode { Name = "green.txt", FullPath = Path.Combine(fixture, "missing", "green.txt") }
    };
    var filename = await search.SearchAsync(virtualNames,
        new SearchOptions("red OR blue", SearchMode.FileName));
    Assert(filename.Hits.Count == 2 && filename.Skipped == 0,
        "FileName mode did not evaluate the full expression without content reads");
});

await Check("boolean terms span searchable Office XML parts", async () =>
{
    string path = Path.Combine(fixture, "boolean-office.docx");
    using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
    {
        using (var writer = new StreamWriter(archive.CreateEntry("word/document.xml").Open()))
            writer.Write("<document><p><r><t>document-term</t></r></p></document>");
        using (var writer = new StreamWriter(archive.CreateEntry("word/header1.xml").Open()))
            writer.Write("<header><p><r><t>header-term</t></r></p></header>");
    }

    var report = await search.SearchAsync([FileNode(path)],
        new SearchOptions("document-term AND header-term", SearchMode.Contents));
    Assert(report.Hits.Count == 1, "AND terms in different Office parts were not accumulated");

    string boundaryPath = Path.Combine(fixture, "boolean-office-boundaries.docx");
    using (var archive = ZipFile.Open(boundaryPath, ZipArchiveMode.Create))
    using (var writer = new StreamWriter(archive.CreateEntry("word/document.xml").Open()))
        writer.Write("<document><p><r><t>alpha</t></r></p><p><r><t>beta</t></r></p></document>");
    var falsePhrase = await search.SearchAsync([FileNode(boundaryPath)],
        new SearchOptions("alphabeta", SearchMode.Contents));
    Assert(falsePhrase.Hits.Count == 0,
        "Text in separate Office paragraphs was incorrectly concatenated into a literal match");

    string breakPath = Path.Combine(fixture, "boolean-office-break.docx");
    using (var archive = ZipFile.Open(breakPath, ZipArchiveMode.Create))
    using (var writer = new StreamWriter(archive.CreateEntry("word/document.xml").Open()))
        writer.Write("<document><p><r><t>line</t><br/><t>break</t><t>run</t></r></p></document>");
    var falseBreakPhrase = await search.SearchAsync([FileNode(breakPath)],
        new SearchOptions("linebreak", SearchMode.Contents));
    var contiguousRuns = await search.SearchAsync([FileNode(breakPath)],
        new SearchOptions("breakrun", SearchMode.Contents));
    Assert(falseBreakPhrase.Hits.Count == 0 && contiguousRuns.Hits.Count == 1,
        "Office explicit breaks and contiguous formatting runs were not distinguished");
});

await Check("malformed and excessive boolean queries are rejected in Korean", () =>
{
    string[] malformed =
    [
        "alpha AND", "OR alpha", "alpha AND OR beta", "(alpha OR beta", "alpha OR beta)",
        "()", "\"unterminated", "\"\"", "\"alpha\" \"beta\"",
        new string('(', QueryExpression.MaximumNestingDepth + 1) + "alpha" +
            new string(')', QueryExpression.MaximumNestingDepth + 1)
    ];

    foreach (string query in malformed)
    {
        try
        {
            QueryExpression.Parse(query);
            throw new Exception($"Malformed query was accepted: {query}");
        }
        catch (ArgumentException error) when (error.ParamName == "query")
        {
            Assert(error.Message.Any(character => character is >= '\uac00' and <= '\ud7a3'),
                $"Malformed query did not return a Korean message: {query}");
        }
    }

    string tooManyTerms = string.Join(" OR ", Enumerable.Repeat("term", QueryExpression.MaximumTerms + 1));
    try
    {
        QueryExpression.Parse(tooManyTerms);
        throw new Exception("Term-count limit was ignored");
    }
    catch (ArgumentException error) when (error.ParamName == "query") { }

    return Task.CompletedTask;
});

await Check("UTF-8, UTF-16, UTF-32 text and case-sensitive content search", async () =>
{
    var nodes = new[]
    {
        FileNode(Write("encoding/utf8.txt", "before\n검색대상 NeedleCase\nafter")),
        FileNode(Write("encoding/utf16.txt", "검색대상 NeedleCase", Encoding.Unicode)),
        FileNode(Write("encoding/utf16be.txt", "검색대상 NeedleCase", Encoding.BigEndianUnicode)),
        FileNode(Write("encoding/utf32.txt", "검색대상 NeedleCase", Encoding.UTF32))
    };
    Assert((await search.SearchAsync(nodes, new SearchOptions("검색대상", SearchMode.Contents))).Hits.Count == 4, "Unicode encoding detection failed");
    Assert((await search.SearchAsync(nodes, new SearchOptions("needlecase", SearchMode.Contents))).Hits.Count == 4, "Case-insensitive matching failed");
    Assert((await search.SearchAsync(nodes, new SearchOptions("needlecase", SearchMode.Contents, true))).Hits.Count == 0, "Case-sensitive matching ignored");
});

await Check("streaming content matches across buffers and inside a large file", async () =>
{
    var paths = new List<ScanNode>();
    foreach (int boundary in new[] { 4096, 32768, 65536, 262144, 1048576 })
        paths.Add(FileNode(Write($"boundary/{boundary}.txt", new string('a', boundary - 4) + "unique-boundary-needle" + new string('b', 100))));
    var large = Write("boundary/large.log", "");
    await using (var stream = File.OpenWrite(large))
    {
        var buffer = Encoding.UTF8.GetBytes(new string('a', 1024 * 1024));
        for (int i = 0; i < 32; i++) await stream.WriteAsync(buffer);
        await stream.WriteAsync(Encoding.UTF8.GetBytes("unique-boundary-needle"));
    }
    paths.Add(FileNode(large));
    var report = await search.SearchAsync(paths, new SearchOptions("unique-boundary-needle", SearchMode.Contents));
    Assert(report.Hits.Count == paths.Count, "Boundary or large-file match missed");
    Assert(report.Hits.All(hit => hit.Preview.Length < 2000), "Preview must remain bounded");
});

await Check("many absent terms keep streaming allocations bounded", async () =>
{
    string path = Write("boundary/many-terms.txt", new string('q', 4 * 1024 * 1024));
    string query = string.Join(" OR ", Enumerable.Range(0, QueryExpression.MaximumTerms)
        .Select(index => $"absent-token-{index:D2}"));
    GC.Collect();
    GC.WaitForPendingFinalizers();
    long before = GC.GetTotalAllocatedBytes(precise: true);
    var report = await search.SearchAsync([FileNode(path)], new SearchOptions(query, SearchMode.Contents));
    long allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
    Assert(report.Hits.Count == 0, "Absent-term allocation fixture unexpectedly matched");
    Assert(allocated < 128L * 1024 * 1024,
        $"Streaming matcher allocated {allocated:N0} bytes for a 4 MiB file and 64 absent terms");
});

await Check("Office XML content, split formatting runs and malformed document isolation", async () =>
{
    ScanNode Office(string name, string entryName, string xml)
    {
        var path = Path.Combine(fixture, name);
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(archive.CreateEntry(entryName).Open())) writer.Write(xml);
        return FileNode(path);
    }
    var documents = new[]
    {
        Office("sample.docx", "word/document.xml", "<document><p><r><t>Office</t></r><r><t>Needle</t></r></p></document>"),
        Office("sample.xlsx", "xl/sharedStrings.xml", "<sst><si><t>OfficeNeedle</t></si></sst>"),
        Office("sample.pptx", "ppt/slides/slide1.xml", "<slide><t>OfficeNeedle</t></slide>"),
        Office("unsafe.docx", "word/document.xml", "<!DOCTYPE d [<!ENTITY e SYSTEM 'file:///C:/Windows/win.ini'>]><d>&e;</d>"),
        FileNode(Write("broken.docx", "not a zip archive"))
    };
    var report = await search.SearchAsync(documents, new SearchOptions("OfficeNeedle", SearchMode.Contents));
    Assert(report.Hits.Count == 3 && report.Skipped >= 2, "Office matches or invalid document isolation failed");
});

await Check("binary files, reparse/cloud placeholders and ancestor attributes are excluded", async () =>
{
    var binary = Path.Combine(fixture, "binary.bin");
    File.WriteAllBytes(binary, [0, 1, 0, 2, 0, 3, 65, 66, 67]);
    Assert((await search.SearchAsync([FileNode(binary)], new SearchOptions("ABC", SearchMode.Contents))).Hits.Count == 0, "Binary content treated as text");
    foreach (var attributes in new[] { FileAttributes.System, FileAttributes.ReparsePoint, FileAttributes.Offline, (FileAttributes)0x00400000, (FileAttributes)0x00040000 })
        Assert(SearchPolicy.IsExcluded(new ScanNode { Name = "a.txt", FullPath = @"C:\Users\me\a.txt", Attributes = attributes }), $"Unsafe content attribute allowed: {attributes}");
    var parent = new ScanNode { FullPath = @"C:\private", IsDirectory = true, Attributes = FileAttributes.System };
    Assert(SearchPolicy.IsExcluded(new ScanNode { FullPath = @"C:\private\a.txt", Name = "a.txt", Parent = parent }), "Ancestor system attribute ignored");
});

await Check("result limit, no-content filename search and search cancellation", async () =>
{
    var virtualNodes = Enumerable.Range(0, 60).Select(index => new ScanNode
    { Name = $"match-{index}.txt", FullPath = Path.Combine(fixture, "not-on-disk", $"match-{index}.txt") }).ToArray();
    var report = await search.SearchAsync(virtualNodes, new SearchOptions("match", SearchMode.FileName, MaxResults: 7));
    Assert(report.Hits.Count == 7 && report.LimitReached, "Result limit not enforced/reported");
    Assert((await search.SearchAsync(virtualNodes, new SearchOptions("match", SearchMode.NameOrContents))).Hits.Count == 60, "Name match should not need file content access");
    using var cancellation = new CancellationTokenSource();
    cancellation.Cancel();
    try { await search.SearchAsync(virtualNodes, new SearchOptions("match"), cancellationToken: cancellation.Token); throw new Exception("Search cancellation ignored"); }
    catch (OperationCanceledException) { }
});

await Check("duplicate finder hashes only eligible same-size candidates and freezes paths", async () =>
{
    string first = Write("duplicates/first-name.bin", "same duplicate payload");
    string second = Write("duplicates/other-name.bin", "same duplicate payload");
    string sameSizeDifferent = Write("duplicates/same-size.bin", "different payload.....");
    Assert(new FileInfo(first).Length == new FileInfo(sameSizeDifferent).Length,
        "Duplicate same-size fixture is not the intended length");
    string unique = Write("duplicates/unique.bin", "a size no other candidate has");
    string empty = Write("duplicates/empty.bin", "");
    string protectedFile = Write("duplicates/system.bin", "same duplicate payload");
    File.SetAttributes(protectedFile, File.GetAttributes(protectedFile) | FileAttributes.System);

    var firstNode = FileNode(first);
    firstNode.Parent = new ScanNode
    {
        FullPath = Path.GetPathRoot(first)!,
        Name = Path.GetPathRoot(first)!,
        IsDirectory = true,
        Attributes = FileAttributes.Directory | FileAttributes.System | FileAttributes.Hidden
    };
    IEnumerable<ScanNode> Nodes()
    {
        yield return firstNode;
        // The finder must have copied the first path before asking for the next item.
        firstNode.FullPath = Path.Combine(fixture, "mutated-after-snapshot.bin");
        firstNode.Size = long.MaxValue;
        yield return FileNode(second);
        yield return FileNode(second);
        yield return FileNode(sameSizeDifferent);
        yield return FileNode(unique);
        yield return FileNode(empty);
        yield return FileNode(protectedFile);
    }
    DuplicateScanResult result = await new DuplicateFinder().FindAsync(Nodes());

    Assert(result.Groups.Count == 1, "Exactly one duplicate group should be found");
    DuplicateGroup group = result.Groups[0];
    Assert(group.Files.Select(file => file.FullPath).Order(StringComparer.OrdinalIgnoreCase)
        .SequenceEqual(new[] { first, second }.Order(StringComparer.OrdinalIgnoreCase)),
        "Different names with equal content were not grouped or the frozen path changed");
    Assert(group.ReclaimableBytes == new FileInfo(first).Length, "Reclaimable byte count is wrong");
    Assert(group.Files.All(file => file.Size > 0 && file.ContentHash.Length == 64 && file.Identity.Contains(':')),
        "Duplicate snapshot metadata is incomplete");
    Assert(result.SkippedFiles >= 2, "Empty and protected files were not excluded");
});

await Check("duplicate finder excludes reparse points and hard links", async () =>
{
    string original = Write("duplicate-links/original.bin", "linked duplicate payload");
    string hardLink = Path.Combine(fixture, "duplicate-links", "hard-link.bin");
    bool hardLinkCreated = NativeCheckHelpers.TryCreateHardLink(hardLink, original);
    if (hardLinkCreated)
    {
        var result = await new DuplicateFinder().FindAsync([FileNode(original), FileNode(hardLink)]);
        Assert(result.Groups.Count == 0 && result.SkippedFiles == 2,
            "Files with multiple hard links must be excluded from duplicate deletion");
    }

    string target = Write("duplicate-links/target.bin", "reparse duplicate payload");
    string symbolic = Path.Combine(fixture, "duplicate-links", "symbolic.bin");
    try
    {
        File.CreateSymbolicLink(symbolic, target);
        var result = await new DuplicateFinder().FindAsync([FileNode(target), FileNode(symbolic)]);
        Assert(result.Groups.Count == 0 && result.SkippedFiles >= 1,
            "A reparse-point file escaped duplicate filtering");
    }
    catch (Exception error) when (error is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
    {
        // Symlink creation can be unavailable without Windows developer mode.
    }
});

await Check("duplicate deletion validates selection and uses only the recycler", async () =>
{
    string keeper = Write("duplicate-delete/keeper.bin", "delete guard payload");
    string selected = Write("duplicate-delete/selected.bin", "delete guard payload");
    DuplicateGroup group = (await new DuplicateFinder().FindAsync([FileNode(keeper), FileNode(selected)])).Groups.Single();
    var recycler = new FakeRecycler();
    var service = new DuplicateDeletionService(recycler);
    DuplicateDeleteResult result = await service.DeleteAsync([group], [selected]);

    Assert(result.DeletedPaths.SequenceEqual([selected]) && result.Failures.Count == 0,
        "A verified selected duplicate was not reported as recycled");
    Assert(recycler.Paths.SequenceEqual([selected]) && recycler.ValidationCalls == 1,
        "Deletion bypassed the recycler or its immediate validation callback");
    Assert(File.Exists(selected), "The Core service directly deleted a file instead of delegating to the recycler");

    await AssertArgumentRejected(() => service.DeleteAsync([group], [keeper, selected]),
        "Selecting every copy in a group was accepted");
    await AssertArgumentRejected(() => service.DeleteAsync([group], new List<string> { selected, selected }),
        "A duplicate selected path was accepted");
    await AssertArgumentRejected(() => service.DeleteAsync([group], [Path.Combine(fixture, "unknown.bin")]),
        "A path outside the duplicate results was accepted");
});

await Check("duplicate deletion rejects changed targets and missing verified keepers", async () =>
{
    string keeper = Write("duplicate-changes/keeper.bin", "0123456789ABCDEF");
    string selected = Write("duplicate-changes/selected.bin", "0123456789ABCDEF");
    DuplicateGroup group = (await new DuplicateFinder().FindAsync([FileNode(keeper), FileNode(selected)])).Groups.Single();

    File.WriteAllText(selected, "FEDCBA9876543210");
    File.SetLastWriteTimeUtc(selected, group.Files.Single(file => file.FullPath == selected).LastWriteTimeUtc);
    var changedRecycler = new FakeRecycler();
    DuplicateDeleteResult changed = await new DuplicateDeletionService(changedRecycler).DeleteAsync([group], [selected]);
    Assert(changed.DeletedPaths.Count == 0 && changed.Failures.ContainsKey(selected) && changedRecycler.Paths.Count == 0,
        "A same-size, same-timestamp content change passed final SHA verification");

    string keeper2 = Write("duplicate-keeper-failure/keeper.bin", "keeper verification payload");
    string selected2 = Write("duplicate-keeper-failure/selected.bin", "keeper verification payload");
    DuplicateGroup group2 = (await new DuplicateFinder().FindAsync([FileNode(keeper2), FileNode(selected2)])).Groups.Single();
    File.WriteAllText(keeper2, "changed keeper content!!!!!");
    var keeperRecycler = new FakeRecycler();
    DuplicateDeleteResult noKeeper = await new DuplicateDeletionService(keeperRecycler).DeleteAsync([group2], [selected2]);
    Assert(noKeeper.DeletedPaths.Count == 0 && noKeeper.Failures.ContainsKey(selected2) && keeperRecycler.Paths.Count == 0,
        "Deletion continued without a verified matching keeper");
});

await Check("duplicate operations honor cancellation and return partial deletion results", async () =>
{
    using (var cancelled = new CancellationTokenSource())
    {
        cancelled.Cancel();
        try
        {
            await new DuplicateFinder().FindAsync([], cancellationToken: cancelled.Token);
            throw new Exception("Pre-cancelled duplicate scan was accepted");
        }
        catch (OperationCanceledException) { }
    }

    string inFlight1 = Write("duplicate-cancel/inflight-1.bin", "in-flight duplicate payload");
    string inFlight2 = Write("duplicate-cancel/inflight-2.bin", "in-flight duplicate payload");
    using (var inFlightCancellation = new CancellationTokenSource())
    {
        var cancelProgress = new InlineProgress<DuplicateProgress>(_ => inFlightCancellation.Cancel());
        try
        {
            await new DuplicateFinder().FindAsync(
                [FileNode(inFlight1), FileNode(inFlight2)], cancelProgress, inFlightCancellation.Token);
            throw new Exception("In-flight duplicate scan cancellation was ignored");
        }
        catch (OperationCanceledException) { }
    }

    string keeper = Write("duplicate-cancel/keeper.bin", "cancellation duplicate payload");
    string selected1 = Write("duplicate-cancel/selected-1.bin", "cancellation duplicate payload");
    string selected2 = Write("duplicate-cancel/selected-2.bin", "cancellation duplicate payload");
    DuplicateGroup group = (await new DuplicateFinder().FindAsync(
        [FileNode(keeper), FileNode(selected1), FileNode(selected2)])).Groups.Single();
    using var cancellation = new CancellationTokenSource();
    var recycler = new FakeRecycler(() => cancellation.Cancel());
    DuplicateDeleteResult partial = await new DuplicateDeletionService(recycler).DeleteAsync(
        [group], [selected1, selected2], cancellationToken: cancellation.Token);
    Assert(partial.DeletedPaths.Count == 1 && recycler.Paths.Count == 1,
        "Cancellation lost the successful partial deletion result or processed another file");
});

await Check("duplicate deletion preserves prior success when cancelled during the next keeper hash", async () =>
{
    string keeper1 = Write("duplicate-keeper-cancel/group-1-keeper.bin", "first group duplicate payload");
    string selected1 = Write("duplicate-keeper-cancel/group-1-selected.bin", "first group duplicate payload");
    string keeper2 = Write("duplicate-keeper-cancel/group-2-keeper.bin", "second group different duplicate payload");
    string selected2 = Write("duplicate-keeper-cancel/group-2-selected.bin", "second group different duplicate payload");
    DuplicateGroup group1 = (await new DuplicateFinder().FindAsync([FileNode(keeper1), FileNode(selected1)])).Groups.Single();
    DuplicateGroup group2 = (await new DuplicateFinder().FindAsync([FileNode(keeper2), FileNode(selected2)])).Groups.Single();

    using var cancellation = new CancellationTokenSource();
    var progress = new InlineProgress<DuplicateProgress>(update =>
    {
        if (update.Stage == "보관본 검증" &&
            update.CurrentPath.Equals(keeper2, StringComparison.OrdinalIgnoreCase))
            cancellation.Cancel();
    });
    var recycler = new FakeRecycler();
    DuplicateDeleteResult partial = await new DuplicateDeletionService(recycler).DeleteAsync(
        [group1, group2], [selected1, selected2], progress, cancellation.Token);
    Assert(partial.DeletedPaths.SequenceEqual([selected1]) && recycler.Paths.SequenceEqual([selected1]),
        "Keeper-hash cancellation discarded prior success or recycled the second group");
});

await Check("duplicate deletion releases a failed keeper and falls back to another copy", async () =>
{
    string badKeeper = Write("duplicate-keeper-fallback/a-bad.bin", "fallback duplicate payload");
    string goodKeeper = Write("duplicate-keeper-fallback/b-good.bin", "fallback duplicate payload");
    string selected = Write("duplicate-keeper-fallback/c-selected.bin", "fallback duplicate payload");
    DuplicateGroup group = (await new DuplicateFinder().FindAsync(
        [FileNode(badKeeper), FileNode(goodKeeper), FileNode(selected)])).Groups.Single();
    File.WriteAllText(badKeeper, "changed fallback payload!");

    var recycler = new FakeRecycler();
    DuplicateDeleteResult result = await new DuplicateDeletionService(recycler).DeleteAsync([group], [selected]);
    Assert(result.DeletedPaths.SequenceEqual([selected]) && result.Failures.Count == 0,
        "A valid fallback keeper was not used after the first keeper changed");
    using FileStream exclusiveWrite = File.Open(badKeeper, FileMode.Open, FileAccess.Write, FileShare.None);
    Assert(exclusiveWrite.CanWrite, "The rejected keeper handle remained open");
});

if (args.Contains("--benchmark"))
{
    await Check("100,000-file disk scan and repeated filename search benchmark", async () =>
    {
        var bench = Path.Combine(fixture, "benchmark");
        var payload = Encoding.UTF8.GetBytes("DiskAtlas benchmark content needle 0123456789\n");
        await Parallel.ForEachAsync(Enumerable.Range(0, 100), new ParallelOptions { MaxDegreeOfParallelism = 8 }, (group, _) =>
        {
            var directory = Path.Combine(bench, group.ToString("D3"));
            Directory.CreateDirectory(directory);
            for (int file = 0; file < 1000; file++) File.WriteAllBytes(Path.Combine(directory, $"sample-{file:D4}.txt"), payload);
            return ValueTask.CompletedTask;
        });
        var timer = Stopwatch.StartNew();
        var scan = await scanner.ScanAsync(bench);
        var scanMs = timer.Elapsed.TotalMilliseconds;
        Assert(scan.Files.Count == 100000 && scan.Root.Size == (long)payload.Length * 100000, "Benchmark count/size mismatch");
        timer.Restart();
        var report = await search.SearchAsync(scan.Files, new SearchOptions("sample-0999", SearchMode.FileName));
        var searchMs = timer.Elapsed.TotalMilliseconds;
        Assert(report.Hits.Count == 100, "Benchmark name results incorrect");
        timer.Restart();
        var contents = await search.SearchAsync(scan.Files.Take(10000), new SearchOptions("benchmark content", SearchMode.Contents, MaxResults: 20000));
        var contentMs = timer.Elapsed.TotalMilliseconds;
        Assert(contents.Hits.Count == 10000, "Benchmark content results incorrect");
        outcomes.Add(new { benchmark = true, files = 100000, bytes = scan.Root.Size, scanMilliseconds = scanMs,
            nameSearchMilliseconds = searchMs, contentSearchFiles = 10000, contentSearchMilliseconds = contentMs,
            scanMode = scan.Summary.Mode, peakWorkingSetBytes = Process.GetCurrentProcess().PeakWorkingSet64 });
        Console.WriteLine($"BENCHMARK files=100000 mode={scan.Summary.Mode} scan={scanMs:F1}ms name-search={searchMs:F1}ms content-search(10000)={contentMs:F1}ms");
    });
}

var artifactRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../artifacts"));
Directory.CreateDirectory(artifactRoot);
File.WriteAllText(Path.Combine(artifactRoot, args.Contains("--benchmark") ? "benchmark-checks.json" : "core-checks.json"), JsonSerializer.Serialize(new
{ timestamp = DateTimeOffset.Now, fixture, failed, results = outcomes }, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"Checks: {outcomes.Count} records, {failed} failed. Fixtures: {fixture}");
return failed == 0 ? 0 : 1;

internal sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}

internal sealed class FakeRecycler(Action? afterRecycle = null) : IFileRecycler
{
    internal List<string> Paths { get; } = [];
    internal int ValidationCalls { get; private set; }

    public Task RecycleAsync(string path, Action validateImmediatelyBeforeRecycle, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        validateImmediatelyBeforeRecycle();
        ValidationCalls++;
        Paths.Add(path);
        afterRecycle?.Invoke();
        return Task.CompletedTask;
    }
}

internal static class NativeCheckHelpers
{
    internal static bool TryCreateHardLink(string linkPath, string existingPath) =>
        CreateHardLink(linkPath, existingPath, IntPtr.Zero);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode,
        SetLastError = true, EntryPoint = "CreateHardLinkW")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string fileName, string existingFileName, IntPtr securityAttributes);
}
