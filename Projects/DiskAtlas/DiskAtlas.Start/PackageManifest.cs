using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace DiskAtlas.Startup;

public sealed record PackageEntry(string Hash, string RelativePath);

public sealed class PackageManifest
{
    private static readonly Regex EntryPattern = new(
        "^(?<hash>[0-9a-fA-F]{64})  (?<path>[^\\r\\n]+)$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private PackageManifest(string payloadRoot, string fingerprint, byte[] bytes, IReadOnlyList<PackageEntry> entries)
    {
        PayloadRoot = payloadRoot;
        Fingerprint = fingerprint;
        RawBytes = bytes;
        Entries = entries;
    }

    public string PayloadRoot { get; }
    public string Fingerprint { get; }
    public byte[] RawBytes { get; }
    public IReadOnlyList<PackageEntry> Entries { get; }

    public static async Task<PackageManifest> LoadAsync(string payloadRoot, CancellationToken cancellationToken = default)
    {
        string root = Path.GetFullPath(payloadRoot);
        PathPolicy.RejectExistingReparsePoints(root);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException("DiskAtlas 앱 패키지 폴더를 찾을 수 없습니다.");
        }

        string manifestPath = Path.Combine(root, "package.sha256");
        PathPolicy.RejectExistingReparsePoints(manifestPath);
        byte[] bytes = await File.ReadAllBytesAsync(manifestPath, cancellationToken).ConfigureAwait(false);
        if (bytes.Length == 0 || bytes.Length > 4 * 1024 * 1024)
        {
            throw new InvalidDataException("패키지 매니페스트 크기가 올바르지 않습니다.");
        }

        string text;
        try
        {
            text = new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException("패키지 매니페스트는 UTF-8이어야 합니다.", exception);
        }

        var entries = new List<PackageEntry>();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string rawLine in text.Split('\n'))
        {
            string line = rawLine.EndsWith('\r') ? rawLine[..^1] : rawLine;
            if (line.Length == 0)
            {
                continue;
            }

            Match match = EntryPattern.Match(line);
            if (!match.Success)
            {
                throw new InvalidDataException("패키지 매니페스트 형식이 올바르지 않습니다.");
            }

            string relativePath = match.Groups["path"].Value.Replace('\\', '/');
            _ = PathPolicy.ResolveManifestPath(root, relativePath);
            if (relativePath.Equals("package.sha256", StringComparison.OrdinalIgnoreCase) || !paths.Add(relativePath))
            {
                throw new InvalidDataException($"중복되거나 예약된 패키지 경로입니다: {relativePath}");
            }

            entries.Add(new PackageEntry(match.Groups["hash"].Value.ToLowerInvariant(), relativePath));
        }

        if (entries.Count == 0 || entries.Count > 10_000)
        {
            throw new InvalidDataException("패키지 파일 목록이 비어 있거나 너무 큽니다.");
        }

        if (!paths.Contains("DiskAtlas.exe"))
        {
            throw new InvalidDataException("패키지에 DiskAtlas.exe가 없습니다.");
        }

        string fingerprint = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return new PackageManifest(root, fingerprint, bytes, entries);
    }

    public async Task VerifyAsync(CancellationToken cancellationToken = default)
    {
        ValidateExactFileSet();
        foreach (PackageEntry entry in Entries)
        {
            string sourcePath = PathPolicy.ResolveManifestPath(PayloadRoot, entry.RelativePath);
            PathPolicy.RejectExistingReparsePoints(sourcePath);
            await using FileStream source = OpenSourceFile(sourcePath);
            string actual = Convert.ToHexString(await SHA256.HashDataAsync(source, cancellationToken).ConfigureAwait(false));
            if (!actual.Equals(entry.Hash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"패키지 파일 해시가 일치하지 않습니다: {entry.RelativePath}");
            }
        }
    }

    public void ValidateExactFileSet()
    {
        var expected = new HashSet<string>(
            Entries.Select(entry => entry.RelativePath.Replace('/', Path.DirectorySeparatorChar))
                .Append("package.sha256"),
            StringComparer.OrdinalIgnoreCase);
        var actual = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>();
        pending.Push(PayloadRoot);
        while (pending.Count != 0)
        {
            string directory = pending.Pop();
            PathPolicy.RejectExistingReparsePoints(directory);
            foreach (string file in Directory.EnumerateFiles(directory))
            {
                PathPolicy.RejectExistingReparsePoints(file);
                actual.Add(Path.GetRelativePath(PayloadRoot, file));
            }

            foreach (string child in Directory.EnumerateDirectories(directory))
            {
                PathPolicy.RejectExistingReparsePoints(child);
                pending.Push(child);
            }
        }

        if (!expected.SetEquals(actual))
        {
            throw new InvalidDataException("패키지 폴더의 파일 목록이 매니페스트와 일치하지 않습니다.");
        }
    }

    internal static FileStream OpenSourceFile(string path) => new(
        path,
        FileMode.Open,
        FileAccess.Read,
        FileShare.Read,
        bufferSize: 128 * 1024,
        FileOptions.Asynchronous | FileOptions.SequentialScan);
}
