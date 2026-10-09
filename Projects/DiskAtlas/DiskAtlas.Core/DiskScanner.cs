using DiskAtlas.Core.Scanning;

namespace DiskAtlas.Core;

public sealed class DiskScanner
{
    public async Task<ScanResult> ScanAsync(string path, IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        cancellationToken.ThrowIfCancellationRequested();
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!Directory.Exists(fullPath))
            throw new DirectoryNotFoundException($"Scan root '{fullPath}' does not exist or is not a directory.");

        var root = Path.GetPathRoot(fullPath);
        var isWholeDrive = root is not null &&
            string.Equals(Path.TrimEndingDirectorySeparator(root), fullPath, StringComparison.OrdinalIgnoreCase);
        var initialWarnings = new List<string>();
        string? reason = null;
        if (isWholeDrive && IsLocalNtfsDrive(root!, out reason))
        {
            try
            {
                return await Task.Run(() => NtfsMftScanner.Scan(root!, progress, cancellationToken),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is RawScanUnavailableException or UnauthorizedAccessException or IOException or OverflowException)
            {
                initialWarnings.Add($"NTFS MFT mode was unavailable ({ex.Message}); used file-system enumeration instead.");
            }
        }
        else if (isWholeDrive && reason is not null)
        {
            initialWarnings.Add($"NTFS MFT mode was not used ({reason}).");
        }

        return await FileSystemScanner.ScanAsync(fullPath, progress, cancellationToken, initialWarnings)
            .ConfigureAwait(false);
    }

    private static bool IsLocalNtfsDrive(string root, out string? reason)
    {
        reason = null;
        try
        {
            var drive = new DriveInfo(root);
            if (drive.DriveType is DriveType.Network or DriveType.CDRom or DriveType.Ram)
            {
                reason = "the target is not a local disk volume";
                return false;
            }
            if (!drive.IsReady)
            {
                reason = "the volume is not ready";
                return false;
            }
            if (!string.Equals(drive.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase))
            {
                reason = $"the volume format is {drive.DriveFormat}, not NTFS";
                return false;
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            reason = ex.Message;
            return false;
        }
    }
}
