using System.IO;

namespace TerminalNotes;

public partial class MainWindow
{
    private readonly List<ProjectNameBinding> _projectNames = [];
    private sealed record ProjectNameBinding(string Project, string Name);

    private void LoadProjectNames(IEnumerable<ProjectNameBinding>? bindings)
    {
        _projectNames.Clear();
        foreach (var binding in bindings ?? [])
        {
            if (binding is null || string.IsNullOrWhiteSpace(binding.Project) || string.IsNullOrWhiteSpace(binding.Name)) continue;
            try
            {
                if (!Path.IsPathFullyQualified(binding.Project)) continue;
                string project = CanonicalProjectPath(binding.Project);
                string name = binding.Name.Trim();
                if (name.Length == 0) continue;
                if (name.Length > 60) name = name[..60];
                _projectNames.RemoveAll(existing =>
                    string.Equals(existing.Project, project, StringComparison.OrdinalIgnoreCase));
                _projectNames.Add(new(project, name));
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
            {
                System.Diagnostics.Trace.WriteLine(ex);
            }
        }
    }

    private string ProjectDisplayName(string project)
    {
        string canonical = CanonicalProjectPath(project);
        var binding = _projectNames.LastOrDefault(item =>
            string.Equals(item.Project, canonical, StringComparison.OrdinalIgnoreCase));
        if (binding is not null) return binding.Name;
        return Path.GetFileName(canonical.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) is { Length: > 0 } leaf
            ? leaf
            : canonical;
    }

    private bool HasProjectName(string project)
    {
        string canonical = CanonicalProjectPath(project);
        return _projectNames.Any(item =>
            string.Equals(item.Project, canonical, StringComparison.OrdinalIgnoreCase));
    }

    private void RememberProjectName(string project, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        string canonical = CanonicalProjectPath(project);
        string normalized = name.Trim();
        if (normalized.Length == 0) throw new ArgumentException("프로젝트 이름을 입력하세요.", nameof(name));
        if (normalized.Length > 60) throw new ArgumentException("프로젝트 이름은 60자 이하여야 합니다.", nameof(name));
        string previousName = ProjectDisplayName(canonical);
        var updated = new List<TerminalTabState>();
        try
        {
            foreach (var tab in TerminalTabs.Items.Cast<System.Windows.Controls.TabItem>())
            {
                var open = (TerminalTabState)tab.Tag;
                if (open.IsIndependent) continue;
                if (!string.Equals(CanonicalProjectPath(open.Pane.WorkingDirectory), canonical, StringComparison.OrdinalIgnoreCase)) continue;
                open.Pane.UpdatePlanDocumentContext(open.PlanDocumentPath, normalized);
                updated.Add(open);
            }
        }
        catch
        {
            foreach (var open in updated)
            {
                try { open.Pane.UpdatePlanDocumentContext(open.PlanDocumentPath, previousName); }
                catch (Exception ex) { open.Pane.IsUserInputBlocked = true; System.Diagnostics.Trace.WriteLine(ex); }
            }
            throw;
        }
        _projectNames.RemoveAll(item =>
            string.Equals(item.Project, canonical, StringComparison.OrdinalIgnoreCase));
        _projectNames.Add(new(canonical, normalized));
        SaveSettings();
        if (IsDisplayedTaskProject(canonical) && _taskHeading is not null) _taskHeading.Text = normalized;
        if (_automationRuntimes.TryGetValue(canonical, out var runtime) &&
            runtime.SingleTerminal is { Tag: TerminalTabState single } singleTab && single.Title == previousName)
            SetTerminalTitle(singleTab, normalized);
        UpdateDispatcher();
        UpdateProjectTaskActivity();
    }
}
