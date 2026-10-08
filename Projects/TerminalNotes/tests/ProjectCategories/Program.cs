using System.Text.Json;
using TerminalNotes.Services;

string root = Path.Combine(Path.GetTempPath(), "TerminalNotes categories ", Guid.NewGuid().ToString("N"));
string workspace = Path.Combine(root, "workspace");
string otherWorkspace = Path.Combine(root, "other-workspace");
string project = Path.Combine(workspace, "project");
string movedProject = Path.Combine(workspace, "archive", "project");

var catalog = new ProjectCategoryCatalog();
Check(catalog.TryAddCategory(workspace, "  Work  "), "category names are trimmed");
Check(catalog.TryAddCategory(workspace, "alpha") && catalog.TryAddCategory(workspace, "Beta"), "categories can be created");
Check(!catalog.TryAddCategory(workspace, "WORK"), "duplicate names are rejected ignoring case");
Check(catalog.GetCategories(workspace).SequenceEqual(new[] { "alpha", "Beta", "Work" }), "category order is stable and case-insensitive");
Check(catalog.TryAddCategory(otherWorkspace, "Work"), "same category can exist in a separate workspace");
Check(catalog.Assign(workspace, project, "work"), "project can be assigned by case-insensitive category name");
Check(catalog.GetCategory(workspace, project) == "Work", "assignment returns canonical category name");
Check(catalog.Assign(otherWorkspace, project, "Work") && catalog.GetCategory(otherWorkspace, project) == "Work",
    "the same project can have an independent assignment in another workspace");
Check(catalog.TryRenameCategory(workspace, "WORK", "  Projects "), "category can be renamed and trimmed");
Check(catalog.GetCategory(workspace, project) == "Projects", "rename updates existing assignments");
Check(!catalog.TryRenameCategory(workspace, "Projects", "alpha"), "rename rejects an existing category name");
Check(catalog.RemoveCategory(workspace, "projects"), "category can be deleted ignoring case");
Check(catalog.GetCategory(workspace, project) is null, "delete clears assignments without touching project files");
Check(catalog.TryAddCategory(workspace, "Projects") && catalog.Assign(workspace, project, "Projects"), "category and assignment can be recreated");

ProjectCategoryWorkspaceState[] beforeMove = catalog.Snapshot();
string json = JsonSerializer.Serialize(beforeMove);
ProjectCategoryWorkspaceState[] fromJson = JsonSerializer.Deserialize<ProjectCategoryWorkspaceState[]>(json)!;
var reloaded = new ProjectCategoryCatalog(fromJson);
Check(reloaded.GetCategory(workspace, project) == "Projects", "snapshot serialization and reload preserve assignments");
Check(reloaded.Snapshot().Single(state =>
    StringComparer.OrdinalIgnoreCase.Equals(state.Workspace, Path.GetFullPath(workspace))).Workspace == Path.GetFullPath(workspace),
    "workspace paths are stored canonically");

catalog.MoveProject(project, movedProject);
Check(catalog.GetCategory(workspace, project) is null && catalog.GetCategory(workspace, movedProject) == "Projects",
    "archive path migration moves assignments");
Check(catalog.GetCategory(otherWorkspace, movedProject) == "Work", "archive path migration covers each workspace assignment map");

Check(!catalog.TryAddCategory(workspace, " "), "blank category names are rejected");
Check(!catalog.TryAddCategory(workspace, "a\nb"), "control characters are rejected");
Check(!catalog.TryAddCategory(workspace, new string('x', 65)), "names longer than 64 characters are rejected");
Check(!catalog.TryAddCategory(workspace, "미분류"), "reserved unclassified name is rejected");
Check(!catalog.Assign(workspace, project, "missing"), "assignment to an undefined category is rejected");

var malformed = new ProjectCategoryWorkspaceState[]
{
    null!,
    new(null!, null!, null!),
    new(workspace, new[] { " valid ", "VALID", "미분류", "", "bad\tname" }, new Dictionary<string, string>
    {
        [project] = "VALID",
        [Path.Combine(workspace, "orphan")] = "unknown"
    })
};
var defensive = new ProjectCategoryCatalog(malformed);
Check(defensive.GetCategories(workspace).SequenceEqual(new[] { "valid" }), "legacy malformed entries are filtered and duplicates are normalized");
Check(defensive.GetCategory(workspace, project) == "valid", "valid legacy assignments resolve to the canonical category");
Check(defensive.GetCategory(workspace, Path.Combine(workspace, "orphan")) is null, "dangling legacy assignments are ignored");

ProjectCategoryWorkspaceState[] snapshot = catalog.Snapshot();
ProjectCategoryWorkspaceState workspaceSnapshot = snapshot.Single(state =>
    StringComparer.OrdinalIgnoreCase.Equals(state.Workspace, Path.GetFullPath(workspace)));
workspaceSnapshot.Categories[0] = "changed";
workspaceSnapshot.Assignments.Clear();
Check(catalog.GetCategory(workspace, movedProject) == "Projects" && !catalog.GetCategories(workspace).Contains("changed"),
    "snapshot deeply copies categories and assignments");

Console.WriteLine("PROJECT CATEGORY TESTS PASSED");

static void Check(bool value, string message)
{
    if (!value) throw new Exception(message);
    Console.WriteLine("PASS " + message);
}
