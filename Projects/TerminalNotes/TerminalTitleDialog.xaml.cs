using System.Windows;

namespace TerminalNotes;

public partial class TerminalTitleDialog : Window
{
    public string TerminalTitle => TitleInput.Text.Trim();
    public TerminalTitleDialog(string initialTitle, bool rename = false)
    {
        InitializeComponent();
        TitleInput.Text = initialTitle;
        if (rename) { Heading.Text = "터미널 이름 바꾸기"; ConfirmButton.Content = "변경"; }
        Loaded += (_, _) => { TitleInput.Focus(); TitleInput.SelectAll(); };
    }
    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(TerminalTitle))
        {
            ValidationMessage.Text = "구분할 수 있는 이름을 입력해주세요.";
            TitleInput.Focus();
            return;
        }
        DialogResult = true;
    }
}
