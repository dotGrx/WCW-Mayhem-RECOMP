using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace WcwLauncher.Core;

public sealed record ModChoice(string Value, string Label);

public sealed record ModOption(string FeatureId, string Id, string Type, string Default, IReadOnlyList<ModChoice> Choices);

public sealed record ModFeature(string Id, string Name, bool DefaultEnabled, bool Hidden, string Channel);

public sealed record ModPackage(string Id, string Version, string Name, string Folder, bool Installed,
    IReadOnlyList<string> Targets, IReadOnlyList<ModFeature> Features, IReadOnlyList<ModOption> Options)
{
    public ModFeature? Feature(string id) => Features.FirstOrDefault(f => f.Id == id);
    public ModOption? Option(string featureId, string id) => Options.FirstOrDefault(o => o.FeatureId == featureId && o.Id == id);
}

/// <summary>
/// The game's mod catalog: mods\bundled (staged by every build) and mods\installed (the player's
/// archives) beside the game exe, each &lt;package-id&gt;\&lt;version&gt;\manifest.toml. Read only for
/// feature defaults and option choices; the game does the real resolution.
/// </summary>
public sealed class ModCatalog
{
    private readonly List<ModPackage> _packages = [];

    public IReadOnlyList<ModPackage> Packages => _packages;
    public List<string> Errors { get; } = [];

    public static ModCatalog Scan(string modsRoot)
    {
        var catalog = new ModCatalog();
        catalog.ScanRoot(Path.Combine(modsRoot, "bundled"), installed: false);
        catalog.ScanRoot(Path.Combine(modsRoot, "installed"), installed: true);
        return catalog;
    }

    /// <summary>The selected version (state.toml's, else the newest; installed shadows bundled).</summary>
    public ModPackage? Find(string id, string? version = null)
    {
        var all = _packages.Where(p => p.Id == id).ToList();
        if (all.Count == 0) return null;
        if (version != null && all.LastOrDefault(p => p.Version == version) is { } pinned) return pinned;
        return all.OrderBy(p => ParseVersion(p.Version)).ThenBy(p => p.Installed).Last();
    }

    private static Version ParseVersion(string v) =>
        System.Version.TryParse(v.Split('-', '+')[0], out var parsed) ? parsed : new Version(0, 0);

    private void ScanRoot(string root, bool installed)
    {
        if (!Directory.Exists(root)) return;
        foreach (string packageDir in Directory.EnumerateDirectories(root).Order(StringComparer.Ordinal))
        {
            foreach (string versionDir in Directory.EnumerateDirectories(packageDir).Order(StringComparer.Ordinal))
            {
                string manifest = Path.Combine(versionDir, "manifest.toml");
                if (!File.Exists(manifest)) continue;
                try
                {
                    _packages.Add(ReadManifest(Toml.Parse(File.ReadAllText(manifest)), versionDir, installed));
                }
                catch (Exception e) when (e is TomlException or IOException or UnauthorizedAccessException)
                {
                    Errors.Add($"{manifest}: {e.Message}");
                }
            }
        }
    }

    private static ModPackage ReadManifest(TomlTable m, string folder, bool installed)
    {
        string id = m.String("id") ?? Path.GetFileName(Path.GetDirectoryName(folder)!) ?? "";
        string version = m.String("version") ?? Path.GetFileName(folder);
        string packageChannel = m.String("channel") ?? "stable";
        var targets = m.Tables("target").Select(t => t.String("game_id") ?? "").ToList();
        var features = m.Tables("feature").Select(f => new ModFeature(
            f.String("id") ?? "",
            f.String("name") ?? f.String("id") ?? "",
            f.Bool("default_enabled") ?? false,
            f.Bool("hidden") ?? false,
            f.String("channel") ?? packageChannel)).ToList();
        var options = m.Tables("option").Select(o => new ModOption(
            o.String("feature") ?? "",
            o.String("id") ?? "",
            o.String("type") ?? "",
            o.Scalar("default") ?? "",
            o.Tables("choice").Select(c => new ModChoice(c.Scalar("value") ?? "", c.String("label") ?? c.Scalar("value") ?? "")).ToList()))
            .ToList();
        return new ModPackage(id, version, m.String("name") ?? id, folder, installed, targets, features, options);
    }
}

/// <summary>
/// mods\state.toml beside the game exe: which mod features the player turned on and their option
/// values (format 2, docs\MOD_PACKAGES.md "State and migration"). Written in the exact layout the
/// game's own ModPackageManager::save_state() produces (the game rewrites the file that way on
/// every start), keeping every entry the launcher doesn't own: other packages, resources, sources.
/// </summary>
public sealed class ModState
{
    private sealed class PackageEntry
    {
        public string? Version;
        public bool? Enabled;  // legacy (format 1) packages only
        public SortedDictionary<string, string> Values = new(StringComparer.Ordinal);
    }

    private sealed class FeatureEntry
    {
        public bool Enabled;
        public SortedDictionary<string, string> Values = new(StringComparer.Ordinal);
        public SortedDictionary<string, string> Resources = new(StringComparer.Ordinal);
    }

    private readonly SortedDictionary<string, string> _sources = new(StringComparer.Ordinal);
    private readonly SortedDictionary<string, PackageEntry> _packages = new(StringComparer.Ordinal);
    private readonly SortedDictionary<string, SortedDictionary<string, FeatureEntry>> _features = new(StringComparer.Ordinal);
    private bool _changed;

    private ModState(string path) => Path = path;

    public string Path { get; }
    public bool Exists { get; private set; }
    /// <summary>The file couldn't be read; the game then runs with no mods at all.</summary>
    public string? Error { get; private set; }

    public static ModState Load(string modsRoot)
    {
        var state = new ModState(System.IO.Path.Combine(modsRoot, "state.toml"));
        if (!File.Exists(state.Path)) return state;
        state.Exists = true;
        try
        {
            state.Read(Toml.Parse(File.ReadAllText(state.Path)));
        }
        catch (Exception e) when (e is TomlException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            state.Error = e.Message;
            state._sources.Clear();
            state._packages.Clear();
            state._features.Clear();
        }
        return state;
    }

    private void Read(TomlTable doc)
    {
        long version = doc.Int("format_version") ?? throw new InvalidDataException("no format_version");
        if (version is not (1 or 2)) throw new InvalidDataException($"unsupported format_version {version}");
        if (doc.Table("sources") is { } sources)
        {
            foreach (var (id, value) in sources)
            {
                _sources[id] = value as string ?? throw new InvalidDataException("invalid shared source binding");
            }
        }
        foreach (TomlTable p in doc.Tables("package"))
        {
            string id = p.String("id") ?? throw new InvalidDataException("[[package]] without id");
            var entry = Package(id);
            entry.Version = p.String("version");
            entry.Enabled = p.Bool("enabled");
            if (p.Table("values") is { } values)
            {
                foreach (var (k, v) in values) entry.Values[k] = ScalarValue(v);
            }
        }
        if (version != 2) return;
        foreach (TomlTable f in doc.Tables("feature"))
        {
            string packageId = f.String("package_id") ?? throw new InvalidDataException("[[feature]] without package_id");
            string featureId = f.String("id") ?? throw new InvalidDataException("[[feature]] without id");
            var entry = new FeatureEntry
            {
                Enabled = f.Bool("enabled") ?? throw new InvalidDataException($"feature {packageId}/{featureId} has no enabled = true/false"),
            };
            if (f.Table("values") is { } values)
            {
                foreach (var (k, v) in values) entry.Values[k] = ScalarValue(v);
            }
            if (f.Table("resources") is { } resources)
            {
                foreach (var (k, v) in resources)
                {
                    entry.Resources[k] = v as string ?? throw new InvalidDataException("resource paths must be strings");
                }
            }
            Package(packageId);
            Features(packageId)[featureId] = entry;
        }
    }

    private static string ScalarValue(object v) =>
        TomlTable.ScalarText(v is double ? throw new InvalidDataException("option values must be strings, booleans or integers") : v)
        ?? throw new InvalidDataException("option values must be scalar");

    private PackageEntry Package(string id)
    {
        if (!_packages.TryGetValue(id, out var entry)) _packages[id] = entry = new PackageEntry();
        return entry;
    }

    private SortedDictionary<string, FeatureEntry> Features(string packageId)
    {
        if (!_features.TryGetValue(packageId, out var map)) _features[packageId] = map = new(StringComparer.Ordinal);
        return map;
    }

    private FeatureEntry? Entry(string packageId, string featureId) =>
        _features.TryGetValue(packageId, out var map) && map.TryGetValue(featureId, out var e) ? e : null;

    public string? PinnedVersion(string packageId) => _packages.TryGetValue(packageId, out var p) ? p.Version : null;

    /// <summary>Whether a feature is on: the player's choice, else the manifest's default_enabled.</summary>
    public bool IsEnabled(ModPackage package, string featureId) =>
        Entry(package.Id, featureId)?.Enabled ?? package.Feature(featureId)?.DefaultEnabled ?? false;

    /// <summary>An option's value: the player's choice, else the manifest default.</summary>
    public string OptionValue(ModPackage package, string featureId, string optionId) =>
        Entry(package.Id, featureId)?.Values.GetValueOrDefault(optionId)
        ?? package.Option(featureId, optionId)?.Default ?? "";

    public bool HasChanges => _changed;

    /// <summary>Records a feature's on/off state (and option values) when it differs from now.</summary>
    public void Set(ModPackage package, string featureId, bool enabled, IReadOnlyDictionary<string, string>? values = null)
    {
        bool same = IsEnabled(package, featureId) == enabled &&
                    (values == null || values.All(kv => OptionValue(package, featureId, kv.Key) == kv.Value));
        if (same) return;
        Package(package.Id);
        var map = Features(package.Id);
        if (!map.TryGetValue(featureId, out var entry)) map[featureId] = entry = new FeatureEntry();
        entry.Enabled = enabled;
        if (values != null)
        {
            foreach (var (k, v) in values) entry.Values[k] = v;
        }
        _changed = true;
    }

    public string Text
    {
        get
        {
            var sb = new StringBuilder("format_version = 2\n");
            if (_sources.Count > 0)
            {
                sb.Append("\n[sources]\n");
                foreach (var (id, path) in _sources) sb.Append(Toml.Quote(id)).Append(" = ").Append(Toml.Quote(path)).Append('\n');
            }
            foreach (var (id, p) in _packages)
            {
                sb.Append("\n[[package]]\n").Append("id = ").Append(Toml.Quote(id)).Append('\n');
                if (!string.IsNullOrEmpty(p.Version)) sb.Append("version = ").Append(Toml.Quote(p.Version)).Append('\n');
                if (p.Enabled is { } on) sb.Append("enabled = ").Append(on ? "true" : "false").Append('\n');
                if (p.Values.Count > 0)
                {
                    sb.Append("[package.values]\n");
                    foreach (var (k, v) in p.Values) sb.Append(Toml.Key(k)).Append(" = ").Append(Toml.Quote(v)).Append('\n');
                }
            }
            foreach (var (packageId, map) in _features)
            {
                foreach (var (featureId, f) in map)
                {
                    sb.Append("\n[[feature]]\n")
                      .Append("package_id = ").Append(Toml.Quote(packageId)).Append('\n')
                      .Append("id = ").Append(Toml.Quote(featureId)).Append('\n')
                      .Append("enabled = ").Append(f.Enabled ? "true" : "false").Append('\n');
                    if (f.Values.Count > 0)
                    {
                        sb.Append("[feature.values]\n");
                        foreach (var (k, v) in f.Values) sb.Append(Toml.Key(k)).Append(" = ").Append(Toml.Quote(v)).Append('\n');
                    }
                    if (f.Resources.Count > 0)
                    {
                        sb.Append("[feature.resources]\n");
                        foreach (var (k, v) in f.Resources) sb.Append(Toml.Key(k)).Append(" = ").Append(Toml.Quote(v)).Append('\n');
                    }
                }
            }
            return sb.ToString();
        }
    }

    /// <summary>Atomic write. An unreadable old file is kept as state.toml.bad first.</summary>
    public void Save()
    {
        if (!_changed) return;
        string text = Text;
        Toml.Parse(text);  // never hand the game a file it can't read
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        if (Error != null && File.Exists(Path)) File.Copy(Path, Path + ".bad", overwrite: true);
        string temp = Path + ".launcher.tmp";
        File.WriteAllText(temp, text, new UTF8Encoding(false));
        File.Move(temp, Path, overwrite: true);
        _changed = false;
        Exists = true;
        Error = null;
    }
}

/// <summary>The mod features the launcher shows.</summary>
public static class LauncherMods
{
    public const string WidescreenPackage = "wcw.enhancement.widescreen";
    public const string WidescreenFeature = "widescreen";
    public const string WidescreenAspect = "aspect";
    public const string SmoothMotionPackage = "wcw.enhancement.smooth-motion";
    public const string SmoothMotionFeature = "smooth-motion";
    public const string SmoothMotionRate = "rate";
    public const string SmoothMotionBlend = "blend";
    public const string PgxpPackage = "psx.enhancement.pgxp";
    public const string PgxpFeature = "pgxp";
    /// <summary>Unlock all: the game's own Pay-Per-View unlock bits turned on every session (hidden
    /// wrestlers, doppelganger select, classic Nitro). No options; nothing goes to the memory card.</summary>
    public const string UnlockAllPackage = "wcw.cheat.unlock-all";
    public const string UnlockAllFeature = "unlock-all";
    /// <summary>CD Speed: divides the emulated sector delay, so loads finish sooner while the game
    /// itself runs at normal speed. Offered as Off / 2x / 4x / 8x (option "multiplier"); the
    /// launcher always leaves "instant" unticked.</summary>
    public const string CdSpeedPackage = "psx.enhancement.cd-speed";
    public const string CdSpeedFeature = "cd-speed";
    public const string CdSpeedMultiplier = "multiplier";
    public const string CdSpeedInstant = "instant";
    public static readonly int[] CdSpeedSteps = [2, 4, 8];
    public const int CdSpeedRecommended = 4;

    /// <summary>
    /// Fast Loading (host pacing) is no longer offered: it speeds up wall-clock time whenever it
    /// detects a load, and WCW keeps reading the CD during matches, so the game ran badly. The
    /// game build excludes the package (EXCLUDE_BUILTIN_MODS); the launcher only reports it, and
    /// leaves any old state.toml entry for it alone.
    /// </summary>
    public const string FastLoadingPackage = "psx.enhancement.fast-loading";
    public const string FastLoadingFeature = "fast-loading";

    public static string ModsRoot(string gameExe) => Path.Combine(Path.GetDirectoryName(gameExe)!, "mods");

    /// <summary>
    /// The framework packages the game build declines (EXCLUDE_BUILTIN_MODS), from the
    /// psx_mod_catalog_*.excluded.txt the build writes next to the exe; empty when there is none.
    /// </summary>
    public static HashSet<string> ExcludedBuiltins(string gameExe)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            foreach (string file in Directory.EnumerateFiles(Path.GetDirectoryName(gameExe)!, "psx_mod_catalog_*.excluded.txt"))
            {
                foreach (string line in File.ReadLines(file))
                {
                    string id = line.Trim();
                    if (id.Length > 0 && id[0] != '#') ids.Add(id);
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
        return ids;
    }
}
