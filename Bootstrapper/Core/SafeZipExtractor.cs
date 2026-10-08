using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace ILauncher.Bootstrapper
{
    internal static class SafeZipExtractor
    {
        internal static void ExtractRuntime(string archivePath, string destination, string cacheRoot,
            BootstrapperOptions options, BootstrapperRuntime runtime, Action<long, long> progress)
        {
            Extract(archivePath, destination, cacheRoot, options, delegate(HashSet<string> files)
            {
                string hostFxr = "host/fxr/" + runtime.Version + "/hostfxr.dll";
                string coreClr = "shared/Microsoft.NETCore.App/" + runtime.Version + "/coreclr.dll";
                string desktopClr = "shared/Microsoft.WindowsDesktop.App/" + runtime.Version + "/PresentationFramework.dll";
                if (!files.Contains("dotnet.exe") || !files.Contains(hostFxr) || !files.Contains(coreClr) ||
                    !files.Contains(desktopClr))
                    throw new InvalidDataException("Runtime package is missing required host files.");
            }, progress);
        }

        internal static void ExtractApp(string archivePath, string destination, string cacheRoot,
            BootstrapperOptions options, BootstrapperApp app, Action<long, long> progress)
        {
            string executable = app.Executable.Replace('\\', '/');
            Extract(archivePath, destination, cacheRoot, options, delegate(HashSet<string> files)
            {
                if (!files.Contains(executable)) throw new InvalidDataException("App package is missing its executable.");
            }, progress);
        }

        private static void Extract(string archivePath, string destination, string cacheRoot,
            BootstrapperOptions options, Action<HashSet<string>> validate, Action<long, long> progress)
        {
            string fullCache = Path.GetFullPath(cacheRoot);
            string fullDestination = Path.GetFullPath(destination);
            if (!SafeFiles.IsWithinOrEqual(fullCache, fullDestination) ||
                String.Equals(fullCache, fullDestination, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Extraction destination is outside the cache root.");
            SafeFiles.EnsureSafeDirectory(fullCache, Path.GetDirectoryName(fullDestination), true);
            if (Directory.Exists(fullDestination) || File.Exists(fullDestination))
                throw new IOException("Cache extraction destination already exists.");
            Directory.CreateDirectory(fullDestination);
            SafeFiles.EnsureSafeDirectory(fullCache, fullDestination, false);

            HashSet<string> files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long declaredTotal = 0;
            long actualTotal = 0;
            using (FileStream stream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (ZipArchive archive = new ZipArchive(stream, ZipArchiveMode.Read, false))
            {
                if (archive.Entries.Count == 0 || archive.Entries.Count > options.MaximumArchiveEntries)
                    throw new InvalidDataException("Package ZIP entry count is outside the allowed range.");
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    string path = NormalizeEntryName(entry.FullName, options.MaximumPathLength);
                    if (path.Length == 0) continue;
                    bool isDirectory = IsDirectoryEntry(entry, path);
                    if (IsLinkOrSpecialFile(entry, isDirectory))
                        throw new InvalidDataException("Package ZIP contains a link or special file.");
                    RegisterPath(path, isDirectory, files, directories);
                    if (isDirectory) continue;
                    if (entry.Length < 0 || entry.Length > options.MaximumExpandedBytes ||
                        declaredTotal > options.MaximumExpandedBytes - entry.Length)
                        throw new InvalidDataException("Package ZIP expands beyond its allowed size.");
                    declaredTotal += entry.Length;
                }

                long completed = 0;
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    string path = NormalizeEntryName(entry.FullName, options.MaximumPathLength);
                    if (path.Length == 0) continue;
                    bool isDirectory = IsDirectoryEntry(entry, path);
                    string target = SafeFiles.CombineContained(fullDestination, path.Replace('/', Path.DirectorySeparatorChar),
                        "Package entry");
                    if (isDirectory)
                    {
                        SafeFiles.EnsureSafeDirectory(fullDestination, target, true);
                        continue;
                    }
                    string parent = Path.GetDirectoryName(target);
                    SafeFiles.EnsureSafeDirectory(fullDestination, parent, true);
                    using (Stream input = entry.Open())
                    using (FileStream output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        byte[] buffer = new byte[64 * 1024];
                        int read;
                        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            if (actualTotal > options.MaximumExpandedBytes - read)
                                throw new InvalidDataException("Package ZIP expands beyond its allowed size.");
                            output.Write(buffer, 0, read);
                            actualTotal += read;
                            completed += read;
                            if (progress != null) progress(completed, declaredTotal);
                        }
                        output.Flush(true);
                        if (output.Length != entry.Length)
                            throw new InvalidDataException("Package ZIP entry length does not match its directory record.");
                    }
                }
            }
            SafeFiles.EnsureTreeHasNoReparsePoints(fullDestination);
            validate(files);
        }

        private static string NormalizeEntryName(string value, int maximumLength)
        {
            if (String.IsNullOrEmpty(value) || value.Length > maximumLength || value.IndexOf('\0') >= 0 ||
                value.StartsWith("/", StringComparison.Ordinal) || value.StartsWith("\\", StringComparison.Ordinal) ||
                value.IndexOf(':') >= 0)
                throw new InvalidDataException("Package ZIP contains an unsafe path.");
            value = value.Replace('\\', '/');
            string path = value.EndsWith("/", StringComparison.Ordinal)
                ? value.Substring(0, value.Length - 1) : value;
            if (path.Length == 0) return String.Empty;
            string[] segments = path.Split('/');
            foreach (string segment in segments)
            {
                if (segment.Length == 0 || segment == "." || segment == ".." || segment.EndsWith(".", StringComparison.Ordinal) ||
                    segment.EndsWith(" ", StringComparison.Ordinal) || IsReserved(segment) ||
                    ContainsControlCharacter(segment))
                    throw new InvalidDataException("Package ZIP contains an unsafe path segment.");
            }
            return String.Join("/", segments);
        }

        private static bool IsDirectoryEntry(ZipArchiveEntry entry, string normalizedPath)
        {
            return entry.FullName.EndsWith("/", StringComparison.Ordinal) ||
                   (entry.ExternalAttributes & (int)FileAttributes.Directory) != 0;
        }

        private static bool IsLinkOrSpecialFile(ZipArchiveEntry entry, bool isDirectory)
        {
            int attributes = entry.ExternalAttributes;
            if ((attributes & (int)FileAttributes.ReparsePoint) != 0) return true;
            int unixType = (attributes >> 16) & 0xF000;
            if (unixType == 0) return false;
            return isDirectory ? unixType != 0x4000 : unixType != 0x8000;
        }

        private static void RegisterPath(string path, bool isDirectory, HashSet<string> files,
            HashSet<string> directories)
        {
            string[] parts = path.Split('/');
            string current = String.Empty;
            for (int i = 0; i < parts.Length; i++)
            {
                current = current.Length == 0 ? parts[i] : current + "/" + parts[i];
                bool final = i == parts.Length - 1;
                if (!final)
                {
                    if (files.Contains(current)) throw new InvalidDataException("Package ZIP has conflicting paths.");
                    directories.Add(current);
                }
                else if (isDirectory)
                {
                    if (files.Contains(current)) throw new InvalidDataException("Package ZIP has conflicting paths.");
                    directories.Add(current);
                }
                else
                {
                    if (files.Contains(current) || directories.Contains(current))
                        throw new InvalidDataException("Package ZIP has duplicate or conflicting paths.");
                    files.Add(current);
                }
            }
        }

        private static bool IsReserved(string segment)
        {
            string stem = segment.Split('.')[0];
            return new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
                "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" }
                .Contains(stem, StringComparer.OrdinalIgnoreCase);
        }

        private static bool ContainsControlCharacter(string value)
        {
            foreach (char c in value) if (Char.IsControl(c)) return true;
            return false;
        }
    }
}
