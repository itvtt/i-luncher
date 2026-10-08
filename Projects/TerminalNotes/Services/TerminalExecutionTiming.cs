namespace TerminalNotes.Services;

/// <summary>Bounded phase budgets for terminal startup and native execution evidence.</summary>
public static class TerminalExecutionTiming
{
    public static readonly TimeSpan StartupReadyTimeout = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan NativeSubmissionConfirmationTimeout = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan MetadataRequestTimeout = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan NativeStatusHandshakeTimeout = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan InstallationProbeTimeout = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan SlowStartupNoticeAfter = TimeSpan.FromSeconds(20);

    internal static async Task<bool> WaitForNativeSubmissionConfirmationAsync(
        Task confirmation,
        Func<bool> isStillOwned,
        TimeProvider? timeProvider = null,
        TimeSpan? timeoutOverride = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(confirmation);
        ArgumentNullException.ThrowIfNull(isStillOwned);
        TimeProvider clock = timeProvider ?? TimeProvider.System;
        TimeSpan timeout = timeoutOverride ?? NativeSubmissionConfirmationTimeout;
        DateTimeOffset deadline = clock.GetUtcNow() + timeout;

        while (clock.GetUtcNow() < deadline)
        {
            if (cancellationToken.IsCancellationRequested || !isStillOwned()) return false;
            if (confirmation.IsCompleted) return !cancellationToken.IsCancellationRequested && isStillOwned();
            TimeSpan remaining = deadline - clock.GetUtcNow();
            if (remaining <= TimeSpan.Zero) break;
            TimeSpan interval = remaining < TimeSpan.FromMilliseconds(200) ? remaining : TimeSpan.FromMilliseconds(200);
            Task delay = Task.Delay(interval, clock, cancellationToken);
            await Task.WhenAny(confirmation, delay).ConfigureAwait(false);
        }

        return confirmation.IsCompleted && isStillOwned() && !cancellationToken.IsCancellationRequested;
    }
}