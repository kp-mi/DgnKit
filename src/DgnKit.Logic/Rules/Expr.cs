using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace DgnKit.Logic.Rules
{
    /// <summary>An object whose members an expression can read (<c>subject.diameter</c>).</summary>
    public interface IExprEntity
    {
        /// <summary>Returns the member value, or null when absent.</summary>
        object? Member(string name);
        /// <summary>True when the entity is (a subclass of) the named class.</summary>
        bool IsA(string className);
    }

    /// <summary>Variables and functions visible to an expression.</summary>
    public interface IExprScope
    {
        bool TryGet(string name, out object? value);
    }

    public sealed class DictScope : IExprScope
    {
        private readonly Dictionary<string, object?> _vars = new(StringComparer.OrdinalIgnoreCase);
        private readonly IExprScope? _parent;
        public DictScope(IExprScope? parent = null) { _parent = parent; }
        public DictScope Set(string name, object? value) { _vars[name] = value; return this; }
        public bool TryGet(string name, out object? value)
        {
            if (_vars.TryGetValue(name, out value)) return true;
            if (_parent != null) return _parent.TryGet(name, out value);
            value = null;
            return false;
        }
    }

    /// <summary>
    /// The rule expression language: a small, side-effect-free, total language over numbers, strings, booleans, null,
    /// lists and entities. Grammar (lowest precedence first):
    /// <code>
    ///   cond ? a : b      or / ||      and / &amp;&amp;      not / !
    ///   == != &lt; &lt;= &gt; &gt;=   ~ (regex match, case-insensitive)   !~   in
    ///   + - (numbers; + concatenates strings)   * / %   unary -
    ///   a.b   a["prop name"]   f(x, y)   [1, 2]   'str' "str"   12.5   true false null
    /// </code>
    /// Missing members evaluate to null; comparisons with null are false (except == / !=); arithmetic with null gives
    /// null. Errors never throw at evaluation time — a rule simply does not fire.
    /// </summary>
    public sealed class Expr
    {
        private readonly Func<IExprScope, object?> _eval;
        public string Source { get; }

        private Expr(string source, Func<IExprScope, object?> eval) { Source = source; _eval = eval; }

        public static Expr Parse(string source) => new(source, new Parser(source).ParseAll());

        public static readonly Expr True = new("true", _ => true);

        public object? Evaluate(IExprScope scope)
        {
            try { return _eval(scope); }
            catch (Exception) { return null; }
        }

        public bool Test(IExprScope scope) => Truthy(Evaluate(scope));

        public double? Number(IExprScope scope) => ToNumber(Evaluate(scope));

        public override string ToString() => Source;

        // ---------------------------------------------------------------- value semantics

        public static bool Truthy(object? v) => v switch
        {
            null => false,
            bool b => b,
            double d => d != 0 && !double.IsNaN(d),
            string s => s.Length > 0,
            System.Collections.ICollection c => c.Count > 0,
            _ => true,
        };

        public static double? ToNumber(object? v) => v switch
        {
            null => null,
            double d => d,
            float f => f,
            int i => i,
            long l => l,
            uint u => u,
            ulong ul => ul,
            short sh => sh,
            byte by => by,
            decimal m => (double)m,
            bool b => b ? 1 : 0,
            string s => ParseNumber(s),
            _ => null,
        };

        /// <summary>Parses "300", "300 mm", "12.5in", "8\"" leniently (leading number; unit suffix ignored).</summary>
        public static double? ParseNumber(string s)
        {
            s = s.Trim();
            if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return d;
            var m = LeadingNumber.Match(s);
            if (m.Success && double.TryParse(m.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return d;
            return null;
        }

        private static readonly Regex LeadingNumber = new(@"^[-+]?(\d+\.?\d*|\.\d+)([eE][-+]?\d+)?", RegexOptions.Compiled);

        public static string? ToStr(object? v) => v switch
        {
            null => null,
            string s => s,
            double d => d.ToString("R", CultureInfo.InvariantCulture),
            bool b => b ? "true" : "false",
            IExprEntity e => e.Member("key") as string ?? e.ToString(),
            _ => Convert.ToString(v, CultureInfo.InvariantCulture),
        };

        private static bool IsNumeric(object? v) => v is double or float or int or long or uint or ulong or short or byte or decimal;

        public static bool ValuesEqual(object? a, object? b)
        {
            if (a == null || b == null) return a == null && b == null;
            if (a is bool ba && b is bool bb) return ba == bb;
            if (IsNumeric(a) || IsNumeric(b))
            {
                var x = ToNumber(a); var y = ToNumber(b);
                if (x != null && y != null) return Math.Abs(x.Value - y.Value) <= 1e-9 * Math.Max(1, Math.Abs(x.Value));
            }
            if (a is IExprEntity || b is IExprEntity) return ReferenceEquals(a, b);
            return string.Equals(ToStr(a), ToStr(b), StringComparison.OrdinalIgnoreCase);
        }

        private static int? Compare(object? a, object? b)
        {
            if (a == null || b == null) return null;
            var x = ToNumber(a); var y = ToNumber(b);
            if (x != null && y != null) return x.Value.CompareTo(y.Value);
            var sa = ToStr(a); var sb = ToStr(b);
            if (sa == null || sb == null) return null;
            return string.Compare(sa, sb, StringComparison.OrdinalIgnoreCase);
        }

        private static readonly Dictionary<string, Regex> RegexCache = new();

        public static Regex GetRegex(string pattern)
        {
            lock (RegexCache)
            {
                if (!RegexCache.TryGetValue(pattern, out var re))
                {
                    re = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                    RegexCache[pattern] = re;
                }
                return re;
            }
        }

        private static object? Member(object? target, string name)
        {
            switch (target)
            {
                case null: return null;
                case IExprEntity e: return e.Member(name);
                case IDictionary<string, object?> d:
                    if (d.TryGetValue(name, out var v)) return v;
                    foreach (var kv in d) if (string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase)) return kv.Value;
                    return null;
                case string s when name.Equals("length", StringComparison.OrdinalIgnoreCase): return (double)s.Length;
                case System.Collections.ICollection c when name.Equals("count", StringComparison.OrdinalIgnoreCase): return (double)c.Count;
                default: return null;
            }
        }

        // ---------------------------------------------------------------- built-in functions

        private static object? Call(string fn, object?[] a, IExprScope scope)
        {
            switch (fn.ToLowerInvariant())
            {
                case "lower": return ToStr(Arg(a, 0))?.ToLowerInvariant();
                case "upper": return ToStr(Arg(a, 0))?.ToUpperInvariant();
                case "trim": return ToStr(Arg(a, 0))?.Trim();
                case "str": return ToStr(Arg(a, 0));
                case "number": return ToNumber(Arg(a, 0));
                case "len":
                case "length":
                {
                    var v = Arg(a, 0);
                    if (v is string s) return (double)s.Length;
                    if (v is System.Collections.ICollection c) return (double)c.Count;
                    if (v is IExprEntity e) return e.Member("length");
                    return null;
                }
                case "abs": return ToNumber(Arg(a, 0)) is double d ? Math.Abs(d) : null;
                case "sqrt": return ToNumber(Arg(a, 0)) is double d2 && d2 >= 0 ? Math.Sqrt(d2) : null;
                case "pow": return ToNumber(Arg(a, 0)) is double pb && ToNumber(Arg(a, 1)) is double pe ? Math.Pow(pb, pe) : null;
                case "round":
                {
                    var x = ToNumber(Arg(a, 0));
                    int digits = (int)(ToNumber(Arg(a, 1)) ?? 0);
                    return x is double xv ? Math.Round(xv, Math.Max(0, Math.Min(12, digits))) : null;
                }
                case "min": { var ns = a.Select(ToNumber).Where(n => n != null).ToList(); return ns.Count > 0 ? ns.Min() : null; }
                case "max": { var ns = a.Select(ToNumber).Where(n => n != null).ToList(); return ns.Count > 0 ? ns.Max() : null; }
                case "exists":
                case "has": return Arg(a, 0) != null;
                case "coalesce": return a.FirstOrDefault(x => x != null);
                case "if": return Truthy(Arg(a, 0)) ? Arg(a, 1) : Arg(a, 2);
                case "matches":
                {
                    var s = ToStr(Arg(a, 0)); var p = ToStr(Arg(a, 1));
                    return s != null && p != null && GetRegex(p).IsMatch(s);
                }
                case "capture":
                {
                    // capture(text, regex, group=1): the captured group, or null
                    var s = ToStr(Arg(a, 0)); var p = ToStr(Arg(a, 1));
                    if (s == null || p == null) return null;
                    var m = GetRegex(p).Match(s);
                    if (!m.Success) return null;
                    int g = (int)(ToNumber(Arg(a, 2)) ?? 1);
                    return g < m.Groups.Count ? m.Groups[g].Value : null;
                }
                case "contains":
                {
                    var hay = Arg(a, 0); var needle = Arg(a, 1);
                    if (hay is System.Collections.IEnumerable list && hay is not string)
                        return list.Cast<object?>().Any(x => ValuesEqual(x, needle));
                    var hs = ToStr(hay); var ns = ToStr(needle);
                    return hs != null && ns != null && hs.IndexOf(ns, StringComparison.OrdinalIgnoreCase) >= 0;
                }
                case "startswith":
                {
                    var s = ToStr(Arg(a, 0)); var p = ToStr(Arg(a, 1));
                    return s != null && p != null && s.StartsWith(p, StringComparison.OrdinalIgnoreCase);
                }
                case "endswith":
                {
                    var s = ToStr(Arg(a, 0)); var p = ToStr(Arg(a, 1));
                    return s != null && p != null && s.EndsWith(p, StringComparison.OrdinalIgnoreCase);
                }
                case "anymatch":
                {
                    // anyMatch(list, regex): some element of the list matches
                    var p = ToStr(Arg(a, 1));
                    if (p == null) return false;
                    var re = GetRegex(p);
                    if (Arg(a, 0) is System.Collections.IEnumerable seq && Arg(a, 0) is not string)
                        return seq.Cast<object?>().Any(x => ToStr(x) is string xs && re.IsMatch(xs));
                    return ToStr(Arg(a, 0)) is string one && re.IsMatch(one);
                }
                case "isa":
                {
                    return Arg(a, 0) is IExprEntity e && ToStr(Arg(a, 1)) is string cls && e.IsA(cls);
                }
                case "prop":
                    return Member(Arg(a, 0), ToStr(Arg(a, 1)) ?? "");
                default:
                    // functions supplied by the scope (e.g. the engine's geometric helpers)
                    if (scope.TryGet("fn:" + fn, out var f) && f is Func<object?[], object?> impl) return impl(a);
                    throw new InvalidOperationException("unknown function " + fn);
            }
        }

        private static object? Arg(object?[] a, int i) => i < a.Length ? a[i] : null;

        /// <summary>Names of functions known at parse time (scope functions are also accepted).</summary>
        private static readonly HashSet<string> Builtins = new(StringComparer.OrdinalIgnoreCase)
        {
            "lower", "upper", "trim", "str", "number", "len", "length", "abs", "sqrt", "pow", "round", "min", "max", "exists", "has",
            "coalesce", "if", "matches", "capture", "contains", "startswith", "endswith", "isa", "prop", "anymatch",
        };

        // ---------------------------------------------------------------- parser

        private enum T { Num, Str, Ident, Op, End }

        private readonly struct Tok
        {
            public readonly T Type; public readonly string Text; public readonly double Num; public readonly int Pos;
            public Tok(T type, string text, double num, int pos) { Type = type; Text = text; Num = num; Pos = pos; }
            public override string ToString() => Text;
        }

        private sealed class Parser
        {
            private readonly string _src;
            private readonly List<Tok> _toks = new();
            private int _p;

            public Parser(string src)
            {
                _src = src;
                Lex();
            }

            private FormatException Error(string msg, int pos) => new($"expression '{_src}': {msg} at {pos + 1}");

            private void Lex()
            {
                int i = 0;
                var s = _src;
                while (i < s.Length)
                {
                    char c = s[i];
                    if (char.IsWhiteSpace(c)) { i++; continue; }
                    int start = i;
                    if (char.IsDigit(c) || (c == '.' && i + 1 < s.Length && char.IsDigit(s[i + 1])))
                    {
                        while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.')) i++;
                        if (i < s.Length && (s[i] == 'e' || s[i] == 'E'))
                        {
                            int save = i;
                            i++;
                            if (i < s.Length && (s[i] == '+' || s[i] == '-')) i++;
                            if (i < s.Length && char.IsDigit(s[i])) { while (i < s.Length && char.IsDigit(s[i])) i++; }
                            else i = save;
                        }
                        _toks.Add(new Tok(T.Num, s.Substring(start, i - start), double.Parse(s.Substring(start, i - start), CultureInfo.InvariantCulture), start));
                        continue;
                    }
                    if (char.IsLetter(c) || c == '_' || c == '@' || c == '$')
                    {
                        while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] == '_' || s[i] == '@' || s[i] == '$')) i++;
                        _toks.Add(new Tok(T.Ident, s.Substring(start, i - start), 0, start));
                        continue;
                    }
                    if (c == '\'' || c == '"')
                    {
                        char q = c;
                        i++;
                        var sb = new StringBuilder();
                        while (i < s.Length && s[i] != q)
                        {
                            if (s[i] == '\\' && i + 1 < s.Length)
                            {
                                char e = s[i + 1];
                                // keep regex escapes (\d, \s, \.) intact; only unescape quotes and backslash
                                if (e == q || e == '\\') { sb.Append(e); i += 2; continue; }
                                sb.Append('\\').Append(e); i += 2; continue;
                            }
                            sb.Append(s[i++]);
                        }
                        if (i >= s.Length) throw Error("unterminated string", start);
                        i++;
                        _toks.Add(new Tok(T.Str, sb.ToString(), 0, start));
                        continue;
                    }
                    string[] ops = { "==", "!=", "<=", ">=", "&&", "||", "!~", "<", ">", "!", "~", "+", "-", "*", "/", "%", "(", ")", "[", "]", ",", ".", "?", ":" };
                    var op = ops.FirstOrDefault(o => string.CompareOrdinal(s, i, o, 0, o.Length) == 0);
                    if (op == null) throw Error($"unexpected '{c}'", i);
                    i += op.Length;
                    _toks.Add(new Tok(T.Op, op, 0, start));
                }
                _toks.Add(new Tok(T.End, "", 0, s.Length));
            }

            private Tok Peek => _toks[_p];
            private Tok Next() => _toks[_p++];
            private bool IsOp(string op) => Peek.Type == T.Op && Peek.Text == op;
            private bool IsWord(string w) => Peek.Type == T.Ident && string.Equals(Peek.Text, w, StringComparison.OrdinalIgnoreCase);
            private void Expect(string op)
            {
                if (!IsOp(op)) throw Error($"expected '{op}'", Peek.Pos);
                _p++;
            }

            public Func<IExprScope, object?> ParseAll()
            {
                var e = Ternary();
                if (Peek.Type != T.End) throw Error($"unexpected '{Peek.Text}'", Peek.Pos);
                return e;
            }

            private Func<IExprScope, object?> Ternary()
            {
                var c = Or();
                if (!IsOp("?")) return c;
                _p++;
                var a = Ternary();
                Expect(":");
                var b = Ternary();
                return s => Truthy(c(s)) ? a(s) : b(s);
            }

            private Func<IExprScope, object?> Or()
            {
                var l = And();
                while (IsOp("||") || IsWord("or"))
                {
                    _p++;
                    var r = And();
                    var l0 = l;
                    l = s => Truthy(l0(s)) || Truthy(r(s));
                }
                return l;
            }

            private Func<IExprScope, object?> And()
            {
                var l = Not();
                while (IsOp("&&") || IsWord("and"))
                {
                    _p++;
                    var r = Not();
                    var l0 = l;
                    l = s => Truthy(l0(s)) && Truthy(r(s));
                }
                return l;
            }

            private Func<IExprScope, object?> Not()
            {
                if (IsOp("!") || IsWord("not"))
                {
                    _p++;
                    var e = Not();
                    return s => !Truthy(e(s));
                }
                return Cmp();
            }

            private Func<IExprScope, object?> Cmp()
            {
                var l = Add();
                string? op = null;
                if (Peek.Type == T.Op && (Peek.Text is "==" or "!=" or "<" or "<=" or ">" or ">=" or "~" or "!~")) op = Next().Text;
                else if (IsWord("in")) { _p++; op = "in"; }
                if (op == null) return l;
                var r = Add();
                switch (op)
                {
                    case "==": return s => ValuesEqual(l(s), r(s));
                    case "!=": return s => !ValuesEqual(l(s), r(s));
                    case "<": return s => Compare(l(s), r(s)) is int c && c < 0;
                    case "<=": return s => Compare(l(s), r(s)) is int c && c <= 0;
                    case ">": return s => Compare(l(s), r(s)) is int c && c > 0;
                    case ">=": return s => Compare(l(s), r(s)) is int c && c >= 0;
                    case "~":
                        return s => ToStr(l(s)) is string a && ToStr(r(s)) is string p && GetRegex(p).IsMatch(a);
                    case "!~":
                        return s => !(ToStr(l(s)) is string a && ToStr(r(s)) is string p && GetRegex(p).IsMatch(a));
                    default: // in
                        return s =>
                        {
                            var a = l(s); var b = r(s);
                            if (b is System.Collections.IEnumerable list && b is not string) return list.Cast<object?>().Any(x => ValuesEqual(a, x));
                            return ToStr(a) is string sa && ToStr(b) is string sb && sb.IndexOf(sa, StringComparison.OrdinalIgnoreCase) >= 0;
                        };
                }
            }

            private Func<IExprScope, object?> Add()
            {
                var l = Mul();
                while (IsOp("+") || IsOp("-"))
                {
                    string op = Next().Text;
                    var r = Mul();
                    var l0 = l;
                    if (op == "+")
                        l = s =>
                        {
                            var a = l0(s); var b = r(s);
                            if (a is string || b is string) return a == null || b == null ? null : ToStr(a) + ToStr(b);
                            return ToNumber(a) is double x && ToNumber(b) is double y ? x + y : null;
                        };
                    else l = s => ToNumber(l0(s)) is double x && ToNumber(r(s)) is double y ? x - y : null;
                }
                return l;
            }

            private Func<IExprScope, object?> Mul()
            {
                var l = Unary();
                while (IsOp("*") || IsOp("/") || IsOp("%"))
                {
                    string op = Next().Text;
                    var r = Unary();
                    var l0 = l;
                    l = op switch
                    {
                        "*" => s => ToNumber(l0(s)) is double x && ToNumber(r(s)) is double y ? x * y : null,
                        "/" => s => ToNumber(l0(s)) is double x && ToNumber(r(s)) is double y && y != 0 ? x / y : null,
                        _ => s => ToNumber(l0(s)) is double x && ToNumber(r(s)) is double y && y != 0 ? x % y : null,
                    };
                }
                return l;
            }

            private Func<IExprScope, object?> Unary()
            {
                if (IsOp("-"))
                {
                    _p++;
                    var e = Unary();
                    return s => ToNumber(e(s)) is double x ? -x : null;
                }
                return Postfix();
            }

            private Func<IExprScope, object?> Postfix()
            {
                var e = Primary(out string? ident);
                while (true)
                {
                    if (IsOp("."))
                    {
                        _p++;
                        var t = Next();
                        if (t.Type != T.Ident) throw Error("expected member name", t.Pos);
                        var e0 = e;
                        string name = t.Text;
                        e = s => Member(e0(s), name);
                        ident = null;
                    }
                    else if (IsOp("["))
                    {
                        _p++;
                        var idx = Ternary();
                        Expect("]");
                        var e0 = e;
                        e = s =>
                        {
                            var target = e0(s); var k = idx(s);
                            if (target is IList<object?> list && ToNumber(k) is double d)
                            {
                                int i = (int)d;
                                return i >= 0 && i < list.Count ? list[i] : null;
                            }
                            return Member(target, ToStr(k) ?? "");
                        };
                        ident = null;
                    }
                    else if (IsOp("(") && ident != null)
                    {
                        _p++;
                        var args = new List<Func<IExprScope, object?>>();
                        if (!IsOp(")"))
                        {
                            do { args.Add(Ternary()); } while (IsOp(",") && Next().Text == ",");
                        }
                        Expect(")");
                        string fn = ident;
                        var argArr = args.ToArray();
                        e = s =>
                        {
                            var vals = new object?[argArr.Length];
                            for (int i = 0; i < vals.Length; i++) vals[i] = argArr[i](s);
                            return Call(fn, vals, s);
                        };
                        ident = null;
                    }
                    else return e;
                }
            }

            private Func<IExprScope, object?> Primary(out string? ident)
            {
                ident = null;
                var t = Next();
                switch (t.Type)
                {
                    case T.Num: { object v = t.Num; return _ => v; }
                    case T.Str: { object v = t.Text; return _ => v; }
                    case T.Ident:
                    {
                        string w = t.Text;
                        if (w.Equals("true", StringComparison.OrdinalIgnoreCase)) return _ => true;
                        if (w.Equals("false", StringComparison.OrdinalIgnoreCase)) return _ => false;
                        if (w.Equals("null", StringComparison.OrdinalIgnoreCase)) return _ => null;
                        ident = w;
                        return s => s.TryGet(w, out var v) ? v : null;
                    }
                    case T.Op when t.Text == "(":
                    {
                        var e = Ternary();
                        Expect(")");
                        return e;
                    }
                    case T.Op when t.Text == "[":
                    {
                        var items = new List<Func<IExprScope, object?>>();
                        if (!IsOp("]"))
                        {
                            do { items.Add(Ternary()); } while (IsOp(",") && Next().Text == ",");
                        }
                        Expect("]");
                        var arr = items.ToArray();
                        return s => arr.Select(f => f(s)).ToList();
                    }
                    default:
                        throw Error($"unexpected '{t.Text}'", t.Pos);
                }
            }
        }

        internal static bool IsBuiltin(string name) => Builtins.Contains(name);
    }
}
