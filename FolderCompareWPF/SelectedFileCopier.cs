using System.IO;

namespace FolderCompareWPF;

internal static class SelectedFileCopier
{
    // Stage beside the destination so a failed read never truncates an existing file.
    public static void Copy(string rootA, string rootB, string relativePath)
    {
        string source = Resolve(rootA, relativePath);
        string destination = Resolve(rootB, relativePath);
        if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase))
            throw new IOException("A와 B가 같은 파일입니다.");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        string staging = Path.Combine(Path.GetDirectoryName(destination)!, ".foldercompare-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.Copy(source, staging, overwrite: false);
            File.Move(staging, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(staging))
            {
                File.SetAttributes(staging, File.GetAttributes(staging) & ~FileAttributes.ReadOnly);
                File.Delete(staging);
            }
        }
    }

    private static string Resolve(string root, string relativePath)
    {
        string fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        string path = Path.GetFullPath(Path.Combine(fullRoot, relativePath));
        string prefix = Path.EndsInDirectorySeparator(fullRoot) ? fullRoot : fullRoot + Path.DirectorySeparatorChar;
        if (Path.IsPathRooted(relativePath) || !path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new IOException("선택한 파일이 비교 폴더 밖에 있습니다.");
        for (string? current = path; current != null; current = Path.GetDirectoryName(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("링크 또는 연결된 폴더를 통한 복사는 지원하지 않습니다.");
        }
        return path;
    }
}
