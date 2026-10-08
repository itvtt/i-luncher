using System.IO;
using System.Text.Json;
using TerminalNotes.Services;

namespace TerminalNotes;

public sealed partial class TerminalPane
{
    private static string BuildOpenCodeAttachCommand(string executable, Uri endpoint, string directory, string sessionId) =>
        "& " + QuotePowerShellLiteral(executable) + " attach " + QuotePowerShellLiteral(endpoint.GetLeftPart(UriPartial.Authority)) +
        " --dir " + QuotePowerShellLiteral(Path.GetFullPath(directory)) + " --session " + QuotePowerShellLiteral(sessionId);

    private static string QuotePowerShellLiteral(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    private void ObserveOpenCodeTuiManualSubmission(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.TrimStart().StartsWith('/'))
        {
            _openCodeTuiManualText = null;
            return;
        }

        _openCodeTuiManualText = text;
        _openCodeTuiManualSubmittedAt = DateTimeOffset.UtcNow;
        _openCodeTuiManualGeneration = _openCodeStatus?.SubmissionGeneration ?? 0;
    }

    private void HandleOpenCodeTuiExit()
    {
        if (_disposed || _failed) return;
        _ready = false;
        _failed = true;
        FailPendingTextSends(new InvalidOperationException("The OpenCode TUI process exited."));
        ReportLifecycleStage("opencode_tui_exited");
        StatusChanged?.Invoke("OpenCode TUI가 종료되었습니다. 서버는 이 터미널을 닫을 때까지 유지됩니다.");
        ApplyActivityChange();
    }

    private void OnOpenCodeTuiRecoverySignal(OpenCodeRecoverySignal signal)
    {
        if (!_usesNativeOpenCodeTui || _disposed || _failed || signal.Type != "message.updated" ||
            string.IsNullOrWhiteSpace(signal.MessageId) || signal.MessageCreatedAt is not { } created ||
            _openCodeTuiManualText is not { } expectedText || _headlessOpenCodeClient is not { } client ||
            created.ToUnixTimeMilliseconds() < _openCodeTuiManualSubmittedAt.ToUnixTimeMilliseconds())
            return;

        string messageId = signal.MessageId;
        long generation = _openCodeTuiManualGeneration;
        string sessionId = signal.SessionId;
        _ = VerifyOpenCodeTuiManualReceiptAsync(client, sessionId, messageId, expectedText, generation, created);
    }

    private async Task VerifyOpenCodeTuiManualReceiptAsync(OpenCodeConversationClient client, string sessionId,
        string messageId, string expectedText, long generation, DateTimeOffset messageCreatedAt)
    {
        try
        {
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(HeadlessLifetimeCancellation);
            JsonElement session = await client.GetSessionAsync(sessionId, lifetime.Token);
            if (!IsRootSessionInProject(session, sessionId)) return;
            bool receiptFound = false;
            foreach (int delay in new[] { 0, 125, 300, 600 })
            {
                if (delay > 0) await Task.Delay(delay, lifetime.Token);
                JsonElement messages = await client.ReadMessagesAsync(sessionId, lifetime.Token);
                if (ContainsExactUserMessage(messages, sessionId, messageId, expectedText, messageCreatedAt))
                {
                    receiptFound = true;
                    break;
                }
            }
            if (!receiptFound) return;
            await Dispatcher.InvokeAsync(() =>
            {
                if (_disposed || _failed || !_usesNativeOpenCodeTui ||
                    _openCodeTuiManualGeneration != generation || _openCodeTuiManualText != expectedText || _openCodeStatus is null)
                    return;
                BindNativeAutomationSession(sessionId);
                _openCodeStatus.ConfirmApiSubmission(sessionId, messageId, generation);
                _autoResume?.ObserveSubmittedPrompt(expectedText, sessionId, messageId);
                _autoResume?.ConfirmApiSubmission(sessionId, messageId);
                _openCodeTuiManualText = null;
                ClearWorkFailed();
            });
        }
        catch (OperationCanceledException) when (HeadlessLifetimeCancellation.IsCancellationRequested || _disposed) { }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.WriteLine("OpenCode TUI message receipt could not be verified: " + exception.GetType().Name);
        }
    }

    private bool IsRootSessionInProject(JsonElement session, string sessionId) =>
        session.ValueKind == JsonValueKind.Object &&
        session.TryGetProperty("id", out JsonElement id) && id.ValueKind == JsonValueKind.String && id.GetString() == sessionId &&
        (!session.TryGetProperty("parentID", out JsonElement parent) || parent.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) &&
        session.TryGetProperty("directory", out JsonElement directory) && directory.ValueKind == JsonValueKind.String &&
        AreOpenCodeDirectoriesEqual(directory.GetString(), WorkingDirectory);

    private static bool AreOpenCodeDirectoriesEqual(string? left, string right)
    {
        if (string.IsNullOrWhiteSpace(left)) return false;
        try
        {
            string canonicalLeft = Path.TrimEndingDirectorySeparator(Path.GetFullPath(left));
            string canonicalRight = Path.TrimEndingDirectorySeparator(Path.GetFullPath(right));
            return string.Equals(canonicalLeft, canonicalRight,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool ContainsExactUserMessage(JsonElement messages, string sessionId, string messageId,
        string expectedText, DateTimeOffset signalCreatedAt)
    {
        if (messages.ValueKind != JsonValueKind.Array) return false;
        foreach (JsonElement message in messages.EnumerateArray())
        {
            JsonElement info = message.TryGetProperty("info", out JsonElement nested) && nested.ValueKind == JsonValueKind.Object
                ? nested : message;
            if (!info.TryGetProperty("id", out JsonElement id) || id.ValueKind != JsonValueKind.String ||
                id.GetString() != messageId || !info.TryGetProperty("role", out JsonElement role) ||
                role.ValueKind != JsonValueKind.String || role.GetString() != "user" ||
                !info.TryGetProperty("sessionID", out JsonElement messageSession) || messageSession.ValueKind != JsonValueKind.String ||
                messageSession.GetString() != sessionId || !info.TryGetProperty("time", out JsonElement time) ||
                time.ValueKind != JsonValueKind.Object || !time.TryGetProperty("created", out JsonElement created) ||
                created.ValueKind != JsonValueKind.Number || !created.TryGetInt64(out long milliseconds))
                continue;

            DateTimeOffset messageTime;
            try { messageTime = DateTimeOffset.FromUnixTimeMilliseconds(milliseconds); }
            catch (ArgumentOutOfRangeException) { return false; }
            if (messageTime != signalCreatedAt) return false;

            JsonElement parts = message.TryGetProperty("parts", out JsonElement messageParts) ? messageParts : default;
            if (parts.ValueKind != JsonValueKind.Array) return false;
            var text = new System.Text.StringBuilder();
            foreach (JsonElement part in parts.EnumerateArray())
                if (part.ValueKind == JsonValueKind.Object && part.TryGetProperty("type", out JsonElement type) &&
                    type.ValueKind == JsonValueKind.String && type.GetString() == "text" &&
                    part.TryGetProperty("text", out JsonElement value) && value.ValueKind == JsonValueKind.String)
                    text.Append(value.GetString());
            return string.Equals(text.ToString(), expectedText, StringComparison.Ordinal);
        }
        return false;
    }
}
