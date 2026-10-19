using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace WcwLauncher.Core;

/// <summary>
/// Xbox controllers as XInput sees them (slots 0-3). The game itself reads pads through SDL,
/// whose XInput driver enumerates the same controllers; this count only drives "Automatic".
/// </summary>
public static class XInput
{
    public const int Slots = 4;
    private const uint ErrorSuccess = 0;
    private static int _library;  // 0 = untried, 1 = xinput1_4, 2 = xinput9_1_0, -1 = none

    [StructLayout(LayoutKind.Sequential)]
    private struct Gamepad
    {
        public ushort Buttons;
        public byte LeftTrigger, RightTrigger;
        public short ThumbLX, ThumbLY, ThumbRX, ThumbRY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct State
    {
        public uint PacketNumber;
        public Gamepad Gamepad;
    }

    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
    private static extern uint GetState14(uint index, out State state);

    [DllImport("xinput9_1_0.dll", EntryPoint = "XInputGetState")]
    private static extern uint GetState910(uint index, out State state);

    /// <summary>Which of the four XInput slots have a controller (ERROR_SUCCESS = connected).</summary>
    public static bool[] Connected()
    {
        var result = new bool[Slots];
        for (uint i = 0; i < Slots; i++) result[i] = Query(i);
        return result;
    }

    public static int Count() => Connected().Count(c => c);

    public static string Library => _library switch { 1 => "xinput1_4.dll", 2 => "xinput9_1_0.dll", -1 => "(no XInput)", _ => "(not queried)" };

    private static bool Query(uint index)
    {
        if (_library <= 1 && _library != -1)
        {
            try
            {
                bool ok = GetState14(index, out _) == ErrorSuccess;
                _library = 1;
                return ok;
            }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
            {
                _library = 2;
            }
        }
        if (_library == 2)
        {
            try
            {
                return GetState910(index, out _) == ErrorSuccess;
            }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
            {
                _library = -1;
            }
        }
        return false;
    }

    public static string Describe(int count) => count switch
    {
        0 => "No Xbox controller",
        1 => "1 Xbox controller",
        _ => $"{count} Xbox controllers",
    };
}

/// <summary>A player's controller choice in the launcher (kept in launcher.json).</summary>
public static class DeviceChoice
{
    public const string Automatic = "auto";
    public const string Gamepad = "gamepad";
    public const string Keyboard = "keyboard";
    public const string None = "none";

    public static readonly string[] All = [Automatic, Gamepad, Keyboard, None];

    public static string Normalize(string? choice) => All.Contains(choice) ? choice! : Automatic;
}

/// <summary>
/// Turns the four choices into the settings.toml [controller] p1_device..p4_device values the
/// game understands ("gamepad" | "keyboard" | "none"), when PLAY is pressed. Automatic slots
/// share out the connected Xbox controllers in player order (after any slot explicitly set to a
/// controller); with none connected, an automatic Player 1 gets the keyboard. The runtime then
/// gives each "gamepad" slot the first controller no earlier slot has claimed.
/// </summary>
public static class ControllerPlan
{
    public const int Players = 4;

    public static string[] Resolve(IReadOnlyList<string> choices, int connectedPads)
    {
        var devices = new string[Players];
        string Choice(int i) => DeviceChoice.Normalize(i < choices.Count ? choices[i] : null);
        int padsLeft = connectedPads - Enumerable.Range(0, Players).Count(i => Choice(i) == DeviceChoice.Gamepad);
        bool keyboardTaken = Enumerable.Range(0, Players).Any(i => Choice(i) == DeviceChoice.Keyboard);
        for (int i = 0; i < Players; i++)
        {
            string choice = Choice(i);
            if (choice != DeviceChoice.Automatic)
            {
                devices[i] = choice;
            }
            else if (padsLeft > 0)
            {
                devices[i] = DeviceChoice.Gamepad;
                padsLeft--;
            }
            else
            {
                devices[i] = i == 0 && connectedPads == 0 && !keyboardTaken ? DeviceChoice.Keyboard : DeviceChoice.None;
            }
        }
        return devices;
    }

    /// <summary>Players 3 and 4 plug into a PlayStation multitap: on only when 3+ players have a device.</summary>
    public static bool NeedsMultitap(IReadOnlyList<string> devices) => devices.Count(d => d != DeviceChoice.None) >= 3;

    public static string Label(string device) => device switch
    {
        DeviceChoice.Gamepad => "Xbox controller",
        DeviceChoice.Keyboard => "Keyboard",
        _ => "Nobody",
    };

    /// <summary>For the log and the self-test: "P1=gamepad P2=none ... multitap=false".</summary>
    public static string Summary(IReadOnlyList<string> devices) =>
        string.Join(" ", devices.Select((d, i) => $"P{i + 1}={d}")) + $" multitap={(NeedsMultitap(devices) ? "true" : "false")}";
}

/// <summary>
/// The controller-to-PlayStation button map from input.ini next to the game exe ([mapping],
/// SDL gamepad names), for the reference table on the Controls tab.
/// </summary>
public sealed class InputMap
{
    public const string FileName = "input.ini";
    public const string KeyboardFileName = "keybinds.ini";

    private static readonly Dictionary<string, string> Defaults = new()
    {
        ["cross"] = "a", ["circle"] = "b", ["square"] = "x", ["triangle"] = "y",
        ["l1"] = "leftshoulder", ["r1"] = "rightshoulder", ["l2"] = "lefttrigger", ["r2"] = "righttrigger",
        ["l3"] = "leftstick", ["r3"] = "rightstick", ["start"] = "start", ["select"] = "back",
        ["up"] = "dpup", ["down"] = "dpdown", ["left"] = "dpleft", ["right"] = "dpright",
        ["ls_up"] = "lefty-",
    };

    private readonly Dictionary<string, string> _map;

    private InputMap(Dictionary<string, string> map, bool fromFile)
    {
        _map = map;
        FromFile = fromFile;
    }

    public bool FromFile { get; }

    public static InputMap Load(string? gameExe)
    {
        string? path = gameExe == null ? null : Path.Combine(Path.GetDirectoryName(gameExe)!, FileName);
        var map = new Dictionary<string, string>(Defaults, StringComparer.OrdinalIgnoreCase);
        if (path == null || !File.Exists(path)) return new InputMap(map, false);
        try
        {
            string section = "";
            foreach (string raw in File.ReadLines(path))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] is ';' or '#') continue;
                if (line[0] == '[')
                {
                    section = line.Trim('[', ']').Trim().ToLowerInvariant();
                    continue;
                }
                int eq = line.IndexOf('=');
                if (section != "mapping" || eq <= 0) continue;
                map[line[..eq].Trim()] = line[(eq + 1)..].Trim();
            }
            return new InputMap(map, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new InputMap(map, false);
        }
    }

    /// <summary>The controller input(s) a PlayStation button is mapped to, in Xbox terms.</summary>
    public string XboxFor(string psxButton)
    {
        if (!_map.TryGetValue(psxButton, out string? sources) || string.IsNullOrWhiteSpace(sources)) return "(unbound)";
        var names = sources.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(XboxName).ToList();
        return names.Count == 0 ? "(unbound)" : string.Join(" / ", names);
    }

    public static string XboxName(string sdl) => Regex.Replace(sdl.ToLowerInvariant(), "[^a-z0-9+-]", "") switch
    {
        "a" => "A",
        "b" => "B",
        "x" => "X",
        "y" => "Y",
        "leftshoulder" => "LB",
        "rightshoulder" => "RB",
        "lefttrigger" or "lefttrigger+" => "LT",
        "righttrigger" or "righttrigger+" => "RT",
        "leftstick" => "LS (click)",
        "rightstick" => "RS (click)",
        "back" => "View",
        "start" => "Menu",
        "guide" => "Xbox",
        "dpup" => "D-pad up",
        "dpdown" => "D-pad down",
        "dpleft" => "D-pad left",
        "dpright" => "D-pad right",
        "none" or "" => "(unbound)",
        var other => other,
    };

    /// <summary>The left stick folds onto the D-pad (digital pad) when ls_* entries map stick axes.</summary>
    public bool LeftStickMoves => _map.TryGetValue("ls_up", out string? v) && v.Contains("lefty", StringComparison.OrdinalIgnoreCase);
}

/// <summary>How many player slots the game exe was compiled with (only known in a build folder).</summary>
public static partial class BuildInfo
{
    private static readonly Dictionary<string, int?> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>PSX_MAX_PLAYERS from build.ninja next to the exe, or null when there is none (a release).
    /// The PlayStation multitap (players 3 and 4) is only compiled in at 5 or more.</summary>
    public static int? PlayerSlots(string? gameExe)
    {
        if (gameExe == null) return null;
        if (Cache.TryGetValue(gameExe, out int? cached)) return cached;
        int? slots = null;
        try
        {
            string ninja = Path.Combine(Path.GetDirectoryName(gameExe)!, "build.ninja");
            if (File.Exists(ninja))
            {
                using var reader = new StreamReader(ninja);
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    var m = MaxPlayers().Match(line);
                    if (m.Success)
                    {
                        slots = int.Parse(m.Groups[1].Value);
                        break;
                    }
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
        Cache[gameExe] = slots;
        return slots;
    }

    public const int MultitapMinimumSlots = 5;

    [GeneratedRegex(@"PSX_MAX_PLAYERS=(\d+)")]
    private static partial Regex MaxPlayers();
}
