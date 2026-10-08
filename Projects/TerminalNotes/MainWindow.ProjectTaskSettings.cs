namespace TerminalNotes;

public partial class MainWindow
{
    private string ProjectImprovementSummary(string project)
    {
        if (!_projectTaskContinuousModes.GetValueOrDefault(project)) return "";
        return _projectTaskImprovementUntil.GetValueOrDefault(project) is { } end
            ? end <= DateTimeOffset.Now ? "개선 종료" : $"∞ {end.LocalDateTime:MM/dd HH:mm}까지"
            : "∞ Loop";
    }
}