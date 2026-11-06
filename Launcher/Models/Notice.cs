namespace ILauncher.Models;

public record Notice(string Id, string Revision, string Title, string Body, DateTimeOffset PublishedAt, bool Important);
