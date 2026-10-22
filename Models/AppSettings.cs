namespace MauseRecorder.Models;

public sealed class AppSettings
{
    public double PlaybackSpeed { get; set; } = 1.0;
    public bool LoopPlayback { get; set; }
    public int CountdownSeconds { get; set; } = 3;
    public bool CaptureMotion { get; set; } = true;
    public bool MinimizeOnRecord { get; set; } = true;
    public bool MinimizeOnPlayback { get; set; } = true;
    public bool ShowRecordingHud { get; set; } = true;
    public bool PlayClickSounds { get; set; }
    public bool PartyMode { get; set; } = true;
    public bool GremlinMode { get; set; }
    public bool AlwaysOnTop { get; set; }
    public string Accent { get; set; } = "Violet";
    public string? LastRecordingId { get; set; }

    public AppSettings Clone() => (AppSettings)MemberwiseClone();

    public void Normalize()
    {
        PlaybackSpeed = Math.Clamp(PlaybackSpeed, 0.25, 5);
        CountdownSeconds = CountdownSeconds is 0 or 3 or 5 ? CountdownSeconds : 3;
        Accent = Accent is "Violet" or "Cyan" or "Lime" or "Sunset" ? Accent : "Violet";
    }
}

public sealed class AccentPalette
{
    public required string Name { get; init; }
    public required string Hex { get; init; }
    public required string SoftHex { get; init; }
}

public static class AccentPalettes
{
    public static readonly AccentPalette[] All =
    [
        new() { Name = "Violet", Hex = "#8B5CF6", SoftHex = "#2A1D4F" },
        new() { Name = "Cyan",   Hex = "#22D3EE", SoftHex = "#0F3540" },
        new() { Name = "Lime",   Hex = "#A3E635", SoftHex = "#2B3D12" },
        new() { Name = "Sunset", Hex = "#FB7185", SoftHex = "#431D2A" }
    ];

    public static AccentPalette Get(string name) =>
        All.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) ?? All[0];
}
