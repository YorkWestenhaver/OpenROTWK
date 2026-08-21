#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace OpenSage.Content.Translation.Providers;

public sealed class StrTranslationProvider : ATranslationProviderBase
{
    private class Str
    {
        private enum State
        {
            Begin,
            CommentBegin,
            Category,
            Label,
            PreValue,
            CommentPreValue,
            Value,
            String,
            End1,
            End2,
            End3
        }

        internal readonly Dictionary<string, Dictionary<string, string>> _strings;

        internal int _numStrings;
        internal readonly string _language;

        public Str(string language)
        {
            _language = language;
            _strings = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        }


        private static NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

        public static void ReadStr(Str str, Stream stream, string language)
        {
            var category = string.Empty;
            var label = string.Empty;
            var value = string.Empty;
            var sb = new StringBuilder();
            var state = State.Begin;
            char c;
            var reader = new BinaryReader(stream, Encoding.ASCII);
            var isEscaped = false;
            // Whether the next character in the End states is the first non-blank one on its line.
            // END is only recognised there, as in the retail parser.
            var atLineStart = false;
            while (stream.Position < stream.Length)
            {
                c = reader.ReadChar();
                switch (state)
                {
                    case State.CommentBegin:
                        if (c == '\n' || c == '\r')
                        {
                            state = State.Begin;
                        }
                        break;
                    case State.CommentPreValue:
                        if (c == '\n' || c == '\r')
                        {
                            state = State.PreValue;
                        }
                        break;
                    case State.Begin:
                        if (char.IsWhiteSpace(c))
                        {
                            continue;
                        }
                        else if (c == '/')
                        {
                            c = reader.ReadChar();
                            if (c == '/')
                            {
                                state = State.CommentBegin;
                            }
                        }
                        else if (c == ';')
                        {
                            state = State.CommentBegin;
                        }
                        // TODO: multiline comments
                        else if ((c >= '0' && c <= '9') || (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z'))
                        {
                            state = State.Category;
                            sb.Append(c);
                        }
                        else
                        {
                            throw new InvalidDataException($"Unexpected token {c}.");
                        }
                        break;
                    case State.Category:
                        if (c == ':')
                        {
                            category = sb.ToString();
                            sb.Clear();
                            state = State.Label;
                        }
                        else if (c == '\n' || c == '\r')
                        {
                            // A label line with no `CATEGORY:` prefix. Age of the Ring's lotr.str
                            // contains one (`TooltipRisenCarrionDebuff`, where every neighbour is
                            // `CONTROLBAR:...`). The retail parser keeps the whole line as the
                            // label, so the entry exists but no `CATEGORY:LABEL` lookup can reach
                            // it; do the same instead of failing the entire string table.
                            category = string.Empty;
                            label = sb.ToString().TrimEnd();
                            sb.Clear();
                            state = State.PreValue;
                        }
                        else if (c == ' ' || c == '\t' || (c >= '!' && c <= 'z'))
                        {
                            sb.Append(c);
                        }
                        else
                        {
                            throw new InvalidDataException($"Unexpected token {c}.");
                        }
                        break;
                    case State.Label:
                        // A label runs to the end of its line, spaces included. The retail parser
                        // is line-based (GameTextManager::parseStringFile takes the whole trimmed
                        // line as the label), and content relies on it: Age of the Ring's
                        // lotr.str names campaign maps as `Map:MAP GOOD REDHORN`, and its map.str
                        // files carry script labels like `SCRIPT:Hint_HelmsDeep_Start Fight`.
                        // Ending the label at the first space instead made the rest of the line
                        // parse as a value, and the leftovers then desynced the state machine
                        // (`REDHORN` -> `E` `D` -> "Unexpected token D." while looking for END).
                        if (c == '\n' || c == '\r')
                        {
                            label = sb.ToString().TrimEnd();
                            sb.Clear();
                            state = State.PreValue;
                        }
                        else if (c == '"')
                        {
                            // Tolerated non-retail shape: value quoted on the label's own line.
                            label = sb.ToString().TrimEnd();
                            sb.Clear();
                            state = State.String;
                        }
                        else if (c == ' ' || c == '\t' || (c >= '!' && c <= 'z'))
                        {
                            sb.Append(c);
                        }
                        else
                        {
                            throw new InvalidDataException($"Unexpected token {c}.");
                        }
                        break;
                    case State.PreValue:
                        if (char.IsWhiteSpace(c))
                        {
                            continue;
                        }
                        else if (c == '/')
                        {
                            c = reader.ReadChar();
                            if (c == '/')
                            {
                                state = State.CommentPreValue;
                            }
                        }
                        else if (c == ';')
                        {
                            state = State.CommentPreValue;
                        }
                        // TODO: multiline comments
                        else if ((c >= '0' && c <= '9') || (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z'))
                        {
                            state = State.Value;
                            sb.Append(c);
                        }
                        else if (c == '"')
                        {
                            state = State.String;
                        }
                        else
                        {
                            throw new InvalidDataException($"Unexpected token {c}.");
                        }
                        break;
                    case State.Value:
                        if ((c >= '0' && c <= '9') || (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z'))
                        {
                            sb.Append(c);
                        }
                        else if (char.IsWhiteSpace(c))
                        {
                            value = sb.ToString();
                            sb.Clear();
                            state = State.End1;
                            atLineStart = c == '\n' || c == '\r';
                        }
                        else
                        {
                            throw new InvalidDataException($"Unexpected token {c}.");
                        }
                        break;
                    case State.String:
                        if (isEscaped)
                        {
                            switch (c)
                            {
                                case 'n':
                                    sb.Append('\n');
                                    break;
                                case 'r':
                                    sb.Append('\r');
                                    break;
                                case 't':
                                    sb.Append('\t');
                                    break;
                                case 'v':
                                    sb.Append('\v');
                                    break;
                                case '\\':
                                    sb.Append('\\');
                                    break;
                                case '\'':
                                    sb.Append('\'');
                                    break;
                                case '"':
                                    sb.Append('"');
                                    break;
                                    // TODO: unicode escapes
                            }
                            isEscaped = false;
                        }
                        else if (c == '\\')
                        {
                            isEscaped = true;
                        }
                        else if (c == '"')
                        {
                            value = sb.ToString();
                            sb.Clear();
                            state = State.End1;
                            atLineStart = false;
                        }
                        else
                        {
                            sb.Append(c);
                        }
                        break;
                    // Everything between the value and the terminating END is skipped, a line at a
                    // time, and END is only recognised as the first word on a line. This mirrors
                    // the retail parser, whose inner loop reads whole lines and compares each one
                    // against "END" (GameTextManager::parseStringFile), ignoring every other line.
                    // It is also what makes the tolerated malformations parse: BFME2's stock typo
                    // (a value ending in two '"'), and Age of the Ring values that contain an
                    // unescaped '"' - the value ends early at that quote, exactly as retail's
                    // readToEndOfQuote does, and the remaining prose is discarded instead of being
                    // scanned for an E-N-D triple wherever it happens to fall.
                    case State.End1:
                        if (c == '\n' || c == '\r')
                        {
                            atLineStart = true;
                        }
                        else if (atLineStart && char.IsWhiteSpace(c))
                        {
                            // Leading blanks don't end the line start.
                        }
                        else if (atLineStart && (c == 'E' || c == 'e'))
                        {
                            state = State.End2;
                        }
                        else
                        {
                            atLineStart = false;
                        }
                        break;
                    case State.End2:
                        if (c == 'N' || c == 'n')
                        {
                            state = State.End3;
                        }
                        else
                        {
                            // Not END after all - skip the rest of this line.
                            state = State.End1;
                            atLineStart = c == '\n' || c == '\r';
                        }
                        break;
                    case State.End3:
                        if (c == 'D' || c == 'd')
                        {
                            if (!str._strings.TryGetValue(category, out var dict))
                            {
                                dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                                str._strings.Add(category, dict);
                            }
                            if (dict.TryGetValue(label, out var dictValue))
                            {
                                Logger.Info($"[STR] String duplication: '{category}:{label}' -> '{dictValue}', new value: '{value}'");
                            }
                            else
                            {
                                dict.Add(label, value);
                                ++str._numStrings;
                            }
                            state = State.Begin;
                        }
                        else
                        {
                            // Not END after all - skip the rest of this line.
                            state = State.End1;
                            atLineStart = c == '\n' || c == '\r';
                        }
                        break;
                }
            }
        }

        public bool TryGetString(string str, out string? result)
        {
            var colonIdx = str.IndexOf(':');
            var label = string.Empty;
            if (colonIdx == -1)
            {
                result = str;
                return false;
            }
            label = str.Substring(0, colonIdx);
            if (_strings.TryGetValue(label, out var category) && category.TryGetValue(str.Substring(colonIdx + 1), out result))
            {
                return true;
            }
            result = null;
            return false;
        }
    }

    private readonly Str _str;

    public override string Name => NameOverride ?? _str._language;
    public override IReadOnlyCollection<string> Labels
    {
        get
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var label in _str._strings)
            {
                foreach (var str in label.Value)
                {
                    result.Add($"{label.Key}:{str.Key}");
                }
            }
            return result;
        }
    }

    public StrTranslationProvider(Stream stream, string language)
    {
        Debug.Assert(stream is not null, $"{nameof(stream)} is null");
        _str = new Str(language);
        Str.ReadStr(_str, stream, language);
    }

    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

    public override string? GetString(string str)
    {
        Debug.Assert(str is not null, $"{nameof(str)} is null");
        if (!_str.TryGetString(str, out var result))
        {
            Logger.Warn($"Requested string '{str}' not found in '{Name}'.");
        }
        return result;
    }

    public override string ToString()
    {
        return $"[STR: {Name} - {_str._numStrings} strings in {_str._strings.Count} categories]";
    }
}
