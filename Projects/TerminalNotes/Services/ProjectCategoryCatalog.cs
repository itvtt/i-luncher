using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace TerminalNotes.Services;

public record ProjectCategoryWorkspaceState(
    string Workspace,
    string[] Categories,
    Dictionary<string, string> Assignments);

/// <summary>
/// In-memory category definitions and project assignments grouped by workspace.
/// The catalog does not read or write files; callers own persistence.
/// </summary>
public sealed class ProjectCategoryCatalog
{
    private const int MaximumCategoryNameLength = 64;
    private const string UnclassifiedCategory = "미분류";

    private static readonly StringComparer NameComparer = StringComparer.OrdinalIgnoreCase;
    private readonly Dictionary<string, WorkspaceData> _workspaces = new(StringComparer.OrdinalIgnoreCase);

    public ProjectCategoryCatalog(IEnumerable<ProjectCategoryWorkspaceState>? initialState = null)
    {
        if (initialState is null)
            return;

        foreach (ProjectCategoryWorkspaceState? state in initialState)
        {
            if (state is null || !TryCanonicalizePath(state.Workspace, out string workspace))
                continue;

            WorkspaceData data = GetOrCreateWorkspace(workspace);
            if (state.Categories is not null)
            {
                foreach (string? candidate in state.Categories)
                {
                    if (TryNormalizeCategory(candidate, out string name))
                        data.Categories.Add(name);
                }
            }

            if (state.Assignments is not null)
            {
                foreach (KeyValuePair<string, string>? pair in SafePairs(state.Assignments))
                {
                    if (pair is not { } assignment ||
                        !TryCanonicalizePath(assignment.Key, out string project) ||
                        !TryFindCategory(data, assignment.Value, out string category))
                    {
                        continue;
                    }

                    data.Assignments[project] = category;
                }
            }
        }
    }

    public IReadOnlyList<string> GetCategories(string workspace)
    {
        if (!TryGetWorkspace(workspace, out WorkspaceData data))
            return Array.Empty<string>();

        return Array.AsReadOnly(SortedCategories(data).ToArray());
    }

    public string? GetCategory(string workspace, string project)
    {
        if (!TryGetWorkspace(workspace, out WorkspaceData data) ||
            !TryCanonicalizePath(project, out string canonicalProject))
        {
            return null;
        }

        return data.Assignments.TryGetValue(canonicalProject, out string? category) ? category : null;
    }

    public bool TryAddCategory(string workspace, string name)
    {
        if (!TryGetOrCreateWorkspace(workspace, out WorkspaceData data) ||
            !TryNormalizeCategory(name, out string normalized) ||
            data.Categories.Contains(normalized))
        {
            return false;
        }

        data.Categories.Add(normalized);
        return true;
    }

    public bool TryRenameCategory(string workspace, string oldName, string newName)
    {
        if (!TryGetWorkspace(workspace, out WorkspaceData data) ||
            !TryFindCategory(data, oldName, out string existing) ||
            !TryNormalizeCategory(newName, out string normalized))
        {
            return false;
        }

        if (!NameComparer.Equals(existing, normalized) && data.Categories.Contains(normalized))
            return false;

        data.Categories.Remove(existing);
        data.Categories.Add(normalized);
        foreach (string project in data.Assignments.Keys.ToArray())
        {
            if (NameComparer.Equals(data.Assignments[project], existing))
                data.Assignments[project] = normalized;
        }

        return true;
    }

    public bool RemoveCategory(string workspace, string name)
    {
        if (!TryGetWorkspace(workspace, out WorkspaceData data) ||
            !TryFindCategory(data, name, out string existing))
        {
            return false;
        }

        data.Categories.Remove(existing);
        foreach (string project in data.Assignments.Keys.ToArray())
        {
            if (NameComparer.Equals(data.Assignments[project], existing))
                data.Assignments.Remove(project);
        }

        return true;
    }

    public bool Assign(string workspace, string project, string? category)
    {
        if (!TryGetOrCreateWorkspace(workspace, out WorkspaceData data) ||
            !TryCanonicalizePath(project, out string canonicalProject))
        {
            return false;
        }

        if (category is null)
        {
            data.Assignments.Remove(canonicalProject);
            return true;
        }

        if (!TryFindCategory(data, category, out string normalized))
            return false;

        data.Assignments[canonicalProject] = normalized;
        return true;
    }

    public void MoveProject(string source, string destination)
    {
        if (!TryCanonicalizePath(source, out string canonicalSource) ||
            !TryCanonicalizePath(destination, out string canonicalDestination) ||
            NameComparer.Equals(canonicalSource, canonicalDestination))
        {
            return;
        }

        foreach (WorkspaceData data in _workspaces.Values)
        {
            if (!data.Assignments.Remove(canonicalSource, out string? category))
                continue;

            // A destination assignment already represents the moved path and wins.
            data.Assignments.TryAdd(canonicalDestination, category);
        }
    }

    public ProjectCategoryWorkspaceState[] Snapshot()
    {
        return _workspaces
            .OrderBy(pair => pair.Key, NameComparer)
            .ThenBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new ProjectCategoryWorkspaceState(
                pair.Key,
                SortedCategories(pair.Value).ToArray(),
                new Dictionary<string, string>(pair.Value.Assignments, StringComparer.OrdinalIgnoreCase)))
            .ToArray();
    }

    private bool TryGetWorkspace(string? workspace, out WorkspaceData data)
    {
        if (TryCanonicalizePath(workspace, out string canonicalWorkspace) &&
            _workspaces.TryGetValue(canonicalWorkspace, out WorkspaceData? existing))
        {
            data = existing;
            return true;
        }

        data = null!;
        return false;
    }

    private bool TryGetOrCreateWorkspace(string? workspace, out WorkspaceData data)
    {
        if (!TryCanonicalizePath(workspace, out string canonicalWorkspace))
        {
            data = null!;
            return false;
        }

        data = GetOrCreateWorkspace(canonicalWorkspace);
        return true;
    }

    private WorkspaceData GetOrCreateWorkspace(string workspace)
    {
        if (!_workspaces.TryGetValue(workspace, out WorkspaceData? data) || data is null)
        {
            data = new WorkspaceData();
            _workspaces.Add(workspace, data);
        }

        return data;
    }

    private static IEnumerable<string> SortedCategories(WorkspaceData data) =>
        data.Categories.OrderBy(name => name, NameComparer).ThenBy(name => name, StringComparer.Ordinal);

    private static bool TryFindCategory(WorkspaceData data, string? name, out string category)
    {
        category = string.Empty;
        if (!TryNormalizeCategory(name, out string normalized))
            return false;

        category = data.Categories.FirstOrDefault(candidate => NameComparer.Equals(candidate, normalized)) ?? string.Empty;
        return category.Length > 0;
    }

    private static bool TryNormalizeCategory(string? candidate, out string normalized)
    {
        normalized = candidate?.Trim() ?? string.Empty;
        if (normalized.Length == 0 || normalized.Length > MaximumCategoryNameLength ||
            NameComparer.Equals(normalized, UnclassifiedCategory) ||
            normalized.Any(char.IsControl))
        {
            normalized = string.Empty;
            return false;
        }

        return true;
    }

    private static bool TryCanonicalizePath(string? path, out string canonicalPath)
    {
        canonicalPath = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
            return false;

        try
        {
            canonicalPath = Path.GetFullPath(path.Trim());
            return canonicalPath.Length > 0;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static IEnumerable<KeyValuePair<string, string>?> SafePairs(Dictionary<string, string> assignments)
    {
        try
        {
            return assignments.Select(pair => (KeyValuePair<string, string>?)pair).ToArray();
        }
        catch (InvalidOperationException)
        {
            return Array.Empty<KeyValuePair<string, string>?>();
        }
    }

    private sealed class WorkspaceData
    {
        public HashSet<string> Categories { get; } = new(NameComparer);
        public Dictionary<string, string> Assignments { get; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
