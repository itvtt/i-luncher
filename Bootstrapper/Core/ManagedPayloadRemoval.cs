using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Directory = ILauncher.Bootstrapper.LongPathDirectory;
using File = ILauncher.Bootstrapper.LongPathFile;

namespace ILauncher.Bootstrapper
{
    // Shared by both managed installation stores. No registration is changed until
    // every selected tree has passed validation and has been moved reversibly.
    internal sealed class ManagedPayloadRemoval
    {
        internal static readonly SemaphoreSlim Gate = new SemaphoreSlim(1, 1);
        private readonly List<KeyValuePair<string, string>> _trees = new List<KeyValuePair<string, string>>();
        private readonly List<KeyValuePair<string, string>> _staged = new List<KeyValuePair<string, string>>();

        internal static async Task<FileStream> AcquireStoreLockAsync(string cacheRoot, CancellationToken token)
        {
            SafeFiles.EnsureExistingPathHasNoReparsePoints(cacheRoot, true);
            string path = SafeFiles.CombineContained(cacheRoot, ".managed-payload.lock", "Managed store operation lock");
            for (int attempt = 0; ; attempt++)
            {
                token.ThrowIfCancellationRequested();
                SafeFiles.EnsureExistingPathHasNoReparsePoints(path, false);
                try { return File.Open(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
                catch (IOException)
                {
                    if (attempt >= 100) throw new IOException("Another launcher is using this managed installation store. Retry shortly.");
                }
                await Task.Delay(100, token).ConfigureAwait(false);
            }
        }

        internal void Add(string ownedRoot, string directory, string protectedSource)
        {
            string root = Path.GetFullPath(ownedRoot), path = Path.GetFullPath(directory);
            if (!SafeFiles.IsWithinOrEqual(root, path) || String.Equals(root, path, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Full removal refused a directory outside its managed store.");
            SafeFiles.EnsureExistingPathHasNoReparsePoints(path, false);
            if (!Directory.Exists(path)) return;
            if (!String.IsNullOrEmpty(protectedSource) && SafeFiles.IsWithinOrEqual(path, Path.GetFullPath(protectedSource)))
                throw new InvalidDataException("Full removal cannot delete the source distribution. Remove its registration instead.");
            foreach (KeyValuePair<string, string> existing in _trees)
                if (String.Equals(existing.Value, path, StringComparison.OrdinalIgnoreCase)) return;
            Preflight(path);
            _trees.Add(new KeyValuePair<string, string>(root, path));
        }

        private static void Preflight(string root)
        {
            SafeFiles.EnsureExistingPathHasNoReparsePoints(root, false);
            SafeFiles.EnsureTreeHasNoReparsePoints(root);
            foreach (Process process in Process.GetProcesses())
                using (process)
                {
                    string executable = null;
                    try { ProcessModule module = process.MainModule; if (module != null) executable = module.FileName; }
                    catch (System.ComponentModel.Win32Exception) { }
                    catch (InvalidOperationException) { }
                    if (executable != null && SafeFiles.IsWithinOrEqual(root, executable))
                        throw new IOException("An app in the selected installation is running. Close it and retry full removal.");
                }
            var pending = new Stack<string>();
            pending.Push(root);
            while (pending.Count != 0)
                foreach (string entry in Directory.GetFileSystemEntries(pending.Pop()))
                {
                    FileAttributes attributes = File.GetAttributes(entry);
                    if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.ReadOnly)) != 0)
                        throw new IOException("A selected installation file is linked or read-only. Its installation was preserved.");
                    if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
                    else using (FileStream stream = File.Open(entry, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
                }
        }

        internal void Stage()
        {
            try
            {
                foreach (KeyValuePair<string, string> tree in _trees)
                {
                    Preflight(tree.Value);
                    string staging = tree.Value + ".removing-" + Guid.NewGuid().ToString("N");
                    SafeFiles.EnsureExistingPathHasNoReparsePoints(staging, false);
                    Directory.Move(tree.Value, staging);
                    _staged.Add(new KeyValuePair<string, string>(tree.Value, staging));
                }
            }
            catch { Rollback(); throw; }
        }

        internal void Rollback()
        {
            for (int i = _staged.Count - 1; i >= 0; i--)
            {
                KeyValuePair<string, string> tree = _staged[i];
                SafeFiles.EnsureExistingPathHasNoReparsePoints(tree.Key, false);
                SafeFiles.EnsureExistingPathHasNoReparsePoints(tree.Value, false);
                if (Directory.Exists(tree.Value)) Directory.Move(tree.Value, tree.Key);
            }
            _staged.Clear();
        }

        internal void Purge()
        {
            foreach (KeyValuePair<string, string> tree in _staged)
                try { SafeFiles.DeleteOwnedStaging(Path.GetDirectoryName(tree.Value), tree.Value); }
                catch (Exception error)
                {
                    throw new IOException("The app registration was removed, but cleanup is incomplete. Remaining managed files: " +
                        tree.Value + ". Close apps using these files and remove this remaining cache directory.", error);
                }
            _staged.Clear();
        }

        // A malformed or unknown record makes shared ownership uncertain, so retain
        // shared files. Scan BOTH native and older WPF registration stores.
        internal static bool HasOtherRegistrations(string cacheRoot, string selectedId, bool removesNativeRegistration = true)
        {
            try
            {
                foreach (string store in new[] { Path.Combine(cacheRoot, "installed"), Path.Combine(cacheRoot, "Apps") })
                {
                    SafeFiles.EnsureExistingPathHasNoReparsePoints(store, false);
                    if (!Directory.Exists(store)) continue;
                    foreach (string app in Directory.GetFileSystemEntries(store))
                    {
                        string id = Path.GetFileName(app);
                        if (id == "_shared-payloads") continue;
                        if (String.Equals(id, selectedId, StringComparison.OrdinalIgnoreCase) &&
                            (removesNativeRegistration || String.Equals(Path.GetFileName(store), "Apps", StringComparison.OrdinalIgnoreCase))) continue;
                        SafeFiles.EnsureExistingPathHasNoReparsePoints(app, false);
                        if (!Directory.Exists(app)) return true;
                        foreach (string receiptName in new[] { "current.receipt", "current.version" })
                        {
                            string receipt = Path.Combine(app, receiptName);
                            SafeFiles.EnsureExistingPathHasNoReparsePoints(receipt, false);
                            if (File.Exists(receipt)) return true;
                        }
                        // Unregistered payloads may still be used by a native launch.
                        foreach (string entry in Directory.GetFileSystemEntries(app))
                            if (Directory.Exists(entry)) return true;
                    }
                }
                return false;
            }
            catch { return true; }
        }

        internal static void AddVersions(ManagedPayloadRemoval removal, string store, string appId,
            bool native, string protectedSource)
        {
            SafeFiles.EnsureExistingPathHasNoReparsePoints(store, false);
            if (!Directory.Exists(store)) return;
            foreach (string version in Directory.GetFileSystemEntries(store))
            {
                string hash = Path.GetFileName(version);
                if (!SafeFiles.IsHash(hash)) continue; // Unknown files and user directories are preserved.
                SafeFiles.EnsureExistingPathHasNoReparsePoints(version, false);
                if (!Directory.Exists(version)) throw new InvalidDataException("A managed version directory was replaced by a file.");
                string marker = Path.Combine(version, ".ready");
                SafeFiles.EnsureExistingPathHasNoReparsePoints(marker, false);
                string text = File.Exists(marker) ? File.ReadAllText(marker, new UTF8Encoding(false, true)) : null;
                if (text != null)
                {
                    if (!native) continue; // WPF removal does not retire the corresponding native registration.
                    if (!String.Equals(text, appId + "\n" + hash + "\n\n", StringComparison.Ordinal))
                        throw new InvalidDataException("A managed payload ownership marker is invalid; full removal was refused.");
                }
                else
                {
                    marker = Path.Combine(version, ".install-complete");
                    SafeFiles.EnsureExistingPathHasNoReparsePoints(marker, false);
                    if (!File.Exists(marker) || File.ReadAllText(marker, new UTF8Encoding(false, true)) != "complete")
                        throw new InvalidDataException("A managed payload ownership marker is invalid; full removal was refused.");
                }
                removal.Add(store, version, protectedSource);
            }
        }

        internal static void AddRuntimes(ManagedPayloadRemoval removal, string cacheRoot, string protectedSource)
        {
            string store = Path.Combine(cacheRoot, "runtime");
            SafeFiles.EnsureExistingPathHasNoReparsePoints(store, false);
            if (!Directory.Exists(store)) return;
            foreach (string version in Directory.GetFileSystemEntries(store))
            {
                string hash = Path.GetFileName(version);
                if (!SafeFiles.IsHash(hash)) continue;
                SafeFiles.EnsureExistingPathHasNoReparsePoints(version, false);
                string marker = Path.Combine(version, ".ready");
                SafeFiles.EnsureExistingPathHasNoReparsePoints(marker, false);
                string[] lines = File.Exists(marker) ? File.ReadAllLines(marker, new UTF8Encoding(false, true)) : new string[0];
                if (lines.Length != 3 || lines[0] != "runtime" || lines[1] != hash || String.IsNullOrWhiteSpace(lines[2]))
                    continue; // Uncertain runtime ownership is retained.
                removal.Add(store, version, protectedSource);
            }
        }
    }
}
