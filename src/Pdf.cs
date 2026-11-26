using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;

static partial class Documents
{
    private static IEnumerable<Unit> Pdf(string path, Options options, Action<DocumentIssue> issue,
        Action excludePage, Action pageFinished, Action<int> pageStarted, Action<int> scheduleOcr, CancellationToken cancellation)
    {
        ProcessResult info;
        try { info = RunTool("pdfinfo", ["-enc", "UTF-8", path], 20, cancellation); }
        catch (Win32Exception)
        {
            issue(new("pdf_unavailable", "Die lokale PDF-Komponente pdfinfo fehlt."));
            yield break;
        }
        catch (TimeoutException)
        {
            issue(new("parse_failed", "Zeitlimit beim Lesen der PDF-Eigenschaften überschritten."));
            yield break;
        }
        string[] lines = info.Text.Split('\n');
        if (info.Code != 0 && info.Error.Contains("Incorrect password", StringComparison.OrdinalIgnoreCase))
        {
            issue(new("pdf_locked", "PDF verlangt zum Öffnen ein Passwort."));
            yield break;
        }
        string? count = Property("Pages");
        if (info.Code != 0 || !int.TryParse(count, NumberStyles.None, CultureInfo.InvariantCulture, out int pages))
        {
            issue(new("parse_failed", "PDF konnte nicht gelesen werden."));
            yield break;
        }
        string? Property(string name) => lines.FirstOrDefault(line => line.StartsWith(name + ":", StringComparison.Ordinal))?
            [(name.Length + 1)..].Trim();
        yield return new Unit(Path.GetFileName(path), "filename", "metadata");
        foreach (var (name, field) in new[] { ("Title", "title"), ("Author", "author") })
            if (Property(name) is { Length: > 0 } value) yield return new Unit(value, field, "metadata");
        if (options.Field is not ("all" or "content")) yield break;
        const int blockSize = 16;
        int lastPage = Math.Min(pages, 10000);
        for (int first = 1; first <= lastPage; first += blockSize)
        {
            int last = Math.Min(lastPage, first + blockSize - 1);
            string[]? batch = first == last ? null : TryTextBlock(path, first, last, cancellation);
            HashSet<int>? images = batch is not null && batch.Any(text => !HasEnoughText(text))
                ? TryImageBlock(path, first, last, cancellation)
                : null;

            for (int page = first; page <= last; page++)
            {
                cancellation.ThrowIfCancellationRequested();
                pageStarted(page);
                string? extracted = batch is null ? ReadTextPage(path, page, issue, cancellation) : batch[page - first];
                if (extracted is not null)
                {
                    if (extracted.Length > 0) yield return new Unit(extracted, Page: page);
                    if (!HasEnoughText(extracted))
                    {
                        bool? substantialImage = images is null
                            ? ReadImagePage(path, page, issue, cancellation)
                            : images.Contains(page);
                        if (substantialImage == false && extracted.Length == 0)
                            yield return new Unit("", Page: page);
                        if (substantialImage == true)
                        {
                            if (!options.NoOcr) scheduleOcr(page);
                            else if (extracted.Length == 0) excludePage();
                        }
                    }
                }
                pageFinished();
            }
        }
        if (pages > lastPage)
            issue(new("parse_failed", "PDF überschreitet das Limit von 10’000 verarbeiteten Seiten."));
    }

    private static string[] Range(int first, int last) =>
        ["-f", first.ToString(CultureInfo.InvariantCulture), "-l", last.ToString(CultureInfo.InvariantCulture)];

    private static bool HasEnoughText(string text) =>
        text.Count(char.IsLetterOrDigit) >= 80 &&
        text.Split('\n').Count(line => !string.IsNullOrWhiteSpace(line)) >= 3;

    private static string[]? TryTextBlock(string path, int first, int last, CancellationToken cancellation)
    {
        try
        {
            var result = RunTool("pdftotext", [.. Range(first, last), "-enc", "UTF-8", "-layout", path, "-"], 20, cancellation);
            string[] pages = result.Text.Split('\f');
            int count = last - first + 1;
            if (result.Code != 0 || pages.Length != count + 1 || !string.IsNullOrWhiteSpace(pages[^1]))
                return null;
            return pages.Take(count).Select(page => page.Trim()).ToArray();
        }
        catch (Exception error) when (error is Win32Exception or TimeoutException or IOException) { return null; }
    }

    private static string? ReadTextPage(string path, int page, Action<DocumentIssue> issue, CancellationToken cancellation)
    {
        try
        {
            var result = RunTool("pdftotext", [.. Range(page, page), "-enc", "UTF-8", "-layout", "-nopgbrk", path, "-"], 20, cancellation);
            if (result.Code != 0) throw new IOException("PDF-Textebene konnte nicht gelesen werden.");
            return result.Text.Trim();
        }
        catch (Exception error) when (error is Win32Exception or TimeoutException or IOException)
        {
            issue(new(error is Win32Exception ? "pdf_unavailable" : "parse_failed",
                error is Win32Exception ? "Die lokale PDF-Komponente pdftotext fehlt." : error.Message, page));
            return null;
        }
    }

    private static HashSet<int>? TryImageBlock(string path, int first, int last, CancellationToken cancellation)
    {
        try { return ReadImages(path, first, last, cancellation); }
        catch (Exception error) when (error is Win32Exception or TimeoutException or IOException) { return null; }
    }

    private static bool? ReadImagePage(string path, int page, Action<DocumentIssue> issue, CancellationToken cancellation)
    {
        try { return ReadImages(path, page, page, cancellation).Contains(page); }
        catch (Exception error) when (error is Win32Exception or TimeoutException or IOException)
        {
            issue(new(error is Win32Exception ? "pdf_unavailable" : "parse_failed",
                error is Win32Exception ? "Die lokale PDF-Komponente pdfimages fehlt." : error.Message, page));
            return null;
        }
    }

    private static HashSet<int> ReadImages(string path, int first, int last, CancellationToken cancellation)
    {
        var result = RunTool("pdfimages", [.. Range(first, last), "-list", path], 20, cancellation);
        if (result.Code != 0) throw new IOException("PDF-Bildinformationen konnten nicht gelesen werden.");
        var pages = new HashSet<int>();
        foreach (string line in result.Text.Split('\n'))
        {
            string[] columns = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (columns.Length >= 5 && columns[2] == "image" &&
                int.TryParse(columns[0], out int page) &&
                int.TryParse(columns[3], out int width) && int.TryParse(columns[4], out int height) &&
                width >= 240 && height >= 240 && (long)width * height >= 150000)
                pages.Add(page);
        }
        return pages;
    }

    public static Unit? RecognizePdfPage(string path, int page, Action<DocumentIssue> issue,
        OcrCache? cache, Lazy<string?>? pdfFingerprint, Action<bool> countOcr, Action countRender,
        CancellationToken cancellation)
    {
        string? pageKey = cache?.PageKey(pdfFingerprint, page);
        string? recognized = cache?.ReadPage(pageKey);
        if (recognized is not null)
        {
            countOcr(true);
            return new Unit(recognized, Source: "ocr", Page: page);
        }

        string[] range = Range(page, page);
        string? directory = null;
        try
        {
            directory = Directory.CreateTempSubdirectory("wrg-").FullName;
            string prefix = Path.Combine(directory, "page");
            countRender();
            var render = RunTool("pdftoppm", [.. range, "-scale-to", "2400", "-gray", "-png", "-singlefile", path, prefix], 30, cancellation);
            if (render.Code != 0) throw new IOException("PDF-Seite konnte nicht gerendert werden.");
            string tessdata = Path.Combine(AppContext.BaseDirectory, "tools", "tesseract", "tessdata");
            string[] languageData = Directory.Exists(tessdata) ? ["--tessdata-dir", tessdata] : [];
            string image = prefix + ".png";
            string? key = cache?.Key(image);
            recognized = cache?.Read(key);
            countOcr(recognized is not null);
            if (recognized is null)
            {
                var ocr = RunTool("tesseract", [image, "stdout", "-l", "deu+eng", .. languageData], 45, cancellation);
                if (ocr.Code != 0)
                    issue(new("ocr_unavailable", "Tesseract konnte die Seite mit den Sprachdaten deu+eng nicht erkennen.", page));
                else
                {
                    recognized = ocr.Text.Trim();
                    cache?.Write(key, recognized);
                }
            }
            if (recognized is not null) cache?.RememberPage(pageKey, key);
        }
        catch (TimeoutException error) { issue(new("ocr_timeout", error.Message, page)); }
        catch (Win32Exception) { issue(new("ocr_unavailable", "Die lokale OCR- oder Render-Komponente fehlt.", page)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { issue(new("parse_failed", error.Message, page)); }
        finally
        {
            if (directory is not null)
            {
                try { Directory.Delete(directory, recursive: true); }
                catch (IOException) { issue(new("cleanup_failed", "Temporäre OCR-Dateien konnten nicht entfernt werden.", page)); }
                catch (UnauthorizedAccessException) { issue(new("cleanup_failed", "Temporäre OCR-Dateien konnten nicht entfernt werden.", page)); }
            }
        }
        return recognized is null ? null : new Unit(recognized, Source: "ocr", Page: page);
    }

    private sealed record ProcessResult(int Code, string Text, string Error);

    private static ProcessResult RunTool(string tool, string[] arguments, int seconds, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        string toolDirectory = tool == "tesseract" ? "tesseract" : Path.Combine("poppler", "Library", "bin");
        string bundled = Path.Combine(AppContext.BaseDirectory, "tools", toolDirectory,
            tool + (OperatingSystem.IsWindows() ? ".exe" : ""));
        if (OperatingSystem.IsWindows() && !File.Exists(bundled))
            throw new Win32Exception($"Die gebündelte Komponente {tool} fehlt.");
        var start = new ProcessStartInfo(File.Exists(bundled) ? bundled : tool)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8, CreateNoWindow = true
        };
        start.Environment["LC_ALL"] = "C";
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using Process process = Process.Start(start) ?? throw new Win32Exception();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(seconds));
        Task<string> output = ReadBounded(process.StandardOutput, timeout.Token);
        Task<string> error = ReadBounded(process.StandardError, timeout.Token);
        try
        {
            process.WaitForExitAsync(timeout.Token).GetAwaiter().GetResult();
            return new(process.ExitCode, output.GetAwaiter().GetResult(), error.GetAwaiter().GetResult());
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            throw new TimeoutException($"Zeitlimit für {tool} überschritten ({seconds} Sekunden).");
        }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); process.WaitForExit(2000); }
                catch (InvalidOperationException) { }
                catch (Win32Exception) { }
            }
        }
    }

    private static async Task<string> ReadBounded(StreamReader reader, CancellationToken cancellation)
    {
        var text = new StringBuilder();
        var buffer = new char[8192];
        bool tooLong = false;
        while (await reader.ReadAsync(buffer.AsMemory(), cancellation) is int count && count > 0)
        {
            if (text.Length + count > 4 * 1024 * 1024) tooLong = true;
            if (!tooLong) text.Append(buffer, 0, count);
        }
        if (tooLong) throw new IOException("Ausgabe der PDF- oder OCR-Komponente überschreitet 4 MiB.");
        return text.ToString();
    }
}
