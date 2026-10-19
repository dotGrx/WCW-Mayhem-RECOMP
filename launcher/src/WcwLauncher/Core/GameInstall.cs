using System;
using System.Collections.Generic;
using System.IO;

namespace WcwLauncher.Core;

/// <summary>Where the recompiled game lives, its bundled game.toml, and a first guess at the disc.</summary>
public static class GameInstall
{
    public const string GameExeName = "WCWMayhem_Recompiled.exe";

    /// <summary>The recompiled game: as configured, next to the launcher, or the project's build folder.</summary>
    public static string? FindGameExe(string? configured)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(configured)) candidates.Add(configured);
        string baseDir = AppContext.BaseDirectory;
        candidates.Add(Path.Combine(baseDir, GameExeName));
        // Source checkout: launcher\dist\ -> ..\..\build-release\ (build.cmd's output). Also the
        // older side-by-side layout, wcw_launcher\dist\ -> ..\..\WCWMayhemRecomp\build-release\.
        for (string? dir = baseDir; dir != null; dir = Path.GetDirectoryName(dir))
        {
            candidates.Add(Path.Combine(dir, "build-release", GameExeName));
            candidates.Add(Path.Combine(dir, "WCWMayhemRecomp", "build-release", GameExeName));
        }
        foreach (string c in candidates)
        {
            if (File.Exists(c)) return Path.GetFullPath(c);
        }
        return null;
    }

    public static string? GameTomlPath(string? gameExe) =>
        gameExe == null ? null : Path.Combine(Path.GetDirectoryName(gameExe)!, "game.toml");

    /// <summary>game.toml next to the game exe (the defaults the runtime starts from); empty if unreadable.</summary>
    public static TomlTable ReadGameToml(string? gameExe)
    {
        string? path = GameTomlPath(gameExe);
        try
        {
            if (path != null && File.Exists(path)) return Toml.Parse(File.ReadAllText(path));
        }
        catch (Exception e) when (e is TomlException or IOException or UnauthorizedAccessException)
        {
            // Defaults then come from the runtime's built-in values.
        }
        return new TomlTable();
    }

    /// <summary>
    /// A disc image to start with, in the order the game itself looks: settings.toml [disc] path,
    /// disc.cfg (its last picked disc), game.toml [game] disc, then a .cue next to the launcher
    /// or the game.
    /// </summary>
    public static string? GuessDisc(string? gameExe, TomlTable gameToml)
    {
        var candidates = new List<string?>();
        string? exeDir = gameExe != null ? Path.GetDirectoryName(gameExe) : null;
        if (exeDir != null)
        {
            try
            {
                var settings = TomlLines.Load(Path.Combine(exeDir, PlayerSettings.FileName), "");
                candidates.Add(Anchor(exeDir, settings.GetString("disc", "path")));
                string cfg = Path.Combine(exeDir, "disc.cfg");
                if (File.Exists(cfg))
                {
                    using var reader = new StreamReader(cfg);
                    candidates.Add(Anchor(exeDir, reader.ReadLine()?.Trim()));
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Fall through to the other guesses.
            }
            string? configured = gameToml.Table("game")?.String("disc");
            if (configured != null)
            {
                for (string? dir = exeDir; dir != null; dir = Path.GetDirectoryName(dir))
                {
                    candidates.Add(Anchor(dir, configured));
                    if (Path.IsPathRooted(configured)) break;
                }
            }
        }
        // A .cue dropped next to the launcher or the game.
        foreach (string dir in new[] { AppContext.BaseDirectory, exeDir ?? AppContext.BaseDirectory })
        {
            try
            {
                foreach (string cue in Directory.EnumerateFiles(dir, "*.cue")) candidates.Add(cue);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Not readable: skip.
            }
        }
        foreach (string? c in candidates)
        {
            if (!string.IsNullOrWhiteSpace(c) && File.Exists(c)) return Path.GetFullPath(c);
        }
        return null;
    }

    private static string? Anchor(string dir, string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        path = path.Replace('/', Path.DirectorySeparatorChar);
        return Path.IsPathRooted(path) ? path : Path.Combine(dir, path);
    }
}
