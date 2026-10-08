namespace TerminalNotes.TerminalTransport;

public static class TerminalProcessFactory
{
    public static ITerminalProcess Create()
    {
        if (OperatingSystem.IsWindows()) return new WindowsConPtyProcess();
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()) return new UnixPtyProcess();
        throw new PlatformNotSupportedException("Terminal transport is supported only on Windows, Linux and macOS.");
    }
}
