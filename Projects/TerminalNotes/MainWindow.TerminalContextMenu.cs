using System.Windows.Controls;

namespace TerminalNotes;

public partial class MainWindow
{
    private const string TerminalCopyTag = "TerminalCopy";
    private const string TerminalPasteTag = "TerminalPaste";
    private const string TerminalClearSelectionTag = "TerminalClearSelection";

    private static ContextMenu CreateTerminalContextMenu(TerminalPane terminal)
    {
        var menu = new ContextMenu();
        menu.Items.Add(TerminalMenuItem("복사", TerminalCopyTag, terminal.CopySelection, "Ctrl+Shift+C"));
        menu.Items.Add(TerminalMenuItem("붙여넣기", TerminalPasteTag, terminal.PasteFromClipboard, "Ctrl+V"));
        menu.Items.Add(new Separator());
        menu.Items.Add(TerminalMenuItem("전체 선택", "TerminalSelectAll", terminal.SelectAll));
        menu.Items.Add(TerminalMenuItem("선택 해제", TerminalClearSelectionTag, terminal.ClearSelection));
        menu.Items.Add(new Separator());
        menu.Items.Add(TerminalMenuItem("화면 지우기", "TerminalClearScreen", terminal.ClearScreen));
        return menu;
    }

    private static MenuItem TerminalMenuItem(string header, string tag, Action action, string? gesture = null)
    {
        var item = new MenuItem { Header = header, Tag = tag, InputGestureText = gesture ?? string.Empty };
        item.Click += (_, _) => action();
        return item;
    }

    private static void UpdateTerminalContextMenu(TerminalPane terminal, bool hasSelection)
    {
        foreach (var item in terminal.ContextMenu.Items.OfType<MenuItem>())
        {
            item.IsEnabled = item.Tag switch
            {
                TerminalCopyTag or TerminalClearSelectionTag => hasSelection,
                TerminalPasteTag => terminal.CanPasteFromClipboard,
                _ => true
            };
        }
    }
}
