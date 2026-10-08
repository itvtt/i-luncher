using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace TerminalNotes.Services;

/// <summary>
/// Represents a Markdown file while preserving its on-disk Unicode encoding.
/// </summary>
public sealed class MarkdownDocument
{
    public const int MaxFileSizeBytes = 5 * 1024 * 1024;

    private readonly DocumentEncoding _encoding;
    private string _text;
    private string _originalText;
    private FileFingerprint? _originalFingerprint;

    private MarkdownDocument(
        string? filePath,
        string text,
        DocumentEncoding encoding,
        FileFingerprint? originalFingerprint)
    {
        FilePath = filePath;
        _text = text;
        _originalText = text;
        _encoding = encoding;
        _originalFingerprint = originalFingerprint;
    }

    public string? FilePath { get; private set; }

    public string Text
    {
        get => _text;
        set => _text = value ?? throw new ArgumentNullException(nameof(value));
    }

    public bool IsDirty => !string.Equals(_text, _originalText, StringComparison.Ordinal);

    public static MarkdownDocument Create() =>
        new(filePath: null, text: string.Empty, DocumentEncoding.Utf8, originalFingerprint: null);

    public static MarkdownDocument Load(string path)
    {
        string fullPath = NormalizePath(path);
        byte[] bytes;

        using (var stream = new FileStream(
                   fullPath,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.Read,
                   bufferSize: 64 * 1024,
                   FileOptions.SequentialScan))
        {
            if (stream.Length > MaxFileSizeBytes)
            {
                throw new IOException(
                    $"The file is larger than the {MaxFileSizeBytes / (1024 * 1024)} MB limit: {fullPath}");
            }

            bytes = new byte[checked((int)stream.Length)];
            stream.ReadExactly(bytes);
        }

        (DocumentEncoding encoding, int preambleLength) = DetectEncoding(bytes, fullPath);
        string text = Decode(bytes.AsSpan(preambleLength), encoding, fullPath);
        var fingerprint = new FileFingerprint(bytes.LongLength, Hash(bytes));

        return new MarkdownDocument(fullPath, text, encoding, fingerprint);
    }

    public void Save(string? path = null, bool overwriteExternalChanges = false)
    {
        string destination = path is null
            ? FilePath ?? throw new InvalidOperationException("A path is required when saving a new document.")
            : NormalizePath(path);

        bool isTrackedPath = FilePath is not null && PathsEqual(FilePath, destination);
        bool mustMatchOriginal = isTrackedPath && !overwriteExternalChanges;

        if (!isTrackedPath && !overwriteExternalChanges && File.Exists(destination))
        {
            throw new DocumentConflictException(
                destination,
                "The Save As destination already exists and was not overwritten.");
        }

        byte[] bytes = Encode(_text, _encoding, destination);
        string? temporaryPath = null;

        try
        {
            temporaryPath = WriteTemporaryFile(destination, bytes);

            if (mustMatchOriginal)
            {
                ReplaceTrackedFile(temporaryPath, destination);
            }
            else if (overwriteExternalChanges && File.Exists(destination))
            {
                ReplaceExistingFile(temporaryPath, destination);
            }
            else
            {
                MoveNewFile(temporaryPath, destination, overwriteExternalChanges);
            }

            temporaryPath = null;
            FilePath = destination;
            _originalText = _text;
            _originalFingerprint = new FileFingerprint(bytes.LongLength, Hash(bytes));
        }
        finally
        {
            if (temporaryPath is not null)
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch (IOException)
                {
                    // The destination is intact; an orphaned temporary file is safer than masking the save error.
                }
                catch (UnauthorizedAccessException)
                {
                    // See above.
                }
            }
        }
    }

    private void ReplaceTrackedFile(string temporaryPath, string destination)
    {
        if (_originalFingerprint is null)
        {
            throw new InvalidOperationException("The document has no original file fingerprint.");
        }

        FileStream currentFile;
        try
        {
            // Sharing deletion lets the atomic replace happen while denying concurrent write handles.
            currentFile = new FileStream(
                destination,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read | FileShare.Delete,
                bufferSize: 64 * 1024,
                FileOptions.SequentialScan);
        }
        catch (IOException exception) when (
            exception is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new DocumentConflictException(
                destination,
                "The file was deleted or moved after it was opened.",
                exception);
        }

        using (currentFile)
        {
            if (currentFile.Length > MaxFileSizeBytes)
            {
                throw new DocumentConflictException(
                    destination,
                    "The file changed after it was opened and is now larger than the supported limit.");
            }

            var currentFingerprint = new FileFingerprint(currentFile.Length, Hash(currentFile));
            if (currentFingerprint != _originalFingerprint.Value)
            {
                throw new DocumentConflictException(
                    destination,
                    "The file changed on disk after it was opened.");
            }

            try
            {
                File.Replace(temporaryPath, destination, destinationBackupFileName: null, ignoreMetadataErrors: true);
            }
            catch (IOException exception) when (!File.Exists(destination))
            {
                throw new DocumentConflictException(
                    destination,
                    "The file was deleted or moved while the save was being completed.",
                    exception);
            }
        }
    }

    private static void ReplaceExistingFile(string temporaryPath, string destination)
    {
        try
        {
            File.Replace(temporaryPath, destination, destinationBackupFileName: null, ignoreMetadataErrors: true);
        }
        catch (IOException) when (!File.Exists(destination))
        {
            // The destination disappeared between the existence check and the replace.
            File.Move(temporaryPath, destination);
        }
    }

    private static void MoveNewFile(string temporaryPath, string destination, bool overwriteExternalChanges)
    {
        try
        {
            File.Move(temporaryPath, destination);
        }
        catch (IOException exception) when (!overwriteExternalChanges && File.Exists(destination))
        {
            throw new DocumentConflictException(
                destination,
                "A file appeared at the Save As destination before the save completed.",
                exception);
        }
        catch (IOException) when (overwriteExternalChanges && File.Exists(destination))
        {
            File.Replace(temporaryPath, destination, destinationBackupFileName: null, ignoreMetadataErrors: true);
        }
    }

    private static string WriteTemporaryFile(string destination, byte[] bytes)
    {
        string? directory = Path.GetDirectoryName(destination);
        if (string.IsNullOrEmpty(directory))
        {
            throw new DirectoryNotFoundException($"The destination has no parent directory: {destination}");
        }

        for (int attempt = 0; attempt < 10; attempt++)
        {
            string temporaryPath = Path.Combine(directory, $".terminalnotes-{Guid.NewGuid():N}.tmp");
            FileStream stream;
            try
            {
                stream = new FileStream(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 64 * 1024,
                    FileOptions.WriteThrough);
            }
            catch (IOException) when (File.Exists(temporaryPath))
            {
                // An extremely unlikely generated-name collision; try another name.
                continue;
            }

            try
            {
                using (stream)
                {
                    stream.Write(bytes);
                    stream.Flush(flushToDisk: true);
                }

                return temporaryPath;
            }
            catch
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch (IOException)
                {
                    // Preserve the original write error.
                }
                catch (UnauthorizedAccessException)
                {
                    // Preserve the original write error.
                }

                throw;
            }
        }

        throw new IOException($"Could not create a temporary file beside: {destination}");
    }

    private static byte[] Encode(string text, DocumentEncoding documentEncoding, string path)
    {
        Encoding encoding = GetEncoding(documentEncoding);
        byte[] payload;
        try
        {
            payload = encoding.GetBytes(text);
        }
        catch (EncoderFallbackException exception)
        {
            throw new InvalidDataException($"The document contains text that cannot be encoded: {path}", exception);
        }

        byte[] preamble = encoding.GetPreamble();
        long totalLength = (long)preamble.Length + payload.Length;
        if (totalLength > MaxFileSizeBytes)
        {
            throw new IOException(
                $"The encoded document is larger than the {MaxFileSizeBytes / (1024 * 1024)} MB limit: {path}");
        }

        byte[] result = GC.AllocateUninitializedArray<byte>(checked((int)totalLength));
        preamble.CopyTo(result, 0);
        payload.CopyTo(result, preamble.Length);
        return result;
    }

    private static (DocumentEncoding Encoding, int PreambleLength) DetectEncoding(
        ReadOnlySpan<byte> bytes,
        string path)
    {
        if (bytes.StartsWith(new byte[] { 0x00, 0x00, 0xFE, 0xFF }) ||
            bytes.StartsWith(new byte[] { 0xFF, 0xFE, 0x00, 0x00 }))
        {
            throw new InvalidDataException($"UTF-32 Markdown files are not supported: {path}");
        }

        if (bytes.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }))
        {
            return (DocumentEncoding.Utf8Bom, 3);
        }

        if (bytes.StartsWith(new byte[] { 0xFF, 0xFE }))
        {
            return (DocumentEncoding.Utf16LittleEndian, 2);
        }

        if (bytes.StartsWith(new byte[] { 0xFE, 0xFF }))
        {
            return (DocumentEncoding.Utf16BigEndian, 2);
        }

        return (DocumentEncoding.Utf8, 0);
    }

    private static string Decode(ReadOnlySpan<byte> bytes, DocumentEncoding documentEncoding, string path)
    {
        try
        {
            return GetEncoding(documentEncoding).GetString(bytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException($"The file is not valid {EncodingName(documentEncoding)} text: {path}", exception);
        }
    }

    private static Encoding GetEncoding(DocumentEncoding encoding) => encoding switch
    {
        DocumentEncoding.Utf8 => new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
        DocumentEncoding.Utf8Bom => new UTF8Encoding(encoderShouldEmitUTF8Identifier: true, throwOnInvalidBytes: true),
        DocumentEncoding.Utf16LittleEndian => new UnicodeEncoding(
            bigEndian: false,
            byteOrderMark: true,
            throwOnInvalidBytes: true),
        DocumentEncoding.Utf16BigEndian => new UnicodeEncoding(
            bigEndian: true,
            byteOrderMark: true,
            throwOnInvalidBytes: true),
        _ => throw new ArgumentOutOfRangeException(nameof(encoding))
    };

    private static string EncodingName(DocumentEncoding encoding) => encoding switch
    {
        DocumentEncoding.Utf8 or DocumentEncoding.Utf8Bom => "UTF-8",
        DocumentEncoding.Utf16LittleEndian or DocumentEncoding.Utf16BigEndian => "UTF-16",
        _ => "Unicode"
    };

    private static string NormalizePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Path.GetFullPath(path);
    }

    private static bool PathsEqual(string first, string second) =>
        string.Equals(
            first,
            second,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static string Hash(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes));

    private static string Hash(Stream stream) =>
        Convert.ToHexString(SHA256.HashData(stream));

    private readonly record struct FileFingerprint(long Length, string Sha256);

    private enum DocumentEncoding
    {
        Utf8,
        Utf8Bom,
        Utf16LittleEndian,
        Utf16BigEndian
    }
}

/// <summary>
/// Thrown when saving would overwrite a file that changed or an existing Save As target.
/// </summary>
public sealed class DocumentConflictException : IOException
{
    public DocumentConflictException(string filePath, string message)
        : base(message)
    {
        FilePath = filePath;
    }

    public DocumentConflictException(string filePath, string message, Exception innerException)
        : base(message, innerException)
    {
        FilePath = filePath;
    }

    public string FilePath { get; }
}
