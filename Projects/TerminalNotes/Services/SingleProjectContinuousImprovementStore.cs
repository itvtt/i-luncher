using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace TerminalNotes.Services;

public sealed record SingleProjectImprovementPreference(bool Enabled, DateTimeOffset? Until);

/// <summary>Persists continuous-improvement preferences for single-terminal projects.</summary>
public static class SingleProjectContinuousImprovementStore
{
    private static readonly object Gate = new();
    private static readonly string StorePath = Path.Combine(
        OpenCodeProjectContext.DataDirectory, "single-project-improvement.json");
    private static readonly string MutexName = "Local\\TerminalNotesImprovement_" +
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(StorePath.ToUpperInvariant()))[..12]);

    public static SingleProjectImprovementPreference? Read(string project)
    {
        string key = Canonicalize(project);
        lock (Gate)
        {
            using var mutex = new Mutex(false, MutexName);
            try { mutex.WaitOne(); }
            catch (AbandonedMutexException) { }
            try
            {
                Dictionary<string, SingleProjectImprovementPreference> records = ReadAll();
                return records.TryGetValue(key, out var preference) ? preference : null;
            }
            finally { mutex.ReleaseMutex(); }
        }
    }

    public static void Write(string project, bool enabled, DateTimeOffset? until)
    {
        string key = Canonicalize(project);
        lock (Gate)
        {
            using var mutex = new Mutex(false, MutexName);
            try { mutex.WaitOne(); }
            catch (AbandonedMutexException) { }
            string temporaryPath = StorePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                Dictionary<string, SingleProjectImprovementPreference> records = ReadAll();
                records[key] = new(enabled, until);
                Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
                File.WriteAllText(temporaryPath, JsonSerializer.Serialize(records));
                File.Move(temporaryPath, StorePath, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
                mutex.ReleaseMutex();
            }
        }
    }

    private static Dictionary<string, SingleProjectImprovementPreference> ReadAll()
    {
        if (!File.Exists(StorePath)) return new(StringComparer.OrdinalIgnoreCase);
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, SingleProjectImprovementPreference>>(
                File.ReadAllText(StorePath)) is { } records
                ? new(records, StringComparer.OrdinalIgnoreCase)
                : throw new InvalidDataException("The single-project improvement settings are empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The single-project improvement settings are malformed and were preserved.", ex);
        }
    }

    private static string Canonicalize(string project) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(project));
}
