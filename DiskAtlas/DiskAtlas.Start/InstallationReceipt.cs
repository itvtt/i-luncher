using System.Text;

namespace DiskAtlas.Startup;

public sealed record InstallationReceipt(string Fingerprint, string AppPath)
{
    public static InstallationReceipt Load(string path)
    {
        PathPolicy.RejectExistingReparsePoints(path);
        string[] lines = File.ReadAllLines(path, new UTF8Encoding(false, true));
        if (lines.Length != 2 ||
            !lines[0].StartsWith("fingerprint=", StringComparison.Ordinal) ||
            !lines[1].StartsWith("app=", StringComparison.Ordinal))
        {
            throw new InvalidDataException("설치 영수증 형식이 올바르지 않습니다.");
        }

        string fingerprint = lines[0]["fingerprint=".Length..];
        InstallationPaths.ValidateFingerprint(fingerprint);
        string appPath = lines[1]["app=".Length..];
        if (!Path.IsPathFullyQualified(appPath))
        {
            throw new InvalidDataException("설치 영수증의 앱 경로가 올바르지 않습니다.");
        }

        return new InstallationReceipt(fingerprint.ToLowerInvariant(), Path.GetFullPath(appPath));
    }

    internal static void WriteAtomically(string path, InstallationReceipt receipt)
    {
        InstallationPaths.ValidateFingerprint(receipt.Fingerprint);
        string temporaryPath = path + ".new-" + Guid.NewGuid().ToString("N");
        string content = $"fingerprint={receipt.Fingerprint.ToLowerInvariant()}\napp={Path.GetFullPath(receipt.AppPath)}\n";
        File.WriteAllText(temporaryPath, content, new UTF8Encoding(false));
        File.Move(temporaryPath, path, overwrite: true);
    }
}
