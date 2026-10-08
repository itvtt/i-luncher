using System.IO;
using System.Windows;
using TerminalNotes.Services;

namespace TerminalNotes;

public partial class GlobalAgentInstructionsDialog : Window
{
    private string _loadedContents;

    public GlobalAgentInstructionsDialog()
    {
        InitializeComponent();
        PathText.Text = GlobalAgentInstructions.FilePath;
        _loadedContents = GlobalAgentInstructions.ReadCurrent();
        InstructionsInput.Text = _loadedContents;
    }

    private void SaveInstructionsButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            switch (GlobalAgentInstructions.TrySave(_loadedContents, InstructionsInput.Text))
            {
                case GlobalAgentInstructions.SaveResult.Saved:
                    _loadedContents = InstructionsInput.Text;
                    SaveStatusText.Text = "저장했습니다. 새 OpenCode 요청부터 적용됩니다.";
                    SaveStatusText.Foreground = System.Windows.Media.Brushes.LightGreen;
                    break;
                case GlobalAgentInstructions.SaveResult.Conflict:
                    SaveStatusText.Text = "편집 중 파일이 변경되어 덮어쓰지 않았습니다. 최신 파일을 확인한 뒤 다시 편집하세요.";
                    break;
                default:
                    SaveStatusText.Text = "파일을 저장할 수 없습니다. 다른 TerminalNotes 창의 저장이 끝난 뒤 다시 시도하세요.";
                    break;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            SaveStatusText.Text = "전역 지침 파일을 저장하지 못했습니다. " + exception.Message;
        }
    }
}
