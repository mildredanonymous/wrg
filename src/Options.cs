using System.Globalization;

sealed class Options
{
    public string Query { get; private set; } = "";
    public bool Exact, Phrase, NoRecursion, NoOcr, Interactive, Verbose, Help, Version, Stream, OcrCache;
    public string Field = "all", Format = "text";
    public DateTime? Since;
    public List<string> Paths { get; } = [];
    public HashSet<string> Types { get; } = [];
    public List<string> Excludes { get; } = [];
    public string Mode => Phrase ? "phrase" : Exact ? "exact" : "default";

    public static Options Parse(string[] args)
    {
        var result = new Options();
        var positionals = new List<string>();
        var values = new Dictionary<string, string>();
        bool parseOptions = true;
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (parseOptions && arg == "--")
            {
                parseOptions = false;
                continue;
            }
            if (!parseOptions || !arg.StartsWith('-'))
            {
                positionals.Add(arg);
                continue;
            }
            string Value(params string[] allowed)
            {
                if (++i >= args.Length || args[i] == "--" || args[i].StartsWith("--"))
                    throw new ArgumentException($"Für {arg} fehlt ein Wert.");
                string value = args[i];
                if (allowed.Length != 0 && !allowed.Contains(value))
                    throw new ArgumentException($"Ungültiger Wert für {arg}: {value}.");
                if (arg is not ("--type" or "--exclude-word") &&
                    values.TryGetValue(arg, out string? first) && first != value)
                    throw new ArgumentException($"Widersprüchliche Werte für {arg}.");
                values[arg] = value;
                return value;
            }
            switch (arg)
            {
                case "--help": result.Help = true; break;
                case "--version": result.Version = true; break;
                case "--exact": result.Exact = true; break;
                case "--phrase": result.Phrase = result.Exact = true; break;
                case "--field": result.Field = Value("all", "content", "filename", "title", "author"); break;
                case "--type": result.Types.Add(Value("pdf", "docx", "md", "txt")); break;
                case "--modified-since":
                    if (!DateTime.TryParseExact(Value(), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeLocal, out DateTime since))
                        throw new ArgumentException("--modified-since erwartet ein Datum im Format YYYY-MM-DD.");
                    result.Since = since.ToUniversalTime();
                    break;
                case "--exclude-word":
                    string excluded = Value();
                    if (!Search.IsWord(excluded))
                        throw new ArgumentException("--exclude-word erwartet ein ganzes Wort.");
                    result.Excludes.Add(excluded);
                    break;
                case "--no-recursion": result.NoRecursion = true; break;
                case "--no-ocr": result.NoOcr = true; break;
                case "--ocr-cache": result.OcrCache = true; break;
                case "--stream": result.Stream = true; break;
                case "--interactive": result.Interactive = true; break;
                case "--verbose": result.Verbose = true; break;
                case "--format": result.Format = Value("text", "jsonl"); break;
                default: throw new ArgumentException($"Unbekannte Option: {arg}.");
            }
        }
        if (result.Interactive && result.Format == "jsonl")
            throw new ArgumentException("--interactive und --format jsonl sind nicht kombinierbar.");
        if (result.Interactive && result.Stream)
            throw new ArgumentException("--interactive und --stream sind nicht kombinierbar.");
        if (result.Help || result.Version) return result;
        if (positionals.Count == 0 || string.IsNullOrWhiteSpace(positionals[0]))
            throw new ArgumentException("Ein Suchtext ist erforderlich.");
        result.Query = positionals[0];
        foreach (string path in positionals.Skip(1).DefaultIfEmpty("."))
            result.Paths.Add(Path.GetFullPath(path));
        return result;
    }

    public static bool WantsJson(string[] args)
    {
        var formats = new HashSet<string>();
        for (int i = 0; i < args.Length && args[i] != "--"; i++)
        {
            if (args[i] != "--format") continue;
            if (i + 1 == args.Length || args[i + 1].StartsWith("--"))
                return false;
            formats.Add(args[++i]);
        }
        return formats.SetEquals(["jsonl"]);
    }
}
