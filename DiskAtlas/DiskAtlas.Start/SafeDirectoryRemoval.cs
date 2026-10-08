namespace DiskAtlas.Startup;

internal static class SafeDirectoryRemoval
{
    internal static void DeleteTreeWithoutFollowingReparsePoints(string root)
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        PathPolicy.RejectExistingReparsePoints(root);
        DeleteDirectory(root);
    }

    private static void DeleteDirectory(string directory)
    {
        foreach (string file in Directory.EnumerateFiles(directory))
        {
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException($"재분석 지점이 있어 폴더를 삭제하지 않았습니다: {file}");
            }

            File.SetAttributes(file, FileAttributes.Normal);
            File.Delete(file);
        }

        foreach (string child in Directory.EnumerateDirectories(directory))
        {
            if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException($"재분석 지점이 있어 폴더를 삭제하지 않았습니다: {child}");
            }

            DeleteDirectory(child);
        }

        Directory.Delete(directory, recursive: false);
    }
}
