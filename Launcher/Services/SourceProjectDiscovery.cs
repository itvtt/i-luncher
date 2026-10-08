using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace ILauncher.Services;

internal sealed record DiscoveredSourceProject(
    string AppDirectory,
    string ProjectPath,
    string Id,
    string Name,
    string AssemblyName);

internal sealed record SourceProjectDiscoveryResult(
    IReadOnlyList<DiscoveredSourceProject> Projects,
    IReadOnlyList<string> Errors);

internal static class SourceProjectDiscovery
{
    private const int MaximumProjectDepth = 4;
    private static readonly HashSet<string> ExcludedDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".vs", ".vscode", "bin", "obj", "node_modules", "artifacts", "dist",
        "test", "tests", "check", "checks", "launcher", "launcher.checks", "launcher.uichecks",
        "distribution", "tools"
    };

    internal static IReadOnlyList<string> GetAppDirectories(string rootDirectory)
    {
        var directories = new List<string>();
        foreach (string child in SafeChildDirectories(rootDirectory))
        {
            if (string.Equals(Path.GetFileName(child), "Projects", StringComparison.OrdinalIgnoreCase))
            {
                directories.AddRange(SafeChildDirectories(child)
                    .Where(projectDirectory => !IsExcludedDirectory(Path.GetFileName(projectDirectory))));
                continue;
            }
            if (!IsExcludedDirectory(Path.GetFileName(child))) directories.Add(child);
        }
        return directories.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    internal static SourceProjectDiscoveryResult Discover(string rootDirectory, IEnumerable<string> appDirectories)
    {
        var projects = new List<DiscoveredSourceProject>();
        var errors = new List<string>();
        foreach (string appDirectory in appDirectories)
        {
            if (File.Exists(Path.Combine(appDirectory, "launcher.app.json"))) continue;

            var projectFiles = FindProjects(appDirectory);
            var runnable = new List<(string Path, string AssemblyName)>();
            foreach (string projectPath in projectFiles)
            {
                try
                {
                    if (TryReadRunnableProject(projectPath, out string assemblyName))
                        runnable.Add((projectPath, assemblyName));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException
                    or InvalidOperationException or ArgumentException or NotSupportedException or System.Security.SecurityException)
                {
                    errors.Add($"{Path.GetFileName(appDirectory)}: cannot inspect {Path.GetRelativePath(appDirectory, projectPath)} ({ex.Message}); add launcher.app.json to configure it explicitly.");
                }
            }

            if (runnable.Count == 0) continue;
            if (runnable.Count > 1)
            {
                errors.Add($"{Path.GetRelativePath(rootDirectory, appDirectory)}: found {runnable.Count} runnable .NET projects; add launcher.app.json to select one.");
                continue;
            }

            string id = CreateId(rootDirectory, appDirectory);
            if (!IsValidId(id))
            {
                errors.Add($"{Path.GetRelativePath(rootDirectory, appDirectory)}: could not derive a safe app id; add launcher.app.json with an explicit id.");
                continue;
            }
            projects.Add(new DiscoveredSourceProject(appDirectory, runnable[0].Path, id,
                Path.GetFileName(appDirectory), runnable[0].AssemblyName));
        }
        return new SourceProjectDiscoveryResult(projects, errors);
    }

    internal static string CreateId(string rootDirectory, string appDirectory)
    {
        string folderName = Path.GetFileName(Path.TrimEndingDirectorySeparator(appDirectory));
        var slug = new StringBuilder(folderName.Length);
        bool separator = false;
        foreach (char character in folderName.ToLowerInvariant())
        {
            if (character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_')
            {
                if (separator && slug.Length > 0) slug.Append('-');
                slug.Append(character);
                separator = false;
            }
            else separator = true;
        }
        string value = slug.ToString().Trim('-', '_');
        if (value.Length > 0) return value;

        string relativePath = Path.GetRelativePath(rootDirectory, appDirectory).Replace('\\', '/').ToLowerInvariant();
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(relativePath))).ToLowerInvariant();
        return "app-" + hash[..12];
    }

    internal static bool IsValidId(string id) => id.Length is > 0 and <= 80
        && char.IsAsciiLetterOrDigit(id[0])
        && id.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_')
        && !IsReservedDeviceName(id);

    internal static string CreateSourceFingerprint(string appDirectory)
    {
        var records = GetMetadataRecords(EnumerateSourceFiles(appDirectory));
        return HashRecords(records);
    }

    internal static string? CreateBuildInputFingerprint(string rootDirectory, string appDirectory, string projectPath)
    {
        string root = NormalizeDirectory(rootDirectory);
        string app = Path.GetFullPath(appDirectory);
        string project = Path.GetFullPath(projectPath);
        if (!IsWithin(root, app) || !IsWithin(root, project) || !File.Exists(project)) return null;

        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pendingBuildFiles = new Queue<string>();
        var seenBuildFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        pendingBuildFiles.Enqueue(project);
        try
        {
            if (!AddSourceTree(root, app, files)) return null;
            EnqueueAncestorBuildInputs(root, app, files, pendingBuildFiles);
            int projectCount = 0;
            while (pendingBuildFiles.Count > 0)
            {
                string current = Path.GetFullPath(pendingBuildFiles.Dequeue());
                if (!seenBuildFiles.Add(current)) continue;
                if (++projectCount > 256 || !IsWithin(root, current) || !IsSafePath(root, current)) return null;
                files.Add(current);
                string projectDirectory = Path.GetDirectoryName(current)!;
                EnqueueAncestorBuildInputs(root, projectDirectory, files, pendingBuildFiles);
                if (current.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
                    && !AddSourceTree(root, projectDirectory, files)) return null;

                var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
                using XmlReader reader = XmlReader.Create(current, settings);
                var document = XDocument.Load(reader, LoadOptions.None);
                foreach (XElement import in document.Descendants()
                             .Where(element => element.Name.LocalName == "Import"))
                {
                    string? include = import.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName == "Project")?.Value;
                    if (string.IsNullOrWhiteSpace(include) || include.Contains("$(", StringComparison.Ordinal)
                        || include.Contains("@(", StringComparison.Ordinal)
                        || include.IndexOfAny(['*', '?']) >= 0) return null;
                    string importedFile = Path.GetFullPath(include, projectDirectory);
                    if (!IsWithin(root, importedFile) || !IsSafePath(root, importedFile) || !File.Exists(importedFile)) return null;
                    pendingBuildFiles.Enqueue(importedFile);
                }
                foreach (XElement reference in document.Descendants()
                             .Where(element => element.Name.LocalName == "ProjectReference"))
                {
                    string? include = reference.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName == "Include")?.Value;
                    if (string.IsNullOrWhiteSpace(include)) return null;
                    foreach (string includePath in include.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        if (includePath.Contains("$(", StringComparison.Ordinal) || includePath.Contains("@(", StringComparison.Ordinal)
                            || includePath.IndexOfAny(['*', '?']) >= 0) return null;
                        string referencedProject = Path.GetFullPath(includePath, projectDirectory);
                        if (!IsWithin(root, referencedProject) || !IsSafePath(root, referencedProject)
                            || !File.Exists(referencedProject)) return null;
                        pendingBuildFiles.Enqueue(referencedProject);
                    }
                }
            }
            return HashRecords(GetMetadataRecords(files));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException
            or ArgumentException or InvalidOperationException or NotSupportedException or System.Security.SecurityException)
        {
            return null;
        }
    }

    private static IEnumerable<string> FindProjects(string appDirectory)
    {
        var found = new List<string>();
        var pending = new Queue<(string Directory, int Depth)>();
        pending.Enqueue((appDirectory, 0));
        while (pending.Count > 0)
        {
            var (directory, depth) = pending.Dequeue();
            if (depth > 0 && IsExcludedDirectory(Path.GetFileName(directory))) continue;
            foreach (string project in Directory.EnumerateFiles(directory, "*.csproj", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    if ((File.GetAttributes(project) & FileAttributes.ReparsePoint) == 0) found.Add(project);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            if (depth >= MaximumProjectDepth) continue;
            foreach (string child in SafeChildDirectories(directory)) pending.Enqueue((child, depth + 1));
        }
        found.Sort(StringComparer.OrdinalIgnoreCase);
        return found;
    }

    private static bool TryReadRunnableProject(string projectPath, out string assemblyName)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
        using XmlReader reader = XmlReader.Create(projectPath, settings);
        var document = XDocument.Load(reader, LoadOptions.None);
        XElement? root = document.Root;
        if (root is null || root.Name.LocalName != "Project")
            throw new InvalidOperationException("the project file has no Project root element");

        string[] outputTypes = root.Descendants().Where(element => element.Name.LocalName == "OutputType")
            .Select(element => element.Value.Trim()).Where(value => value.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        bool runnable = outputTypes.Length > 0 && outputTypes.All(value =>
            value.Equals("Exe", StringComparison.OrdinalIgnoreCase) || value.Equals("WinExe", StringComparison.OrdinalIgnoreCase));
        assemblyName = root.Descendants().FirstOrDefault(element => element.Name.LocalName == "AssemblyName")?.Value.Trim()
            ?? Path.GetFileNameWithoutExtension(projectPath);
        if (assemblyName.Length == 0 || assemblyName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            assemblyName.Contains(Path.DirectorySeparatorChar) || assemblyName.Contains(Path.AltDirectorySeparatorChar)
            || assemblyName.Contains("$(", StringComparison.Ordinal) || assemblyName.Contains("@(", StringComparison.Ordinal))
            throw new InvalidOperationException("AssemblyName must be a simple file name");
        return runnable;
    }

    private static IEnumerable<string> EnumerateSourceFiles(string appDirectory)
    {
        var pending = new Stack<(string Directory, int Depth)>();
        pending.Push((appDirectory, 0));
        while (pending.Count > 0)
        {
            var (directory, depth) = pending.Pop();
            foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
            {
                bool isSafe = false;
                try
                {
                    isSafe = (File.GetAttributes(file) & FileAttributes.ReparsePoint) == 0;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                if (isSafe) yield return file;
            }
            if (depth >= 8) continue;
            foreach (string child in SafeChildDirectories(directory))
                if (!IsExcludedDirectory(Path.GetFileName(child))) pending.Push((child, depth + 1));
        }
    }

    private static bool AddSourceTree(string root, string directory, HashSet<string> files)
    {
        if (!IsWithin(root, directory) || !IsSafePath(root, directory)) return false;
        var pending = new Stack<(string Directory, int Depth)>();
        pending.Push((directory, 0));
        while (pending.Count > 0)
        {
            var (current, depth) = pending.Pop();
            foreach (string file in Directory.EnumerateFiles(current, "*", SearchOption.TopDirectoryOnly))
            {
                if (files.Count >= 100_000 || !IsSafePath(root, file)) return false;
                files.Add(file);
            }
            foreach (string child in Directory.EnumerateDirectories(current, "*", SearchOption.TopDirectoryOnly))
            {
                string name = Path.GetFileName(child);
                if (IsExcludedDirectory(name)) continue;
                if (!IsSafePath(root, child)) return false;
                if (depth >= 16) return false;
                pending.Push((child, depth + 1));
            }
        }
        return true;
    }

    private static void EnqueueAncestorBuildInputs(string root, string directory, HashSet<string> files,
        Queue<string> pendingBuildFiles)
    {
        string? current = Path.GetFullPath(directory);
        string[] knownNames = ["Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props", "global.json", "NuGet.Config", "nuget.config"];
        while (current is not null && IsWithinOrEqual(root, current))
        {
            foreach (string name in knownNames)
            {
                string candidate = Path.Combine(current, name);
                if (!File.Exists(candidate)) continue;
                if (!IsSafePath(root, candidate)) throw new IOException($"Build input contains a reparse point: {candidate}");
                files.Add(candidate);
                if (name.EndsWith(".props", StringComparison.OrdinalIgnoreCase)
                    || name.EndsWith(".targets", StringComparison.OrdinalIgnoreCase))
                    pendingBuildFiles.Enqueue(candidate);
            }
            if (current.Equals(root, StringComparison.OrdinalIgnoreCase)) break;
            current = Directory.GetParent(current)?.FullName;
        }
    }

    private static bool IsSafePath(string root, string path)
    {
        string fullPath = Path.GetFullPath(path);
        if (!IsWithinOrEqual(root, fullPath)) return false;
        string current = NormalizeDirectory(root);
        if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return false;
        string relative = Path.GetRelativePath(current, fullPath);
        foreach (string segment in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (segment is "" or ".") continue;
            current = Path.Combine(current, segment);
            if (!File.Exists(current) && !Directory.Exists(current)) return false;
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return false;
        }
        return true;
    }

    private static List<string> GetMetadataRecords(IEnumerable<string> sourceFiles)
    {
        var records = new List<string>();
        foreach (string file in sourceFiles)
        {
            var info = new FileInfo(file);
            records.Add($"{Path.GetFullPath(file).ToLowerInvariant()}\0{info.Length}\0{info.LastWriteTimeUtc.Ticks}");
        }
        records.Sort(StringComparer.Ordinal);
        return records;
    }

    private static string HashRecords(IEnumerable<string> records) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", records)))).ToLowerInvariant();

    private static bool IsWithin(string root, string path) => IsWithinOrEqual(root, path) &&
        !Path.GetFullPath(root).Equals(Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase);

    private static bool IsWithinOrEqual(string root, string path)
    {
        string normalizedRoot = NormalizeDirectory(root);
        string normalizedPath = NormalizeDirectory(path);
        return normalizedPath.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase)
            || normalizedPath.StartsWith(normalizedRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeDirectory(string directory)
    {
        string fullPath = Path.GetFullPath(directory);
        string root = Path.GetPathRoot(fullPath)!;
        return fullPath.Equals(root, StringComparison.OrdinalIgnoreCase)
            ? root : fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static IEnumerable<string> SafeChildDirectories(string directory)
    {
        foreach (string child in Directory.EnumerateDirectories(directory, "*", SearchOption.TopDirectoryOnly))
        {
            bool isSafe = false;
            try
            {
                isSafe = (File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            if (isSafe) yield return child;
        }
    }

    private static bool IsReservedDeviceName(string id) =>
        new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" }
            .Contains(id, StringComparer.OrdinalIgnoreCase);

    private static bool IsExcludedDirectory(string name) => ExcludedDirectoryNames.Contains(name)
        || name.EndsWith(".test", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".tests", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".check", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".checks", StringComparison.OrdinalIgnoreCase);
}
