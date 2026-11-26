using System.Diagnostics;
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
              --exclude-dir NAME                Unterordner nach Namen auslassen (wiederholbar)
              --no-recursion                    Nur direkt enthaltene Dateien prüfen
              --no-ocr                          Texterkennung abschalten
              --ocr-cache                       Erkannten Scan-Text lokal speichern
              --stream                          Texttreffer sofort ausgeben
              --files-only                      Je passender Datei einen absoluten Pfad ausgeben
              --timing                          Suchzeiten und OCR-Zähler auf stderr anzeigen
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
        output.BeginSearch();
        try { scanner.Scan(); }
        finally { output.EndSearch(); }
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
            for (int i = 0; i < hits.Length; i++)
            {
                if (i > 0) Console.WriteLine();
                Console.WriteLine(Output.Describe(hits[i], options.Verbose));
            }
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

    private bool stream, verbose, timing;
    private HashSet<string>? matchingPaths;
    private Stopwatch? elapsed;
    private TimeSpan? firstMatch;
    private int ocrAttempts, ocrCacheHits, pageRenders;

    public void Start(Options options)
    {
        stream = options.Stream;
        verbose = options.Verbose;
        timing = options.Timing;
        if (options.FilesOnly)
            matchingPaths = new HashSet<string>(OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        if (json) Write("start", new { query = options.Query, paths = options.Paths, mode = options.Mode });
    }

    public void BeginSearch()
    {
        if (timing) elapsed = Stopwatch.StartNew();
    }

    public void EndSearch() => elapsed?.Stop();

    public void CountOcr(bool fromCache)
    {
        if (!timing) return;
        if (fromCache) Interlocked.Increment(ref ocrCacheHits);
        else Interlocked.Increment(ref ocrAttempts);
    }

    public void CountRender()
    {
        if (timing) Interlocked.Increment(ref pageRenders);
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
        if (elapsed is not null) firstMatch ??= elapsed.Elapsed;
        if (matchingPaths is not null)
        {
            if (!matchingPaths.Add(result.Path)) return;
            Matches++;
            Console.WriteLine(result.Path);
            Console.Out.Flush();
            return;
        }

        Matches++;
        if (!json)
        {
            if (stream)
            {
                if (Matches > 1) Console.WriteLine();
                Console.WriteLine(Describe(result, verbose));
                Console.Out.Flush();
            }
            else results.Add(result);
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
        else
        {
            string position = page is int number ? $" · Seite {number}" : "";
            Console.Error.WriteLine(Clean(path is null ? message : $"Hinweis: {message}"));
            if (path is not null) Console.Error.WriteLine("  " + Clean(path + position));
        }
    }

    public Result[] Sorted() => results.OrderBy(r => r.Hit.Rank).ThenBy(r => r.Hit.Distance)
        .ThenBy(r => r.Path, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Unit.Page)
        .ThenBy(r => r.Unit.Line).ThenBy(r => r.Unit.Paragraph).ToArray();

    public int Complete(bool verbose)
    {
        if (!json)
        {
            string count = matchingPaths is null
                ? (Matches == 1 ? "1 Treffer" : $"{Matches} Treffer")
                : (Matches == 1 ? "1 passende Datei" : $"{Matches} passende Dateien");
            Console.Error.WriteLine($"{count} · {Searched} Dateien durchsucht" + (Gap ? " · Suche mit Lücken" : ""));
            if (verbose || Gap)
                Console.Error.WriteLine($"Dateien: {Skipped} übersprungen, {Excluded} ausgeschlossen. Seiten: {PagesSkipped} übersprungen, {PagesExcluded} ausgeschlossen.");
        }
        return Gap ? (CheckedInput || Searched > 0 ? Finish("partial", 3) : Finish("failed", 2))
            : Finish("complete", Matches == 0 ? 1 : 0);
    }

    public int Finish(string status, int exitCode)
    {
        if (elapsed is not null)
        {
            string first = firstMatch is TimeSpan found ? $"{found.TotalSeconds:0.000} s" : "kein Treffer";
            Console.Error.WriteLine($"Zeit · erster Treffer: {first} · Suche: {elapsed.Elapsed.TotalSeconds:0.000} s");
            Console.Error.WriteLine($"OCR · Tesseract-Aufrufe: {ocrAttempts} · Seiten aus dem Cache: {ocrCacheHits} · Render-Aufrufe: {pageRenders}");
        }
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
        string folder = Path.GetDirectoryName(result.Path) ?? result.Path;
        if (!absolute) folder = Path.GetRelativePath(Environment.CurrentDirectory, folder);
        string heading = Clean($"{Path.GetFileName(result.Path)} · {DescribeLocation(result)}");
        string preview = result.Unit.Field == "filename" ? "" : $"{Environment.NewLine}  {Clean(result.Snippet)}";
        return heading + preview +
            (folder == "." ? "" : $"{Environment.NewLine}  Ordner: {Clean(folder)}");
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
    private readonly Queue<Task<Action>> pendingOcr = new();
    private readonly OcrCache? ocrCache = options.OcrCache && !options.NoOcr ? new OcrCache() : null;
    private CancellationToken workerCancellation;

    public void Scan()
    {
        using var worker = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        workerCancellation = worker.Token;
        try
        {
            foreach (string path in options.Paths)
            {
                cancellation.ThrowIfCancellationRequested();
                Walk(path, true);
            }
            while (pendingOcr.Count > 0) CollectOcr(wait: true);
        }
        finally
        {
            worker.Cancel();
            while (pendingOcr.TryDequeue(out Task<Action>? job))
            {
                try { job.GetAwaiter().GetResult(); }
                catch (OperationCanceledException) { }
            }
        }
    }

    private void CollectOcr(bool wait = false)
    {
        while (pendingOcr.TryPeek(out Task<Action>? job) && (wait || job.IsCompleted))
        {
            pendingOcr.Dequeue();
            job.GetAwaiter().GetResult()();
            wait = false;
        }
    }

    private void QueueOcr(string path, int page, DateTime modified, long length,
        Lazy<string?>? pdfFingerprint, Action<Unit?, List<DocumentIssue>> complete)
    {
        const int maxPending = 64;
        CollectOcr(wait: pendingOcr.Count >= maxPending);
        Task<Action>? previous = pendingOcr.Count > 0 ? pendingOcr.Last() : null;
        pendingOcr.Enqueue(Task.Run(async () =>
        {
            if (previous is not null)
                await ((Task)previous).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            workerCancellation.ThrowIfCancellationRequested();
            var issues = new List<DocumentIssue>();
            Unit? unit = null;
            bool unchanged = Unchanged();
            if (unchanged)
                unit = Documents.RecognizePdfPage(path, page, issues.Add, ocrCache, pdfFingerprint,
                    output.CountOcr, output.CountRender, workerCancellation);
            if (!unchanged || !Unchanged())
            {
                unit = null;
                issues.Add(new("parse_failed", "PDF wurde während der Suche geändert oder entfernt.", page));
            }
            return (Action)(() => complete(unit, issues));

            bool Unchanged()
            {
                var current = new FileInfo(path);
                try { return current.Exists && current.LastWriteTimeUtc == modified && current.Length == length; }
                catch (IOException) { return false; }
                catch (UnauthorizedAccessException) { return false; }
            }
        }));
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
            if (directory && !explicitPath && (options.NoRecursion ||
                options.ExcludedDirectories.Contains(Path.GetFileName(path)))) continue;
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
                CollectOcr();
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
        if ((type == "docx" && Path.GetFileName(path).StartsWith("~$", StringComparison.Ordinal)) ||
            ((options.Field is "author" or "title") && type is not ("docx" or "pdf")))
        {
            output.Excluded++;
            output.CheckedInput |= explicitPath;
            return;
        }
        bool checkedUnit = false, gap = false;
        bool excluded = false, scanFinished = false, excludedPage = false;
        int pending = 0;
        void FinishFile()
        {
            if (!scanFinished || pending != 0) return;
            if (checkedUnit || (!gap && !excluded && !excludedPage &&
                options.Field is "all" or "content" && !cancellation.IsCancellationRequested))
                output.Searched++;
            else if (gap) output.Skipped++;
            else if (!excluded && !cancellation.IsCancellationRequested) output.CheckedInput = true;
        }

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
            if (options.Interactive || options.Verbose) output.Progress(path);
            Result? pendingPage = null;
            void FlushPage()
            {
                if (pendingPage is not null) output.Match(pendingPage);
                pendingPage = null;
            }
            Result? Find(Unit unit)
            {
                cancellation.ThrowIfCancellationRequested();
                if (options.Field != "all" && options.Field != unit.Field) return null;
                checkedUnit = true;
                Hit? hit = search.Find(unit, cancellation);
                if (hit is null) return null;
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
                return new Result(path, type, modified, unit with { Text = "", Line = line }, hit, snippet);
            }
            void Check(Unit unit)
            {
                Result? result = Find(unit);
                if (result is null) return;
                if (unit.Page is null) output.Match(result);
                else pendingPage = Better(pendingPage, result);
            }
            void OnIssue(DocumentIssue issue)
            {
                gap = true;
                Report(issue.Code, issue.Message, path, issue.Page);
            }
            Lazy<string?>? pdfFingerprint = null;
            void ScheduleOcr(int page)
            {
                if (ocrCache is not null)
                    pdfFingerprint ??= new Lazy<string?>(() => OcrCache.PdfFingerprint(path, workerCancellation));
                Result? textHit = pendingPage;
                pendingPage = null;
                QueueOcr(path, page, modified, info.Length, pdfFingerprint, (unit, issues) =>
                {
                    foreach (DocumentIssue issue in issues) OnIssue(issue);
                    Result? hit = Better(textHit, unit is null ? null : Find(unit));
                    if (hit is not null) output.Match(hit);
                    pending--;
                    FinishFile();
                });
                pending++;
            }
            if (options.Field == "filename" || (options.Field == "all" && type != "pdf"))
                Check(new Unit(info.Name, "filename", "metadata"));
            if (options.Field == "filename") return;
            if (info.Length > Documents.FileLimit) throw new InvalidDataException("Datei überschreitet 256 MiB.");
            foreach (Unit unit in Documents.Read(path, type, options, OnIssue,
                () => { output.PagesExcluded++; excludedPage = true; }, FlushPage,
                page =>
                {
                    CollectOcr();
                    if (options.Interactive || options.Verbose) output.Progress(path, page);
                }, ScheduleOcr, cancellation))
                Check(unit);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or
            SecurityException or InvalidDataException or DecoderFallbackException or XmlException)
        {
            gap = true;
            Report(IssueCode(error), error.Message, path);
        }
        finally
        {
            scanFinished = true;
            FinishFile();
        }
    }

    private static Result? Better(Result? first, Result? second)
    {
        if (first is null) return second;
        if (second is null) return first;
        return (first.Hit.Rank, first.Hit.Distance).CompareTo((second.Hit.Rank, second.Hit.Distance)) <= 0
            ? first : second;
    }
}
