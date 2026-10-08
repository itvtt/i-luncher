namespace TerminalNotes;

public partial class MainWindow
{
    // Compatibility hooks remain for existing terminal lifecycle callers. Native accepted input is
    // persisted by the OpenCode input plugin, not by this panel or inferred keystrokes.
    internal void RefreshConversationHistory() { }

    internal void TransferMemoryQuestions(TerminalPane oldPane, TerminalPane newPane) { }

    internal void DisposeConversationHistory() => DisposeTabDocuments();
}
