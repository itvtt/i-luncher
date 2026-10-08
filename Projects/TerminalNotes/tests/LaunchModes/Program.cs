using System.Text.Json;
using TerminalNotes.Services;

internal static class Program
{
    private static int Main()
    {
        try
        {
            VerifyEveryCombination();
            VerifyCommandsAndSafeFallbacks();
            VerifyProfileJsonCompatibility();
            Console.WriteLine("PASS: launch mode support, safe normalization, commands, and profile compatibility.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("FAIL: " + exception);
            return 1;
        }
    }

    private static void VerifyEveryCombination()
    {
        var expected = new HashSet<(TerminalTool Tool, TerminalPermissionMode Mode)>
        {
            (TerminalTool.Default, TerminalPermissionMode.Standard),
            (TerminalTool.Default, TerminalPermissionMode.Auto),
            (TerminalTool.OpenCode, TerminalPermissionMode.Standard),
            (TerminalTool.OpenCode, TerminalPermissionMode.Auto)
        };

        foreach (TerminalTool tool in Enum.GetValues<TerminalTool>())
        {
            foreach (TerminalPermissionMode mode in Enum.GetValues<TerminalPermissionMode>())
            {
                bool supported = TerminalLaunchModes.Supports(tool, mode);
                Require(
                    supported == expected.Contains((tool, mode)),
                    $"Unexpected support result for {tool}/{mode}: {supported}.");
                Require(
                    TerminalLaunchModes.Normalize(tool, mode) ==
                    (supported ? mode : TerminalPermissionMode.Standard),
                    $"Unsafe normalization result for {tool}/{mode}.");
            }
        }

        var invalidMode = (TerminalPermissionMode)999;
        var invalidTool = (TerminalTool)999;
        Require(!TerminalLaunchModes.Supports(TerminalTool.OpenCode, invalidMode), "An invalid mode was reported as supported.");
        Require(TerminalLaunchModes.Normalize(TerminalTool.OpenCode, invalidMode) == TerminalPermissionMode.Standard, "Invalid mode did not normalize to Standard.");
        Require(!TerminalLaunchModes.Supports(invalidTool, TerminalPermissionMode.Yolo), "An invalid tool was reported as supporting Yolo.");
        Require(TerminalLaunchModes.Normalize(invalidTool, TerminalPermissionMode.Yolo) == TerminalPermissionMode.Standard, "Invalid tool did not normalize to Standard.");
    }

    private static void VerifyCommandsAndSafeFallbacks()
    {
        var commands = new Dictionary<(TerminalTool Tool, TerminalPermissionMode Mode), string?>
        {
            [(TerminalTool.Default, TerminalPermissionMode.Standard)] = "opencode",
            [(TerminalTool.Default, TerminalPermissionMode.Auto)] = "opencode --auto",
            [(TerminalTool.Default, TerminalPermissionMode.Yolo)] = "opencode",
            [(TerminalTool.OpenCode, TerminalPermissionMode.Standard)] = "opencode",
            [(TerminalTool.OpenCode, TerminalPermissionMode.Auto)] = "opencode --auto",
            [(TerminalTool.Codex, TerminalPermissionMode.Standard)] = null,
            [(TerminalTool.ClaudeCode, TerminalPermissionMode.Standard)] = null,
            [(TerminalTool.PowerShell, TerminalPermissionMode.Standard)] = null,
            [(TerminalTool.Custom, TerminalPermissionMode.Standard)] = null
        };

        foreach (((TerminalTool tool, TerminalPermissionMode mode), string? expected) in commands)
        {
            Require(TerminalLaunchModes.Command(tool, mode) == expected, $"Wrong command for {tool}/{mode}.");
        }

        Require(TerminalLaunchModes.Command(TerminalTool.OpenCode, TerminalPermissionMode.Bypass) == "opencode", "Unsupported OpenCode Bypass did not fall back to Standard.");
        Require(TerminalLaunchModes.Command(TerminalTool.Codex, TerminalPermissionMode.Yolo) is null, "Legacy Codex profile retained a launch command.");
        Require(TerminalLaunchModes.Command(TerminalTool.ClaudeCode, TerminalPermissionMode.Bypass) is null, "Legacy Claude profile retained a launch command.");
        Require(TerminalLaunchModes.Command(TerminalTool.PowerShell, TerminalPermissionMode.Yolo) is null, "PowerShell gained an unsafe fallback command.");
        Require(TerminalLaunchModes.Command(TerminalTool.Custom, TerminalPermissionMode.Bypass) is null, "Custom gained an unsafe fallback command.");
        Require(TerminalLaunchModes.Command((TerminalTool)999, TerminalPermissionMode.Bypass) is null, "Unknown tool gained a command.");

        foreach (TerminalTool tool in Enum.GetValues<TerminalTool>())
        {
            string? command = TerminalLaunchModes.Command(tool, (TerminalPermissionMode)999);
            Require(
                command is null || !command.Contains("danger", StringComparison.OrdinalIgnoreCase),
                $"Invalid mode produced a dangerous command for {tool}.");
        }
    }

    private static void VerifyProfileJsonCompatibility()
    {
        const string legacyJson = """
            {"Slot":2,"Tool":1,"Title":"Legacy","Command":null,"WorkingDirectory":"Z:\\codex"}
            """;
        TerminalLaunchProfile legacy = JsonSerializer.Deserialize<TerminalLaunchProfile>(legacyJson)
            ?? throw new InvalidOperationException("Legacy profile did not deserialize.");
        Require(legacy.Mode == TerminalPermissionMode.Standard, "A legacy profile did not default to Standard.");
        Require(legacy.Slot == 2 && legacy.Tool == TerminalTool.Codex && legacy.Title == "Legacy", "Legacy profile fields changed.");

        var oldSourceCompatible = new TerminalLaunchProfile(3, TerminalTool.ClaudeCode, "Claude", null, "C:\\work");
        Require(oldSourceCompatible.Mode == TerminalPermissionMode.Standard, "Existing constructor usage did not retain Standard.");

        var configured = new TerminalLaunchProfile(4, TerminalTool.OpenCode, "OpenCode", null, null, TerminalPermissionMode.Auto);
        string json = JsonSerializer.Serialize(configured);
        TerminalLaunchProfile roundTrip = JsonSerializer.Deserialize<TerminalLaunchProfile>(json)
            ?? throw new InvalidOperationException("Configured profile did not deserialize.");
        Require(roundTrip == configured, "Permission mode did not survive JSON round-trip.");

        const string invalidModeJson = """
            {"Slot":5,"Tool":2,"Mode":999}
            """;
        TerminalLaunchProfile invalid = JsonSerializer.Deserialize<TerminalLaunchProfile>(invalidModeJson)
            ?? throw new InvalidOperationException("Invalid-mode profile did not deserialize.");
        Require(TerminalLaunchModes.Normalize(TerminalTool.OpenCode, invalid.Mode) == TerminalPermissionMode.Standard, "Persisted invalid mode was not safely normalized.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
