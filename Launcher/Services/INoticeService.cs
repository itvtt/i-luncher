using ILauncher.Models;

namespace ILauncher.Services;

public interface INoticeService
{
    IReadOnlyList<Notice> CachedNotices { get; }
    Task<IReadOnlyList<Notice>> RefreshAsync(CancellationToken cancellationToken = default);
    bool IsRead(Notice notice);
    void MarkRead(Notice notice);
}
