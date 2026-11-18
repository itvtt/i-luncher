using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ILauncher;

/// <summary>
/// 아이콘 고르기 (즐겨찾기 · 내 앱 편집 창): 자동(사이트 · 프로그램 · 저장소 아이콘) / 기본 아이콘 / 이미지 파일.
/// Value 는 CustomIcons 의 값 ("" · "builtin:이름" · 복사해 둔 PNG 경로)
/// </summary>
public partial class IconPicker : UserControl
{
    private static readonly Brush Selected = Freeze(new SolidColorBrush(Color.FromRgb(0x0A, 0x84, 0xFF)));
    private static readonly Brush ModeBack = Freeze(new SolidColorBrush(Color.FromArgb(0x38, 0x0A, 0x84, 0xFF)));
    private static readonly Brush ModeText = Freeze(new SolidColorBrush(Color.FromRgb(0x6C, 0xB6, 0xFF)));
    private readonly List<string> _imported = [];
    private string _value = "";
    private ImageSource? _autoIcon;

    public IconPicker()
    {
        InitializeComponent();
        foreach (var builtin in CustomIcons.Builtins)
        {
            var tile = new Button
            {
                Style = (Style)FindResource("DialogTileStyle"),
                Tag = CustomIcons.BuiltinPrefix + builtin.Key,
                ToolTip = builtin.Label,
                BorderBrush = Brushes.Transparent,
                Content = new Image { Source = CustomIcons.Render(builtin), Stretch = Stretch.Uniform }
            };
            System.Windows.Automation.AutomationProperties.SetName(tile, "기본 아이콘 " + builtin.Label);
            RenderOptions.SetBitmapScalingMode((Image)tile.Content, BitmapScalingMode.HighQuality);
            tile.Click += (_, _) => Value = (string)tile.Tag;
            BuiltinTiles.Children.Add(tile);
        }
        Refresh();
    }

    /// <summary>고른 아이콘이 바뀜</summary>
    public event EventHandler? Changed;

    public string Value
    {
        get => _value;
        set
        {
            value ??= "";
            if (value == _value) return;
            _value = value;
            Refresh();
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>자동일 때의 아이콘 (사이트 · 프로그램 · 저장소에서 찾은 것, 없으면 null)</summary>
    public ImageSource? AutoIcon
    {
        get => _autoIcon;
        set { _autoIcon = value; Refresh(); }
    }

    /// <summary>자동일 때 무엇을 쓰는지 (예: "사이트 아이콘")</summary>
    public string AutoText { get; set; } = "자동";

    /// <summary>자동인데 찾은 아이콘이 없을 때의 안내</summary>
    public string AutoMissingText { get; set; } = "아이콘이 없어 글자로 보입니다. 아래에서 고르거나 이미지 파일을 쓰세요.";

    /// <summary>아이콘이 없을 때의 글자 판</summary>
    public string FallbackText { get; set; } = "?";
    public Brush FallbackBrush { get; set; } = Freeze(new LinearGradientBrush(Color.FromRgb(0x4F, 0x5B, 0x6E), Color.FromRgb(0x2A, 0x31, 0x3C), 90));

    /// <summary>미리 보기 다시 그리기 (자동 아이콘 · 글자를 바꾼 뒤)</summary>
    public void Refresh()
    {
        if (PreviewImage is null) return;
        ImageSource? image = CustomIcons.IsCustom(_value) ? CustomIcons.Load(_value) : _autoIcon;
        PreviewImage.Source = image;
        PreviewPlate.Background = image is null ? FallbackBrush : Brushes.Transparent;
        PreviewInitials.Text = image is null ? FallbackText : "";
        StatusText.Text = CustomIcons.FindBuiltin(_value) is { } builtin ? $"기본 아이콘 · {builtin.Label}"
            : CustomIcons.IsCustom(_value) ? (image is null ? "이미지 파일이 없습니다. 다시 고르세요." : "이미지 파일")
            : _autoIcon is not null ? AutoText : AutoMissingText;
        bool builtinChosen = CustomIcons.FindBuiltin(_value) is not null;
        Mode(AutoButton, !CustomIcons.IsCustom(_value));
        Mode(BuiltinButton, builtinChosen);
        Mode(FileButton, CustomIcons.IsCustom(_value) && !builtinChosen);
        if (builtinChosen) BuiltinTiles.Visibility = Visibility.Visible;
        foreach (var tile in BuiltinTiles.Children.OfType<Button>())
            tile.BorderBrush = string.Equals((string)tile.Tag, _value, StringComparison.OrdinalIgnoreCase) ? Selected : Brushes.Transparent;
    }

    /// <summary>지금 고른 방식의 단추는 파랑 (나머지는 보통 단추)</summary>
    private static void Mode(Button button, bool on)
    {
        if (on)
        {
            button.Background = ModeBack;
            button.Foreground = ModeText;
        }
        else
        {
            button.ClearValue(BackgroundProperty);
            button.ClearValue(ForegroundProperty);
        }
    }

    private void Auto_Click(object sender, RoutedEventArgs e) => Value = "";

    private void Builtin_Click(object sender, RoutedEventArgs e) =>
        BuiltinTiles.Visibility = BuiltinTiles.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>기본 아이콘 펼치기 (사이트 아이콘을 못 받았을 때 바로 고를 수 있게)</summary>
    public void ShowBuiltins() => BuiltinTiles.Visibility = Visibility.Visible;

    private void File_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "아이콘 이미지 고르기",
            Filter = "이미지 (*.png;*.jpg;*.jpeg;*.ico;*.bmp;*.gif)|*.png;*.jpg;*.jpeg;*.ico;*.bmp;*.gif|모든 파일 (*.*)|*.*"
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            string copy = CustomIcons.Import(dialog.FileName);
            _imported.Add(copy);
            Value = copy;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            StatusText.Text = "이 파일은 아이콘으로 쓸 수 없습니다: " + error.Message;
        }
    }

    /// <summary>창을 닫을 때: 이번에 복사했지만 쓰지 않게 된 그림을 지움 (keep = 저장한 값)</summary>
    public void CleanUp(string? keep)
    {
        foreach (string path in _imported.Where(path => !string.Equals(path, keep, StringComparison.OrdinalIgnoreCase)))
            CustomIcons.DeleteImported(path);
        _imported.Clear();
    }

    private static T Freeze<T>(T brush) where T : Freezable
    {
        brush.Freeze();
        return brush;
    }
}
