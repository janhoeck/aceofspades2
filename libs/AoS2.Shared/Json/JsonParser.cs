using System;
using System.Globalization;
using System.Text;

namespace AoS2.Shared.Json
{
    /// <summary>Liest JSON-Text in einen <see cref="JsonValue"/>-Baum. Fehler nennen Zeile und Spalte.</summary>
    public sealed class JsonParser
    {
        private readonly string _text;
        private int _pos;

        private JsonParser(string text)
        {
            _text = text;
        }

        public static JsonValue Parse(string text)
        {
            if (text == null) throw new ArgumentNullException(nameof(text));
            var parser = new JsonParser(text);
            // Byte Order Mark überspringen, falls die Datei eines hat.
            if (text.Length > 0 && text[0] == '﻿') parser._pos = 1;
            var value = parser.ReadValue();
            parser.SkipWhitespace();
            if (parser._pos < text.Length) throw parser.Error("Unerwartete Zeichen nach dem Ende");
            return value;
        }

        private JsonValue ReadValue()
        {
            SkipWhitespace();
            if (_pos >= _text.Length) throw Error("Unerwartetes Ende");
            var c = _text[_pos];
            switch (c)
            {
                case '{': return ReadObject();
                case '[': return ReadArray();
                case '"': return JsonValue.String(ReadString());
                case 't': Expect("true"); return JsonValue.Bool(true);
                case 'f': Expect("false"); return JsonValue.Bool(false);
                case 'n': Expect("null"); return JsonValue.Null;
                default:
                    if (c == '-' || (c >= '0' && c <= '9')) return ReadNumber();
                    throw Error($"Unerwartetes Zeichen '{c}'");
            }
        }

        private JsonValue ReadObject()
        {
            var obj = JsonValue.NewObject();
            _pos++; // {
            SkipWhitespace();
            if (Peek() == '}')
            {
                _pos++;
                return obj;
            }
            while (true)
            {
                SkipWhitespace();
                if (Peek() != '"') throw Error("Name in Anführungszeichen erwartet");
                var name = ReadString();
                SkipWhitespace();
                if (Peek() != ':') throw Error("':' erwartet");
                _pos++;
                obj.Set(name, ReadValue());
                SkipWhitespace();
                var c = Peek();
                _pos++;
                if (c == '}') return obj;
                if (c != ',') throw Error("',' oder '}' erwartet", -1);
            }
        }

        private JsonValue ReadArray()
        {
            var arr = JsonValue.NewArray();
            _pos++; // [
            SkipWhitespace();
            if (Peek() == ']')
            {
                _pos++;
                return arr;
            }
            while (true)
            {
                arr.Add(ReadValue());
                SkipWhitespace();
                var c = Peek();
                _pos++;
                if (c == ']') return arr;
                if (c != ',') throw Error("',' oder ']' erwartet", -1);
            }
        }

        private string ReadString()
        {
            _pos++; // "
            var sb = new StringBuilder();
            while (true)
            {
                if (_pos >= _text.Length) throw Error("Text ohne schließendes Anführungszeichen");
                var c = _text[_pos++];
                if (c == '"') return sb.ToString();
                if (c != '\\')
                {
                    sb.Append(c);
                    continue;
                }
                if (_pos >= _text.Length) throw Error("Unvollständige Escape-Sequenz");
                var e = _text[_pos++];
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
                        if (_pos + 4 > _text.Length) throw Error("Unvollständige \\u-Sequenz");
                        sb.Append((char)int.Parse(_text.Substring(_pos, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        _pos += 4;
                        break;
                    default: throw Error($"Unbekannte Escape-Sequenz \\{e}", -1);
                }
            }
        }

        private JsonValue ReadNumber()
        {
            var start = _pos;
            if (Peek() == '-') _pos++;
            while (_pos < _text.Length)
            {
                var c = _text[_pos];
                if ((c >= '0' && c <= '9') || c == '.' || c == 'e' || c == 'E' || c == '+' || c == '-') _pos++;
                else break;
            }
            var s = _text.Substring(start, _pos - start);
            if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                throw Error($"Ungültige Zahl '{s}'", start - _pos);
            return JsonValue.Number(value);
        }

        private void Expect(string word)
        {
            if (string.CompareOrdinal(_text, _pos, word, 0, word.Length) != 0) throw Error($"'{word}' erwartet");
            _pos += word.Length;
        }

        private char Peek() => _pos < _text.Length ? _text[_pos] : '\0';

        private void SkipWhitespace()
        {
            while (_pos < _text.Length && (_text[_pos] == ' ' || _text[_pos] == '\t' || _text[_pos] == '\n' || _text[_pos] == '\r'))
                _pos++;
        }

        private FormatException Error(string message, int offset = 0)
        {
            var at = Math.Max(0, Math.Min(_text.Length, _pos + offset));
            int line = 1, column = 1;
            for (var i = 0; i < at; i++)
            {
                if (_text[i] == '\n')
                {
                    line++;
                    column = 1;
                }
                else column++;
            }
            return new FormatException($"JSON: {message} (Zeile {line}, Spalte {column}).");
        }
    }
}
