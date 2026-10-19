using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace WcwLauncher.Core;

/// <summary>
/// A TOML file edited line by line: the launcher changes only the keys it owns, inside their
/// [section], and every other line (comments, blank lines, keys it doesn't know, other
/// sections) stays exactly as it was.
/// </summary>
public sealed class TomlLines
{
    private readonly List<string> _lines;
    private readonly string _newline;
    private readonly string _original;

    private TomlLines(string path, string text, bool exists)
    {
        Path = path;
        Exists = exists;
        _original = text;
        _newline = text.Contains("\r\n") ? "\r\n" : "\n";
        string body = text.Length > 0 && text[0] == '﻿' ? text[1..] : text;
        _lines = new List<string>(body.Replace("\r\n", "\n").Split('\n'));
        if (_lines.Count > 0 && _lines[^1].Length == 0) _lines.RemoveAt(_lines.Count - 1);
        try
        {
            Toml.Parse(text);
        }
        catch (TomlException e)
        {
            SyntaxError = e.Message;
        }
    }

    public string Path { get; }
    public bool Exists { get; }

    /// <summary>Set when the file on disk isn't valid TOML (the game then ignores all of it).</summary>
    public string? SyntaxError { get; }

    /// <summary>Loads the file, or starts a new one with the given header comment lines.</summary>
    public static TomlLines Load(string path, string newFileHeader)
    {
        if (File.Exists(path)) return new TomlLines(path, File.ReadAllText(path), true);
        return new TomlLines(path, newFileHeader, false);
    }

    public string Text => string.Join(_newline, _lines) + _newline;

    public bool Changed => Text != _original;

    /// <summary>
    /// Writes atomically (temp file + move) so the game never reads half a file. A file that was
    /// valid TOML is checked again before it is written, so a save can't break it.
    /// </summary>
    public void Save()
    {
        string text = Text;
        if (SyntaxError == null) Toml.Parse(text);  // throws TomlException rather than write a broken file
        string? dir = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        string temp = Path + ".tmp";
        File.WriteAllText(temp, text, new UTF8Encoding(false));
        File.Move(temp, Path, overwrite: true);
    }

    // --- Reading -------------------------------------------------------------------------------

    /// <summary>The value's TOML text (comment stripped), or null when the key isn't set.</summary>
    public string? GetRaw(string section, string key)
    {
        Entry? e = Find(section, key);
        if (e == null) return null;
        Entry entry = e.Value;
        string first = _lines[entry.Line][entry.ValueStart..];
        if (entry.LineCount == 1) return StripComment(first).Trim();
        var sb = new StringBuilder(first);
        for (int i = 1; i < entry.LineCount; i++) sb.Append('\n').Append(_lines[entry.Line + i]);
        return sb.ToString().Trim();
    }

    /// <summary>The value parsed as TOML (string, bool, long, double, ...), or null.</summary>
    public object? Get(string section, string key)
    {
        string? raw = GetRaw(section, key);
        if (raw == null) return null;
        try
        {
            return Toml.Parse("v = " + raw).TryGetValue("v", out object? v) ? v : null;
        }
        catch (TomlException)
        {
            return null;
        }
    }

    public string? GetString(string section, string key) => Get(section, key) as string;
    public bool? GetBool(string section, string key) => Get(section, key) is bool b ? b : null;
    public long? GetInt(string section, string key) => Get(section, key) is long n ? n : null;

    public double? GetDouble(string section, string key) => Get(section, key) switch
    {
        double d => d,
        long n => n,
        _ => null,
    };

    // --- Writing -------------------------------------------------------------------------------

    /// <summary>Sets section.key to a TOML value (already formatted: "\"x\"", "true", "4", "0.5").</summary>
    public void Set(string section, string key, string tomlValue)
    {
        Entry? found = Find(section, key);
        if (found is { } e)
        {
            string line = _lines[e.Line];
            string prefix = line[..e.ValueStart];
            if (!prefix.EndsWith(' ') && !prefix.EndsWith('\t')) prefix += " ";
            string? comment = e.LineCount == 1 ? CommentOf(line[e.ValueStart..]) : null;
            _lines[e.Line] = prefix + tomlValue + (comment != null ? "  " + comment : "");
            if (e.LineCount > 1) _lines.RemoveRange(e.Line + 1, e.LineCount - 1);
            return;
        }

        string newLine = $"{Toml.Key(key)} = {tomlValue}";
        int header = FindHeader(section);
        if (header < 0 && section.Length > 0)
        {
            // New section at the end of the file, after a blank line.
            while (_lines.Count > 0 && string.IsNullOrWhiteSpace(_lines[^1])) _lines.RemoveAt(_lines.Count - 1);
            if (_lines.Count > 0) _lines.Add("");
            _lines.Add($"[{section}]");
            _lines.Add(newLine);
            return;
        }

        // After the section's last key line (before trailing blanks/comments and the next header).
        int start = header + 1;
        int end = start;
        while (end < _lines.Count && !IsHeader(_lines[end])) end++;
        int insert = end;
        while (insert > start && IsBlankOrComment(_lines[insert - 1])) insert--;
        _lines.Insert(insert, newLine);
    }

    public bool Remove(string section, string key)
    {
        if (Find(section, key) is not { } e) return false;
        _lines.RemoveRange(e.Line, e.LineCount);
        return true;
    }

    // --- Scanning ------------------------------------------------------------------------------

    private readonly record struct Entry(int Line, int ValueStart, int LineCount);

    private Entry? Find(string section, string key)
    {
        string current = "";
        for (int i = 0; i < _lines.Count; i++)
        {
            string line = _lines[i];
            string trimmed = line.TrimStart();
            if (trimmed.Length == 0 || trimmed[0] == '#') continue;
            if (trimmed[0] == '[')
            {
                current = HeaderName(trimmed) ?? "\0";
                continue;
            }
            int eq = KeyEnd(line, out string? name);
            if (eq < 0) continue;
            int valueStart = eq + 1;
            int count = ValueLineCount(i, valueStart);
            if (current == section && name == key) return new Entry(i, valueStart, count);
            i += count - 1;
        }
        return null;
    }

    private int FindHeader(string section)
    {
        if (section.Length == 0) return -1;
        for (int i = 0; i < _lines.Count; i++)
        {
            string trimmed = _lines[i].TrimStart();
            if (trimmed.StartsWith('[') && HeaderName(trimmed) == section) return i;
        }
        return -1;
    }

    private static bool IsHeader(string line) => line.TrimStart().StartsWith('[');

    private static bool IsBlankOrComment(string line)
    {
        string t = line.TrimStart();
        return t.Length == 0 || t[0] == '#';
    }

    /// <summary>"[video]" -> "video"; "[ a . b ]" -> "a.b"; arrays of tables get a "[[" prefix so
    /// they never match a plain section name.</summary>
    private static string? HeaderName(string trimmed)
    {
        bool array = trimmed.StartsWith("[[");
        int open = array ? 2 : 1;
        int close = trimmed.IndexOf(array ? "]]" : "]", open, StringComparison.Ordinal);
        if (close < 0) return null;
        var parts = trimmed[open..close].Split('.');
        for (int i = 0; i < parts.Length; i++) parts[i] = Unquote(parts[i].Trim());
        string name = string.Join('.', parts);
        return array ? "[[" + name : name;
    }

    private static string Unquote(string key)
    {
        if (key.Length >= 2 && ((key[0] == '"' && key[^1] == '"') || (key[0] == '\'' && key[^1] == '\'')))
            return key[1..^1];
        return key;
    }

    /// <summary>Index of the '=' of a "key = value" line (and the key), or -1.</summary>
    private static int KeyEnd(string line, out string? name)
    {
        name = null;
        bool inBasic = false, inLiteral = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (inBasic)
            {
                if (c == '\\') i++;
                else if (c == '"') inBasic = false;
                continue;
            }
            if (inLiteral)
            {
                if (c == '\'') inLiteral = false;
                continue;
            }
            if (c == '"') inBasic = true;
            else if (c == '\'') inLiteral = true;
            else if (c == '#') return -1;
            else if (c == '=')
            {
                name = Unquote(line[..i].Trim());
                return name.Length > 0 ? i : -1;
            }
        }
        return -1;
    }

    /// <summary>How many lines a value spans (multi-line arrays and strings).</summary>
    private int ValueLineCount(int line, int valueStart)
    {
        int depth = 0;
        string? openTriple = null;
        bool started = false;
        for (int i = line; i < _lines.Count; i++)
        {
            string text = i == line ? _lines[i][valueStart..] : _lines[i];
            for (int j = 0; j < text.Length; j++)
            {
                char c = text[j];
                if (openTriple != null)
                {
                    if (string.CompareOrdinal(text, j, openTriple, 0, 3) == 0) { openTriple = null; j += 2; }
                    else if (openTriple == "\"\"\"" && c == '\\') j++;
                    continue;
                }
                if (c == '#') break;
                if (c is '"' or '\'')
                {
                    string q = new(c, 3);
                    if (string.CompareOrdinal(text, j, q, 0, 3) == 0) { openTriple = q; j += 2; continue; }
                    // Single-line string: skip to its end.
                    for (j++; j < text.Length; j++)
                    {
                        if (c == '"' && text[j] == '\\') { j++; continue; }
                        if (text[j] == c) break;
                    }
                    continue;
                }
                if (c is '[' or '{') { depth++; started = true; }
                else if (c is ']' or '}') depth--;
            }
            if (openTriple == null && (depth <= 0 || !started)) return i - line + 1;
        }
        return _lines.Count - line;
    }

    private static string StripComment(string value)
    {
        int at = CommentIndex(value);
        return at < 0 ? value : value[..at];
    }

    private static string? CommentOf(string value)
    {
        int at = CommentIndex(value);
        return at < 0 ? null : value[at..].TrimEnd();
    }

    private static int CommentIndex(string value)
    {
        bool inBasic = false, inLiteral = false;
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (inBasic)
            {
                if (c == '\\') i++;
                else if (c == '"') inBasic = false;
            }
            else if (inLiteral)
            {
                if (c == '\'') inLiteral = false;
            }
            else if (c == '"') inBasic = true;
            else if (c == '\'') inLiteral = true;
            else if (c == '#') return i;
        }
        return -1;
    }
}
