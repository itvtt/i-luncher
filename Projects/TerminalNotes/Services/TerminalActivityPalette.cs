namespace TerminalNotes.Services;

/// <summary>
/// Saturated state colors shared by terminal bodies, headings, and floating tiles.
/// Only work in progress is green and requests for user input are orange.
/// Every inactive state is gray; orange uses dark text for contrast.
/// </summary>
public static class TerminalActivityPalette
{
    public static string Background(TerminalActivity activity, bool hasCompletedWork = false) => activity switch
    {
        TerminalActivity.NeedsInput => "#FF8A00",
        _ when hasCompletedWork => "#252D3A",
        TerminalActivity.Active => "#00853F",
        _ => "#252D3A"
    };

    public static string Foreground(TerminalActivity activity, bool hasCompletedWork = false) =>
        activity == TerminalActivity.NeedsInput ? "#211000" : "#FFFFFF";

    /// <summary>Brighter accents for text shown outside the colored terminal surface.</summary>
    public static string Accent(TerminalActivity activity, bool hasCompletedWork = false) => activity switch
    {
        TerminalActivity.NeedsInput => "#FF8A00",
        _ when hasCompletedWork => "#B6C4D9",
        TerminalActivity.Active => "#00FF73",
        _ => "#B6C4D9"
    };
}
