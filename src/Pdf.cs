using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

static partial class Documents
{
    private static IEnumerable<Unit> Pdf(string path, Options options, Action<DocumentIssue> issue,
        Action excludePage, Action pageFinished, Action<int> pageStarted, CancellationToken cancellation)
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
        for (int page = 1; page <= pages; page++)
        {
            cancellation.ThrowIfCancellationRequested();
            if (page > 10000)
            {
                issue(new("parse_failed", "PDF überschreitet das Limit von 10’000 verarbeiteten Seiten."));
                yield break;
            }
            pageStarted(page);
            foreach (Unit unit in PdfPage(path, page, options, issue, excludePage, cancellation))
                yield return unit;
            pageFinished();
        }
    }

    private static IEnumerable<Unit> PdfPage(string path, int page, Options options,
        Action<DocumentIssue> issue, Action excludePage, CancellationToken cancellation)
    {
        string number = page.ToString(CultureInfo.InvariantCulture);
        string[] range = ["-f", number, "-l", number];
        ProcessResult text;
        try
        {
            text = RunTool("pdftotext", [.. range, "-enc", "UTF-8", "-layout", "-nopgbrk", path, "-"], 20, cancellation);
            if (text.Code != 0) throw new IOException("PDF-Textebene konnte nicht gelesen werden.");
        }
        catch (Exception error) when (error is Win32Exception or TimeoutException or IOException)
        {
            issue(new(error is Win32Exception ? "pdf_unavailable" : "parse_failed",
                error is Win32Exception ? "Die lokale PDF-Komponente pdftotext fehlt." : error.Message, page));
            yield break;
        }
        string extracted = text.Text.Trim();
        if (extracted.Length > 0) yield return new Unit(extracted, Page: page);
        if (extracted.Count(char.IsLetterOrDigit) >= 80 && extracted.Split('\n').Count(line => !string.IsNullOrWhiteSpace(line)) >= 3)
            yield break;

        ProcessResult imageList;
        try { imageList = RunTool("pdfimages", [.. range, "-list", path], 20, cancellation); }
        catch (Exception error) when (error is Win32Exception or TimeoutException or IOException)
        {
            issue(new(error is Win32Exception ? "pdf_unavailable" : "parse_failed",
                error is Win32Exception ? "Die lokale PDF-Komponente pdfimages fehlt." : error.Message, page));
            yield break;
        }
        if (imageList.Code != 0)
        {
            issue(new("parse_failed", "PDF-Bildinformationen konnten nicht gelesen werden.", page));
            yield break;
        }
        bool substantialImage = imageList.Text.Split('\n').Any(line =>
        {
            string[] columns = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            return columns.Length >= 5 && columns[2] == "image" &&
                int.TryParse(columns[3], out int width) && int.TryParse(columns[4], out int height) &&
                width >= 240 && height >= 240 && (long)width * height >= 150000;
        });
        if (!substantialImage)
        {
            if (extracted.Length == 0) yield return new Unit("", Page: page);
            yield break;
        }
        if (options.NoOcr)
        {
            if (extracted.Length == 0) excludePage();
            yield break;
        }

        string? recognized = null;
        string? directory = null;
        try
        {
            directory = Directory.CreateTempSubdirectory("wrg-").FullName;
            string prefix = Path.Combine(directory, "page");
            var render = RunTool("pdftoppm", [.. range, "-scale-to", "2400", "-gray", "-png", "-singlefile", path, prefix], 30, cancellation);
            if (render.Code != 0) throw new IOException("PDF-Seite konnte nicht gerendert werden.");
            string tessdata = Path.Combine(AppContext.BaseDirectory, "tools", "tesseract", "tessdata");
            string[] languageData = Directory.Exists(tessdata) ? ["--tessdata-dir", tessdata] : [];
            var ocr = RunTool("tesseract", [prefix + ".png", "stdout", "-l", "deu+eng", .. languageData], 45, cancellation);
            if (ocr.Code != 0)
                issue(new("ocr_unavailable", "Tesseract konnte die Seite mit den Sprachdaten deu+eng nicht erkennen.", page));
            else recognized = ocr.Text.Trim();
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
        if (recognized is not null) yield return new Unit(recognized, Source: "ocr", Page: page);
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
