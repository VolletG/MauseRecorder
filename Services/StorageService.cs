using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using MauseRecorder.Models;

namespace MauseRecorder.Services;

public sealed class StorageService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public string RootDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MauseRecorder");

    public string RecordingsDirectory => Path.Combine(RootDirectory, "recordings");
    private string SettingsPath => Path.Combine(RootDirectory, "settings.json");

    public StorageService()
    {
        Directory.CreateDirectory(RootDirectory);
        Directory.CreateDirectory(RecordingsDirectory);
    }

    public AppSettings LoadSettings()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return new AppSettings();
            var json = File.ReadAllText(SettingsPath);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
            settings.Normalize();
            return settings;
        }
        catch
        {
            return new AppSettings();
        }
    }

    public void SaveSettings(AppSettings settings)
    {
        try
        {
            settings.Normalize();
            WriteAtomic(SettingsPath, JsonSerializer.Serialize(settings, JsonOptions));
        }
        catch
        {
            // Settings are a convenience; never crash the app over them.
        }
    }

    public Recording SaveRecording(Recording recording, string? path = null)
    {
        path ??= Path.Combine(RecordingsDirectory,
            $"{recording.CreatedAt:yyyyMMdd-HHmmss}-{recording.Id[..Math.Min(6, recording.Id.Length)]}.mrec");

        WriteAtomic(path, JsonSerializer.Serialize(recording, JsonOptions));
        recording.FilePath = path;
        return recording;
    }

    public IReadOnlyList<Recording> LoadRecordings()
    {
        var recordings = new List<Recording>();

        foreach (var file in Directory.EnumerateFiles(RecordingsDirectory, "*.mrec"))
        {
            try
            {
                var recording = JsonSerializer.Deserialize<Recording>(File.ReadAllText(file), JsonOptions);
                if (recording is null || recording.Events.Count == 0) continue;
                recording.FilePath = file;
                if (recording.ScreenWidth <= 0) recording.ScreenWidth = 1920;
                if (recording.ScreenHeight <= 0) recording.ScreenHeight = 1080;
                if (string.IsNullOrWhiteSpace(recording.Name)) recording.Name = "Untitled take";
                recordings.Add(recording);
            }
            catch
            {
                // A single broken file should not hide the rest of the archive.
            }
        }

        return recordings.OrderByDescending(r => r.CreatedAt).ToList();
    }

    public void DeleteRecording(Recording recording)
    {
        try
        {
            if (recording.FilePath is not null && File.Exists(recording.FilePath))
                File.Delete(recording.FilePath);
        }
        catch
        {
            // Ignore filesystem races.
        }
    }

    public void ClearRecordings()
    {
        foreach (var file in Directory.EnumerateFiles(RecordingsDirectory, "*.mrec"))
        {
            try { File.Delete(file); } catch { /* keep going */ }
        }
    }

    public void OpenDataFolder()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = RootDirectory,
                UseShellExecute = true
            });
        }
        catch
        {
            // Non-critical.
        }
    }

    public string NextTakeName(int savedCount)
    {
        return $"Take {savedCount + 1:00} · {DateTime.Now:MMM d, HH:mm}";
    }

    private static void WriteAtomic(string path, string contents)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, contents);
        File.Move(temp, path, true);
    }
}
