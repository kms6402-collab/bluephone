using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Threading;

// ─────────────────────────────────────────────────────────────────────────────
// Windows 판(.NET Framework)의 JavaScriptSerializer 와 같은 규칙으로 JSON 을 읽고 쓰는 호환 구현.
//  · 속성 이름 그대로(PascalCase), [ScriptIgnore] 는 제외
//  · object 로 읽을 때: 객체 → Dictionary<string, object>, 배열 → ArrayList, 정수 → int/long, 소수 → decimal
// 같은 settings.json · history.jsonl · 통신 패킷을 Windows 판과 그대로 주고받기 위함
// ─────────────────────────────────────────────────────────────────────────────
namespace System.Web.Script.Serialization
{
    [AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
    public sealed class ScriptIgnoreAttribute : Attribute { }

    public sealed class JavaScriptSerializer
    {
        public int MaxJsonLength { get; set; } = int.MaxValue;

        // ───────────── 쓰기 ─────────────

        public string Serialize(object o)
        {
            var sb = new StringBuilder();
            Write(sb, o, 0);
            return sb.ToString();
        }

        static void Write(StringBuilder sb, object o, int depth)
        {
            if (depth > 64) throw new InvalidOperationException("JSON 깊이 초과");
            switch (o)
            {
                case null: sb.Append("null"); return;
                case string s: Str(sb, s); return;
                case bool b: sb.Append(b ? "true" : "false"); return;
                case char c: Str(sb, c.ToString()); return;
                case Enum e: sb.Append(System.Convert.ToInt64(e).ToString(CultureInfo.InvariantCulture)); return;
                case int or long or short or byte or sbyte or uint or ulong or ushort:
                    sb.Append(System.Convert.ToString(o, CultureInfo.InvariantCulture)); return;
                case double d: sb.Append(double.IsFinite(d) ? d.ToString("R", CultureInfo.InvariantCulture) : "null"); return;
                case float f: sb.Append(float.IsFinite(f) ? f.ToString("R", CultureInfo.InvariantCulture) : "null"); return;
                case decimal m: sb.Append(m.ToString(CultureInfo.InvariantCulture)); return;
                case Guid g: Str(sb, g.ToString()); return;
                case IDictionary dict:
                {
                    sb.Append('{');
                    bool first = true;
                    foreach (DictionaryEntry kv in dict)
                    {
                        if (!first) sb.Append(',');
                        first = false;
                        Str(sb, System.Convert.ToString(kv.Key, CultureInfo.InvariantCulture));
                        sb.Append(':');
                        Write(sb, kv.Value, depth + 1);
                    }
                    sb.Append('}');
                    return;
                }
                case IEnumerable list:
                {
                    sb.Append('[');
                    bool first = true;
                    foreach (var x in list)
                    {
                        if (!first) sb.Append(',');
                        first = false;
                        Write(sb, x, depth + 1);
                    }
                    sb.Append(']');
                    return;
                }
            }
            // 일반 객체: 공개 속성(읽기 가능) · 공개 필드
            sb.Append('{');
            bool f1 = true;
            foreach (var m in Members(o.GetType()))
            {
                object v;
                try { v = m.Get(o); } catch { continue; }
                if (!f1) sb.Append(',');
                f1 = false;
                Str(sb, m.Name);
                sb.Append(':');
                Write(sb, v, depth + 1);
            }
            sb.Append('}');
        }

        static void Str(StringBuilder sb, string s)
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
                        if (c < 0x20 || c == '<' || c == '>' || c == '&' || c == '\'' || c == '\u2028' || c == '\u2029')
                            sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        sealed class Member
        {
            public string Name;
            public Type Type;
            public Func<object, object> Get;
            public Action<object, object> Set;
        }

        static readonly Dictionary<Type, List<Member>> _members = new Dictionary<Type, List<Member>>();

        static List<Member> Members(Type t)
        {
            lock (_members)
            {
                if (_members.TryGetValue(t, out var list)) return list;
                list = new List<Member>();
                foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (p.GetIndexParameters().Length > 0 || p.GetCustomAttribute<ScriptIgnoreAttribute>() != null) continue;
                    if (p.GetGetMethod() == null) continue;
                    var pp = p;
                    list.Add(new Member
                    {
                        Name = p.Name, Type = p.PropertyType, Get = o => pp.GetValue(o),
                        Set = p.GetSetMethod() != null ? (o, v) => pp.SetValue(o, v) : null,
                    });
                }
                foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (f.GetCustomAttribute<ScriptIgnoreAttribute>() != null) continue;
                    var ff = f;
                    list.Add(new Member { Name = f.Name, Type = f.FieldType, Get = o => ff.GetValue(o), Set = f.IsInitOnly ? null : (o, v) => ff.SetValue(o, v) });
                }
                _members[t] = list;
                return list;
            }
        }

        // ───────────── 읽기 ─────────────

        public T Deserialize<T>(string json) => (T)Convert(Parse(json), typeof(T));

        public object DeserializeObject(string json) => Parse(json);

        static object Parse(string json)
        {
            if (json == null) throw new ArgumentNullException(nameof(json));
            var p = new Parser(json);
            p.Ws();
            var v = p.Value(0);
            p.Ws();
            if (!p.End) throw new FormatException("JSON 뒤에 남은 글자가 있습니다.");
            return v;
        }

        sealed class Parser
        {
            readonly string _s;
            int _i;
            public Parser(string s) { _s = s; }
            public bool End => _i >= _s.Length;

            public void Ws() { while (_i < _s.Length && char.IsWhiteSpace(_s[_i])) _i++; }

            public object Value(int depth)
            {
                if (depth > 128) throw new FormatException("JSON 깊이 초과");
                Ws();
                if (End) throw new FormatException("JSON 이 끝났습니다.");
                char c = _s[_i];
                switch (c)
                {
                    case '{':
                    {
                        _i++;
                        var d = new Dictionary<string, object>();
                        Ws();
                        if (Peek('}')) { _i++; return d; }
                        while (true)
                        {
                            Ws();
                            var k = String();
                            Ws(); Expect(':');
                            d[k] = Value(depth + 1);
                            Ws();
                            if (Peek(',')) { _i++; continue; }
                            Expect('}');
                            return d;
                        }
                    }
                    case '[':
                    {
                        _i++;
                        var a = new ArrayList();
                        Ws();
                        if (Peek(']')) { _i++; return a; }
                        while (true)
                        {
                            a.Add(Value(depth + 1));
                            Ws();
                            if (Peek(',')) { _i++; continue; }
                            Expect(']');
                            return a;
                        }
                    }
                    case '"': return String();
                    case 't': Word("true"); return true;
                    case 'f': Word("false"); return false;
                    case 'n': Word("null"); return null;
                    default: return Number();
                }
            }

            bool Peek(char c) => _i < _s.Length && _s[_i] == c;
            void Expect(char c) { if (!Peek(c)) throw new FormatException($"'{c}' 가 필요합니다 (위치 {_i})"); _i++; }
            void Word(string w)
            {
                if (string.CompareOrdinal(_s, _i, w, 0, w.Length) != 0) throw new FormatException("잘못된 JSON 값");
                _i += w.Length;
            }

            string String()
            {
                Expect('"');
                var sb = new StringBuilder();
                while (true)
                {
                    if (End) throw new FormatException("문자열이 끝나지 않았습니다.");
                    char c = _s[_i++];
                    if (c == '"') return sb.ToString();
                    if (c != '\\') { sb.Append(c); continue; }
                    if (End) throw new FormatException("잘못된 이스케이프");
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
                            if (_i + 4 > _s.Length) throw new FormatException("잘못된 \\u");
                            sb.Append((char)int.Parse(_s.Substring(_i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                            _i += 4;
                            break;
                        default: sb.Append(e); break;
                    }
                }
            }

            object Number()
            {
                int start = _i;
                if (Peek('-') || Peek('+')) _i++;
                while (_i < _s.Length && (char.IsDigit(_s[_i]) || _s[_i] == '.' || _s[_i] == 'e' || _s[_i] == 'E' || _s[_i] == '-' || _s[_i] == '+')) _i++;
                var t = _s.Substring(start, _i - start);
                if (t.Length == 0) throw new FormatException($"잘못된 JSON (위치 {start})");
                if (t.IndexOfAny(new[] { '.', 'e', 'E' }) < 0)
                {
                    if (int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)) return i;
                    if (long.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l)) return l;
                }
                if (decimal.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var m)) return m;
                return double.Parse(t, NumberStyles.Float, CultureInfo.InvariantCulture);
            }
        }

        static object Convert(object v, Type t)
        {
            if (t == typeof(object)) return v;
            var nt = Nullable.GetUnderlyingType(t);
            if (v == null) return t.IsValueType && nt == null ? Activator.CreateInstance(t) : null;
            if (nt != null) t = nt;
            if (t.IsInstanceOfType(v) && !(v is ArrayList) && !(v is Dictionary<string, object> && t != typeof(Dictionary<string, object>) && t != typeof(object)))
                return v;
            if (t == typeof(string)) return System.Convert.ToString(v, CultureInfo.InvariantCulture);
            if (t.IsEnum) return v is string es ? Enum.Parse(t, es, true) : Enum.ToObject(t, System.Convert.ToInt64(v, CultureInfo.InvariantCulture));
            if (t == typeof(Guid)) return Guid.Parse(System.Convert.ToString(v, CultureInfo.InvariantCulture));
            if (t.IsPrimitive || t == typeof(decimal))
            {
                if (v is string ps && t != typeof(char))
                    return System.Convert.ChangeType(ps, t, CultureInfo.InvariantCulture);
                return System.Convert.ChangeType(v, t, CultureInfo.InvariantCulture);
            }

            if (v is Dictionary<string, object> dict)
            {
                if (typeof(IDictionary).IsAssignableFrom(t))
                {
                    var target = (IDictionary)Activator.CreateInstance(t);
                    var valType = t.IsGenericType ? t.GetGenericArguments()[1] : typeof(object);
                    foreach (var kv in dict) target[kv.Key] = Convert(kv.Value, valType);
                    return target;
                }
                var o = Activator.CreateInstance(t);
                var members = Members(t);
                foreach (var kv in dict)
                {
                    var m = members.FirstOrDefault(x => x.Name == kv.Key) ?? members.FirstOrDefault(x => string.Equals(x.Name, kv.Key, StringComparison.OrdinalIgnoreCase));
                    if (m?.Set == null) continue;
                    try { m.Set(o, Convert(kv.Value, m.Type)); } catch { }
                }
                return o;
            }

            if (v is ArrayList arr)
            {
                if (t.IsArray)
                {
                    var et = t.GetElementType();
                    var a = Array.CreateInstance(et, arr.Count);
                    for (int i = 0; i < arr.Count; i++) a.SetValue(Convert(arr[i], et), i);
                    return a;
                }
                if (t == typeof(ArrayList) || t == typeof(IEnumerable) || t == typeof(IList)) return arr;
                var elem = t.IsGenericType ? t.GetGenericArguments()[0] : typeof(object);
                var listType = t.IsInterface ? typeof(List<>).MakeGenericType(elem) : t;
                var list = (IList)Activator.CreateInstance(listType);
                foreach (var x in arr) list.Add(Convert(x, elem));
                return list;
            }
            return System.Convert.ChangeType(v, t, CultureInfo.InvariantCulture);
        }
    }
}

namespace BluePhone
{
    /// <summary>WPF Dispatcher 와 같은 모양의 UI 스레드 호출 (공유 코드 Telegram.cs 용)</summary>
    public sealed class UiDispatcher
    {
        public static readonly UiDispatcher Instance = new UiDispatcher();

        public void BeginInvoke(Delegate d) => Dispatcher.UIThread.Post(() => d.DynamicInvoke());

        public Op<T> InvokeAsync<T>(Func<T> f) => new Op<T>(Dispatcher.UIThread.InvokeAsync(f).GetTask());

        public sealed class Op<T>
        {
            public Op(Task<T> t) { Task = t; }
            public Task<T> Task { get; }
        }
    }
}
