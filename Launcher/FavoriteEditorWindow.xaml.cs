using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ILauncher.Services;

namespace ILauncher;

/// <summary>즐겨찾기 추가·편집: 설치된 프로그램에서 고르거나 경로를 직접 (실행 파일·바로 가기·아무 파일)</summary>
public partial class FavoriteEditorWindow : Window
{
    private sealed record ProgramItem(string Name, string Path);

    private readonly FavoriteProgram? _editing;
    private List<ProgramItem> _programs = [];

    public FavoriteEditorWindow(FavoriteProgram? editing)
    {
        _editing = editing;
        InitializeComponent();
        if (editing is not null)
        {
            TitleText.Text = "프로그램 편집";
            PickerPanel.Visibility = Visibility.Collapsed;
            NameBox.Text = editing.Name;
            PathBox.Text = editing.Path;
            ArgumentsBox.Text = editing.Arguments;
        }
        Loaded += async (_, _) =>
        {
            if (_editing is not null) { NameBox.Focus(); return; }
            FilterBox.Focus();
            _programs = (await Task.Run(InstalledPrograms.Scan)).Select(item => new ProgramItem(item.Name, item.Path)).ToList();
            ApplyFilter();
        };
    }

    public FavoriteProgram? Result { get; private set; }

    private void ApplyFilter()
    {
        string text = FilterBox.Text.Trim();
        ProgramList.ItemsSource = text.Length == 0 ? _programs : _programs.Where(item => item.Name.Contains(text, StringComparison.CurrentCultureIgnoreCase)).ToList();
    }

    private void FilterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        FilterPlaceholder.Visibility = FilterBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ApplyFilter();
    }

    private void ProgramList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProgramList.SelectedItem is not ProgramItem item) return;
        NameBox.Text = item.Name;
        PathBox.Text = item.Path;
        ArgumentsBox.Text = "";
    }

    private void ProgramList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ProgramList.SelectedItem is ProgramItem && SaveButton.IsEnabled) SaveButton_Click(sender, e);
    }

    private void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "프로그램 고르기",
            Filter = "프로그램 (*.exe;*.lnk;*.bat;*.cmd)|*.exe;*.lnk;*.bat;*.cmd|모든 파일 (*.*)|*.*",
            DereferenceLinks = false
        };
        string current = PathBox.Text.Trim().Trim('"');
        if (File.Exists(current)) dialog.InitialDirectory = Path.GetDirectoryName(current);
        if (dialog.ShowDialog(this) != true) return;
        PathBox.Text = dialog.FileName;
        if (NameBox.Text.Trim().Length == 0) NameBox.Text = Path.GetFileNameWithoutExtension(dialog.FileName);
    }

    private void Field_Changed(object sender, RoutedEventArgs e)
    {
        if (SaveButton is null) return;
        ErrorText.Visibility = Visibility.Collapsed;
        bool filled = NameBox.Text.Trim().Length > 0 && PathBox.Text.Trim().Trim('"').Length > 0;
        bool changed = _editing is null || NameBox.Text.Trim() != _editing.Name || PathBox.Text.Trim().Trim('"') != _editing.Path || ArgumentsBox.Text.Trim() != _editing.Arguments;
        SaveButton.IsEnabled = filled && changed;
    }

    private void Card_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed && e.OriginalSource is not TextBox) DragMove();
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        string path = Environment.ExpandEnvironmentVariables(PathBox.Text.Trim().Trim('"'));
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            ErrorText.Text = "경로에 파일이 없습니다.";
            ErrorText.Visibility = Visibility.Visible;
            return;
        }
        Result = new FavoriteProgram
        {
            Id = _editing?.Id ?? Guid.NewGuid().ToString("N"),
            Name = NameBox.Text.Trim(),
            Path = path,
            Arguments = ArgumentsBox.Text.Trim()
        };
        DialogResult = true;
    }
}
