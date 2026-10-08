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
        private readonly bool _smokeUi;
        private readonly bool _smokeRealProfile;
        private readonly Stopwatch _startup;
        private readonly string _reportPath;
        private readonly string _smokeInstalledAppId;
        private readonly System.Windows.Forms.Timer _smokeTimer;
        private Icon _ownedWindowIcon;
        private string _windowIconSource;
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
            BackColor = Color.FromArgb(17, 20, 27);
            ForeColor = Color.FromArgb(241, 244, 250);
            Font = new Font("Segoe UI", 9F);
            MinimumSize = new Size(720, 520);
            StartPosition = FormStartPosition.CenterScreen;
            Size = new Size(1240, 840);
            _ownedWindowIcon = LoadWindowIcon();
            Icon = _ownedWindowIcon;
            ClampToWorkArea();

            TableLayoutPanel root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, BackColor = BackColor };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
            Controls.Add(root);

            Panel header = new Panel { Dock = DockStyle.Fill, Padding = new Padding(24, 12, 20, 8), BackColor = Color.FromArgb(22, 26, 35) };
            root.Controls.Add(header, 0, 0);
            Label title = new Label { Text = "i-luncher", AutoSize = true, Font = new Font("Segoe UI Semibold", 16F), ForeColor = Color.White, Location = new Point(22, 14) };
            header.Controls.Add(title);
            Label build = new Label { Text = "버전 " + catalog.Version, AutoSize = true, ForeColor = Color.FromArgb(157, 166, 182), Location = new Point(140, 24) };
            header.Controls.Add(build);
            Button settings = new Button { Text = "설정", Width = 82, Height = 32, Anchor = AnchorStyles.Top | AnchorStyles.Right, Location = new Point(Width - 142, 14) };
            settings.Enabled = !_smokeUi;
            StyleButton(settings, false);
            settings.Click += delegate { OpenSettings(); };
            Label usageHint = new Label
            {
                Text = "설치: 개별 설치·시작 메뉴 바로가기 · 포터블 실행: 등록 없이 바로 실행",
                Left = 250, Top = 22, Height = 20, ForeColor = Color.FromArgb(157, 166, 182),
                Font = new Font("Segoe UI", 8.25F), AutoEllipsis = true, Anchor = AnchorStyles.Top | AnchorStyles.Left
            };
            Action layoutHeaderActions = delegate
            {
                settings.Left = header.ClientSize.Width - settings.Width - 18;
                usageHint.Width = Math.Max(0, settings.Left - usageHint.Left - 12);
            };
            header.Resize += delegate { layoutHeaderActions(); };
            header.Controls.Add(settings);
            header.Controls.Add(usageHint);
            layoutHeaderActions();

            FlowLayoutPanel grid = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(18, 18, 18, 18), WrapContents = true, BackColor = BackColor };
            root.Controls.Add(grid, 0, 1);
            int cardWidth = 360;
            foreach (BootstrapApp app in catalog.Apps)
            {
                AppCard card = new AppCard(app, cardWidth, catalog.RuntimeBytes, InstallClicked, RemoveClicked, RunClicked, true);
                _cards.Add(app.Id, card);
                grid.Controls.Add(card);
            }
            Panel footer = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(22, 26, 35), Padding = new Padding(20, 4, 20, 4) };
            root.Controls.Add(footer, 0, 2);
            _status = new Label { Left = 0, Top = 0, Width = 900, Height = 22, Text = "목록을 불러왔습니다. 필요한 항목을 설치하거나 실행하세요.", ForeColor = Color.FromArgb(180, 190, 207), TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
            footer.Controls.Add(_status);
            Label runtimeNote = new Label
            {
                Left = 0, Top = 24, Width = 1120, Height = 20,
                Text = "공용 .NET 런타임 " + FormatMegabytes(catalog.RuntimeBytes) + "은 처음 필요한 앱을 실행할 때 한 번 다운로드해 캐시에 저장하고, 이후 앱이 공유합니다.",
                ForeColor = Color.FromArgb(135, 145, 164), Font = new Font("Segoe UI", 8.25F), AutoEllipsis = true
            };
            footer.Controls.Add(runtimeNote);
            _engine.ProgressChanged += Engine_ProgressChanged;
            Shown += async delegate
            {
                await LoadStatusesAsync();
                if (_smokeUi) _smokeTimer.Start();
            };
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (_firstPaintMs < 0) _firstPaintMs = _startup.ElapsedMilliseconds;
        }

        private void WriteSmokeReportAndClose()
        {
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
                        { "actionPlacement", "top-right-icon" },
                        { "portableAction", card.RunActionLabel },
                        { "portablePlacement", "full-width-bottom" },
                        { "status", card.StatusLabel }
                    }).ToArray() },
                { "gridColumns", _cards.Count == 0 ? 0 : _cards.Values.Select(card => card.Left).Distinct().Count() },
                { "iconsDecoded", _cards.Values.Count(card => card.HasDecodedIcon) },
                { "smokeInstalledAppId", _smokeInstalledAppId },
                { "smokeInstalledAction", String.IsNullOrWhiteSpace(_smokeInstalledAppId) ? null : _cards[_smokeInstalledAppId].InstallActionLabel },
                { "smokeInstalledStatus", String.IsNullOrWhiteSpace(_smokeInstalledAppId) ? null : _cards[_smokeInstalledAppId].StatusLabel },
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

        private void ClampToWorkArea()
        {
            Rectangle area = Screen.FromPoint(Cursor.Position).WorkingArea;
            int width = Math.Max(480, Math.Min(1240, area.Width - 24));
            int height = Math.Max(360, Math.Min(840, area.Height - 24));
            MinimumSize = new Size(Math.Min(720, width), Math.Min(520, height));
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
            if (disposing && _ownedWindowIcon != null)
            {
                _ownedWindowIcon.Dispose();
                _ownedWindowIcon = null;
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
            catch (Exception ex) { _status.Text = "목록 확인 실패: " + Program.ShortError(ex); }
        }
        private async void InstallClicked(BootstrapApp app)
        {
            if (_smokeUi) return;
            if (!_inFlight.Add(app.Id)) return;
            SetBusy(app.Id, true, "설치 중…");
            _status.Text = app.Name + " 설치를 준비하고 있습니다…";
            try
            {
                BootstrapperInstallResult result = await _engine.InstallAsync(app.Id, CancellationToken.None);
                _cards[app.Id].SetStatus(_engine.GetStatus(app.Id));
                _status.Text = "설치 완료: " + result.ShortcutPath;
            }
            catch (Exception ex)
            {
                _cards[app.Id].SetStatus(_engine.GetStatus(app.Id));
                _status.Text = "설치 실패: " + Program.ShortError(ex);
            }
            finally { _inFlight.Remove(app.Id); SetBusy(app.Id, false); }
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
            _status.Text = app.Name + " 제거 중…";
            try
            {
                BootstrapperUninstallResult result = await _engine.UninstallAsync(app.Id, CancellationToken.None);
                _cards[app.Id].SetStatus(_engine.GetStatus(app.Id));
                _status.Text = result.Removed ? "제거 완료: " + app.Name : "제거하지 않았습니다: " + app.Name;
            }
            catch (Exception ex)
            {
                _cards[app.Id].SetStatus(_engine.GetStatus(app.Id));
                _status.Text = "제거 실패: " + Program.ShortError(ex);
            }
            finally { _inFlight.Remove(app.Id); SetBusy(app.Id, false); }
        }
        private async void RunClicked(BootstrapApp app)
        {
            if (_smokeUi) return;
            if (!_inFlight.Add(app.Id)) return;
            SetBusy(app.Id, true, null);
            _status.Text = app.Name + "을(를) 확인하고 있습니다…";
            try
            {
                BootstrapperLaunchResult result = await _engine.LaunchAsync(app.Id, CancellationToken.None);
                _status.Text = "실행했습니다: " + Path.GetFileName(result.ExecutablePath);
                _cards[app.Id].SetStatus(_engine.GetStatus(app.Id));
            }
            catch (Exception ex)
            {
                _cards[app.Id].SetStatus(_engine.GetStatus(app.Id));
                _status.Text = "실행 실패: " + Program.ShortError(ex);
            }
            finally { _inFlight.Remove(app.Id); SetBusy(app.Id, false); }
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
            string bytes = e.BytesTotal > 0 ? string.Format("{0:0.0} / {1:0.0} MiB", e.BytesTransferred / 1048576.0, e.BytesTotal / 1048576.0) : "다운로드 중";
            _status.Text = e.Phase + " · " + bytes;
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
                        _status.Text = "설정을 저장했습니다. 토큰은 선택한 경우에만 현재 사용자 DPAPI로 보호됩니다.";
                    }
                    catch (Exception ex) { MessageBox.Show(this, Program.ShortError(ex), "설정", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
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

    internal sealed class AppCard : Panel
    {
        public BootstrapApp App { get; private set; }
        public bool HasDecodedIcon { get; private set; }
        public bool IsInstalled { get { return _installed; } }
        public string InstallActionLabel { get { return _installed ? "제거" : "설치"; } }
        public string InstallActionColor { get { return ColorTranslator.ToHtml(_install.BackColor); } }
        public string InstallAccessibleName { get { return _install.AccessibleName; } }
        public string RunActionLabel { get { return _run.Text; } }
        public string StatusLabel { get { return _status.Text; } }
        private readonly Button _install;
        private readonly Button _run;
        private readonly Label _status;
        private readonly Label _downloadSize;
        private readonly string _downloadSizeText;
        private readonly bool _actionsEnabled;
        private readonly ProgressBar _progress;
        private bool _ready;
        private bool _installed;
        public AppCard(BootstrapApp app, int width, long runtimeBytes, Action<BootstrapApp> install,
            Action<BootstrapApp> remove, Action<BootstrapApp> run, bool actionsEnabled)
        {
            App = app;
            _actionsEnabled = actionsEnabled;
            Width = width; Height = 206; Margin = new Padding(8); Padding = new Padding(16);
            BackColor = Color.FromArgb(27, 32, 42); BorderStyle = BorderStyle.FixedSingle;
            PictureBox icon = new PictureBox { Left = 14, Top = 14, Width = 52, Height = 52, SizeMode = PictureBoxSizeMode.Zoom };
            bool iconDecoded;
            icon.Image = DecodeIcon(app.IconBase64, out iconDecoded);
            HasDecodedIcon = iconDecoded;
            Controls.Add(icon);
            Label name = new Label { Text = app.Name, Left = 80, Top = 17, Width = width - 142, Height = 29, Font = new Font("Segoe UI Semibold", 14F), ForeColor = Color.White, AutoEllipsis = true };
            Controls.Add(name);
            Label category = new Label { Text = app.Category, Left = 80, Top = 48, Width = width - 142, Height = 20, ForeColor = Color.FromArgb(135, 165, 216), AutoEllipsis = true };
            Controls.Add(category);
            Label description = new Label { Text = app.Description, Left = 16, Top = 76, Width = width - 36, Height = 40, ForeColor = Color.FromArgb(181, 188, 201), AutoEllipsis = true };
            Controls.Add(description);
            _downloadSizeText = "첫 다운로드: 앱 " + FormatMegabytes(app.Bytes) + " + 런타임 " + FormatMegabytes(runtimeBytes);
            _downloadSize = new Label
            {
                Text = _downloadSizeText,
                Left = 16, Top = 116, Width = width - 36, Height = 19,
                ForeColor = Color.FromArgb(135, 165, 216), Font = new Font("Segoe UI", 8.25F), AutoEllipsis = true
            };
            Controls.Add(_downloadSize);
            _status = new Label { Text = "확인 중", Left = 16, Top = 137, Width = width - 36, Height = 22, ForeColor = Color.FromArgb(165, 175, 192), AutoEllipsis = true };
            Controls.Add(_status);
            _progress = new ProgressBar { Left = 16, Top = 160, Width = width - 36, Height = 4, Visible = false, Style = ProgressBarStyle.Marquee };
            Controls.Add(_progress);
            _install = new Button { Text = "", Left = width - 54, Top = 14, Width = 36, Height = 32, UseVisualStyleBackColor = false };
            _run = new Button { Text = "포터블 실행", Left = 16, Top = 166, Width = width - 36, Height = 30 };
            _run.AccessibleName = "포터블 실행 " + app.Name;
            _run.AccessibleDescription = "설치 등록 없이 실행합니다. 필요한 파일은 처음에 다운로드할 수 있습니다.";
            _install.FlatStyle = FlatStyle.Flat;
            _install.FlatAppearance.BorderSize = 1;
            _install.Cursor = Cursors.Hand;
            _install.Paint += DrawInstallActionIcon;
            _install.EnabledChanged += delegate { ApplyInstallActionStyle(); };
            _toolTip = new ToolTip { ShowAlways = true };
            LauncherFormStyle(_run, true);
            _install.Click += delegate { if (_installed) remove(App); else install(App); };
            _run.Click += delegate { run(App); };
            Controls.Add(_install); Controls.Add(_run);
            _run.Enabled = false;
            _install.Enabled = actionsEnabled;
            _run.AccessibleName = "포터블 실행 " + app.Name;
            UpdateInstallActionSemantics(null);
            ApplyInstallActionStyle();
        }
        public void SetStatus(BootstrapperAppStatus status)
        {
            _ready = status != null && status.IsReady;
            _installed = status != null && status.IsInstalled;
            _downloadSize.Text = _ready ? "준비된 파일로 추가 다운로드 없이 포터블 실행합니다" : _downloadSizeText;
            _status.Text = _installed ? "설치완료" : (_ready ? "준비됨" : "필요할 때 다운로드합니다" );
            UpdateInstallActionSemantics(null);
            _run.Enabled = _actionsEnabled;
            _install.Enabled = _actionsEnabled;
            ApplyInstallActionStyle();
        }
        public void SetSmokeInstalledStatus()
        {
            _ready = true;
            _installed = true;
            _downloadSize.Text = "준비된 파일로 추가 다운로드 없이 포터블 실행합니다";
            _status.Text = "설치완료";
            UpdateInstallActionSemantics(null);
            _install.Enabled = _actionsEnabled;
            _run.Enabled = _actionsEnabled;
            ApplyInstallActionStyle();
        }
        public void SetBusy(bool busy, string busyLabel)
        {
            _install.Enabled = !busy;
            _run.Enabled = !busy;
            _progress.Visible = busy;
            UpdateInstallActionSemantics(busy ? (String.IsNullOrEmpty(busyLabel) ? "실행 중…" : busyLabel) : null);
            _run.Text = busy && String.IsNullOrEmpty(busyLabel) ? "실행 중…" : "포터블 실행";
            _run.AccessibleName = _run.Text + " " + App.Name;
            if (busy) _status.Text = String.IsNullOrEmpty(busyLabel) ? "포터블 실행 중…" : busyLabel;
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
        private void DrawInstallActionIcon(object sender, PaintEventArgs e)
        {
            Graphics graphics = e.Graphics;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Color color = _install.Enabled ? Color.White : (_installed ? Color.FromArgb(190, 164, 166) : Color.FromArgb(154, 160, 171));
            using (Pen pen = new Pen(color, 2.1F))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                float cx = _install.ClientSize.Width / 2F;
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
        private void ApplyInstallActionStyle()
        {
            if (_installed)
            {
                _install.BackColor = _install.Enabled ? Color.FromArgb(151, 51, 60) : Color.FromArgb(76, 47, 52);
                _install.ForeColor = _install.Enabled ? Color.White : Color.FromArgb(190, 164, 166);
                _install.FlatAppearance.BorderColor = _install.Enabled ? Color.FromArgb(186, 77, 86) : Color.FromArgb(103, 65, 70);
                _install.FlatAppearance.MouseOverBackColor = _install.Enabled ? Color.FromArgb(174, 62, 72) : _install.BackColor;
                _install.FlatAppearance.MouseDownBackColor = _install.Enabled ? Color.FromArgb(126, 40, 48) : _install.BackColor;
            }
            else
            {
                _install.BackColor = _install.Enabled ? Color.FromArgb(50, 91, 168) : Color.FromArgb(47, 53, 65);
                _install.ForeColor = _install.Enabled ? Color.White : Color.FromArgb(154, 160, 171);
                _install.FlatAppearance.BorderColor = _install.Enabled ? Color.FromArgb(74, 112, 183) : Color.FromArgb(63, 70, 82);
                _install.FlatAppearance.MouseOverBackColor = _install.Enabled ? Color.FromArgb(62, 108, 193) : _install.BackColor;
                _install.FlatAppearance.MouseDownBackColor = _install.Enabled ? Color.FromArgb(41, 76, 143) : _install.BackColor;
            }
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) _toolTip.Dispose();
            base.Dispose(disposing);
        }
        private static string FormatMegabytes(long bytes)
        {
            return string.Format("{0:0.0} MB", bytes / 1048576.0);
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
                        using (Icon icon = new Icon(stream))
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
