using System.Globalization;
using DawnPlayer.Core.Models;

namespace DawnPlayer.Core.Playlists;

/// <summary>Comparison operators a query expression can use.</summary>
internal enum QueryOp { Is, NotIs, Eq, Ne, Gt, Ge, Lt, Le, Has }

/// <summary>
/// A parsed foobar2000-style query used by user smart playlists. Supports a practical subset of
/// the foobar syntax:
/// <code>
/// %rating% GREATER 3 AND %genre% HAS jazz
/// %artist% IS "Norma Jean Wright" OR (%album% HAS Remix AND NOT %rating% MISSING)
/// %last_played% DURING LAST 30 DAYS AND %play_count% GREATER 2 LIMIT 50
/// </code>
/// Strings compare case-insensitively; numeric fields compare numerically; date fields
/// (<c>last_played</c>, <c>first_seen</c>) accept ISO dates or <c>DURING LAST n DAYS|WEEKS|MONTHS</c>.
/// Parsing is total (errors report a message) and evaluation never throws.
/// </summary>
public sealed class SmartPlaylistQuery
{
    private readonly Node _root;

    private SmartPlaylistQuery(Node root, int? limit, string source)
    {
        _root = root;
        Limit = limit;
        Source = source;
    }

    /// <summary>Optional row cap requested through a trailing <c>LIMIT n</c> clause.</summary>
    public int? Limit { get; }

    /// <summary>The query text this was parsed from.</summary>
    public string Source { get; }

    public static bool TryParse(string? text, out SmartPlaylistQuery? query, out string? error)
    {
        query = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "The query is empty.";
            return false;
        }

        var tokens = Tokenizer.Tokenize(text, out error);
        if (tokens == null) return false;

        var parser = new Parser(tokens);
        Node root = parser.ParseOr();
        if (parser.Error != null)
        {
            error = parser.Error;
            return false;
        }

        int? limit = null;
        if (parser.PeekIsKeyword("limit"))
        {
            parser.Next();
            if (parser.PeekIsWord(out var countToken) &&
                int.TryParse(countToken, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) &&
                n > 0)
            {
                limit = n;
                parser.Next();
            }
            else
            {
                error = "LIMIT needs a positive number.";
                return false;
            }
        }

        if (!parser.AtEnd)
        {
            error = $"Unexpected '{parser.PeekText}' after the end of the query.";
            return false;
        }

        query = new SmartPlaylistQuery(root, limit, text.Trim());
        error = null;
        return true;
    }

    /// <summary>Filters <paramref name="source"/> (in its given order) through the query.</summary>
    public IEnumerable<Track> Apply(IEnumerable<Track> source)
    {
        IEnumerable<Track> filtered = source.Where(t => t != null && _root.Matches(t));
        return Limit is int n ? filtered.Take(n) : filtered;
    }

    public override string ToString() => Source;

    // ---------------- token model ----------------

    private enum TokenKind { Word, Quoted, Symbol, LParen, RParen }

    private readonly record struct Token(TokenKind Kind, string Text, int Position);

    private static bool IsKeyword(Token t, string keyword) =>
        t.Kind == TokenKind.Word && string.Equals(t.Text, keyword, StringComparison.OrdinalIgnoreCase);

    private static bool IsSymbol(Token t, string symbol) =>
        t.Kind == TokenKind.Symbol && t.Text == symbol;

    private static class Tokenizer
    {
        public static List<Token>? Tokenize(string text, out string? error)
        {
            var tokens = new List<Token>();
            int i = 0;
            while (i < text.Length)
            {
                char c = text[i];
                if (char.IsWhiteSpace(c)) { i++; continue; }

                if (c == '(') { tokens.Add(new(TokenKind.LParen, "(", i++)); continue; }
                if (c == ')') { tokens.Add(new(TokenKind.RParen, ")", i++)); continue; }

                if (c == '"')
                {
                    int start = i + 1;
                    int end = text.IndexOf('"', start);
                    if (end < 0)
                    {
                        error = "A quoted value is missing its closing '\"'.";
                        return null;
                    }
                    tokens.Add(new(TokenKind.Quoted, text[start..end], start));
                    i = end + 1;
                    continue;
                }

                // Multi-char symbol operators before single-char, so ">=" never reads as ">".
                if (c is '=' or '!' or '<' or '>')
                {
                    string pair = i + 1 < text.Length ? text.Substring(i, 2) : c.ToString();
                    if (pair is "==" or "!=" or ">=" or "<=")
                    {
                        tokens.Add(new(TokenKind.Symbol, pair, i));
                        i += 2;
                    }
                    else
                    {
                        tokens.Add(new(TokenKind.Symbol, c.ToString(), i));
                        i++;
                    }
                    continue;
                }

                if (c == '%' && TryReadFieldName(text, ref i, out var name))
                {
                    tokens.Add(new(TokenKind.Word, name, i - name.Length - 2));
                    continue;
                }

                int wordStart = i;
                while (i < text.Length && !char.IsWhiteSpace(text[i]) &&
                       text[i] is not '(' and not ')' and not '"' and not '%')
                {
                    i++;
                }
                if (i == wordStart)
                {
                    error = $"Unexpected character '{c}'.";
                    return null;
                }
                tokens.Add(new(TokenKind.Word, text[wordStart..i], wordStart));
            }

            error = null;
            return tokens;
        }

        /// <summary>Reads a <c>%field%</c> reference, returning the inner name as a plain word.</summary>
        private static bool TryReadFieldName(string text, ref int i, out string name)
        {
            int close = text.IndexOf('%', i + 1);
            if (close <= i + 1)
            {
                name = "";
                return false;
            }
            name = text[(i + 1)..close];
            i = close + 1;
            return name.Length > 0;
        }
    }

    // ---------------- parser ----------------

    private sealed class Parser
    {
        private readonly List<Token> _tokens;
        private int _pos;

        public Parser(List<Token> tokens) => _tokens = tokens;

        public string? Error { get; private set; }

        public bool AtEnd => _pos >= _tokens.Count;

        public string? PeekText => _pos < _tokens.Count ? _tokens[_pos].Text : null;

        public bool PeekIsKeyword(string keyword) =>
            _pos < _tokens.Count && IsKeyword(_tokens[_pos], keyword);

        public bool PeekIsWord(out string text)
        {
            text = "";
            if (_pos < _tokens.Count && _tokens[_pos].Kind == TokenKind.Word)
            {
                text = _tokens[_pos].Text;
                return true;
            }
            return false;
        }

        public Token Next() => _tokens[_pos++];

        private Token Expect(TokenKind kind, string what)
        {
            if (_pos < _tokens.Count && _tokens[_pos].Kind == kind) return _tokens[_pos++];
            Fail(_pos < _tokens.Count ? $"Expected {what} but found '{_tokens[_pos].Text}'." : $"Expected {what} but the query ended.");
            return new(kind, "", _pos);
        }

        private void Fail(string message) => Error ??= message;

        // or := and (OR and)*     — lowest precedence
        public Node ParseOr()
        {
            var left = ParseAnd();
            while (Error == null && PeekIsKeyword("or"))
            {
                Next();
                left = new OrNode(left, ParseAnd());
            }
            return left;
        }

        // and := not (AND not)*
        private Node ParseAnd()
        {
            var left = ParseNot();
            while (Error == null && PeekIsKeyword("and"))
            {
                Next();
                left = new AndNode(left, ParseNot());
            }
            return left;
        }

        // not := NOT not | primary
        private Node ParseNot()
        {
            if (PeekIsKeyword("not"))
            {
                Next();
                return new NotNode(ParseNot());
            }
            return ParsePrimary();
        }

        // primary := '(' or ')' | field op value | field MISSING | field PRESENT | field DURING LAST n UNIT
        private Node ParsePrimary()
        {
            if (Error != null) return new TrueNode();

            if (_pos < _tokens.Count && _tokens[_pos].Kind == TokenKind.LParen)
            {
                Next();
                var inner = ParseOr();
                if (Error == null) Expect(TokenKind.RParen, "')'");
                return inner;
            }

            if (!PeekIsWord(out var field))
            {
                Fail(_pos < _tokens.Count
                    ? $"Expected a field name (e.g. %rating%) but found '{_tokens[_pos].Text}'."
                    : "Expected a field name (e.g. %rating%) but the query ended.");
                return new TrueNode();
            }

            var fieldToken = Next();

            if (PeekIsKeyword("missing") || PeekIsKeyword("present"))
            {
                bool missing = PeekIsKeyword("missing");
                Next();
                if (!Fields.IsKnown(fieldToken.Text))
                {
                    Fail($"Unknown field '{fieldToken.Text}'.");
                    return new TrueNode();
                }
                return new PresenceNode(fieldToken.Text, missing);
            }

            if (PeekIsKeyword("during"))
            {
                Next();
                if (!PeekIsKeyword("last"))
                {
                    Fail("DURING must be followed by 'LAST <number> DAYS|WEEKS|MONTHS'.");
                    return new TrueNode();
                }
                Next();
                if (!PeekIsWord(out var countText) ||
                    !int.TryParse(countText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) ||
                    count <= 0)
                {
                    Fail("DURING LAST needs a positive number of days, weeks or months.");
                    return new TrueNode();
                }
                Next();
                if (!PeekIsWord(out var unit) ||
                    !TryUnitToDays(unit, out var days))
                {
                    Fail("The time unit must be DAYS, WEEKS or MONTHS.");
                    return new TrueNode();
                }
                Next();

                if (!Fields.IsDateField(fieldToken.Text))
                {
                    Fail($"DURING LAST only applies to date fields ({Fields.LastPlayed}, {Fields.FirstSeen}).");
                    return new TrueNode();
                }
                // "3 WEEKS" = 3 × 7 days — the unit converts to days, the count scales it.
                return new DuringNode(fieldToken.Text, count * days);
            }

            QueryOp op;
            if (_pos >= _tokens.Count)
            {
                Fail("Expected an operator such as IS, HAS or >= after the field name.");
                return new TrueNode();
            }

            var t = _tokens[_pos];
            if (IsKeyword(t, "is")) { op = QueryOp.Is; Next(); }
            else if (IsKeyword(t, "has")) { op = QueryOp.Has; Next(); }
            else if (IsKeyword(t, "greater")) { op = QueryOp.Gt; Next(); }
            else if (IsKeyword(t, "less")) { op = QueryOp.Lt; Next(); }
            else if (IsSymbol(t, "==")) { op = QueryOp.Eq; Next(); }
            else if (IsSymbol(t, "!=")) { op = QueryOp.Ne; Next(); }
            else if (IsSymbol(t, ">=")) { op = QueryOp.Ge; Next(); }
            else if (IsSymbol(t, "<=")) { op = QueryOp.Le; Next(); }
            else if (IsSymbol(t, ">")) { op = QueryOp.Gt; Next(); }
            else if (IsSymbol(t, "<")) { op = QueryOp.Lt; Next(); }
            else
            {
                Fail($"Expected an operator such as IS, HAS or >= but found '{t.Text}'.");
                return new TrueNode();
            }

            string? value = null;
            if (_pos < _tokens.Count && _tokens[_pos].Kind is TokenKind.Word or TokenKind.Quoted)
            {
                value = Next().Text;
            }
            if (value == null)
            {
                Fail("Expected a value after the operator.");
                return new TrueNode();
            }

            if (!Fields.IsKnown(fieldToken.Text))
            {
                Fail($"Unknown field '{fieldToken.Text}'.");
                return new TrueNode();
            }

            if ((op is QueryOp.Gt or QueryOp.Ge or QueryOp.Lt or QueryOp.Le) &&
                Fields.IsTextField(fieldToken.Text))
            {
                Fail($"'{fieldToken.Text}' is a text field; use IS or HAS instead of an ordering operator.");
                return new TrueNode();
            }

            return new CmpNode(fieldToken.Text, op, value);
        }

        private static bool TryUnitToDays(string unit, out int days)
        {
            switch (unit.ToUpperInvariant())
            {
                case "DAY":
                case "DAYS": days = 1; return true;
                case "WEEK":
                case "WEEKS": days = 7; return true;
                case "MONTH":
                case "MONTHS": days = 30; return true;
                default: days = 0; return false;
            }
        }
    }

    // ---------------- evaluation ----------------

    private abstract class Node
    {
        public abstract bool Matches(Track track);
    }

    private sealed class TrueNode : Node
    {
        public override bool Matches(Track track) => true;
    }

    private sealed class AndNode(Node Left, Node Right) : Node
    {
        public override bool Matches(Track track) => Left.Matches(track) && Right.Matches(track);
    }

    private sealed class OrNode(Node Left, Node Right) : Node
    {
        public override bool Matches(Track track) => Left.Matches(track) || Right.Matches(track);
    }

    private sealed class NotNode(Node Child) : Node
    {
        public override bool Matches(Track track) => !Child.Matches(track);
    }

    private sealed class PresenceNode(string Field, bool Missing) : Node
    {
        public override bool Matches(Track track)
        {
            var v = Fields.Get(track, Field);
            bool present = v.IsPresent;
            return Missing ? !present : present;
        }
    }

    private sealed class DuringNode(string Field, int Days) : Node
    {
        public override bool Matches(Track track)
        {
            var v = Fields.Get(track, Field);
            if (!v.Number.HasValue) return false; // never played / unknown date → not "during"
            var cutoff = DateTime.UtcNow - TimeSpan.FromDays(Days);
            return v.Number.Value >= cutoff.Ticks;
        }
    }

    private sealed class CmpNode(string Field, QueryOp Op, string Value) : Node
    {
        public override bool Matches(Track track)
        {
            var v = Fields.Get(track, Field);

            if (Fields.IsNumericField(Field))
            {
                double? target = ParseNumber(Value);
                if (target == null) return false; // numeric field compared to non-number: no match
                double? actual = v.Number;
                return (Op, actual) switch
                {
                    (QueryOp.Is or QueryOp.Eq, not null) => actual.Value == target.Value,
                    (QueryOp.Ne or QueryOp.NotIs, _) => actual != target,
                    (QueryOp.Gt, not null) => actual.Value > target.Value,
                    (QueryOp.Ge, not null) => actual.Value >= target.Value,
                    (QueryOp.Lt, not null) => actual.Value < target.Value,
                    (QueryOp.Le, not null) => actual.Value <= target.Value,
                    _ => Op == QueryOp.Ne || Op == QueryOp.NotIs, // missing field: only != "matches"
                };
            }

            // Text comparison: ordinal-ignore-case. IS requires the whole value, HAS a substring.
            // A missing field compares as the empty string, which IS/HAS reject but != accepts.
            string actualText = v.Text ?? "";
            return Op switch
            {
                QueryOp.Is or QueryOp.Eq => string.Equals(actualText, Value, StringComparison.OrdinalIgnoreCase),
                QueryOp.Ne or QueryOp.NotIs => !string.Equals(actualText, Value, StringComparison.OrdinalIgnoreCase),
                QueryOp.Has => actualText.Contains(Value, StringComparison.OrdinalIgnoreCase),
                _ => false,
            };
        }
    }

    private static double? ParseNumber(string s)
    {
        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var n)) return n;
        if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date)) return date.Ticks;
        return null;
    }

    /// <summary>Field name resolution shared by the parser (validation) and the evaluator.</summary>
    private static class Fields
    {
        public const string LastPlayed = "last_played";
        public const string FirstSeen = "first_seen";

        private static readonly string[] DateFields = { LastPlayed, FirstSeen };

        private static readonly HashSet<string> NumericFields = new(StringComparer.OrdinalIgnoreCase)
        {
            "rating", "play_count", "plays", "skip_count", "skips", "duration", "length",
            "year", "date", "trackno", "track", "discno", "disc",
            LastPlayed, FirstSeen,
        };

        private static readonly HashSet<string> KnownFields = new(StringComparer.OrdinalIgnoreCase)
        {
            "title", "artist", "album", "albumartist", "album_artist", "genre", "path", "filename",
            "codec", "format",
        };

        static Fields() => KnownFields.UnionWith(NumericFields);

        public static bool IsKnown(string field) =>
            KnownFields.Contains(field) || NumericFields.Contains(field);

        public static bool IsTextField(string field) =>
            !NumericFields.Contains(field);

        public static bool IsDateField(string field) =>
            DateFields.Contains(field, StringComparer.OrdinalIgnoreCase);

        public static bool IsNumericField(string field) =>
            NumericFields.Contains(field);

        public readonly record struct FieldValue(string? Text, double? Number)
        {
            public bool IsPresent => (Text != null && Text.Length > 0) || Number.HasValue;
        }

        public static FieldValue Get(Track t, string field)
        {
            switch (field.ToLowerInvariant())
            {
                case "title": return new(NullIfEmpty(t.Title), null);
                case "artist": return new(NullIfEmpty(t.Artist), null);
                case "albumartist" or "album_artist": return new(NullIfEmpty(t.AlbumArtist), null);
                case "album": return new(NullIfEmpty(t.Album), null);
                case "genre": return new(NullIfEmpty(t.Genre), null);
                case "path" or "filename": return new(NullIfEmpty(t.Path), null);
                case "codec" or "format": return new(NullIfEmpty(t.Codec), null);
                case "rating": return t.Rating > 0 ? new(null, t.Rating) : new(null, null);
                case "play_count" or "plays": return new(null, t.PlayCount);
                case "skip_count" or "skips": return new(null, t.SkipCount);
                case "duration" or "length": return new(null, t.Duration.TotalSeconds);
                case "year" or "date": return t.Year > 0 ? new(null, t.Year) : new(null, null);
                case "trackno" or "track": return t.TrackNo > 0 ? new(null, t.TrackNo) : new(null, null);
                case "discno" or "disc": return t.DiscNo > 0 ? new(null, t.DiscNo) : new(null, null);
                case LastPlayed: return t.LastPlayedUtcTicks > 0 ? new(null, (double)t.LastPlayedUtcTicks) : new(null, null);
                case FirstSeen: return t.FirstSeenUtcTicks > 0 ? new(null, (double)t.FirstSeenUtcTicks) : new(null, null);
                default: return new(null, null);
            }
        }

        private static string? NullIfEmpty(string? s) =>
            string.IsNullOrEmpty(s) ? null : s;
    }
}
