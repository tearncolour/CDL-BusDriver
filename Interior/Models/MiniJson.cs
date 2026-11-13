using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace CDLC.BusDriver.Interior.Models
{
    /// <summary>
    /// A small strict JSON reader for vehicle pack profiles: objects (Dictionary&lt;string, object&gt;), arrays (List&lt;object&gt;),
    /// strings, numbers (kept as their text, a <see cref="Number"/>, so a float reads the same on the game's .NET Framework and
    /// the tests' .NET), true/false/null. No dependency: the same code runs in the mod, the offline tests and the build tools.
    /// </summary>
    internal static class MiniJson
    {
        internal readonly struct Number
        {
            internal readonly string Text;
            internal Number(string text) { Text = text; }
            // through double on every runtime (the game's float.Parse goes through double too): one rule everywhere
            internal double Double => double.Parse(Text, NumberStyles.Float, CultureInfo.InvariantCulture);
            internal float Float => (float)Double;
            public override string ToString() => Text;
        }

        internal sealed class FormatError : Exception
        {
            internal FormatError(string message) : base(message) { }
        }

        private const int MaxDepth = 32;

        internal static object Parse(string text)
        {
            if (text == null) throw new FormatError("no text");
            int i = 0;
            object value = Value(text, ref i, 0);
            Skip(text, ref i);
            if (i != text.Length) throw new FormatError("trailing characters at " + i);
            return value;
        }

        private static void Skip(string s, ref int i)
        {
            while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\r' || s[i] == '\n' || s[i] == '﻿')) i++;
        }

        private static object Value(string s, ref int i, int depth)
        {
            if (depth > MaxDepth) throw new FormatError("nested too deep");
            Skip(s, ref i);
            if (i >= s.Length) throw new FormatError("unexpected end");
            char c = s[i];
            if (c == '{')
            {
                i++;
                var obj = new Dictionary<string, object>(StringComparer.Ordinal);
                Skip(s, ref i);
                if (i < s.Length && s[i] == '}') { i++; return obj; }
                while (true)
                {
                    Skip(s, ref i);
                    if (i >= s.Length || s[i] != '"') throw new FormatError("expected a key at " + i);
                    string key = Str(s, ref i);
                    Skip(s, ref i);
                    if (i >= s.Length || s[i] != ':') throw new FormatError("expected ':' at " + i);
                    i++;
                    if (obj.ContainsKey(key)) throw new FormatError("duplicate key " + key);
                    obj[key] = Value(s, ref i, depth + 1);
                    Skip(s, ref i);
                    if (i < s.Length && s[i] == ',') { i++; continue; }
                    if (i < s.Length && s[i] == '}') { i++; return obj; }
                    throw new FormatError("expected ',' or '}' at " + i);
                }
            }
            if (c == '[')
            {
                i++;
                var list = new List<object>();
                Skip(s, ref i);
                if (i < s.Length && s[i] == ']') { i++; return list; }
                while (true)
                {
                    list.Add(Value(s, ref i, depth + 1));
                    Skip(s, ref i);
                    if (i < s.Length && s[i] == ',') { i++; continue; }
                    if (i < s.Length && s[i] == ']') { i++; return list; }
                    throw new FormatError("expected ',' or ']' at " + i);
                }
            }
            if (c == '"') return Str(s, ref i);
            if (Word(s, ref i, "true")) return true;
            if (Word(s, ref i, "false")) return false;
            if (Word(s, ref i, "null")) return null;
            int start = i;
            if (i < s.Length && s[i] == '-') i++;
            int digits = i;
            while (i < s.Length && char.IsDigit(s[i])) i++;
            if (i == digits) throw new FormatError("unexpected character at " + start);
            if (i < s.Length && s[i] == '.')
            {
                i++;
                int frac = i;
                while (i < s.Length && char.IsDigit(s[i])) i++;
                if (i == frac) throw new FormatError("bad number at " + start);
            }
            if (i < s.Length && (s[i] == 'e' || s[i] == 'E'))
            {
                i++;
                if (i < s.Length && (s[i] == '+' || s[i] == '-')) i++;
                int exp = i;
                while (i < s.Length && char.IsDigit(s[i])) i++;
                if (i == exp) throw new FormatError("bad number at " + start);
            }
            return new Number(s.Substring(start, i - start));
        }

        private static bool Word(string s, ref int i, string word)
        {
            if (string.CompareOrdinal(s, i, word, 0, word.Length) != 0) return false;
            i += word.Length;
            return true;
        }

        private static string Str(string s, ref int i)
        {
            i++;     // the opening quote
            var b = new StringBuilder();
            while (i < s.Length)
            {
                char c = s[i++];
                if (c == '"') return b.ToString();
                if (c == '\\')
                {
                    if (i >= s.Length) break;
                    char e = s[i++];
                    switch (e)
                    {
                        case '"': b.Append('"'); break;
                        case '\\': b.Append('\\'); break;
                        case '/': b.Append('/'); break;
                        case 'b': b.Append('\b'); break;
                        case 'f': b.Append('\f'); break;
                        case 'n': b.Append('\n'); break;
                        case 'r': b.Append('\r'); break;
                        case 't': b.Append('\t'); break;
                        case 'u':
                            if (i + 4 > s.Length) throw new FormatError("bad escape");
                            b.Append((char)int.Parse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                            i += 4;
                            break;
                        default: throw new FormatError("bad escape \\" + e);
                    }
                }
                else if (c < ' ') throw new FormatError("control character in a string");
                else b.Append(c);
            }
            throw new FormatError("unterminated string");
        }
    }
}
