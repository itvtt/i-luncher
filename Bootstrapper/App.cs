using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
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
                if (smokeRealProfile && !smokeUi)
                    throw new ArgumentException("--smoke-real-profile requires --smoke-ui.");
                BootstrapperEngine engine = EngineFactory.Create(catalog, smokeUi && !smokeRealProfile);
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
        public static BootstrapperEngine Create(BootstrapCatalog catalog, bool smokeOnly)
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
                    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "i-luncher")
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
            int cardWidth = 220;
            foreach (BootstrapApp app in catalog.Apps)
            {
                AppCard card = new AppCard(app, cardWidth, InstallClicked, RemoveClicked, RunClicked, true);
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
                using (Bitmap screenshot = new Bitmap(Width, Height))
                {
                    DrawToBitmap(screenshot, new Rectangle(Point.Empty, Size));
                    screenshot.Save(screenshotPath, System.Drawing.Imaging.ImageFormat.Png);
                }
            }
            catch (Exception ex) { screenshotError = Program.ShortError(ex); }
            Dictionary<string, object> report = new Dictionary<string, object>
            {
                { "firstPaintMs", _firstPaintMs < 0 ? _startup.ElapsedMilliseconds : _firstPaintMs },
                { "bounds", new Dictionary<string, int> { { "x", bounds.X }, { "y", bounds.Y }, { "width", bounds.Width }, { "height", bounds.Height } } },
                { "cards", _cards.Count },
                { "appStatuses", _cards.Values.OrderBy(card => card.App.Id, StringComparer.OrdinalIgnoreCase)
                    .Select(card => new Dictionary<string, object>
                    {
                        { "appId", card.App.Id },
                        { "installed", card.IsInstalled },
                        { "action", card.InstallActionLabel },
                        { "actionColor", card.InstallActionColor },
                        { "actionAccessibleName", card.InstallAccessibleName },
                        { "actionPlacement", "grouped-right-column" },
                        { "installBounds", RectangleToDictionary(card.InstallActionBounds) },
                        { "portableAction", card.RunActionLabel },
                        { "portablePlacement", card.RunActionPlacement },
                        { "runBounds", RectangleToDictionary(card.RunActionBounds) },
                        { "cardBounds", RectangleToDictionary(card.CardBounds) },
                        { "status", card.StatusLabel },
                        { "installAboveRun", card.InstallActionBounds.Bottom + 8 == card.RunActionBounds.Top && card.InstallActionBounds.Left == card.RunActionBounds.Left && card.InstallActionBounds.Size == card.RunActionBounds.Size },
                        { "titleHasFullTooltip", card.TitleHasFullTooltip },
                        { "iconSourceWidth", card.IconSourceSize.Width },
                        { "iconSourceHeight", card.IconSourceSize.Height }
                    }).ToArray() },
                { "gridColumns", _cards.Count == 0 ? 0 : _cards.Values.Select(card => card.Left).Distinct().Count() },
                { "iconsDecoded", _cards.Values.Count(card => card.HasDecodedIcon) },
                { "smokeInstalledAppId", _smokeInstalledAppId },
                { "smokeInstalledAction", String.IsNullOrWhiteSpace(_smokeInstalledAppId) ? null : _cards[_smokeInstalledAppId].InstallActionLabel },
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
            try
            {
                sample.SetBusy(true, "설치 중…");
                result["busyShowsTransientStatus"] = sample.HasCompactBusyState;
                sample.SetBusy(false, null);
                result["idleRestoresMinimalCard"] = sample.HasCompactIdleState;
                while (_grid.Controls.Count < 9)
                {
                    BootstrapApp fixtureApp = _cards.Values.ElementAt(fixtures.Count % _cards.Count).App;
                    AppCard fixture = new AppCard(fixtureApp, 220, delegate { }, delegate { }, delegate { }, true);
                    fixture.SetStatus(null);
                    fixtures.Add(fixture);
                    _grid.Controls.Add(fixture);
                }
                _grid.PerformLayout();
                Application.DoEvents();
                AppCard[] all = _grid.Controls.OfType<AppCard>().ToArray();
                _resultCount.Text = all.Length + "개 앱";
                result["compactCardHeight"] = all.All(card => card.Height == 104);
                result["iconTitleAndActionsDoNotOverlap"] = all.All(card => card.ContentClearsActions);
                result["fullTitlesFitNameArea"] = all.All(card => card.TitleFitsNameArea);
                result["nineCardsFitThreeRows"] = all.Length == 9 && all.Select(card => card.Left).Distinct().Count() == 4 &&
                    all.Select(card => card.Top).Distinct().Count() == 3 && !_grid.VerticalScroll.Visible && !_grid.HorizontalScroll.Visible &&
                    all.Max(card => card.Bottom) <= _grid.ClientSize.Height;
                string path = Path.ChangeExtension(Path.GetFullPath(_reportPath), ".nine-cards.png");
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                using (Bitmap screenshot = new Bitmap(Width, Height))
                {
                    DrawToBitmap(screenshot, new Rectangle(Point.Empty, Size));
                    screenshot.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                }
                result["nineCardScreenshotPath"] = path;
                AppCard busyFixture = fixtures.FirstOrDefault(card => card.App.Name.Contains(" ")) ?? fixtures.FirstOrDefault();
                if (busyFixture != null)
                {
                    busyFixture.SetBusy(true, "설치 중…");
                    using (Bitmap screenshot = new Bitmap(Width, Height))
                    {
                        DrawToBitmap(screenshot, new Rectangle(Point.Empty, Size));
                        string busyPath = Path.ChangeExtension(Path.GetFullPath(_reportPath), ".busy.png");
                        screenshot.Save(busyPath, System.Drawing.Imaging.ImageFormat.Png);
                        result["busyFixtureScreenshotPath"] = busyPath;
                    }
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
                    new Rectangle(Point.Empty, card.CardBounds.Size).Contains(card.RunActionBounds) &&
                    new Rectangle(Point.Empty, card.CardBounds.Size).Contains(card.InstallActionBounds));
                RoundedActionButton lastCategory = _filterButtons.Values.OrderBy(button => button.Top).Last();
                ScrollableControl sidebar = (ScrollableControl)lastCategory.Parent;
                sidebar.ScrollControlIntoView(lastCategory);
                minimumSizeSidebarEndAccessible = sidebar.ClientRectangle.Contains(lastCategory.Bounds);
                _grid.AutoScrollPosition = Point.Empty;
                using (Bitmap screenshot = new Bitmap(Width, Height))
                {
                    DrawToBitmap(screenshot, new Rectangle(Point.Empty, Size));
                    string path = Path.ChangeExtension(Path.GetFullPath(_reportPath), ".minimum.png");
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    screenshot.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                }
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
        private async void RemoveClicked(BootstrapApp app)
        {
            if (_smokeUi) return;
            string confirmation = "\"" + app.Name + "\" 앱을 제거할까요?\r\n\r\n" +
                "이 앱의 개별 설치 등록과 시작 메뉴 바로가기만 제거합니다. " +
                "다운로드한 파일, 공용 실행 환경, 사용자 데이터는 유지됩니다.";
            if (MessageBox.Show(this, confirmation, "앱 제거", MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                return;
            if (!_inFlight.Add(app.Id)) return;
            SetBusy(app.Id, true, "제거 중…");
            _statusBar.SetState("working");
            _status.Text = app.Name + " 제거 중…";
            try
            {
                BootstrapperUninstallResult result = await _engine.UninstallAsync(app.Id, CancellationToken.None);
                _cards[app.Id].SetStatus(_engine.GetStatus(app.Id));
                SetOperationResultState(result.Removed ? "success" : "ready");
                _status.Text = result.Removed ? "제거 완료: " + app.Name : "제거하지 않았습니다: " + app.Name;
            }
            catch (Exception ex)
            {
                _cards[app.Id].SetStatus(_engine.GetStatus(app.Id));
                SetOperationResultState("error");
                _status.Text = "제거 실패: " + Program.ShortError(ex);
            }
            finally { FinishOperation(app.Id); }
        }
        private async void RunClicked(BootstrapApp app)
        {
            if (_smokeUi) return;
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
            finally { FinishOperation(app.Id); }
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
        private bool _hovered;
        private bool _pressed;
        internal event PaintEventHandler PaintGlyph;

        public RoundedActionButton()
        {
            CornerRadius = 11;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            FlatStyle = FlatStyle.Flat;
            UseVisualStyleBackColor = false;
            SetStyle(ControlStyles.Opaque, false);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent == null ? SystemColors.Control : Parent.BackColor);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics graphics = e.Graphics;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Color fill = BackColor;
            if (Enabled && _pressed)
                fill = FlatAppearance.MouseDownBackColor.IsEmpty ? ControlPaint.Dark(BackColor, 0.12F) : FlatAppearance.MouseDownBackColor;
            else if (Enabled && _hovered)
                fill = FlatAppearance.MouseOverBackColor.IsEmpty ? ControlPaint.Light(BackColor, 0.12F) : FlatAppearance.MouseOverBackColor;
            bool focused = Enabled && Focused;
            float inset = focused ? 1F : 0.5F;
            using (GraphicsPath path = RoundedDrawing.CreatePath(new RectangleF(inset, inset, Width - inset * 2 - 1, Height - inset * 2 - 1), CornerRadius))
            using (SolidBrush brush = new SolidBrush(fill))
            using (Pen border = new Pen(focused ? Color.FromArgb(124, 156, 255) : FlatAppearance.BorderColor, focused ? 2F : 1F))
            {
                graphics.FillPath(brush, path);
                graphics.DrawPath(border, path);
            }
            TextRenderer.DrawText(graphics, Text, Font, ClientRectangle,
                Enabled ? ForeColor : Color.FromArgb(137, 146, 163),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
                (ShowKeyboardCues ? TextFormatFlags.Default : TextFormatFlags.HidePrefix));
            PaintEventHandler painter = PaintGlyph;
            if (painter != null) painter(this, e);
        }

        protected override void OnMouseEnter(EventArgs e) { _hovered = true; base.OnMouseEnter(e); Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { _hovered = false; _pressed = false; base.OnMouseLeave(e); Invalidate(); }
        protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) _pressed = true; base.OnMouseDown(e); Invalidate(); }
        protected override void OnMouseUp(MouseEventArgs e) { _pressed = false; base.OnMouseUp(e); Invalidate(); }
        protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode == Keys.Space) _pressed = true; base.OnKeyDown(e); Invalidate(); }
        protected override void OnKeyUp(KeyEventArgs e) { _pressed = false; base.OnKeyUp(e); Invalidate(); }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { _pressed = false; base.OnLostFocus(e); Invalidate(); }
        protected override void OnEnabledChanged(EventArgs e) { _pressed = false; base.OnEnabledChanged(e); Invalidate(); }
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
            TextRenderer.DrawText(e.Graphics, Text, Font, new Rectangle(9, 0, Width - 18, Height), ForeColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        }
    }

    internal sealed class AppCard : Panel
    {
        public BootstrapApp App { get; private set; }
        public bool HasDecodedIcon { get; private set; }
        public Size IconSourceSize { get { return _icon.Image.Size; } }
        public bool IsInstalled { get { return _installed; } }
        public string InstallActionLabel { get { return _installed ? "제거" : "설치"; } }
        public string InstallActionColor { get { return ColorTranslator.ToHtml(_install.BackColor); } }
        public string InstallAccessibleName { get { return _install.AccessibleName; } }
        public string RunActionLabel { get { return "실행"; } }
        public string RunActionPlacement { get { return "right-side"; } }
        public Rectangle RunActionBounds { get { return _run.Bounds; } }
        public Rectangle InstallActionBounds { get { return _install.Bounds; } }
        public Rectangle CardBounds { get { return Bounds; } }
        public string StatusLabel { get { return _status.Text; } }
        public bool ActionsLocked { get { return !_install.Enabled && !_run.Enabled; } }
        public bool HasCompactBusyState { get { return _status.Visible && _progress.Visible && !_install.Enabled && !_run.Enabled && _status.Right + 6 <= _run.Left && _status.Left >= _icon.Right + 10; } }
        public bool HasCompactIdleState { get { return !_status.Visible && !_progress.Visible && _status.Text.Length == 0 && Controls.OfType<Label>().Count(label => label.Visible) == 1; } }
        private readonly PictureBox _icon;
        private readonly Label _name;
        public bool ContentClearsActions { get { return ClientRectangle.Contains(_icon.Bounds) && _icon.Right + 10 <= _name.Left && _name.Right + 10 <= _install.Left && _install.Right == Width - 14 && _name.Bottom <= _status.Top && _status.Right + 10 <= _run.Left && _install.Left == _run.Left && _install.Size == _run.Size && _install.Bottom + 8 == _run.Top && ClientRectangle.Contains(_run.Bounds); } }
        public bool TitleFitsNameArea { get { Size measured = TextRenderer.MeasureText(_name.Text, _name.Font, new Size(_name.Width, Int32.MaxValue), TextFormatFlags.WordBreak); return measured.Width <= _name.Width && measured.Height <= _name.Height; } }
        public bool TitleHasFullTooltip { get { return _name.AutoEllipsis && _toolTip.GetToolTip(_name) == App.Name; } }
        private readonly RoundedActionButton _install;
        private readonly RoundedActionButton _run;
        private readonly Label _status;
        private readonly bool _actionsEnabled;
        private readonly ProgressBar _progress;
        private bool _installed;
        public AppCard(BootstrapApp app, int width, Action<BootstrapApp> install,
            Action<BootstrapApp> remove, Action<BootstrapApp> run, bool actionsEnabled)
        {
            App = app;
            _actionsEnabled = actionsEnabled;
            Width = width; Height = 104; Margin = new Padding(0, 0, 10, 10); Padding = new Padding(14);
            BackColor = Color.FromArgb(27, 32, 42); BorderStyle = BorderStyle.None;
            DoubleBuffered = true;
            _icon = new PictureBox { Left = 14, Top = 20, Width = 64, Height = 64, SizeMode = PictureBoxSizeMode.Zoom, AccessibleName = app.Name, AccessibleDescription = app.Category + ": " + app.Description };
            bool iconDecoded;
            _icon.Image = DecodeIcon(app.IconBase64, out iconDecoded);
            HasDecodedIcon = iconDecoded;
            Controls.Add(_icon);
            _name = new Label { Text = app.Name, Left = 88, Top = 28, Width = width - 142, Height = 48, TextAlign = ContentAlignment.MiddleLeft, Font = new Font("Segoe UI Semibold", 10.5F), ForeColor = Color.White, AutoEllipsis = true };
            Controls.Add(_name);
            _status = new Label { Text = "", Left = 88, Top = 78, Width = width - 142, Height = 16, Font = new Font("Segoe UI", 8.5F), Visible = false, ForeColor = Color.FromArgb(165, 190, 231), AutoEllipsis = true, AccessibleRole = AccessibleRole.StatusBar, AccessibleName = app.Name + " 상태" };
            Controls.Add(_status);
            _progress = new ProgressBar { Left = 14, Top = 98, Width = width - 28, Height = 3, Visible = false, Style = ProgressBarStyle.Marquee, AccessibleName = app.Name + " 진행 상황" };
            Controls.Add(_progress);
            _install = new RoundedActionButton { Text = "", Left = width - 44, Top = 18, Width = 30, Height = 30, CornerRadius = 15 };
            _run = new RoundedActionButton { Text = "", Left = width - 44, Top = 56, Width = 30, Height = 30, CornerRadius = 15 };
            _run.PaintGlyph += DrawPlayActionIcon;
            _run.AccessibleName = "실행 " + app.Name;
            _run.AccessibleDescription = app.Name + "을(를) 실행합니다.";
            _install.FlatStyle = FlatStyle.Flat;
            _install.FlatAppearance.BorderSize = 1;
            _install.Cursor = Cursors.Hand;
            _install.PaintGlyph += DrawInstallActionIcon;
            _install.EnabledChanged += delegate { ApplyInstallActionStyle(); };
            _toolTip = new ToolTip { ShowAlways = true };
            _toolTip.SetToolTip(_name, app.Name);
            _toolTip.SetToolTip(_run, "실행 " + app.Name);
            _toolTip.SetToolTip(_icon, app.Name + "\r\n" + app.Category + " · " + app.Description);
            _toolTip.SetToolTip(this, app.Category + " · " + app.Description);
            LauncherFormStyle(_run, true);
            _install.Click += delegate { if (_installed) remove(App); else install(App); };
            _run.Click += delegate { run(App); };
            Controls.Add(_install); Controls.Add(_run);
            _run.Enabled = false;
            _install.Enabled = actionsEnabled;
            _run.AccessibleName = "실행 " + app.Name;
            UpdateInstallActionSemantics(null);
            ApplyInstallActionStyle();
        }
        public void SetStatus(BootstrapperAppStatus status)
        {
            _installed = status != null && status.IsInstalled;
            _status.Text = "";
            UpdateInstallActionSemantics(null);
            _run.Enabled = _actionsEnabled;
            _install.Enabled = _actionsEnabled;
            ApplyInstallActionStyle();
        }
        public void SetSmokeInstalledStatus()
        {
            _installed = true;
            _status.Text = "";
            UpdateInstallActionSemantics(null);
            _install.Enabled = _actionsEnabled;
            _run.Enabled = _actionsEnabled;
            ApplyInstallActionStyle();
        }
        public void SetBusy(bool busy, string busyLabel)
        {
            _install.Enabled = _actionsEnabled && !busy;
            _run.Enabled = _actionsEnabled && !busy;
            _progress.Visible = busy;
            _status.Visible = busy;
            UpdateInstallActionSemantics(busy ? (String.IsNullOrEmpty(busyLabel) ? "실행 중…" : busyLabel) : null);
            _run.AccessibleName = "실행 " + App.Name;
            _status.Text = busy ? (String.IsNullOrEmpty(busyLabel) ? "실행 중…" : busyLabel) : "";
            ApplyInstallActionStyle();
        }
        private readonly ToolTip _toolTip;
        private void UpdateInstallActionSemantics(string busyLabel)
        {
            string action = busyLabel ?? (_installed ? "제거" : "설치");
            string description = busyLabel != null ? action : (_installed
                ? "설치 등록과 시작 메뉴 바로가기를 제거합니다. 다운로드 파일과 사용자 데이터는 유지됩니다."
                : "개별 설치와 시작 메뉴 바로가기를 추가합니다.");
            _install.AccessibleName = action + " " + App.Name;
            _install.AccessibleDescription = description;
            _toolTip.SetToolTip(_install, busyLabel != null ? action : App.Name + " " + action + ": " + description);
            _install.Invalidate();
        }
        private void DrawPlayActionIcon(object sender, PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (SolidBrush fill = new SolidBrush(_run.Enabled ? _run.ForeColor : Color.FromArgb(137, 146, 163)))
                e.Graphics.FillPolygon(fill, new[] { new PointF(11, 8.5F), new PointF(11, 20.5F), new PointF(21, 14.5F) });
        }
        private void DrawInstallActionIcon(object sender, PaintEventArgs e)
        {
            Graphics graphics = e.Graphics;
            GraphicsState saved = graphics.Save();
            try
            {
            graphics.ScaleTransform(_install.Width / 30F, _install.Height / 30F);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Color color = _install.ForeColor;
            using (Pen pen = new Pen(color, 2.1F))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                float cx = 15F;
                if (_installed)
                {
                    graphics.DrawLine(pen, cx - 7, 11, cx + 7, 11);
                    graphics.DrawLine(pen, cx - 4, 8, cx + 4, 8);
                    graphics.DrawLine(pen, cx - 5, 13, cx - 4, 23);
                    graphics.DrawLine(pen, cx + 5, 13, cx + 4, 23);
                    graphics.DrawLine(pen, cx - 4, 23, cx + 4, 23);
                    graphics.DrawLine(pen, cx - 1, 15, cx - 1, 20);
                    graphics.DrawLine(pen, cx + 2, 15, cx + 2, 20);
                }
                else
                {
                    graphics.DrawLine(pen, cx - 8, 20, cx - 8, 24);
                    graphics.DrawLine(pen, cx - 8, 24, cx + 8, 24);
                    graphics.DrawLine(pen, cx + 8, 24, cx + 8, 20);
                    graphics.DrawLine(pen, cx, 7, cx, 19);
                    graphics.DrawLine(pen, cx, 19, cx - 5, 14);
                    graphics.DrawLine(pen, cx, 19, cx + 5, 14);
                }
            }
            }
            finally { graphics.Restore(saved); }
        }
        private void ApplyInstallActionStyle()
        {
            if (_installed)
            {
                _install.BackColor = _install.Enabled ? Color.FromArgb(56, 35, 42) : Color.FromArgb(42, 33, 38);
                _install.ForeColor = _install.Enabled ? Color.FromArgb(255, 193, 201) : Color.FromArgb(150, 125, 133);
                _install.FlatAppearance.BorderColor = _install.Enabled ? Color.FromArgb(140, 70, 85) : Color.FromArgb(83, 56, 65);
                _install.FlatAppearance.MouseOverBackColor = _install.Enabled ? Color.FromArgb(81, 43, 54) : _install.BackColor;
                _install.FlatAppearance.MouseDownBackColor = _install.Enabled ? Color.FromArgb(41, 26, 31) : _install.BackColor;
            }
            else
            {
                _install.BackColor = _install.Enabled ? Color.FromArgb(22, 27, 36) : Color.FromArgb(27, 32, 42);
                _install.ForeColor = _install.Enabled ? Color.White : Color.FromArgb(154, 160, 171);
                _install.FlatAppearance.BorderColor = _install.Enabled ? Color.FromArgb(54, 63, 78) : Color.FromArgb(40, 47, 59);
                _install.FlatAppearance.MouseOverBackColor = _install.Enabled ? Color.FromArgb(36, 44, 58) : _install.BackColor;
                _install.FlatAppearance.MouseDownBackColor = _install.Enabled ? Color.FromArgb(17, 21, 29) : _install.BackColor;
            }
        }
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent == null ? Color.FromArgb(17, 20, 27) : Parent.BackColor);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath path = RoundedDrawing.CreatePath(new RectangleF(0.5F, 0.5F, Width - 1, Height - 1), 14))
            using (SolidBrush fill = new SolidBrush(BackColor))
                e.Graphics.FillPath(fill, path);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath path = RoundedDrawing.CreatePath(new RectangleF(0.5F, 0.5F, Width - 1, Height - 1), 14))
            using (Pen border = new Pen(Color.FromArgb(47, 56, 71)))
                e.Graphics.DrawPath(border, path);
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _toolTip.Dispose();
                _name.Font.Dispose();
                _status.Font.Dispose();
                foreach (PictureBox icon in Controls.OfType<PictureBox>())
                    if (icon.Image != null) icon.Image.Dispose();
            }
            base.Dispose(disposing);
        }
        private static Image DecodeIcon(string text, out bool decoded)
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
        private static void LauncherFormStyle(Button button, bool primary)
        {
            button.FlatStyle = FlatStyle.Flat; button.FlatAppearance.BorderColor = Color.FromArgb(61, 70, 86);
            button.BackColor = primary ? Color.FromArgb(50, 91, 168) : Color.FromArgb(37, 43, 55); button.ForeColor = Color.White; button.Cursor = Cursors.Hand;
        }
    }
}
