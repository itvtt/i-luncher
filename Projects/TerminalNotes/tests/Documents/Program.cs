using System.Text;
using TerminalNotes.Services;

var tests = new (string Name, Action Run)[]
{
    ("new document lifecycle", NewDocumentLifecycle),
    ("encoding and newline preservation", EncodingAndNewlinePreservation),
    ("external edit conflict and explicit overwrite", ExternalEditConflict),
    ("external deletion conflict", ExternalDeletionConflict),
    ("Save As existing target conflict", SaveAsConflict),
    ("file size limits", FileSizeLimits),
    ("invalid UTF-8 rejected", InvalidUtf8Rejected),
};

int failures = 0;
foreach ((string name, Action run) in tests)
{
    try
    {
        run();
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception exception)
    {
        failures++;
        Console.Error.WriteLine($"FAIL {name}: {exception}");
    }
}

return failures == 0 ? 0 : 1;

static void NewDocumentLifecycle()
{
    using var temp = new TempDirectory();
    string path = Path.Combine(temp.Path, "new.md");
    MarkdownDocument document = MarkdownDocument.Create();

    Equal(null, document.FilePath);
    Equal(string.Empty, document.Text);
    False(document.IsDirty);
    Throws<InvalidOperationException>(() => document.Save());

    document.Text = "alpha\r\nbeta\n";
    True(document.IsDirty);
    document.Save(path);

    Equal(Path.GetFullPath(path), document.FilePath);
    False(document.IsDirty);
    SequenceEqual(Encoding.UTF8.GetBytes(document.Text), File.ReadAllBytes(path));

    document.Text = "changed";
    True(document.IsDirty);
    document.Text = "alpha\r\nbeta\n";
    False(document.IsDirty);
}

static void EncodingAndNewlinePreservation()
{
    using var temp = new TempDirectory();
    string text = "# 제목\r\nline two\nlast\r";
    (string FileName, Encoding Encoding)[] cases =
    {
        ("utf8.md", new UTF8Encoding(false, true)),
        ("utf8-bom.md", new UTF8Encoding(true, true)),
        ("utf16-le.md", new UnicodeEncoding(false, true, true)),
        ("utf16-be.md", new UnicodeEncoding(true, true, true)),
    };

    foreach ((string fileName, Encoding encoding) in cases)
    {
        string path = Path.Combine(temp.Path, fileName);
        File.WriteAllBytes(path, WithPreamble(encoding, text));

        MarkdownDocument document = MarkdownDocument.Load(path);
        Equal(text, document.Text);
        False(document.IsDirty);

        document.Text += "추가";
        document.Save();
        SequenceEqual(WithPreamble(encoding, text + "추가"), File.ReadAllBytes(path));
        False(document.IsDirty);
    }
}

static void ExternalEditConflict()
{
    using var temp = new TempDirectory();
    string path = Path.Combine(temp.Path, "conflict.md");
    File.WriteAllText(path, "original", new UTF8Encoding(false));
    MarkdownDocument document = MarkdownDocument.Load(path);
    document.Text = "my changes";

    File.WriteAllText(path, "external changes", new UTF8Encoding(false));
    DocumentConflictException conflict = Throws<DocumentConflictException>(() => document.Save());
    Equal(Path.GetFullPath(path), conflict.FilePath);
    Equal("external changes", File.ReadAllText(path));
    True(document.IsDirty);

    document.Save(overwriteExternalChanges: true);
    Equal("my changes", File.ReadAllText(path));
    False(document.IsDirty);
    NoTemporaryFiles(temp.Path);
}

static void ExternalDeletionConflict()
{
    using var temp = new TempDirectory();
    string path = Path.Combine(temp.Path, "deleted.md");
    File.WriteAllText(path, "original");
    MarkdownDocument document = MarkdownDocument.Load(path);
    document.Text = "my changes";
    File.Delete(path);

    Throws<DocumentConflictException>(() => document.Save());
    False(File.Exists(path));
    True(document.IsDirty);
    NoTemporaryFiles(temp.Path);
}

static void SaveAsConflict()
{
    using var temp = new TempDirectory();
    string source = Path.Combine(temp.Path, "source.md");
    string destination = Path.Combine(temp.Path, "destination.md");
    File.WriteAllText(source, "source");
    File.WriteAllText(destination, "keep me");

    MarkdownDocument document = MarkdownDocument.Load(source);
    document.Text = "replacement";
    Throws<DocumentConflictException>(() => document.Save(destination));
    Equal("keep me", File.ReadAllText(destination));
    Equal(Path.GetFullPath(source), document.FilePath);

    document.Save(destination, overwriteExternalChanges: true);
    Equal("replacement", File.ReadAllText(destination));
    Equal(Path.GetFullPath(destination), document.FilePath);
    False(document.IsDirty);
    NoTemporaryFiles(temp.Path);
}

static void FileSizeLimits()
{
    using var temp = new TempDirectory();
    string path = Path.Combine(temp.Path, "large.md");
    File.WriteAllBytes(path, new byte[MarkdownDocument.MaxFileSizeBytes + 1]);
    Throws<IOException>(() => MarkdownDocument.Load(path));

    string destination = Path.Combine(temp.Path, "too-large-to-save.md");
    MarkdownDocument document = MarkdownDocument.Create();
    document.Text = new string('x', MarkdownDocument.MaxFileSizeBytes + 1);
    Throws<IOException>(() => document.Save(destination));
    False(File.Exists(destination));
}

static void InvalidUtf8Rejected()
{
    using var temp = new TempDirectory();
    string path = Path.Combine(temp.Path, "invalid.md");
    File.WriteAllBytes(path, [0xC3, 0x28]);
    Throws<InvalidDataException>(() => MarkdownDocument.Load(path));
}

static byte[] WithPreamble(Encoding encoding, string text)
{
    byte[] preamble = encoding.GetPreamble();
    byte[] payload = encoding.GetBytes(text);
    byte[] result = new byte[preamble.Length + payload.Length];
    preamble.CopyTo(result, 0);
    payload.CopyTo(result, preamble.Length);
    return result;
}

static void NoTemporaryFiles(string directory) =>
    Equal(0, Directory.GetFiles(directory, ".terminalnotes-*.tmp").Length);

static T Throws<T>(Action action) where T : Exception
{
    try
    {
        action();
    }
    catch (T exception)
    {
        return exception;
    }

    throw new Exception($"Expected {typeof(T).Name}.");
}

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new Exception($"Expected <{expected}> but got <{actual}>.");
    }
}

static void True(bool value)
{
    if (!value)
    {
        throw new Exception("Expected true.");
    }
}

static void False(bool value) => True(!value);

static void SequenceEqual(byte[] expected, byte[] actual)
{
    if (!expected.AsSpan().SequenceEqual(actual))
    {
        throw new Exception(
            $"Byte sequences differ (expected {expected.Length} bytes, actual {actual.Length} bytes).");
    }
}

sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"TerminalNotes.Tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        Directory.Delete(Path, recursive: true);
    }
}
