// Mynell Lottie Player — Tiny JSON tokenizer used only at parse time.
// Public-domain style minimal implementation. No reflection, no schema.
// Returns nested Dictionary<string,object> / List<object> / string / double / bool / null.

using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Mynell.LottiePlayer
{
    internal static class MiniJson
    {
        public static object Deserialize(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            var p = new Parser(json);
            return p.ParseValue();
        }

        private sealed class Parser
        {
            private readonly string _s;
            private int _i;
            private readonly StringBuilder _sb = new StringBuilder(64);

            public Parser(string s) { _s = s; _i = 0; }

            public object ParseValue()
            {
                SkipWs();
                if (_i >= _s.Length) return null;
                char c = _s[_i];
                switch (c)
                {
                    case '{': return ParseObject();
                    case '[': return ParseArray();
                    case '"': return ParseString();
                    case 't':
                    case 'f': return ParseBool();
                    case 'n': return ParseNull();
                    default:  return ParseNumber();
                }
            }

            private Dictionary<string, object> ParseObject()
            {
                var dict = new Dictionary<string, object>();
                _i++; // '{'
                while (true)
                {
                    SkipWs();
                    if (_i >= _s.Length) break;
                    char c = _s[_i];
                    if (c == '}') { _i++; return dict; }
                    if (c == ',') { _i++; continue; }
                    if (c != '"') Throw("Expected string key");
                    string key = ParseString();
                    SkipWs();
                    if (_i >= _s.Length || _s[_i] != ':') Throw("Expected ':'");
                    _i++;
                    object val = ParseValue();
                    dict[key] = val;
                }
                Throw("Unterminated object");
                return dict;
            }

            private List<object> ParseArray()
            {
                var list = new List<object>();
                _i++; // '['
                while (true)
                {
                    SkipWs();
                    if (_i >= _s.Length) break;
                    char c = _s[_i];
                    if (c == ']') { _i++; return list; }
                    if (c == ',') { _i++; continue; }
                    list.Add(ParseValue());
                }
                Throw("Unterminated array");
                return list;
            }

            private string ParseString()
            {
                _sb.Length = 0;
                _i++; // opening "
                while (_i < _s.Length)
                {
                    char c = _s[_i++];
                    if (c == '"') return _sb.ToString();
                    if (c == '\\' && _i < _s.Length)
                    {
                        char esc = _s[_i++];
                        switch (esc)
                        {
                            case '"': _sb.Append('"'); break;
                            case '\\': _sb.Append('\\'); break;
                            case '/': _sb.Append('/'); break;
                            case 'b': _sb.Append('\b'); break;
                            case 'f': _sb.Append('\f'); break;
                            case 'n': _sb.Append('\n'); break;
                            case 'r': _sb.Append('\r'); break;
                            case 't': _sb.Append('\t'); break;
                            case 'u':
                                if (_i + 4 <= _s.Length)
                                {
                                    int code = int.Parse(_s.Substring(_i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                                    _sb.Append((char)code);
                                    _i += 4;
                                }
                                break;
                            default: _sb.Append(esc); break;
                        }
                    }
                    else _sb.Append(c);
                }
                Throw("Unterminated string");
                return _sb.ToString();
            }

            private double ParseNumber()
            {
                int start = _i;
                if (_s[_i] == '-' || _s[_i] == '+') _i++;
                while (_i < _s.Length)
                {
                    char c = _s[_i];
                    if ((c >= '0' && c <= '9') || c == '.' || c == 'e' || c == 'E' || c == '+' || c == '-') _i++;
                    else break;
                }
                return double.Parse(_s.Substring(start, _i - start), NumberStyles.Float, CultureInfo.InvariantCulture);
            }

            private bool ParseBool()
            {
                if (_s[_i] == 't') { _i += 4; return true; }
                _i += 5; return false;
            }

            private object ParseNull() { _i += 4; return null; }

            private void SkipWs()
            {
                while (_i < _s.Length)
                {
                    char c = _s[_i];
                    if (c == ' ' || c == '\t' || c == '\n' || c == '\r') _i++;
                    else break;
                }
            }

            private void Throw(string msg)
            {
                throw new System.Exception($"[Mynell.MiniJson] {msg} at index {_i}");
            }
        }
    }
}
