using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TerminalNotes.Services;

/// <summary>Tracks only explicit token counters emitted by this terminal session.</summary>
public sealed partial class TerminalTokenUsage
{
    private readonly object _gate = new();
    private readonly StringBuilder _lineBuffer = new();
    private readonly HashSet<string> _completedEvents = new(StringComparer.Ordinal);
    private long _completedInput;
    private long _completedOutput;
    private long _completedTotal;
    private long? _inputTokens;
    private long? _outputTokens;
    private long? _totalTokens;
    private bool _hasCumulativeCounter;
    private string? _scopeId;

    public event Action? Changed;

    public long? InputTokens { get { lock (_gate) return _inputTokens; } }
    public long? OutputTokens { get { lock (_gate) return _outputTokens; } }
    public long? TotalTokens { get { lock (_gate) return _totalTokens; } }
    public string Label { get { lock (_gate) return FormatLabel(_totalTokens); } }

    public void Observe(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0) return;

        bool changed = false;
        lock (_gate)
        {
            string plain = StripAnsiRegex().Replace(text, "");
            foreach (Match match in ExplicitTotalRegex().Matches(plain))
            {
                if (TryNumber(match.Groups[1].Value, out long total))
                    changed |= SetCounters(null, null, total, cumulative: true);
            }

            _lineBuffer.Append(plain);
            while (TryTakeLine(out string? line)) changed |= ObserveLine(line);

            // A machine event may arrive without a trailing newline. Parse it once it
            // is a complete JSON value, while retaining partial values for later data.
            string pending = _lineBuffer.ToString().Trim();
            if (pending.Length > 1 && pending[0] == '{' && pending[^1] == '}' && TryParseJson(pending, out JsonDocument? document))
            {
                using (document) changed |= ObserveJson(document!.RootElement, pending);
                _lineBuffer.Clear();
            }

            if (_lineBuffer.Length > 1_000_000) _lineBuffer.Remove(0, _lineBuffer.Length - 64_000);
        }

        if (changed) Changed?.Invoke();
    }

    private bool ObserveLine(string line)
    {
        string value = line.Trim();
        bool changed = false;
        foreach (Match match in ExplicitTotalRegex().Matches(value))
        {
            if (TryNumber(match.Groups[1].Value, out long total))
                changed |= SetCounters(null, null, total, cumulative: true);
        }
        if (value.Length < 2 || value[0] != '{' || value[^1] != '}' || !TryParseJson(value, out JsonDocument? document)) return changed;
        using (document) return ObserveJson(document!.RootElement, value) || changed;
    }

    private bool ObserveJson(JsonElement root, string fingerprint)
    {
        string? type = FindString(root, "type");
        if (string.Equals(type, "thread.started", StringComparison.OrdinalIgnoreCase))
        {
            string scope = FindStringRecursive(root, "thread_id", "threadId") ?? fingerprint;
            if (!string.Equals(scope, _scopeId, StringComparison.Ordinal))
            {
                _scopeId = scope;
                _completedEvents.Clear();
            }
            return false;
        }
        if (string.Equals(type, "turn.completed", StringComparison.OrdinalIgnoreCase))
        {
            if (!_completedEvents.Add(fingerprint)) return false;
            JsonElement usage = FindObject(root, "usage");
            long? input = FindNumber(usage, "input_tokens", "inputTokens");
            long? output = FindNumber(usage, "output_tokens", "outputTokens");
            long? total = FindNumber(usage, "total_tokens", "totalTokens");
            if (input is null && output is null && total is null) return false;

            _completedInput += input ?? 0;
            _completedOutput += output ?? (total is { } t ? Math.Max(0, t - (input ?? 0)) : 0);
            _completedTotal += total ?? (input ?? 0) + (output ?? 0);
            if (_hasCumulativeCounter) return false;
            return SetCounters(_completedInput, _completedOutput, _completedTotal, cumulative: false);
        }

        if (string.Equals(type, "step_finish", StringComparison.OrdinalIgnoreCase))
        {
            string? session = FindStringRecursive(root, "sessionID", "session_id");
            if (session is not null && !string.Equals(session, _scopeId, StringComparison.Ordinal))
            {
                _scopeId = session;
                _completedEvents.Clear();
            }
            JsonElement part = FindObject(root, "part");
            JsonElement usage = FindObject(part, "tokens");
            if (usage.ValueKind != JsonValueKind.Object) return false;
            string stepFingerprint = (session ?? "") + ":" + (FindStringRecursive(part, "id") ?? FingerprintFor(root));
            if (!_completedEvents.Add(stepFingerprint)) return false;

            long input = FindNumber(usage, "input") ?? 0;
            long output = FindNumber(usage, "output") ?? 0;
            long reasoning = FindNumber(usage, "reasoning") ?? 0;
            JsonElement cache = FindObject(usage, "cache");
            long cacheRead = FindNumber(cache, "read") ?? 0;
            long cacheWrite = FindNumber(cache, "write") ?? 0;
            long total = FindNumber(usage, "total") ?? input + output + reasoning + cacheRead + cacheWrite;
            _completedInput += input + cacheRead + cacheWrite;
            _completedOutput += output + reasoning;
            _completedTotal += total;
            if (_hasCumulativeCounter) return false;
            return SetCounters(_completedInput, _completedOutput, _completedTotal, cumulative: false);
        }

        if (type?.Contains("token_count", StringComparison.OrdinalIgnoreCase) == true ||
            type?.Contains("token.count", StringComparison.OrdinalIgnoreCase) == true)
        {
            long? total = FindNumber(root, "total_tokens", "totalTokens");
            long? input = FindNumber(root, "input_tokens", "inputTokens");
            long? output = FindNumber(root, "output_tokens", "outputTokens");
            if (total is null && (input is not null || output is not null)) total = (input ?? 0) + (output ?? 0);
            return total is not null && SetCounters(input, output, total, cumulative: true);
        }

        return false;
    }

    private bool SetCounters(long? input, long? output, long? total, bool cumulative)
    {
        if (total is null || total < 0) return false;
        bool changed = _inputTokens != input || _outputTokens != output || _totalTokens != total;
        _inputTokens = input;
        _outputTokens = output;
        _totalTokens = total;
        _hasCumulativeCounter |= cumulative;
        return changed;
    }

    private bool TryTakeLine(out string line)
    {
        for (int i = 0; i < _lineBuffer.Length; i++)
        {
            if (_lineBuffer[i] is not ('\r' or '\n')) continue;
            line = _lineBuffer.ToString(0, i);
            int remove = i + 1;
            if (_lineBuffer[i] == '\r' && remove < _lineBuffer.Length && _lineBuffer[remove] == '\n') remove++;
            _lineBuffer.Remove(0, remove);
            return true;
        }
        line = "";
        return false;
    }

    private static JsonElement FindObject(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind == JsonValueKind.Object) return property.Value;
                JsonElement nested = FindObject(property.Value, name);
                if (nested.ValueKind != JsonValueKind.Undefined) return nested;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in element.EnumerateArray())
            {
                JsonElement nested = FindObject(item, name);
                if (nested.ValueKind != JsonValueKind.Undefined) return nested;
            }
        }
        return default;
    }

    private static long? FindNumber(JsonElement element, params string[] names)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (names.Any(name => property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) &&
                    property.Value.ValueKind == JsonValueKind.Number &&
                    property.Value.TryGetInt64(out long value)) return value;
                long? nested = FindNumber(property.Value, names);
                if (nested is not null) return nested;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in element.EnumerateArray())
            {
                long? nested = FindNumber(item, names);
                if (nested is not null) return nested;
            }
        }
        return null;
    }

    private static string? FindString(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind == JsonValueKind.String) return property.Value.GetString();
        }
        return null;
    }

    private static string? FindStringRecursive(JsonElement element, params string[] names)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (names.Any(name => property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) &&
                    property.Value.ValueKind == JsonValueKind.String) return property.Value.GetString();
                string? nested = FindStringRecursive(property.Value, names);
                if (nested is not null) return nested;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in element.EnumerateArray())
            {
                string? nested = FindStringRecursive(item, names);
                if (nested is not null) return nested;
            }
        }
        return null;
    }

    private static bool TryParseJson(string value, out JsonDocument? document)
    {
        try { document = JsonDocument.Parse(value); return true; }
        catch (JsonException) { document = null; return false; }
    }

    private static string FingerprintFor(JsonElement element) => element.GetRawText();

    private static bool TryNumber(string text, out long value) =>
        long.TryParse(text.Replace(",", "", StringComparison.Ordinal), NumberStyles.None, CultureInfo.InvariantCulture, out value);

    public static string FormatCount(long total)
    {
        string[] units = ["", "K", "M", "B"];
        decimal value = total;
        int unit = 0;
        while (value >= 1000 && unit < units.Length - 1)
        {
            value /= 1000;
            unit++;
        }
        value = decimal.Round(value, 2, MidpointRounding.AwayFromZero);
        // Promote rounded boundaries as well, so 999,999 displays as 1M, not 1000K.
        if (value >= 1000 && unit < units.Length - 1)
        {
            value /= 1000;
            unit++;
        }
        return value.ToString("0.##", CultureInfo.InvariantCulture) + units[unit];
    }

    private static string FormatLabel(long? total) => total is { } value
        ? $"토큰 {FormatCount(value)}"
        : "토큰 정보 없음";

    [GeneratedRegex("(?:total\\s+tokens|tokens\\s+used|token\\s+usage|총\\s*토큰)\\s*[:=]\\s*([0-9][0-9,]*)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ExplicitTotalRegex();

    [GeneratedRegex("\\x1B\\][^\\x07]*(?:\\x07|\\x1B\\\\)|\\x1B\\[[0-?]*[ -/]*[@-~]", RegexOptions.CultureInvariant)]
    private static partial Regex StripAnsiRegex();
}
