namespace ILauncher.Models;

public record Notice(string Id, int Revision, string Title, string Body, DateTimeOffset PublishedAt, bool Important);
