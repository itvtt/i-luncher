using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Runtime.InteropServices;
using Directory = ILauncher.Bootstrapper.LongPathDirectory;
using File = ILauncher.Bootstrapper.LongPathFile;

namespace ILauncher.Bootstrapper
{
    internal sealed class LegacyManagedInstall
    {
        internal LegacyManagedInstall(string executablePath, string workingDirectory, string[] arguments,
            string shortcutPath, string runtimeDirectory, IDictionary<string, byte[]> registrationFiles)
        {
            ExecutablePath = executablePath;
            WorkingDirectory = workingDirectory;
            Arguments = arguments;
            ShortcutPath = shortcutPath;
            RuntimeDirectory = runtimeDirectory;
            RegistrationFiles = registrationFiles;
        }

        internal string ExecutablePath { get; private set; }
        internal string WorkingDirectory { get; private set; }
        internal string[] Arguments { get; private set; }
        internal string ShortcutPath { get; private set; }
        internal string RuntimeDirectory { get; private set; }
        internal IDictionary<string, byte[]> RegistrationFiles { get; private set; }
    }

    internal sealed class LegacyManagedInstallStore
    {
        private const int MaximumReceiptBytes = 4096;
        private const int MaximumShortcutBytes = 1024 * 1024;
        private const int MaximumIconBytes = 8 * 1024 * 1024;
        private const long MaximumSnapshotBytes = 12L * 1024L * 1024L;

        private readonly string _appsRoot;
        private readonly string _startMenuRoot;

        internal LegacyManagedInstallStore(BootstrapperOptions options)
        {
            if (options == null) throw new ArgumentNullException("options");
            _appsRoot = SafeFiles.CombineContained(options.CacheRoot, "Apps", "Legacy app store");
            _startMenuRoot = Path.GetFullPath(options.StartMenuRoot);
        }

        internal LegacyManagedInstall Find(BootstrapperApp app)
        {
            if (app == null) throw new ArgumentNullException("app");

            string executableRelativePath;
            string shortcutSuffix;
            string[] arguments;
            bool sharedHost;
            bool sharedExtraApp;
            if (!TryGetLegacyDefinition(app, out executableRelativePath, out shortcutSuffix,
                out arguments, out sharedHost, out sharedExtraApp))
                return null;

            SafeFiles.EnsureExistingPathHasNoReparsePoints(_appsRoot, false);
            if (!Directory.Exists(_appsRoot)) return null;

            string appDirectory = SafeFiles.CombineContained(_appsRoot, app.Id, "Legacy app receipt directory");
            SafeFiles.EnsureExistingPathHasNoReparsePoints(appDirectory, false);
            if (!Directory.Exists(appDirectory)) return null;

            string versionReceipt = SafeFiles.CombineContained(appDirectory, "current.version", "Legacy version receipt");
            SafeFiles.EnsureExistingPathHasNoReparsePoints(versionReceipt, false);
            if (!File.Exists(versionReceipt)) return null;
            string version = ReadSmallText(versionReceipt, MaximumReceiptBytes).Trim();
            if (!SafeFiles.IsHash(version))
                throw new InvalidDataException("A legacy install receipt contains an invalid version hash.");

            string portablePayloadDirectory = SafeFiles.CombineContained(appDirectory, version, "Legacy portable payload");
            bool useSharedExtraPayload = sharedExtraApp;
            if (sharedExtraApp)
            {
                SafeFiles.EnsureExistingPathHasNoReparsePoints(portablePayloadDirectory, false);
                useSharedExtraPayload = !Directory.Exists(portablePayloadDirectory);
            }
            string payloadRoot = sharedHost || useSharedExtraPayload
                ? SafeFiles.CombineContained(_appsRoot, "_shared-payloads", "Legacy shared payload store")
                : appDirectory;
            string versionDirectory = SafeFiles.CombineContained(payloadRoot, version, "Legacy app payload");
            SafeFiles.EnsureExistingPathHasNoReparsePoints(versionDirectory, false);
            if (!Directory.Exists(versionDirectory))
                throw new InvalidDataException("A legacy install receipt points to a missing payload.");

            string completionMarker = SafeFiles.CombineContained(versionDirectory, ".install-complete", "Legacy completion marker");
            SafeFiles.EnsureExistingPathHasNoReparsePoints(completionMarker, false);
            if (!File.Exists(completionMarker) || !String.Equals(ReadSmallText(completionMarker, MaximumReceiptBytes),
                "complete", StringComparison.Ordinal))
                throw new InvalidDataException("A legacy app payload is incomplete.");

            string executableRoot = useSharedExtraPayload
                ? ResolveSharedExtraAppDirectory(versionDirectory, app.Id, executableRelativePath)
                : versionDirectory;
            string executable = SafeFiles.CombineContained(executableRoot, executableRelativePath, "Legacy executable");
            SafeFiles.EnsureExistingPathHasNoReparsePoints(executable, false);
            if (!File.Exists(executable)) throw new InvalidDataException("A legacy app executable is missing.");
            string workingDirectory = sharedHost ? versionDirectory : Path.GetDirectoryName(executable);
            SafeFiles.EnsureExistingPathHasNoReparsePoints(workingDirectory, false);
            if (!Directory.Exists(workingDirectory)) throw new InvalidDataException("A legacy app working directory is missing.");

            string shortcutNameReceipt = SafeFiles.CombineContained(appDirectory, "shortcut.name", "Legacy shortcut receipt");
            SafeFiles.EnsureExistingPathHasNoReparsePoints(shortcutNameReceipt, false);
            if (!File.Exists(shortcutNameReceipt))
                throw new InvalidDataException("A legacy install receipt has no shortcut name record.");
            string recordedShortcutName = ReadSmallText(shortcutNameReceipt, MaximumReceiptBytes).Trim();
            if (!IsOwnedShortcutLeaf(recordedShortcutName, shortcutSuffix))
                throw new InvalidDataException("A legacy shortcut name record is not owned by this app.");

            SafeFiles.EnsureExistingPathHasNoReparsePoints(_startMenuRoot, false);
            if (!Directory.Exists(_startMenuRoot))
                throw new InvalidDataException("The legacy Start Menu directory is missing.");
            string shortcut = SafeFiles.CombineContained(_startMenuRoot, recordedShortcutName, "Legacy Start Menu shortcut");
            SafeFiles.EnsureExistingPathHasNoReparsePoints(shortcut, false);
            if (!File.Exists(shortcut)) throw new InvalidDataException("The legacy Start Menu shortcut is missing.");
            VerifyShortcut(shortcut, executable, workingDirectory, BuildCommandLine(arguments));

            var registrationFiles = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            long totalBytes = 0;
            AddSnapshot(registrationFiles, versionReceipt, MaximumReceiptBytes, ref totalBytes);
            AddSnapshot(registrationFiles, shortcutNameReceipt, MaximumReceiptBytes, ref totalBytes);
            AddSnapshot(registrationFiles, shortcut, MaximumShortcutBytes, ref totalBytes);
            foreach (string iconPath in Directory.GetFileSystemEntries(appDirectory))
                if (Path.GetFileName(iconPath).StartsWith("shortcut-icon.", StringComparison.OrdinalIgnoreCase)
                    && (File.Exists(iconPath) || Directory.Exists(iconPath)))
                {
                    ValidateOwnedIcon(iconPath);
                    AddSnapshot(registrationFiles, iconPath, MaximumIconBytes, ref totalBytes);
                }

            string runtimeDirectory = null;
            if (sharedHost || useSharedExtraPayload)
            {
                string candidate = SafeFiles.CombineContained(versionDirectory, "runtime", "Legacy sibling runtime");
                string dotnet = SafeFiles.CombineContained(candidate, "dotnet.exe", "Legacy runtime host");
                string hostFxr = SafeFiles.CombineContained(candidate, Path.Combine("host", "fxr"), "Legacy runtime hostfxr");
                if (Directory.Exists(candidate) && File.Exists(dotnet) && Directory.Exists(hostFxr))
                {
                    SafeFiles.EnsureExistingPathHasNoReparsePoints(dotnet, false);
                    SafeFiles.EnsureExistingPathHasNoReparsePoints(hostFxr, false);
                    runtimeDirectory = candidate;
                }
            }

            return new LegacyManagedInstall(executable, workingDirectory, arguments, shortcut, runtimeDirectory,
                registrationFiles);
        }

        private static bool TryGetLegacyDefinition(BootstrapperApp app, out string executableRelativePath,
            out string shortcutSuffix, out string[] arguments, out bool sharedHost, out bool sharedExtraApp)
        {
            executableRelativePath = null;
            shortcutSuffix = " (" + app.Id + ").lnk";
            arguments = null;
            sharedHost = false;
            sharedExtraApp = false;
            switch (app.Id)
            {
                case "folder-compare":
                    executableRelativePath = "i-luncher.exe";
                    arguments = new[] { "--app", app.Id }; sharedHost = true; return true;
                case "swift-copy":
                    executableRelativePath = "i-luncher.exe";
                    arguments = new[] { "--app", app.Id }; sharedHost = true; return true;
                case "disk-atlas":
                    executableRelativePath = "i-luncher.exe";
                    arguments = new[] { "--app", app.Id }; sharedHost = true; return true;
                case "deskcal":
                    executableRelativePath = "i-luncher.exe";
                    arguments = new[] { "--app", app.Id }; sharedHost = true; return true;
                case "terminalnotes":
                    executableRelativePath = SafeRelativeExecutable(app.Executable);
                    if (executableRelativePath == null) return false;
                    arguments = new string[0]; sharedExtraApp = true; return true;
                default:
                    executableRelativePath = SafeRelativeExecutable(app.Executable);
                    if (executableRelativePath == null) return false;
                    arguments = new string[0];
                    sharedExtraApp = true;
                    return true;
            }
        }

        private static string SafeRelativeExecutable(string executable)
        {
            if (String.IsNullOrWhiteSpace(executable) || Path.IsPathRooted(executable)
                || executable.IndexOf(':') >= 0 || executable.IndexOf('\0') >= 0)
                return null;
            string normalized = executable.Replace('/', '\\');
            string[] components = normalized.Split('\\');
            if (components.Length == 0 || !components[components.Length - 1].EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                return null;
            foreach (string component in components)
                if (component.Length == 0 || component == "." || component == "..") return null;
            return normalized;
        }

        private static string ResolveSharedExtraAppDirectory(string versionDirectory, string appId,
            string executableRelativePath)
        {
            string idDirectory = SafeFiles.CombineContained(versionDirectory, appId, "Legacy app payload directory");
            string idExecutable = SafeFiles.CombineContained(idDirectory, executableRelativePath, "Legacy app executable");
            SafeFiles.EnsureExistingPathHasNoReparsePoints(idDirectory, false);
            if (File.Exists(idExecutable)) return idDirectory;

            string match = null;
            foreach (string candidate in Directory.GetFileSystemEntries(versionDirectory))
            {
                if (!Directory.Exists(candidate)) continue;
                SafeFiles.EnsureExistingPathHasNoReparsePoints(candidate, false);
                string candidateExecutable = SafeFiles.CombineContained(candidate, executableRelativePath,
                    "Legacy app executable");
                if (!File.Exists(candidateExecutable)) continue;
                if (match != null)
                    throw new InvalidDataException("A legacy shared payload has multiple matching app directories.");
                match = candidate;
            }
            return match ?? idDirectory;
        }

        private static bool IsOwnedShortcutLeaf(string name, string suffix)
        {
            if (String.IsNullOrWhiteSpace(name) || !name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                || !String.Equals(Path.GetFileName(name), name, StringComparison.Ordinal))
                return false;
            return name.Length > suffix.Length;
        }

        private static string ReadSmallText(string path, int maximumBytes)
        {
            long length = File.Length(path);
            if (length < 0 || length > maximumBytes)
                throw new InvalidDataException("A legacy install receipt exceeds its size limit.");
            return new UTF8Encoding(false, true).GetString(File.ReadAllBytes(path));
        }

        private static void ValidateOwnedIcon(string path)
        {
            SafeFiles.EnsureExistingPathHasNoReparsePoints(path, false);
            if (Directory.Exists(path)) throw new InvalidDataException("A copied legacy icon is not a file.");
            string extension = Path.GetExtension(path);
            long length = File.Length(path);
            if (length > MaximumIconBytes) throw new InvalidDataException("A copied legacy icon exceeds its size limit.");
            byte[] signature = File.ReadAllBytes(path);
            bool valid = false;
            if (String.Equals(extension, ".ico", StringComparison.OrdinalIgnoreCase))
                valid = signature.Length >= 4 && signature[0] == 0 && signature[1] == 0 && signature[2] == 1 && signature[3] == 0;
            else if (String.Equals(extension, ".png", StringComparison.OrdinalIgnoreCase))
                valid = signature.Length >= 8 && signature[0] == 0x89 && signature[1] == 0x50 && signature[2] == 0x4e && signature[3] == 0x47
                    && signature[4] == 0x0d && signature[5] == 0x0a && signature[6] == 0x1a && signature[7] == 0x0a;
            else if (String.Equals(extension, ".bmp", StringComparison.OrdinalIgnoreCase))
                valid = signature.Length >= 2 && signature[0] == 0x42 && signature[1] == 0x4d;
            else
                throw new InvalidDataException("A copied legacy icon has an unsupported extension.");
            if (!valid) throw new InvalidDataException("A copied legacy icon has an invalid header.");
        }

        private static void AddSnapshot(IDictionary<string, byte[]> files, string path, int maximumBytes,
            ref long totalBytes)
        {
            SafeFiles.EnsureExistingPathHasNoReparsePoints(path, false);
            long length = File.Length(path);
            if (length < 0 || length > maximumBytes || totalBytes + length > MaximumSnapshotBytes)
                throw new InvalidDataException("Legacy install registration files exceed the snapshot size limit.");
            files.Add(Path.GetFullPath(path), File.ReadAllBytes(path));
            totalBytes += length;
        }

        private static void VerifyShortcut(string shortcutPath, string executable, string workingDirectory,
            string expectedArguments)
        {
            object shell = null;
            object shortcut = null;
            try
            {
                Type shellType = Type.GetTypeFromProgID("WScript.Shell", true);
                shell = Activator.CreateInstance(shellType);
                shortcut = shellType.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod,
                    null, shell, new object[] { shortcutPath });
                Type shortcutType = shortcut.GetType();
                string target = (string)shortcutType.InvokeMember("TargetPath",
                    System.Reflection.BindingFlags.GetProperty, null, shortcut, null);
                string working = (string)shortcutType.InvokeMember("WorkingDirectory",
                    System.Reflection.BindingFlags.GetProperty, null, shortcut, null);
                string arguments = (string)shortcutType.InvokeMember("Arguments",
                    System.Reflection.BindingFlags.GetProperty, null, shortcut, null);
                if (!Path.IsPathRooted(target) || !Path.IsPathRooted(working) ||
                    !String.Equals(Path.GetFullPath(target), Path.GetFullPath(executable), StringComparison.OrdinalIgnoreCase) ||
                    !String.Equals(TrimDirectory(Path.GetFullPath(working)), TrimDirectory(Path.GetFullPath(workingDirectory)),
                        StringComparison.OrdinalIgnoreCase) ||
                    !String.Equals(arguments ?? String.Empty, expectedArguments, StringComparison.Ordinal))
                    throw new InvalidDataException("A legacy shortcut does not match its app receipt.");
            }
            finally
            {
                if (shortcut != null && Marshal.IsComObject(shortcut)) Marshal.FinalReleaseComObject(shortcut);
                if (shell != null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
            }
        }

        private static string BuildCommandLine(string[] arguments)
        {
            var parts = new List<string>();
            foreach (string argument in arguments) parts.Add(QuoteArgument(argument));
            return String.Join(" ", parts.ToArray());
        }

        private static string QuoteArgument(string argument)
        {
            if (argument.Length > 0)
            {
                bool quote = false;
                for (int i = 0; i < argument.Length; i++)
                    if (Char.IsWhiteSpace(argument[i]) || argument[i] == '\"') { quote = true; break; }
                if (!quote) return argument;
            }

            var result = new StringBuilder();
            result.Append('"');
            int backslashes = 0;
            foreach (char character in argument)
            {
                if (character == '\\') { backslashes++; continue; }
                if (character == '\"') result.Append('\\', backslashes * 2 + 1).Append('\"');
                else result.Append('\\', backslashes).Append(character);
                backslashes = 0;
            }
            result.Append('\\', backslashes * 2).Append('"');
            return result.ToString();
        }

        private static string TrimDirectory(string path)
        {
            return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
    }
}
