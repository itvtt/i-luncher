using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using TerminalNotes.Services;

namespace TerminalNotes;

public partial class MainWindow
{
    private TextBlock? _commandTarget, _commandUsage;
    private FrameworkElement? _projectStartSurface;
    private TerminalTabState? _composerState;
    private bool _updatingComposer;
    private readonly HashSet<Task> _journalWrites = [];

    private static string InputModeLabel(TerminalInputMode mode) => mode switch
    {
        TerminalInputMode.Question => "질문", TerminalInputMode.Plan => "플랜",
        TerminalInputMode.Build => "빌드", TerminalInputMode.Review => "검토", _ => "명령"
    };

    private void InitializeWorkbench()
    {
        var panel = new Grid { Background = StateColorBrush("#141417") };
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition());

        var header = new Border
        {
            Background = StateColorBrush("#18181C"),
            BorderBrush = StateColorBrush("#303037"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(16, 14, 12, 14)
        };
        var heading = new StackPanel();
        var projectHeading = new Grid();
        projectHeading.ColumnDefinitions.Add(new ColumnDefinition());
        projectHeading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _commandTarget = new TextBlock
        {
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(243, 245, 248)),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
        projectHeading.Children.Add(_commandTarget);
        _commandUsage = new TextBlock
        {
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(165, 173, 186)),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(8, 0, 0, 0)
        };
        Grid.SetColumn(_commandUsage, 1);
        projectHeading.Children.Add(_commandUsage);
        heading.Children.Add(projectHeading);

        header.Child = heading;
        panel.Children.Add(header);

        var workbenchBody = new Grid();
        workbenchBody.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        workbenchBody.RowDefinitions.Add(new RowDefinition());
        Grid.SetRow(workbenchBody, 1);
        panel.Children.Add(workbenchBody);
        InitializeAutomationProgressPanel(workbenchBody);
        InitializeTabDocuments(workbenchBody);
        if (_tabDocumentSurface is not null)
            Grid.SetRow(_tabDocumentSurface, 1);
        var projectChoices = new StackPanel { Margin = new Thickness(16, 20, 16, 20), VerticalAlignment = VerticalAlignment.Center };
        projectChoices.Children.Add(new TextBlock
        {
            Text = "프로젝트를 시작해 보세요",
            FontSize = 17,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(243, 245, 248)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 6)
        });
        projectChoices.Children.Add(new TextBlock
        {
            Text = "1  왼쪽 태스크 패널에서 프로젝트를 열거나 새로 만듭니다.\n2  만들고 싶은 것을 편한 말로 적습니다.\n3  AI가 터미널에서 작업합니다.",
            FontSize = 13,
            LineHeight = 20,
            Foreground = new SolidColorBrush(Color.FromRgb(183, 190, 201)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 16)
        });
        _projectStartSurface = new Border
        {
            Background = StateColorBrush("#141417"),
            Padding = new Thickness(6),
            Child = projectChoices
        };
        Grid.SetRow(_projectStartSurface, 1);
        workbenchBody.Children.Add(_projectStartSurface);

        WorkbenchHost.Content = panel;
        UpdateWorkbenchTarget();
    }

    private void UpdateWorkbenchTarget()
    {
        if (_updatingComposer) return;
        _updatingComposer = true;
        try
        {
            var state = SelectedPlanTerminal();
            _composerState = state;
            bool hasProject = state is not null || _selectedProjectPath is not null;
            string project = state?.Pane.WorkingDirectory ?? CurrentProjectPath;
            _commandTarget!.Text = hasProject ? ProjectDisplayName(project) : "프로젝트 선택";
            _commandTarget.ToolTip = hasProject ? DefaultProjectPlanPath(project) : _workspace;
            NewTerminalButton.ToolTip = "새 OpenCode 터미널\n" + (ActiveTerminal?.WorkingDirectory ?? _workspace);
            string? usage = state?.Pane.TokenUsageLabel;
            _commandUsage!.Text = usage == "토큰 정보 없음" ? string.Empty : usage ?? string.Empty;
            _commandUsage.Visibility = string.IsNullOrWhiteSpace(_commandUsage.Text)
                ? Visibility.Collapsed
                : Visibility.Visible;
            SetTabDocumentModeVisible(hasProject);
            if (_projectStartSurface is not null)
                _projectStartSurface.Visibility = hasProject ? Visibility.Collapsed : Visibility.Visible;
            UpdateTabDocumentEditor(state);
            UpdateProjectInputComposer();
            UpdatePlanExecutionControls();
            UpdateAutomationProgressPanel();
        }
        finally { _updatingComposer = false; }
    }

    private Button CreateProjectActionButton(string geometry, string label, string toolTip, bool iconOnly = false)
    {
        var icon = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse(geometry),
            Stroke = new SolidColorBrush(Color.FromRgb(204, 216, 229)),
            StrokeThickness = 1.6,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            Stretch = Stretch.Uniform,
            Width = 14,
            Height = 14
        };
        object content = icon;
        if (!iconOnly)
        {
            var contentPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            contentPanel.Children.Add(icon);
            contentPanel.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(5, 0, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            content = contentPanel;
        }
        var button = new Button
        {
            Content = content,
            Width = iconOnly ? 32 : double.NaN,
            Height = iconOnly ? 32 : 34,
            Padding = new Thickness(iconOnly ? 0 : 4),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            ToolTip = toolTip
        };
        AutomationProperties.SetName(button, label);
        return button;
    }

    private Button CreateProjectChoiceCard(string geometry, string title, string description)
    {
        var icon = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse(geometry),
            Stroke = new SolidColorBrush(Color.FromRgb(126, 220, 187)),
            StrokeThickness = 1.8,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            Stretch = Stretch.Uniform,
            Width = 42,
            Height = 36,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        var content = new StackPanel { Margin = new Thickness(8) };
        content.Children.Add(icon);
        content.Children.Add(new TextBlock
        {
            Text = title,
            FontWeight = FontWeights.SemiBold,
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.FromRgb(243, 245, 248)),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 10, 0, 3)
        });
        content.Children.Add(new TextBlock
        {
            Text = description,
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.FromRgb(165, 173, 186)),
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        });
        var button = new Button
        {
            Content = content,
            MinHeight = 132,
            Margin = new Thickness(5),
            Padding = new Thickness(8),
            ToolTip = description
        };
        button.SetResourceReference(FrameworkElement.StyleProperty, "DocumentActionButton");
        AutomationProperties.SetName(button, title);
        return button;
    }

    // Manual terminal text stays in the terminal session; it is not copied into files or journal history.
    // Keystroke reconstruction is not an authoritative submitted request. The
    // process-local OpenCode plugin records the accepted message (also in CMD).
    private void ObserveManualSubmission(TerminalTabState state, string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.TrimStart().StartsWith('/')) return;
        var runtime = _automationRuntimes.Values.FirstOrDefault(value =>
            value.SingleTerminal?.Tag is TerminalTabState taskState && ReferenceEquals(taskState.Pane, state.Pane));
        if (runtime is null) return;
        if (runtime.StartingSingle) runtime.IntentVersion++;

        OpenCodeSubmissionObservation? observation = state.Pane.LastManualSubmissionObservation;
        if (observation is null) return;
        state.ManualSubmissionPending = true;
        state.ManualSubmissionConfirmationBaseline = observation.Value.ConfirmationVersion;
        state.ManualSubmissionGeneration = observation.Value.Generation;
    }

    private void ObserveNativeSubmissionConfirmation(TerminalTabState state, long version, long generation)
    {
        if (!state.ManualSubmissionPending || version <= state.ManualSubmissionConfirmationBaseline ||
            generation != state.ManualSubmissionGeneration || generation != state.Pane.NativeSubmissionGeneration)
            return;

        state.ManualSubmissionPending = false;
        var runtime = _automationRuntimes.Values.FirstOrDefault(value =>
            value.SingleTerminal?.Tag is TerminalTabState taskState && ReferenceEquals(taskState.Pane, state.Pane));
        if (runtime is null) return;
        runtime.SingleError = null;
        UpdateProjectTaskActivity();
    }

    private void TrackJournalWork(Task pending)
    {
        _journalWrites.Add(pending);
        _ = pending.ContinueWith(_ => Dispatcher.BeginInvoke(() => _journalWrites.Remove(pending)), TaskScheduler.Default);
    }

    private void OpenConversationJournalFolder()
    {
        var state = _composerState;
        if (state is null) return;
        try
        {
            string project = state.Pane.WorkingDirectory;
            string documentPath = Path.GetFullPath(state.PlanDocumentPath, Path.GetFullPath(project));
            string target = Path.GetDirectoryName(documentPath) ?? project;
            Directory.CreateDirectory(target);
            var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
            start.ArgumentList.Add(target);
            Process.Start(start);
        }
        catch (Exception ex) { SetStatus("기록 폴더를 열 수 없습니다 · " + ex.Message); }
    }

    private void Compact_Click(object sender, RoutedEventArgs e) => ShowCompactDashboard();
    private void Automation_Click(object sender, RoutedEventArgs e) => ShowAutomationWindow();
}
