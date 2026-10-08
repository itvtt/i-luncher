using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Directory = ILauncher.Bootstrapper.LongPathDirectory;
using File = ILauncher.Bootstrapper.LongPathFile;

namespace ILauncher.Bootstrapper
{
    internal static class SafeFiles
    {
        internal static string Sha256(byte[] bytes)
        {
            using (SHA256 sha = SHA256.Create())
                return ToHex(sha.ComputeHash(bytes));
        }

        internal static string Sha256File(string path)
        {
            using (SHA256 sha = SHA256.Create())
            using (FileStream stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                return ToHex(sha.ComputeHash(stream));
        }

        internal static string ToHex(byte[] bytes)
        {
            StringBuilder builder = new StringBuilder(bytes.Length * 2);
            foreach (byte value in bytes) builder.Append(value.ToString("x2"));
            return builder.ToString();
        }

        internal static bool IsHash(string value)
        {
            if (value == null || value.Length != 64) return false;
            foreach (char c in value)
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))
                    return false;
            return true;
        }

        internal static string CombineContained(string root, string relative, string description)
        {
            string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string full = Path.GetFullPath(Path.Combine(fullRoot, relative));
            string prefix = fullRoot + Path.DirectorySeparatorChar;
            if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(description + " escapes its allowed directory.");
            return full;
        }

        internal static void EnsureSafeDirectory(string root, string directory, bool create)
        {
            string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string fullDirectory = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!IsWithinOrEqual(fullRoot, fullDirectory))
                throw new InvalidDataException("Directory escaped its owned root.");
            EnsureExistingPathHasNoReparsePoints(fullRoot, create);
            EnsureExistingPathHasNoReparsePoints(fullDirectory, create);
        }

        internal static void EnsureExistingPathHasNoReparsePoints(string path, bool create)
        {
            string full = Path.GetFullPath(path);
            string root = Path.GetPathRoot(full);
            if (String.IsNullOrEmpty(root)) throw new InvalidDataException("Path has no filesystem root.");
            string current = root;
            string[] components = full.Substring(root.Length).Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < components.Length; i++)
            {
                current = Path.Combine(current, components[i]);
                if (!Directory.Exists(current) && !File.Exists(current))
                {
                    if (create) Directory.CreateDirectory(current);
                    else continue;
                }
                FileAttributes attributes = File.GetAttributes(current);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("A cache path contains a reparse point.");
                if (i < components.Length - 1 && (attributes & FileAttributes.Directory) == 0)
                    throw new InvalidDataException("A cache path parent is not a directory.");
            }
        }

        internal static bool IsWithinOrEqual(string root, string path)
        {
            string normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string normalizedPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return String.Equals(normalizedRoot, normalizedPath, StringComparison.OrdinalIgnoreCase) ||
                   normalizedPath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        internal static void DeleteOwnedStaging(string parent, string staging)
        {
            if (!Directory.Exists(staging)) return;
            string fullParent = Path.GetFullPath(parent);
            string fullStaging = Path.GetFullPath(staging);
            if (!IsWithinOrEqual(fullParent, fullStaging) || String.Equals(fullParent, fullStaging, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Refusing to remove a directory outside the owned staging parent.");
            EnsureTreeHasNoReparsePoints(fullStaging);
            DeleteTreeWithoutFollowingLinks(fullStaging);
        }

        internal static void EnsureTreeHasNoReparsePoints(string root)
        {
            Stack<string> pending = new Stack<string>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                string directory = pending.Pop();
                FileAttributes currentAttributes = File.GetAttributes(directory);
                if ((currentAttributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("A cache staging path contains a reparse point.");
                foreach (string entry in Directory.GetFileSystemEntries(directory))
                {
                    FileAttributes attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                        throw new InvalidDataException("A cache staging path contains a reparse point.");
                    if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
                }
            }
        }

        private static void DeleteTreeWithoutFollowingLinks(string root)
        {
            Stack<KeyValuePair<string, bool>> pending = new Stack<KeyValuePair<string, bool>>();
            pending.Push(new KeyValuePair<string, bool>(root, false));
            while (pending.Count > 0)
            {
                KeyValuePair<string, bool> item = pending.Pop();
                if (!Directory.Exists(item.Key) && !File.Exists(item.Key)) continue;
                FileAttributes attributes = File.GetAttributes(item.Key);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("A cache staging path contains a reparse point.");
                if ((attributes & FileAttributes.Directory) == 0)
                {
                    File.Delete(item.Key);
                    continue;
                }
                if (item.Value)
                {
                    Directory.Delete(item.Key, false);
                    continue;
                }
                pending.Push(new KeyValuePair<string, bool>(item.Key, true));
                string[] entries = Directory.GetFileSystemEntries(item.Key);
                for (int i = entries.Length - 1; i >= 0; i--)
                    pending.Push(new KeyValuePair<string, bool>(entries[i], false));
            }
        }

        internal static void WriteAtomic(string path, byte[] contents)
        {
            string directory = Path.GetDirectoryName(path);
            EnsureExistingPathHasNoReparsePoints(directory, true);
            EnsureExistingPathHasNoReparsePoints(path, false);
            string temporary = Path.Combine(directory, ".tmp-" + Guid.NewGuid().ToString("N"));
            try
            {
                using (FileStream stream = File.Open(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(contents, 0, contents.Length);
                    stream.Flush(true);
                }
                if (File.Exists(path))
                {
                    string backup = temporary + ".old";
                    try
                    {
                        File.Replace(temporary, path, backup, true);
                        if (File.Exists(backup)) File.Delete(backup);
                    }
                    catch
                    {
                        if (File.Exists(backup)) File.Delete(backup);
                        throw;
                    }
                }
                else File.Move(temporary, path);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
    }
}
