using System;
using System.IO;

namespace ILauncher.Bootstrapper
{
    internal static class LongPathFileSystem
    {
        internal static string ForIo(string path)
        {
            if (String.IsNullOrWhiteSpace(path)) throw new ArgumentException("A filesystem path is required.", "path");
            if (path.StartsWith("\\\\?\\", StringComparison.Ordinal) ||
                path.StartsWith("\\\\.\\", StringComparison.Ordinal) ||
                path.StartsWith("\\\\??\\", StringComparison.Ordinal))
                throw new InvalidDataException("Device paths are not accepted.");
            if (path.StartsWith("\\\\", StringComparison.Ordinal) ||
                (path.Length >= 3 && Char.IsLetter(path[0]) && path[1] == ':' && path[2] == '\\'))
                for (int i = 2; i < path.Length; i++)
                    if (path[i] == ':') throw new InvalidDataException("Alternate data stream paths are not accepted.");

            string full = Path.GetFullPath(path);
            if (full.StartsWith("\\\\?\\", StringComparison.Ordinal) ||
                full.StartsWith("\\\\.\\", StringComparison.Ordinal) ||
                full.StartsWith("\\\\??\\", StringComparison.Ordinal))
                throw new InvalidDataException("Device paths are not accepted.");

            bool unc = full.StartsWith("\\\\", StringComparison.Ordinal);
            if (!unc && (full.Length < 3 || !Char.IsLetter(full[0]) || full[1] != ':' || full[2] != '\\'))
                throw new InvalidDataException("Filesystem paths must be absolute Windows paths.");
            for (int i = 2; i < full.Length; i++)
                if (full[i] == ':') throw new InvalidDataException("Alternate data stream paths are not accepted.");

            if (full.Length < 248) return full;
            if (unc) return "\\\\?\\UNC\\" + full.Substring(2);
            return "\\\\?\\" + full;
        }

        internal static string Canonical(string path)
        {
            if (path.StartsWith("\\\\?\\UNC\\", StringComparison.OrdinalIgnoreCase))
                return "\\\\" + path.Substring(8);
            if (path.StartsWith("\\\\?\\", StringComparison.Ordinal))
                return path.Substring(4);
            return path;
        }
    }

    internal static class LongPathFile
    {
        internal static bool Exists(string path) { return File.Exists(LongPathFileSystem.ForIo(path)); }
        internal static FileAttributes GetAttributes(string path) { return File.GetAttributes(LongPathFileSystem.ForIo(path)); }
        internal static void Delete(string path) { File.Delete(LongPathFileSystem.ForIo(path)); }
        internal static void Move(string source, string destination) { File.Move(LongPathFileSystem.ForIo(source), LongPathFileSystem.ForIo(destination)); }
        internal static void Copy(string source, string destination, bool overwrite) { File.Copy(LongPathFileSystem.ForIo(source), LongPathFileSystem.ForIo(destination), overwrite); }
        internal static void Replace(string source, string destination, string backup, bool ignoreMetadataErrors)
        {
            File.Replace(LongPathFileSystem.ForIo(source), LongPathFileSystem.ForIo(destination),
                backup == null ? null : LongPathFileSystem.ForIo(backup), ignoreMetadataErrors);
        }
        internal static byte[] ReadAllBytes(string path) { return File.ReadAllBytes(LongPathFileSystem.ForIo(path)); }
        internal static string ReadAllText(string path, System.Text.Encoding encoding) { return File.ReadAllText(LongPathFileSystem.ForIo(path), encoding); }
        internal static string[] ReadAllLines(string path, System.Text.Encoding encoding) { return File.ReadAllLines(LongPathFileSystem.ForIo(path), encoding); }
        internal static void WriteAllBytes(string path, byte[] bytes) { File.WriteAllBytes(LongPathFileSystem.ForIo(path), bytes); }
        internal static void WriteAllText(string path, string contents, System.Text.Encoding encoding) { File.WriteAllText(LongPathFileSystem.ForIo(path), contents, encoding); }
        internal static FileStream Open(string path, FileMode mode, FileAccess access, FileShare share)
        {
            return new FileStream(LongPathFileSystem.ForIo(path), mode, access, share);
        }
        internal static long Length(string path) { return new FileInfo(LongPathFileSystem.ForIo(path)).Length; }
    }

    internal static class LongPathDirectory
    {
        internal static bool Exists(string path) { return Directory.Exists(LongPathFileSystem.ForIo(path)); }
        internal static void CreateDirectory(string path) { Directory.CreateDirectory(LongPathFileSystem.ForIo(path)); }
        internal static void Delete(string path, bool recursive) { Directory.Delete(LongPathFileSystem.ForIo(path), recursive); }
        internal static void Move(string source, string destination) { Directory.Move(LongPathFileSystem.ForIo(source), LongPathFileSystem.ForIo(destination)); }
        internal static string[] GetFileSystemEntries(string path)
        {
            string[] entries = Directory.GetFileSystemEntries(LongPathFileSystem.ForIo(path));
            for (int i = 0; i < entries.Length; i++) entries[i] = LongPathFileSystem.Canonical(entries[i]);
            return entries;
        }
    }
}
