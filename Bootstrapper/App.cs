using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using ILauncher.Bootstrapper;

namespace ILauncher.BootstrapperUi
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            FrameworkCompatibility.Initialize();
            Stopwatch startup = Stopwatch.StartNew();
            try
            {
                BootstrapCatalog catalog = BootstrapCatalog.LoadEmbedded();
                if (args.Any(a => string.Equals(a, "--self-test", StringComparison.OrdinalIgnoreCase) ||
                                  string.Equals(a, "--smoke-only", StringComparison.OrdinalIgnoreCase)))
                {
                    NativeConsole.AttachParent();
                    NativeConsole.InitializeStreams();
                    BootstrapperOptions options = new BootstrapperOptions(catalog.Json, catalog.Sha256,
                        Path.Combine(Path.GetTempPath(), "iluncher-selftest-" + Guid.NewGuid().ToString("N")),
                        Application.ExecutablePath);
                    using (BootstrapperEngine testEngine = new BootstrapperEngine(options, NullBootstrapperCredentialProvider.Instance))
                    {
                        BootstrapperCatalog coreCatalog = testEngine.LoadCatalogAsync(CancellationToken.None).GetAwaiter().GetResult();
                        if (coreCatalog.Apps.Count != catalog.Apps.Count || coreCatalog.Version != catalog.Version)
                            throw new InvalidDataException("Core catalog validation did not match the embedded catalog.");
                    }
                    Console.WriteLine("Catalog OK: " + catalog.Version + "; .NET Framework bootstrapper; no network or app launch.");
                    return 0;
                }

                string appId = null;
                bool smokeUi = args.Any(a => string.Equals(a, "--smoke-ui", StringComparison.OrdinalIgnoreCase));
                bool smokeRealProfile = args.Any(a => string.Equals(a, "--smoke-real-profile", StringComparison.OrdinalIgnoreCase));
                string smokeInstalledAppId = GetArgument(args, "--smoke-installed-card");
                string reportPath = GetArgument(args, "--report-path");
                for (int i = 0; i + 1 < args.Length; i++)
                {
                    if (string.Equals(args[i], "--app", StringComparison.OrdinalIgnoreCase))
                    {
                        appId = args[i + 1];
                        break;
                    }
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                if (args.Any(a => string.Equals(a, "--smoke-card-styles", StringComparison.OrdinalIgnoreCase)))
                {
                    NativeConsole.AttachParent();
                    NativeConsole.InitializeStreams();
                    string previewPath = GetArgument(args, "--report-path");
                    if (string.IsNullOrWhiteSpace(previewPath))
                        throw new ArgumentException("--smoke-card-styles requires --report-path <png>.");
                    return RunCardStylePreview(catalog, previewPath);
                }
                if (args.Any(a => string.Equals(a, "--smoke-install-confirmation", StringComparison.OrdinalIgnoreCase)))
                {
                    NativeConsole.AttachParent();
                    NativeConsole.InitializeStreams();
                    string previewPath = GetArgument(args, "--report-path");
                    if (string.IsNullOrWhiteSpace(previewPath))
                        throw new ArgumentException("--smoke-install-confirmation requires --report-path <png>.");
                    return RunInstallConfirmationSmoke(catalog, previewPath);
                }
                if (smokeRealProfile && !smokeUi)
                    throw new ArgumentException("--smoke-real-profile requires --smoke-ui.");
                BootstrapperEngine engine = EngineFactory.Create(catalog, smokeUi && !smokeRealProfile, smokeRealProfile);
                if (!string.IsNullOrEmpty(appId))
                {
                    if (smokeUi)
                        throw new ArgumentException("App shortcuts cannot be launched in smoke mode.");
                    return RunAppShortcut(engine, appId);
                }
                if (smokeUi && string.IsNullOrWhiteSpace(reportPath))
                    throw new ArgumentException("--smoke-ui requires --report-path <file>.");
                if (!string.IsNullOrWhiteSpace(smokeInstalledAppId) && !smokeUi)
                    throw new ArgumentException("--smoke-installed-card requires --smoke-ui.");
                Application.Run(new LauncherForm(catalog, engine, smokeUi, smokeRealProfile, startup, reportPath, smokeInstalledAppId));
                return 0;
            }
            catch (Exception ex)
            {
                if (args.Any(a => string.Equals(a, "--self-test", StringComparison.OrdinalIgnoreCase) ||
                                  string.Equals(a, "--smoke-only", StringComparison.OrdinalIgnoreCase)))
                {
                    NativeConsole.AttachParent();
                    NativeConsole.InitializeStreams();
                    Console.Error.WriteLine(ShortError(ex));
                    return 1;
                }
                if (args.Any(a => string.Equals(a, "--smoke-ui", StringComparison.OrdinalIgnoreCase)))
                {
                    string reportPath = GetArgument(args, "--report-path");
                    try
                    {
                        if (!string.IsNullOrWhiteSpace(reportPath))
                        {
                            string json = new JavaScriptSerializer().Serialize(new Dictionary<string, object>
                            {
                                { "error", ShortError(ex) }, { "networkUsed", false }, { "appsLaunched", false }
                            });
                            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath)));
                            File.WriteAllText(reportPath, json, new UTF8Encoding(false));
                        }
                    }
                    catch { }
                    return 1;
                }
                if (args.Any(a => string.Equals(a, "--smoke-card-styles", StringComparison.OrdinalIgnoreCase)))
                {
                    NativeConsole.AttachParent();
                    NativeConsole.InitializeStreams();
                    Console.Error.WriteLine(ShortError(ex));
                    return 1;
                }
                if (args.Any(a => string.Equals(a, "--smoke-install-confirmation", StringComparison.OrdinalIgnoreCase)))
                {
                    NativeConsole.AttachParent();
                    NativeConsole.InitializeStreams();
                    Console.Error.WriteLine(ShortError(ex));
                    return 1;
                }
                MessageBox.Show(ShortError(ex), "i-luncher", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }

        private static string GetArgument(string[] args, string name)
        {
            for (int i = 0; i + 1 < args.Length; i++)
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
            return null;
        }

        private static int RunInstallConfirmationSmoke(BootstrapCatalog catalog, string previewPath)
        {
            BootstrapApp app = catalog.Apps.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, "deskcal", StringComparison.OrdinalIgnoreCase)) ?? catalog.Apps.First();
            using (Form owner = new Form
            {
                ShowInTaskbar = false,
                StartPosition = FormStartPosition.Manual,
                Location = new Point(-20000, -20000),
                Size = new Size(1, 1),
                Opacity = 0
            })
            using (InstallConfirmationDialog dialog = new InstallConfirmationDialog(app))
            using (System.Windows.Forms.Timer dismissTimer = new System.Windows.Forms.Timer { Interval = 100 })
            {
                bool shownAsOwnedModal = false;
                string previewError = null;
                dialog.Shown += delegate
                {
                    shownAsOwnedModal = dialog.Modal && dialog.Owner == owner &&
                        dialog.AcceptButton == dialog.CancelActionForSmoke &&
                        dialog.CancelButton == dialog.CancelActionForSmoke;
                    dismissTimer.Start();
                };
                dismissTimer.Tick += delegate
                {
                    dismissTimer.Stop();
                    try
                    {
                        string fullPath = Path.GetFullPath(previewPath);
                        Directory.CreateDirectory(Path.GetDirectoryName(fullPath));
                        using (Bitmap preview = new Bitmap(dialog.Width, dialog.Height))
                        {
                            dialog.DrawToBitmap(preview, dialog.ClientRectangle);
                            preview.Save(fullPath, ImageFormat.Png);
                            if (preview.GetPixel(preview.Width - 40, preview.Height / 2).ToArgb() !=
                                InstallConfirmationDialog.CardSurfaceForSmoke.ToArgb())
                                throw new InvalidOperationException("Install confirmation preview did not render its app surface.");
                        }
                    }
                    catch (Exception ex) { previewError = Program.ShortError(ex); }
                    finally { dialog.CancelActionForSmoke.PerformClick(); }
                };
                owner.Show();
                DialogResult result = dialog.ShowDialog(owner);
                if (!shownAsOwnedModal || result != DialogResult.Cancel ||
                    dialog.InstallActionResultForSmoke != DialogResult.Yes ||
                    dialog.IconAccessibleNameForSmoke != app.Name)
                    throw new InvalidOperationException("Install confirmation dialog failed its safe modal smoke check.");
                if (previewError != null) throw new InvalidOperationException("Install confirmation preview failed: " + previewError);
            }
            Console.WriteLine("Install confirmation smoke passed: owned modal, safe Enter/Escape defaults, accessible icon, no install action.");
            return 0;
        }

        private static int RunCardStylePreview(BootstrapCatalog catalog, string previewPath)
        {
            BootstrapApp app = catalog.Apps.First();
            using (Panel previewPanel = new Panel { Size = new Size(584, 120), BackColor = Color.FromArgb(11, 13, 18) })
            using (AppCard installed = new AppCard(app, 280, delegate { }, delegate { }, delegate { }, true))
            using (AppCard uninstalled = new AppCard(app, 280, delegate { }, delegate { }, delegate { }, true))
            using (AppCard legacyInstalled = new AppCard(app, 280, delegate { }, delegate { }, delegate { }, true))
            {
                BootstrapperApp coreApp = new BootstrapperApp(app.Id, app.Name, app.Description,
                    app.Category, null, null, app.Executable, null, app.IconBase64, app.Asset, app.Sha256, app.Bytes);
                installed.SetSmokeInstalledStatus();
                uninstalled.SetStatus(new BootstrapperAppStatus(coreApp, true, false,
                    null, null, "1.2.3", null, false));
                if (uninstalled.VersionTextForSmoke != "v1.2.3")
                    throw new InvalidOperationException("Uninstalled app did not retain its known available version label.");
                legacyInstalled.SetStatus(new BootstrapperAppStatus(coreApp, true, true,
                    "C:\\fake\\installed.exe", null, "1.2.3", null, false));
                if (legacyInstalled.VersionTextForSmoke != "버전 미확인")
                    throw new InvalidOperationException("Installed legacy receipt displayed an available version as its installed version.");
                installed.Location = new Point(8, 6);
                uninstalled.Location = new Point(296, 6);
                previewPanel.Controls.Add(installed);
                previewPanel.Controls.Add(uninstalled);
                previewPanel.PerformLayout();
                Application.DoEvents();
                using (Bitmap preview = new Bitmap(previewPanel.Width, previewPanel.Height))
                {
                    previewPanel.DrawToBitmap(preview, previewPanel.ClientRectangle);
                    DrawCardPreviewDetails(preview, installed);
                    DrawCardPreviewDetails(preview, uninstalled);
                    int installedOutlinePixels = CountRenderedOutlinePixels(preview, installed, false);
                    int uninstalledOutlinePixels = CountRenderedOutlinePixels(preview, uninstalled, false);
                    int installedFullCoveragePixels = CountRenderedOutlinePixels(preview, installed, true);
                    int uninstalledFullCoveragePixels = CountRenderedOutlinePixels(preview, uninstalled, true);
                    bool rendered = preview.GetPixel(88, 90) == installed.CardSurfaceColor
                        && preview.GetPixel(376, 90) == uninstalled.CardSurfaceColor
                        && installedOutlinePixels >= installed.Width / 2
                        && uninstalledOutlinePixels >= uninstalled.Width / 2
                        && installedFullCoveragePixels >= installed.Width / 2
                        && uninstalledFullCoveragePixels < uninstalled.Width / 2
                        && installed.CardOutlineThickness == 2F && uninstalled.CardOutlineThickness == 1F
                        && installed.LaunchChromeInset == 2F && uninstalled.LaunchChromeInset == 2F
                        && installed.LaunchChromeCornerRadius == 10F && uninstalled.LaunchChromeCornerRadius == 10F
                        && HasVisibleCardDetails(preview, installed)
                        && HasVisibleCardDetails(preview, uninstalled);
                    string fullPath = Path.GetFullPath(previewPath);
                    Directory.CreateDirectory(Path.GetDirectoryName(fullPath));
                    preview.Save(fullPath, ImageFormat.Png);
                    if (!rendered)
                        throw new InvalidOperationException("Card-state preview pixel check failed: installed surface=" + preview.GetPixel(88, 90) +
                            ", neutral surface=" + preview.GetPixel(376, 90) + ", installed outline pixels=" + installedOutlinePixels +
                            ", neutral outline pixels=" + uninstalledOutlinePixels + ", full-coverage installed/neutral=" +
                            installedFullCoveragePixels + "/" + uninstalledFullCoveragePixels + ", card details visible=" +
                            (HasVisibleCardDetails(preview, installed) && HasVisibleCardDetails(preview, uninstalled)) + ".");
                    Console.WriteLine("Card-state preview OK: " + fullPath);
                }
            }
            return 0;
        }

        internal static int CountRenderedOutlinePixels(Bitmap preview, AppCard card, bool fullCoverageOnly)
        {
            Color surface = card.CardSurfaceColor;
            Color outline = card.CardOutlineColor;
            int matchingPixels = 0;
            int startX = card.Left + 20;
            int endX = card.Left + card.Width - 20;
            for (int y = card.Top; y < card.Top + 4; y++)
            {
                for (int x = startX; x < endX; x++)
                {
                    Color pixel = preview.GetPixel(x, y);
                    if (TracksOutlineColor(pixel, surface, outline, fullCoverageOnly)) matchingPixels++;
                }
            }
            return matchingPixels;
        }

        private static bool TracksOutlineColor(Color pixel, Color surface, Color outline, bool fullCoverageOnly)
        {
            double redCoverage = ChannelCoverage(pixel.R, surface.R, outline.R);
            double greenCoverage = ChannelCoverage(pixel.G, surface.G, outline.G);
            double blueCoverage = ChannelCoverage(pixel.B, surface.B, outline.B);
            double minimum = Math.Min(redCoverage, Math.Min(greenCoverage, blueCoverage));
            double maximum = Math.Max(redCoverage, Math.Max(greenCoverage, blueCoverage));
            return minimum >= (fullCoverageOnly ? 0.85 : 0.30) && maximum <= 1.25 && maximum - minimum <= 0.30;
        }

        private static double ChannelCoverage(int pixel, int surface, int outline)
        {
            int delta = outline - surface;
            return delta == 0 ? 0 : (pixel - surface) / (double)delta;
        }

        private static bool HasVisibleCardDetails(Bitmap preview, AppCard card)
        {
            Rectangle icon = new Rectangle(card.Left + 12, card.Top + 20, 64, 64);
            Rectangle title = new Rectangle(card.Left + 92, card.Top + 10, 176, 22);
            Rectangle description = new Rectangle(card.Left + 92, card.Top + 34, 176, 32);
            Rectangle version = new Rectangle(card.Left + 92, card.Top + 72, 176, 24);
            return CountNonSurfacePixels(preview, icon, card.CardSurfaceColor) > 100 &&
                CountNonSurfacePixels(preview, title, card.CardSurfaceColor) > 4 &&
                CountNonSurfacePixels(preview, description, card.CardSurfaceColor) > 4 &&
                CountNonSurfacePixels(preview, version, card.CardSurfaceColor) > 4;
        }

        private static int CountNonSurfacePixels(Bitmap preview, Rectangle bounds, Color surface)
        {
            int count = 0;
            for (int y = bounds.Top; y < bounds.Bottom; y++)
            {
                for (int x = bounds.Left; x < bounds.Right; x++)
                {
                    Color pixel = preview.GetPixel(x, y);
                    if (Math.Abs(pixel.R - surface.R) + Math.Abs(pixel.G - surface.G) + Math.Abs(pixel.B - surface.B) > 36)
                        count++;
                }
            }
            return count;
        }

        private static void DrawCardPreviewDetails(Bitmap preview, AppCard card)
        {
            using (Graphics graphics = Graphics.FromImage(preview))
            {
                foreach (Control child in card.Controls.Cast<Control>().Where(control =>
                             control.Visible && (control is Label || control is PictureBox)))
                {
                    using (Bitmap renderedChild = new Bitmap(child.Width, child.Height))
                    {
                        child.DrawToBitmap(renderedChild, new Rectangle(Point.Empty, renderedChild.Size));
                        graphics.DrawImageUnscaled(renderedChild, card.Left + child.Left, card.Top + child.Top);
                    }
                }
            }
        }

        private static int RunAppShortcut(BootstrapperEngine engine, string appId)
        {
            try
            {
                engine.LaunchAsync(appId, CancellationToken.None).GetAwaiter().GetResult();
                return 0;
            }
            catch (Exception ex)
            {
                MessageBox.Show(ShortError(ex), "i-luncher", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }

        internal static string ShortError(Exception ex)
        {
            Exception current = ex;
            while (current.InnerException != null) current = current.InnerException;
            string message = (current.Message ?? "Unknown error.").Trim();
            if (message.Length > 400) message = message.Substring(0, 397) + "...";
            return message;
        }
    }

    internal static class NativeConsole
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool AttachConsole(uint processId);
        public static void AttachParent() { AttachConsole(UInt32.MaxValue); }
        public static void InitializeStreams()
        {
            try
            {
                Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
                Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });
            }
            catch { }
        }
    }

    internal sealed class BootstrapCatalog
    {
        public string Version { get; private set; }
        public string ServerUrl { get; private set; }
        public string ApiUrl { get; private set; }
        public string Repository { get; private set; }
        public string TagName { get; private set; }
        public long RuntimeBytes { get; private set; }
        public List<BootstrapApp> Apps { get; private set; }
        public string Json { get; private set; }
        public string Sha256 { get; private set; }

        private BootstrapCatalog() { }

        public static BootstrapCatalog LoadEmbedded()
        {
            Assembly assembly = Assembly.GetExecutingAssembly();
            byte[] catalogBytes = ReadResourceBytes(assembly, "ILauncher.Bootstrapper.catalog.json");
            string json = Encoding.UTF8.GetString(catalogBytes);
            string expected = ReadResource(assembly, "ILauncher.Bootstrapper.catalog.sha256").Trim();
            string actual;
            using (SHA256 sha = SHA256.Create()) actual = ToHex(sha.ComputeHash(catalogBytes));
            if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Embedded catalog integrity check failed. Rebuild the bootstrapper from a trusted catalog.");

            JavaScriptSerializer serializer = new JavaScriptSerializer();
            serializer.MaxJsonLength = 2 * 1024 * 1024;
            serializer.RecursionLimit = 64;
            Dictionary<string, object> root = serializer.Deserialize<Dictionary<string, object>>(json);
            if (GetLong(root, "schemaVersion") != 1) throw new InvalidDataException("Unsupported catalog schema version.");
            Dictionary<string, object> source = GetObject(root, "source");
            Dictionary<string, object> runtime = GetObject(root, "runtime");
            string runtimeAsset = Required(runtime, "asset");
            string runtimeHash = Required(runtime, "sha256");
            if (!IsAssetName(runtimeAsset) || !IsSha256(runtimeHash) || GetLong(runtime, "bytes") <= 0 ||
                string.IsNullOrWhiteSpace(Required(runtime, "version")))
                throw new InvalidDataException("Catalog runtime metadata is invalid.");
            List<BootstrapApp> apps = new List<BootstrapApp>();
            System.Collections.IList appRows = root.ContainsKey("apps") ? root["apps"] as System.Collections.IList : null;
            if (appRows == null || appRows.Count == 0 || appRows.Count > 100) throw new InvalidDataException("Catalog app list is empty or too large.");
            HashSet<string> ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (object row in appRows)
            {
                Dictionary<string, object> item = row as Dictionary<string, object>;
                if (item == null) throw new InvalidDataException("Catalog contains an invalid app entry.");
                BootstrapApp app = new BootstrapApp
                {
                    Id = Required(item, "id"), Name = Required(item, "name"),
                    Description = Optional(item, "description"), Category = Optional(item, "category"),
                    Executable = Required(item, "executable"), IconBase64 = Optional(item, "iconBase64"),
                    Asset = Required(item, "asset"), Sha256 = Required(item, "sha256"), Bytes = GetLong(item, "bytes")
                };
                if (!IsSafeId(app.Id) || !ids.Add(app.Id)) throw new InvalidDataException("Catalog contains an invalid or duplicate app id: " + app.Id);
                if (!IsAssetName(app.Asset) || !IsRelativeExecutable(app.Executable) || !IsSha256(app.Sha256) || app.Bytes <= 0)
                    throw new InvalidDataException("Catalog app metadata is invalid: " + app.Id);
                apps.Add(app);
            }
            string server = Required(source, "serverUrl");
            string api = Required(source, "apiUrl");
            Uri serverUri, apiUri;
            if (!Uri.TryCreate(server, UriKind.Absolute, out serverUri) || !Uri.TryCreate(api, UriKind.Absolute, out apiUri) ||
                serverUri.Scheme != Uri.UriSchemeHttps || apiUri.Scheme != Uri.UriSchemeHttps ||
                !string.IsNullOrEmpty(serverUri.UserInfo) || !string.IsNullOrEmpty(apiUri.UserInfo))
                throw new InvalidDataException("Catalog server or API URL is invalid.");
            return new BootstrapCatalog
            {
                Version = Required(root, "version"), ServerUrl = server, ApiUrl = api,
                Repository = Required(source, "repository"), TagName = Required(source, "tagName"), RuntimeBytes = GetLong(runtime, "bytes"),
                Apps = apps, Json = json, Sha256 = actual
            };
        }

        private static string ReadResource(Assembly assembly, string name)
        {
            using (Stream stream = assembly.GetManifestResourceStream(name))
            {
                if (stream == null) throw new InvalidDataException("Missing embedded bootstrap resource: " + name);
                using (StreamReader reader = new StreamReader(stream, Encoding.UTF8, true)) return reader.ReadToEnd();
            }
        }
        private static byte[] ReadResourceBytes(Assembly assembly, string name)
        {
            using (Stream stream = assembly.GetManifestResourceStream(name))
            {
                if (stream == null) throw new InvalidDataException("Missing embedded bootstrap resource: " + name);
                using (MemoryStream copy = new MemoryStream())
                {
                    stream.CopyTo(copy);
                    if (copy.Length > 2 * 1024 * 1024) throw new InvalidDataException("Embedded catalog exceeds the 2 MiB limit.");
                    return copy.ToArray();
                }
            }
        }
        private static Dictionary<string, object> GetObject(Dictionary<string, object> source, string name)
        {
            object value;
            Dictionary<string, object> result;
            if (!source.TryGetValue(name, out value) || (result = value as Dictionary<string, object>) == null)
                throw new InvalidDataException("Catalog is missing " + name + ".");
            return result;
        }
        private static string Required(Dictionary<string, object> source, string key)
        {
            string value = Optional(source, key);
            if (string.IsNullOrWhiteSpace(value)) throw new InvalidDataException("Catalog is missing " + key + ".");
            return value;
        }
        private static string Optional(Dictionary<string, object> source, string key)
        {
            object value;
            return source.TryGetValue(key, out value) && value != null ? Convert.ToString(value) : "";
        }
        private static long GetLong(Dictionary<string, object> source, string key)
        {
            object value;
            long parsed;
            if (!source.TryGetValue(key, out value) || !long.TryParse(Convert.ToString(value), out parsed))
                throw new InvalidDataException("Catalog is missing or has an invalid " + key + ".");
            return parsed;
        }
        private static bool IsSafeId(string value)
        {
            return value != null && System.Text.RegularExpressions.Regex.IsMatch(value, "^[a-z0-9][a-z0-9._-]{0,63}$") &&
                !value.EndsWith(".", StringComparison.Ordinal) && !IsReservedDeviceName(value);
        }
        private static bool IsReservedDeviceName(string value)
        {
            string name = value.Split('.')[0];
            return new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" }
                .Contains(name, StringComparer.OrdinalIgnoreCase);
        }
        private static bool IsAssetName(string value)
        {
            return !string.IsNullOrWhiteSpace(value) && value.Length <= 128 &&
                System.Text.RegularExpressions.Regex.IsMatch(value, "^[A-Za-z0-9][A-Za-z0-9._-]*\\.zip$");
        }
        private static bool IsRelativeExecutable(string value)
        {
            return !string.IsNullOrWhiteSpace(value) && value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                !Path.IsPathRooted(value) && value.IndexOf(':') < 0 && value.IndexOf('\\') < 0 &&
                value.Split('/').All(part => part.Length > 0 && part != "." && part != ".." && !part.EndsWith(".") && !part.EndsWith(" "));
        }
        private static bool IsSha256(string value) { return value != null && System.Text.RegularExpressions.Regex.IsMatch(value, "^[0-9a-fA-F]{64}$"); }
        private static string ToHex(byte[] bytes) { return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant(); }
    }

    internal sealed class BootstrapApp
    {
        public string Id, Name, Description, Category, Executable, IconBase64, Asset, Sha256;
        public long Bytes;
    }

    internal static class EngineFactory
    {
        public static UserCredentialProvider Credentials { get; private set; }
        public static BootstrapperOptions Options { get; private set; }
        public static BootstrapperEngine Create(BootstrapCatalog catalog, bool smokeOnly, bool readOnlyStatus)
        {
            string cacheRoot = smokeOnly
                ? Path.Combine(Path.GetTempPath(), "iluncher-smoke-" + Guid.NewGuid().ToString("N"))
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "i-luncher");
            BootstrapSettings settings = smokeOnly ? new BootstrapSettings() : BootstrapSettings.Load();
            BootstrapperOptions options = new BootstrapperOptions(
                catalog.Json, catalog.Sha256, cacheRoot,
                Application.ExecutablePath)
            {
                PackageMirrorBase = settings.MirrorBase,
                StartMenuRoot = smokeOnly
                    ? Path.Combine(cacheRoot, "StartMenu")
                    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "i-luncher"),
                ReadOnlyStatus = readOnlyStatus
            };
            Options = options;
            Credentials = new UserCredentialProvider(settings);
            return new BootstrapperEngine(options, Credentials);
        }
    }

    internal sealed class UserCredentialProvider : IBootstrapperCredentialProvider
    {
        private string _token;
        public UserCredentialProvider(BootstrapSettings settings) { _token = settings.Token; }
        public string GetToken(Uri apiOrigin) { return _token; }
        public void Update(BootstrapSettings settings) { _token = settings.Token; }
    }
    internal sealed class BootstrapSettings
    {
        public string MirrorBase = "";
        public string Token = "";
        public bool RememberToken;
        private static string FilePath { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "i-luncher", "bootstrapper-settings.bin"); } }
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("i-luncher bootstrapper local credential v1");
        public static BootstrapSettings Load()
        {
            try
            {
                byte[] encrypted = File.ReadAllBytes(FilePath);
                byte[] plain = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
                JavaScriptSerializer serializer = new JavaScriptSerializer();
                Dictionary<string, object> saved = serializer.Deserialize<Dictionary<string, object>>(Encoding.UTF8.GetString(plain));
                if (saved == null) return new BootstrapSettings();
                object mirror, token, remember;
                return new BootstrapSettings
                {
                    MirrorBase = saved.TryGetValue("mirrorBase", out mirror) ? Convert.ToString(mirror) : "",
                    Token = saved.TryGetValue("token", out token) ? Convert.ToString(token) : "",
                    RememberToken = saved.TryGetValue("rememberToken", out remember) && Convert.ToBoolean(remember)
                };
            }
            catch { return new BootstrapSettings(); }
        }
        public void Save()
        {
            string directory = Path.GetDirectoryName(FilePath);
            Directory.CreateDirectory(directory);
            Dictionary<string, object> values = new Dictionary<string, object>
            {
                { "mirrorBase", MirrorBase ?? "" }, { "token", Token ?? "" }, { "rememberToken", RememberToken }
            };
            byte[] encrypted = ProtectedData.Protect(Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(values)), Entropy, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(FilePath, encrypted);
        }
    }

    internal sealed class LauncherForm : Form
    {
        private readonly BootstrapCatalog _catalog;
        private readonly BootstrapperEngine _engine;
        private readonly UserCredentialProvider _credentials;
        private readonly Dictionary<string, AppCard> _cards = new Dictionary<string, AppCard>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _inFlight = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Label _status;
        private readonly LauncherStatusBar _statusBar;
        private readonly Panel _headerPanel;
        private readonly Button _settingsButton;
        private readonly Button _minimizeButton;
        private readonly Button _maximizeButton;
        private readonly Button _closeButton;
        private readonly ContextMenuStrip _windowMenu;
        private readonly Font _windowButtonFont;
        private readonly FlowLayoutPanel _grid;
        private readonly TextBox _search;
        private readonly Label _sectionHeading;
        private readonly Label _resultCount;
        private readonly Panel _emptyState;
        private readonly Dictionary<string, RoundedActionButton> _filterButtons = new Dictionary<string, RoundedActionButton>();
        private string _selectedFilter = "all";
        private readonly bool _smokeUi;
        private readonly bool _smokeRealProfile;
        private readonly Stopwatch _startup;
        private readonly string _reportPath;
        private readonly string _smokeInstalledAppId;
        private readonly System.Windows.Forms.Timer _smokeTimer;
        private Icon _ownedWindowIcon;
        private string _windowIconSource;
        private bool _customMaximized;
        private Rectangle _restoreBounds;
        private Rectangle _maximizedBounds;
        private long _firstPaintMs = -1;

        public LauncherForm(BootstrapCatalog catalog, BootstrapperEngine engine, bool smokeUi, bool smokeRealProfile, Stopwatch startup,
            string reportPath, string smokeInstalledAppId)
        {
            _catalog = catalog;
            _engine = engine;
            _credentials = EngineFactory.Credentials;
            _smokeUi = smokeUi;
            _smokeRealProfile = smokeRealProfile;
            _startup = startup;
            _reportPath = reportPath;
            _smokeInstalledAppId = smokeInstalledAppId;
            if (!string.IsNullOrWhiteSpace(_smokeInstalledAppId) && !catalog.Apps.Any(app =>
                string.Equals(app.Id, _smokeInstalledAppId, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("--smoke-installed-card must name an app in the embedded catalog.");
            _smokeTimer = new System.Windows.Forms.Timer { Interval = 200 };
            _smokeTimer.Tick += delegate { _smokeTimer.Stop(); WriteSmokeReportAndClose(); };
            Text = "i-luncher";
            FormBorderStyle = FormBorderStyle.None;
            BackColor = Color.FromArgb(17, 20, 27);
            ForeColor = Color.FromArgb(241, 244, 250);
            Font = new Font("Segoe UI", 9F);
            KeyPreview = true;
            KeyDown += Form_KeyDown;
            MinimumSize = new Size(720, 420);
            StartPosition = FormStartPosition.CenterScreen;
            Size = new Size(1100, 600);
            _ownedWindowIcon = LoadWindowIcon();
            Icon = _ownedWindowIcon;
            ClampToWorkArea();

            TableLayoutPanel root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, BackColor = BackColor };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));
            Controls.Add(root);

            _headerPanel = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty, BackColor = Color.FromArgb(22, 26, 35) };
            root.Controls.Add(_headerPanel, 0, 0);
            Label title = new Label { Text = "i-luncher", AutoSize = true, Font = new Font("Segoe UI Semibold", 15F), ForeColor = Color.White, Location = new Point(20, 11) };
            _headerPanel.Controls.Add(title);
            _settingsButton = new RoundedActionButton { Text = "설정", Width = 72, Height = 30, Anchor = AnchorStyles.Top | AnchorStyles.Right, Location = new Point(Width - 260, 9), AccessibleName = "설정" };
            _settingsButton.Enabled = !_smokeUi;
            StyleButton(_settingsButton, false);
            _settingsButton.Click += delegate { OpenSettings(); };
            _windowButtonFont = new Font("Segoe UI Symbol", 14F);
            _minimizeButton = CreateWindowButton("—", "최소화", false);
            _maximizeButton = CreateWindowButton("□", "최대화", false);
            _closeButton = CreateWindowButton("×", "닫기", true);
            _windowMenu = CreateWindowMenu();
            _headerPanel.ContextMenuStrip = _windowMenu;
            title.ContextMenuStrip = _windowMenu;
            _minimizeButton.Click += delegate { WindowState = FormWindowState.Minimized; };
            _maximizeButton.Click += delegate { ToggleCustomMaximize(); };
            _closeButton.Click += delegate { Close(); };
            Action layoutHeaderActions = delegate
            {
                _closeButton.Left = _headerPanel.ClientSize.Width - _closeButton.Width;
                _maximizeButton.Left = _closeButton.Left - _maximizeButton.Width;
                _minimizeButton.Left = _maximizeButton.Left - _minimizeButton.Width;
                _settingsButton.Left = _minimizeButton.Left - _settingsButton.Width - 10;
            };
            _headerPanel.Resize += delegate { layoutHeaderActions(); };
            _headerPanel.Controls.Add(_settingsButton);
            _headerPanel.Controls.Add(_minimizeButton);
            _headerPanel.Controls.Add(_maximizeButton);
            _headerPanel.Controls.Add(_closeButton);
            layoutHeaderActions();

            TableLayoutPanel store = new TableLayoutPanel { Dock = DockStyle.Fill, Margin = Padding.Empty, ColumnCount = 2, RowCount = 1, BackColor = BackColor };
            store.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 148));
            store.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.Controls.Add(store, 0, 1);
            FlowLayoutPanel sidebar = new FlowLayoutPanel { Dock = DockStyle.Fill, Margin = Padding.Empty, Padding = new Padding(12, 18, 12, 12), AutoScroll = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Color.FromArgb(20, 24, 32) };
            store.Controls.Add(sidebar, 0, 0);
            sidebar.Controls.Add(new Label { Text = "라이브러리", Width = 124, Height = 26, Margin = new Padding(4, 0, 0, 8), ForeColor = Color.FromArgb(142, 153, 175), Font = new Font("Segoe UI Semibold", 9F) });
            AddFilterButton(sidebar, "all", "전체 앱");
            AddFilterButton(sidebar, "installed", "설치된 앱");
            sidebar.Controls.Add(new Panel { Width = 116, Height = 1, Margin = new Padding(4, 12, 4, 14), BackColor = Color.FromArgb(43, 51, 66) });
            foreach (string category in catalog.Apps.Select(app => app.Category).Where(value => !String.IsNullOrWhiteSpace(value)).Distinct().OrderBy(value => value, StringComparer.CurrentCulture))
                AddFilterButton(sidebar, "category:" + category, category);

            TableLayoutPanel content = new TableLayoutPanel { Dock = DockStyle.Fill, Margin = Padding.Empty, Padding = new Padding(16), ColumnCount = 1, RowCount = 3, BackColor = BackColor };
            content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            content.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
            content.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
            content.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            store.Controls.Add(content, 1, 0);
            StoreSearchBox searchBox = new StoreSearchBox { Dock = DockStyle.Top, Height = 38, Margin = Padding.Empty };
            _search = searchBox.Input;
            content.Controls.Add(searchBox, 0, 0);
            Panel section = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
            _sectionHeading = new Label { Text = "전체 앱", Left = 0, Top = 6, Width = 300, Height = 30, Font = new Font("Segoe UI Semibold", 17F), ForeColor = ForeColor };
            _resultCount = new Label { TextAlign = ContentAlignment.MiddleRight, Top = 8, Width = 100, Height = 26, ForeColor = Color.FromArgb(150, 163, 185) };
            section.Resize += delegate { _resultCount.Left = section.ClientSize.Width - _resultCount.Width; _sectionHeading.Width = Math.Max(0, section.ClientSize.Width - 110); };
            section.Controls.Add(_sectionHeading);
            section.Controls.Add(_resultCount);
            content.Controls.Add(section, 0, 1);
            Panel results = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty, BackColor = BackColor };
            content.Controls.Add(results, 0, 2);
            _grid = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, Margin = Padding.Empty, Padding = Padding.Empty, WrapContents = true, BackColor = BackColor };
            results.Controls.Add(_grid);
            _emptyState = new Panel { Dock = DockStyle.Fill, Visible = false, BackColor = BackColor };
            Label emptyText = new Label { Text = "일치하는 앱이 없습니다", AutoSize = true, Left = 14, Top = 40, Font = new Font("Segoe UI Semibold", 13F), ForeColor = Color.FromArgb(181, 191, 210) };
            RoundedActionButton clearFilters = new RoundedActionButton { Text = "필터 지우기", Left = 14, Top = 82, Width = 120, Height = 34, AccessibleName = "검색 및 필터 지우기" };
            StyleButton(clearFilters, false);
            clearFilters.Click += delegate { ClearFilters(); };
            _emptyState.Controls.Add(emptyText);
            _emptyState.Controls.Add(clearFilters);
            results.Controls.Add(_emptyState);
            int cardWidth = 280;
            foreach (BootstrapApp app in catalog.Apps)
            {
                AppCard card = new AppCard(app, cardWidth, InstallClicked, RemoveClicked, RunClicked, true,
                    InstallFromLaunchPrompt, ConfirmInstallFromLaunchPrompt, GetStatusForCardContext, UpdateClicked,
                    FullDeleteClicked);
                _cards.Add(app.Id, card);
                _grid.Controls.Add(card);
            }
            _search.TextChanged += delegate { ApplyFilters(); };
            ApplyFilters();
            _statusBar = new LauncherStatusBar();
            _statusBar.Visible = false;
            root.Controls.Add(_statusBar, 0, 2);
            _status = _statusBar.StatusLabel;
            _statusBar.VisibilityChanged += delegate(bool visible)
            {
                _statusBar.Visible = visible;
                root.RowStyles[2].Height = visible ? 48 : 0;
                root.PerformLayout();
            };
            _engine.ProgressChanged += Engine_ProgressChanged;
            Shown += async delegate
            {
                await LoadStatusesAsync();
                if (_smokeUi) _smokeTimer.Start();
                else await CheckForUpdatesInBackgroundAsync();
            };
        }

        private void AddFilterButton(FlowLayoutPanel sidebar, string key, string text)
        {
            RoundedActionButton button = new RoundedActionButton { Text = text, Width = 124, Height = 36, Margin = new Padding(0, 0, 0, 4), AccessibleName = text, AccessibleDescription = "앱 목록 필터", Cursor = Cursors.Hand };
            button.Click += delegate { _selectedFilter = key; ApplyFilters(); };
            sidebar.Controls.Add(button);
            _filterButtons.Add(key, button);
        }

        private bool MatchesFilter(AppCard card)
        {
            if (_selectedFilter == "installed" && !card.IsInstalled) return false;
            if (_selectedFilter.StartsWith("category:", StringComparison.Ordinal) && card.App.Category != _selectedFilter.Substring(9)) return false;
            string query = _search.Text.Trim();
            return query.Length == 0 || new[] { card.App.Name, card.App.Category, card.App.Description }
                .Any(value => (value ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private void ApplyFilters()
        {
            if (_grid == null || _emptyState == null) return;
            _grid.SuspendLayout();
            int visibleCount = 0;
            foreach (AppCard card in _cards.Values)
            {
                bool visible = MatchesFilter(card);
                card.Visible = visible;
                if (visible) visibleCount++;
            }
            _grid.AutoScrollPosition = Point.Empty;
            _grid.ResumeLayout(true);
            _grid.Visible = visibleCount > 0;
            _emptyState.Visible = visibleCount == 0;
            if (_emptyState.Visible) _emptyState.BringToFront();
            else _grid.BringToFront();
            _sectionHeading.Text = _filterButtons[_selectedFilter].Text;
            _resultCount.Text = visibleCount + "개 앱";
            foreach (KeyValuePair<string, RoundedActionButton> item in _filterButtons)
            {
                bool selected = item.Key == _selectedFilter;
                item.Value.BackColor = selected ? Color.FromArgb(35, 53, 88) : Color.FromArgb(20, 24, 32);
                item.Value.ForeColor = selected ? Color.FromArgb(182, 206, 255) : Color.FromArgb(168, 180, 201);
                item.Value.FlatAppearance.BorderColor = selected ? Color.FromArgb(58, 81, 122) : item.Value.BackColor;
                item.Value.AccessibleDescription = selected ? "선택된 앱 목록 필터" : "앱 목록 필터";
                item.Value.Invalidate();
            }
        }

        private void ClearFilters()
        {
            _selectedFilter = "all";
            _search.Clear();
            ApplyFilters();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (_firstPaintMs < 0) _firstPaintMs = _startup.ElapsedMilliseconds;
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams parameters = base.CreateParams;
                parameters.Style |= 0x00040000 | 0x00020000 | 0x00010000 | 0x00080000;
                return parameters;
            }
        }

        private Button CreateWindowButton(string text, string name, bool closeButton)
        {
            Button button = new Button
            {
                Text = text, Width = 44, Height = 48, Top = 0,
                FlatStyle = FlatStyle.Flat, UseVisualStyleBackColor = false,
                BackColor = Color.FromArgb(22, 26, 35), ForeColor = Color.FromArgb(221, 226, 236),
                Font = _windowButtonFont,
                AccessibleName = name, AccessibleRole = AccessibleRole.PushButton,
                Anchor = AnchorStyles.Top | AnchorStyles.Right, TabStop = true
            };
            button.FlatAppearance.BorderSize = 0;
            button.FlatAppearance.MouseOverBackColor = closeButton ? Color.FromArgb(196, 54, 64) : Color.FromArgb(42, 49, 63);
            button.FlatAppearance.MouseDownBackColor = closeButton ? Color.FromArgb(153, 37, 47) : Color.FromArgb(51, 60, 76);
            button.Cursor = Cursors.Hand;
            return button;
        }

        private ContextMenuStrip CreateWindowMenu()
        {
            ContextMenuStrip menu = new ContextMenuStrip();
            ToolStripMenuItem restore = new ToolStripMenuItem("복원/최대화");
            ToolStripMenuItem minimize = new ToolStripMenuItem("최소화");
            ToolStripMenuItem close = new ToolStripMenuItem("닫기");
            restore.Click += delegate { ToggleCustomMaximize(); };
            minimize.Click += delegate { WindowState = FormWindowState.Minimized; };
            close.Click += delegate { Close(); };
            menu.Items.Add(restore);
            menu.Items.Add(minimize);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(close);
            return menu;
        }

        private void Form_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Alt && e.KeyCode == Keys.Space)
            {
                _windowMenu.Show(_headerPanel, new Point(8, _headerPanel.Height));
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == (Keys.Control | Keys.F))
            {
                _search.Focus();
                _search.SelectAll();
                return true;
            }
            if (keyData == Keys.Escape && _search.Focused && _search.Text.Length > 0)
            {
                _search.Clear();
                return true;
            }
            if (keyData == (Keys.Alt | Keys.Space))
            {
                _windowMenu.Show(_headerPanel, new Point(8, _headerPanel.Height));
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        private void ToggleCustomMaximize()
        {
            if (_customMaximized)
            {
                Rectangle restore = _restoreBounds;
                if (restore.Width < MinimumSize.Width || restore.Height < MinimumSize.Height)
                    restore = new Rectangle(Left, Top, Math.Max(MinimumSize.Width, Width), Math.Max(MinimumSize.Height, Height));
                Rectangle area = Screen.FromRectangle(restore).WorkingArea;
                if (!area.IntersectsWith(restore)) restore.Location = new Point(area.Left + 24, area.Top + 24);
                Bounds = restore;
                _customMaximized = false;
                _maximizeButton.Text = "□";
                _maximizeButton.AccessibleName = "최대화";
            }
            else
            {
                _restoreBounds = Bounds;
                _maximizedBounds = Screen.FromControl(this).WorkingArea;
                Bounds = _maximizedBounds;
                _customMaximized = true;
                _maximizeButton.Text = "❐";
                _maximizeButton.AccessibleName = "복원";
            }
        }

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SendMessage(IntPtr handle, int message, IntPtr wParam, IntPtr lParam);
        [DllImport("dwmapi.dll")]
        private static extern int DwmFlush();

        private int SendNcHitTest(Point screenPoint)
        {
            int packed = (screenPoint.X & 0xFFFF) | ((screenPoint.Y & 0xFFFF) << 16);
            return SendMessage(Handle, 0x0084, IntPtr.Zero, new IntPtr(packed)).ToInt32();
        }

        protected override void WndProc(ref Message m)
        {
            const int WmNcCalcSize = 0x0083;
            const int WmNcPaint = 0x0085;
            const int WmNcHitTest = 0x0084;
            const int WmNcLButtonDown = 0x00A1;
            const int WmNcLButtonDblClk = 0x00A3;
            const int WmSysCommand = 0x0112;
            const int HtClient = 1;
            const int HtCaption = 2;
            const int ScMaximize = 0xF030;
            const int ScMinimize = 0xF020;
            const int ScRestore = 0xF120;

            if (m.Msg == WmSysCommand)
            {
                int command = unchecked((int)m.WParam.ToInt64()) & 0xFFF0;
                if (command == ScMaximize && !_customMaximized)
                {
                    ToggleCustomMaximize();
                    m.Result = IntPtr.Zero;
                    return;
                }
                if (command == ScRestore && _customMaximized)
                {
                    if (WindowState == FormWindowState.Minimized)
                    {
                        WindowState = FormWindowState.Normal;
                        Bounds = _maximizedBounds;
                    }
                    else
                        ToggleCustomMaximize();
                    m.Result = IntPtr.Zero;
                    return;
                }
                if (command == ScMinimize)
                {
                    base.WndProc(ref m);
                    return;
                }
            }
            if (m.Msg == WmNcLButtonDown && m.WParam.ToInt32() == HtCaption && _customMaximized)
            {
                Point cursor = Cursor.Position;
                ToggleCustomMaximize();
                Left = cursor.X - Width / 2;
                Top = cursor.Y - 15;
                base.WndProc(ref m);
                return;
            }
            if (m.Msg == WmNcLButtonDblClk && m.WParam.ToInt32() == HtCaption)
            {
                ToggleCustomMaximize();
                m.Result = IntPtr.Zero;
                return;
            }

            if (m.Msg == WmNcCalcSize)
            {
                m.Result = IntPtr.Zero;
                return;
            }
            if (m.Msg == WmNcPaint)
            {
                m.Result = IntPtr.Zero;
                return;
            }
            base.WndProc(ref m);
            if (m.Msg != WmNcHitTest || m.Result.ToInt32() != HtClient) return;

            long packed = m.LParam.ToInt64();
            Point point = PointToClient(new Point(unchecked((short)(packed & 0xFFFF)), unchecked((short)((packed >> 16) & 0xFFFF))));
            m.Result = new IntPtr(HitTestWindow(point));
        }

        private int HitTestWindow(Point point)
        {
            const int HtClient = 1;
            const int HtCaption = 2;
            const int HtLeft = 10;
            const int HtRight = 11;
            const int HtTop = 12;
            const int HtTopLeft = 13;
            const int HtTopRight = 14;
            const int HtBottom = 15;
            const int HtBottomLeft = 16;
            const int HtBottomRight = 17;
            int edge = 6;
            bool left = point.X < edge;
            bool right = point.X >= ClientSize.Width - edge;
            bool top = point.Y < edge;
            bool bottom = point.Y >= ClientSize.Height - edge;
            if (!_customMaximized)
            {
                if (top && left) return HtTopLeft;
                if (top && right) return HtTopRight;
                if (bottom && left) return HtBottomLeft;
                if (bottom && right) return HtBottomRight;
                if (left) return HtLeft;
                if (right) return HtRight;
                if (top) return HtTop;
                if (bottom) return HtBottom;
            }
            if (point.Y >= 0 && point.Y < _headerPanel.Height)
            {
                Point headerPoint = _headerPanel.PointToClient(PointToScreen(point));
                foreach (Control control in _headerPanel.Controls)
                    if (control is Button && control.Bounds.Contains(headerPoint)) return HtClient;
                return HtCaption;
            }
            return HtClient;
        }

        private void WriteSmokeReportAndClose()
        {
            Dictionary<string, object> chromeChecks = RunChromeSmokeChecks();
            Dictionary<string, object> filterChecks = RunFilterSmokeChecks();
            Dictionary<string, object> cardChecks = RunCardSmokeChecks();
            Dictionary<string, object> paintChecks = RunRoundedPaintSmokeChecks();
            Rectangle bounds = Bounds;
            Process process = Process.GetCurrentProcess();
            string screenshotPath = Path.ChangeExtension(Path.GetFullPath(_reportPath), ".png");
            string screenshotError = null;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(screenshotPath));
                SavePhysicalSmokeScreenshot(screenshotPath);
            }
            catch (Exception ex) { screenshotError = Program.ShortError(ex); }
            Dictionary<string, object> report = new Dictionary<string, object>
            {
                { "firstPaintMs", _firstPaintMs < 0 ? _startup.ElapsedMilliseconds : _firstPaintMs },
                { "bounds", new Dictionary<string, int> { { "x", bounds.X }, { "y", bounds.Y }, { "width", bounds.Width }, { "height", bounds.Height } } },
                { "cards", _cards.Count },
                { "appStatuses", _cards.Values.OrderBy(card => card.App.Id, StringComparer.OrdinalIgnoreCase)
                    .Select(CreateAppStatusSmokeReport).ToArray() },
                { "gridColumns", _cards.Count == 0 ? 0 : _cards.Values.Select(card => card.Left).Distinct().Count() },
                { "iconsDecoded", _cards.Values.Count(card => card.HasDecodedIcon) },
                { "smokeInstalledAppId", _smokeInstalledAppId },
                { "smokeInstalledAction", String.IsNullOrWhiteSpace(_smokeInstalledAppId) ? null : _cards[_smokeInstalledAppId].RunActionLabel },
                { "smokeInstalledStatus", String.IsNullOrWhiteSpace(_smokeInstalledAppId) ? null : _cards[_smokeInstalledAppId].StatusLabel },
                { "footerStyle", _statusBar.StyleName },
                { "footerState", _statusBar.StateName },
                { "footerStatusAccessibleName", _status.AccessibleName },
                { "footerProgressVisible", _statusBar.ProgressVisible },
                { "windowChrome", "custom-borderless" },
                { "titleBarBounds", RectangleToDictionary(_headerPanel.Bounds) },
                { "gridLayout", new Dictionary<string, object>
                    {
                        { "clientBounds", RectangleToDictionary(_grid.ClientRectangle) },
                        { "displayBounds", RectangleToDictionary(_grid.DisplayRectangle) },
                        { "autoScrollMinSize", RectangleToDictionary(new Rectangle(Point.Empty, _grid.AutoScrollMinSize)) },
                        { "verticalScrollVisible", _grid.VerticalScroll.Visible },
                        { "horizontalScrollVisible", _grid.HorizontalScroll.Visible },
                        { "cardBottom", _cards.Count == 0 ? 0 : _cards.Values.Max(card => card.Bottom) }
                    } },
                { "windowControlBounds", new Dictionary<string, object>
                    {
                        { "settings", RectangleToDictionary(_settingsButton.Bounds) },
                        { "minimize", RectangleToDictionary(_minimizeButton.Bounds) },
                        { "maximizeRestore", RectangleToDictionary(_maximizeButton.Bounds) },
                        { "close", RectangleToDictionary(_closeButton.Bounds) }
                    } },
                { "smokeChromeChecks", chromeChecks },
                { "smokeCardChecks", cardChecks },
                { "smokeFilterChecks", filterChecks },
                { "smokeRoundedPaintChecks", paintChecks },
                { "windowIconSource", _windowIconSource },
                { "windowIconSize", _ownedWindowIcon == null ? null : _ownedWindowIcon.Width + "x" + _ownedWindowIcon.Height },
                { "profileMode", _smokeRealProfile ? "current-user-read-only" : "isolated-temporary" },
                { "statusOnly", _smokeUi },
                { "workingSetBytes", process.WorkingSet64 },
                { "networkUsed", false },
                { "appsLaunched", false },
                { "screenshotPath", screenshotError == null ? screenshotPath : null },
                { "screenshotError", screenshotError }
            };
            string reportJson = new JavaScriptSerializer().Serialize(report);
            string reportDirectory = Path.GetDirectoryName(Path.GetFullPath(_reportPath));
            Directory.CreateDirectory(reportDirectory);
            File.WriteAllText(_reportPath, reportJson, new UTF8Encoding(false));
            _smokeTimer.Dispose();
            Close();
        }

        private Dictionary<string, object> CreateAppStatusSmokeReport(AppCard card)
        {
            BootstrapperAppStatus engineStatus = null;
            string statusError = null;
            try { engineStatus = _engine.GetStatus(card.App.Id); }
            catch (Exception ex) { statusError = Program.ShortError(ex); }
            return new Dictionary<string, object>
            {
                { "appId", card.App.Id },
                { "installed", card.IsInstalled },
                { "engineInstalled", engineStatus == null ? (object)null : engineStatus.IsInstalled },
                { "installedVersion", engineStatus == null ? null : engineStatus.InstalledVersion },
                { "availableVersion", engineStatus == null ? null : engineStatus.AvailableVersion },
                { "updateAvailable", engineStatus == null ? (object)null : engineStatus.UpdateAvailable },
                { "statusReadError", statusError },
                { "contextRemoveAvailable", card.ContextRemoveAvailable },
                { "launchAction", card.RunActionLabel },
                { "launchAccessibleName", card.RunAccessibleName },
                { "launchAvailable", card.CanRun },
                { "launchBounds", RectangleToDictionary(card.RunActionBounds) },
                { "cardBounds", RectangleToDictionary(card.CardBounds) },
                { "description", card.DescriptionText },
                { "busyStatus", card.IsBusy ? card.StatusLabel : null },
                { "launchGroupGeometry", card.LaunchGroupGeometry },
                { "registrationActionGeometry", card.RegistrationActionGeometry },
                { "titleHasFullTooltip", card.TitleHasFullTooltip },
                { "iconSourceWidth", card.IconSourceSize.Width },
                { "iconSourceHeight", card.IconSourceSize.Height }
            };
        }

        private Dictionary<string, object> RunRoundedPaintSmokeChecks()
        {
            Dictionary<string, object> result = new Dictionary<string, object>();
            Control previousFocus = ActiveControl;
            using (Panel surface = new Panel { Bounds = new Rectangle(24, 64, 160, 82), BackColor = Color.FromArgb(38, 45, 57) })
            using (Bitmap captures = new Bitmap(128 * 4, 42))
            {
                RoundedActionButton probe = new RoundedActionButton
                {
                    Bounds = new Rectangle(18, 20, 106, 34), Text = "검증", Enabled = true,
                    BackColor = Color.FromArgb(50, 91, 168), ForeColor = Color.White
                };
                probe.FlatAppearance.BorderColor = Color.FromArgb(70, 82, 105);
                probe.FlatAppearance.MouseOverBackColor = Color.FromArgb(68, 108, 180);
                int paintCount = 0;
                probe.PaintGlyph += delegate { paintCount++; };
                surface.Controls.Add(probe);
                Controls.Add(surface);
                surface.BringToFront();
                try
                {
                    string[] states = { "normal", "hover", "focus", "resizeRepaint" };
                    for (int index = 0; index < states.Length; index++)
                    {
                        if (index == 1)
                            typeof(RoundedActionButton).GetMethod("OnMouseEnter", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                                .Invoke(probe, new object[] { EventArgs.Empty });
                        if (index == 2) result["enabledFixtureTakesFocus"] = probe.Focus();
                        if (index == 3) { surface.BackColor = Color.FromArgb(29, 36, 48); probe.Width += 8; }
                        // This probe verifies each deterministic paint endpoint; animation timing is covered by the card tween checks.
                        probe.CompleteAnimationForSmoke();
                        int before = paintCount;
                        surface.Refresh();
                        probe.Invalidate();
                        probe.Update();
                        Application.DoEvents();
                        DwmFlush();
                        using (Bitmap frame = new Bitmap(probe.Width, probe.Height))
                        {
                            // Capture the composed screen after normal WM_PAINT; WM_PRINT/DrawToBitmap bypassed the original defect.
                            using (Graphics graphics = Graphics.FromImage(frame))
                                graphics.CopyFromScreen(probe.PointToScreen(Point.Empty), Point.Empty, frame.Size);
                            int background = surface.BackColor.ToArgb();
                            Color expectedFill = index == 0 ? probe.BackColor : probe.FlatAppearance.MouseOverBackColor;
                            result[states[index] + "PaintClearsCorners"] = paintCount > before && probe.Enabled &&
                                frame.GetPixel(0, 0).ToArgb() == background && frame.GetPixel(frame.Width - 1, 0).ToArgb() == background &&
                                frame.GetPixel(0, frame.Height - 1).ToArgb() == background && frame.GetPixel(frame.Width - 1, frame.Height - 1).ToArgb() == background &&
                                frame.GetPixel(frame.Width / 2, 5).ToArgb() == expectedFill.ToArgb();
                            using (Graphics graphics = Graphics.FromImage(captures)) graphics.DrawImageUnscaled(frame, index * 128, 4);
                        }
                    }
                    string path = Path.ChangeExtension(Path.GetFullPath(_reportPath), ".paint-states.png");
                    captures.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                    result["physicalStateScreenshotPath"] = path;
                }
                catch (Exception ex) { result["paintCaptureError"] = Program.ShortError(ex); }
                finally
                {
                    Controls.Remove(surface);
                    if (previousFocus != null && previousFocus.CanFocus) previousFocus.Focus();
                }
            }
            return result;
        }

        private Dictionary<string, object> RunFilterSmokeChecks()
        {
            Dictionary<string, object> result = new Dictionary<string, object>();
            if (_cards.Count == 0) return result;
            AppCard sample = _cards.Values.First();
            bool wasInstalled = sample.IsInstalled;
            try
            {
                ClearFilters();
                _search.Text = sample.App.Name.ToUpperInvariant();
                result["searchMatchesNameIgnoringCase"] = sample.Visible && _cards.Values.Where(card => card.Visible).All(card =>
                    new[] { card.App.Name, card.App.Category, card.App.Description }.Any(value => (value ?? "").IndexOf(_search.Text, StringComparison.OrdinalIgnoreCase) >= 0));
                _search.Text = sample.App.Description;
                result["searchMatchesDescription"] = sample.Visible;
                _search.Text = sample.App.Category;
                result["searchMatchesCategory"] = sample.Visible;
                _search.Text = sample.App.Name;
                _filterButtons["category:" + sample.App.Category].PerformClick();
                result["categoryCombinesWithSearch"] = sample.Visible && _cards.Values.Where(card => card.Visible).All(card => card.App.Category == sample.App.Category);
                result["categoriesComeFromCatalog"] = _filterButtons.Count == 2 + _catalog.Apps.Select(app => app.Category).Where(value => !String.IsNullOrWhiteSpace(value)).Distinct().Count();
                sample.SetBusy(true, "설치 중…");
                _search.Text = "__no_matching_app_fixture__";
                result["hiddenOperationKeepsCardAndLock"] = Object.ReferenceEquals(sample, _cards[sample.App.Id]) && sample.ActionsLocked && sample.StatusLabel == "설치 중…";
                result["noResultsShowsClearAction"] = !_cards.Values.Any(card => card.Visible) && _emptyState.Visible && _resultCount.Text == "0개 앱";
                using (Bitmap screenshot = new Bitmap(Width, Height))
                {
                    DrawToBitmap(screenshot, new Rectangle(Point.Empty, Size));
                    string path = Path.ChangeExtension(Path.GetFullPath(_reportPath), ".empty.png");
                    screenshot.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                    result["emptyScreenshotPath"] = path;
                }
                _emptyState.Controls.OfType<Button>().Single().PerformClick();
                result["clearRestoresAllCards"] = _search.Text.Length == 0 && _selectedFilter == "all" && _cards.Values.All(card => card.Visible) && !_emptyState.Visible;
                result["filterPreservesBusyState"] = sample.HasCompactBusyState;
                _search.Text = "__no_matching_app_fixture__";
                sample.SetStatus(null);
                FinishOperation(sample.App.Id);
                ClearFilters();
                result["hiddenCompletionRestoresIdleCard"] = Object.ReferenceEquals(sample, _cards[sample.App.Id]) && sample.Visible && sample.HasCompactIdleState && !sample.ActionsLocked;
                _filterButtons["installed"].PerformClick();
                result["installedFilterUsesActualState"] = _cards.Values.All(card => card.Visible == card.IsInstalled);
                sample.SetSmokeInstalledStatus();
                FinishOperation(sample.App.Id);
                bool installedAppears = sample.Visible;
                sample.SetStatus(null);
                FinishOperation(sample.App.Id);
                result["installedFilterRefreshesAfterOperation"] = installedAppears && !sample.Visible && _selectedFilter == "installed";
                ClearFilters();
                _search.Text = sample.App.Name;
                Message message = Message.Create(Handle, 0, IntPtr.Zero, IntPtr.Zero);
                bool focusHandled = ProcessCmdKey(ref message, Keys.Control | Keys.F);
                bool searchFocused = _search.Focused && _search.SelectionLength == _search.Text.Length;
                bool clearHandled = ProcessCmdKey(ref message, Keys.Escape);
                result["searchKeyboardShortcuts"] = focusHandled && searchFocused && clearHandled && _search.Text.Length == 0;
            }
            finally
            {
                sample.SetBusy(false, null);
                if (wasInstalled) sample.SetSmokeInstalledStatus(); else sample.SetStatus(null);
                ClearFilters();
            }
            return result;
        }

        private Dictionary<string, object> RunCardSmokeChecks()
        {
            List<AppCard> fixtures = new List<AppCard>();
            Dictionary<string, object> result = new Dictionary<string, object>();
            if (_cards.Count == 0) return result;
            AppCard sample = _cards.Values.First();
            AppCard installedStyleFixture = new AppCard(sample.App, 280, delegate { }, delegate { }, delegate { }, true);
            AppCard uninstalledStyleFixture = new AppCard(sample.App, 280, delegate { }, delegate { }, delegate { }, true);
            installedStyleFixture.SetSmokeInstalledStatus();
            uninstalledStyleFixture.SetStatus(null);
            using (Panel previewPanel = new Panel { Size = new Size(584, 120), BackColor = Color.FromArgb(11, 13, 18) })
            try
            {
                result["installedAndUninstalledCardColorsDiffer"] =
                    installedStyleFixture.CardSurfaceColor == Color.FromArgb(25, 39, 42) &&
                    installedStyleFixture.CardOutlineColor == Color.FromArgb(89, 191, 174) &&
                    installedStyleFixture.CardOutlineThickness == 2F &&
                    uninstalledStyleFixture.CardSurfaceColor == Color.FromArgb(35, 45, 59) &&
                    uninstalledStyleFixture.CardOutlineColor == Color.FromArgb(117, 131, 152) &&
                    installedStyleFixture.CardSurfaceColor != uninstalledStyleFixture.CardSurfaceColor &&
                    installedStyleFixture.CardOutlineColor != uninstalledStyleFixture.CardOutlineColor;
                installedStyleFixture.SetBusy(true, "작업 중…");
                result["busyRetainsInstalledCardColorsAndGeometry"] = installedStyleFixture.IsInstalled &&
                    installedStyleFixture.CardSurfaceColor == Color.FromArgb(25, 39, 42) &&
                    installedStyleFixture.CardOutlineColor == Color.FromArgb(89, 191, 174) &&
                    installedStyleFixture.CardOutlineThickness == 2F &&
                    installedStyleFixture.LaunchGroupGeometry == "matches" &&
                    uninstalledStyleFixture.LaunchGroupGeometry == "matches" &&
                    installedStyleFixture.FocusOutlineColor == Color.FromArgb(124, 156, 255);
                installedStyleFixture.SetBusy(false, null);
                installedStyleFixture.Location = new Point(8, 6);
                uninstalledStyleFixture.Location = new Point(296, 6);
                previewPanel.Controls.Add(installedStyleFixture);
                previewPanel.Controls.Add(uninstalledStyleFixture);
                previewPanel.PerformLayout();
                Application.DoEvents();
                using (Bitmap preview = new Bitmap(previewPanel.Width, previewPanel.Height))
                {
                    previewPanel.DrawToBitmap(preview, previewPanel.ClientRectangle);
                    Color installedPixel = preview.GetPixel(88, 90);
                    Color uninstalledPixel = preview.GetPixel(376, 90);
                    bool renderedStates = installedPixel == installedStyleFixture.CardSurfaceColor &&
                        uninstalledPixel == uninstalledStyleFixture.CardSurfaceColor && installedPixel != uninstalledPixel;
                    renderedStates &= Program.CountRenderedOutlinePixels(preview, installedStyleFixture, false) >= installedStyleFixture.Width / 2 &&
                        Program.CountRenderedOutlinePixels(preview, uninstalledStyleFixture, false) >= uninstalledStyleFixture.Width / 2 &&
                        Program.CountRenderedOutlinePixels(preview, installedStyleFixture, true) >= installedStyleFixture.Width / 2 &&
                        Program.CountRenderedOutlinePixels(preview, uninstalledStyleFixture, true) < uninstalledStyleFixture.Width / 2;
                    string previewPath = Path.ChangeExtension(Path.GetFullPath(_reportPath), ".card-states.png");
                    if (renderedStates)
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(previewPath));
                        preview.Save(previewPath, ImageFormat.Png);
                    }
                    result["installedUninstalledCardPreviewRendered"] = renderedStates;
                    result["installedUninstalledCardPreviewPath"] = renderedStates ? previewPath : null;
                }
                sample.SetBusy(true, "설치 중…");
                result["busyShowsTransientStatus"] = sample.HasCompactBusyState;
                sample.SetBusy(false, null);
                result["idleRestoresDescription"] = sample.HasCompactIdleState && sample.DescriptionText == sample.App.Description;
                while (_grid.Controls.Count < 9)
                {
                    BootstrapApp fixtureApp = _cards.Values.ElementAt(fixtures.Count % _cards.Count).App;
                    AppCard fixture = new AppCard(fixtureApp, 280, delegate { }, delegate { }, delegate { }, true,
                        delegate { }, delegate { return DialogResult.No; });
                    fixture.SetStatus(null);
                    fixtures.Add(fixture);
                    _grid.Controls.Add(fixture);
                }
                _grid.PerformLayout();
                Application.DoEvents();
                AppCard[] all = _grid.Controls.OfType<AppCard>().ToArray();
                _resultCount.Text = all.Length + "개 앱";
                result["compactCardHeight"] = all.All(card => card.Height == 108);
                result["launchGroupAndMetadataGeometry"] = all.All(card => card.ContentClearsActions);
                result["titleEllipsisKeepsFullTooltip"] = all.All(card => card.TitleHasFullTooltip);
                result["launchSurfaceAccessibleAndCoversCard"] = all.All(card =>
                    card.RunAccessibleName == (card.IsInstalled ? "실행 " : "설치 ") + card.App.Name &&
                    card.RunActionBounds == new Rectangle(0, 0, card.CardBounds.Width, card.CardBounds.Height) &&
                    card.LaunchKeyboardAccessible && card.LaunchRegionContainsContent);
                result["uninstalledShowsInstallPromptSurface"] = all.Where(card => !card.IsInstalled).All(card =>
                    !card.CanRun && card.RunActionEnabled && card.LaunchVisualMuted &&
                    card.RunAccessibleName == "설치 " + card.App.Name && card.RunAccessibleDescription.Contains("설치"));
                result["primaryActionPromptBehaviors"] = RunPrimaryActionSmokeChecks();
                result["contextMenuBehaviors"] = RunContextMenuSmokeChecks();
                result["updateMenuBehaviors"] = RunUpdateMenuSmokeChecks();
                result["nineCardsFitThreeRows"] = all.Length == 9 && all.Select(card => card.Left).Distinct().Count() == 3 &&
                    all.Select(card => card.Top).Distinct().Count() == 3 && !_grid.VerticalScroll.Visible && !_grid.HorizontalScroll.Visible &&
                    all.Max(card => card.Bottom) <= _grid.ClientSize.Height;
                foreach (AppCard card in all) { card.Invalidate(true); card.Update(); }
                _grid.Refresh();
                Application.DoEvents();
                string path = Path.ChangeExtension(Path.GetFullPath(_reportPath), ".nine-cards.png");
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                SavePhysicalSmokeScreenshot(path);
                result["nineCardScreenshotPath"] = path;
                AppCard busyFixture = fixtures.FirstOrDefault(card => card.App.Name.Contains(" ")) ?? fixtures.FirstOrDefault();
                if (busyFixture != null)
                {
                    busyFixture.SetBusy(true, "설치 중…");
                    result["busyReplacesDescription"] = busyFixture.IsBusy && busyFixture.StatusLabel == "설치 중…" &&
                        busyFixture.DescriptionText == "설치 중…" && busyFixture.ProgressVisible;
                    string busyPath = Path.ChangeExtension(Path.GetFullPath(_reportPath), ".busy.png");
                    SavePhysicalSmokeScreenshot(busyPath);
                    result["busyFixtureScreenshotPath"] = busyPath;
                }
                AppCard focusFixture = all.FirstOrDefault(card => card.CanRun);
                bool launchFocusVisible = focusFixture == null || focusFixture.FocusLaunchSurface();
                result["launchSurfaceFocusVisible"] = launchFocusVisible;
                if (focusFixture != null)
                {
                    string focusPath = Path.ChangeExtension(Path.GetFullPath(_reportPath), ".launch-focus.png");
                    SavePhysicalSmokeScreenshot(focusPath);
                    result["launchFocusScreenshotPath"] = focusPath;
                }
            }
            finally
            {
                sample.SetBusy(false, null);
                foreach (AppCard fixture in fixtures) { _grid.Controls.Remove(fixture); fixture.Dispose(); }
                ApplyFilters();
            }
            return result;
        }

        private Dictionary<string, object> RunPrimaryActionSmokeChecks()
        {
            int promptCalls = 0, installCalls = 0, removeCalls = 0, launchCalls = 0;
            AppCard hover = new AppCard(_cards.Values.First().App, 280, delegate { }, delegate { }, delegate { }, true);
            hover.SetStatus(null);
            Color idlePixel;
            using (Bitmap initial = new Bitmap(hover.Width, hover.Height))
            {
                hover.DrawToBitmap(initial, hover.ClientRectangle);
                idlePixel = initial.GetPixel(275, 50);
            }
            hover.SetPointerHoverForSmoke(true);
            bool hoverTimerStarted = hover.VisualAnimationActiveForSmoke;
            hover.AdvanceAnimationForSmoke(80);
            Color transitionPixel;
            using (Bitmap transitioning = new Bitmap(hover.Width, hover.Height))
            {
                hover.DrawToBitmap(transitioning, hover.ClientRectangle);
                transitionPixel = transitioning.GetPixel(275, 50);
            }
            bool hoverInterpolates = hoverTimerStarted && idlePixel != transitionPixel &&
                transitionPixel != hover.HoverSurfaceColorForSmoke && hover.HoverSurfaceColorForSmoke != idlePixel;
            hover.SetPointerHoverForSmoke(false);
            bool returnTransitionStarted = hover.VisualAnimationActiveForSmoke;
            hover.CompleteAnimationForSmoke();
            Color settledPixel;
            using (Bitmap settled = new Bitmap(hover.Width, hover.Height))
            {
                hover.DrawToBitmap(settled, hover.ClientRectangle);
                settledPixel = settled.GetPixel(275, 50);
            }
            bool hoverReturnsAndStops = returnTransitionStarted && settledPixel == idlePixel && !hover.VisualAnimationActiveForSmoke;
            hover.SetPointerHoverForSmoke(true);
            hover.Dispose();
            bool timerDisposedWithCard = hover.VisualTimerDisposedForSmoke && !hover.VisualAnimationActiveForSmoke;

            AppCard canceled = new AppCard(_cards.Values.First().App, 280,
                delegate { installCalls++; }, delegate { removeCalls++; }, delegate { launchCalls++; }, true,
                delegate { installCalls++; }, delegate { promptCalls++; return DialogResult.No; });
            canceled.SetStatus(null);
            canceled.PerformPrimaryActionForSmoke();
            bool cancelDoesNothing = promptCalls == 1 && installCalls == 0 && removeCalls == 0 && launchCalls == 0;
            canceled.Dispose();

            AppCard accepted = new AppCard(_cards.Values.First().App, 280,
                delegate { installCalls++; }, delegate { removeCalls++; }, delegate { launchCalls++; }, true,
                delegate { installCalls++; }, delegate { promptCalls++; return DialogResult.Yes; });
            accepted.SetStatus(null);
            accepted.PerformPrimaryActionForSmoke();
            bool acceptInstallsOnly = promptCalls == 2 && installCalls == 1 && removeCalls == 0 && launchCalls == 0;
            accepted.Dispose();

            AppCard installed = null;
            bool launchPulseBeforeCallback = false;
            installed = new AppCard(_cards.Values.First().App, 280,
                delegate { installCalls++; }, delegate { removeCalls++; }, delegate
                {
                    launchCalls++;
                    launchPulseBeforeCallback = installed.LaunchAnimationTriggeredForSmoke &&
                        installed.LaunchPulseActiveForSmoke && installed.LaunchPulsePaintsWhenFocusedForSmoke;
                }, true,
                delegate { installCalls++; }, delegate { promptCalls++; return DialogResult.Yes; });
            installed.SetSmokeInstalledStatus();
            installed.PerformPrimaryActionForSmoke();
            bool installedLaunchesWithoutPrompt = promptCalls == 2 && installCalls == 1 && removeCalls == 0 && launchCalls == 1 && launchPulseBeforeCallback;
            installed.SetBusy(true, "실행 중…");
            bool busyDisablesBothActions = !installed.RunActionEnabled && !installed.InstallActionEnabled;
            installed.PerformPrimaryActionForSmoke();
            bool busyBlocksCallbacks = promptCalls == 2 && installCalls == 1 && removeCalls == 0 && launchCalls == 1;
            installed.Dispose();

            AppCard stateChangedCard = null;
            int stateChangedPromptCalls = 0;
            AppCard stateChanged = new AppCard(_cards.Values.First().App, 280,
                delegate { installCalls++; }, delegate { removeCalls++; }, delegate { launchCalls++; }, true,
                delegate { installCalls++; }, delegate { stateChangedPromptCalls++; stateChangedCard.SetSmokeInstalledStatus(); return DialogResult.Yes; });
            stateChangedCard = stateChanged;
            stateChanged.SetStatus(null);
            stateChanged.PerformPrimaryActionForSmoke();
            bool installedDuringPromptDoesNotInstall = stateChangedPromptCalls == 1 && installCalls == 1 && removeCalls == 0 && launchCalls == 1;
            stateChanged.Dispose();

            return new Dictionary<string, object>
            {
                { "cancelDoesNothing", cancelDoesNothing },
                { "acceptInstallsOnly", acceptInstallsOnly },
                { "installedClickLaunchesOnly", installedLaunchesWithoutPrompt },
                { "busyDisablesAndBlocksActions", busyDisablesBothActions && busyBlocksCallbacks },
                { "stateChangedDuringPromptCannotRemove", installedDuringPromptDoesNotInstall },
                { "hoverSurfaceInterpolates", hoverInterpolates },
                { "hoverReturnsToIdleAndStops", hoverReturnsAndStops },
                { "animationTimerDisposedWithCard", timerDisposedWithCard },
                { "launchPulseStartsBeforeSingleCallback", launchPulseBeforeCallback && launchCalls == 1 }
            };
        }

        private Dictionary<string, object> RunContextMenuSmokeChecks()
        {
            AppCard installedSource = _cards.Values.FirstOrDefault(card => card.IsInstalled);
            AppCard uninstalledSource = _cards.Values.FirstOrDefault(card => !card.IsInstalled);
            int refreshCalls = 0, removeCalls = 0, fullDeleteCalls = 0, installCalls = 0, promptCalls = 0, launchCalls = 0;
            Dictionary<string, object> result = new Dictionary<string, object>();
            if (installedSource == null || uninstalledSource == null)
            {
                result["installedRemoveOnly"] = false;
                result["installedFullDeleteDistinct"] = false;
                result["uninstalledContextHasNoAction"] = false;
                result["busyContextBlocked"] = false;
                return result;
            }
            AppCard installed = new AppCard(installedSource.App, 280,
                delegate { installCalls++; }, delegate { removeCalls++; }, delegate { launchCalls++; }, true,
                delegate { installCalls++; }, delegate { promptCalls++; return DialogResult.Yes; },
                delegate(BootstrapApp app) { refreshCalls++; BootstrapperAppStatus status = _engine.GetStatus(app.Id); return new BootstrapperAppStatus(status.App, status.IsReady, true, status.ExecutablePath, status.Error, status.AvailableVersion, status.InstalledVersion, false); },
                null, delegate { fullDeleteCalls++; });
            installed.SetSmokeInstalledStatus();
            installed.RefreshContextMenuForSmoke();
            bool removeAvailable = installed.ContextRemoveAvailable && refreshCalls == 1;
            installed.PerformContextRemoveForSmoke();
            result["installedRemoveOnly"] = removeAvailable && removeCalls == 1 && installCalls == 0 && promptCalls == 0 && launchCalls == 0;
            bool fullDeleteAvailable = installed.ContextFullDeleteAvailable;
            installed.PerformContextFullDeleteForSmoke();
            result["installedFullDeleteDistinct"] = fullDeleteAvailable && fullDeleteCalls == 1 && removeCalls == 1 &&
                installCalls == 0 && promptCalls == 0 && launchCalls == 0;
            installed.SetBusy(true, "작업 중…");
            installed.RefreshContextMenuForSmoke();
            installed.PerformContextRemoveForSmoke();
            installed.PerformContextFullDeleteForSmoke();
            result["busyContextBlocked"] = !installed.ContextRemoveAvailable && !installed.ContextFullDeleteAvailable &&
                removeCalls == 1 && fullDeleteCalls == 1 && installCalls == 0 && promptCalls == 0 && launchCalls == 0;
            installed.Dispose();

            AppCard uninstalled = new AppCard(uninstalledSource.App, 280,
                delegate { installCalls++; }, delegate { removeCalls++; }, delegate { launchCalls++; }, true,
                delegate { installCalls++; }, delegate { promptCalls++; return DialogResult.Yes; },
                delegate(BootstrapApp app) { refreshCalls++; return _engine.GetStatus(app.Id); },
                null, delegate { fullDeleteCalls++; });
            uninstalled.SetStatus(null);
            int priorRefreshCalls = refreshCalls;
            uninstalled.RefreshContextMenuForSmoke();
            uninstalled.PerformContextRemoveForSmoke();
            uninstalled.PerformContextFullDeleteForSmoke();
            result["uninstalledContextHasNoAction"] = !uninstalled.ContextRemoveAvailable && !uninstalled.ContextFullDeleteAvailable &&
                refreshCalls == priorRefreshCalls + 1 && removeCalls == 1 && fullDeleteCalls == 1 &&
                installCalls == 0 && promptCalls == 0 && launchCalls == 0;
            uninstalled.Dispose();
            return result;
        }

        private Dictionary<string, object> RunUpdateMenuSmokeChecks()
        {
            Dictionary<string, object> result = new Dictionary<string, object>();
            if (_cards.Count == 0) { result["explicitUpdateOnly"] = false; return result; }
            BootstrapperAppStatus baseStatus = _engine.GetStatus(_cards.Values.First().App.Id);
            BootstrapperAppStatus updateStatus = new BootstrapperAppStatus(baseStatus.App, true, true,
                "C:\\fake\\installed.exe", null, "2.0.0", "1.0.0", true);
            int updateCalls = 0, installCalls = 0, removeCalls = 0, runCalls = 0;
            AppCard card = new AppCard(_cards.Values.First().App, 280,
                delegate { installCalls++; }, delegate { removeCalls++; }, delegate { runCalls++; }, true,
                delegate { installCalls++; }, delegate { return DialogResult.No; },
                delegate { return updateStatus; }, delegate { updateCalls++; });
            card.SetStatus(updateStatus);
            card.RefreshContextMenuForSmoke();
            bool shown = card.ContextUpdateAvailable;
            card.PerformContextUpdateForSmoke();
            bool explicitUpdate = shown && updateCalls == 1 && installCalls == 0 && removeCalls == 0 && runCalls == 0;
            card.SetBusy(true, "업데이트 중…");
            card.RefreshContextMenuForSmoke();
            card.PerformContextUpdateForSmoke();
            result["explicitUpdateOnly"] = explicitUpdate && !card.ContextUpdateAvailable &&
                updateCalls == 1 && installCalls == 0 && removeCalls == 0 && runCalls == 0;
            card.Dispose();
            return result;
        }

        private void SavePhysicalSmokeScreenshot(string path)
        {
            Activate();
            BringToFront();
            Refresh();
            Application.DoEvents();
            DwmFlush();
            using (Bitmap screenshot = new Bitmap(Width, Height))
            using (Graphics capture = Graphics.FromImage(screenshot))
            {
                capture.CopyFromScreen(Left, Top, 0, 0, Size);
                screenshot.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            }
        }

        private Dictionary<string, object> RunChromeSmokeChecks()
        {
            const int HtClient = 1;
            const int HtCaption = 2;
            const int HtLeft = 10;
            const int ScRestore = 0xF120;
            Rectangle originalBounds = Bounds;
            bool maximizedToWorkArea = false;
            bool restoredBounds = false;
            bool minimized = false;
            bool restoredFromMinimize = false;
            bool taskbarRestoreKeepsMaximized = false;
            bool minimumSizeScrollOverflowAccessible = false;
            bool minimumSizeLastCardVisible = false;
            bool minimumSizeActionsInsideCards = false;
            bool minimumSizeSidebarEndAccessible = false;
            bool idleLayoutRestoresNoScroll = false;
            try
            {
                _maximizeButton.PerformClick();
                maximizedToWorkArea = Bounds.Equals(Screen.FromControl(this).WorkingArea);
                _maximizeButton.PerformClick();
                restoredBounds = Bounds.Equals(originalBounds);
                _maximizeButton.PerformClick();
                _minimizeButton.PerformClick();
                minimized = WindowState == FormWindowState.Minimized;
                SendMessage(Handle, 0x0112, new IntPtr(ScRestore), IntPtr.Zero);
                restoredFromMinimize = WindowState == FormWindowState.Normal;
                taskbarRestoreKeepsMaximized = _customMaximized && Bounds.Equals(Screen.FromControl(this).WorkingArea);
                _maximizeButton.PerformClick();
                if (!Bounds.Equals(originalBounds)) Bounds = originalBounds;
                PerformLayout();
                _grid.PerformLayout();
                Size minimumWindow = MinimumSize;
                Rectangle workArea = Screen.FromControl(this).WorkingArea;
                Bounds = new Rectangle(workArea.Left + 12, workArea.Top + 12,
                    Math.Min(minimumWindow.Width, workArea.Width - 24), Math.Min(minimumWindow.Height, workArea.Height - 24));
                PerformLayout();
                _grid.PerformLayout();
                Application.DoEvents();
                bool overflowVisible = _grid.VerticalScroll.Visible &&
                    _grid.VerticalScroll.Maximum > _grid.VerticalScroll.LargeChange - 1;
                int scrollMaximum = Math.Max(0, _grid.VerticalScroll.Maximum - _grid.VerticalScroll.LargeChange + 1);
                if (overflowVisible && scrollMaximum > 0)
                {
                    _grid.AutoScrollPosition = new Point(0, scrollMaximum);
                    Application.DoEvents();
                }
                minimumSizeScrollOverflowAccessible = overflowVisible && _grid.AutoScrollPosition.Y < 0;
                minimumSizeLastCardVisible = _cards.Count == 0 || _cards.Values.Max(card => card.Bottom) <= _grid.ClientSize.Height;
                minimumSizeActionsInsideCards = _cards.Values.All(card =>
                    new Rectangle(Point.Empty, card.CardBounds.Size).Contains(card.RunActionBounds));
                RoundedActionButton lastCategory = _filterButtons.Values.OrderBy(button => button.Top).Last();
                ScrollableControl sidebar = (ScrollableControl)lastCategory.Parent;
                sidebar.ScrollControlIntoView(lastCategory);
                minimumSizeSidebarEndAccessible = sidebar.ClientRectangle.Contains(lastCategory.Bounds);
                _grid.AutoScrollPosition = Point.Empty;
                string minimumPath = Path.ChangeExtension(Path.GetFullPath(_reportPath), ".minimum.png");
                Directory.CreateDirectory(Path.GetDirectoryName(minimumPath));
                SavePhysicalSmokeScreenshot(minimumPath);
                Bounds = originalBounds;
                PerformLayout();
                _grid.PerformLayout();
                Application.DoEvents();
                idleLayoutRestoresNoScroll = !_grid.VerticalScroll.Visible && !_grid.HorizontalScroll.Visible;
            }
            catch { }
            Point resizePoint = PointToScreen(new Point(2, ClientSize.Height / 2));
            Point titlePoint = PointToScreen(new Point(150, _headerPanel.Height / 2));
            Point settingsPoint = _settingsButton.PointToScreen(new Point(_settingsButton.Width / 2, _settingsButton.Height / 2));
            return new Dictionary<string, object>
            {
                { "maximizeUsesWorkingArea", maximizedToWorkArea },
                { "restoreReturnsToPriorBounds", restoredBounds },
                { "minimizeAndRestore", minimized && restoredFromMinimize },
                { "taskbarRestoreKeepsMaximized", taskbarRestoreKeepsMaximized },
                { "minimumSize", new Dictionary<string, int> { { "width", MinimumSize.Width }, { "height", MinimumSize.Height } } },
                { "minimumSizeScrollOverflowAccessible", minimumSizeScrollOverflowAccessible },
                { "minimumSizeLastCardVisible", minimumSizeLastCardVisible },
                { "minimumSizeActionsInsideCards", minimumSizeActionsInsideCards },
                { "minimumSizeSidebarEndAccessible", minimumSizeSidebarEndAccessible },
                { "idleLayoutRestoresNoScroll", idleLayoutRestoresNoScroll },
                { "resizeEdgeHitTest", SendNcHitTest(resizePoint) == HtLeft },
                { "titleDragHitTest", SendNcHitTest(titlePoint) == HtCaption },
                { "settingsButtonHitTest", SendNcHitTest(settingsPoint) == HtClient },
                { "closeControlAccessible", _closeButton.Enabled && _closeButton.AccessibleName == "닫기" }
            };
        }

        private static Dictionary<string, int> RectangleToDictionary(Rectangle rectangle)
        {
            return new Dictionary<string, int>
            {
                { "x", rectangle.X }, { "y", rectangle.Y }, { "width", rectangle.Width }, { "height", rectangle.Height }
            };
        }

        private void ClampToWorkArea()
        {
            Rectangle area = Screen.FromPoint(Cursor.Position).WorkingArea;
            int width = Math.Max(480, Math.Min(1100, area.Width - 24));
            int height = Math.Max(360, Math.Min(600, area.Height - 24));
            MinimumSize = new Size(Math.Min(720, width), Math.Min(420, height));
            Width = width;
            Height = height;
        }
        private Icon LoadWindowIcon()
        {
            try
            {
                Icon embedded = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                if (embedded != null)
                {
                    _windowIconSource = "embedded-executable";
                    return embedded;
                }
            }
            catch { }
            try
            {
                Icon sidecar = new Icon(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ILauncher.ico"));
                _windowIconSource = "sidecar-ico";
                return sidecar;
            }
            catch
            {
                _windowIconSource = "system-fallback";
                return (Icon)SystemIcons.Application.Clone();
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_ownedWindowIcon != null)
                {
                    _ownedWindowIcon.Dispose();
                    _ownedWindowIcon = null;
                }
                if (_windowMenu != null) _windowMenu.Dispose();
                if (_windowButtonFont != null) _windowButtonFont.Dispose();
            }
            base.Dispose(disposing);
        }
        private async Task LoadStatusesAsync()
        {
            try
            {
                await _engine.LoadCatalogAsync(CancellationToken.None);
                foreach (BootstrapApp app in _catalog.Apps)
                {
                    try
                    {
                        BootstrapperAppStatus status = _engine.GetStatus(app.Id);
                        _cards[app.Id].SetStatus(status);
                        if (string.Equals(app.Id, _smokeInstalledAppId, StringComparison.OrdinalIgnoreCase))
                            _cards[app.Id].SetSmokeInstalledStatus();
                    }
                    catch { }
                }
            }
            catch (Exception ex) { SetAmbientStatusState("error"); _status.Text = "목록 확인 실패: " + Program.ShortError(ex); }
            ApplyFilters();
        }
        private async Task CheckForUpdatesInBackgroundAsync()
        {
            try
            {
                await _engine.CheckForUpdatesAsync(CancellationToken.None);
                if (IsDisposed || !IsHandleCreated) return;
                foreach (BootstrapApp app in _catalog.Apps)
                {
                    AppCard card;
                    if (!_cards.TryGetValue(app.Id, out card) || card.IsBusy || _inFlight.Contains(app.Id)) continue;
                    try { card.SetStatus(_engine.GetStatus(app.Id)); }
                    catch { }
                }
                ApplyFilters();
            }
            catch { }
        }
        private async void InstallClicked(BootstrapApp app)
        {
            if (_smokeUi) return;
            if (!_inFlight.Add(app.Id)) return;
            SetBusy(app.Id, true, "설치 중…");
            _statusBar.SetState("working");
            _status.Text = app.Name + " 설치를 준비하고 있습니다…";
            try
            {
                BootstrapperInstallResult result = await _engine.InstallAsync(app.Id, CancellationToken.None);
                _cards[app.Id].SetStatus(_engine.GetStatus(app.Id));
                SetOperationResultState("success");
                _status.Text = "설치 완료: " + result.ShortcutPath;
            }
            catch (Exception ex)
            {
                _cards[app.Id].SetStatus(_engine.GetStatus(app.Id));
                SetOperationResultState("error");
                _status.Text = "설치 실패: " + Program.ShortError(ex);
            }
            finally { FinishOperation(app.Id); }
        }
        private void InstallFromLaunchPrompt(BootstrapApp app)
        {
            if (_smokeUi) return;
            AppCard card;
            if (!_cards.TryGetValue(app.Id, out card) || card.IsBusy || _inFlight.Contains(app.Id)) return;
            try
            {
                BootstrapperAppStatus currentStatus = _engine.GetStatus(app.Id);
                card.SetStatus(currentStatus);
                if (currentStatus != null && currentStatus.IsInstalled)
                {
                    SetOperationResultState("ready");
                    _status.Text = app.Name + " is already installed.";
                    return;
                }
            }
            catch (Exception ex)
            {
                SetOperationResultState("error");
                _status.Text = "설치 상태 확인 실패: " + Program.ShortError(ex);
                return;
            }
            InstallClicked(app);
        }
        private static DialogResult ConfirmInstallFromLaunchPrompt(Form owner, BootstrapApp app)
        {
            return InstallConfirmationDialog.Show(owner, app);
        }
        private BootstrapperAppStatus GetStatusForCardContext(BootstrapApp app)
        {
            BootstrapperAppStatus status = _engine.GetStatus(app.Id);
            AppCard card;
            if (_cards.TryGetValue(app.Id, out card)) card.SetStatus(status);
            ApplyFilters();
            return status;
        }
        private async void RemoveClicked(BootstrapApp app)
        {
            await RemoveAppAsync(app, false);
        }
        private async void FullDeleteClicked(BootstrapApp app)
        {
            await RemoveAppAsync(app, true);
        }
        private async Task RemoveAppAsync(BootstrapApp app, bool deleteFiles)
        {
            if (_smokeUi) return;
            AppCard card;
            if (!_cards.TryGetValue(app.Id, out card) || !card.IsInstalled || card.IsBusy) return;
            string operation = deleteFiles ? "완전 삭제" : "제거";
            string confirmation = deleteFiles
                ? "\"" + app.Name + "\" 앱을 완전히 삭제할까요?\r\n\r\n" +
                  "설치 등록과 시작 메뉴 바로가기뿐 아니라 런처가 관리하는 이 앱의 설치 폴더와 다운로드 캐시도 삭제합니다. 설치 폴더 안에 직접 저장한 파일도 함께 삭제됩니다. " +
                  "다른 앱에 필요한 공용 파일과 실행 환경, 설치 폴더 밖의 사용자 데이터는 유지됩니다.\r\n\r\n" +
                  "삭제한 파일은 되돌릴 수 없습니다. 다시 사용하려면 설치해야 합니다."
                : "\"" + app.Name + "\" 앱을 제거할까요?\r\n\r\n" +
                "이 앱의 개별 설치 등록과 시작 메뉴 바로가기만 제거합니다. " +
                "다운로드한 파일, 공용 실행 환경, 사용자 데이터는 유지됩니다.";
            if (MessageBox.Show(this, confirmation, "앱 " + operation, MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                return;
            if (!card.IsInstalled || card.IsBusy || !_inFlight.Add(app.Id)) return;
            SetBusy(app.Id, true, operation + " 중…");
            _statusBar.SetState("working");
            _status.Text = app.Name + " " + operation + " 중…";
            try
            {
                BootstrapperUninstallResult result = deleteFiles
                    ? await _engine.UninstallAsync(app.Id, true, CancellationToken.None)
                    : await _engine.UninstallAsync(app.Id, CancellationToken.None);
                _cards[app.Id].SetStatus(_engine.GetStatus(app.Id));
                SetOperationResultState(result.Removed ? "success" : "ready");
                _status.Text = result.Removed ? operation + " 완료: " + app.Name : operation + "하지 않았습니다: " + app.Name;
            }
            catch (Exception ex)
            {
                _cards[app.Id].SetStatus(_engine.GetStatus(app.Id));
                SetOperationResultState("error");
                _status.Text = operation + " 실패: " + Program.ShortError(ex);
            }
            finally { FinishOperation(app.Id); }
        }
        private async void UpdateClicked(BootstrapApp app)
        {
            if (_smokeUi) return;
            AppCard card;
            if (!_cards.TryGetValue(app.Id, out card) || card.IsBusy || !_inFlight.Add(app.Id)) return;
            try
            {
                BootstrapperAppStatus current = _engine.GetStatus(app.Id);
                card.SetStatus(current);
                if (current == null || !current.IsInstalled || !current.UpdateAvailable)
                {
                    _inFlight.Remove(app.Id);
                    return;
                }
            }
            catch (Exception ex)
            {
                _inFlight.Remove(app.Id);
                SetOperationResultState("error");
                _status.Text = "업데이트 상태 확인 실패: " + Program.ShortError(ex);
                return;
            }
            SetBusy(app.Id, true, "업데이트 중…");
            _statusBar.SetState("working");
            _status.Text = app.Name + " 업데이트를 준비하고 있습니다…";
            try
            {
                await _engine.UpdateAsync(app.Id, CancellationToken.None);
                card.SetStatus(_engine.GetStatus(app.Id));
                SetOperationResultState("success");
                _status.Text = "업데이트 완료: " + app.Name;
            }
            catch (Exception ex)
            {
                card.SetStatus(_engine.GetStatus(app.Id));
                SetOperationResultState("error");
                _status.Text = "업데이트 실패: " + Program.ShortError(ex);
            }
            finally { FinishOperation(app.Id); }
        }
        private async void RunClicked(BootstrapApp app)
        {
            if (_smokeUi) return;
            AppCard card;
            if (!_cards.TryGetValue(app.Id, out card) || !card.CanRun || _inFlight.Contains(app.Id)) return;
            try
            {
                BootstrapperAppStatus currentStatus = _engine.GetStatus(app.Id);
                card.SetStatus(currentStatus);
                if (currentStatus == null || !currentStatus.IsInstalled)
                {
                    SetOperationResultState("ready");
                    _status.Text = app.Name + " 미설치: 설치 후 실행할 수 있습니다.";
                    return;
                }
            }
            catch (Exception ex)
            {
                SetOperationResultState("error");
                _status.Text = "실행 상태 확인 실패: " + Program.ShortError(ex);
                return;
            }
            if (!_inFlight.Add(app.Id)) return;
            SetBusy(app.Id, true, null);
            _statusBar.SetState("working");
            _status.Text = app.Name + "을(를) 확인하고 있습니다…";
            try
            {
                BootstrapperLaunchResult result = await _engine.LaunchAsync(app.Id, CancellationToken.None);
                SetOperationResultState("success");
                _status.Text = "실행했습니다: " + Path.GetFileName(result.ExecutablePath);
                _cards[app.Id].SetStatus(_engine.GetStatus(app.Id));
            }
            catch (Exception ex)
            {
                _cards[app.Id].SetStatus(_engine.GetStatus(app.Id));
                SetOperationResultState("error");
                _status.Text = "실행 실패: " + Program.ShortError(ex);
            }
            finally
            {
                card.EndLaunchAnimation();
                FinishOperation(app.Id);
            }
        }
        private void FinishOperation(string appId)
        {
            _inFlight.Remove(appId);
            SetBusy(appId, false);
            ApplyFilters();
            if (_inFlight.Count > 0) _statusBar.SetState("working");
            _statusBar.SetProgress(0, 0);
        }
        private void SetOperationResultState(string state)
        {
            _statusBar.SetState(_inFlight.Count > 1 ? "working" : state);
        }
        private void SetAmbientStatusState(string state)
        {
            _statusBar.SetState(_inFlight.Count > 0 ? "working" : state);
        }
        private void SetBusy(string appId, bool busy, string busyLabel = null)
        {
            AppCard card;
            if (_cards.TryGetValue(appId, out card)) card.SetBusy(busy, busyLabel);
        }
        private void Engine_ProgressChanged(object sender, BootstrapperProgressEventArgs e)
        {
            if (IsDisposed) return;
            if (InvokeRequired) { BeginInvoke(new EventHandler<BootstrapperProgressEventArgs>(Engine_ProgressChanged), sender, e); return; }
            if (_inFlight.Count == 0 || (!string.Equals(e.AppId, "runtime", StringComparison.Ordinal)
                && !_inFlight.Contains(e.AppId))) return;
            BootstrapApp app = _catalog.Apps.FirstOrDefault(item => string.Equals(item.Id, e.AppId, StringComparison.OrdinalIgnoreCase));
            SetAmbientStatusState("working");
            _status.Text = (app == null ? e.AppId : app.Name) + " · " + e.Phase;
            _statusBar.SetProgress(e.BytesTransferred, e.BytesTotal);
        }
        private void OpenSettings()
        {
            if (_smokeUi) return;
            BootstrapSettings settings = BootstrapSettings.Load();
            using (Form dialog = new Form { Text = "설정", FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent, ClientSize = new Size(520, 260), MaximizeBox = false, MinimizeBox = false, BackColor = Color.FromArgb(24, 28, 37), ForeColor = ForeColor })
            using (TextBox mirror = new TextBox { Text = settings.MirrorBase, Left = 20, Top = 44, Width = 470 })
            using (TextBox token = new TextBox { Text = settings.Token, Left = 20, Top = 104, Width = 470, UseSystemPasswordChar = true })
            using (CheckBox remember = new CheckBox { Text = "이 PC의 현재 Windows 사용자 계정에 PAT 기억", Checked = settings.RememberToken, Left = 20, Top = 148, AutoSize = true })
            using (Button save = new Button { Text = "저장", Left = 392, Top = 200, Width = 98, Height = 34, DialogResult = DialogResult.OK })
            using (Button cancel = new Button { Text = "취소", Left = 284, Top = 200, Width = 98, Height = 34, DialogResult = DialogResult.Cancel })
            using (Label mirrorLabel = new Label { Text = "미러 기본 경로 (HTTPS 또는 로컬/UNC 경로, 비우면 기본 서버)", Left = 20, Top = 20, AutoSize = true })
            using (Label tokenLabel = new Label { Text = "GitHub / GHES PAT (선택 사항)", Left = 20, Top = 80, AutoSize = true })
            {
                dialog.Controls.AddRange(new Control[] { mirrorLabel, mirror, tokenLabel, token, remember, save, cancel });
                dialog.AcceptButton = save; dialog.CancelButton = cancel;
                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    try
                    {
                        ValidateMirror(mirror.Text.Trim());
                        settings.MirrorBase = mirror.Text.Trim();
                        settings.RememberToken = remember.Checked;
                        settings.Token = remember.Checked ? token.Text : "";
                        settings.Save();
                        EngineFactory.Options.PackageMirrorBase = settings.MirrorBase;
                        _credentials.Update(new BootstrapSettings { MirrorBase = mirror.Text.Trim(), RememberToken = remember.Checked, Token = token.Text });
                        SetAmbientStatusState("success");
                        _status.Text = "설정을 저장했습니다. 토큰은 선택한 경우에만 현재 사용자 DPAPI로 보호됩니다.";
                    }
                    catch (Exception ex) { SetAmbientStatusState("error"); MessageBox.Show(this, Program.ShortError(ex), "설정", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
                }
            }
        }
        private static void ValidateMirror(string value)
        {
            if (value.Length == 0) return;
            Uri uri;
            if (Uri.TryCreate(value, UriKind.Absolute, out uri) && uri.IsUnc == false && uri.Scheme == Uri.UriSchemeHttps &&
                string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment)) return;
            if (Path.IsPathRooted(value) || value.StartsWith("\\\\", StringComparison.Ordinal)) return;
            throw new InvalidOperationException("미러 경로는 HTTPS URL, 로컬 경로 또는 UNC 경로여야 합니다.");
        }
        private static string FormatMegabytes(long bytes)
        {
            return string.Format("{0:0.0} MB", bytes / 1048576.0);
        }
        private static void StyleButton(Button button, bool primary)
        {
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderColor = Color.FromArgb(61, 70, 86);
            button.BackColor = primary ? Color.FromArgb(50, 91, 168) : Color.FromArgb(37, 43, 55);
            button.ForeColor = Color.White;
            button.Cursor = Cursors.Hand;
        }
    }

    internal sealed class LauncherStatusBar : Panel
    {
        private readonly Label _status;
        private readonly Panel _progressTrack;
        private readonly Panel _progressFill;
        private readonly ToolTip _statusTip;
        private string _stateName = "ready";
        private long _progressTransferred;
        private long _progressTotal;
        private bool _isShown;

        public Label StatusLabel { get { return _status; } }
        public string StyleName { get { return "compact-transient-status"; } }
        public string StateName { get { return _stateName; } }
        public bool ProgressVisible { get { return _progressTrack.Visible; } }
        public event Action<bool> VisibilityChanged;

        public LauncherStatusBar()
        {
            Dock = DockStyle.Fill;
            BackColor = Color.FromArgb(22, 26, 35);
            Padding = new Padding(20, 5, 20, 5);
            AccessibleName = "상태 표시줄";

            Panel divider = new Panel { Dock = DockStyle.Top, Height = 1, BackColor = Color.FromArgb(49, 57, 72) };
            Controls.Add(divider);

            _status = new Label
            {
                Left = Padding.Left, Top = 5, Width = 700, Height = 21,
                Text = "", ForeColor = Color.FromArgb(223, 228, 238),
                TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true,
                AccessibleRole = AccessibleRole.StatusBar, AccessibleName = "상태 메시지", AccessibleDescription = "현재 작업의 상태 메시지",
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            Controls.Add(_status);

            _progressTrack = new Panel { Left = _status.Left, Top = 30, Width = 700, Height = 3, BackColor = Color.FromArgb(49, 57, 72), Visible = false, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
            _progressFill = new Panel { Left = 0, Top = 0, Width = 0, Height = 3, BackColor = Color.FromArgb(83, 154, 239) };
            _progressTrack.Controls.Add(_progressFill);
            Controls.Add(_progressTrack);

            _statusTip = new ToolTip { ShowAlways = true, InitialDelay = 350, ReshowDelay = 100, AutoPopDelay = 10000 };
            _statusTip.SetToolTip(_status, _status.Text);
            _status.TextChanged += delegate
            {
                _statusTip.SetToolTip(_status, _status.Text);
                _status.AccessibleName = String.IsNullOrWhiteSpace(_status.Text) ? "상태 메시지" : _status.Text;
            };
            Resize += delegate { LayoutContents(); };
            LayoutContents();
        }

        public void SetProgress(long transferred, long total)
        {
            _progressTransferred = transferred;
            _progressTotal = total;
            _progressTrack.Visible = total > 0 && _stateName == "working";
            LayoutProgress();
        }

        private void LayoutContents()
        {
            int available = Math.Max(0, ClientSize.Width - Padding.Right - _status.Left);
            _status.Width = available;
            _progressTrack.Left = _status.Left;
            _progressTrack.Width = available;
            LayoutProgress();
        }

        private void LayoutProgress()
        {
            if (_progressFill == null || _progressTrack == null) return;
            int width = _progressTotal <= 0 ? 0 : (int)Math.Max(0, Math.Min(_progressTrack.ClientSize.Width,
                (double)_progressTrack.ClientSize.Width * Math.Min(_progressTransferred, _progressTotal) / (double)_progressTotal));
            _progressFill.Width = width;
        }

        public void SetState(string stateName)
        {
            Color accent;
            if (string.Equals(stateName, "error", StringComparison.Ordinal))
            {
                _stateName = "error"; accent = Color.FromArgb(242, 112, 112);
            }
            else if (string.Equals(stateName, "working", StringComparison.Ordinal))
            {
                _stateName = "working"; accent = Color.FromArgb(95, 165, 245);
            }
            else if (string.Equals(stateName, "success", StringComparison.Ordinal))
            {
                _stateName = "success"; accent = Color.FromArgb(83, 190, 143);
            }
            else
            {
                _stateName = "ready"; accent = Color.FromArgb(83, 190, 143);
            }
            _progressFill.BackColor = _stateName == "error" ? Color.FromArgb(224, 100, 105) : accent;
            if (_stateName != "working") SetProgress(0, 0);
            bool shouldShow = _stateName == "working" || _stateName == "error";
            if (_isShown != shouldShow)
            {
                _isShown = shouldShow;
                Action<bool> handler = VisibilityChanged;
                if (handler != null) handler(shouldShow);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _statusTip.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    internal static class RoundedDrawing
    {
        internal static GraphicsPath CreatePath(RectangleF bounds, float radius)
        {
            float diameter = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
            GraphicsPath path = new GraphicsPath();
            path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    internal sealed class StoreSearchBox : Panel
    {
        internal readonly TextBox Input;
        private readonly Label _hint;
        private readonly RoundedActionButton _clear;

        internal StoreSearchBox()
        {
            DoubleBuffered = true;
            BackColor = Color.FromArgb(23, 28, 38);
            Input = new TextBox { BorderStyle = BorderStyle.None, Left = 38, Top = 10, BackColor = BackColor, ForeColor = Color.FromArgb(236, 241, 250), AccessibleName = "앱 검색", TabIndex = 0 };
            _hint = new Label { Text = "앱 검색", Left = 38, Top = 9, Width = 120, Height = 22, ForeColor = Color.FromArgb(142, 153, 175), BackColor = BackColor };
            _hint.Click += delegate { Input.Focus(); };
            _clear = new RoundedActionButton { Text = "×", Width = 28, Height = 28, Top = 5, BackColor = BackColor, ForeColor = Color.FromArgb(180, 193, 214), Visible = false, AccessibleName = "검색어 지우기", Cursor = Cursors.Hand, TabIndex = 1 };
            _clear.FlatAppearance.BorderColor = BackColor;
            _clear.Click += delegate { Input.Clear(); Input.Focus(); };
            Input.TextChanged += delegate { _hint.Visible = Input.Text.Length == 0; _clear.Visible = Input.Text.Length > 0; };
            Input.GotFocus += delegate { Invalidate(); };
            Input.LostFocus += delegate { Invalidate(); };
            Controls.Add(Input);
            Controls.Add(_hint);
            Controls.Add(_clear);
            _hint.BringToFront();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (Input == null) return;
            Input.Width = Math.Max(20, ClientSize.Width - 78);
            _clear.Left = ClientSize.Width - 34;
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent == null ? Color.FromArgb(17, 20, 27) : Parent.BackColor);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath path = RoundedDrawing.CreatePath(new RectangleF(0.5F, 0.5F, Width - 2, Height - 2), 11))
            using (SolidBrush fill = new SolidBrush(BackColor))
            using (Pen border = new Pen(Input.Focused ? Color.FromArgb(108, 145, 250) : Color.FromArgb(47, 56, 71)))
            using (Pen glyph = new Pen(Color.FromArgb(148, 163, 185), 1.5F))
            {
                e.Graphics.FillPath(fill, path);
                e.Graphics.DrawPath(border, path);
                e.Graphics.DrawEllipse(glyph, 14, 11, 9, 9);
                e.Graphics.DrawLine(glyph, 22, 19, 27, 24);
            }
            base.OnPaint(e);
        }
    }

    internal sealed class RoundedActionButton : Button
    {
        public float CornerRadius { get; set; }
        public float ChromeClipInset { get; set; }
        private bool _hovered;
        private bool _pressed;
        private System.Windows.Forms.Timer _visualTimer;
        private Color _animatedFill;
        private Color _animatedBorder;
        private Color _transitionStartFill;
        private Color _transitionStartBorder;
        private Color _transitionTargetFill;
        private Color _transitionTargetBorder;
        private int _transitionStarted;
        private bool _colorsInitialized;
        private bool _launchPulse;
        private int _launchPulseStarted;
        private bool _launchAnimationTriggered;
        internal event EventHandler PointerLeaveRequested;
        internal event Action<Color> AnimatedFillChanged;
        internal event PaintEventHandler PaintGlyph;

        internal bool VisualAnimationActiveForSmoke { get { return _visualTimer != null && _visualTimer.Enabled; } }
        internal bool LaunchAnimationTriggeredForSmoke { get { return _launchAnimationTriggered; } }
        internal bool LaunchPulseActiveForSmoke { get { return _launchPulse; } }
        internal bool LaunchPulsePaintsWhenFocusedForSmoke { get { return ShouldPaintLaunchPulse(true); } }
        internal bool SupportsAnimationForSmoke { get { return true; } }
        internal Color AnimatedFillForSmoke { get { EnsureAnimatedColors(); return _animatedFill; } }
        internal bool VisualTimerDisposedForSmoke { get { return _animationTimerDisposed; } }
        private bool _animationTimerDisposed;

        public RoundedActionButton()
        {
            CornerRadius = 11;
            DrawSurfaceChrome = true;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            FlatStyle = FlatStyle.Flat;
            UseVisualStyleBackColor = false;
            SetStyle(ControlStyles.Opaque, false);
        }

        public bool DrawSurfaceChrome { get; set; }

        public void SetPointerHover(bool hovered)
        {
            if (_hovered == hovered) return;
            _hovered = hovered;
            UpdateAnimatedVisuals();
        }

        internal void RefreshAnimatedVisuals(bool animate)
        {
            if (animate) UpdateAnimatedVisuals();
            else SnapAnimatedVisuals();
        }

        private void SnapAnimatedVisuals()
        {
            EnsureAnimatedColors();
            Color fill, border;
            GetVisualTargets(out fill, out border);
            _transitionStartFill = _transitionTargetFill = _animatedFill = fill;
            _transitionStartBorder = _transitionTargetBorder = _animatedBorder = border;
            _transitionStarted = Environment.TickCount;
            NotifyAnimatedFillChanged();
            StopVisualTimerIfIdle();
            Invalidate();
        }

        public void StartLaunchAnimation()
        {
            _launchAnimationTriggered = true;
            _launchPulse = true;
            _launchPulseStarted = Environment.TickCount;
            EnsureVisualTimer();
            Invalidate();
            Update();
        }

        public void EndLaunchAnimation()
        {
            // The pulse has its own short fade and must remain visible even when launch completes synchronously.
            Invalidate();
        }

        internal void AdvanceAnimationForSmoke(int elapsedMilliseconds)
        {
            if (_visualTimer == null || !_visualTimer.Enabled) return;
            _transitionStarted = unchecked(Environment.TickCount - Math.Max(0, elapsedMilliseconds));
            VisualTimer_Tick(this, EventArgs.Empty);
        }

        internal void CompleteAnimationForSmoke()
        {
            _animatedFill = _transitionTargetFill;
            _animatedBorder = _transitionTargetBorder;
            NotifyAnimatedFillChanged();
            if (_visualTimer != null && !_launchPulse) _visualTimer.Stop();
            Invalidate();
        }

        private void UpdateAnimatedVisuals()
        {
            EnsureAnimatedColors();
            Color fill, border;
            GetVisualTargets(out fill, out border);
            if (fill == _transitionTargetFill && border == _transitionTargetBorder && _visualTimer != null && _visualTimer.Enabled)
                return;
            _transitionStartFill = _animatedFill;
            _transitionStartBorder = _animatedBorder;
            _transitionTargetFill = fill;
            _transitionTargetBorder = border;
            _transitionStarted = Environment.TickCount;
            if (_transitionStartFill == fill && _transitionStartBorder == border)
            {
                _animatedFill = fill;
                _animatedBorder = border;
                NotifyAnimatedFillChanged();
                StopVisualTimerIfIdle();
            }
            else EnsureVisualTimer();
            Invalidate();
        }

        private void EnsureAnimatedColors()
        {
            if (_colorsInitialized) return;
            _animatedFill = BackColor;
            _animatedBorder = FlatAppearance.BorderColor;
            _transitionTargetFill = _animatedFill;
            _transitionTargetBorder = _animatedBorder;
            _colorsInitialized = true;
        }

        private void GetVisualTargets(out Color fill, out Color border)
        {
            fill = BackColor;
            border = FlatAppearance.BorderColor;
            if (!Enabled) return;
            if (_pressed)
            {
                fill = FlatAppearance.MouseDownBackColor.IsEmpty
                    ? ControlPaint.Dark(BackColor, 0.10F) : FlatAppearance.MouseDownBackColor;
                border = ControlPaint.Light(FlatAppearance.BorderColor, 0.06F);
            }
            else if (_hovered)
            {
                fill = FlatAppearance.MouseOverBackColor.IsEmpty
                    ? ControlPaint.Light(BackColor, 0.10F) : FlatAppearance.MouseOverBackColor;
                border = ControlPaint.Light(FlatAppearance.BorderColor, 0.16F);
            }
        }

        private void EnsureVisualTimer()
        {
            if (_visualTimer == null)
            {
                _visualTimer = new System.Windows.Forms.Timer { Interval = 15 };
                _visualTimer.Tick += VisualTimer_Tick;
            }
            if (!_visualTimer.Enabled) _visualTimer.Start();
        }

        private void StopVisualTimerIfIdle()
        {
            if (_visualTimer != null && !_launchPulse &&
                _animatedFill == _transitionTargetFill && _animatedBorder == _transitionTargetBorder)
                _visualTimer.Stop();
        }

        private void VisualTimer_Tick(object sender, EventArgs e)
        {
            int elapsed = unchecked(Environment.TickCount - _transitionStarted);
            float amount = Math.Min(1F, Math.Max(0F, elapsed / 160F));
            float eased = 1F - (1F - amount) * (1F - amount);
            _animatedFill = Blend(_transitionStartFill, _transitionTargetFill, eased);
            _animatedBorder = Blend(_transitionStartBorder, _transitionTargetBorder, eased);
            NotifyAnimatedFillChanged();
            if (_launchPulse && unchecked(Environment.TickCount - _launchPulseStarted) >= 240)
                _launchPulse = false;
            if (amount >= 1F)
            {
                _animatedFill = _transitionTargetFill;
                _animatedBorder = _transitionTargetBorder;
            }
            Invalidate();
            if (!_launchPulse && amount >= 1F) _visualTimer.Stop();
        }

        private static Color Blend(Color from, Color to, float amount)
        {
            return Color.FromArgb(
                from.A + (int)((to.A - from.A) * amount),
                from.R + (int)((to.R - from.R) * amount),
                from.G + (int)((to.G - from.G) * amount),
                from.B + (int)((to.B - from.B) * amount));
        }

        private void NotifyAnimatedFillChanged()
        {
            Action<Color> handler = AnimatedFillChanged;
            if (handler != null) handler(_animatedFill);
        }

        private bool ShouldPaintLaunchPulse(bool focused)
        {
            return _launchPulse && (!focused || Enabled);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent == null ? SystemColors.Control : Parent.BackColor);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics graphics = e.Graphics;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            bool focused = Enabled && Focused;
            if (DrawSurfaceChrome)
            {
                EnsureAnimatedColors();
                Color targetFill, targetBorder;
                GetVisualTargets(out targetFill, out targetBorder);
                Color fill = _animatedFill;
                Color outline = focused ? Color.FromArgb(124, 156, 255) : _animatedBorder;
                float borderThickness = focused ? 2F : Math.Max(1, FlatAppearance.BorderSize);
                float inset = ChromeClipInset + borderThickness / 2F;
                float pathRadius = ChromeClipInset > 0F
                    ? Math.Max(0F, CornerRadius - borderThickness / 2F)
                    : CornerRadius;
                using (GraphicsPath path = RoundedDrawing.CreatePath(new RectangleF(inset, inset, Width - inset * 2 - 1, Height - inset * 2 - 1), pathRadius))
                using (SolidBrush brush = new SolidBrush(fill))
                using (Pen border = new Pen(outline, borderThickness))
                {
                    if (DrawSurfaceChrome) graphics.FillPath(brush, path);
                    graphics.DrawPath(border, path);
                    if (ShouldPaintLaunchPulse(focused))
                    {
                        int elapsed = Math.Max(0, unchecked(Environment.TickCount - _launchPulseStarted));
                        int alpha = Math.Max(0, 150 - elapsed * 150 / 240);
                        if (alpha > 0)
                        {
                            float pulseThickness = focused ? 1F : borderThickness + 1F;
                            using (Pen pulse = new Pen(Color.FromArgb(alpha, 116, 225, 205), pulseThickness))
                                graphics.DrawPath(pulse, path);
                        }
                    }
                }
            }
            TextRenderer.DrawText(graphics, Text, Font, ClientRectangle,
                Enabled ? ForeColor : Color.FromArgb(137, 146, 163),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
                (ShowKeyboardCues ? TextFormatFlags.Default : TextFormatFlags.HidePrefix));
            PaintEventHandler painter = PaintGlyph;
            if (painter != null) painter(this, e);
        }

        protected override void OnMouseEnter(EventArgs e) { _hovered = true; base.OnMouseEnter(e); UpdateAnimatedVisuals(); }
        protected override void OnMouseLeave(EventArgs e)
        {
            _pressed = false;
            base.OnMouseLeave(e);
            EventHandler leave = PointerLeaveRequested;
            if (leave != null) leave(this, EventArgs.Empty);
            else { _hovered = false; UpdateAnimatedVisuals(); }
        }
        protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) _pressed = true; base.OnMouseDown(e); UpdateAnimatedVisuals(); }
        protected override void OnMouseUp(MouseEventArgs e) { _pressed = false; base.OnMouseUp(e); UpdateAnimatedVisuals(); }
        protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode == Keys.Space) _pressed = true; base.OnKeyDown(e); UpdateAnimatedVisuals(); }
        protected override void OnKeyUp(KeyEventArgs e) { _pressed = false; base.OnKeyUp(e); UpdateAnimatedVisuals(); }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { _pressed = false; base.OnLostFocus(e); UpdateAnimatedVisuals(); }
        protected override void OnEnabledChanged(EventArgs e) { _pressed = false; base.OnEnabledChanged(e); SnapAnimatedVisuals(); }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _visualTimer != null)
            {
                _visualTimer.Stop();
                _visualTimer.Dispose();
                _visualTimer = null;
                _launchPulse = false;
                _animationTimerDisposed = true;
            }
            base.Dispose(disposing);
        }
    }

    internal sealed class InstallConfirmationDialog : Form
    {
        private static readonly Color WindowSurface = Color.FromArgb(25, 33, 43);
        private static readonly Color CardSurface = Color.FromArgb(35, 45, 59);
        private static readonly Color Accent = Color.FromArgb(89, 191, 174);
        private readonly PictureBox _icon;
        private readonly Panel _appPanel;
        private readonly ToolTip _toolTip;
        private readonly Font _windowFont;
        private readonly Font _eyebrowFont;
        private readonly Font _appNameFont;
        private readonly Font _bodyFont;
        private readonly RoundedActionButton _cancelButton;
        private readonly RoundedActionButton _installButton;

        internal static Color CardSurfaceForSmoke { get { return CardSurface; } }
        internal Button CancelActionForSmoke { get { return _cancelButton; } }
        internal DialogResult InstallActionResultForSmoke { get { return _installButton.DialogResult; } }
        internal string IconAccessibleNameForSmoke { get { return _icon.AccessibleName; } }

        internal InstallConfirmationDialog(BootstrapApp app)
        {
            if (app == null) throw new ArgumentNullException("app");
            Text = "앱 설치 확인";
            AccessibleRole = AccessibleRole.Dialog;
            AccessibleName = "앱 설치 확인";
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(440, 264);
            BackColor = WindowSurface;
            ForeColor = Color.FromArgb(241, 244, 250);
            _windowFont = new Font("Segoe UI", 9F);
            Font = _windowFont;
            MinimizeBox = false;
            MaximizeBox = false;
            ControlBox = false;
            KeyPreview = true;
            SetRoundedRegion();

            _eyebrowFont = new Font("Segoe UI Semibold", 10F, FontStyle.Bold);
            _appNameFont = new Font("Segoe UI Semibold", 12F, FontStyle.Bold);
            _bodyFont = new Font("Segoe UI", 9F, FontStyle.Regular);
            _toolTip = new ToolTip { ShowAlways = true };

            Label heading = new Label
            {
                Text = "앱 설치",
                Left = 22,
                Top = 18,
                Width = 396,
                Height = 22,
                Font = _eyebrowFont,
                ForeColor = Accent,
                BackColor = WindowSurface,
                AccessibleRole = AccessibleRole.StaticText
            };
            Controls.Add(heading);

            _appPanel = new Panel
            {
                Left = 20,
                Top = 52,
                Width = 400,
                Height = 116,
                BackColor = CardSurface,
                AccessibleRole = AccessibleRole.Grouping,
                AccessibleName = app.Name
            };
            _appPanel.SizeChanged += delegate { SetRoundedAppPanelRegion(); };
            SetRoundedAppPanelRegion();
            Controls.Add(_appPanel);

            _icon = new PictureBox
            {
                Left = 16,
                Top = 26,
                Width = 64,
                Height = 64,
                SizeMode = PictureBoxSizeMode.Zoom,
                BackColor = CardSurface,
                AccessibleRole = AccessibleRole.Graphic,
                AccessibleName = app.Name,
                AccessibleDescription = "설치할 앱 아이콘"
            };
            bool iconDecoded;
            _icon.Image = AppCard.DecodeIcon(app.IconBase64, out iconDecoded);
            _icon.AccessibleDescription = iconDecoded
                ? "설치할 앱 아이콘"
                : "앱 아이콘을 사용할 수 없어 기본 아이콘을 표시합니다";
            _appPanel.Controls.Add(_icon);

            Label appName = new Label
            {
                Text = app.Name,
                Left = 96,
                Top = 22,
                Width = 284,
                Height = 28,
                Font = _appNameFont,
                ForeColor = Color.FromArgb(241, 244, 250),
                BackColor = CardSurface,
                AutoSize = false,
                AutoEllipsis = true,
                AccessibleRole = AccessibleRole.StaticText,
                AccessibleName = "앱 이름"
            };
            _appPanel.Controls.Add(appName);
            _toolTip.SetToolTip(appName, app.Name);

            Label prompt = new Label
            {
                Text = "이 앱을 설치할까요?",
                Left = 96,
                Top = 54,
                Width = 284,
                Height = 28,
                Font = _bodyFont,
                ForeColor = Color.FromArgb(185, 195, 211),
                BackColor = CardSurface,
                AutoSize = false,
                AccessibleRole = AccessibleRole.StaticText,
                AccessibleName = "설치 확인 안내"
            };
            _appPanel.Controls.Add(prompt);

            _cancelButton = new RoundedActionButton
            {
                Text = "취소",
                Left = 194,
                Top = 202,
                Width = 100,
                Height = 40,
                CornerRadius = 11F,
                BackColor = CardSurface,
                ForeColor = Color.FromArgb(225, 231, 241),
                AccessibleRole = AccessibleRole.PushButton,
                AccessibleName = "설치 취소",
                AccessibleDescription = "설치를 취소합니다",
                TabStop = true,
                TabIndex = 0,
                DialogResult = DialogResult.Cancel
            };
            _cancelButton.FlatAppearance.BorderColor = Color.FromArgb(86, 101, 122);
            _cancelButton.FlatAppearance.BorderSize = 1;
            _cancelButton.FlatAppearance.MouseOverBackColor = Color.FromArgb(48, 61, 79);
            _cancelButton.FlatAppearance.MouseDownBackColor = Color.FromArgb(41, 53, 69);
            Controls.Add(_cancelButton);

            _installButton = new RoundedActionButton
            {
                Text = "설치",
                Left = 306,
                Top = 202,
                Width = 114,
                Height = 40,
                CornerRadius = 11F,
                BackColor = Accent,
                ForeColor = Color.FromArgb(17, 31, 34),
                AccessibleRole = AccessibleRole.PushButton,
                AccessibleName = "앱 설치 확인",
                AccessibleDescription = "선택한 앱 설치를 시작합니다",
                TabStop = true,
                TabIndex = 1,
                DialogResult = DialogResult.Yes
            };
            _installButton.FlatAppearance.BorderColor = Color.FromArgb(112, 213, 195);
            _installButton.FlatAppearance.BorderSize = 1;
            _installButton.FlatAppearance.MouseOverBackColor = Color.FromArgb(112, 213, 195);
            _installButton.FlatAppearance.MouseDownBackColor = Color.FromArgb(73, 165, 150);
            Controls.Add(_installButton);

            AcceptButton = _cancelButton;
            CancelButton = _cancelButton;
            Shown += delegate { _cancelButton.Focus(); };
        }

        internal static DialogResult Show(Form owner, BootstrapApp app)
        {
            using (InstallConfirmationDialog dialog = new InstallConfirmationDialog(app))
            {
                return owner == null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath path = RoundedDrawing.CreatePath(new RectangleF(0.5F, 0.5F, Width - 1F, Height - 1F), 18F))
            using (Pen border = new Pen(Color.FromArgb(63, 84, 101), 1F))
                e.Graphics.DrawPath(border, path);
        }

        protected override void OnDpiChanged(DpiChangedEventArgs e)
        {
            base.OnDpiChanged(e);
            float scale = DeviceDpi / 96F;
            if (_cancelButton != null) _cancelButton.CornerRadius = 11F * scale;
            if (_installButton != null) _installButton.CornerRadius = 11F * scale;
            SetRoundedAppPanelRegion();
            SetRoundedRegion();
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            SetRoundedRegion();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_toolTip != null) _toolTip.Dispose();
                if (_icon != null && _icon.Image != null) _icon.Image.Dispose();
                if (_windowFont != null) _windowFont.Dispose();
                if (_eyebrowFont != null) _eyebrowFont.Dispose();
                if (_appNameFont != null) _appNameFont.Dispose();
                if (_bodyFont != null) _bodyFont.Dispose();
                if (Region != null) Region.Dispose();
            }
            base.Dispose(disposing);
        }

        private void SetRoundedRegion()
        {
            if (Width <= 0 || Height <= 0) return;
            float radius = 18F * (DeviceDpi / 96F);
            using (GraphicsPath path = RoundedDrawing.CreatePath(new RectangleF(0, 0, Width, Height), radius))
            {
                Region previous = Region;
                Region = new Region(path);
                if (previous != null) previous.Dispose();
            }
        }

        private void SetRoundedAppPanelRegion()
        {
            if (_appPanel == null || _appPanel.Width <= 0 || _appPanel.Height <= 0) return;
            float radius = 14F * (DeviceDpi / 96F);
            using (GraphicsPath path = RoundedDrawing.CreatePath(
                new RectangleF(0, 0, _appPanel.ClientSize.Width, _appPanel.ClientSize.Height), radius))
            {
                Region previous = _appPanel.Region;
                _appPanel.Region = new Region(path);
                if (previous != null) previous.Dispose();
            }
        }
    }

    internal sealed class CategoryBadge : Control
    {
        public CategoryBadge()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            AccessibleRole = AccessibleRole.StaticText;
            TabStop = false;
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent == null ? SystemColors.Control : Parent.BackColor);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath path = RoundedDrawing.CreatePath(new RectangleF(0.5F, 0.5F, Width - 1, Height - 1), Height / 2F))
            using (SolidBrush fill = new SolidBrush(Color.FromArgb(34, 43, 58)))
                e.Graphics.FillPath(fill, path);
            TextRenderer.DrawText(e.Graphics, Text, Font, new Rectangle(4, 0, Width - 8, Height), ForeColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        }
    }

    internal sealed class AppCard : Panel
    {
        public BootstrapApp App { get; private set; }
        public bool HasDecodedIcon { get; private set; }
        public Size IconSourceSize { get { return _colorIconImage.Size; } }
        public bool IsInstalled { get { return _installed; } }
        public bool IsBusy { get { return _busy; } }
        public bool CanRun { get { return _actionsEnabled && _installed && !_busy; } }
        public string VersionTextForSmoke { get { return _versionLabel.Text; } }
        public float LaunchChromeInset { get { return _run.ChromeClipInset; } }
        public float LaunchChromeCornerRadius { get { return _run.CornerRadius; } }
        public bool CanChoosePrimaryAction { get { return _actionsEnabled && !_busy; } }
        public bool RunActionEnabled { get { return _run.Enabled; } }
        public bool InstallActionEnabled { get { return CanChoosePrimaryAction; } }
        public bool ContextRemoveAvailable { get { return _contextRemoveAvailable; } }
        public bool ContextFullDeleteAvailable { get { return _actionsEnabled && _installed && !_busy && _fullDeleteAction != null && _fullDeleteMenuItem.Enabled; } }
        public bool ContextUpdateAvailable { get { return _contextUpdateAvailable; } }
        public bool LaunchVisualMuted { get { return !_installed && Object.ReferenceEquals(_icon.Image, _mutedIconImage) && _name.ForeColor == MutedTitleColor && _status.ForeColor == MutedDescriptionColor; } }
        public Color CardSurfaceColor { get { return _installed ? InstalledSurfaceColor : UninstalledSurfaceColor; } }
        internal Color HoverSurfaceColorForSmoke { get { return ControlPaint.Light(CardSurfaceColor, 0.10F); } }
        public Color CardOutlineColor { get { return _installed ? InstalledOutlineColor : UninstalledOutlineColor; } }
        public float CardOutlineThickness { get { return _installed ? 2F : 1F; } }
        public Color FocusOutlineColor { get { return Color.FromArgb(124, 156, 255); } }
        public string RunActionLabel { get { return _installed ? "실행" : "설치"; } }
        public string RunAccessibleName { get { return _run.AccessibleName; } }
        public string RunAccessibleDescription { get { return _run.AccessibleDescription; } }
        public Rectangle RunActionBounds { get { return _run.Bounds; } }
        public Rectangle IconBounds { get { return _icon.Bounds; } }
        public Rectangle CardBounds { get { return Bounds; } }
        public string StatusLabel { get { return _status.Text; } }
        public string DescriptionText { get { return _status.Text; } }
        public bool ProgressVisible { get { return _progress.Visible; } }
        public bool ActionsLocked { get { return !_run.Enabled; } }
        internal bool VisualAnimationActiveForSmoke { get { return _run.VisualAnimationActiveForSmoke; } }
        internal bool LaunchAnimationTriggeredForSmoke { get { return _run.LaunchAnimationTriggeredForSmoke; } }
        internal bool LaunchPulseActiveForSmoke { get { return _run.LaunchPulseActiveForSmoke; } }
        internal bool LaunchPulsePaintsWhenFocusedForSmoke { get { return _run.LaunchPulsePaintsWhenFocusedForSmoke; } }
        internal bool SupportsAnimationForSmoke { get { return _run.SupportsAnimationForSmoke; } }
        internal Color AnimatedFillForSmoke { get { return _run.AnimatedFillForSmoke; } }
        internal bool VisualTimerDisposedForSmoke { get { return _run.VisualTimerDisposedForSmoke; } }
        public bool LaunchKeyboardAccessible { get { return _run.TabStop && _run.AccessibleRole == AccessibleRole.PushButton; } }
        public bool FocusLaunchSurface() { return CanChoosePrimaryAction && _run.Focus(); }
        public bool LaunchRegionContainsContent { get { return _run.Bounds.Contains(_icon.Bounds) && _run.Bounds.Contains(_name.Bounds) && _run.Bounds.Contains(_status.Bounds) && _run.Region.IsVisible(ToLaunchLocal(_icon.Bounds)) && _run.Region.IsVisible(ToLaunchLocal(_name.Bounds)) && _run.Region.IsVisible(ToLaunchLocal(_status.Bounds)); } }
        public bool HasCompactBusyState { get { return _status.Visible && _progress.Visible && !_run.Enabled && _status.Bounds == new Rectangle(92, 34, 176, 32); } }
        public bool HasCompactIdleState { get { return _status.Visible && !_progress.Visible && _status.Text == App.Description; } }
        private readonly PictureBox _icon;
        private readonly Label _name;
        private readonly CategoryBadge _categoryBadge;
        private readonly Label _versionLabel;
        public string LaunchGroupGeometry { get { return _icon.Bounds == new Rectangle(12, 20, 64, 64) && _name.Bounds == new Rectangle(92, 10, 100, 22) && _categoryBadge.Bounds == new Rectangle(204, 10, 64, 20) && _status.Bounds == new Rectangle(92, 34, 176, 32) && _run.Bounds == new Rectangle(0, 0, 280, 108) ? "matches" : "mismatch"; } }
        public string RegistrationActionGeometry { get { return _versionLabel.Bounds == new Rectangle(92, 72, 176, 24) && _progress.Bounds == new Rectangle(12, 103, 256, 3) ? "matches" : "mismatch"; } }
        public bool ContentClearsActions { get { return LaunchGroupGeometry == "matches" && RegistrationActionGeometry == "matches" && LaunchRegionContainsContent && ClientRectangle.Contains(_versionLabel.Bounds) && ClientRectangle.Contains(_progress.Bounds); } }
        public bool TitleHasFullTooltip { get { return _name.AutoEllipsis && _toolTip.GetToolTip(_name) == App.Name; } }
        private Rectangle ToLaunchLocal(Rectangle bounds) { return new Rectangle(bounds.Left - _run.Left, bounds.Top - _run.Top, bounds.Width, bounds.Height); }
        private readonly RoundedActionButton _run;
        private readonly Label _status;
        private readonly Image _colorIconImage;
        private readonly Image _mutedIconImage;
        private readonly bool _actionsEnabled;
        private readonly Action<BootstrapApp> _installFromPrompt;
        private readonly Action<BootstrapApp> _removeAction;
        private readonly Action<BootstrapApp> _fullDeleteAction;
        private readonly Action<BootstrapApp> _updateAction;
        private readonly Action<BootstrapApp> _runAction;
        private readonly Func<Form, BootstrapApp, DialogResult> _confirmInstall;
        private readonly Func<BootstrapApp, BootstrapperAppStatus> _refreshStatus;
        private readonly ContextMenuStrip _contextMenu;
        private readonly ToolStripMenuItem _removeMenuItem;
        private readonly ToolStripMenuItem _fullDeleteMenuItem;
        private readonly ToolStripMenuItem _updateMenuItem;
        private readonly ProgressBar _progress;
        private bool _installed;
        private bool _busy;
        private bool _contextRemoveAvailable;
        private bool _contextUpdateAvailable;
        private bool _updateAvailable;
        private bool _hoverExitCheckPending;
        private static readonly Color MutedTitleColor = Color.FromArgb(205, 214, 228);
        private static readonly Color MutedDescriptionColor = Color.FromArgb(155, 167, 186);
        private static readonly Color InstalledSurfaceColor = Color.FromArgb(25, 39, 42);
        private static readonly Color UninstalledSurfaceColor = Color.FromArgb(35, 45, 59);
        private static readonly Color InstalledOutlineColor = Color.FromArgb(89, 191, 174);
        private static readonly Color UninstalledOutlineColor = Color.FromArgb(117, 131, 152);
        public AppCard(BootstrapApp app, int width, Action<BootstrapApp> install,
            Action<BootstrapApp> remove, Action<BootstrapApp> run, bool actionsEnabled,
            Action<BootstrapApp> installFromPrompt = null,
            Func<Form, BootstrapApp, DialogResult> confirmInstall = null,
            Func<BootstrapApp, BootstrapperAppStatus> refreshStatus = null,
            Action<BootstrapApp> update = null,
            Action<BootstrapApp> fullDelete = null)
        {
            App = app;
            _actionsEnabled = actionsEnabled;
            _installFromPrompt = installFromPrompt ?? install;
            _removeAction = remove;
            _fullDeleteAction = fullDelete;
            _updateAction = update;
            _runAction = run;
            _confirmInstall = confirmInstall ?? ShowInstallConfirmation;
            _refreshStatus = refreshStatus;
            Width = width; Height = 108; Margin = new Padding(0, 0, 16, 12); Padding = Padding.Empty;
            BackColor = UninstalledSurfaceColor; BorderStyle = BorderStyle.None;
            DoubleBuffered = true;
            _run = new RoundedActionButton { Text = "", Left = 0, Top = 0, Width = width, Height = 108, CornerRadius = 10, ChromeClipInset = 2F, DrawSurfaceChrome = true,
                AccessibleRole = AccessibleRole.PushButton, TabStop = true, AccessibleName = "설치 " + app.Name,
                AccessibleDescription = "설치를 시작하려면 선택하세요", Cursor = Cursors.Hand, BackColor = BackColor };
            using (GraphicsPath primaryPath = RoundedDrawing.CreatePath(new RectangleF(2F, 2F, width - 4F, 104F), 10F))
                _run.Region = new Region(primaryPath);
            Controls.Add(_run);
            _icon = new PictureBox { Left = 12, Top = 20, Width = 64, Height = 64, SizeMode = PictureBoxSizeMode.Zoom, AccessibleName = app.Name, AccessibleDescription = app.Category + ": " + app.Description, Cursor = Cursors.Hand, BackColor = BackColor };
            using (GraphicsPath iconPath = RoundedDrawing.CreatePath(new RectangleF(0, 0, 64, 64), 12)) _icon.Region = new Region(iconPath);
            bool iconDecoded;
            _colorIconImage = DecodeIcon(app.IconBase64, out iconDecoded);
            _mutedIconImage = CreateMutedIcon(_colorIconImage);
            _icon.Image = _mutedIconImage;
            HasDecodedIcon = iconDecoded;
            Controls.Add(_icon);
            _name = new Label { Text = app.Name, Left = 92, Top = 10, Width = 100, Height = 22, TextAlign = ContentAlignment.MiddleLeft, Font = new Font("Segoe UI Semibold", 10.5F), ForeColor = Color.White, BackColor = BackColor, AutoSize = false, AutoEllipsis = true, Cursor = Cursors.Hand, AccessibleRole = AccessibleRole.StaticText };
            Controls.Add(_name);
            _categoryBadge = new CategoryBadge { Text = app.Category, Left = width - 76, Top = 10, Width = 64, Height = 20, Anchor = AnchorStyles.Top | AnchorStyles.Right,
                Font = new Font("Segoe UI", 7.5F), ForeColor = Color.FromArgb(198, 211, 255), BackColor = BackColor, Cursor = Cursors.Hand, AccessibleName = app.Category };
            Controls.Add(_categoryBadge);
            _status = new Label { Text = app.Description, Left = 92, Top = 34, Width = 176, Height = 32, Font = new Font("Segoe UI", 8.5F), Visible = true, ForeColor = Color.FromArgb(165, 174, 190), BackColor = BackColor, AutoSize = false, AutoEllipsis = true, AccessibleRole = AccessibleRole.StaticText, AccessibleName = app.Name + " 설명", Cursor = Cursors.Hand };
            Controls.Add(_status);
            _versionLabel = new Label { Text = "", Left = 92, Top = 72, Width = 176, Height = 24, Font = new Font("Segoe UI", 8.5F), Visible = true, ForeColor = Color.FromArgb(142, 153, 175), BackColor = BackColor, AutoSize = false, AutoEllipsis = true, AccessibleRole = AccessibleRole.StaticText, AccessibleName = app.Name + " 버전" };
            Controls.Add(_versionLabel);
            _run.AnimatedFillChanged += ApplyAnimatedSurfaceColor;
            _progress = new ProgressBar { Left = 12, Top = 103, Width = width - 24, Height = 3, Visible = false, Style = ProgressBarStyle.Marquee, AccessibleName = app.Name + " 진행 상황" };
            Controls.Add(_progress);
            _toolTip = new ToolTip { ShowAlways = true };
            _toolTip.SetToolTip(_name, app.Name);
            _toolTip.SetToolTip(_run, _run.AccessibleDescription);
            _toolTip.SetToolTip(_icon, app.Name + "\r\n" + app.Category + " · " + app.Description);
            _toolTip.SetToolTip(this, app.Category + " · " + app.Description);
            LauncherFormStyle(_run, true);
            _run.Click += delegate { HandlePrimaryAction(); };
            Click += delegate { _run.PerformClick(); };
            _run.PointerLeaveRequested += delegate { SchedulePointerHoverReset(); };
            _run.GotFocus += delegate { Invalidate(); };
            _run.LostFocus += delegate { Invalidate(); };
            _run.EnabledChanged += delegate { Invalidate(); };
            _icon.Click += delegate { _run.PerformClick(); };
            _name.Click += delegate { _run.PerformClick(); };
            _categoryBadge.Click += delegate { _run.PerformClick(); };
            _status.Click += delegate { _run.PerformClick(); };
            _icon.MouseEnter += delegate { _run.SetPointerHover(true); };
            _icon.MouseLeave += delegate { SchedulePointerHoverReset(); };
            _name.MouseEnter += delegate { _run.SetPointerHover(true); };
            _name.MouseLeave += delegate { SchedulePointerHoverReset(); };
            _categoryBadge.MouseEnter += delegate { _run.SetPointerHover(true); };
            _categoryBadge.MouseLeave += delegate { SchedulePointerHoverReset(); };
            _status.MouseEnter += delegate { _run.SetPointerHover(true); };
            _status.MouseLeave += delegate { SchedulePointerHoverReset(); };
            _contextMenu = new ContextMenuStrip();
            Controls.SetChildIndex(_run, Controls.Count - 1);
            _updateMenuItem = new ToolStripMenuItem("업데이트") { AccessibleName = app.Name + " 업데이트", Visible = false };
            _removeMenuItem = new ToolStripMenuItem("제거") { AccessibleName = "" + app.Name + " 제거", Visible = false };
            _fullDeleteMenuItem = new ToolStripMenuItem("완전 삭제") { AccessibleName = app.Name + " 완전 삭제", Visible = false, Enabled = false };
            _contextMenu.Items.Add(_updateMenuItem);
            _contextMenu.Items.Add(_removeMenuItem);
            _contextMenu.Items.Add(_fullDeleteMenuItem);
            _contextMenu.Opening += ContextMenu_Opening;
            _updateMenuItem.Click += delegate { UpdateFromContextMenu(); };
            _removeMenuItem.Click += delegate { RemoveFromContextMenu(); };
            _fullDeleteMenuItem.Click += delegate { FullDeleteFromContextMenu(); };
            ContextMenuStrip = _contextMenu;
            _run.ContextMenuStrip = _contextMenu;
            _icon.ContextMenuStrip = _contextMenu;
            _name.ContextMenuStrip = _contextMenu;
            _categoryBadge.ContextMenuStrip = _contextMenu;
            _status.ContextMenuStrip = _contextMenu;
            _versionLabel.ContextMenuStrip = _contextMenu;
            _versionLabel.Click += delegate { _run.PerformClick(); };
            _versionLabel.MouseEnter += delegate { _run.SetPointerHover(true); };
            _versionLabel.MouseLeave += delegate { SchedulePointerHoverReset(); };
            ApplyLaunchState();
            ApplyAnimatedSurfaceColor(_run.AnimatedFillForSmoke);
        }
        public void SetStatus(BootstrapperAppStatus status)
        {
            _installed = status != null && status.IsInstalled;
            _updateAvailable = status != null && status.UpdateAvailable;
            if (!_busy) _status.Text = App.Description;
            if (!_busy)
            {
                string available = status == null ? null : status.AvailableVersion;
                string installed = status == null ? null : status.InstalledVersion;
                if (_installed && _updateAvailable)
                {
                    _versionLabel.Text = String.IsNullOrEmpty(installed)
                        ? (String.IsNullOrEmpty(available) ? "버전 미확인" : "최신 v" + available)
                        : (String.IsNullOrEmpty(available) || String.Equals(installed, available, StringComparison.OrdinalIgnoreCase)
                            ? "v" + installed + " · 업데이트"
                            : "v" + installed + " → v" + available);
                    _versionLabel.ForeColor = Color.FromArgb(168, 218, 181);
                    _toolTip.SetToolTip(_versionLabel, String.IsNullOrEmpty(installed)
                        ? (String.IsNullOrEmpty(available)
                            ? "설치 및 사용 가능 버전 정보를 확인할 수 없습니다. 업데이트할 수 있습니다."
                            : "현재 설치 버전 정보는 확인할 수 없습니다. 사용 가능 버전 v" + available)
                        : (String.IsNullOrEmpty(available)
                            ? "설치 버전 v" + installed + " · 업데이트할 수 있습니다."
                            : "설치 버전 v" + installed + " · 사용 가능 v" + available));
                }
                else if (_installed)
                {
                    _versionLabel.Text = String.IsNullOrEmpty(installed) ? "버전 미확인" : "v" + installed;
                    _versionLabel.ForeColor = Color.FromArgb(142, 153, 175);
                    _toolTip.SetToolTip(_versionLabel, String.IsNullOrEmpty(installed)
                        ? "현재 설치 버전 정보를 확인할 수 없습니다."
                        : "설치 버전 v" + installed);
                }
                else
                {
                    _versionLabel.Text = String.IsNullOrEmpty(available) ? "버전 미확인" : "v" + available;
                    _versionLabel.ForeColor = Color.FromArgb(142, 153, 175);
                    _toolTip.SetToolTip(_versionLabel, String.IsNullOrEmpty(available) ? "" : "사용 가능 버전 v" + available);
                }
            }
            if (!_installed || _busy || !_actionsEnabled) _contextRemoveAvailable = false;
            if (!_installed || !_updateAvailable || _busy || !_actionsEnabled) _contextUpdateAvailable = false;
            ApplyLaunchState();
        }
        public void SetSmokeInstalledStatus()
        {
            _installed = true;
            _updateAvailable = false;
            _versionLabel.Text = "버전 미확인";
            _toolTip.SetToolTip(_versionLabel, "현재 설치 버전 정보를 확인할 수 없습니다.");
            if (!_busy) _status.Text = App.Description;
            ApplyLaunchState();
        }
        public void EndLaunchAnimation() { _run.EndLaunchAnimation(); }

        public void SetBusy(bool busy, string busyLabel)
        {
            _busy = busy;
            if (busy) { _contextRemoveAvailable = false; _contextUpdateAvailable = false; }
            _progress.Visible = busy;
            _status.Text = busy ? (String.IsNullOrEmpty(busyLabel) ? "실행 중…" : busyLabel) : App.Description;
            ApplyLaunchState();
        }
        public void PerformPrimaryActionForSmoke() { HandlePrimaryAction(); }
        internal void SetPointerHoverForSmoke(bool hovered) { _run.SetPointerHover(hovered); }
        internal void AdvanceAnimationForSmoke(int elapsedMilliseconds) { _run.AdvanceAnimationForSmoke(elapsedMilliseconds); }
        internal void CompleteAnimationForSmoke() { _run.CompleteAnimationForSmoke(); }
        public void RefreshContextMenuForSmoke() { ContextMenu_Opening(this, new CancelEventArgs()); }
        public void PerformContextRemoveForSmoke() { if (ContextRemoveAvailable) RemoveFromContextMenu(); }
        public void PerformContextFullDeleteForSmoke() { if (ContextFullDeleteAvailable) FullDeleteFromContextMenu(); }
        public void PerformContextUpdateForSmoke() { if (ContextUpdateAvailable) UpdateFromContextMenu(); }
        private void HandlePrimaryAction()
        {
            if (!CanChoosePrimaryAction) return;
            if (_installed)
            {
                if (CanRun && _runAction != null)
                {
                    _run.StartLaunchAnimation();
                    _runAction(App);
                }
                return;
            }
            if (_confirmInstall(FindForm(), App) != DialogResult.Yes || !CanChoosePrimaryAction || _installed) return;
            if (_installFromPrompt != null) _installFromPrompt(App);
        }

        private void SchedulePointerHoverReset()
        {
            if (_hoverExitCheckPending || IsDisposed) return;
            if (!IsHandleCreated)
            {
                _run.SetPointerHover(false);
                return;
            }
            _hoverExitCheckPending = true;
            try
            {
                BeginInvoke((MethodInvoker)delegate
                {
                    _hoverExitCheckPending = false;
                    if (IsDisposed) return;
                    Point cursor = PointToClient(Control.MousePosition);
                    if (!ClientRectangle.Contains(cursor)) _run.SetPointerHover(false);
                });
            }
            catch (InvalidOperationException)
            {
                _hoverExitCheckPending = false;
                if (!IsDisposed) _run.SetPointerHover(false);
            }
        }

        private void ApplyAnimatedSurfaceColor(Color color)
        {
            BackColor = color;
            if (_icon != null) _icon.BackColor = color;
            if (_name != null) _name.BackColor = color;
            if (_categoryBadge != null) _categoryBadge.BackColor = color;
            if (_status != null) _status.BackColor = color;
            if (_versionLabel != null) _versionLabel.BackColor = color;
        }
        private static DialogResult ShowInstallConfirmation(Form owner, BootstrapApp app)
        {
            return InstallConfirmationDialog.Show(owner, app);
        }
        private void ContextMenu_Opening(object sender, CancelEventArgs e)
        {
            if (!_actionsEnabled || _busy) { HideActionMenuItems(); e.Cancel = true; return; }
            try { if (_refreshStatus != null) SetStatus(_refreshStatus(App)); }
            catch { HideActionMenuItems(); e.Cancel = true; return; }
            _updateMenuItem.Visible = _installed && _updateAvailable;
            _updateMenuItem.Enabled = _installed && _updateAvailable && !_busy && _actionsEnabled;
            _contextUpdateAvailable = _updateMenuItem.Enabled;
            _removeMenuItem.Visible = _installed;
            _removeMenuItem.Enabled = _installed && !_busy && _actionsEnabled;
            _contextRemoveAvailable = _removeMenuItem.Enabled;
            _fullDeleteMenuItem.Visible = _installed && _fullDeleteAction != null;
            _fullDeleteMenuItem.Enabled = _installed && !_busy && _actionsEnabled && _fullDeleteAction != null;
            e.Cancel = !_contextRemoveAvailable && !_contextUpdateAvailable;
        }
        private void HideActionMenuItems()
        {
            _contextRemoveAvailable = false;
            _contextUpdateAvailable = false;
            _removeMenuItem.Visible = false;
            _removeMenuItem.Enabled = false;
            _fullDeleteMenuItem.Visible = false;
            _fullDeleteMenuItem.Enabled = false;
            _updateMenuItem.Visible = false;
            _updateMenuItem.Enabled = false;
        }
        private void UpdateFromContextMenu()
        {
            if (!_actionsEnabled || _busy || _updateAction == null) return;
            try { if (_refreshStatus != null) SetStatus(_refreshStatus(App)); }
            catch { return; }
            if (_installed && _updateAvailable && !_busy) _updateAction(App);
        }
        private void RemoveFromContextMenu()
        {
            if (!_actionsEnabled || _busy || _removeAction == null) return;
            try { if (_refreshStatus != null) SetStatus(_refreshStatus(App)); }
            catch { return; }
            if (_installed && !_busy) _removeAction(App);
        }
        private void FullDeleteFromContextMenu()
        {
            if (!_actionsEnabled || _busy || _fullDeleteAction == null) return;
            try { if (_refreshStatus != null) SetStatus(_refreshStatus(App)); }
            catch { return; }
            if (_installed && !_busy) _fullDeleteAction(App);
        }
        private void ApplyLaunchState()
        {
            BackColor = CardSurfaceColor;
            _run.BackColor = CardSurfaceColor;
            _run.FlatAppearance.BorderColor = CardOutlineColor;
            _run.FlatAppearance.BorderSize = (int)CardOutlineThickness;
            _run.FlatAppearance.MouseOverBackColor = ControlPaint.Light(CardSurfaceColor, 0.10F);
            _run.FlatAppearance.MouseDownBackColor = ControlPaint.Dark(CardSurfaceColor, 0.10F);
            _icon.BackColor = CardSurfaceColor;
            _name.BackColor = CardSurfaceColor;
            _categoryBadge.BackColor = CardSurfaceColor;
            _status.BackColor = CardSurfaceColor;
            _versionLabel.BackColor = CardSurfaceColor;
            _run.Enabled = CanChoosePrimaryAction;
            _run.RefreshAnimatedVisuals(false);
            _icon.Enabled = true;
            _icon.Image = _installed ? _colorIconImage : _mutedIconImage;
            _name.ForeColor = _installed ? Color.White : MutedTitleColor;
            _status.ForeColor = _installed ? Color.FromArgb(165, 174, 190) : MutedDescriptionColor;
            _run.AccessibleName = (_installed ? "실행 " : "설치 ") + App.Name;
            _run.AccessibleDescription = _installed ? "앱 실행" : "설치를 시작하려면 선택하세요";
            _status.AccessibleRole = _busy ? AccessibleRole.StatusBar : AccessibleRole.StaticText;
            _status.AccessibleName = _busy ? App.Name + " 상태" : App.Name + " 설명";
            if (_toolTip != null)
            {
                _toolTip.SetToolTip(_run, _run.AccessibleDescription);
                _toolTip.SetToolTip(_icon, _run.AccessibleDescription);
                _toolTip.SetToolTip(_name, App.Name);
                _toolTip.SetToolTip(_status, _busy ? _status.Text : (_installed ? App.Description : _run.AccessibleDescription));
                _toolTip.SetToolTip(this, _run.AccessibleDescription);
            }
        }
        private readonly ToolTip _toolTip;
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent == null ? UninstalledSurfaceColor : Parent.BackColor);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath path = RoundedDrawing.CreatePath(new RectangleF(0.5F, 0.5F, Width - 1F, Height - 1F), 12))
            using (SolidBrush surface = new SolidBrush(BackColor))
            {
                e.Graphics.FillPath(surface, path);
            }
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _toolTip.Dispose();
                _contextMenu.Dispose();
                _name.Font.Dispose();
                _categoryBadge.Font.Dispose();
                _status.Font.Dispose();
                _versionLabel.Font.Dispose();
                _colorIconImage.Dispose();
                _mutedIconImage.Dispose();
            }
            base.Dispose(disposing);
        }
        internal static Image DecodeIcon(string text, out bool decoded)
        {
            try
            {
                byte[] bytes = Convert.FromBase64String(text);
                if (bytes.Length > 1024 * 1024) throw new InvalidDataException("Icon exceeds its size limit.");
                using (MemoryStream stream = new MemoryStream(bytes))
                {
                    if (bytes.Length >= 4 && bytes[0] == 0 && bytes[1] == 0 && bytes[2] == 1 && bytes[3] == 0)
                    {
                        if (bytes.Length >= 22)
                        {
                            uint imageLength = BitConverter.ToUInt32(bytes, 14);
                            uint imageOffset = BitConverter.ToUInt32(bytes, 18);
                            if (imageLength > 0 && imageLength <= bytes.Length && imageOffset >= 22 &&
                                imageOffset <= bytes.Length && imageLength <= bytes.Length - imageOffset)
                            {
                                int offset = checked((int)imageOffset);
                                int length = checked((int)imageLength);
                                if (length >= 8 && bytes[offset] == 0x89 && bytes[offset + 1] == 0x50 &&
                                    bytes[offset + 2] == 0x4E && bytes[offset + 3] == 0x47)
                                {
                                    using (MemoryStream png = new MemoryStream(bytes, offset, length))
                                    using (Image image = Image.FromStream(png))
                                    {
                                        if (image.Width > 512 || image.Height > 512) throw new InvalidDataException("Icon dimensions exceed the display limit.");
                                        decoded = true;
                                        return new Bitmap(image);
                                    }
                                }
                            }
                        }
                        using (Icon icon = new Icon(stream, 64, 64))
                        {
                            if (icon.Width > 512 || icon.Height > 512) throw new InvalidDataException("Icon dimensions exceed the display limit.");
                            decoded = true;
                            return icon.ToBitmap();
                        }
                    }
                    using (Image image = Image.FromStream(stream))
                    {
                        if (image.Width > 512 || image.Height > 512) throw new InvalidDataException("Icon dimensions exceed the display limit.");
                        decoded = true;
                        return new Bitmap(image);
                    }
                }
            }
            catch { decoded = false; return SystemIcons.Application.ToBitmap(); }
        }
        private static Image CreateMutedIcon(Image source)
        {
            Bitmap muted = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
            using (Graphics graphics = Graphics.FromImage(muted))
            using (ImageAttributes attributes = new ImageAttributes())
            {
                ColorMatrix colorPreserving = new ColorMatrix(new[]
                {
                    new[] { 1F, 0F, 0F, 0F, 0F },
                    new[] { 0F, 1F, 0F, 0F, 0F },
                    new[] { 0F, 0F, 1F, 0F, 0F },
                    new[] { 0F, 0F, 0F, 0.85F, 0F },
                    new[] { 0F, 0F, 0F, 0F, 1F }
                });
                attributes.SetColorMatrix(colorPreserving);
                graphics.DrawImage(source, new Rectangle(0, 0, source.Width, source.Height),
                    0, 0, source.Width, source.Height, GraphicsUnit.Pixel, attributes);
            }
            return muted;
        }
        private static void LauncherFormStyle(Button button, bool primary)
        {
            button.FlatStyle = FlatStyle.Flat; button.FlatAppearance.BorderColor = Color.FromArgb(61, 70, 86);
            button.BackColor = primary ? Color.FromArgb(50, 91, 168) : Color.FromArgb(37, 43, 55); button.ForeColor = Color.White; button.Cursor = Cursors.Hand;
        }
    }
}
