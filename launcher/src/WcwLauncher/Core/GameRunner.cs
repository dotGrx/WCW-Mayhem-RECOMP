using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace WcwLauncher.Core;

/// <param name="Crashed">The game died (crash report reason other than a normal exit, or a Windows exception code).</param>
/// <param name="Reason">The crash report's reason ("seh", "signal_11", a fatal message), when there is one.</param>
/// <param name="LastError">The last error line the game printed, for a non-zero exit without a crash.</param>
/// <param name="WasForeground">The player was in the game window when it closed.</param>
public sealed record SessionResult(int ExitCode, TimeSpan Duration, bool Crashed, string? Reason, string? LastError,
    bool WasForeground);

/// <summary>
/// Starts WCWMayhem_Recompiled.exe on the chosen disc and reports how the session ended. The
/// runtime writes no log file of its own (only stdout/stderr, plus psx_last_run_report.json in
/// its working folder at every exit), so the launcher saves the game's output to
/// logs\last-run.log next to itself and copies the exit report beside it.
/// </summary>
public static partial class GameRunner
{
    public const string ReportName = "psx_last_run_report.json";

    /// <summary>logs\ next to the launcher, or under %LOCALAPPDATA% when that folder is read-only.</summary>
    public static string LogsFolder
    {
        get
        {
            string folder = Path.Combine(AppContext.BaseDirectory, "logs");
            try
            {
                Directory.CreateDirectory(folder);
                string probe = Path.Combine(folder, ".write-test");
                File.WriteAllText(probe, "");
                File.Delete(probe);
                return folder;
            }
            catch (Exception)
            {
                string fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "WCWMayhemLauncher", "logs");
                Directory.CreateDirectory(fallback);
                return fallback;
            }
        }
    }

    /// <summary>
    /// The command line: --no-launcher skips the game's own launcher window, --disc mounts the
    /// image (the command line outranks settings.toml). Everything else the game reads from
    /// settings.toml and mods\state.toml next to it.
    /// </summary>
    public static ProcessStartInfo StartInfo(string gameExe, string discPath)
    {
        var info = new ProcessStartInfo(gameExe)
        {
            WorkingDirectory = Path.GetDirectoryName(gameExe)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        info.ArgumentList.Add("--no-launcher");
        info.ArgumentList.Add("--disc");
        info.ArgumentList.Add(discPath);
        return info;
    }

    public static string Describe(ProcessStartInfo info)
    {
        var sb = new StringBuilder(Quote(info.FileName));
        foreach (string a in info.ArgumentList) sb.Append(' ').Append(Quote(a));
        return sb.ToString();
    }

    private static string Quote(string s) => s.Contains(' ') || s.Length == 0 ? $"\"{s}\"" : s;

    public static async Task<SessionResult> RunAsync(string gameExe, string discPath, string? note = null)
    {
        string logs = LogsFolder;
        string log = Path.Combine(logs, "last-run.log");
        string reportCopy = Path.Combine(logs, "last-run-report.json");
        string gameDir = Path.GetDirectoryName(gameExe)!;
        string report = Path.Combine(gameDir, ReportName);
        // Keep the previous session's output for comparison.
        try
        {
            if (File.Exists(log)) File.Move(log, Path.Combine(logs, "previous-run.log"), overwrite: true);
            if (File.Exists(reportCopy)) File.Delete(reportCopy);
        }
        catch (IOException)
        {
        }

        DateTime started = DateTime.Now;
        ProcessStartInfo info = StartInfo(gameExe, discPath);
        var gate = new object();
        var recentErrors = new Queue<string>();
        using var writer = new StreamWriter(log, append: false, new UTF8Encoding(false)) { AutoFlush = true };
        writer.WriteLine($"# WCW Mayhem launcher: game started {started:yyyy-MM-dd HH:mm:ss}");
        writer.WriteLine($"# {Describe(info)}");
        writer.WriteLine($"# working folder: {gameDir}");
        if (note != null) writer.WriteLine($"# {note}");

        bool closed = false;
        using var process = new Process { StartInfo = info };
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            lock (gate)
            {
                if (!closed) writer.WriteLine(e.Data);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            lock (gate)
            {
                if (closed) return;
                writer.WriteLine(e.Data);
                if (e.Data.Trim().Length > 0)
                {
                    recentErrors.Enqueue(e.Data.Trim());
                    if (recentErrors.Count > 40) recentErrors.Dequeue();
                }
            }
        };
        if (!process.Start()) throw new InvalidOperationException("The game did not start.");
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        // Sample which app the player is using, so the launcher only takes focus back if they
        // were in the game when it closed (and never jumps in front of something else).
        bool wasForeground = true;
        var exited = process.WaitForExitAsync();
        while (await Task.WhenAny(exited, Task.Delay(500)) != exited)
        {
            wasForeground = ForegroundProcessId() == (uint)process.Id;
        }
        await exited;
        TimeSpan duration = DateTime.Now - started;
        int code = process.ExitCode;

        // The runtime's exit report: reason "atexit" is a normal exit; anything else (seh,
        // signal_N, a fatal message) is a crash. Written on every exit, so only a fresh one counts.
        string? reason = null;
        if (File.Exists(report) && File.GetLastWriteTime(report) >= started.AddSeconds(-2))
        {
            try
            {
                string head = ReadHead(report, 4096);
                reason = ReasonRegex().Match(head) is { Success: true } m ? Regex.Unescape(m.Groups[1].Value) : null;
                File.Copy(report, reportCopy, overwrite: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
            {
            }
        }
        string? freezeDump = Directory.EnumerateFiles(gameDir, "psx_freeze_dump_*.json")
            .Where(f => File.GetLastWriteTime(f) >= started)
            .OrderByDescending(File.GetLastWriteTime).FirstOrDefault();
        if (freezeDump != null)
        {
            try
            {
                File.Copy(freezeDump, Path.Combine(logs, Path.GetFileName(freezeDump)), overwrite: true);
            }
            catch (IOException)
            {
            }
        }

        bool windowsException = unchecked((uint)code) >= 0xC0000000u;
        bool crashed = windowsException || (reason != null && reason != "atexit");
        string? lastError = null;
        lock (gate)
        {
            if (code != 0 && !crashed)
            {
                lastError = recentErrors.Reverse().FirstOrDefault(l =>
                                l.Contains("cannot", StringComparison.OrdinalIgnoreCase) ||
                                l.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
                                l.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                                l.Contains("no disc", StringComparison.OrdinalIgnoreCase))
                            ?? recentErrors.LastOrDefault();
            }
            writer.WriteLine();
            writer.WriteLine($"# game exited after {duration:hh\\:mm\\:ss}, exit code {code} (0x{unchecked((uint)code):X8})" +
                             (reason != null ? $", exit report reason \"{reason}\"" : "") +
                             (freezeDump != null ? $", freeze dump {Path.GetFileName(freezeDump)}" : ""));
            closed = true;
        }
        return new SessionResult(code, duration, crashed, reason, lastError, wasForeground);
    }

    private static string ReadHead(string path, int bytes)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var buffer = new byte[Math.Min(bytes, (int)Math.Max(0, stream.Length))];
        int n = stream.Read(buffer, 0, buffer.Length);
        return Encoding.UTF8.GetString(buffer, 0, n);
    }

    [GeneratedRegex("\"reason\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"")]
    private static partial Regex ReasonRegex();

    public static void OpenFolder(string folder)
    {
        Directory.CreateDirectory(folder);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
    }

    private static uint ForegroundProcessId()
    {
        IntPtr window = GetForegroundWindow();
        if (window == IntPtr.Zero) return 0;
        GetWindowThreadProcessId(window, out uint pid);
        return pid;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
}
