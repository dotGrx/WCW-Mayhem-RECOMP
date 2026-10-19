using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;

namespace WcwLauncher.Core;

public enum DiscStatus
{
    /// <summary>Nothing chosen, or the file is gone.</summary>
    Missing,
    /// <summary>Not a disc image the game can mount (bad cue sheet, missing track file).</summary>
    NotADisc,
    /// <summary>Readable, but not the release the recomp was built from. Play is still offered.</summary>
    UnknownVersion,
    /// <summary>Right size; the data track still has to be hashed.</summary>
    NeedsHash,
    Verified,
}

/// <summary>What the launcher knows about the chosen disc image.</summary>
public sealed record DiscInfo(string? Path, string? TrackFile, long Size, DateTime ModifiedUtc, DiscStatus Status, string Detail)
{
    public bool Playable => Status is DiscStatus.Verified or DiscStatus.UnknownVersion or DiscStatus.NeedsHash;
}

/// <summary>
/// The stock disc: WCW Mayhem (Europe), SLES-02193, a Redump single-track .cue/.bin. Track 1's
/// size and MD5 come from game.toml [prepare_disc] (the same digests the recomp's own disc
/// preparation checks), with the values it shipped with as a fallback.
/// </summary>
public sealed class DiscIdentity
{
    public const string Serial = "SLES-02193";
    private const long StockSize = 728503776;
    private const string StockMd5 = "f05d7e5edad41f89e3c679dad83ea5f2";

    public IReadOnlyList<long> Sizes { get; private init; } = [StockSize];
    public IReadOnlyList<string> Md5s { get; private init; } = [StockMd5];
    public string Source { get; private init; } = "built in";

    public static DiscIdentity From(TomlTable gameToml, string? gameTomlPath)
    {
        var prepare = gameToml.Table("prepare_disc");
        var sizes = (prepare?.GetValueOrDefault("known_sizes") as List<object>)?.OfType<long>().ToList();
        var md5s = (prepare?.GetValueOrDefault("known_md5") as List<object>)?.OfType<string>()
            .Select(s => s.ToLowerInvariant()).ToList();
        if (sizes is not { Count: > 0 } || md5s is not { Count: > 0 }) return new DiscIdentity();
        return new DiscIdentity { Sizes = sizes, Md5s = md5s, Source = gameTomlPath ?? "game.toml" };
    }

    public bool KnownSize(long size) => Sizes.Contains(size);
    public bool KnownMd5(string md5) => Md5s.Contains(md5.ToLowerInvariant());
}

public static class DiscImage
{
    /// <summary>The image types the game itself accepts for --disc.</summary>
    public static readonly string[] Patterns = ["*.cue", "*.bin", "*.img", "*.iso", "*.chd"];

    /// <summary>Checks a disc image without hashing it (cached hashes are used when they match).</summary>
    public static DiscInfo Inspect(string? path, DiscIdentity identity, LauncherState? cache)
    {
        if (string.IsNullOrWhiteSpace(path))
            return new DiscInfo(null, null, 0, default, DiscStatus.Missing, "Choose your WCW Mayhem disc image (.cue).");
        if (!File.Exists(path))
            return new DiscInfo(path, null, 0, default, DiscStatus.Missing, "The disc image isn't there any more. Choose it again.");

        string ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        if (ext == ".chd")
        {
            return new DiscInfo(path, path, new FileInfo(path).Length, File.GetLastWriteTimeUtc(path), DiscStatus.UnknownVersion,
                "CHD image: the game reads it, but the launcher can't check which release it is.");
        }

        string track;
        if (ext == ".cue")
        {
            string? file;
            try
            {
                file = FirstTrackFile(path);
            }
            catch (IOException e)
            {
                return new DiscInfo(path, null, 0, default, DiscStatus.NotADisc, $"The cue sheet couldn't be read: {e.Message}");
            }
            if (file == null)
                return new DiscInfo(path, null, 0, default, DiscStatus.NotADisc, "This .cue has no FILE line, so it isn't a disc image.");
            track = System.IO.Path.IsPathRooted(file) ? file : System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path)!, file);
            if (!File.Exists(track))
                return new DiscInfo(path, track, 0, default, DiscStatus.NotADisc,
                    $"The .cue points to \"{System.IO.Path.GetFileName(file)}\", which isn't next to it.");
        }
        else
        {
            track = path;
        }

        var info = new FileInfo(track);
        long size = info.Length;
        DateTime modified = info.LastWriteTimeUtc;
        if (!identity.KnownSize(size))
        {
            return new DiscInfo(path, track, size, modified, DiscStatus.UnknownVersion,
                $"Not the PAL disc the recomp was built from ({DiscIdentity.Serial}: track 1 is " +
                $"{identity.Sizes[0]:N0} bytes, this one {size:N0}). It may not run.");
        }
        string? md5 = cache?.CachedMd5(track, size, modified);
        return md5 == null
            ? new DiscInfo(path, track, size, modified, DiscStatus.NeedsHash, "Checking the disc image…")
            : WithMd5(new DiscInfo(path, track, size, modified, DiscStatus.NeedsHash, ""), md5, identity);
    }

    public static DiscInfo WithMd5(DiscInfo info, string md5, DiscIdentity identity) => identity.KnownMd5(md5)
        ? info with { Status = DiscStatus.Verified, Detail = $"Disc image verified ({DiscIdentity.Serial}, PAL)." }
        : info with
        {
            Status = DiscStatus.UnknownVersion,
            Detail = "This disc image's data track differs from the stock PAL release (a modified or bad dump?). It may not run.",
        };

    /// <summary>MD5 of a file, reading 1 MB at a time; reports progress from 0 to 1.</summary>
    public static string Md5(string file, Action<double>? progress, CancellationToken cancel)
    {
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20,
            FileOptions.SequentialScan);
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(1 << 20);
        try
        {
            long total = Math.Max(1, stream.Length), done = 0;
            int lastPercent = -1;
            int n;
            while ((n = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                cancel.ThrowIfCancellationRequested();
                md5.AppendData(buffer, 0, n);
                done += n;
                int percent = (int)(done * 100 / total);
                if (percent != lastPercent)
                {
                    lastPercent = percent;
                    progress?.Invoke(done / (double)total);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
        return Convert.ToHexStringLower(md5.GetHashAndReset());
    }

    /// <summary>The first FILE of a cue sheet (track 1 on a Redump dump), as written in the sheet.</summary>
    public static string? FirstTrackFile(string cuePath)
    {
        foreach (string raw in File.ReadLines(cuePath))
        {
            string line = raw.Trim();
            if (!line.StartsWith("FILE", StringComparison.OrdinalIgnoreCase) || line.Length < 6 || !char.IsWhiteSpace(line[4]))
                continue;
            string rest = line[5..].TrimStart();
            if (rest.StartsWith('"'))
            {
                int close = rest.IndexOf('"', 1);
                return close > 1 ? rest[1..close] : null;
            }
            int space = rest.LastIndexOf(' ');  // FILE name.bin BINARY
            return space > 0 ? rest[..space].Trim() : rest;
        }
        return null;
    }
}
