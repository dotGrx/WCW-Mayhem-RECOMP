using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using Avalonia;
using WcwLauncher.Core;

namespace WcwLauncher;

internal static class Program
{
    /// <summary>Set by --capture-screenshots: the window saves its home and settings views there and closes.</summary>
    public static string? CaptureFolder { get; private set; }

    [STAThread]
    public static int Main(string[] args)
    {
        // Headless check for troubleshooting: what the launcher sees, without opening a window.
        // An optional path after it checks that disc image instead of the saved one.
        int selfTest = Array.IndexOf(args, "--self-test");
        if (selfTest >= 0) return SelfTest(selfTest + 1 < args.Length ? args[selfTest + 1] : null);
        int capture = Array.IndexOf(args, "--capture-screenshots");
        if (capture >= 0) CaptureFolder = Path.GetFullPath(capture + 1 < args.Length ? args[capture + 1] : AppContext.BaseDirectory);
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    private static int SelfTest(string? discArgument)
    {
        var r = new StringBuilder();
        var state = LauncherState.Load();
        bool ok = true;
        r.AppendLine($"launcher    : {Environment.ProcessPath} ({Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)})");

        string? exe = GameInstall.FindGameExe(state.GameExe);
        r.AppendLine($"game exe    : {exe ?? "(not found)"}");
        if (exe == null) ok = false;
        TomlTable gameToml = GameInstall.ReadGameToml(exe);
        var identity = DiscIdentity.From(gameToml, GameInstall.GameTomlPath(exe));
        r.AppendLine($"disc ident  : {DiscIdentity.Serial}, track 1 {string.Join(" / ", identity.Sizes.Select(s => s.ToString("N0")))} bytes, " +
                     $"MD5 {string.Join(" / ", identity.Md5s)} (from {identity.Source})");

        string? disc = discArgument ?? state.DiscPath ?? GameInstall.GuessDisc(exe, gameToml);
        string source = discArgument != null ? "argument" : state.DiscPath != null ? "launcher.json" : "guessed";
        r.AppendLine($"disc        : {disc ?? "(none)"} ({source})");
        DiscInfo info = DiscImage.Inspect(disc, identity, state);
        if (info.TrackFile != null) r.AppendLine($"track 1     : {info.TrackFile} ({info.Size:N0} bytes)");
        if (info.Status == DiscStatus.NeedsHash)
        {
            var watch = Stopwatch.StartNew();
            string md5 = DiscImage.Md5(info.TrackFile!, null, CancellationToken.None);
            state.RememberMd5(info.TrackFile!, info.Size, info.ModifiedUtc, md5);
            state.Save();
            r.AppendLine($"md5         : {md5} (hashed in {watch.ElapsedMilliseconds} ms, cached in launcher.json)");
            info = DiscImage.WithMd5(info, md5, identity);
        }
        else if (info.TrackFile != null && state.CachedMd5(info.TrackFile, info.Size, info.ModifiedUtc) is { } cached)
        {
            r.AppendLine($"md5         : {cached} (cached in launcher.json)");
        }
        r.AppendLine($"disc check  : {info.Status} - {info.Detail}");
        if (!info.Playable) ok = false;

        if (exe != null)
        {
            string gameDir = Path.GetDirectoryName(exe)!;
            var settings = PlayerSettings.Load(gameDir, gameToml);
            r.AppendLine($"settings    : {settings.FilePath} ({(settings.FileExists ? "exists" : "not created yet; game.toml defaults apply")})");
            if (settings.SyntaxError != null) r.AppendLine($"  SYNTAX ERROR (the game ignores the file): {settings.SyntaxError}");
            int ir = settings.InternalResolutionValue;
            int display = InternalResolution.PrimaryDisplayHeight();
            r.AppendLine($"  internal resolution : {InternalResolution.Label(ir)} (scale {InternalResolution.Scale(ir, display)}x; " +
                         $"settings.toml internal_resolution={settings.Raw("video", "internal_resolution") ?? "-"} supersampling={settings.Raw("video", "supersampling") ?? "-"})");
            r.AppendLine($"  renderer={settings.Renderer} fullscreen={settings.Fullscreen} vsync={settings.VSync} " +
                         $"window_width={settings.WindowWidth?.ToString() ?? "auto"} texture_filtering={(settings.BilinearTextures ? "bilinear" : "nearest")} " +
                         $"antialiasing={settings.Antialiasing} crt_filter={settings.CrtFilter} scanlines={settings.Scanlines} " +
                         $"scanline_strength={settings.ScanlineStrength:0.0##} fast_boot={settings.FastBoot} spu_hq={settings.HighQualityAudio} " +
                         $"frequency={settings.AudioFrequency}");
            string? staleInterp = settings.Raw("video", "frame_interpolation");
            if (staleInterp != null)
                r.AppendLine($"  note: settings.toml has frame_interpolation = {staleInterp} (ignored by the game; smooth motion is the mod below)");

            string modsRoot = LauncherMods.ModsRoot(exe);
            var catalog = ModCatalog.Scan(modsRoot);
            var mods = ModState.Load(modsRoot);
            r.AppendLine($"mod catalog : {modsRoot} ({catalog.Packages.Count} packages: " +
                         $"{string.Join(", ", catalog.Packages.Select(p => $"{p.Id} {p.Version}"))})");
            foreach (string error in catalog.Errors) r.AppendLine($"  MANIFEST ERROR: {error}");
            r.AppendLine($"mod state   : {mods.Path} ({(mods.Exists ? "exists" : "not created yet; manifest defaults apply")})");
            if (mods.Error != null) r.AppendLine($"  UNREADABLE (the game starts with no mods): {mods.Error}");
            ReportMod(r, catalog, mods, "widescreen   ", LauncherMods.WidescreenPackage, LauncherMods.WidescreenFeature, LauncherMods.WidescreenAspect);
            ReportMod(r, catalog, mods, "smooth motion", LauncherMods.SmoothMotionPackage, LauncherMods.SmoothMotionFeature,
                LauncherMods.SmoothMotionRate, LauncherMods.SmoothMotionBlend);
            ReportMod(r, catalog, mods, "pgxp         ", LauncherMods.PgxpPackage, LauncherMods.PgxpFeature);
            ReportMod(r, catalog, mods, "unlock all   ", LauncherMods.UnlockAllPackage, LauncherMods.UnlockAllFeature);
            ReportMod(r, catalog, mods, "cd speed     ", LauncherMods.CdSpeedPackage, LauncherMods.CdSpeedFeature,
                LauncherMods.CdSpeedMultiplier, LauncherMods.CdSpeedInstant);
            if (catalog.Find(LauncherMods.CdSpeedPackage) is { } cdPackage)
            {
                bool cdOn = mods.IsEnabled(cdPackage, LauncherMods.CdSpeedFeature);
                bool instant = mods.OptionValue(cdPackage, LauncherMods.CdSpeedFeature, LauncherMods.CdSpeedInstant) == "true";
                string multiplier = mods.OptionValue(cdPackage, LauncherMods.CdSpeedFeature, LauncherMods.CdSpeedMultiplier);
                r.AppendLine($"    launcher shows: {(!cdOn ? "Off" : instant ? "Instant (hand-set)" : $"{multiplier}x")}");
            }
            // Fast Loading (host pacing) is no longer offered; report where it stands, never as an error.
            bool fastExcluded = LauncherMods.ExcludedBuiltins(exe).Contains(LauncherMods.FastLoadingPackage);
            ModPackage? fastPackage = catalog.Find(LauncherMods.FastLoadingPackage);
            string fastWhere = fastExcluded ? "excluded by the game build (EXCLUDE_BUILTIN_MODS)"
                : fastPackage == null ? "absent from this build's catalog"
                : "still in mods\\bundled (until the game is rebuilt with it excluded)";
            string fastState = fastPackage != null
                ? (mods.IsEnabled(fastPackage, LauncherMods.FastLoadingFeature) ? "ON in state.toml, so it still runs in this build" : "off")
                : "inactive";
            r.AppendLine($"  fast loading  : not offered by the launcher; {fastWhere}; {fastState}" +
                         (mods.Text.Contains($"\"{LauncherMods.FastLoadingPackage}\"") ? "; its old state.toml entry is left as it is" : ""));

            // Controllers: what XInput sees now and what PLAY would write.
            bool[] slots = XInput.Connected();
            int pads = slots.Count(c => c);
            r.AppendLine($"controllers : {XInput.Describe(pads)} via {XInput.Library} " +
                         $"(slots {string.Join(" ", slots.Select((c, i) => $"{i}:{(c ? "connected" : "-")}"))})");
            var choices = state.Controllers.Take(ControllerPlan.Players).Select(DeviceChoice.Normalize).ToList();
            while (choices.Count < ControllerPlan.Players) choices.Add(DeviceChoice.Automatic);
            string[] devices = ControllerPlan.Resolve(choices, pads);
            r.AppendLine($"  choices (launcher.json) : {string.Join(" ", choices.Select((c, i) => $"P{i + 1}={c}"))}");
            r.AppendLine($"  PLAY would write        : [controller] " +
                         string.Join(" ", devices.Select((d, i) => $"p{i + 1}_device=\"{d}\"")) +
                         $" multitap={(ControllerPlan.NeedsMultitap(devices) ? "true" : "false")}");
            for (int n = 0; n <= XInput.Slots; n++)
            {
                if (n == pads) continue;
                r.AppendLine($"    with {n} pad{(n == 1 ? " " : "s")} instead   : {ControllerPlan.Summary(ControllerPlan.Resolve(choices, n))}");
            }
            r.AppendLine($"  settings.toml now       : {string.Join(" ", Enumerable.Range(1, ControllerPlan.Players).Select(p => $"p{p}_device={settings.Device(p)}"))} " +
                         $"multitap={(settings.Multitap ? "true" : "false")} (game.toml [controller] p1_device={gameToml.Table("controller")?.String("p1_device") ?? "-"})");
            r.AppendLine($"  pad type / deadzone     : {string.Join(" ", Enumerable.Range(1, ControllerPlan.Players).Select(p => $"p{p}_mode={settings.PadMode(p)}"))} " +
                         $"deadzone={settings.Deadzone(1)}");
            int? built = BuildInfo.PlayerSlots(exe);
            r.AppendLine($"  player slots in build   : {(built?.ToString() ?? "unknown (no build.ninja next to the exe)")}" +
                         (built is { } b && b < BuildInfo.MultitapMinimumSlots
                             ? $" (multitap needs {BuildInfo.MultitapMinimumSlots}: players 3-4 can't reach the game in this build)" : ""));
            var map = InputMap.Load(exe);
            r.AppendLine($"  button map ({(map.FromFile ? "input.ini" : "defaults")}) : " +
                         string.Join(" ", new[] { "cross", "circle", "square", "triangle", "l1", "r1", "l2", "r2", "select", "start" }
                             .Select(k => $"{k}={map.XboxFor(k)}")) + $" left-stick-moves={map.LeftStickMoves}");

            string writers = CheckWriters(settings.FilePath, mods.Path, catalog);
            r.AppendLine($"writer check: {writers}");
            if (!writers.StartsWith("ok")) ok = false;
            if (disc != null)
                r.AppendLine($"command     : {GameRunner.Describe(GameRunner.StartInfo(exe, disc))}");
        }

        string? background = LauncherArt.Background;
        r.AppendLine($"art         : background {background ?? "(none: drawn backdrop)"}, logo {LauncherArt.Logo ??
            (background != null ? "(none: the background's own title logo shows)" : "(none: typed WCW MAYHEM title)")}");
        r.AppendLine($"logs        : {GameRunner.LogsFolder} (game output: last-run.log; exit report: last-run-report.json)");
        r.AppendLine($"result      : {(ok ? "PASS" : "FAIL")}");
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "launcher-selftest.txt"), r.ToString());
        return ok ? 0 : 1;
    }

    private static void ReportMod(StringBuilder r, ModCatalog catalog, ModState mods, string label, string packageId,
        string featureId, params string[] options)
    {
        ModPackage? package = catalog.Find(packageId, mods.PinnedVersion(packageId));
        ModFeature? feature = package?.Feature(featureId);
        if (package == null || feature == null)
        {
            r.AppendLine($"  {label} : (not in this build's catalog: {packageId})");
            return;
        }
        string values = string.Join(" ", options.Select(o =>
        {
            var choices = package.Option(featureId, o)?.Choices.Select(c => c.Value).ToList() ?? [];
            return $"{o}={mods.OptionValue(package, featureId, o)}" + (choices.Count > 0 ? $" [{string.Join("|", choices)}]" : "");
        }));
        r.AppendLine($"  {label} : {(mods.IsEnabled(package, featureId) ? "on" : "off")} (default {(feature.DefaultEnabled ? "on" : "off")}, " +
                     $"{feature.Channel}) {packageId}/{featureId} {values}".TrimEnd());
    }

    /// <summary>
    /// Proves the two writers on throwaway copies (in %TEMP%, never the game's own files): every
    /// original line of settings.toml survives an edit, the result is valid TOML that reads back
    /// the new values, and state.toml keeps the entries it doesn't own.
    /// </summary>
    private static string CheckWriters(string settingsPath, string statePath, ModCatalog catalog)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"wcw-launcher-selftest-{Environment.ProcessId}");
        try
        {
            Directory.CreateDirectory(dir);
            string original = File.Exists(settingsPath)
                ? File.ReadAllText(settingsPath)
                : "# a comment the launcher must keep\n[video]\nrenderer = \"opengl\"  # trailing comment\n\n[controller]\np1_mode = \"digital\"\n";
            string copy = Path.Combine(dir, "settings.toml");
            File.WriteAllText(copy, original);
            var settings = PlayerSettings.Load(dir, new TomlTable());
            settings.InternalResolutionValue = settings.InternalResolutionValue == 2160 ? 1440 : 2160;
            settings.VSync = settings.VSync == 0 ? 1 : 0;
            settings.ScanlineStrength = settings.ScanlineStrength > 0.6 ? 0.3 : 0.8;
            settings.HighQualityAudio = !settings.HighQualityAudio;
            string[] devices = ["gamepad", "gamepad", "keyboard", "none"];
            settings.WriteControllerAssignment(devices, ControllerPlan.NeedsMultitap(devices));
            for (int p = 1; p <= ControllerPlan.Players; p++) settings.SetPadMode(p, "analog");
            settings.SetDeadzone(1, 6554);
            settings.Save();
            string written = File.ReadAllText(copy);
            Toml.Parse(written);
            var lines = new HashSet<string>(written.Replace("\r\n", "\n").Split('\n'));
            var reread = PlayerSettings.Load(dir, new TomlTable());
            var touchedKeys = new[] { "internal_resolution", "supersampling", "vsync", "scanline_strength", "spu_hq",
                                      "p1_", "p2_", "p3_", "p4_", "multitap" };
            var lost = original.Replace("\r\n", "\n").Split('\n')
                .Where(l => !lines.Contains(l) && !touchedKeys.Any(k => l.TrimStart().StartsWith(k))).ToList();
            if (lost.Count > 0) return $"FAIL: settings.toml lines lost: {string.Join(" | ", lost)}";
            if (reread.InternalResolutionValue != settings.InternalResolutionValue || reread.VSync != settings.VSync ||
                reread.HighQualityAudio != settings.HighQualityAudio)
                return "FAIL: settings.toml values didn't read back";
            if (reread.Device(1) != "gamepad" || reread.Device(2) != "gamepad" || reread.Device(3) != "keyboard" ||
                reread.Device(4) != "none" || !reread.Multitap || reread.PadMode(4) != "analog" || reread.Deadzone(1) != 6554)
                return "FAIL: [controller] values didn't read back";
            if (written.Contains("frame_interpolation") && !original.Contains("frame_interpolation"))
                return "FAIL: frame_interpolation was written to settings.toml";

            string stateDir = Path.Combine(dir, "mods");
            Directory.CreateDirectory(stateDir);
            if (File.Exists(statePath)) File.Copy(statePath, Path.Combine(stateDir, "state.toml"));
            var before = ModState.Load(stateDir);
            if (before.Error != null) return $"FAIL: state.toml unreadable: {before.Error}";
            string beforeText = before.Text;
            ModPackage? ws = catalog.Find(LauncherMods.WidescreenPackage);
            ModPackage? sm = catalog.Find(LauncherMods.SmoothMotionPackage);
            if (ws == null || sm == null) return "FAIL: widescreen / smooth-motion packages missing from the catalog";
            before.Set(ws, LauncherMods.WidescreenFeature, !before.IsEnabled(ws, LauncherMods.WidescreenFeature),
                new Dictionary<string, string> { [LauncherMods.WidescreenAspect] = "21:9" });
            before.Set(sm, LauncherMods.SmoothMotionFeature, true,
                new Dictionary<string, string> { [LauncherMods.SmoothMotionRate] = "120", [LauncherMods.SmoothMotionBlend] = "linear" });
            // CD speed as the dropdown writes it (8x, instant unticked), when the build ships it.
            ModPackage? cd = catalog.Find(LauncherMods.CdSpeedPackage);
            if (cd != null)
            {
                before.Set(cd, LauncherMods.CdSpeedFeature, true, new Dictionary<string, string>
                {
                    [LauncherMods.CdSpeedMultiplier] = "8",
                    [LauncherMods.CdSpeedInstant] = "false",
                });
            }
            // Unlock all: no options, just enabled; flip it so a change is always written.
            ModPackage? unlock = catalog.Find(LauncherMods.UnlockAllPackage);
            bool unlockTo = unlock != null && !before.IsEnabled(unlock, LauncherMods.UnlockAllFeature);
            if (unlock != null) before.Set(unlock, LauncherMods.UnlockAllFeature, unlockTo, new Dictionary<string, string>());
            before.Save();
            var after = ModState.Load(stateDir);
            if (after.Error != null) return $"FAIL: written state.toml unreadable: {after.Error}";
            if (unlock != null)
            {
                if (after.IsEnabled(unlock, LauncherMods.UnlockAllFeature) != unlockTo) return "FAIL: unlock-all didn't read back";
                string block = $"[[feature]]\npackage_id = \"{LauncherMods.UnlockAllPackage}\"\nid = \"{LauncherMods.UnlockAllFeature}\"\n" +
                               $"enabled = {(unlockTo ? "true" : "false")}\n";
                if (!after.Text.Contains(block)) return "FAIL: unlock-all [[feature]] block isn't in the game's layout";
            }
            if (after.OptionValue(ws, LauncherMods.WidescreenFeature, LauncherMods.WidescreenAspect) != "21:9" ||
                !after.IsEnabled(sm, LauncherMods.SmoothMotionFeature) ||
                after.OptionValue(sm, LauncherMods.SmoothMotionFeature, LauncherMods.SmoothMotionRate) != "120")
                return "FAIL: state.toml values didn't read back";
            if (cd != null && (!after.IsEnabled(cd, LauncherMods.CdSpeedFeature) ||
                               after.OptionValue(cd, LauncherMods.CdSpeedFeature, LauncherMods.CdSpeedMultiplier) != "8" ||
                               after.OptionValue(cd, LauncherMods.CdSpeedFeature, LauncherMods.CdSpeedInstant) != "false"))
                return "FAIL: CD speed didn't read back";
            string[] owned = ["enabled", "aspect", "rate", "blend", "multiplier", "instant", "[feature.values]"];
            var kept = beforeText.Split('\n').Where(l => l.Length > 0 && !owned.Any(l.StartsWith))
                .Except(after.Text.Split('\n')).ToList();
            if (kept.Count > 0) return $"FAIL: state.toml lost lines: {string.Join(" | ", kept)}";
            // The next game build drops fast-loading from mods\bundled: copy the catalog without it,
            // save a CD speed change over the real state.toml, and check nothing breaks and the
            // old fast-loading entry is still there, untouched.
            string futureRoot = Path.Combine(dir, "future-mods");
            string bundled = Path.Combine(Path.GetDirectoryName(statePath)!, "bundled");
            if (Directory.Exists(bundled))
            {
                foreach (string manifest in Directory.EnumerateFiles(bundled, "manifest.toml", SearchOption.AllDirectories))
                {
                    string relative = Path.GetRelativePath(bundled, manifest);
                    if (relative.StartsWith(LauncherMods.FastLoadingPackage, StringComparison.Ordinal)) continue;
                    string target = Path.Combine(futureRoot, "bundled", relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(manifest, target);
                }
            }
            if (File.Exists(statePath)) File.Copy(statePath, Path.Combine(futureRoot, "state.toml"));
            var futureCatalog = ModCatalog.Scan(futureRoot);
            var futureState = ModState.Load(futureRoot);
            if (futureCatalog.Find(LauncherMods.FastLoadingPackage) != null) return "FAIL: fast-loading still in the simulated catalog";
            string fastEntriesBefore = string.Join("\n", futureState.Text.Split('\n').Where(l => l.Contains(LauncherMods.FastLoadingPackage)));
            if (futureCatalog.Find(LauncherMods.CdSpeedPackage) is { } futureCd)
            {
                futureState.Set(futureCd, LauncherMods.CdSpeedFeature, true, new Dictionary<string, string>
                {
                    [LauncherMods.CdSpeedMultiplier] = "4",
                    [LauncherMods.CdSpeedInstant] = "false",
                });
                futureState.Save();
            }
            string fastEntriesAfter = string.Join("\n", ModState.Load(futureRoot).Text.Split('\n').Where(l => l.Contains(LauncherMods.FastLoadingPackage)));
            if (fastEntriesAfter != fastEntriesBefore) return "FAIL: the old fast-loading state.toml entry changed";
            int originalLines = original.Replace("\r\n", "\n").TrimEnd('\n').Split('\n').Length;
            return $"ok (settings.toml: {originalLines} original lines kept + display/audio/controller edits read back, valid TOML; " +
                   $"state.toml: widescreen/smooth motion{(cd != null ? "/CD speed 8x" : "")}" +
                   $"{(unlock != null ? $"/unlock-all {(unlockTo ? "on" : "off")}" : "")} read back, other entries kept, valid TOML; " +
                   "catalog without fast-loading: CD speed saved, old fast-loading entry untouched; " +
                   $"temp copies in {dir}, deleted)";
        }
        catch (Exception e)
        {
            return $"FAIL: {e.GetType().Name}: {e.Message}";
        }
        finally
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (Exception)
            {
            }
        }
    }
}
