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
    private const string WordCharacters = @"\p{L}\p{Nd}\p{M}";
    private static readonly Regex Words = new($"[{WordCharacters}]+");
    private static readonly Regex Space = new(@"[^\S ]\s*| {2,}\s*| (?=[^\S ])[^\S ]\s*");
    private static readonly Regex Hyphen = new(@"(?<=\p{L})-\r?\n[ \t]*(?=\p{L})");
    private static readonly Regex Spelling = new("ä|ö|ü|ß|ae|oe|ue|ss", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private readonly string[] terms = options.Query.Trim().Normalize().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
    private readonly string phrase = Space.Replace(options.Query.Trim().Normalize(), " ");

    public static bool IsWord(string value) => Words.Match(value) is { Success: true } match && match.Length == value.Length;

    private static Match Literal(string text, string term)
    {
        string pattern = Regex.Escape(term);
        if (Words.IsMatch(term[..1])) pattern = $"(?<![{WordCharacters}])" + pattern;
        if (Words.IsMatch(term[^1..])) pattern += $"(?![{WordCharacters}])";
        return Regex.Match(text, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    public Hit? Find(Unit unit, CancellationToken cancellation)
    {
        MappedText dehyphenated = MappedText.Normalize(unit.Text);
        if (unit.Page is not null) dehyphenated = dehyphenated.Replace(Hyphen, "");
        MappedText normalized = dehyphenated.Replace(Space, " ");
        string text = normalized.Value;
        if (options.Excludes.Any(word => Literal(text, word.Normalize()).Success)) return null;
        if (options.Phrase)
        {
            Match match = Literal(text, phrase);
            return match.Success ? new Hit(0, 0, [normalized.Original(match)], normalized.Offset(match.Index)) : null;
        }

        var selected = new List<(int Rank, int Distance, int At, string Word)>();
        foreach (string term in terms)
        {
            cancellation.ThrowIfCancellationRequested();
            Match exact = Literal(text, term);
            if (exact.Success)
            {
                selected.Add((0, 0, normalized.Offset(exact.Index), normalized.Original(exact)));
                continue;
            }
            Rune[] runes = term.EnumerateRunes().ToArray();
            if (options.Exact || !IsWord(term) || runes.Any(Rune.IsDigit) ||
                (runes.Length is >= 2 and <= 5 && runes.All(Rune.IsUpper)))
                return null;

            string folded = Fold(term);
            Match? variant = Words.Matches(text).FirstOrDefault(word => Fold(word.Value) == folded);
            if (variant is not null)
            {
                selected.Add((1, 0, normalized.Offset(variant.Index), normalized.Original(variant)));
                continue;
            }
            if (unit.Source == "ocr")
            {
                string[] characters = runes.Select(rune => Regex.Escape(rune.ToString())).ToArray();
                string pattern = $"(?<![{WordCharacters}])" +
                    string.Join(@"[\t ]*", characters) + $"(?![{WordCharacters}])";
                Match ocr = Regex.Match(dehyphenated.Value, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                if (ocr.Success)
                {
                    selected.Add((2, 0, dehyphenated.Offset(ocr.Index), dehyphenated.Original(ocr)));
                    continue;
                }
            }

            int limit = runes.Length < 4 ? 0 : runes.Length < 8 ? 1 : 2;
            if (limit == 0) return null;
            int best = limit + 1;
            Match? fuzzy = null;
            Rune[] query = term.ToUpperInvariant().EnumerateRunes().ToArray();
            foreach (Match word in Words.Matches(text))
            {
                cancellation.ThrowIfCancellationRequested();
                if (Math.Abs(word.Length - term.Length) > limit * 2) continue;
                Rune[] candidate = word.Value.ToUpperInvariant().EnumerateRunes().ToArray();
                if (Math.Abs(candidate.Length - query.Length) > limit) continue;
                int distance = Distance(query, candidate);
                if (distance >= best) continue;
                best = distance;
                fuzzy = word;
                if (best == 1) break;
            }
            if (fuzzy is null) return null;
            selected.Add((3, best, normalized.Offset(fuzzy.Index), normalized.Original(fuzzy)));
        }
        var ordered = selected.OrderBy(item => item.At).ToArray();
        return new Hit(ordered.Max(item => item.Rank), ordered.Sum(item => item.Distance),
            ordered.Select(item => item.Word).ToArray(), ordered[0].At);
    }

    private static string Fold(string word) => Spelling.Replace(word.ToLowerInvariant(), match => match.Value switch
    {
        "ä" or "ae" => "ä", "ö" or "oe" => "ö", "ü" or "ue" => "ü", _ => "ß"
    });

    private static int Distance(Rune[] left, Rune[] right)
    {
        int[] previousPrevious = new int[right.Length + 1];
        int[] previous = Enumerable.Range(0, right.Length + 1).ToArray();
        int[] current = new int[right.Length + 1];
        for (int i = 1; i <= left.Length; i++)
        {
            current[0] = i;
            for (int j = 1; j <= right.Length; j++)
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
    public string Original(Match match) => original[Span(match.Index).Start..Span(match.Index + match.Length - 1).End];

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
