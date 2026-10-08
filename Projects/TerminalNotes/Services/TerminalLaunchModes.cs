namespace TerminalNotes.Services;

public enum TerminalPermissionMode
{
    Standard,
    Auto,
    Yolo,
    Bypass
}

/// <summary>
/// Maps OpenCode permission modes. Legacy tool values are retained for settings
/// compatibility, but do not enable a second launch path.
/// </summary>
public static class TerminalLaunchModes
{
    public static bool Supports(TerminalTool tool, TerminalPermissionMode mode)
    {
        if (!Enum.IsDefined(tool) || !Enum.IsDefined(mode))
        {
            return false;
        }

        TerminalTool normalizedTool = tool == TerminalTool.Default ? TerminalTool.OpenCode : tool;
        return normalizedTool == TerminalTool.OpenCode &&
            mode is TerminalPermissionMode.Standard or TerminalPermissionMode.Auto;
    }

    public static TerminalPermissionMode Normalize(TerminalTool tool, TerminalPermissionMode mode) =>
        Supports(tool, mode) ? mode : TerminalPermissionMode.Standard;

    public static string? Command(TerminalTool tool, TerminalPermissionMode mode)
    {
        TerminalTool normalizedTool = tool == TerminalTool.Default ? TerminalTool.OpenCode : tool;
        TerminalPermissionMode normalizedMode = Normalize(tool, mode);

        return (normalizedTool, normalizedMode) switch
        {
            (TerminalTool.OpenCode, TerminalPermissionMode.Standard) => "opencode",
            (TerminalTool.OpenCode, TerminalPermissionMode.Auto) => "opencode --auto",
            _ => null
        };
    }
}
