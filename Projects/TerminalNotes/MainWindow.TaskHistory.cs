using System.IO;
using System.Windows;
using System.Windows.Controls;
using TerminalNotes.Services;

namespace TerminalNotes;

public partial class MainWindow
{
    private async void ShowProjectTaskHistory(string project)
    {
        try
        {
            var runs = (await new AutomationStore(project).LoadAsync()).OrderByDescending(run => run.CreatedAt).ToArray();
            if (_closing || runs.Length == 0) return;
            var window = new Window
            {
                Owner = this, Title = "실행 기록 · " + ProjectDisplayName(project), Width = 720, Height = 640,
                MinWidth = 460, MinHeight = 380, WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = StateColorBrush("#121C2D"), Foreground = StateColorBrush("#F3F5F8"), FontFamily = FontFamily, FontSize = 14
            };
            window.Resources.MergedDictionaries.Add(Resources);
            var panel = new Grid { Margin = new Thickness(24) };
            panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            panel.RowDefinitions.Add(new RowDefinition());
            panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            panel.Children.Add(new TextBlock { Text = "실행 기록", FontSize = 24, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 16) });
            var list = new ListBox { MaxHeight = 120, Background = StateColorBrush("#1E2B41"), Foreground = StateColorBrush("#F3F5F8"), BorderThickness = new Thickness(0), Margin = new Thickness(0, 0, 0, 12) };
            foreach (var run in runs) list.Items.Add(new ListBoxItem { Content = $"{run.CreatedAt.ToLocalTime():yyyy.MM.dd HH:mm}  ·  {AutomationStateLabel(run.State)}", Tag = run, Padding = new Thickness(10, 7, 10, 7) });
            var detail = new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(16), FontSize = 14 };
            list.SelectionChanged += (_, _) =>
            {
                if ((list.SelectedItem as ListBoxItem)?.Tag is not AutomationRun run) return;
                detail.Text = string.Join("\n\n", new[]
                {
                    $"{AutomationStateLabel(run.State)} · {RoleLabel(run.NextRole)} · {WorkflowPhaseLabel(run.WorkflowPhase)}",
                    run.StatusDetail, run.LastSummary,
                    run.FinalVerificationEvidence is { Length: > 0 } evidence ? "최종 검증\n" + evidence : null,
                    run.DeliveryEvidence is { Length: > 0 } delivery ? "실행 근거\n" + delivery : null,
                    run.LastReviewSummary is { Length: > 0 } review ? "검토 결과\n" + review : null,
                    run.CoordinatorPlanningPlan?.ToMarkdown() ?? run.CoordinatorPlan
                }.Where(value => !string.IsNullOrWhiteSpace(value)));
            };
            Grid.SetRow(list, 1); panel.Children.Add(list);
            Grid.SetRow(detail, 2); panel.Children.Add(detail);
            var record = new Button { Content = "기록 폴더 열기", HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
            record.Click += (_, _) =>
            {
                if ((list.SelectedItem as ListBoxItem)?.Tag is not AutomationRun run) return;
                string folder = Path.Combine(project, "terminal-notes", "runs", run.Id.ToString("N"));
                if (!Directory.Exists(folder)) folder = Path.Combine(project, "terminal-notes");
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe") { ArgumentList = { folder }, UseShellExecute = false }); }
                catch (Exception ex) { SetStatus("기록 폴더 열기 실패 · " + ex.Message); }
            };
            Grid.SetRow(record, 3); panel.Children.Add(record);
            list.SelectedIndex = 0;
            window.Content = panel;
            window.Show();
        }
        catch (Exception ex) { SetStatus("실행 기록 확인 실패 · " + ex.Message); }
    }
}
