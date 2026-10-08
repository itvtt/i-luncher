namespace TerminalNotes.Services;

public static class PlanInputAppend
{
    public static string Append(string document, string input)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(input);
        string newline = document.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        return document + (document.Length == 0 ? "" : newline + newline) + input + newline;
    }
}
