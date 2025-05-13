using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace AaTurpin.SnapshotManager
{
    /// <summary>
    /// Simple JSON reader for parsing JSON strings
    /// </summary>
    internal class JsonReader
    {
        private readonly string _json;
        private int _position;

        public JsonReader(string json)
        {
            _json = json ?? "";
            _position = 0;
        }

        public object ReadValue()
        {
            SkipWhitespace();

            if (_position >= _json.Length)
                throw new InvalidOperationException("Unexpected end of JSON string");

            char c = _json[_position];

            if (c == '{')
                return ReadObject();
            else if (c == '[')
                return ReadArray();
            else if (c == '"')
                return ReadString();
            else if (c == 't' || c == 'f')
                return ReadBoolean();
            else if (c == 'n')
                return ReadNull();
            else if (char.IsDigit(c) || c == '-')
                return ReadNumber();
            else
                throw new InvalidOperationException($"Unexpected character '{c}' at position {_position}");
        }

        private Dictionary<string, object> ReadObject()
        {
            var result = new Dictionary<string, object>();

            // Skip '{'
            _position++;

            SkipWhitespace();

            // Empty object
            if (_position < _json.Length && _json[_position] == '}')
            {
                _position++;
                return result;
            }

            while (_position < _json.Length)
            {
                SkipWhitespace();

                // Property name
                if (_json[_position] != '"')
                    throw new InvalidOperationException($"Expected property name at position {_position}");

                var name = ReadString();

                SkipWhitespace();

                // Colon separator
                if (_position >= _json.Length || _json[_position] != ':')
                    throw new InvalidOperationException($"Expected ':' at position {_position}");

                _position++; // Skip ':'

                SkipWhitespace();

                // Property value
                var value = ReadValue();
                result[name] = value;

                SkipWhitespace();

                // End of object or next property
                if (_position >= _json.Length)
                    throw new InvalidOperationException("Unexpected end of JSON string");

                if (_json[_position] == '}')
                {
                    _position++;
                    return result;
                }

                if (_json[_position] == ',')
                {
                    _position++;
                    continue;
                }

                throw new InvalidOperationException($"Expected ',' or '}}' at position {_position}");
            }

            throw new InvalidOperationException("Unexpected end of JSON string");
        }

        private List<object> ReadArray()
        {
            var result = new List<object>();

            // Skip '['
            _position++;

            SkipWhitespace();

            // Empty array
            if (_position < _json.Length && _json[_position] == ']')
            {
                _position++;
                return result;
            }

            while (_position < _json.Length)
            {
                SkipWhitespace();

                // Array element
                var value = ReadValue();
                result.Add(value);

                SkipWhitespace();

                // End of array or next element
                if (_position >= _json.Length)
                    throw new InvalidOperationException("Unexpected end of JSON string");

                if (_json[_position] == ']')
                {
                    _position++;
                    return result;
                }

                if (_json[_position] == ',')
                {
                    _position++;
                    continue;
                }

                throw new InvalidOperationException($"Expected ',' or ']' at position {_position}");
            }

            throw new InvalidOperationException("Unexpected end of JSON string");
        }

        private string ReadString()
        {
            // Skip opening quote
            _position++;

            var start = _position;
            var sb = new StringBuilder();
            var isEscaped = false;

            while (_position < _json.Length)
            {
                var c = _json[_position];

                if (isEscaped)
                {
                    switch (c)
                    {
                        case '"':
                        case '\\':
                        case '/':
                            sb.Append(c);
                            break;
                        case 'b':
                            sb.Append('\b');
                            break;
                        case 'f':
                            sb.Append('\f');
                            break;
                        case 'n':
                            sb.Append('\n');
                            break;
                        case 'r':
                            sb.Append('\r');
                            break;
                        case 't':
                            sb.Append('\t');
                            break;
                        case 'u':
                            if (_position + 4 < _json.Length)
                            {
                                var hexValue = _json.Substring(_position + 1, 4);
                                sb.Append((char)Convert.ToInt32(hexValue, 16));
                                _position += 4;
                            }
                            break;
                        default:
                            sb.Append(c);
                            break;
                    }

                    isEscaped = false;
                }
                else if (c == '\\')
                {
                    isEscaped = true;
                }
                else if (c == '"')
                {
                    _position++;
                    return sb.ToString();
                }
                else
                {
                    sb.Append(c);
                }

                _position++;
            }

            throw new InvalidOperationException("Unterminated string");
        }

        private bool ReadBoolean()
        {
            if (_position + 3 < _json.Length && _json.Substring(_position, 4) == "true")
            {
                _position += 4;
                return true;
            }
            else if (_position + 4 < _json.Length && _json.Substring(_position, 5) == "false")
            {
                _position += 5;
                return false;
            }
            else
            {
                throw new InvalidOperationException($"Invalid boolean value at position {_position}");
            }
        }

        private object ReadNull()
        {
            if (_position + 3 < _json.Length && _json.Substring(_position, 4) == "null")
            {
                _position += 4;
                return null;
            }
            else
            {
                throw new InvalidOperationException($"Invalid null value at position {_position}");
            }
        }

        private object ReadNumber()
        {
            var start = _position;
            var isFloat = false;

            // Skip minus sign
            if (_json[_position] == '-')
                _position++;

            // Integer part
            while (_position < _json.Length && char.IsDigit(_json[_position]))
                _position++;

            // Fractional part
            if (_position < _json.Length && _json[_position] == '.')
            {
                isFloat = true;
                _position++;

                while (_position < _json.Length && char.IsDigit(_json[_position]))
                    _position++;
            }

            // Exponent part
            if (_position < _json.Length && (_json[_position] == 'e' || _json[_position] == 'E'))
            {
                isFloat = true;
                _position++;

                if (_position < _json.Length && (_json[_position] == '+' || _json[_position] == '-'))
                    _position++;

                while (_position < _json.Length && char.IsDigit(_json[_position]))
                    _position++;
            }

            var numStr = _json.Substring(start, _position - start);

            if (isFloat)
            {
                return double.Parse(numStr, CultureInfo.InvariantCulture);
            }
            else
            {
                // Try to parse as Int32 first, then fall back to Int64
                if (int.TryParse(numStr, out int intValue))
                    return intValue;
                else
                    return long.Parse(numStr, CultureInfo.InvariantCulture);
            }
        }

        private void SkipWhitespace()
        {
            while (_position < _json.Length && char.IsWhiteSpace(_json[_position]))
                _position++;
        }
    }
}
