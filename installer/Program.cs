using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ILuncher.Setup;

// i-luncher-Setup.exe [/S] [/dir=PATH] [/nodesktop] [/nolaunch]   설치 (또는 설치된 버전 바꾸기)
// uninstall.exe /uninstall [/S]                                     제거 (Windows "앱" 목록이 이것을 부름)
// 고급 (배포 스크립트·테스트): /noshortcuts /noregistry
// 조용한 실행(/S)은 %TEMP%\i-luncher-Setup.log 에 기록하고 성공하면 0, 실패하면 1 로 끝남
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        bool Flag(string name) => args.Any(arg => string.Equals(arg, name, StringComparison.OrdinalIgnoreCase));
        string? Value(string name) => args.Select(arg => arg.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase) ? arg.Substring(name.Length + 1).Trim('"') : null)
            .LastOrDefault(value => value is not null);
        bool silent = Flag("/S") || Flag("/silent");
        bool uninstall = Flag("/uninstall");
        string directory = Value("/dir") ?? (uninstall ? AppContext.BaseDirectory : Installer.RegisteredInstallDirectory() ?? Installer.DefaultInstallDirectory);

        if (silent)
        {
            string log = Path.Combine(Path.GetTempPath(), "i-luncher-Setup.log");
            // Windows PowerShell 5.1(Get-Content)에서 한글이 깨지지 않게 BOM 있는 UTF-8
            void Log(string line) { try { File.AppendAllText(log, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}{Environment.NewLine}", System.Text.Encoding.UTF8); } catch (IOException) { } }
            try
            {
                if (uninstall) Installer.Uninstall(directory, Log);
                else Installer.Install(new Installer.Options(directory, !Flag("/noshortcuts"), !Flag("/nodesktop"), !Flag("/noregistry"), !Flag("/nolaunch")), Log);
                return 0;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException)
            {
                Log("실패: " + error.Message);
                return 1;
            }
        }

        Application.EnableVisualStyles();
        if (uninstall)
        {
            if (MessageBox.Show("i-luncher를 제거할까요?\n\n런처로 설치한 앱들은 각각 따로 설치되어 있어 그대로 남습니다 (설정 → 앱에서 각각 제거).",
                    "i-luncher 제거", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return 0;
            try
            {
                Installer.Uninstall(directory, _ => { });
                MessageBox.Show("i-luncher를 제거했습니다.", "i-luncher 제거", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return 0;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                MessageBox.Show(error.Message, "i-luncher 제거", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return 1;
            }
        }
        // 묻지 않고 바로 사용자 폴더(또는 설치된 위치)에 설치한 뒤 런처를 시작
        using var form = new SetupForm(new Installer.Options(directory, shortcuts: !Flag("/noshortcuts"), desktop: !Flag("/nodesktop"),
            register: !Flag("/noregistry"), launch: !Flag("/nolaunch")));
        Application.Run(form);
        return form.Succeeded ? 0 : 1;
    }
}

/// <summary>런처 색으로 된 작은 어두운 창: 바로 설치하며 진행을 보여 주고, 실패했을 때만 버튼이 나옴</summary>
internal sealed class SetupForm : Form
{
    private static readonly Color Back = ColorTranslator.FromHtml("#10141C");
    private static readonly Color Line = ColorTranslator.FromHtml("#2A3140");
    private static readonly Color Text1 = ColorTranslator.FromHtml("#F5F7FB");
    private static readonly Color Muted = ColorTranslator.FromHtml("#9DA7B8");
    private static readonly Color Accent = ColorTranslator.FromHtml("#7C9CFF");
    private static readonly Color AccentInk = ColorTranslator.FromHtml("#0B0D12");

    private readonly Installer.Options _options;
    private readonly Label _status;
    private readonly ProgressBar _progress;
    private readonly Button _retry, _close;
    private bool _installing;
    public bool Succeeded { get; private set; }

    public SetupForm(Installer.Options options)
    {
        _options = options;
        Text = "i-luncher 설치";
        Icon = Icon.ExtractAssociatedIcon(Installer.SelfPath);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(480, 196);
        BackColor = Back;
        ForeColor = Text1;
        Font = new Font("Malgun Gothic", 10f);

        var title = new Label { Text = "i-luncher", Font = new Font("Malgun Gothic", 16f, FontStyle.Bold), ForeColor = Accent, AutoSize = true, Location = new Point(24, 18) };
        var version = new Label { Text = "v" + Installer.Version, ForeColor = Muted, AutoSize = true, Location = new Point(28, 56) };
        _status = new Label { Text = Installer.RegisteredInstallDirectory() is not null ? "설치된 버전을 바꾸는 중…" : "설치 중…",
            ForeColor = Muted, Location = new Point(24, 88), Size = new Size(432, 44) };
        _progress = new ProgressBar { Style = ProgressBarStyle.Marquee, MarqueeAnimationSpeed = 25, Location = new Point(24, 136), Size = new Size(432, 6) };
        _retry = MakeButton("다시 시도", new Point(258, 148), 96, primary: true);
        _close = MakeButton("닫기", new Point(362, 148), 94, primary: false);
        _retry.Visible = _close.Visible = false;
        _retry.Click += async (_, _) => await InstallAsync();
        _close.Click += (_, _) => Close();
        CancelButton = _close;
        Controls.AddRange([title, version, _status, _progress, _retry, _close]);
        Shown += async (_, _) => await InstallAsync();
        FormClosing += (_, e) => e.Cancel = _installing;
    }

    private static Button MakeButton(string text, Point location, int width, bool primary)
    {
        var button = new Button
        {
            Text = text, Location = location, Size = new Size(width, 32), FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand,
            BackColor = primary ? Accent : ColorTranslator.FromHtml("#1A1F2B"), ForeColor = primary ? AccentInk : Text1,
            Font = new Font("Malgun Gothic", 10f, primary ? FontStyle.Bold : FontStyle.Regular)
        };
        button.FlatAppearance.BorderColor = primary ? Accent : Line;
        return button;
    }

    private async Task InstallAsync()
    {
        if (_installing) return;
        _installing = true;
        _retry.Visible = _close.Visible = false;
        _progress.Visible = true;
        _status.ForeColor = Muted;
        try
        {
            await Task.Run(() => Installer.Install(_options, message => BeginInvoke(new Action(() => _status.Text = message))));
            Succeeded = true;
            _installing = false;
            if (_options.Launch) { Close(); return; }
            _progress.Visible = false;
            _status.Text = "설치했습니다. 시작 메뉴의 i-luncher로 실행하세요.";
            _close.Visible = true;
            AcceptButton = _close;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException)
        {
            _installing = false;
            _progress.Visible = false;
            _status.Text = error.Message;
            _status.ForeColor = Text1;
            _retry.Visible = _close.Visible = true;
            AcceptButton = _retry;
        }
    }
}
