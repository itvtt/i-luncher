using System.Reflection;

internal static class Program
{
    private static int _failed;

    [STAThread]
    private static int Main()
    {
        Type dialogType = Type.GetType("TerminalNotes.NewProjectDialog, TerminalNotes", throwOnError: true)!;
        MethodInfo validate = dialogType.GetMethod("TryValidateFolderName", BindingFlags.Public | BindingFlags.Static)!;

        AssertValid("  새 프로젝트 A  ", "새 프로젝트 A");
        AssertValid("release.notes", "release.notes");
        AssertInvalid(null, "empty name is rejected");
        AssertInvalid("   ", "whitespace-only name is rejected");
        AssertInvalid(".", "dot name is rejected");
        AssertInvalid("..", "parent traversal name is rejected");
        AssertInvalid("../outside", "forward-slash path is rejected");
        AssertInvalid("..\\outside", "backslash path is rejected");
        AssertInvalid("C:\\outside", "rooted path is rejected");
        AssertInvalid("name:", "Windows-invalid punctuation is rejected");
        AssertInvalid(new string('x', 61), "names over 60 characters are rejected");
        AssertInvalid("CON", "reserved device name is rejected");
        AssertInvalid("con.txt", "reserved device name with extension is rejected");
        AssertInvalid("COM1", "reserved numbered device name is rejected");

        Console.WriteLine(_failed == 0 ? "PROJECT CREATION TESTS PASSED" : $"PROJECT CREATION TESTS FAILED ({_failed})");
        return _failed == 0 ? 0 : 1;

        void AssertValid(string? input, string expected)
        {
            object?[] arguments = [input, null, null];
            bool valid = (bool)validate.Invoke(null, arguments)!;
            Assert(valid && string.Equals(arguments[1] as string, expected, StringComparison.Ordinal),
                $"valid folder name normalizes to {expected}");
        }

        void AssertInvalid(string? input, string message)
        {
            object?[] arguments = [input, null, null];
            bool valid = (bool)validate.Invoke(null, arguments)!;
            Assert(!valid && !string.IsNullOrWhiteSpace(arguments[2] as string), message);
        }

        void Assert(bool condition, string message)
        {
            if (!condition) _failed++;
            Console.WriteLine((condition ? "PASS " : "FAIL ") + message);
        }
    }
}
