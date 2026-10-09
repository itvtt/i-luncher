namespace DiskAtlas.Startup;

public static class PathPolicy
{
    public static string ResolveManifestPath(string payloadRoot, string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

        if (Path.IsPathRooted(relativePath) || relativePath.IndexOf('\0') >= 0)
        {
            throw new InvalidDataException($"안전하지 않은 패키지 경로입니다: {relativePath}");
        }

        string normalized = relativePath.Replace('/', Path.DirectorySeparatorChar);
        string[] parts = normalized.Split(Path.DirectorySeparatorChar);
        if (parts.Any(static part =>
                part.Length == 0 ||
                part is "." or ".." ||
                part.EndsWith(' ') ||
                part.EndsWith('.') ||
                part.IndexOf(':') >= 0 ||
                part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
        {
            throw new InvalidDataException($"안전하지 않은 패키지 경로입니다: {relativePath}");
        }

        string root = EnsureTrailingSeparator(Path.GetFullPath(payloadRoot));
        string candidate = Path.GetFullPath(Path.Combine(root, normalized));
        if (!candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"패키지 경로가 원본 폴더를 벗어납니다: {relativePath}");
        }

        return candidate;
    }

    public static void RejectExistingReparsePoints(string path)
    {
        string fullPath = Path.GetFullPath(path);
        string? root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrEmpty(root))
        {
            throw new InvalidDataException("경로 루트를 확인하지 못했습니다.");
        }

        string current = root;
        string remainder = fullPath[root.Length..];
        foreach (string component in remainder.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            if (!Directory.Exists(current) && !File.Exists(current))
            {
                continue;
            }

            FileAttributes attributes = File.GetAttributes(current);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException($"재분석 지점이 포함된 경로는 사용할 수 없습니다: {current}");
            }
        }
    }

    public static bool IsContainedBy(string candidate, string expectedRoot)
    {
        string root = EnsureTrailingSeparator(Path.GetFullPath(expectedRoot));
        string fullCandidate = Path.GetFullPath(candidate);
        return fullCandidate.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    private static string EnsureTrailingSeparator(string path) =>
        Path.EndsInDirectorySeparator(path) ? path : path + Path.DirectorySeparatorChar;
}
