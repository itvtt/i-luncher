namespace DiskAtlas.Core;

/// <summary>Central eligibility policy for search and search-result actions.</summary>
public static class SearchPolicy
{
    // FILE_ATTRIBUTE_RECALL_ON_OPEN, FILE_ATTRIBUTE_UNPINNED, and
    // FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS. The latter two values are not
    // available under every target framework as named FileAttributes values.
    private const FileAttributes CloudOrRecallAttributes =
        (FileAttributes)0x00040000 |
        (FileAttributes)0x00100000 |
        (FileAttributes)0x00400000;

    private const FileAttributes ExcludedAttributes =
        FileAttributes.System |
        FileAttributes.ReparsePoint |
        FileAttributes.Offline |
        CloudOrRecallAttributes;

    private static readonly HashSet<string> ProtectedRootDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "$Recycle.Bin", "System Volume Information", "Recovery", "$WinREAgent",
        "Windows", "Windows.old", "Program Files", "Program Files (x86)", "ProgramData",
        "PerfLogs", "Config.Msi", "MSOCache", "Documents and Settings", "EFI", "Boot", "ESD",
        "$WINDOWS.~BT", "$Windows.~WS"
    };

    private static readonly HashSet<string> ProtectedRootFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "pagefile.sys", "hiberfil.sys", "swapfile.sys", "DumpStack.log.tmp",
        "bootmgr", "BOOTNXT", "ntldr", "ntdetect.com", "bootstat.dat"
    };

    private static readonly string[] KnownOperatingSystemRoots = GetKnownOperatingSystemRoots();

    public static bool IsExcluded(ScanNode node)
    {
        ArgumentNullException.ThrowIfNull(node);

        // FullPath covers protected directory ancestry without walking and
        // normalizing every parent path for every file.
        if (IsProtectedPath(node.FullPath))
            return true;

        // Scanner trees are shallow. The high depth bound avoids allocating a
        // per-file visited set while still failing closed on a corrupt cycle.
        const int maximumParentDepth = 4096;
        ScanNode? current = node;
        for (int depth = 0; current is not null && depth < maximumParentDepth; depth++, current = current.Parent)
        {
            if ((current.Attributes & ExcludedAttributes) != 0)
                return true;
        }

        return current is not null;
    }

    private static bool IsProtectedPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        string normalized = NormalizePath(path);
        foreach (string protectedRoot in KnownOperatingSystemRoots)
        {
            if (IsSameOrDescendant(normalized, protectedRoot))
                return true;
        }

        string root;
        try
        {
            root = Path.GetPathRoot(normalized) ?? string.Empty;
        }
        catch
        {
            return false;
        }

        if (root.Length == 0 || normalized.Length <= root.Length)
            return false;

        string relative = normalized[root.Length..]
            .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (relative.Length == 0)
            return false;

        int separator = relative.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]);
        string firstSegment = separator < 0 ? relative : relative[..separator];

        // These directory names are special only immediately below a volume/share
        // root. A user folder such as Documents\Windows remains searchable.
        if (ProtectedRootDirectoryNames.Contains(firstSegment))
            return true;

        return separator < 0 && ProtectedRootFileNames.Contains(firstSegment);
    }

    private static string[] GetKnownOperatingSystemRoots()
    {
        Environment.SpecialFolder[] folders =
        [
            Environment.SpecialFolder.Windows,
            Environment.SpecialFolder.System,
            Environment.SpecialFolder.SystemX86,
            Environment.SpecialFolder.ProgramFiles,
            Environment.SpecialFolder.ProgramFilesX86,
            Environment.SpecialFolder.CommonApplicationData
        ];

        return folders
            .Select(Environment.GetFolderPath)
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Select(NormalizePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string NormalizePath(string path)
    {
        try
        {
            return Path.GetFullPath(path)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
    }

    private static bool IsSameOrDescendant(string path, string root)
    {
        if (path.Equals(root, StringComparison.OrdinalIgnoreCase))
            return true;

        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase) || path.Length <= root.Length)
            return false;

        char boundary = path[root.Length];
        return boundary == Path.DirectorySeparatorChar || boundary == Path.AltDirectorySeparatorChar;
    }
}
