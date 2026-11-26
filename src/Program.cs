using System.Security;
using System.Text;
using System.Text.Json;
using System.Xml;

Console.OutputEncoding = new UTF8Encoding(false);
return Run(args);

static int Run(string[] args)
{
    var output = new Output(Options.WantsJson(args));
    Options options;
    try { options = Options.Parse(args); }
    catch (Exception error) when (error is ArgumentException or NotSupportedException or IOException)
    {
        output.Start(new Options());
        output.Issue("invalid_arguments", error.Message);
        return output.Finish("failed", 2);
    }
    if (options.Help)
    {
        Console.WriteLine("""
            wrg [OPTIONEN] [--] SUCHTEXT [PFAD ...]
            Durchsucht lokale Dokumente. Ohne Pfad wird der aktuelle Ordner durchsucht.
            Mehrere Suchwörter als ein Argument in Anführungszeichen übergeben.

              --help                            Diese Hilfe anzeigen
              --version                         Programmversion anzeigen
              --exact | --phrase                Wörtliche Suche oder Wortfolge
              --field all|content|filename|title|author
              --type pdf|docx|md|txt             Dateityp (wiederholbar)
              --modified-since YYYY-MM-DD       Ab lokaler Mitternacht
              --exclude-word WORT               Einheiten mit diesem Wort ausschliessen
              --no-recursion                    Nur direkt enthaltene Dateien prüfen
              --no-ocr                          Texterkennung abschalten
              --interactive                     Treffer wählen und Original öffnen
              --format text|jsonl               Ausgabeformat
              --verbose                         Absolute Pfade und Abdeckung anzeigen
            """);
        return 0;
    }
    if (options.Version)
    {
        Console.WriteLine("wrg 0.1.0");
        return 0;
    }
    output.Start(options);
    using var cancellation = new CancellationTokenSource();
    ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
    Console.CancelKeyPress += cancel;
    try
    {
        if (options.Interactive && (Console.IsInputRedirected || Console.IsOutputRedirected))
        {
            output.Issue("invalid_arguments", "--interactive erfordert ein interaktives Terminal.");
            return output.Finish("failed", 2);
        }
        var scanner = new Scanner(options, output, cancellation.Token);
        scanner.Scan();
        cancellation.Token.ThrowIfCancellationRequested();
        var hits = output.Sorted();
        if (options.Interactive && hits.Length > 0)
        {
            if (!Picker.Select(hits, cancellation.Token))
            {
                output.Issue("open_failed", "Mindestens ein Öffnungsversuch ist fehlgeschlagen.");
                return output.Finish("failed", 2);
            }
        }
        else
            foreach (Result hit in hits) Console.WriteLine(Output.Describe(hit, options.Verbose));
        return output.Complete(options.Verbose);
    }
    catch (OperationCanceledException)
    {
        output.Issue("cancelled", "Suche abgebrochen.", severity: "info");
        return output.Finish("cancelled", 130);
    }
    catch (IOException) { return 2; }
    finally { Console.CancelKeyPress -= cancel; }
}

record Result(string Path, string Type, DateTime Modified, Unit Unit, Hit Hit, string Snippet);

sealed class Output(bool json)
{
    private readonly List<Result> results = [];
    public int Matches, Searched, Skipped, Excluded, PagesSkipped, PagesExcluded;
    public bool Gap, CheckedInput;

    private void Write(string type, object fields)
    {
        var record = new Dictionary<string, object?> { ["version"] = 1, ["type"] = type };
        foreach (JsonProperty property in JsonSerializer.SerializeToElement(fields).EnumerateObject())
            record.Add(property.Name, property.Value);
        Console.WriteLine(JsonSerializer.Serialize(record));
    }

    public void Start(Options options)
    {
        if (json) Write("start", new { query = options.Query, paths = options.Paths, mode = options.Mode });
    }

    private long? lastProgress;

    public void Progress(string path, int? page = null)
    {
        if (json || Console.IsErrorRedirected) return;
        long now = Environment.TickCount64;
        if (lastProgress is long last && now - last < 3000) return;
        lastProgress = now;
        string position = page is int number ? $" · Seite {number}" : "";
        Console.Error.WriteLine(Clean($"Suche läuft · {Searched} Dateien geprüft · {Path.GetFileName(path)}{position}"));
    }

    public void Match(Result result)
    {
        Matches++;
        if (!json)
        {
            results.Add(result);
            return;
        }
        Unit unit = result.Unit;
        Write("match", new
        {
            path = result.Path, filename = Path.GetFileName(result.Path), file_type = result.Type,
            modified_utc = result.Modified, field = unit.Field, source = unit.Source,
            page = unit.Page, line = unit.Line, paragraph = unit.Paragraph, section = unit.Section,
            match_kind = result.Hit.Kind, edit_distance = result.Hit.Distance,
            matched_text = result.Hit.Words, snippet = result.Snippet
        });
    }

    public void Issue(string code, string message, string? path = null, int? page = null, string severity = "error")
    {
        if (json) Write("issue", new { code, message, path, page, severity });
        else Console.Error.WriteLine(Clean($"{path}{(page is null ? "" : $" · Seite {page}")}{(path is null ? "" : " · ")}{message}"));
    }

    public Result[] Sorted() => results.OrderBy(r => r.Hit.Rank).ThenBy(r => r.Hit.Distance)
        .ThenBy(r => r.Path, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Unit.Page)
        .ThenBy(r => r.Unit.Line).ThenBy(r => r.Unit.Paragraph).ToArray();

    public int Complete(bool verbose)
    {
        if (!json && (verbose || Gap || Excluded > 0 || PagesExcluded > 0))
            Console.Error.WriteLine($"Dateien: {Searched} geprüft, {Skipped} übersprungen, {Excluded} ausgeschlossen. Seiten: {PagesSkipped} übersprungen, {PagesExcluded} ausgeschlossen.");
        return Gap ? (CheckedInput || Searched > 0 ? Finish("partial", 3) : Finish("failed", 2))
            : Finish("complete", Matches == 0 ? 1 : 0);
    }

    public int Finish(string status, int exitCode)
    {
        if (json) Write("summary", new
        {
            status, exit_code = exitCode, matches = Matches, files_searched = Searched,
            files_skipped = Skipped, files_excluded = Excluded, pages_skipped = PagesSkipped,
            pages_excluded = PagesExcluded
        });
        return exitCode;
    }

    public static string Clean(string text) => string.Concat(text.EnumerateRunes().Select(rune =>
        Rune.GetUnicodeCategory(rune) is System.Globalization.UnicodeCategory.Control or
            System.Globalization.UnicodeCategory.Format ? " " : rune.ToString()));

    public static string Describe(Result result, bool absolute)
    {
        string path = absolute ? result.Path : Path.GetRelativePath(Environment.CurrentDirectory, result.Path);
        return Clean($"{path} · {DescribeLocation(result)} · {result.Snippet}");
    }

    public static string DescribeLocation(Result result, bool details = true)
    {
        Unit unit = result.Unit;
        string position = unit.Field switch
        {
            "filename" => "Dateiname", "title" => "Titel", "author" => "Autor",
            _ when unit.Page is int page => $"Seite {page}",
            _ when unit.Line is int line => $"Zeile {line}",
            _ when unit.Paragraph is int paragraph => $"Absatz {paragraph}",
            _ => "Inhalt"
        };
        string section = !details || unit.Section is null ? "" : $" · {unit.Section}";
        string scan = unit.Source == "ocr" ? " · Scan" : "";
        string kind = result.Hit.Rank switch
        {
            1 => " · Schreibvariante", 2 => " · OCR-Variante", 3 => " · Ähnlich", _ => ""
        };
        string words = details && kind.Length > 0 ? $" ({string.Join(", ", result.Hit.Words)})" : "";
        return $"{position}{section}{scan}{kind}{words}";
    }
}

sealed class Scanner(Options options, Output output, CancellationToken cancellation)
{
    private static readonly HashSet<string> Supported = ["txt", "md", "docx", "pdf"];
    private readonly HashSet<string> visited = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> directories = new(StringComparer.OrdinalIgnoreCase);
    private readonly Search search = new(options);

    public void Scan()
    {
        foreach (string path in options.Paths)
        {
            cancellation.ThrowIfCancellationRequested();
            Walk(path, true);
        }
    }

    private void Walk(string path, bool explicitPath)
    {
        var pending = new Stack<(string Path, bool Explicit)>();
        pending.Push((path, explicitPath));
        while (pending.TryPop(out var input))
        {
            cancellation.ThrowIfCancellationRequested();
            path = input.Path;
            explicitPath = input.Explicit;
            if (OperatingSystem.IsWindows() && (path.StartsWith(@"\\") || IsNetworkDrive(path)))
            {
                if (visited.Add(path))
                {
                    output.Skipped++;
                    Report("unsupported_location", "Netzwerk- und Gerätepfade werden nicht durchsucht.", path);
                }
                continue;
            }
            FileAttributes attributes;
            try
            {
                if (explicitPath && HasBlockedAncestor(path))
                {
                    if (visited.Add(path))
                    {
                        if (Supported.Contains(TypeOf(path))) output.Skipped++;
                        Report("unsupported_location", "Der Pfad liegt unter einer Verzeichnisverknüpfung oder einem Onlineplatzhalter.", path);
                    }
                    continue;
                }
                attributes = File.GetAttributes(path);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException)
            {
                if (visited.Add(path))
                {
                    if (explicitPath && Supported.Contains(TypeOf(path))) output.Skipped++;
                    Report(IssueCode(error), error.Message, path);
                }
                continue;
            }
            bool directory = attributes.HasFlag(FileAttributes.Directory);
            if (directory && !explicitPath && options.NoRecursion) continue;
            if (Blocked(attributes))
            {
                if (!visited.Add(path)) continue;
                if (!directory)
                {
                    if (explicitPath) output.Skipped++;
                    else output.Excluded++;
                }
                if (explicitPath) Report("unsupported_location", "Verknüpfung oder nur online verfügbarer Pfad.", path);
                continue;
            }
            if (!directory)
            {
                ScanFile(path, explicitPath);
                continue;
            }
            if (!directories.Add(Path.TrimEndingDirectorySeparator(path))) continue;
            try
            {
                string[] children = Directory.GetFileSystemEntries(path);
                output.CheckedInput = true;
                foreach (string child in children.Reverse()) pending.Push((child, false));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException)
            {
                Report(IssueCode(error), error.Message, path);
            }
        }
    }

    private static bool IsNetworkDrive(string path)
    {
        try { return new DriveInfo(Path.GetPathRoot(path)!).DriveType == DriveType.Network; }
        catch (IOException) { return false; }
    }

    private static bool Blocked(FileAttributes attributes) =>
        (attributes & (FileAttributes.ReparsePoint | FileAttributes.Offline |
            (FileAttributes)0x40000 | (FileAttributes)0x400000)) != 0;

    private static bool HasBlockedAncestor(string path)
    {
        var ancestors = new Stack<string>();
        for (DirectoryInfo? parent = Directory.GetParent(path); parent is not null; parent = parent.Parent)
            ancestors.Push(parent.FullName);
        while (ancestors.TryPop(out string? parent))
            if (Blocked(System.IO.File.GetAttributes(parent))) return true;
        return false;
    }

    private static string TypeOf(string path) => Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
    private static string IssueCode(Exception error) => error switch
    {
        UnauthorizedAccessException or SecurityException => "access_denied",
        FileNotFoundException or DirectoryNotFoundException => "path_not_found",
        DecoderFallbackException => "decode_failed",
        _ => "parse_failed"
    };

    private void Report(string code, string message, string path, int? page = null)
    {
        output.Gap = true;
        if (page is not null) output.PagesSkipped++;
        output.Issue(code, message, path, page, "warning");
    }

    private void ScanFile(string path, bool explicitPath)
    {
        string type = TypeOf(path);
        if (!Supported.Contains(type))
        {
            if (explicitPath)
            {
                output.Skipped++;
                Report("unsupported_type", "Dieser Dateityp wird nicht unterstützt.", path);
            }
            else output.Excluded++;
            return;
        }
        if (!visited.Add(path)) return;
        bool checkedUnit = false, gap = false;
        bool excluded = false;
        try
        {
            var info = new FileInfo(path);
            DateTime modified = info.LastWriteTimeUtc;
            if (options.Types.Count > 0 && !options.Types.Contains(type) || modified < options.Since)
            {
                output.Excluded++;
                excluded = true;
                if (explicitPath) output.CheckedInput = true;
                return;
            }
            output.Progress(path);
            Result? pendingPage = null;
            void FlushPage()
            {
                if (pendingPage is not null) output.Match(pendingPage);
                pendingPage = null;
            }
            void Check(Unit unit)
            {
                cancellation.ThrowIfCancellationRequested();
                if (options.Field != "all" && options.Field != unit.Field) return;
                checkedUnit = true;
                Hit? hit = search.Find(unit, cancellation);
                if (hit is null) return;
                string text = unit.Text;
                int start = Math.Max(hit.Offset - 65, 0);
                int end = Math.Min(text.Length, start + 180);
                if (start > 0 && char.IsLowSurrogate(text[start])) start--;
                if (end < text.Length && char.IsLowSurrogate(text[end])) end++;
                string snippet = (start > 0 ? "…" : "") +
                    System.Text.RegularExpressions.Regex.Replace(text[start..end], @"\s+", " ") +
                    (end < text.Length ? "…" : "");
                int? line = unit.Line;
                if (type == "md" && line is not null)
                    line += text.AsSpan(0, hit.Offset).Count('\n');
                var result = new Result(path, type, modified, unit with { Text = "", Line = line }, hit, snippet);
                if (unit.Page is null) output.Match(result);
                else if (pendingPage is null ||
                    (hit.Rank, hit.Distance).CompareTo((pendingPage.Hit.Rank, pendingPage.Hit.Distance)) < 0)
                    pendingPage = result;
            }
            if (options.Field is "all" or "filename" && type != "pdf")
                Check(new Unit(info.Name, "filename", "metadata"));
            if (options.Field == "filename" && type != "pdf") return;
            if (info.Length > Documents.FileLimit) throw new InvalidDataException("Datei überschreitet 256 MiB.");
            bool excludedPage = false;
            foreach (Unit unit in Documents.Read(path, type, options, issue =>
            {
                gap = true;
                Report(issue.Code, issue.Message, path, issue.Page);
            }, () => { output.PagesExcluded++; excludedPage = true; }, FlushPage,
                page => output.Progress(path, page), cancellation))
                Check(unit);
            if (!gap && !excludedPage && options.Field is "all" or "content") checkedUnit = true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or
            SecurityException or InvalidDataException or DecoderFallbackException or XmlException)
        {
            gap = true;
            Report(IssueCode(error), error.Message, path);
        }
        finally
        {
            if (checkedUnit) output.Searched++;
            else if (gap) output.Skipped++;
            else if (!excluded && !cancellation.IsCancellationRequested) output.CheckedInput = true;
        }
    }
}
