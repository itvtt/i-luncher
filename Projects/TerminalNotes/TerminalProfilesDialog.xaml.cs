using System.IO;
using System.Security;
using System.Windows;
using Microsoft.Win32;

namespace TerminalNotes;

public partial class TerminalProfilesDialog : Window
{
    private const string FallbackWorkspace = @"C:\opencode";

    public TerminalProfilesDialog(string defaultWorkspace, bool useAutoMode, bool firstRun = false)
    {
        InitializeComponent();
        Title = firstRun ? "작업 시작" : "작업 설정";
        DialogHeading.Text = Title;
        SaveButton.Content = firstRun ? "시작" : "저장";
        DefaultWorkspaceInput.Text = string.IsNullOrWhiteSpace(defaultWorkspace)
            ? FallbackWorkspace
            : defaultWorkspace.Trim();
        GlobalAutoModeToggle.IsChecked = useAutoMode;
        DefaultWorkspaceInput.TextChanged += (_, _) => WorkspaceErrorText.Text = string.Empty;
    }

    public string DefaultWorkspace { get; private set; } = FallbackWorkspace;

    public bool UseAutoMode { get; private set; }

    private void EditGlobalInstructionsButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new GlobalAgentInstructionsDialog { Owner = this };
            dialog.ShowDialog();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            WorkspaceErrorText.Text = "전역 지침 파일을 열지 못했습니다 · " + exception.Message;
        }
    }

    private void WorkspaceBrowseButton_Click(object sender, RoutedEventArgs e)
    {
        string? initialDirectory = FindClosestExistingDirectory(DefaultWorkspaceInput.Text);
        var dialog = new OpenFolderDialog
        {
            Title = "기본 작업 폴더 선택",
            Multiselect = false
        };
        if (initialDirectory is not null)
            dialog.InitialDirectory = initialDirectory;

        if (dialog.ShowDialog(this) == true)
        {
            DefaultWorkspaceInput.Text = dialog.FolderName;
            WorkspaceErrorText.Text = string.Empty;
        }
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        string requestedWorkspace = string.IsNullOrWhiteSpace(DefaultWorkspaceInput.Text)
            ? FallbackWorkspace
            : DefaultWorkspaceInput.Text.Trim();
        string normalizedWorkspace;
        try
        {
            normalizedWorkspace = Path.GetFullPath(requestedWorkspace);
            Directory.CreateDirectory(normalizedWorkspace);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException or SecurityException)
        {
            WorkspaceErrorText.Text = "작업 폴더를 확인하거나 만들지 못했습니다. 경로와 권한을 확인하세요. " + exception.Message;
            return;
        }

        if (!Directory.Exists(normalizedWorkspace))
        {
            WorkspaceErrorText.Text = "작업 폴더를 만들지 못했습니다. 다른 폴더를 선택하세요.";
            return;
        }

        DefaultWorkspace = normalizedWorkspace;
        UseAutoMode = GlobalAutoModeToggle.IsChecked == true;
        DialogResult = true;
    }

    private static string? FindClosestExistingDirectory(string path)
    {
        try
        {
            string candidate = Path.GetFullPath(string.IsNullOrWhiteSpace(path) ? FallbackWorkspace : path.Trim());
            string? current = candidate;
            while (!string.IsNullOrWhiteSpace(current))
            {
                if (Directory.Exists(current))
                    return current;

                string? parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(current));
                if (parent is null || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
                    break;
                current = parent;
            }
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException or SecurityException)
        {
            // Invalid text should not prevent opening the folder picker.
        }

        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Directory.Exists(profile) ? profile : null;
    }
}
