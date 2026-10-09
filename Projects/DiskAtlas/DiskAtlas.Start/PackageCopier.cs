using System.Security.Cryptography;

namespace DiskAtlas.Startup;

public static class PackageCopier
{
    public static async Task CopyVerifiedAsync(
        PackageManifest manifest,
        string destinationRoot,
        CancellationToken cancellationToken = default)
    {
        string destination = Path.GetFullPath(destinationRoot);
        if (Directory.Exists(destination) || File.Exists(destination))
        {
            throw new IOException("패키지 대상 폴더가 이미 존재합니다.");
        }

        PathPolicy.RejectExistingReparsePoints(Path.GetDirectoryName(destination)!);
        Directory.CreateDirectory(destination);
        PathPolicy.RejectExistingReparsePoints(destination);

        foreach (PackageEntry entry in manifest.Entries)
        {
            string sourcePath = PathPolicy.ResolveManifestPath(manifest.PayloadRoot, entry.RelativePath);
            string destinationPath = PathPolicy.ResolveManifestPath(destination, entry.RelativePath);
            PathPolicy.RejectExistingReparsePoints(sourcePath);

            string parent = Path.GetDirectoryName(destinationPath)!;
            PathPolicy.RejectExistingReparsePoints(parent);
            Directory.CreateDirectory(parent);
            PathPolicy.RejectExistingReparsePoints(parent);

            await using FileStream source = PackageManifest.OpenSourceFile(sourcePath);
            byte[] actualHash = await SHA256.HashDataAsync(source, cancellationToken).ConfigureAwait(false);
            if (!Convert.ToHexString(actualHash).Equals(entry.Hash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"복사 전 파일 해시가 일치하지 않습니다: {entry.RelativePath}");
            }

            source.Position = 0;
            await using FileStream target = new(
                destinationPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough);
            await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
            await target.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        await File.WriteAllBytesAsync(
            Path.Combine(destination, "package.sha256"),
            manifest.RawBytes,
            cancellationToken).ConfigureAwait(false);

        PackageManifest copied = await PackageManifest.LoadAsync(destination, cancellationToken).ConfigureAwait(false);
        if (!copied.Fingerprint.Equals(manifest.Fingerprint, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("복사된 패키지 지문이 일치하지 않습니다.");
        }

        await copied.VerifyAsync(cancellationToken).ConfigureAwait(false);
    }
}
