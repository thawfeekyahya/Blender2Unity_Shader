using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace B2U
{
    /// <summary>Insertion-ordered JSON object (Shader Graph files are diffed by humans; keep key order stable).</summary>
    public sealed class JObj : IEnumerable<KeyValuePair<string, object>>
    {
        readonly List<KeyValuePair<string, object>> _items = new List<KeyValuePair<string, object>>();

        public void Add(string key, object value)
        {
            for (int i = 0; i < _items.Count; i++)
                if (_items[i].Key == key) { _items[i] = new KeyValuePair<string, object>(key, value); return; }
            _items.Add(new KeyValuePair<string, object>(key, value));
        }

        public object this[string key]
        {
            get { foreach (var kv in _items) if (kv.Key == key) return kv.Value; return null; }
            set => Add(key, value);
        }

        public IEnumerator<KeyValuePair<string, object>> GetEnumerator() => _items.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => _items.GetEnumerator();
    }

    public static class JsonWriter
    {
        public static string Write(object value)
        {
            var sb = new StringBuilder();
            WriteValue(sb, value, 0);
            return sb.ToString();
        }

        static void Indent(StringBuilder sb, int level) => sb.Append(' ', level * 4);

        static void WriteValue(StringBuilder sb, object v, int level)
        {
            switch (v)
            {
                case null: sb.Append("null"); break;
                case string s: WriteString(sb, s); break;
                case bool b: sb.Append(b ? "true" : "false"); break;
                case int i: sb.Append(i.ToString(CultureInfo.InvariantCulture)); break;
                case long l: sb.Append(l.ToString(CultureInfo.InvariantCulture)); break;
                case float f: sb.Append(Num(f)); break;
                case double d: sb.Append(Num((float)d)); break;
                case JObj o:
                {
                    var any = false;
                    sb.Append('{');
                    foreach (var kv in o)
                    {
                        sb.Append(any ? ",\n" : "\n");
                        Indent(sb, level + 1);
                        WriteString(sb, kv.Key);
                        sb.Append(": ");
                        WriteValue(sb, kv.Value, level + 1);
                        any = true;
                    }
                    if (any) { sb.Append('\n'); Indent(sb, level); }
                    sb.Append('}');
                    break;
                }
                case IEnumerable e:
                {
                    var any = false;
                    sb.Append('[');
                    foreach (var item in e)
                    {
                        sb.Append(any ? ",\n" : "\n");
                        Indent(sb, level + 1);
                        WriteValue(sb, item, level + 1);
                        any = true;
                    }
                    if (any) { sb.Append('\n'); Indent(sb, level); }
                    sb.Append(']');
                    break;
                }
                default: WriteString(sb, v.ToString()); break;
            }
        }

        static string Num(float f)
        {
            if (float.IsNaN(f) || float.IsInfinity(f)) f = 0f;
            var s = f.ToString("R", CultureInfo.InvariantCulture);
            return s.Contains(".") || s.Contains("E") ? s : s + ".0";
        }

        public static void WriteString(StringBuilder sb, string s)
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
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }
    }
}
