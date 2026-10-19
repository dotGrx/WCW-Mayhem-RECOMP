using System;
using System.IO;

namespace WcwLauncher.Core;

/// <summary>
/// Optional home-screen art: art\background.png and art\logo.png next to the launcher (the build
/// copies wcw_launcher\art\ to dist\art\), or the same names straight beside the exe; .jpg works
/// for the background too. Running from dist\, the source art\ one folder up is also looked at,
/// so art dropped there shows without a rebuild. Without a logo the launcher types its title.
/// </summary>
public static class LauncherArt
{
    public static string? Background => Find("background", ".png", ".jpg", ".jpeg");
    public static string? Logo => Find("logo", ".png");

    private static string? Find(string name, params string[] extensions)
    {
        string baseDir = AppContext.BaseDirectory;
        foreach (string dir in new[] { Path.Combine(baseDir, "art"), baseDir, Path.Combine(baseDir, "..", "art") })
        {
            foreach (string ext in extensions)
            {
                string path = Path.Combine(dir, name + ext);
                if (File.Exists(path)) return Path.GetFullPath(path);
            }
        }
        return null;
    }
}
