using System.Buffers;
using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Xml;

namespace DiskAtlas.Core;

public sealed class ContentSearchService
{
    private const int ReadBufferCharacters = 32 * 1024;
    private const int ProbeBytes = 8 * 1024;
    private const int WorkBatchSize = 128;
    public const long MaximumOfficeExpandedXmlBytes = 256L * 1024 * 1024;
    public const int MaximumOfficeArchiveEntries = 20_000;

    public static IReadOnlyList<string> SupportedOfficeFileExtensions { get; } =
        Array.AsReadOnly([".docx", ".xlsx", ".pptx"]);

    /// <summary>
    /// PDF and legacy binary Office formats are not decoded. Files with other
    /// extensions are searched when their initial bytes are plausible UTF-8 text.
    /// </summary>
    public static string CapabilitySummary =>
        "Text/code/log files, plausible UTF-8 text with unknown extensions, and streaming .docx/.xlsx/.pptx XML are supported. " +
        "BOM-marked UTF-8/UTF-16/UTF-32 are detected. PDF and legacy binary Office formats are not supported.";

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".text", ".md", ".markdown", ".log", ".csv", ".tsv", ".xml", ".json", ".jsonl",
        ".html", ".htm", ".css", ".scss", ".less", ".js", ".jsx", ".mjs", ".cjs", ".ts", ".tsx",
        ".cs", ".fs", ".vb", ".java", ".kt", ".kts", ".c", ".h", ".cpp", ".hpp", ".cc", ".go",
        ".rs", ".py", ".rb", ".php", ".swift", ".sql", ".r", ".lua", ".sh", ".bash", ".zsh",
        ".ps1", ".bat", ".cmd", ".ini", ".cfg", ".conf", ".config", ".properties", ".toml", ".yaml",
        ".yml", ".xaml", ".csproj", ".fsproj", ".vbproj", ".sln", ".props", ".targets", ".gradle",
        ".cmake", ".makefile", ".dockerfile", ".gitignore", ".gitattributes", ".editorconfig", ".env"
    };

    public static IReadOnlyList<string> SupportedTextFileExtensions { get; } =
        Array.AsReadOnly(TextExtensions.Order(StringComparer.OrdinalIgnoreCase).ToArray());

    private static readonly HashSet<string> KnownBinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".dll", ".sys", ".com", ".obj", ".pdb", ".bin", ".dat", ".db", ".sqlite", ".mdb",
        ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".tif", ".tiff", ".webp", ".ico", ".heic", ".raw",
        ".mp3", ".wav", ".flac", ".aac", ".ogg", ".wma", ".mp4", ".mkv", ".avi", ".mov", ".wmv",
        ".zip", ".rar", ".7z", ".gz", ".bz2", ".xz", ".tar", ".iso", ".cab", ".msi", ".pdf",
        ".doc", ".xls", ".ppt", ".rtf", ".woff", ".woff2", ".ttf", ".otf"
    };

    public async Task<SearchReport> SearchAsync(
        IEnumerable<ScanNode> files,
        SearchOptions options,
        IProgress<SearchProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(options);
        if (options.MaxResults <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "MaxResults must be greater than zero.");

        cancellationToken.ThrowIfCancellationRequested();
        QueryExpression expression = QueryExpression.Parse(options.Query);
        var stopwatch = Stopwatch.StartNew();

        // Snapshot enumeration and all names before parallel work. Filename-only
        // searching consequently performs no filesystem reads. Preparation is
        // offloaded because callers commonly invoke this method on the UI thread.
        Preparation preparation = await Task.Run(() => Prepare(files, cancellationToken), cancellationToken)
            .ConfigureAwait(false);
        List<Candidate> eligible = preparation.Eligible;
        long excluded = preparation.Excluded;

        long examined = 0;
        long skipped = 0;
        long matchesObserved = 0;
        bool limitReached = false;
        var hits = new List<SearchHit>(Math.Min(Math.Min(options.MaxResults, eligible.Count), 4096));
        progress?.Report(new SearchProgress(0, eligible.Count, 0, 0));
        long nextProgressTimestamp = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 10;

        void ReportProgress(bool force = false)
        {
            if (progress is null)
                return;

            if (!force)
            {
                long now = Stopwatch.GetTimestamp();
                long expected = Volatile.Read(ref nextProgressTimestamp);
                if (now < expected || Interlocked.CompareExchange(
                        ref nextProgressTimestamp,
                        now + Stopwatch.Frequency / 10,
                        expected) != expected)
                    return;
            }

            progress.Report(new SearchProgress(
                Interlocked.Read(ref examined),
                eligible.Count,
                Math.Min(Interlocked.Read(ref matchesObserved), options.MaxResults),
                Interlocked.Read(ref skipped)));
        }

        int degree = Math.Max(1, Math.Min(Environment.ProcessorCount, 8));
        for (int offset = 0; offset < eligible.Count && !limitReached; offset += WorkBatchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count = Math.Min(WorkBatchSize, eligible.Count - offset);
            var outcomes = new SearchOutcome[count];

            await Parallel.ForEachAsync(
                Enumerable.Range(0, count),
                new ParallelOptions { CancellationToken = cancellationToken, MaxDegreeOfParallelism = degree },
                async (localIndex, token) =>
                {
                    Candidate candidate = eligible[offset + localIndex];
                    SearchOutcome outcome = await SearchCandidateAsync(candidate, options, expression, token).ConfigureAwait(false);
                    outcomes[localIndex] = outcome;

                    Interlocked.Increment(ref examined);
                    if (outcome.Skipped)
                        Interlocked.Increment(ref skipped);
                    if (outcome.Hit is not null)
                        Interlocked.Increment(ref matchesObserved);
                    ReportProgress();
                }).ConfigureAwait(false);

            // Commit each completed batch in the original snapshot order.
            for (int i = 0; i < outcomes.Length; i++)
            {
                SearchHit? hit = outcomes[i].Hit;
                if (hit is null)
                    continue;

                if (hits.Count == options.MaxResults)
                {
                    limitReached = true;
                    break;
                }

                hits.Add(hit);
                if (hits.Count == options.MaxResults)
                {
                    limitReached = true;
                    break;
                }
            }
        }

        stopwatch.Stop();
        ReportProgress(force: true);
        return new SearchReport(hits, examined, excluded, skipped, limitReached, stopwatch.Elapsed);
    }

    private static Preparation Prepare(IEnumerable<ScanNode> files, CancellationToken cancellationToken)
    {
        var eligible = new List<Candidate>();
        long excluded = 0;
        foreach (ScanNode node in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentNullException.ThrowIfNull(node);
            var candidate = new Candidate(node, node.Name ?? string.Empty);
            if (SearchPolicy.IsExcluded(node))
                excluded++;
            else
                eligible.Add(candidate);
        }

        return new Preparation(eligible, excluded);
    }

    private static async ValueTask<SearchOutcome> SearchCandidateAsync(
        Candidate candidate,
        SearchOptions options,
        QueryExpression expression,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ScanNode node = candidate.Node;
        if (node.IsDirectory)
            return SearchOutcome.NotSearchable;

        StringComparison comparison = options.MatchCase
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;

        if (options.Mode == SearchMode.FileName)
        {
            if (expression.MatchesText(candidate.Name, comparison))
                return new SearchOutcome(new SearchHit(node, "Name", candidate.Name), false);
            return SearchOutcome.NoMatch;
        }

        if (options.Mode == SearchMode.NameOrContents)
        {
            if (expression.MatchesText(candidate.Name, comparison))
                return new SearchOutcome(new SearchHit(node, "Name", candidate.Name), false);
        }

        var matchState = new QueryMatchState(expression, comparison);
        if (options.Mode == SearchMode.NameOrContents)
            matchState.MatchWhole(candidate.Name);

        string extension = node.Extension;
        if (extension is ".docx" or ".xlsx" or ".pptx")
            return await SearchOfficeAsync(node, matchState, cancellationToken).ConfigureAwait(false);

        if (KnownBinaryExtensions.Contains(extension))
            return SearchOutcome.NotSearchable;

        return await SearchTextFileAsync(node, matchState, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async ValueTask<SearchOutcome> SearchTextFileAsync(
        ScanNode node,
        QueryMatchState matchState,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(
                node.FullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            byte[] probe = ArrayPool<byte>.Shared.Rent(ProbeBytes);
            int probeLength;
            try
            {
                probeLength = await stream.ReadAsync(probe.AsMemory(0, ProbeBytes), cancellationToken).ConfigureAwait(false);
                bool hasBom = HasUnicodeBom(probe.AsSpan(0, probeLength));
                if (!hasBom && !LooksLikeUtf8Text(probe.AsSpan(0, probeLength)))
                    return SearchOutcome.NotSearchable;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(probe);
            }

            stream.Position = 0;
            using var reader = new StreamReader(
                stream,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false),
                detectEncodingFromByteOrderMarks: true,
                bufferSize: 16 * 1024,
                leaveOpen: false);

            char[] buffer = ArrayPool<char>.Shared.Rent(ReadBufferCharacters);
            try
            {
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int read = await reader.ReadAsync(buffer.AsMemory(0, ReadBufferCharacters), cancellationToken)
                        .ConfigureAwait(false);
                    if (read == 0)
                        break;

                    MatchDetails? match = matchState.Feed(buffer.AsSpan(0, read));
                    if (match is not null)
                        return new SearchOutcome(new SearchHit(node, "Contents", match.Preview, match.LineNumber), false);
                }
            }
            finally
            {
                ArrayPool<char>.Shared.Return(buffer);
            }

            return SearchOutcome.NoMatch;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or DecoderFallbackException)
        {
            return SearchOutcome.NotSearchable;
        }
    }

    private static async ValueTask<SearchOutcome> SearchOfficeAsync(
        ScanNode node,
        QueryMatchState matchState,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(
                node.FullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);

            if (archive.Entries.Count > MaximumOfficeArchiveEntries)
                return SearchOutcome.NotSearchable;

            long expandedBytes = 0;
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsSearchableOfficeXml(entry.FullName, node.Extension))
                    continue;

                if (entry.Length < 0 || expandedBytes > MaximumOfficeExpandedXmlBytes - entry.Length)
                    return SearchOutcome.NotSearchable;
                expandedBytes += entry.Length;

                await using Stream entryStream = entry.Open();
                using XmlReader xml = XmlReader.Create(entryStream, new XmlReaderSettings
                {
                    Async = true,
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null,
                    IgnoreComments = true,
                    IgnoreProcessingInstructions = true
                });

                if (!xml.CanReadValueChunk)
                    return SearchOutcome.NotSearchable;

                char[] valueBuffer = ArrayPool<char>.Shared.Rent(ReadBufferCharacters);
                try
                {
                    while (await xml.ReadAsync().ConfigureAwait(false))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if ((xml.NodeType == XmlNodeType.EndElement &&
                             IsOfficeTextBoundary(xml.LocalName, entry.FullName, node.Extension)) ||
                            (xml.NodeType == XmlNodeType.Element &&
                             IsOfficeExplicitBreak(xml.LocalName, node.Extension)))
                        {
                            MatchDetails? elementBoundaryMatch = matchState.Feed(" ".AsSpan());
                            if (elementBoundaryMatch is not null)
                                return new SearchOutcome(new SearchHit(node, "Contents", elementBoundaryMatch.Preview), false);
                            continue;
                        }

                        if (xml.NodeType is not (XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace))
                            continue;

                        while (true)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            int read = await xml.ReadValueChunkAsync(valueBuffer, 0, ReadBufferCharacters)
                                .ConfigureAwait(false);
                            if (read == 0)
                                break;

                            MatchDetails? match = matchState.Feed(valueBuffer.AsSpan(0, read));
                            if (match is not null)
                                return new SearchOutcome(new SearchHit(node, "Contents", match.Preview), false);
                        }
                    }
                }
                finally
                {
                    ArrayPool<char>.Shared.Return(valueBuffer);
                }

                MatchDetails? boundaryMatch = matchState.Feed(" ".AsSpan());
                if (boundaryMatch is not null)
                    return new SearchOutcome(new SearchHit(node, "Contents", boundaryMatch.Preview), false);
            }

            return SearchOutcome.NoMatch;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or XmlException or NotSupportedException)
        {
            return SearchOutcome.NotSearchable;
        }
    }

    private static bool IsSearchableOfficeXml(string entryName, string extension)
    {
        string normalized = entryName.Replace('\\', '/');
        if (!normalized.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            return false;

        return extension switch
        {
            ".docx" => normalized.Equals("word/document.xml", StringComparison.OrdinalIgnoreCase) ||
                       normalized.StartsWith("word/header", StringComparison.OrdinalIgnoreCase) ||
                       normalized.StartsWith("word/footer", StringComparison.OrdinalIgnoreCase) ||
                       normalized.Equals("word/footnotes.xml", StringComparison.OrdinalIgnoreCase) ||
                       normalized.Equals("word/endnotes.xml", StringComparison.OrdinalIgnoreCase) ||
                       normalized.Equals("word/comments.xml", StringComparison.OrdinalIgnoreCase),
            ".xlsx" => normalized.Equals("xl/sharedStrings.xml", StringComparison.OrdinalIgnoreCase) ||
                       normalized.StartsWith("xl/worksheets/", StringComparison.OrdinalIgnoreCase),
            ".pptx" => normalized.StartsWith("ppt/slides/slide", StringComparison.OrdinalIgnoreCase) ||
                       normalized.StartsWith("ppt/notesSlides/notesSlide", StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }

    private static bool IsOfficeTextBoundary(string localName, string entryName, string extension)
    {
        if (extension is ".docx" or ".pptx")
            return localName.Equals("p", StringComparison.OrdinalIgnoreCase);

        if (extension != ".xlsx")
            return false;

        string normalized = entryName.Replace('\\', '/');
        return normalized.Equals("xl/sharedStrings.xml", StringComparison.OrdinalIgnoreCase)
            ? localName.Equals("si", StringComparison.OrdinalIgnoreCase)
            : localName.Equals("c", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsOfficeExplicitBreak(string localName, string extension) => extension switch
    {
        ".docx" => localName.Equals("br", StringComparison.OrdinalIgnoreCase) ||
                   localName.Equals("cr", StringComparison.OrdinalIgnoreCase) ||
                   localName.Equals("tab", StringComparison.OrdinalIgnoreCase),
        ".pptx" => localName.Equals("br", StringComparison.OrdinalIgnoreCase),
        _ => false
    };

    private static bool HasUnicodeBom(ReadOnlySpan<byte> bytes)
    {
        return HasPrefix(bytes, 0xEF, 0xBB, 0xBF) ||
               HasPrefix(bytes, 0xFF, 0xFE, 0x00, 0x00) ||
               HasPrefix(bytes, 0x00, 0x00, 0xFE, 0xFF) ||
               HasPrefix(bytes, 0xFF, 0xFE) ||
               HasPrefix(bytes, 0xFE, 0xFF);
    }

    private static bool HasPrefix(ReadOnlySpan<byte> bytes, params byte[] prefix) =>
        bytes.Length >= prefix.Length && bytes[..prefix.Length].SequenceEqual(prefix);

    private static bool LooksLikeUtf8Text(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
            return true;

        int controls = 0;
        foreach (byte value in bytes)
        {
            if (value == 0)
                return false;
            if (value < 0x20 && value is not (0x09 or 0x0A or 0x0C or 0x0D or 0x1B))
                controls++;
        }

        if (controls * 100 > bytes.Length * 2)
            return false;

        try
        {
            Decoder decoder = new UTF8Encoding(false, true).GetDecoder();
            _ = decoder.GetCharCount(bytes, flush: false);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private sealed class QueryMatchState
    {
        private readonly QueryExpression _expression;
        private readonly bool[] _matchedTerms;
        private readonly StringComparison _comparison;
        private StreamingTermMatcher?[]? _matchers;

        public QueryMatchState(QueryExpression expression, StringComparison comparison)
        {
            _expression = expression;
            _comparison = comparison;
            _matchedTerms = new bool[expression.Terms.Count];
        }

        public bool IsMatch => _expression.Evaluate(_matchedTerms);

        public void MatchWhole(string text)
        {
            for (int index = 0; index < _matchedTerms.Length; index++)
            {
                if (_matchedTerms[index] || !text.Contains(_expression.Terms[index], _comparison))
                    continue;

                _matchedTerms[index] = true;
                if (IsMatch)
                    return;
            }
        }

        public MatchDetails? Feed(ReadOnlySpan<char> characters)
        {
            if (characters.IsEmpty)
                return null;

            string incoming = characters.ToString();
            _matchers ??= new StreamingTermMatcher?[_matchedTerms.Length];
            for (int index = 0; index < _matchedTerms.Length; index++)
            {
                if (_matchedTerms[index])
                    continue;

                StreamingTermMatcher matcher = _matchers[index] ??=
                    new StreamingTermMatcher(_expression.Terms[index], _comparison);
                MatchDetails? match = matcher.Feed(incoming);
                if (match is null)
                    continue;

                _matchedTerms[index] = true;
                if (IsMatch)
                    return match;
            }

            return null;
        }
    }

    private sealed class StreamingTermMatcher
    {
        private const int PreviewRadius = 80;
        private readonly string _query;
        private readonly StringComparison _comparison;
        private string _tail = string.Empty;
        private long _lineAtTailStart = 1;
        private bool _pendingCarriageReturn;

        public StreamingTermMatcher(string query, StringComparison comparison)
        {
            _query = query;
            _comparison = comparison;
        }

        public MatchDetails? Feed(string incoming)
        {
            if (incoming.Length == 0)
                return null;

            long lineAtIncomingStart = _lineAtTailStart;
            bool pendingAtIncomingStart = _pendingCarriageReturn;
            AdvanceLineState(_tail, ref lineAtIncomingStart, ref pendingAtIncomingStart);

            if (_tail.Length != 0)
            {
                int prefixLength = Math.Min(incoming.Length, _query.Length - 1);
                string bridge = string.Concat(_tail.AsSpan(), incoming.AsSpan(0, prefixLength));
                int bridgeIndex = bridge.IndexOf(_query, _comparison);
                if (bridgeIndex >= 0)
                    return CreateMatch(bridge, bridgeIndex, _lineAtTailStart, _pendingCarriageReturn);
            }

            int incomingIndex = incoming.IndexOf(_query, _comparison);
            if (incomingIndex >= 0)
                return CreateMatch(incoming, incomingIndex, lineAtIncomingStart, pendingAtIncomingStart);

            int keep = Math.Min(_query.Length - 1, _tail.Length + incoming.Length);
            if (keep == 0)
            {
                _lineAtTailStart = lineAtIncomingStart;
                _pendingCarriageReturn = pendingAtIncomingStart;
                AdvanceLineState(incoming, ref _lineAtTailStart, ref _pendingCarriageReturn);
                _tail = string.Empty;
            }
            else if (incoming.Length >= keep)
            {
                _lineAtTailStart = lineAtIncomingStart;
                _pendingCarriageReturn = pendingAtIncomingStart;
                AdvanceLineState(incoming.AsSpan(0, incoming.Length - keep),
                    ref _lineAtTailStart, ref _pendingCarriageReturn);
                _tail = incoming[^keep..];
            }
            else
            {
                int consumeFromTail = _tail.Length + incoming.Length - keep;
                AdvanceLineState(_tail.AsSpan(0, consumeFromTail),
                    ref _lineAtTailStart, ref _pendingCarriageReturn);
                _tail = string.Concat(_tail.AsSpan(consumeFromTail), incoming.AsSpan());
            }

            return null;
        }

        private MatchDetails CreateMatch(
            string text,
            int index,
            long startingLine,
            bool pendingCarriageReturn)
        {
            long line = CountLineAt(text.AsSpan(0, index), startingLine, pendingCarriageReturn);
            int previewStart = Math.Max(0, index - PreviewRadius);
            int previewEnd = Math.Min(text.Length, index + _query.Length + PreviewRadius);
            string preview = NormalizePreview(text[previewStart..previewEnd]);
            return new MatchDetails(preview, line);
        }

        private static long CountLineAt(ReadOnlySpan<char> text, long startingLine, bool pendingCarriageReturn)
        {
            long line = startingLine;
            AdvanceLineState(text, ref line, ref pendingCarriageReturn);
            return line;
        }

        private static void AdvanceLineState(
            ReadOnlySpan<char> text,
            ref long line,
            ref bool pendingCarriageReturn)
        {
            foreach (char value in text)
            {
                if (value == '\r')
                {
                    line++;
                    pendingCarriageReturn = true;
                }
                else if (value == '\n')
                {
                    if (!pendingCarriageReturn)
                        line++;
                    pendingCarriageReturn = false;
                }
                else
                {
                    pendingCarriageReturn = false;
                }
            }
        }

        private static string NormalizePreview(string preview)
        {
            var builder = new StringBuilder(preview.Length);
            bool previousWhitespace = false;
            foreach (char value in preview)
            {
                bool whitespace = char.IsWhiteSpace(value);
                if (!whitespace || !previousWhitespace)
                    builder.Append(whitespace ? ' ' : value);
                previousWhitespace = whitespace;
            }
            return builder.ToString().Trim();
        }
    }

    private sealed record Candidate(ScanNode Node, string Name);
    private sealed record Preparation(List<Candidate> Eligible, long Excluded);
    private sealed record MatchDetails(string Preview, long LineNumber);
    private readonly record struct SearchOutcome(SearchHit? Hit, bool Skipped)
    {
        public static SearchOutcome NoMatch => new(null, false);
        public static SearchOutcome NotSearchable => new(null, true);
    }
}
