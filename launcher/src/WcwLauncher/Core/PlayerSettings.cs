using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;

namespace WcwLauncher.Core;

/// <summary>
/// The "Internal resolution" values, encoded as runtime/include/internal_resolution.h does:
/// 1 = native, -1 = match the display, N >= 2 = a target height in lines.
/// </summary>
public static class InternalResolution
{
    public const int Native = 1;
    public const int Display = -1;
    public const int ReferenceLines = 240;  // game.toml sets no resolution_reference_lines
    public const int MaxLines = 8640;
    /// <summary>settings.toml's supersampling for older runtimes stops at 4 (PSX_IR_LEGACY_SS_MAX).</summary>
    public const int LegacyMaxFactor = 4;

    public static readonly (string Id, int Value)[] Presets =
    [
        ("native", Native), ("720p", 720), ("1080p", 1080), ("1440p", 1440),
        ("4k", 2160), ("5k", 2880), ("8k", 4320), ("display", Display),
    ];

    /// <summary>A persisted value: a preset id (any case), or a line count (string or integer).</summary>
    public static int? Parse(object? value)
    {
        switch (value)
        {
            case string s:
                foreach (var (id, v) in Presets)
                {
                    if (string.Equals(s, id, StringComparison.OrdinalIgnoreCase)) return v;
                }
                return s.Length > 0 && s.Length <= 5 && int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out int n) &&
                       n >= 2 && n <= MaxLines ? n : null;
            case long l:
                return l >= 2 && l <= MaxLines ? (int)l : null;
            default:
                return null;
        }
    }

    public static string? IdFor(int value)
    {
        foreach (var (id, v) in Presets)
        {
            if (v == value) return id;
        }
        return null;
    }

    /// <summary>The integer scale a value asks the renderer for: ceil(target / 240).</summary>
    public static int Scale(int value, int displayHeight)
    {
        int target = value == Display ? displayHeight : value >= 2 ? value : 0;
        if (target <= ReferenceLines) return 1;
        return (target + ReferenceLines - 1) / ReferenceLines;
    }

    /// <summary>A legacy supersampling factor as a value (3 -> 720p, 6 -> 1440p, else N x 240 lines).</summary>
    public static int FromSupersampling(long n)
    {
        if (n <= 1) return Native;
        foreach (var (_, v) in Presets)
        {
            if (v >= 2 && Scale(v, 0) == n) return v;
        }
        return (int)Math.Min(MaxLines, ReferenceLines * n);
    }

    public static string Label(int value) => value switch
    {
        Native => "Native",
        Display => "Match display",
        2160 => "4K",
        2880 => "5K",
        4320 => "8K",
        720 or 1080 or 1440 => $"{value}p",
        _ => $"{value} lines",
    };

    /// <summary>The primary display's height in pixels (the launcher is per-monitor DPI aware).</summary>
    public static int PrimaryDisplayHeight()
    {
        try
        {
            int h = GetSystemMetrics(1);  // SM_CYSCREEN
            return h > 0 ? h : 1080;
        }
        catch (Exception)
        {
            return 1080;
        }
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
}

/// <summary>
/// The player settings the launcher shows, read from settings.toml next to the game exe (the
/// player's layer) over the game's bundled game.toml (its defaults), the same precedence the
/// runtime uses. Setting a property writes settings.toml only when the value really changes, so
/// keys the player never touched keep following game.toml.
/// </summary>
public sealed class PlayerSettings
{
    public const string FileName = "settings.toml";

    private const string NewFileHeader =
        "# WCW Mayhem Recompiled player settings, read by WCWMayhem_Recompiled.exe at startup.\n" +
        "# The launcher only changes the keys it shows; everything else here is left as it is.\n" +
        "# Overrides the bundled game.toml; the command line overrides this file.\n";

    private readonly TomlLines _file;
    private readonly TomlTable _game;

    private PlayerSettings(TomlLines file, TomlTable game)
    {
        _file = file;
        _game = game;
    }

    public static PlayerSettings Load(string gameExeDirectory, TomlTable gameToml) =>
        new(TomlLines.Load(Path.Combine(gameExeDirectory, FileName), NewFileHeader), gameToml);

    public string FilePath => _file.Path;
    public bool FileExists => _file.Exists;
    /// <summary>settings.toml exists but isn't valid TOML: the game ignores all of it (and sets it
    /// aside as settings.toml.bad).</summary>
    public string? SyntaxError => _file.SyntaxError;
    public bool HasChanges => _file.Changed;

    public void Save() => _file.Save();

    private TomlTable? GameVideo => _game.Table("video");

    /// <summary>game.toml [video] offer_vulkan: only then may the player pick Vulkan.</summary>
    public bool VulkanOffered => GameVideo?.Bool("offer_vulkan") == true;

    // --- [video] internal_resolution (+ supersampling) ------------------------------------------

    public int InternalResolutionValue
    {
        get
        {
            if (InternalResolution.Parse(_file.Get("video", "internal_resolution")) is { } own) return own;
            if (_file.GetInt("video", "supersampling") is { } ss and >= 1 and <= 32) return InternalResolution.FromSupersampling(ss);
            if (InternalResolution.Parse(GameVideo?.GetValueOrDefault("internal_resolution")) is { } game) return game;
            if (GameVideo?.Int("supersampling") is { } gss and >= 1) return InternalResolution.FromSupersampling(gss);
            return InternalResolution.Native;
        }
        set
        {
            if (value == InternalResolutionValue) return;  // unchanged (possibly still game.toml's default)
            string? id = InternalResolution.IdFor(value);
            _file.Set("video", "internal_resolution", id != null ? Toml.Quote(id) : value.ToString(CultureInfo.InvariantCulture));
            // The docs' pairing: supersampling = min(S, 4) beside the preset, for older runtimes.
            int scale = InternalResolution.Scale(value, InternalResolution.PrimaryDisplayHeight());
            _file.Set("video", "supersampling", Math.Min(scale, InternalResolution.LegacyMaxFactor).ToString(CultureInfo.InvariantCulture));
        }
    }

    // --- [video] renderer ----------------------------------------------------------------------

    /// <summary>"opengl", "software" or "vulkan" (Vulkan only when the game offers it).</summary>
    public string Renderer
    {
        get
        {
            string? r = Valid(_file.GetString("video", "renderer"), "opengl", "software", "vulkan")
                        ?? Valid(GameVideo?.String("renderer"), "opengl", "software", "vulkan") ?? "opengl";
            return r == "vulkan" && !VulkanOffered ? "opengl" : r;
        }
        set => SetIfChanged(Renderer, value, "video", "renderer", Toml.Quote(value));
    }

    // --- [video] fullscreen: 0 windowed, 1 borderless, 2 exclusive -------------------------------

    public int Fullscreen
    {
        get
        {
            object? v = _file.Get("video", "fullscreen");
            if (v is long n) return n is >= 0 and <= 2 ? (int)n : 0;
            if (v is bool b) return b ? 1 : 0;
            return 0;
        }
        set => SetIfChanged(Fullscreen, value, "video", "fullscreen", value.ToString(CultureInfo.InvariantCulture));
    }

    // --- [video] vsync: 1 "on", 0 "immediate", -1 "adaptive" -------------------------------------

    public int VSync
    {
        get => ParseVsync(_file.GetString("video", "vsync")) ?? ParseVsync(GameVideo?.String("vsync")) ?? 1;
        set => SetIfChanged(VSync, value, "video", "vsync",
            Toml.Quote(value == 0 ? "immediate" : value < 0 ? "adaptive" : "on"));
    }

    private static int? ParseVsync(string? s) => s switch
    {
        "on" or "vsync" => 1,
        "off" or "immediate" => 0,
        "adaptive" => -1,
        _ => null,
    };

    // --- [video] texture_filtering: "nearest" | "bilinear" ---------------------------------------

    public bool BilinearTextures
    {
        get => (Valid(_file.GetString("video", "texture_filtering"), "nearest", "bilinear")
                ?? Valid(GameVideo?.String("texture_filtering"), "nearest", "bilinear")) == "bilinear";
        set => SetIfChanged(BilinearTextures, value, "video", "texture_filtering", Toml.Quote(value ? "bilinear" : "nearest"));
    }

    // --- [video] antialiasing (smooth scaling of the picture to the window) ---------------------

    public bool Antialiasing
    {
        get => _file.GetBool("video", "antialiasing") ?? GameVideo?.Bool("antialiasing") ?? true;
        set => SetIfChanged(Antialiasing, value, "video", "antialiasing", Bool(value));
    }

    // --- [video] crt_filter: "raw" | "crt" | "composite" | "trinitron" ---------------------------

    public static readonly string[] CrtFilters = ["raw", "crt", "composite", "trinitron"];

    public string CrtFilter
    {
        get => Valid(_file.GetString("video", "crt_filter"), CrtFilters)
               ?? Valid(GameVideo?.String("crt_filter"), CrtFilters) ?? "raw";
        set => SetIfChanged(CrtFilter, value, "video", "crt_filter", Toml.Quote(value));
    }

    // --- [video] scanlines + scanline_strength (0..1) --------------------------------------------

    public bool Scanlines
    {
        get => _file.GetBool("video", "scanlines") ?? GameVideo?.Bool("scanlines") ?? false;
        set => SetIfChanged(Scanlines, value, "video", "scanlines", Bool(value));
    }

    public double ScanlineStrength
    {
        get => Math.Clamp(_file.GetDouble("video", "scanline_strength") ?? GameVideo?.Double("scanline_strength") ?? 0.5, 0, 1);
        set
        {
            if (Math.Abs(ScanlineStrength - value) < 0.001) return;
            // Always with a decimal point: the game reads it as a float and rejects a bare integer.
            _file.Set("video", "scanline_strength", Math.Clamp(value, 0, 1).ToString("0.0##", CultureInfo.InvariantCulture));
        }
    }

    // --- [video] window_width (640..7680; height follows the aspect). Null = fit the display. ---

    public int? WindowWidth
    {
        get
        {
            long? w = _file.GetInt("video", "window_width") ?? GameVideo?.Int("window_width");
            return w is >= 640 and <= 7680 ? (int)w : null;
        }
        set
        {
            if (WindowWidth == value) return;
            if (value == null) _file.Remove("video", "window_width");
            else _file.Set("video", "window_width", value.Value.ToString(CultureInfo.InvariantCulture));
        }
    }

    // --- [video] fast_boot (game.toml: [runtime] fast_boot) --------------------------------------

    public bool FastBoot
    {
        get => _file.GetBool("video", "fast_boot") ?? _game.Table("runtime")?.Bool("fast_boot") ?? false;
        set => SetIfChanged(FastBoot, value, "video", "fast_boot", Bool(value));
    }

    // --- [audio] spu_hq, frequency ---------------------------------------------------------------

    public bool HighQualityAudio
    {
        get => _file.GetBool("audio", "spu_hq") ?? _game.Table("audio")?.Bool("spu_hq") ?? false;
        set => SetIfChanged(HighQualityAudio, value, "audio", "spu_hq", Bool(value));
    }

    public static readonly int[] AudioFrequencies = [44100, 48000, 32000, 32040];

    public int AudioFrequency
    {
        get => _file.GetInt("audio", "frequency") is { } f && Array.IndexOf(AudioFrequencies, (int)f) >= 0 ? (int)f : 44100;
        set => SetIfChanged(AudioFrequency, value, "audio", "frequency", value.ToString(CultureInfo.InvariantCulture));
    }

    // --- [controller]: pad type, deadzone, and the devices written at launch ---------------------

    private TomlTable? GameController => _game.Table("controller");

    /// <summary>
    /// A player's pad type, "digital" or "analog" (DualShock): settings.toml pN_mode (the game's
    /// pad_mode_from_settings_string: "hybrid" counts as analog), else the legacy pN_analog
    /// bool, else game.toml (p1_mode / p2_mode / default_mode, with players 3+ following P1),
    /// else the runtime's default, analog.
    /// </summary>
    public string PadMode(int player)
    {
        string? own = _file.GetString("controller", $"p{player}_mode")?.ToLowerInvariant();
        if (own is "digital") return "digital";
        if (own is "analog" or "hybrid") return "analog";
        if (_file.GetBool("controller", $"p{player}_analog") is { } analog) return analog ? "analog" : "digital";
        var g = GameController;
        if (g == null) return "analog";
        int gamePlayer = player == 2 ? 2 : 1;
        string? mode = g.String($"p{gamePlayer}_mode")?.ToLowerInvariant() ?? g.String("default_mode")?.ToLowerInvariant();
        if (mode is "digital" or "analog") return mode;
        bool? legacy = g.Bool($"p{gamePlayer}_analog") ?? g.Bool("default_analog");
        return legacy is false ? "digital" : "analog";
    }

    public void SetPadMode(int player, string mode)
    {
        if (PadMode(player) != mode) _file.Set("controller", $"p{player}_mode", Toml.Quote(mode));
    }

    public const int DefaultDeadzone = 3277;  // main.cpp kDefaultDeadzoneRaw, ~10%

    /// <summary>pN_deadzone (0..32767), else the legacy shared deadzone, else 3277.</summary>
    public int Deadzone(int player)
    {
        long? own = _file.GetInt("controller", $"p{player}_deadzone") ?? _file.GetInt("controller", "deadzone");
        return own is >= 0 and <= 32767 ? (int)own : DefaultDeadzone;
    }

    public void SetDeadzone(int player, int value)
    {
        if (Deadzone(player) != value)
            _file.Set("controller", $"p{player}_deadzone", Math.Clamp(value, 0, 32767).ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>The device a player gets now: settings.toml pN_device, else game.toml's (P1/P2
    /// only), else the release runtime's default (P1 keyboard, the rest none).</summary>
    public string Device(int player)
    {
        string? own = _file.GetString("controller", $"p{player}_device");
        if (!string.IsNullOrWhiteSpace(own)) return own;
        string? game = player <= 2 ? GameController?.String($"p{player}_device") : null;
        if (!string.IsNullOrWhiteSpace(game)) return game;
        return player == 1 ? "keyboard" : "none";
    }

    /// <summary>[controller] multitap (the runtime's default is true).</summary>
    public bool Multitap => _file.GetBool("controller", "multitap") ?? true;

    /// <summary>Written right before launch: the resolved p1_device..p4_device and multitap.</summary>
    public void WriteControllerAssignment(IReadOnlyList<string> devices, bool multitap)
    {
        for (int i = 0; i < devices.Count; i++) _file.Set("controller", $"p{i + 1}_device", Toml.Quote(devices[i]));
        _file.Set("controller", "multitap", Bool(multitap));
    }

    // --- helpers -------------------------------------------------------------------------------

    /// <summary>Raw settings.toml text of a key, for the self-test report.</summary>
    public string? Raw(string section, string key) => _file.GetRaw(section, key);

    private void SetIfChanged<T>(T current, T value, string section, string key, string toml)
    {
        if (Equals(current, value)) return;
        _file.Set(section, key, toml);
    }

    private static string Bool(bool b) => b ? "true" : "false";

    private static string? Valid(string? value, params string[] allowed) =>
        value != null && Array.IndexOf(allowed, value) >= 0 ? value : null;
}
