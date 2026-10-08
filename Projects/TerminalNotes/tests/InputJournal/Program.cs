using System.IO;
using System.Text.Json;
using System.Security.Cryptography;
using TerminalNotes.Services;

string fixture = Path.Combine(Path.GetTempPath(), "TerminalNotes Global Instructions " + Guid.NewGuid().ToString("N"));
string project = Path.Combine(fixture, "한글 & ' project");
string command = Path.Combine(fixture, "opencode.cmd");
Directory.CreateDirectory(project);
File.WriteAllText(command, "@echo off");
AppContext.SetData("TerminalNotes.DataDirectory", Path.Combine(fixture, "appdata"));
string plan = Path.Combine(project, "terminal-notes", "plan.md");
Directory.CreateDirectory(Path.GetDirectoryName(plan)!);
File.WriteAllText(plan, "# Existing plan\n\nKeep this content.");

string legacySnapshot = FindApprovedPreviousSnapshot();
byte[] approvedPreviousBytes = File.ReadAllBytes(legacySnapshot);
string approvedPreviousHash = Convert.ToHexString(SHA256.HashData(approvedPreviousBytes));
Check(approvedPreviousHash == "407CFA4E18EE27695A3BE67CD85C7588C8BBD5B9C1000E162ACC9C181B90B106",
    "legacy fixture is the exact user-approved previous global file");
string globalPath = GlobalAgentInstructions.FilePath;
Directory.CreateDirectory(Path.GetDirectoryName(globalPath)!);
string freshDefaults = File.ReadAllText(GlobalAgentInstructions.EnsureFile());
Check(freshDefaults.Contains("프로젝트별 단일 에이전트 세션"),
    "a missing global file receives the current default");
File.WriteAllBytes(globalPath, approvedPreviousBytes);
using (var externalWriter = new FileStream(globalPath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
{
    GlobalAgentInstructions.EnsureFile();
    externalWriter.Position = 0;
    byte[] heldBytes = new byte[checked((int)externalWriter.Length)];
    externalWriter.ReadExactly(heldBytes);
    Check(heldBytes.SequenceEqual(approvedPreviousBytes) &&
          Directory.GetFiles(Path.GetDirectoryName(globalPath)!, "global-agent-instructions.md.before-approved-upgrade.*.md").Length == 0,
        "migration defers while an external write handle is open and creates no backup");
}
string defaults = File.ReadAllText(GlobalAgentInstructions.EnsureFile());
Check(defaults.Contains("프로젝트별 단일 에이전트 세션") && defaults.Contains("느린 시작, 대기, 장시간 실행"),
    "the approved previous global file upgrades to the current instructions");
string[] upgradeBackups = Directory.GetFiles(Path.GetDirectoryName(globalPath)!, "global-agent-instructions.md.before-approved-upgrade.*.md");
Check(upgradeBackups.Length == 1 && File.ReadAllBytes(upgradeBackups[0]).SequenceEqual(approvedPreviousBytes),
    "migration retains the exact previous bytes in a same-directory backup");
Check(GlobalAgentInstructions.EnsureFile() == globalPath &&
      File.ReadAllText(globalPath) == defaults &&
      Directory.GetFiles(Path.GetDirectoryName(globalPath)!, "global-agent-instructions.md.before-approved-upgrade.*.md").Length == 1,
    "repeated ensure is idempotent and creates no additional backup");
Check(GlobalAgentInstructions.TrySave(defaults, "# My edited rules\n" + defaults) == GlobalAgentInstructions.SaveResult.Saved,
    "global instructions can be edited with the expected current contents");
string edited = File.ReadAllText(globalPath);
Check(GlobalAgentInstructions.EnsureFile() == globalPath && File.ReadAllText(globalPath) == edited,
    "ensuring the global file preserves custom user edits");
Check(GlobalAgentInstructions.TrySave(defaults, "stale edit") == GlobalAgentInstructions.SaveResult.Conflict &&
      File.ReadAllText(globalPath) == edited,
    "stale editor cannot overwrite newer global instructions");
File.WriteAllText(globalPath, "changed outside TerminalNotes");
Check(GlobalAgentInstructions.TrySave(edited, "stale external overwrite") == GlobalAgentInstructions.SaveResult.Conflict &&
      File.ReadAllText(globalPath) == "changed outside TerminalNotes",
    "external Markdown edits are detected and preserved");
string raceBaseline = File.ReadAllText(globalPath);
string[] raceValues = Enumerable.Range(0, 4).Select(index => "concurrent edit " + index).ToArray();
var raceResults = await Task.WhenAll(raceValues.Select(value => Task.Run(() => GlobalAgentInstructions.TrySave(raceBaseline, value))));
Check(raceResults.Count(result => result == GlobalAgentInstructions.SaveResult.Saved) == 1 &&
      raceValues.Contains(File.ReadAllText(globalPath)),
    "concurrent TerminalNotes saves commit one complete version without losing the winner");
var inherited = new { model = "vendor/model", plugin = new[] { "existing-plugin" }, instructions = new[] { "custom.md" } };
using (var context = new OpenCodeProjectContext(project, plan, "Context fixture"))
using (JsonDocument config = JsonDocument.Parse(context.CreateProcessConfigContent(JsonSerializer.Serialize(inherited))))
{
    string[] paths = config.RootElement.GetProperty("instructions").EnumerateArray().Select(path => path.GetString()!).ToArray();
    Check(paths.Contains(globalPath) && paths.Contains(context.InstructionFilePath) && paths.Contains("custom.md"),
        "process config includes global, project, and inherited instructions");
    Check(config.RootElement.GetProperty("plugin").GetArrayLength() == 1 &&
          config.RootElement.GetProperty("plugin")[0].GetString() == "existing-plugin",
        "process config preserves existing plugins without adding input journaling");
    Check(config.RootElement.GetProperty("model").GetString() == "vendor/model",
        "process config preserves inherited provider settings");
}

var external = ExternalTerminalLauncher.Prepare(command, project, "ses_fixture", plan, "CMD fixture", false);
using (JsonDocument launch = JsonDocument.Parse(File.ReadAllText(Path.Combine(external.Directory, "launch.json"))))
using (JsonDocument config = JsonDocument.Parse(launch.RootElement.GetProperty("Config").GetString()!))
{
    string[] paths = config.RootElement.GetProperty("instructions").EnumerateArray().Select(path => path.GetString()!).ToArray();
    Check(paths.Contains(globalPath) && paths.Any(path => path.EndsWith("instructions.md", StringComparison.OrdinalIgnoreCase)),
        "standalone CMD config retains global instructions and its persistent project context");
    Check(!config.RootElement.TryGetProperty("plugin", out _),
        "standalone CMD config does not install the input journal plugin");
}
Check(File.ReadAllText(plan) == "# Existing plan\n\nKeep this content.",
    "adding global instructions never changes the project plan");
Console.WriteLine("GLOBAL INSTRUCTIONS TESTS PASSED: " + fixture);

static void Check(bool value, string message)
{
    if (!value) throw new Exception(message);
    Console.WriteLine("PASS " + message);
}

static string FindApprovedPreviousSnapshot()
{
    string fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "approved-global-instructions.md");
    if (File.Exists(fixture)) return fixture;
    throw new FileNotFoundException("The approved previous global instructions test fixture was not copied to the output directory.");
}