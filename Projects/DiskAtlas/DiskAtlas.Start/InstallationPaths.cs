namespace DiskAtlas.Startup;

public sealed class InstallationPaths
{
    public const string ProductDirectoryName = "DiskAtlas";
    public const string TaskFolderPath = "\\i-luncher";

    public InstallationPaths(string userSid)
    {
        if (!IsValidUserSid(userSid))
        {
            throw new ArgumentException("Windows 사용자 SID가 올바르지 않습니다.", nameof(userSid));
        }

        UserSid = userSid;
        ILauncherRoot = Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "i-luncher"));
        ProductRoot = Path.Combine(ILauncherRoot, ProductDirectoryName);
        UserRoot = Path.Combine(ProductRoot, userSid);
        PackagesRoot = Path.Combine(UserRoot, "packages");
        ReceiptPath = Path.Combine(UserRoot, "current.receipt");
        TaskName = $"DiskAtlas-{userSid}";
        TaskPath = $"{TaskFolderPath}\\{TaskName}";
    }

    public string UserSid { get; }
    public string ILauncherRoot { get; }
    public string ProductRoot { get; }
    public string UserRoot { get; }
    public string PackagesRoot { get; }
    public string ReceiptPath { get; }
    public string TaskName { get; }
    public string TaskPath { get; }

    public string GetPackageRoot(string fingerprint)
    {
        ValidateFingerprint(fingerprint);
        return Path.Combine(PackagesRoot, fingerprint);
    }

    public string GetAppDirectory(string fingerprint) =>
        Path.Combine(GetPackageRoot(fingerprint), "app");

    public string GetAppExecutable(string fingerprint) =>
        Path.Combine(GetAppDirectory(fingerprint), "DiskAtlas.exe");

    public static bool IsValidUserSid(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 184)
        {
            return false;
        }

        try
        {
            var sid = new System.Security.Principal.SecurityIdentifier(value);
            return sid.IsAccountSid() && value.StartsWith("S-1-5-21-", StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public static void ValidateFingerprint(string fingerprint)
    {
        if (fingerprint.Length != 64 || fingerprint.Any(static character => !Uri.IsHexDigit(character)))
        {
            throw new InvalidDataException("패키지 지문이 올바르지 않습니다.");
        }
    }
}
