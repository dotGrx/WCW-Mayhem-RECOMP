using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace WcwLauncher.Core;

public sealed class TomlException(string message) : Exception(message);

/// <summary>A TOML table. Values are string, bool, long, double, List&lt;object&gt; or TomlTable.</summary>
public sealed class TomlTable() : Dictionary<string, object>(StringComparer.Ordinal)
{
    public TomlTable? Table(string key) => TryGetValue(key, out object? v) ? v as TomlTable : null;

    /// <summary>The tables of an array of tables ([[key]]), or none.</summary>
    public IEnumerable<TomlTable> Tables(string key)
    {
        if (TryGetValue(key, out object? v) && v is List<object> list)
        {
            foreach (object item in list)
            {
                if (item is TomlTable t) yield return t;
            }
        }
    }

    public string? String(string key) => TryGetValue(key, out object? v) ? v as string : null;

    public bool? Bool(string key) => TryGetValue(key, out object? v) && v is bool b ? b : null;

    public long? Int(string key) => TryGetValue(key, out object? v) && v is long n ? n : null;

    public double? Double(string key) => TryGetValue(key, out object? v)
        ? v switch { double d => d, long n => n, _ => null }
        : null;

    /// <summary>A scalar as text: strings as they are, booleans as true/false, numbers invariant.</summary>
    public string? Scalar(string key) => TryGetValue(key, out object? v) ? ScalarText(v) : null;

    public static string? ScalarText(object? v) => v switch
    {
        string s => s,
        bool b => b ? "true" : "false",
        long n => n.ToString(CultureInfo.InvariantCulture),
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        _ => null,
    };
}

/// <summary>
/// A small TOML 1.0 reader for the files the game uses (game.toml, settings.toml, mod manifests,
/// mods\state.toml). Strict where the game's own parser (toml11) is strict, so the launcher can
/// check a file before the game reads it: duplicate keys, tables defined twice, bad escapes and
/// stray text are errors.
/// </summary>
public static class Toml
{
    public static TomlTable Parse(string text) => new Parser(text).Document();

    /// <summary>A basic string, escaped the way the game's own writers do (plus control chars).</summary>
    public static string Quote(string value)
    {
        var sb = new StringBuilder(value.Length + 2).Append('"');
        foreach (char c in value)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20 || c == 0x7F) sb.Append($"\\u{(int)c:X4}");
                    else sb.Append(c);
                    break;
            }
        }
        return sb.Append('"').ToString();
    }

    public static bool IsBareKey(string key)
    {
        if (key.Length == 0) return false;
        foreach (char c in key)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c == '_' || c == '-')) return false;
        }
        return true;
    }

    public static string Key(string key) => IsBareKey(key) ? key : Quote(key);

    private sealed class Parser(string text)
    {
        private readonly string _s = text;
        private int _p;
        private readonly TomlTable _root = new();
        private readonly HashSet<TomlTable> _defined = new(ReferenceEqualityComparer.Instance);
        private TomlTable _current = null!;

        private bool Eof => _p >= _s.Length;
        private char Peek(int ahead = 0) => _p + ahead < _s.Length ? _s[_p + ahead] : '\0';

        public TomlTable Document()
        {
            if (_s.Length > 0 && _s[0] == '﻿') _p = 1;
            _current = _root;
            while (true)
            {
                SkipBlankLines();
                if (Eof) break;
                if (Peek() == '[') Header();
                else KeyValue(_current);
                EndOfLine();
            }
            return _root;
        }

        private TomlException Error(string what)
        {
            int line = 1;
            for (int i = 0; i < Math.Min(_p, _s.Length); i++)
            {
                if (_s[i] == '\n') line++;
            }
            return new TomlException($"line {line}: {what}");
        }

        private void SkipSpaces()
        {
            while (!Eof && (Peek() == ' ' || Peek() == '\t')) _p++;
        }

        private void SkipComment()
        {
            if (Peek() != '#') return;
            while (!Eof && Peek() != '\n') _p++;
        }

        private void SkipBlankLines()
        {
            while (!Eof)
            {
                char c = Peek();
                if (c is ' ' or '\t' or '\r' or '\n') _p++;
                else if (c == '#') SkipComment();
                else break;
            }
        }

        private void EndOfLine()
        {
            SkipSpaces();
            SkipComment();
            if (Eof) return;
            if (Peek() == '\r' && Peek(1) == '\n') { _p += 2; return; }
            if (Peek() == '\n') { _p++; return; }
            throw Error($"unexpected '{Peek()}' after a value");
        }

        private void Header()
        {
            bool array = Peek(1) == '[';
            _p += array ? 2 : 1;
            SkipSpaces();
            List<string> keys = KeyPath();
            SkipSpaces();
            if (array)
            {
                if (Peek() != ']' || Peek(1) != ']') throw Error("expected ']]'");
                _p += 2;
            }
            else
            {
                if (Peek() != ']') throw Error("expected ']'");
                _p++;
            }

            TomlTable t = _root;
            for (int i = 0; i < keys.Count - 1; i++) t = Descend(t, keys[i]);
            string last = keys[^1];
            if (array)
            {
                if (!t.TryGetValue(last, out object? v))
                {
                    v = new List<object>();
                    t[last] = v;
                }
                if (v is not List<object> list || (list.Count > 0 && list[0] is not TomlTable))
                    throw Error($"'{last}' is not an array of tables");
                var table = new TomlTable();
                list.Add(table);
                _current = table;
            }
            else
            {
                if (t.TryGetValue(last, out object? v))
                {
                    if (v is not TomlTable existing) throw Error($"'{last}' is already a value");
                    if (_defined.Contains(existing)) throw Error($"table [{string.Join('.', keys)}] is defined twice");
                    _current = existing;
                }
                else
                {
                    _current = new TomlTable();
                    t[last] = _current;
                }
            }
            _defined.Add(_current);
        }

        private TomlTable Descend(TomlTable t, string key)
        {
            if (!t.TryGetValue(key, out object? v))
            {
                var created = new TomlTable();
                t[key] = created;
                return created;
            }
            return v switch
            {
                TomlTable table => table,
                List<object> { Count: > 0 } list when list[^1] is TomlTable last => last,
                _ => throw Error($"'{key}' is not a table"),
            };
        }

        private List<string> KeyPath()
        {
            var keys = new List<string> { SingleKey() };
            while (true)
            {
                SkipSpaces();
                if (Peek() != '.') break;
                _p++;
                SkipSpaces();
                keys.Add(SingleKey());
            }
            return keys;
        }

        private string SingleKey()
        {
            char c = Peek();
            if (c == '"') return BasicString();
            if (c == '\'') return LiteralString();
            int start = _p;
            while (!Eof && (char.IsAsciiLetterOrDigit(Peek()) || Peek() == '_' || Peek() == '-')) _p++;
            if (_p == start) throw Error(Eof ? "unexpected end of file" : $"unexpected '{c}' where a key belongs");
            return _s[start.._p];
        }

        private void KeyValue(TomlTable table)
        {
            List<string> keys = KeyPath();
            SkipSpaces();
            if (Peek() != '=') throw Error($"expected '=' after '{string.Join('.', keys)}'");
            _p++;
            SkipSpaces();
            object value = Value();
            TomlTable t = table;
            for (int i = 0; i < keys.Count - 1; i++) t = Descend(t, keys[i]);
            if (t.ContainsKey(keys[^1])) throw Error($"duplicate key '{string.Join('.', keys)}'");
            t[keys[^1]] = value;
        }

        private object Value()
        {
            char c = Peek();
            switch (c)
            {
                case '"':
                    return Peek(1) == '"' && Peek(2) == '"' ? MultiLineBasic() : BasicString();
                case '\'':
                    return Peek(1) == '\'' && Peek(2) == '\'' ? MultiLineLiteral() : LiteralString();
                case '[':
                    return Array();
                case '{':
                    return InlineTable();
            }
            if (Word("true")) return true;
            if (Word("false")) return false;
            return NumberOrDate();
        }

        private bool Word(string word)
        {
            if (string.CompareOrdinal(_s, _p, word, 0, word.Length) != 0) return false;
            char after = Peek(word.Length);
            if (char.IsAsciiLetterOrDigit(after) || after == '_' || after == '-') return false;
            _p += word.Length;
            return true;
        }

        private object NumberOrDate()
        {
            int start = _p;
            while (!Eof)
            {
                char c = Peek();
                if (c is ' ' or '\t' or '\r' or '\n' or ',' or ']' or '}' or '#') break;
                _p++;
            }
            string token = _s[start.._p];
            if (token.Length == 0) throw Error(Eof ? "missing value" : $"unexpected '{Peek()}' where a value belongs");
            string n = token.Replace("_", "");
            if (n is "inf" or "+inf") return double.PositiveInfinity;
            if (n == "-inf") return double.NegativeInfinity;
            if (n is "nan" or "+nan" or "-nan") return double.NaN;
            try
            {
                if (n.StartsWith("0x")) return Convert.ToInt64(n[2..], 16);
                if (n.StartsWith("0o")) return Convert.ToInt64(n[2..], 8);
                if (n.StartsWith("0b")) return Convert.ToInt64(n[2..], 2);
            }
            catch (Exception)
            {
                throw Error($"bad number '{token}'");
            }
            if (long.TryParse(n, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long l)) return l;
            if (n.IndexOfAny(['.', 'e', 'E']) >= 0 &&
                double.TryParse(n, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)) return d;
            // Dates and times are kept as their text; nothing the launcher reads uses them.
            if (char.IsAsciiDigit(n[0]) && (n.Contains('-') || n.Contains(':'))) return token;
            throw Error($"unexpected value '{token}'");
        }

        private List<object> Array()
        {
            _p++;  // [
            var list = new List<object>();
            while (true)
            {
                SkipBlankLines();
                if (Peek() == ']') { _p++; return list; }
                if (Eof) throw Error("unterminated array");
                list.Add(Value());
                SkipBlankLines();
                if (Peek() == ',') { _p++; continue; }
                if (Peek() == ']') { _p++; return list; }
                throw Error("expected ',' or ']' in an array");
            }
        }

        private TomlTable InlineTable()
        {
            _p++;  // {
            var table = new TomlTable();
            SkipSpaces();
            if (Peek() == '}') { _p++; return table; }
            while (true)
            {
                SkipSpaces();
                KeyValue(table);
                SkipSpaces();
                if (Peek() == ',') { _p++; continue; }
                if (Peek() == '}') { _p++; return table; }
                throw Error("expected ',' or '}' in an inline table");
            }
        }

        private string BasicString()
        {
            _p++;  // "
            var sb = new StringBuilder();
            while (true)
            {
                if (Eof || Peek() == '\n') throw Error("unterminated string");
                char c = _s[_p++];
                if (c == '"') return sb.ToString();
                if (c == '\\') Escape(sb);
                else sb.Append(c);
            }
        }

        private void Escape(StringBuilder sb)
        {
            if (Eof) throw Error("unterminated string");
            char e = _s[_p++];
            switch (e)
            {
                case 'b': sb.Append('\b'); break;
                case 't': sb.Append('\t'); break;
                case 'n': sb.Append('\n'); break;
                case 'f': sb.Append('\f'); break;
                case 'r': sb.Append('\r'); break;
                case '"': sb.Append('"'); break;
                case '\\': sb.Append('\\'); break;
                case 'u':
                case 'U':
                    int len = e == 'u' ? 4 : 8;
                    if (_p + len > _s.Length ||
                        !int.TryParse(_s.AsSpan(_p, len), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int cp))
                        throw Error("bad unicode escape");
                    sb.Append(char.ConvertFromUtf32(cp));
                    _p += len;
                    break;
                default:
                    throw Error($"invalid escape '\\{e}' (write a backslash as \\\\ or use a 'literal string')");
            }
        }

        private string LiteralString()
        {
            _p++;  // '
            int start = _p;
            while (!Eof && Peek() != '\'' && Peek() != '\n') _p++;
            if (Peek() != '\'') throw Error("unterminated string");
            string value = _s[start.._p];
            _p++;
            return value;
        }

        private string MultiLineBasic()
        {
            _p += 3;
            SkipFirstNewline();
            var sb = new StringBuilder();
            while (true)
            {
                if (Eof) throw Error("unterminated string");
                if (Peek() == '"' && Peek(1) == '"' && Peek(2) == '"')
                {
                    _p += 3;
                    // Up to two quotes may sit right before the closing delimiter.
                    while (Peek() == '"') { sb.Append('"'); _p++; }
                    return sb.ToString();
                }
                char c = _s[_p++];
                if (c != '\\') { sb.Append(c); continue; }
                // Line-ending backslash: drop it and the whitespace that follows.
                int save = _p;
                while (!Eof && (Peek() == ' ' || Peek() == '\t')) _p++;
                if (Peek() == '\n' || (Peek() == '\r' && Peek(1) == '\n'))
                {
                    while (!Eof && (Peek() is ' ' or '\t' or '\r' or '\n')) _p++;
                    continue;
                }
                _p = save;
                Escape(sb);
            }
        }

        private string MultiLineLiteral()
        {
            _p += 3;
            SkipFirstNewline();
            int start = _p;
            int end = _s.IndexOf("'''", _p, StringComparison.Ordinal);
            if (end < 0) throw Error("unterminated string");
            _p = end + 3;
            while (Peek() == '\'') { _p++; end++; }
            return _s[start..end];
        }

        private void SkipFirstNewline()
        {
            if (Peek() == '\n') _p++;
            else if (Peek() == '\r' && Peek(1) == '\n') _p += 2;
        }
    }
}
