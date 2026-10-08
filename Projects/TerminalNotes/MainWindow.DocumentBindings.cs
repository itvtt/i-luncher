using System.IO;
using TerminalNotes.Services;

namespace TerminalNotes;

public partial class MainWindow
{
    // A project owns its plan. Terminal numbers and OpenCode sessions can change.
    private readonly List<ProjectDocumentBinding> _documentBindings = [];

    private static string CanonicalProjectPath(string project) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(project));

    private static string DefaultProjectPlanPath(string project) =>
        TerminalTabDocumentStore.GetProjectPlanPath(CanonicalProjectPath(project));

    private bool IsProjectBoundToPlanPath(string project, string path) =>
        string.Equals(InitialPlanDocumentPath(1, project), Path.GetFullPath(path, CanonicalProjectPath(project)),
            StringComparison.OrdinalIgnoreCase);

    private static string CreateNewPlanDocumentPath(string project) => DefaultProjectPlanPath(project);

    private string InitialPlanDocumentPath(int slot, string project) => DefaultProjectPlanPath(project);

    private string EnsureProjectPlanDocument(string project)
    {
        string fullProject = CanonicalProjectPath(project);
        string canonicalPath = DefaultProjectPlanPath(fullProject);
        if (File.Exists(canonicalPath)) return canonicalPath;

        Directory.CreateDirectory(Path.GetDirectoryName(canonicalPath)!);
        string? persistedPath = _documentBindings.LastOrDefault(binding =>
            string.Equals(binding.Project, fullProject, StringComparison.OrdinalIgnoreCase))?.Path;
        var sources = new[] { persistedPath, FindLatestLegacyPlan(fullProject) }
            .Where(path => !string.IsNullOrWhiteSpace(path) &&
                !string.Equals(Path.GetFullPath(path!), canonicalPath, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (string? source in sources)
        {
            if (source is null || !File.Exists(source)) continue;
            try
            {
                InstallCanonicalPlan(canonicalPath, source);
                return canonicalPath;
            }
            catch (FileNotFoundException) { /* The legacy file was removed concurrently; try the next source. */ }
            catch (DirectoryNotFoundException) { /* The legacy directory was removed concurrently; try the next source. */ }
            catch (IOException) when (File.Exists(canonicalPath))
            {
                // Another selection created the canonical file first. Never replace it.
                return canonicalPath;
            }
        }

        InstallCanonicalPlan(canonicalPath, sourcePath: null);
        return canonicalPath;
    }

    private static void InstallCanonicalPlan(string canonicalPath, string? sourcePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(canonicalPath)!);
        string temporaryPath = canonicalPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            if (sourcePath is null)
            {
                using var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            }
            else
            {
                File.Copy(sourcePath, temporaryPath, overwrite: false);
            }

            try
            {
                File.Move(temporaryPath, canonicalPath, overwrite: false);
            }
            catch (IOException) when (File.Exists(canonicalPath))
            {
                // Atomic no-overwrite move makes concurrent project opens harmless.
            }
        }
        finally
        {
            try { File.Delete(temporaryPath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                System.Diagnostics.Trace.WriteLine(ex);
            }
        }
    }

    private static string? FindLatestLegacyPlan(string project)
    {
        var candidates = new List<(string Path, DateTime Modified)>();
        foreach (string folder in new[] { "plans", "tabs" })
        {
            string directory = Path.Combine(project, "terminal-notes", folder);
            try
            {
                if (!Directory.Exists(directory)) continue;
                foreach (string child in Directory.EnumerateDirectories(directory))
                {
                    string path = Path.Combine(child, "plan.md");
                    try
                    {
                        var file = new FileInfo(path);
                        if (file.Exists) candidates.Add((file.FullName, file.LastWriteTimeUtc));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return candidates.OrderByDescending(item => item.Modified)
            .ThenBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
            .Select(item => item.Path).FirstOrDefault();
    }

    private void LoadDocumentBindings(IEnumerable<ProjectDocumentBinding>? bindings)
    {
        _documentBindings.Clear();
        foreach (var binding in bindings ?? [])
        {
            if (binding is null ||
                string.IsNullOrWhiteSpace(binding.Project) || string.IsNullOrWhiteSpace(binding.Path)) continue;
            try
            {
                // Never reinterpret an old relative project against the app's startup directory.
                if (!Path.IsPathFullyQualified(binding.Project)) continue;
                string project = CanonicalProjectPath(binding.Project);
                string path = Path.GetFullPath(binding.Path, project);
                if (!string.Equals(Path.GetExtension(path), ".md", StringComparison.OrdinalIgnoreCase)) continue;
                // Older settings contain Slot too; JSON ignores it. Last explicit choice wins.
                _documentBindings.RemoveAll(existing =>
                    string.Equals(existing.Project, project, StringComparison.OrdinalIgnoreCase));
                _documentBindings.Add(new(project, path));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                System.Diagnostics.Trace.WriteLine(ex);
            }
        }
    }

    private void RememberPlanDocumentPath(TerminalTabState terminal, string? explicitPath)
    {
        if (terminal.IsIndependent) return;
        string project = CanonicalProjectPath(terminal.Pane.WorkingDirectory);
        _ = explicitPath; // Accepted for compatibility with older callers; the project plan path is fixed.
        string path = DefaultProjectPlanPath(project);
        _documentBindings.RemoveAll(binding =>
            string.Equals(binding.Project, project, StringComparison.OrdinalIgnoreCase));
        _documentBindings.Add(new(project, path));
        SaveSettings();
    }

    private sealed record ProjectDocumentBinding(string Project, string Path);
}
