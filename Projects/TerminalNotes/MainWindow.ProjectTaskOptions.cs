using System.IO;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using TerminalNotes.Services;

namespace TerminalNotes;

public partial class MainWindow
{
    private readonly HashSet<string> _loadedSingleImprovementPreferences = new(StringComparer.OrdinalIgnoreCase);

    private AutomationRun? EditableProjectRun(string project) => _knownAutomationRuns.Values
        .Where(run => DirectoriesEqual(run.ProjectDirectory, project) &&
            run.State is AutomationState.Running or AutomationState.Scheduled or AutomationState.Paused)
        .OrderByDescending(run => run.State is AutomationState.Running or AutomationState.Scheduled)
        .ThenByDescending(run => run.CreatedAt).FirstOrDefault();

    private void UpdateProjectImprovementControls(string project, ProjectTaskRow row, bool editing)
    {
        if (editing) return;
        LoadSingleProjectImprovementPreference(project);
        if (IsMultiProjectTask(project) && EditableProjectRun(project) is { } run)
        {
            _projectTaskContinuousModes[project] = run.Options.ContinuousImprovement;
            _projectTaskImprovementUntil[project] = run.Options.ImprovementUntil;
        }
        DateTimeOffset? until = _projectTaskImprovementUntil.GetValueOrDefault(project);
        bool expired = until is { } end && end <= DateTimeOffset.Now;
        _updatingTaskOptions = true;
        try { row.Loop.IsChecked = _projectTaskContinuousModes.GetValueOrDefault(project) && !expired; }
        finally { _updatingTaskOptions = false; }
        ConfigureSingleContinuousImprovement(project);
        row.Until.Header = until is { } time ? $"Loop 종료 예약 · {time.LocalDateTime:MM/dd HH:mm}" : "Loop 종료 예약";
        row.Until.ToolTip = until is { } full
            ? $"{full.LocalDateTime:yyyy-MM-dd HH:mm} · {(expired ? "시간 만료 · 현재 개선 검토 후 종료" : "현재 개선 검토 후 종료") }"
            : "종료 날짜·시간을 설정합니다. 실행 중에도 변경할 수 있습니다.";
    }

    private async Task<bool> ApplyProjectImprovementOptionsAsync(string project, bool enabled, DateTimeOffset? until)
    {
        if (_closing || _closePending || RuntimeFor(project).StartingRun || !_projectTaskOptionWrites.Add(project)) return false;
        bool previousMode = _projectTaskContinuousModes.GetValueOrDefault(project);
        DateTimeOffset? previousUntil = _projectTaskImprovementUntil.GetValueOrDefault(project);
        UpdateProjectTaskActivity();
        try
        {
            // A checkbox re-enable after expiry starts with no expired deadline.
            if (enabled && until <= DateTimeOffset.Now) until = null;
            var store = new AutomationStore(project);
            var active = (await store.LoadAsync())
                .Where(run => run.State is AutomationState.Running or AutomationState.Scheduled or AutomationState.Paused)
                .OrderByDescending(run => run.State is AutomationState.Running or AutomationState.Scheduled)
                .ThenByDescending(run => run.CreatedAt).FirstOrDefault();
            bool singleMode = !IsMultiProjectTask(project);
            if (!singleMode && active is not null)
            {
                var updated = await store.SetContinuousImprovementAsync(active.Id, enabled, until);
                if (!_knownAutomationRuns.TryGetValue(updated.Id, out var known) ||
                    (known.Revision <= updated.Revision && known.UpdatedAt <= updated.UpdatedAt))
                    _knownAutomationRuns[updated.Id] = updated;
            }
            else if (singleMode)
            {
                SingleProjectContinuousImprovementStore.Write(project, enabled, until);
            }
            _projectTaskContinuousModes[project] = enabled;
            _projectTaskImprovementUntil[project] = until;
            ConfigureSingleContinuousImprovement(project);
            SetStatus(enabled ? until is { } end ? $"Loop · {end.LocalDateTime:MM/dd HH:mm}까지" : "Loop · 시간 제한 없음"
                : "Loop 해제 · 현재 개선의 최종 검토 후 종료");
            return true;
        }
        catch (Exception ex)
        {
            _projectTaskContinuousModes[project] = previousMode;
            _projectTaskImprovementUntil[project] = previousUntil;
            SetStatus("Loop 설정을 저장하지 못했습니다 · " + ex.Message);
            return false;
        }
        finally
        {
            _projectTaskOptionWrites.Remove(project);
            UpdateProjectTaskActivity();
        }
    }

    private void ConfigureSingleContinuousImprovement(string project)
    {
        if (IsMultiProjectTask(project)) return;
        LoadSingleProjectImprovementPreference(project);
        if (RuntimeFor(project).SingleTerminal?.Tag is not TerminalTabState terminal) return;
        DateTimeOffset? until = _projectTaskImprovementUntil.GetValueOrDefault(project);
        bool enabled = _projectTaskContinuousModes.GetValueOrDefault(project) &&
            (until is null || until > DateTimeOffset.Now);
        terminal.Pane.ConfigureSingleContinuousImprovement(project, enabled, until);
    }

    private void LoadSingleProjectImprovementPreference(string project)
    {
        if (IsMultiProjectTask(project) || !_loadedSingleImprovementPreferences.Add(project)) return;
        try
        {
            var preference = SingleProjectContinuousImprovementStore.Read(project);
            if (preference is null) return;
            _projectTaskContinuousModes[project] = preference.Enabled;
            _projectTaskImprovementUntil[project] = preference.Until;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        { SetStatus("Loop 설정을 읽지 못했습니다 · " + ex.Message); }
    }

    private void ShowImprovementSchedule(string project)
    {
        if (_closing || _closePending || RuntimeFor(project).StartingRun ||
            _projectTaskOptionWrites.Contains(project) || _projectTaskControlWrites.Contains(project)) return;
        var window = new Window
        {
            Owner = this, Title = "Loop 종료 시간", Width = 420, SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = StateColorBrush("#181B21"), Foreground = StateColorBrush("#F3F5F8"),
            FontFamily = FontFamily, FontSize = 14
        };
        window.Resources.MergedDictionaries.Add(Resources);
        DateTime value = (_projectTaskImprovementUntil.GetValueOrDefault(project) ?? DateTimeOffset.Now.AddHours(1)).LocalDateTime;
        var panel = new StackPanel { Margin = new Thickness(22) };
        panel.Children.Add(new TextBlock { Text = ProjectDisplayName(project), FontSize = 17, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        var limited = new CheckBox { Content = "종료 시간 지정", IsChecked = true, Margin = new Thickness(0, 18, 0, 12) };
        panel.Children.Add(limited);
        panel.Children.Add(new TextBlock { Text = "날짜 (연-월-일)", Margin = new Thickness(0, 0, 0, 5) });
        var date = new TextBox { Text = value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), Name = "ImprovementEndDate" };
        panel.Children.Add(date);
        panel.Children.Add(new TextBlock { Text = "시간", ToolTip = "24시간 · 이 컴퓨터의 현지 시간", Margin = new Thickness(0, 12, 0, 5) });
        var time = new TextBox { Text = value.ToString("HH:mm", CultureInfo.InvariantCulture), Name = "ImprovementEndTime" };
        panel.Children.Add(time);
        limited.Checked += (_, _) => date.IsEnabled = time.IsEnabled = true;
        limited.Unchecked += (_, _) => date.IsEnabled = time.IsEnabled = false;
        limited.ToolTip = "지정 시간이 지나면 현재 개선의 최종 검토를 마치고 종료합니다.";
        var feedback = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = StateColorBrush("#FFCA80") };
        date.TextChanged += (_, _) => feedback.Text = "";
        time.TextChanged += (_, _) => feedback.Text = "";
        panel.Children.Add(feedback);
        var apply = new Button { Content = "적용", IsDefault = true, Margin = new Thickness(0, 12, 0, 0) };
        apply.Click += async (_, _) =>
        {
            DateTimeOffset? until = null;
            if (limited.IsChecked == true)
            {
                if (!DateTime.TryParseExact(date.Text.Trim() + " " + time.Text.Trim(), "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var parsed) || TimeZoneInfo.Local.IsInvalidTime(parsed))
                { feedback.Text = "날짜와 시간을 확인하세요. 예: 2026-09-23 / 18:30"; return; }
                until = new DateTimeOffset(parsed, TimeZoneInfo.Local.GetUtcOffset(parsed));
                if (until <= DateTimeOffset.Now) { feedback.Text = "현재보다 이후 시간을 지정하세요."; return; }
            }
            apply.IsEnabled = false;
            if (await ApplyProjectImprovementOptionsAsync(project, true, until)) window.Close();
            else { feedback.Text = "설정을 적용하지 못했습니다. 작업 상태를 확인하고 다시 시도하세요."; apply.IsEnabled = true; }
        };
        panel.Children.Add(apply);
        window.Content = panel;
        window.ShowDialog();
    }
}
