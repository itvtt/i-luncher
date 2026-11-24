#if WINDOWS
using System.Drawing;
using System.Windows.Forms;

namespace IApi;

/// <summary>
/// i-api.exe 를 손으로 실행하면 (더블클릭 · 바로 가기) 뜨는 작은 창: 포트를 넣고 [실행] → 이 PC 에서 서버가 켜지고 (설정에 포트를 쓰고,
/// 로그인 자동 실행 · 방화벽), 재부팅해도 다시 켜짐. 이미 정한 포트가 있으면 채워 둠 (바꾸고 [실행]하면 그 포트로 다시 시작)
/// </summary>
internal sealed class SetupWindow : Form
{
    private static readonly Color Back = Color.FromArgb(0x1C, 0x1C, 0x1E), Field = Color.FromArgb(0x2C, 0x2D, 0x32), Text1 = Color.FromArgb(0xF5, 0xF5, 0xF7),
        Muted = Color.FromArgb(0xA1, 0xA1, 0xA6), Accent = Color.FromArgb(0x4A, 0xA3, 0xFF), Good = Color.FromArgb(0x3D, 0xD6, 0x8C), Warn = Color.FromArgb(0xE6, 0xC0, 0x7A);

    private readonly Options _options;
    private readonly TextBox _port = new();
    private readonly Button _run = new();
    private readonly Label _status = new();

    public SetupWindow(Options options)
    {
        _options = options;
        Text = "i-api";
        Font = new Font("Malgun Gothic", 10f);
        BackColor = Back;
        ForeColor = Text1;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(420, 210);
        AutoScaleMode = AutoScaleMode.Dpi;

        var title = new Label { Text = "API 서버 i-api", Font = new Font("Malgun Gothic", 14f, FontStyle.Bold), AutoSize = true, Location = new Point(24, 20) };
        var label = new Label { Text = "포트", ForeColor = Muted, AutoSize = true, Location = new Point(26, 72) };
        _port.SetBounds(80, 68, 120, 30);
        _port.BackColor = Field;
        _port.ForeColor = Text1;
        _port.BorderStyle = BorderStyle.FixedSingle;
        _port.MaxLength = 5;
        _port.Text = Settings.Read(Path.GetFullPath(options.SettingsFile ?? Settings.DefaultFile)).GetValueOrDefault("RELAY_PORT", "");
        _port.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Start(); } };
        _run.Text = "실행";
        _run.SetBounds(212, 66, 90, 32);
        _run.FlatStyle = FlatStyle.Flat;
        _run.FlatAppearance.BorderSize = 0;
        _run.BackColor = Accent;
        _run.ForeColor = Color.White;
        _run.Cursor = Cursors.Hand;
        _run.Click += (_, _) => Start();
        _status.SetBounds(24, 116, 372, 80);
        _status.ForeColor = Muted;
        _status.Text = "포트를 넣고 [실행]하면 이 PC 에서 서버가 켜지고, 로그인할 때마다 다시 켜집니다.";
        Controls.AddRange([title, label, _port, _run, _status]);
        AcceptButton = _run;
        Shown += (_, _) => _port.Focus();
    }

    private void Show(string text, Color color)
    {
        _status.ForeColor = color;
        _status.Text = text;
    }

    private void Start()
    {
        if (!int.TryParse(_port.Text.Trim(), out int port) || port is < 1 or > 65535)
        {
            Show("포트는 1~65535 의 숫자로 씁니다.", Warn);
            _port.Focus();
            return;
        }
        _run.Enabled = _port.Enabled = false;
        Show("시작하는 중… (방화벽을 열 때 Windows 확인 창이 뜨면 \"예\")", Muted);
        Task.Run(() => Program.Configure(_options, port)).ContinueWith(task => BeginInvoke(() =>
        {
            var (ok, message) = task.IsFaulted ? (false, task.Exception!.GetBaseException().Message) : task.Result;
            Show(message, ok ? Good : Warn);
            _run.Enabled = _port.Enabled = true;
            _run.Text = ok ? "다시 실행" : "실행";
        }));
    }

    public static int Run(Options options)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.Run(new SetupWindow(options));
        return 0;
    }
}
#endif
