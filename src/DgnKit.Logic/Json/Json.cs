using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace DgnKit.Logic.Json
{
    /// <summary>
    /// Minimal JSON reader (netstandard2.0 has no built-in JSON). Accepts strict JSON plus <c>//</c> and <c>/* */</c>
    /// comments and trailing commas, so rule files can be annotated. Objects become
    /// <see cref="JsonObject"/> (insertion-ordered), arrays <see cref="List{T}"/> of object?, numbers <see cref="double"/>,
    /// and strings, booleans and null their CLR equivalents.
    /// </summary>
    public static class JsonReader
    {
        public static object? Parse(string text)
        {
            var p = new Parser(text);
            p.SkipWs();
            var v = p.Value();
            p.SkipWs();
            if (!p.End) throw p.Error("unexpected trailing content");
            return v;
        }

        public static object? ParseFile(string path) => Parse(File.ReadAllText(path));

        private sealed class Parser
        {
            private readonly string _s;
            private int _i;
            public Parser(string s) { _s = s; }
            public bool End => _i >= _s.Length;

            public FormatException Error(string msg)
            {
                int line = 1, col = 1;
                for (int k = 0; k < _i && k < _s.Length; k++) { if (_s[k] == '\n') { line++; col = 1; } else col++; }
                return new FormatException($"JSON: {msg} at line {line}, column {col}");
            }

            public void SkipWs()
            {
                while (_i < _s.Length)
                {
                    char c = _s[_i];
                    if (c == ' ' || c == '\t' || c == '\r' || c == '\n' || c == '﻿') { _i++; continue; }
                    if (c == '/' && _i + 1 < _s.Length && _s[_i + 1] == '/')
                    {
                        while (_i < _s.Length && _s[_i] != '\n') _i++;
                        continue;
                    }
                    if (c == '/' && _i + 1 < _s.Length && _s[_i + 1] == '*')
                    {
                        int end = _s.IndexOf("*/", _i + 2, StringComparison.Ordinal);
                        if (end < 0) throw Error("unterminated comment");
                        _i = end + 2;
                        continue;
                    }
                    break;
                }
            }

            public object? Value()
            {
                if (End) throw Error("unexpected end");
                char c = _s[_i];
                switch (c)
                {
                    case '{': return Obj();
                    case '[': return Arr();
                    case '"': return Str();
                    case 't': Lit("true"); return true;
                    case 'f': Lit("false"); return false;
                    case 'n': Lit("null"); return null;
                    default:
                        if (c == '-' || (c >= '0' && c <= '9')) return Num();
                        throw Error($"unexpected '{c}'");
                }
            }

            private void Lit(string w)
            {
                if (string.CompareOrdinal(_s, _i, w, 0, w.Length) != 0) throw Error($"expected {w}");
                _i += w.Length;
            }

            private JsonObject Obj()
            {
                var o = new JsonObject();
                _i++;
                SkipWs();
                while (true)
                {
                    if (End) throw Error("unterminated object");
                    if (_s[_i] == '}') { _i++; return o; }
                    if (_s[_i] != '"') throw Error("expected property name");
                    string k = Str();
                    SkipWs();
                    if (End || _s[_i] != ':') throw Error("expected ':'");
                    _i++;
                    SkipWs();
                    o[k] = Value();
                    SkipWs();
                    if (End) throw Error("unterminated object");
                    if (_s[_i] == ',') { _i++; SkipWs(); continue; }
                    if (_s[_i] == '}') { _i++; return o; }
                    throw Error("expected ',' or '}'");
                }
            }

            private List<object?> Arr()
            {
                var a = new List<object?>();
                _i++;
                SkipWs();
                while (true)
                {
                    if (End) throw Error("unterminated array");
                    if (_s[_i] == ']') { _i++; return a; }
                    a.Add(Value());
                    SkipWs();
                    if (End) throw Error("unterminated array");
                    if (_s[_i] == ',') { _i++; SkipWs(); continue; }
                    if (_s[_i] == ']') { _i++; return a; }
                    throw Error("expected ',' or ']'");
                }
            }

            private string Str()
            {
                _i++;
                var sb = new StringBuilder();
                while (true)
                {
                    if (End) throw Error("unterminated string");
                    char c = _s[_i++];
                    if (c == '"') return sb.ToString();
                    if (c != '\\') { sb.Append(c); continue; }
                    if (End) throw Error("unterminated escape");
                    char e = _s[_i++];
                    switch (e)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            if (_i + 4 > _s.Length) throw Error("bad \\u escape");
                            sb.Append((char)int.Parse(_s.Substring(_i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                            _i += 4;
                            break;
                        default: throw Error($"bad escape \\{e}");
                    }
                }
            }

            private double Num()
            {
                int start = _i;
                if (_s[_i] == '-') _i++;
                while (_i < _s.Length && "0123456789.eE+-".IndexOf(_s[_i]) >= 0) _i++;
                if (!double.TryParse(_s.Substring(start, _i - start), NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                    throw Error("bad number");
                return d;
            }
        }
    }

    /// <summary>Insertion-ordered JSON object with typed accessors.</summary>
    public sealed class JsonObject : Dictionary<string, object?>
    {
        private readonly List<string> _order = new();
        public JsonObject() : base(StringComparer.Ordinal) { }

        public new object? this[string key]
        {
            get => TryGetValue(key, out var v) ? v : null;
            set
            {
                if (!ContainsKey(key)) _order.Add(key);
                base[key] = value;
            }
        }

        public IEnumerable<string> OrderedKeys => _order;

        public string? Str(string key) => this[key] switch { null => null, string s => s, double d => d.ToString("R", CultureInfo.InvariantCulture), bool b => b ? "true" : "false", var o => o.ToString() };
        public double? Num(string key) => this[key] switch { double d => d, string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d, _ => null };
        public bool? Bool(string key) => this[key] as bool?;
        public JsonObject? Obj(string key) => this[key] as JsonObject;
        public List<object?> Arr(string key) => this[key] as List<object?> ?? new List<object?>();

        /// <summary>A string or an array of strings, as a list.</summary>
        public List<string> Strs(string key)
        {
            var v = this[key];
            var r = new List<string>();
            if (v is string s) r.Add(s);
            else if (v is List<object?> a) foreach (var x in a) if (x != null) r.Add(x is string xs ? xs : Convert.ToString(x, CultureInfo.InvariantCulture)!);
            return r;
        }
    }

    /// <summary>Small streaming JSON writer (indented or compact) for reports and exports.</summary>
    public sealed class JsonWriter
    {
        private readonly TextWriter _w;
        private readonly bool _indent;
        private readonly Stack<bool> _first = new();
        private bool _afterName;

        public JsonWriter(TextWriter w, bool indent = true) { _w = w; _indent = indent; }

        private void Sep()
        {
            if (_afterName) { _afterName = false; return; }
            if (_first.Count == 0) return;
            if (!_first.Peek()) _w.Write(',');
            _first.Pop();
            _first.Push(false);
            NewLine();
        }

        private void NewLine()
        {
            if (!_indent) return;
            _w.Write('\n');
            _w.Write(new string(' ', _first.Count * 2));
        }

        public JsonWriter StartObject() { Sep(); _w.Write('{'); _first.Push(true); return this; }
        public JsonWriter EndObject() { bool empty = _first.Pop(); if (!empty) NewLine(); _w.Write('}'); return this; }
        public JsonWriter StartArray() { Sep(); _w.Write('['); _first.Push(true); return this; }
        public JsonWriter EndArray() { bool empty = _first.Pop(); if (!empty) NewLine(); _w.Write(']'); return this; }

        public JsonWriter Name(string name)
        {
            Sep();
            WriteString(name);
            _w.Write(_indent ? ": " : ":");
            _afterName = true;
            return this;
        }

        public JsonWriter Value(object? v)
        {
            Sep();
            WriteRaw(v);
            return this;
        }

        public JsonWriter Prop(string name, object? v) => Name(name).Value(v);

        private void WriteRaw(object? v)
        {
            switch (v)
            {
                case null: _w.Write("null"); break;
                case bool b: _w.Write(b ? "true" : "false"); break;
                case string s: WriteString(s); break;
                case double d: _w.Write(FormatNumber(d)); break;
                case float f: _w.Write(FormatNumber(f)); break;
                case int or long or uint or ulong or short or ushort or byte: _w.Write(Convert.ToString(v, CultureInfo.InvariantCulture)); break;
                case decimal m: _w.Write(m.ToString(CultureInfo.InvariantCulture)); break;
                case IDictionary<string, object?> dict:
                    _w.Write('{'); _first.Push(true);
                    foreach (var kv in dict) Prop(kv.Key, kv.Value);
                    EndObjectNoSep();
                    break;
                case System.Collections.IEnumerable seq:
                    _w.Write('['); _first.Push(true);
                    foreach (var x in seq) Value(x);
                    EndArrayNoSep();
                    break;
                default: WriteString(Convert.ToString(v, CultureInfo.InvariantCulture) ?? ""); break;
            }
        }

        private void EndObjectNoSep() { bool empty = _first.Pop(); if (!empty) NewLine(); _w.Write('}'); }
        private void EndArrayNoSep() { bool empty = _first.Pop(); if (!empty) NewLine(); _w.Write(']'); }

        public static string FormatNumber(double d)
        {
            if (double.IsNaN(d) || double.IsInfinity(d)) return "null";
            if (d == Math.Floor(d) && Math.Abs(d) < 1e15) return ((long)d).ToString(CultureInfo.InvariantCulture);
            return Math.Round(d, 6).ToString("R", CultureInfo.InvariantCulture);
        }

        private void WriteString(string s)
        {
            _w.Write('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': _w.Write("\\\""); break;
                    case '\\': _w.Write("\\\\"); break;
                    case '\n': _w.Write("\\n"); break;
                    case '\r': _w.Write("\\r"); break;
                    case '\t': _w.Write("\\t"); break;
                    default:
                        if (c < 0x20) _w.Write("\\u" + ((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else _w.Write(c);
                        break;
                }
            }
            _w.Write('"');
        }
    }
}
