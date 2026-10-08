using TerminalNotes.Services;

var root = Path.Combine(Path.GetTempPath(), "terminal-tab-doc-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    var store = new TerminalTabDocumentStore(Path.Combine(root, "project-a"));
    var otherProject = new TerminalTabDocumentStore(Path.Combine(root, "project-b"));

    var relativePlanProject = Path.Combine(root, "relative-plan-project");
    var relativePlanStore = new TerminalTabDocumentStore(relativePlanProject, Path.Combine("plans", "selected plan.md"));
    string expectedRelativePlanPath = TerminalTabDocumentStore.GetProjectPlanPath(relativePlanProject);
    Check(relativePlanStore.GetDocumentPath(1, TerminalInputMode.Plan) == expectedRelativePlanPath,
        "legacy custom Plan path resolves to the fixed project document");
    Check(relativePlanStore.GetDocumentPath(8, TerminalInputMode.Plan) == expectedRelativePlanPath,
        "fixed Plan path is independent of the terminal slot");
    Throws<ArgumentOutOfRangeException>(
        () => relativePlanStore.GetDocumentPath(0, TerminalInputMode.Plan),
        "fixed Plan path retains slot validation");
    Throws<ArgumentException>(
        () => relativePlanStore.GetDocumentPath(1, TerminalInputMode.Question),
        "fixed Plan path retains mode validation");
    Check(relativePlanStore.GetDocumentPath(1, TerminalInputMode.Build) ==
          Path.Combine(Path.GetFullPath(relativePlanProject), "terminal-notes", "tabs", "terminal-01", "build.md"),
        "fixed Plan path leaves other mode paths unchanged");
    var relativePlanSnapshot = await relativePlanStore.LoadAsync(1, TerminalInputMode.Plan);
    var relativePlanSaved = await relativePlanStore.SaveAsync(1, TerminalInputMode.Plan, "# Relative plan", relativePlanSnapshot);
    Check(relativePlanSaved.Exists && File.Exists(expectedRelativePlanPath),
        "Load and Save use the fixed project Plan path");
    DateTime expectedPlanTimestamp = new(2024, 2, 3, 4, 5, 6, DateTimeKind.Utc);
    File.SetLastWriteTimeUtc(expectedRelativePlanPath, expectedPlanTimestamp);
    expectedPlanTimestamp = File.GetLastWriteTimeUtc(expectedRelativePlanPath);
    var unchangedPlanSnapshot = await relativePlanStore.LoadAsync(1, TerminalInputMode.Plan);
    var unchangedPlanSave = await relativePlanStore.SaveAsync(
        1, TerminalInputMode.Plan, unchangedPlanSnapshot.Text, unchangedPlanSnapshot);
    Check(unchangedPlanSave.Text == "# Relative plan" &&
          File.GetLastWriteTimeUtc(expectedRelativePlanPath) == expectedPlanTimestamp,
        "saving unchanged populated Plan content preserves file contents and last-write timestamp");
    await File.WriteAllTextAsync(expectedRelativePlanPath, "external edit after no-op save", new System.Text.UTF8Encoding(false));
    await ThrowsAsync<IOException>(() => relativePlanStore.SaveAsync(
            1, TerminalInputMode.Plan, unchangedPlanSave.Text, unchangedPlanSave),
        "unchanged-save snapshot still detects an external Plan edit");
    Check(File.ReadAllText(expectedRelativePlanPath) == "external edit after no-op save",
        "unchanged-save conflict preserves the external Plan edit");
    FileAttributes originalPlanAttributes = File.GetAttributes(expectedRelativePlanPath);
    DateTime readOnlyPlanTimestamp = File.GetLastWriteTimeUtc(expectedRelativePlanPath);
    try
    {
        File.SetAttributes(expectedRelativePlanPath, originalPlanAttributes | FileAttributes.ReadOnly);
        var readOnlyPlanSnapshot = await relativePlanStore.LoadAsync(1, TerminalInputMode.Plan);
        var unchangedReadOnlySave = await relativePlanStore.SaveAsync(
            1, TerminalInputMode.Plan, readOnlyPlanSnapshot.Text, readOnlyPlanSnapshot);
        Check(unchangedReadOnlySave.Text == "external edit after no-op save" &&
              File.ReadAllText(expectedRelativePlanPath) == "external edit after no-op save" &&
              File.GetLastWriteTimeUtc(expectedRelativePlanPath) == readOnlyPlanTimestamp &&
              (File.GetAttributes(expectedRelativePlanPath) & FileAttributes.ReadOnly) != 0,
            "no-op Plan save preserves read-only contents, timestamp, and file attribute");
    }
    finally
    {
        if (File.Exists(expectedRelativePlanPath)) File.SetAttributes(expectedRelativePlanPath, originalPlanAttributes);
    }
    var invalidLegacyPathStore = new TerminalTabDocumentStore(relativePlanProject, Path.Combine("plans", "selected plan.txt"));
    Check(invalidLegacyPathStore.GetDocumentPath(1, TerminalInputMode.Plan) == expectedRelativePlanPath,
        "legacy custom path values do not affect the runtime Plan path");

    string unicodePlanPath = Path.Combine(root, "outside-project", "方案 - café 📘.MD");
    string customPlanProject = Path.Combine(root, "custom-plan-project");
    var customPlanStore = new TerminalTabDocumentStore(customPlanProject, unicodePlanPath);
    string canonicalCustomPlanPath = TerminalTabDocumentStore.GetProjectPlanPath(customPlanProject);
    Check(customPlanStore.GetDocumentPath(3, TerminalInputMode.Plan) == canonicalCustomPlanPath,
        "legacy absolute Unicode Plan path cannot move the project document outside its folder");
    var customPlanSnapshot = await customPlanStore.LoadAsync(3, TerminalInputMode.Plan);
    const string customPlanText = "# 새 계획\n\nUnicode path content";
    var customPlanSaved = await customPlanStore.SaveAsync(3, TerminalInputMode.Plan, customPlanText, customPlanSnapshot);
    Check(customPlanSaved.Exists && customPlanSaved.Path == canonicalCustomPlanPath && customPlanSaved.Text == customPlanText &&
          !File.Exists(unicodePlanPath),
        "Plan saves at the fixed project path regardless of a legacy binding");

    var reassignedPlanStore = new TerminalTabDocumentStore(Path.Combine(root, "custom-plan-project"),
        Path.Combine(root, "outside-project", "reassigned plan.md"));
    Check(reassignedPlanStore.GetDocumentPath(3, TerminalInputMode.Plan) == customPlanStore.GetDocumentPath(3, TerminalInputMode.Plan),
        "reassigning a terminal Plan path cannot change the fixed project document");
    var externallyChangedPlan = await customPlanStore.LoadAsync(3, TerminalInputMode.Plan);
    File.WriteAllText(externallyChangedPlan.Path, "external plan edit", new System.Text.UTF8Encoding(false));
    await ThrowsAsync<IOException>(() => customPlanStore.SaveAsync(3, TerminalInputMode.Plan, "my plan edit", externallyChangedPlan),
        "fixed Plan external change conflicts");
    Check(File.ReadAllText(externallyChangedPlan.Path) == "external plan edit",
        "fixed Plan conflict preserves the external edit");

    var build = await store.LoadAsync(1, TerminalInputMode.Build);
    var plan = await store.LoadAsync(1, TerminalInputMode.Plan);
    Check(plan.Path == TerminalTabDocumentStore.GetProjectPlanPath(Path.Combine(root, "project-a")),
        "default Plan has a stable project-level path");
    Check(plan.Path == store.GetDocumentPath(8, TerminalInputMode.Plan) &&
          plan.Path != otherProject.GetDocumentPath(1, TerminalInputMode.Plan),
        "Plan follows the project, independent of terminal number");
    var slotTwo = await store.LoadAsync(2, TerminalInputMode.Build);
    var other = await otherProject.LoadAsync(1, TerminalInputMode.Build);
    Check(new[] { build.Path, plan.Path, slotTwo.Path, other.Path }.Distinct(StringComparer.OrdinalIgnoreCase).Count() == 4,
        "project, slot, and mode paths are isolated");

    await ThrowsAsync<ArgumentException>(() => store.LoadAsync(1, TerminalInputMode.Question), "question load rejected");
    Check(!Directory.Exists(Path.Combine(root, "project-a")), "question rejection creates no disk data");
    var journal = new ProjectWorkJournal(Path.Combine(root, "question-project"));
    var question = await journal.SubmitAsync(TerminalInputMode.Question, "private question", 1);
    Check(question.Kind == JournalSubmissionKind.NotPersisted && !Directory.Exists(journal.JournalDirectory),
        "question journal submission is not persisted");

    var staleProjectPlan = await store.LoadAsync(8, TerminalInputMode.Plan);
    await store.SaveAsync(1, TerminalInputMode.Plan, "# Project plan", plan);
    Check((await store.LoadAsync(8, TerminalInputMode.Plan)).Text == "# Project plan",
        "another terminal continues the same saved project Plan");
    await ThrowsAsync<IOException>(() => store.SaveAsync(8, TerminalInputMode.Plan, "stale plan", staleProjectPlan),
        "stale same-project terminal cannot overwrite the shared Plan");

    const string unicode = "# 요구사항 🧪\n\n첫 번째 줄\n둘째 줄 — café";
    var saved = await store.SaveAsync(1, TerminalInputMode.Build, unicode, build);
    Check(saved.Exists && saved.Text == unicode, "Unicode multiline content saves and reloads");
    Check((await store.LoadAsync(1, TerminalInputMode.Build)).Text == unicode, "saved content reloads from disk");
    var cleared = await store.SaveAsync(1, TerminalInputMode.Build, "", saved);
    Check(cleared.Exists && cleared.Text == "" && (await store.LoadAsync(1, TerminalInputMode.Build)).Text == "",
        "clearing persists an empty file");

    var externalSnapshot = await store.LoadAsync(1, TerminalInputMode.Build);
    File.WriteAllText(externalSnapshot.Path, "external edit", new System.Text.UTF8Encoding(false));
    await ThrowsAsync<IOException>(() => store.SaveAsync(1, TerminalInputMode.Build, "my edit", externalSnapshot),
        "external change conflicts");
    Check(File.ReadAllText(externalSnapshot.Path) == "external edit", "external change remains intact");

    var deletedSnapshot = await store.LoadAsync(1, TerminalInputMode.Build);
    File.Delete(deletedSnapshot.Path);
    await ThrowsAsync<IOException>(() => store.SaveAsync(1, TerminalInputMode.Build, "my edit", deletedSnapshot),
        "external deletion conflicts");
    Check(!File.Exists(deletedSnapshot.Path), "external deletion remains intact");

    var first = await store.LoadAsync(2, TerminalInputMode.Review);
    var stale = await store.LoadAsync(2, TerminalInputMode.Review);
    await store.SaveAsync(2, TerminalInputMode.Review, "first writer", first);
    await ThrowsAsync<IOException>(() => store.SaveAsync(2, TerminalInputMode.Review, "stale writer", stale),
        "stale concurrent snapshot conflicts");
    Check((await store.LoadAsync(2, TerminalInputMode.Review)).Text == "first writer",
        "stale snapshot cannot overwrite current content");

    var promptSnapshot = await store.LoadAsync(2, TerminalInputMode.Review);
    var prompt = TerminalTabDocumentStore.BuildExecutionPrompt(promptSnapshot, TerminalInputMode.Review);
    await ThrowsAsync<ArgumentException>(
        () => Task.FromResult(TerminalTabDocumentStore.BuildExecutionPrompt(promptSnapshot, TerminalInputMode.Build)),
        "another mode cannot execute this document");
    Check(prompt.Contains(promptSnapshot.Path, StringComparison.Ordinal) &&
          !prompt.Contains(promptSnapshot.Text, StringComparison.Ordinal) &&
          prompt.Contains("검토", StringComparison.Ordinal), "mode-specific prompt points to MD path without inline content");
    await ThrowsAsync<InvalidOperationException>(
        () => Task.FromResult(TerminalTabDocumentStore.BuildExecutionPrompt(cleared, TerminalInputMode.Build)),
        "empty document cannot execute");

    Check(!prompt.Contains("terminal-inputs.md") && prompt.Contains("정상 구현을 다시 만들지 마세요"),
        "document execution preserves existing implementation and reads additional requests");
    var customPlanPrompt = TerminalTabDocumentStore.BuildExecutionPrompt(customPlanSaved, TerminalInputMode.Plan);
    Check(customPlanPrompt.Contains(customPlanSaved.Path, StringComparison.Ordinal) &&
          customPlanPrompt.Contains("검증", StringComparison.Ordinal),
        "Plan prompt uses the fixed project MD filename");

    Console.WriteLine("PASS: terminal tab document store behavior");
}
finally
{
    Directory.Delete(root, recursive: true);
}

static void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException("FAIL: " + name);
    Console.WriteLine("PASS: " + name);
}

static void Throws<T>(Action action, string name) where T : Exception
{
    try { action(); }
    catch (T) { Console.WriteLine("PASS: " + name); return; }
    throw new InvalidOperationException("FAIL: expected " + typeof(T).Name + " for " + name);
}

static async Task ThrowsAsync<T>(Func<Task> action, string name) where T : Exception
{
    try { await action(); }
    catch (T) { Console.WriteLine("PASS: " + name); return; }
    throw new InvalidOperationException("FAIL: expected " + typeof(T).Name + " for " + name);
}
