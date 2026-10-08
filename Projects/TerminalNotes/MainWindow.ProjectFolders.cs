using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;

namespace TerminalNotes;

public partial class MainWindow
{
    private Button CreateProjectFolderButton(string project, Action<ProcessStartInfo>? launch = null)
    {
        var button = new Button { Padding = new Thickness(0), Height = 30, MinHeight = 30,
            ToolTip = "프로젝트 폴더 열기\n" + project };
        button.SetResourceReference(StyleProperty, "TaskActionButton");
        var glyph = new TaskGlyph { Kind = TaskGlyphKind.Folder };
        glyph.SetBinding(ForegroundProperty, new Binding(nameof(Foreground)) { Source = button });
        button.Content = glyph;
        AutomationProperties.SetName(button, "프로젝트 폴더 열기");
        button.Click += (_, e) =>
        {
            e.Handled = true;
            if (_closing || _closePending) return;
            if (!Directory.Exists(project))
            {
                SetStatus("프로젝트 폴더를 찾을 수 없습니다: " + project);
                return;
            }
            try
            {
                var startInfo = new ProcessStartInfo("explorer.exe") { UseShellExecute = false, ArgumentList = { project } };
                if (launch is not null) launch(startInfo);
                else { using var process = Process.Start(startInfo); }
            }
            catch (Exception ex)
            {
                SetStatus("프로젝트 폴더를 열지 못했습니다: " + ex.Message);
            }
        };
        return button;
    }
}
