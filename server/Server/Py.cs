// Python-compatible JSON, rounding and form helpers: replies keep the byte format of the Python reference
// implementation the reply format comes from.
//
// Porting rules:
// - dict/list/scalar → JsonObject/JsonArray/JsonValue (or null). A number is a float when its raw text has . e E or
//   it was created from a double, else an int. A node has one parent: DeepClone() before putting it in a second tree.
// - json.loads(b) → JsonNode.Parse(b); ValueError → catch JsonException. json.dumps(x).encode() → Py.DumpBytes(x).
// - Python / → double division; int(x) → (long)x; math.floor → (long)Math.Floor.
// - sum(...) of floats → Scoring.Sum (CPython 3.12+ Neumaier compensated summation), never LINQ Sum or a plain loop.
//   sorted() on strings → StringComparer.Ordinal.
// - f"{x:.Nf}" → Py.Fixed(x, N); f"{x}" of a JSON value → Py.Str(x); round(x, n) → Py.Round; round(x) → Py.RoundInt.
//   Never Math.Round(x, digits) or "F4": they differ from Python on ties.
// - {x:g} → x.ToString("G6"); {x:.14G} → x.ToString("G14")
//   (ponytail: .NET G ties away from zero and uses E+XX; exact ties and exponents don't occur for these magnitudes).
// - f-string templates → $"..." one-to-one, \t/\n escapes kept.
// - html.escape(s, quote=False) → & < > in that order; html.escape(s) also " → &quot; and ' → &#x27;.
// - Regexes on bytes or needing ASCII \w \d \s \b → RegexOptions.ECMAScript.
// - strftime("%Y-%m-%dT%H:%M:%SZ") → ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
//   isoformat(timespec="milliseconds") with Z → "yyyy-MM-dd'T'HH:mm:ss.fff'Z'".
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Web;

namespace Resurgence.Server;

public static class Py
{
    public static string Dumps(JsonNode? n, bool compact = false)
    {
        var sb = new StringBuilder();
        Write(sb, n, compact ? "," : ", ", compact ? ":" : ": ");
        return sb.ToString();
    }

    public static byte[] DumpBytes(JsonNode? n) => Encoding.ASCII.GetBytes(Dumps(n));

    static void Write(StringBuilder sb, JsonNode? n, string item, string key)
    {
        switch (n)
        {
            case null:
                sb.Append("null");
                break;
            case JsonObject o:
                sb.Append('{');
                var first = true;
                foreach (var (k, v) in o)
                {
                    if (!first) sb.Append(item);
                    first = false;
                    Quote(sb, k);
                    sb.Append(key);
                    Write(sb, v, item, key);
                }
                sb.Append('}');
                break;
            case JsonArray a:
                sb.Append('[');
                for (var i = 0; i < a.Count; i++)
                {
                    if (i > 0) sb.Append(item);
                    Write(sb, a[i], item, key);
                }
                sb.Append(']');
                break;
            default:
                var raw = Raw((JsonValue)n);
                switch (raw)
                {
                    case string s: Quote(sb, s); break;
                    case bool b: sb.Append(b ? "true" : "false"); break;
                    case double d: sb.Append(Num(d)); break;
                    case string[] digits: sb.Append(digits[0]); break;
                    default: sb.Append(Convert.ToString(raw)); break;  // CLR integer
                }
                break;
        }
    }

    static string Num(double d) =>
        double.IsNaN(d) ? "NaN" : double.IsPositiveInfinity(d) ? "Infinity" : double.IsNegativeInfinity(d) ? "-Infinity" : Repr(d);

    /// <summary>Scalar as a CLR value: string, bool, double (float), long/int/… (int), string[1] (big int raw digits).</summary>
    static object Raw(JsonValue v)
    {
        if (v.TryGetValue<JsonElement>(out var el))
            return el.ValueKind switch
            {
                JsonValueKind.String => el.GetString()!,
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Number => el.GetRawText() is var t && t.AsSpan().IndexOfAny('.', 'e', 'E') >= 0
                    ? (object)el.GetDouble()
                    : el.TryGetInt64(out var l) ? l : new[] { t },
                _ => throw new InvalidOperationException($"unexpected JSON value {el.ValueKind}"),
            };
        var o = v.GetValue<object>();
        return o switch
        {
            float f => (double)f,
            decimal m => (double)m,
            char c => c.ToString(),
            _ => o,
        };
    }

    static void Quote(StringBuilder sb, string s)
    {
        sb.Append('"');
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                default:
                    if (c >= 0x20 && c <= 0x7E) sb.Append(c);
                    else sb.Append("\\u").Append(((int)c).ToString("x4"));
                    break;
            }
        }
        sb.Append('"');
    }

    /// <summary>Python repr(float).</summary>
    public static string Repr(double d)
    {
        if (double.IsNaN(d)) return "nan";
        if (double.IsInfinity(d)) return d > 0 ? "inf" : "-inf";
        var r = d.ToString("R");
        var neg = r[0] == '-';
        if (neg) r = r[1..];
        var exp = 0;
        var ei = r.IndexOf('E');
        if (ei >= 0)
        {
            exp = int.Parse(r[(ei + 1)..]);
            r = r[..ei];
        }
        var dot = r.IndexOf('.');
        var digits = dot >= 0 ? r.Remove(dot, 1) : r;
        var decpt = (dot >= 0 ? dot : r.Length) + exp;  // value = 0.digits × 10^decpt
        var lead = 0;
        while (lead < digits.Length - 1 && digits[lead] == '0') lead++;
        digits = digits[lead..];
        decpt -= lead;
        digits = digits.TrimEnd('0');
        if (digits.Length == 0)
        {
            digits = "0";
            decpt = 1;
        }
        string s;
        if (decpt <= -4 || decpt > 16)
        {
            var e = decpt - 1;
            s = digits[..1] + (digits.Length > 1 ? "." + digits[1..] : "") + "e" + (e < 0 ? "-" : "+") + Math.Abs(e).ToString("00");
        }
        else if (decpt <= 0) s = "0." + new string('0', -decpt) + digits;
        else if (decpt >= digits.Length) s = digits + new string('0', decpt - digits.Length) + ".0";
        else s = digits[..decpt] + "." + digits[decpt..];
        return neg ? "-" + s : s;
    }

    /// <summary>Python str() of a JSON value.</summary>
    public static string Str(JsonNode? n)
    {
        if (n is null) return "None";
        if (n is not JsonValue v) throw new ArgumentException("Str of a container");
        return Raw(v) switch
        {
            string s => s,
            bool b => b ? "True" : "False",
            double d => Repr(d),
            string[] digits => digits[0],
            var o => Convert.ToString(o)!,
        };
    }

    /// <summary>Python format(x, f".{n}f"): correctly rounded, half to even on the exact binary value.</summary>
    public static string Fixed(double x, int n)
    {
        if (double.IsNaN(x)) return "nan";
        if (double.IsInfinity(x)) return x > 0 ? "inf" : "-inf";
        var bits = BitConverter.DoubleToInt64Bits(x);
        var be = (int)((bits >> 52) & 0x7FF);
        var man = bits & 0xF_FFFF_FFFF_FFFFL;
        if (be == 0) be = 1;
        else man |= 1L << 52;
        var e = be - 1075;  // |x| = man · 2^e
        BigInteger num = man, den = BigInteger.One;
        if (e > 0) num <<= e;
        else den <<= -e;
        num *= BigInteger.Pow(10, n);
        var q = BigInteger.DivRem(num, den, out var rem);
        var twice = rem * 2;
        if (twice > den || (twice == den && !q.IsEven)) q += 1;
        var s = q.ToString().PadLeft(n + 1, '0');
        if (n > 0) s = s[..^n] + "." + s[^n..];
        return bits < 0 ? "-" + s : s;
    }

    /// <summary>Python round(x, n).</summary>
    public static double Round(double x, int n) => double.IsFinite(x) ? double.Parse(Fixed(x, n)) : x;

    /// <summary>Python round(x).</summary>
    public static long RoundInt(double x) => (long)Math.Round(x, MidpointRounding.ToEven);

    /// <summary>Python urllib.parse.parse_qs.</summary>
    public static OrderedDictionary<string, List<string>> ParseQs(string s, bool keepBlank = false)
    {
        var r = new OrderedDictionary<string, List<string>>();
        foreach (var piece in s.Split('&'))
        {
            if (piece.Length == 0) continue;
            var eq = piece.IndexOf('=');
            if (eq < 0 && !keepBlank) continue;
            var name = eq < 0 ? piece : piece[..eq];
            var value = eq < 0 ? "" : piece[(eq + 1)..];
            if (value.Length == 0 && !keepBlank) continue;
            name = HttpUtility.UrlDecode(name, Encoding.UTF8);
            value = HttpUtility.UrlDecode(value, Encoding.UTF8);
            if (!r.TryGetValue(name, out var list)) r[name] = list = [];
            list.Add(value);
        }
        return r;
    }

    /// <summary>str.isdigit() for ASCII input.</summary>
    public static bool IsDigit(string s) => s.Length > 0 && s.All(char.IsAsciiDigit);

    public static bool IsStr(JsonNode? n) => n is JsonValue v && Raw(v) is string;
    public static bool IsBool(JsonNode? n) => n is JsonValue v && Raw(v) is bool;
    public static bool IsFloat(JsonNode? n) => n is JsonValue v && Raw(v) is double;

    /// <summary>isinstance(n, int) (bool excluded).</summary>
    public static bool IsInt(JsonNode? n) => n is JsonValue v && Raw(v) is not (string or bool or double);

    /// <summary>isinstance(n, (int, float)) (bool excluded).</summary>
    public static bool IsNum(JsonNode? n) => n is JsonValue v && Raw(v) is not (string or bool);

    /// <summary>float(n) of a JSON number or bool.</summary>
    public static double D(JsonNode? n) => Raw((JsonValue)n!) switch
    {
        double d => d,
        bool b => b ? 1 : 0,
        string[] digits => double.Parse(digits[0]),
        string s => throw new InvalidCastException($"float of string {s}"),
        var o => Convert.ToDouble(o),
    };

    /// <summary>int(n) of a JSON number or bool: floats truncate toward zero.</summary>
    public static long L(JsonNode? n) => Raw((JsonValue)n!) switch
    {
        double d => (long)d,
        bool b => b ? 1 : 0,
        string s => throw new InvalidCastException($"int of string {s}"),
        var o => Convert.ToInt64(o),
    };

    /// <summary>Python truthiness of a JSON value.</summary>
    public static bool Truthy(JsonNode? n) => n switch
    {
        null => false,
        JsonObject o => o.Count > 0,
        JsonArray a => a.Count > 0,
        JsonValue v => Raw(v) switch
        {
            string s => s.Length > 0,
            bool b => b,
            double d => d != 0,
            string[] => true,
            var o => Convert.ToInt64(o) != 0,
        },
        _ => true,
    };

    /// <summary>Python == on parsed JSON: numbers (and bools) by value, dicts ignoring key order, lists in order.</summary>
    public static bool Eq(JsonNode? a, JsonNode? b)
    {
        switch (a, b)
        {
            case (null, null): return true;
            case (null, _) or (_, null): return false;
            case (JsonObject x, JsonObject y):
                if (x.Count != y.Count) return false;
                foreach (var (k, v) in x)
                    if (!y.TryGetPropertyValue(k, out var w) || !Eq(v, w)) return false;
                return true;
            case (JsonArray x, JsonArray y):
                if (x.Count != y.Count) return false;
                for (var i = 0; i < x.Count; i++)
                    if (!Eq(x[i], y[i])) return false;
                return true;
            case (JsonValue x, JsonValue y):
                var (p, q) = (Raw(x), Raw(y));
                if (p is string s) return q is string t && s == t;
                if (q is string) return false;
                return D(x) == D(y);
            default: return false;
        }
    }

    /// <summary>ADO value → JSON (sqlite3 row value).</summary>
    public static JsonNode? FromSql(object? v) => v switch
    {
        null or DBNull => null,
        long l => JsonValue.Create(l),
        double d => JsonValue.Create(d),
        string s => JsonValue.Create(s),
        _ => throw new InvalidCastException($"unexpected sqlite value {v.GetType()}"),
    };

    /// <summary>JSON value → ADO parameter (what sqlite3 binds).</summary>
    public static object Sql(JsonNode? n) => n switch
    {
        null => DBNull.Value,
        JsonValue v => Raw(v) switch
        {
            bool b => b ? 1L : 0L,
            double d => d,
            string s => s,
            string[] digits => throw new OverflowException($"Python int too large to convert to SQLite INTEGER: {digits[0]}"),
            var o => Convert.ToInt64(o),
        },
        _ => throw new InvalidCastException("sqlite3 can't bind a dict or list"),
    };

    /// <summary>time.time().</summary>
    public static double Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
}
