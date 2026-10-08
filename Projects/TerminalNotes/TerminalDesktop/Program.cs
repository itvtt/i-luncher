using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;

namespace TerminalNotes.TerminalDesktop;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args) => AppBuilder.Configure<TerminalApplication>()
        .UsePlatformDetect().StartWithClassicDesktopLifetime(args);
}

public sealed class TerminalApplication : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
            desktop.ShutdownMode = Avalonia.Controls.ShutdownMode.OnLastWindowClose;
            desktop.MainWindow = new TerminalWindow(LaunchSettings.Parse(desktop.Args ?? []));
        }
        base.OnFrameworkInitializationCompleted();
    }
}

internal sealed record LaunchSettings(string Directory, string Executable, string[] Arguments, bool Smoke = false)
{
    public static LaunchSettings Parse(string[] args)
    {
        string cwd = Environment.CurrentDirectory;
        string shell = OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe")
            : Environment.GetEnvironmentVariable("SHELL") ?? "/bin/sh";
        string[] shellArgs = OperatingSystem.IsWindows()
            ? ["-NoLogo", "-NoProfile", "-NoExit", "-Command",
                "[Console]::InputEncoding = [Text.UTF8Encoding]::new($false); [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false); $OutputEncoding = [Console]::OutputEncoding"]
            : ["-i"];
        bool smoke = false;
        for (int index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--cwd" when index + 1 < args.Length: cwd = Path.GetFullPath(args[++index]); break;
                case "--shell" when index + 1 < args.Length: shell = args[++index]; shellArgs = []; break;
                case "--smoke": smoke = true; break;
                case "--": shellArgs = args[(index + 1)..]; index = args.Length; break;
                default: throw new ArgumentException("Usage: TerminalNotes.TerminalDesktop [--cwd directory] [--shell executable] [-- arguments]");
            }
        }
        return new(cwd, shell, shellArgs, smoke);
    }
}
