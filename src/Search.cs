using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

record Unit(string Text, string Field = "content", string Source = "text",
    int? Page = null, int? Line = null, int? Paragraph = null, string? Section = null);
record Hit(int Rank, int Distance, string[] Words, int Offset)
{
    public string Kind => Rank switch { 0 => "exact", 1 => "spelling_variant", 2 => "ocr_variant", _ => "fuzzy" };
}

sealed class Search(Options options)
{
    private static readonly Regex Space = new(@"[^\S ]\s*| {2,}\s*| (?=[^\S ])[^\S ]\s*");
    private static readonly Regex Hyphen = new(@"(?<=\p{L})-\r?\n[ \t]*(?=\p{L})");
    private static readonly Regex Spelling = new("ä|ö|ü|ß|ae|oe|ue|ss", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private readonly string[] terms = options.Query.Trim().Normalize().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
    private readonly string phrase = Space.Replace(options.Query.Trim().Normalize(), " ");

    private readonly record struct TextSpan(int Index, int Length);

    private static bool WordRune(Rune rune) => Rune.GetUnicodeCategory(rune) is
        UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter or
        UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter or UnicodeCategory.DecimalDigitNumber or
        UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark;

    public static bool IsWord(string value) => value.Length != 0 && value.EnumerateRunes().All(WordRune);

    private static bool WordBefore(string text, int index) =>
        index > 0 && Rune.DecodeLastFromUtf16(text.AsSpan(0, index), out Rune rune, out _) == System.Buffers.OperationStatus.Done &&
        WordRune(rune);

    private static bool WordAt(string text, int index) =>
        index < text.Length && Rune.TryGetRuneAt(text, index, out Rune rune) && WordRune(rune);

    private static TextSpan? Literal(string text, string term)
    {
        bool startBoundary = WordAt(term, 0);
        bool endBoundary = WordBefore(term, term.Length);
        int from = 0;
        while (from < text.Length)
        {
            int index = text.IndexOf(term, from, StringComparison.OrdinalIgnoreCase);
            if (index < 0) return null;
            if ((!startBoundary || !WordBefore(text, index)) && (!endBoundary || !WordAt(text, index + term.Length)))
                return new TextSpan(index, term.Length);
            from = index + 1;
        }
        return null;
    }

    private static IEnumerable<TextSpan> Words(string text)
    {
        int start = -1, offset = 0;
        foreach (Rune rune in text.EnumerateRunes())
        {
            if (WordRune(rune))
            {
                if (start < 0) start = offset;
            }
            else if (start >= 0)
            {
                yield return new TextSpan(start, offset - start);
                start = -1;
            }
            offset += rune.Utf16SequenceLength;
        }
        if (start >= 0) yield return new TextSpan(start, offset - start);
    }

    public Hit? Find(Unit unit, CancellationToken cancellation)
    {
        MappedText dehyphenated = MappedText.Normalize(unit.Text);
        if (unit.Page is not null) dehyphenated = dehyphenated.Replace(Hyphen, "");
        MappedText normalized = dehyphenated.Replace(Space, " ");
        string text = normalized.Value;
        if (options.Excludes.Any(word => Literal(text, word.Normalize()) is not null)) return null;
        if (options.Phrase)
        {
            return Literal(text, phrase) is { } match
                ? new Hit(0, 0, [normalized.Original(match.Index, match.Length)], normalized.Offset(match.Index))
                : null;
        }

        var selected = new List<(int Rank, int Distance, int At, string Word)>();
        TextSpan[]? spans = null;
        foreach (string term in terms)
        {
            cancellation.ThrowIfCancellationRequested();
            if (Literal(text, term) is { } exact)
            {
                selected.Add((0, 0, normalized.Offset(exact.Index), normalized.Original(exact.Index, exact.Length)));
                continue;
            }
            Rune[] runes = term.EnumerateRunes().ToArray();
            if (options.Exact || !IsWord(term) || runes.Any(Rune.IsDigit) ||
                (runes.Length is >= 2 and <= 5 && runes.All(Rune.IsUpper)))
                return null;

            string folded = Fold(term);
            spans ??= Words(text).ToArray();
            TextSpan variant = Array.Find(spans, word => Fold(text.Substring(word.Index, word.Length)) == folded);
            if (variant.Length > 0)
            {
                selected.Add((1, 0, normalized.Offset(variant.Index), normalized.Original(variant.Index, variant.Length)));
                continue;
            }
            if (unit.Source == "ocr")
            {
                string[] characters = runes.Select(rune => Regex.Escape(rune.ToString())).ToArray();
                string pattern = string.Join(@"[	 ]*", characters);
                Match ocr = Regex.Matches(dehyphenated.Value, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
                    .FirstOrDefault(match => !WordBefore(dehyphenated.Value, match.Index) &&
                        !WordAt(dehyphenated.Value, match.Index + match.Length)) ?? Match.Empty;
                if (ocr.Success)
                {
                    selected.Add((2, 0, dehyphenated.Offset(ocr.Index), dehyphenated.Original(ocr.Index, ocr.Length)));
                    continue;
                }
            }

            int limit = runes.Length < 4 ? 0 : runes.Length < 8 ? 1 : 2;
            if (limit == 0) return null;
            int best = limit + 1;
            TextSpan fuzzy = default;
            Rune[] query = term.ToUpperInvariant().EnumerateRunes().ToArray();
            foreach (TextSpan word in spans)
            {
                cancellation.ThrowIfCancellationRequested();
                if (Math.Abs(word.Length - term.Length) > limit * 2) continue;
                Rune[] candidate = text.Substring(word.Index, word.Length).ToUpperInvariant().EnumerateRunes().ToArray();
                if (Math.Abs(candidate.Length - query.Length) > limit) continue;
                int distance = Distance(query, candidate, limit);
                if (distance >= best) continue;
                best = distance;
                fuzzy = word;
                if (best == 1) break;
            }
            if (fuzzy.Length == 0) return null;
            selected.Add((3, best, normalized.Offset(fuzzy.Index), normalized.Original(fuzzy.Index, fuzzy.Length)));
        }
        var ordered = selected.OrderBy(item => item.At).ToArray();
        return new Hit(ordered.Max(item => item.Rank), ordered.Sum(item => item.Distance),
            ordered.Select(item => item.Word).ToArray(), ordered[0].At);
    }

    private static string Fold(string word) => Spelling.Replace(word.ToLowerInvariant(), match => match.Value switch
    {
        "ä" or "ae" => "ä", "ö" or "oe" => "ö", "ü" or "ue" => "ü", _ => "ß"
    });

    private static int Distance(Rune[] left, Rune[] right, int limit)
    {
        int outOfRange = limit + 1;
        int[] previousPrevious = new int[right.Length + 1];
        int[] previous = new int[right.Length + 1];
        int[] current = new int[right.Length + 1];
        for (int j = 0; j <= Math.Min(right.Length, limit + 1); j++) previous[j] = j;
        for (int i = 1; i <= left.Length; i++)
        {
            int start = Math.Max(1, i - limit);
            int end = Math.Min(right.Length, i + limit);
            current[start - 1] = start == 1 ? i : outOfRange;
            if (end < right.Length) current[end + 1] = outOfRange;
            for (int j = start; j <= end; j++)
            {
                int change = left[i - 1] == right[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1], previous[j]) + 1, previous[j - 1] + change);
                if (i > 1 && j > 1 && left[i - 1] == right[j - 2] && left[i - 2] == right[j - 1])
                    current[j] = Math.Min(current[j], previousPrevious[j - 2] + 1);
            }
            (previousPrevious, previous, current) = (previous, current, previousPrevious);
        }
        return previous[^1];
    }
}

sealed class MappedText(string value, string original, List<(int Start, int End)>? positions)
{
    public string Value { get; } = value;
    private (int Start, int End) Span(int index) => positions is null ? (index, index + 1) : positions[index];
    public int Offset(int index) => Span(index).Start;
    public string Original(int index, int length) => original[Span(index).Start..Span(index + length - 1).End];

    public static MappedText Normalize(string original)
    {
        if (original.IsNormalized()) return new(original, original, null);
        var text = new StringBuilder();
        var offsets = new List<(int, int)>();
        var graphemes = StringInfo.GetTextElementEnumerator(original);
        while (graphemes.MoveNext())
        {
            string element = graphemes.GetTextElement();
            string normalized = element.Normalize();
            text.Append(normalized);
            for (int i = 0; i < normalized.Length; i++)
                offsets.Add((graphemes.ElementIndex, graphemes.ElementIndex + element.Length));
        }
        return new(text.ToString(), original, offsets);
    }

    public MappedText Replace(Regex pattern, string replacement)
    {
        Match match = pattern.Match(Value);
        if (!match.Success) return this;
        var text = new StringBuilder();
        var offsets = new List<(int, int)>();
        int position = 0;
        while (match.Success)
        {
            CopyUntil(match.Index);
            int start = Span(match.Index).Start;
            int end = Span(match.Index + match.Length - 1).End;
            text.Append(replacement);
            for (int i = 0; i < replacement.Length; i++) offsets.Add((start, end));
            position = match.Index + match.Length;
            match = match.NextMatch();
        }
        CopyUntil(Value.Length);
        return new(text.ToString(), original, offsets);

        void CopyUntil(int end)
        {
            text.Append(Value, position, end - position);
            for (; position < end; position++) offsets.Add(Span(position));
        }
    }
}
