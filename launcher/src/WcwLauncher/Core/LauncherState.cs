using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WcwLauncher.Core;

/// <summary>A disc image the launcher already hashed: the data-track file, its size and time.</summary>
public sealed class DiscCheck
{
    public string Path { get; set; } = "";
    public long Size { get; set; }
    public long ModifiedUtcTicks { get; set; }
    public string Md5 { get; set; } = "";
}

/// <summary>
/// The launcher's own memory (launcher.json next to the launcher exe): the disc image, where the
/// game is, when it was last played and the disc hashes already done, so 700 MB isn't read
/// again until the file changes. Game settings live in the game's own files, never here.
/// </summary>
public sealed class LauncherState
{
    /// <summary>The disc image the player chose (a .cue, or a .bin / .img / .iso / .chd).</summary>
    public string? DiscPath { get; set; }
    public string? GameExe { get; set; }
    public DateTime? LastPlayed { get; set; }
    /// <summary>Per player P1-P4: "auto", "gamepad", "keyboard" or "none". Automatic is a
    /// launcher idea, resolved into settings.toml pN_device each time PLAY is pressed.</summary>
    public List<string> Controllers { get; set; } = [DeviceChoice.Automatic, DeviceChoice.Automatic, DeviceChoice.Automatic, DeviceChoice.Automatic];
    public List<DiscCheck> DiscChecks { get; set; } = [];

    public static string FilePath => System.IO.Path.Combine(AppContext.BaseDirectory, "launcher.json");

    public static LauncherState Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                return JsonSerializer.Deserialize(File.ReadAllText(FilePath), LauncherJson.Default.LauncherState)
                       ?? new LauncherState();
            }
        }
        catch (Exception)
        {
            // A damaged file just means starting fresh.
        }
        return new LauncherState();
    }

    public void Save()
    {
        try
        {
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, LauncherJson.Default.LauncherState));
        }
        catch (Exception)
        {
            // Read-only install folder: keep working without remembering.
        }
    }

    /// <summary>The MD5 recorded for this exact file (same path, size and modified time), or null.</summary>
    public string? CachedMd5(string file, long size, DateTime modifiedUtc)
    {
        foreach (DiscCheck c in DiscChecks)
        {
            if (string.Equals(c.Path, file, StringComparison.OrdinalIgnoreCase) && c.Size == size &&
                c.ModifiedUtcTicks == modifiedUtc.Ticks) return c.Md5;
        }
        return null;
    }

    public void RememberMd5(string file, long size, DateTime modifiedUtc, string md5)
    {
        DiscChecks.RemoveAll(c => string.Equals(c.Path, file, StringComparison.OrdinalIgnoreCase));
        DiscChecks.Insert(0, new DiscCheck { Path = file, Size = size, ModifiedUtcTicks = modifiedUtc.Ticks, Md5 = md5 });
        if (DiscChecks.Count > 6) DiscChecks.RemoveRange(6, DiscChecks.Count - 6);
    }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(LauncherState))]
internal partial class LauncherJson : JsonSerializerContext;
