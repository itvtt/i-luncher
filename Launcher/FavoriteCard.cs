using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ILauncher.Services;

namespace ILauncher;

/// <summary>즐겨찾기 카드 하나: 이 PC 의 일반 프로그램 (누르면 실행)</summary>
public sealed class FavoriteCard(FavoriteProgram program)
{
    public FavoriteProgram Program { get; } = program;
    public string Name => Program.Name;
    public string PathText => Program.Path;
    public bool Exists => File.Exists(Program.Path) || Directory.Exists(Program.Path);
    public ImageSource? Icon { get; } = FavoriteIcons.Load(program.Path);
    public Visibility IconVisibility => Icon is null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility InitialsVisibility => Icon is null ? Visibility.Visible : Visibility.Collapsed;
    public string Initials => Name.Trim() is { Length: > 0 } name ? name[..1].ToUpperInvariant() : "?";
    public double CardOpacity => Exists ? 1.0 : 0.55;
    public string ToolTipText => Exists ? Program.Path + (Program.Arguments.Length > 0 ? " " + Program.Arguments : "") : "파일이 없습니다: " + Program.Path;
    public string AutomationId => "Favorite_" + Program.Id;

    public bool Matches(string search) => search.Length == 0 ||
        Name.Contains(search, StringComparison.CurrentCultureIgnoreCase) || Program.Path.Contains(search, StringComparison.CurrentCultureIgnoreCase);
}

/// <summary>즐겨찾기 아이콘: 실행 파일·바로 가기는 64px 아이콘, 그 밖의 파일은 Windows 가 보여 주는 아이콘</summary>
public static class FavoriteIcons
{
    public static ImageSource? Load(string path)
    {
        if (Directory.Exists(path)) return ShellIcon(path);
        if (!File.Exists(path)) return null;
        if (InstalledPrograms.ResolveShortcut(path) is { } shortcut)
        {
            if (shortcut.IconFile.Length > 0 && File.Exists(shortcut.IconFile) && AppIcons.FromFile(shortcut.IconFile, shortcut.IconIndex) is { } linked) return linked;
            if (shortcut.Target.Length > 0 && File.Exists(shortcut.Target) && AppIcons.FromFile(shortcut.Target) is { } target) return target;
        }
        if (path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && AppIcons.FromFile(path) is { } icon) return icon;
        return ShellIcon(path);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellFileInfo
    {
        public IntPtr Icon;
        public int IconIndex;
        public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string TypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string path, uint attributes, ref ShellFileInfo info, uint size, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);

    private static BitmapSource? ShellIcon(string path)
    {
        const uint Icon = 0x100, LargeIcon = 0x0;
        var info = new ShellFileInfo();
        try
        {
            if (SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<ShellFileInfo>(), Icon | LargeIcon) == IntPtr.Zero || info.Icon == IntPtr.Zero) return null;
            var source = Imaging.CreateBitmapSourceFromHIcon(info.Icon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        catch (Exception error) when (error is COMException or ArgumentException or InvalidOperationException) { return null; }
        finally
        {
            if (info.Icon != IntPtr.Zero) DestroyIcon(info.Icon);
        }
    }
}
