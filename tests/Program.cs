using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

if (args.Length == 0) throw new ArgumentException("Pfad zu wrg.exe oder wrg.dll angeben.");
string application = Path.GetFullPath(args[0]);
DirectoryInfo root = Directory.CreateTempSubdirectory("wrg-tests-");
try
{
    string txt = Path.Combine(root.FullName, "Verträge 2026.TXT");
    File.WriteAllText(txt, "Zertifikat TLS-ID\nKündigungsfrist Entwurf\nKündigungsfrist\n", Encoding.BigEndianUnicode);
    string md = Path.Combine(root.FullName, "Handbuch.md");
    File.WriteAllText(md, "# Zugriff\n\nTLS und\nZertifikat\n");
    string sub = Directory.CreateDirectory(Path.Combine(root.FullName, "Unterordner")).FullName;
    string docx = Path.Combine(sub, "Vertrag.docx");
    using (var zip = ZipFile.Open(docx, ZipArchiveMode.Create))
    {
        Add("docProps/core.xml", """
            <cp:coreProperties xmlns:cp="http://schemas.openxmlformats.org/package/2006/metadata/core-properties"
                               xmlns:dc="http://purl.org/dc/elements/1.1/">
              <dc:title>Dokumenttitel</dc:title><dc:creator>Keller</dc:creator>
            </cp:coreProperties>
            """);
        Add("word/document.xml", """
            <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
            <w:body>
              <w:p><w:pPr><w:pStyle w:val="Heading1"/></w:pPr><w:r><w:t>Vertrag</w:t></w:r></w:p>
              <w:tbl><w:tr><w:tc>
                <w:p><w:r><w:t>Zertifikat</w:t></w:r></w:p>
                <w:p><w:r><w:t>TLS</w:t></w:r></w:p>
              </w:tc></w:tr></w:tbl>
            </w:body></w:document>
            """);
        void Add(string name, string contents)
        {
            using var writer = new StreamWriter(zip.CreateEntry(name).Open());
            writer.Write(contents);
        }
    }
    Require(Run(0, "--help").Contains("--no-ocr"), "Hilfe");
    Require(Run(0, "--version").StartsWith("wrg "), "Version");
    Require(Run(0, "--exact", "--", "TLS-ID", txt).Contains("Zeile 1"), "Optionsende und UTF-16 BE");
    Require(Run(1, "TLS", root.FullName, "--type", "docx", "--no-recursion") == "", "Keine Rekursion");
    Require(Run(0, "Zertifikat", "--exact").Contains("Verträge 2026.TXT"), "Aktueller Ordner");
    var found = Json(0, "TLS Zertifikat", txt, root.FullName, "--exact");
    Require(found.Count(r => Kind(r) == "match") == 3, "Deduplizierung und Einheiten");
    Require(One(found, "match", "file_type", "md").GetProperty("section").GetString() == "Zugriff", "Markdown-Abschnitt");
    Require(One(found, "match", "file_type", "docx").GetProperty("paragraph").GetInt32() == 2, "DOCX-Tabellenposition");
    Require(One(Json(0, "Keller", docx, "--field", "author"), "match", "field", "author")
        .GetProperty("source").GetString() == "metadata", "DOCX-Autor");
    Require(One(Json(0, "Dokumenttitel", docx, "--field", "title"), "match", "field", "title")
        .GetProperty("page").ValueKind == JsonValueKind.Null, "DOCX-Titel");
    Require(Json(0, "Kündigungsfrist", txt, "--exclude-word", "ENTWURF")
        .Single(r => Kind(r) == "match").GetProperty("line").GetInt32() == 3, "Ausschlusswort");
    Require(Json(0, "Zertifkat", txt).Single(r => Kind(r) == "match")
        .GetProperty("edit_distance").GetInt32() == 1, "Vertauschung");
    Require(!Json(1, "Zertifkat", txt, "--exact").Any(r => Kind(r) == "match"), "Wörtliche Suche");
    Require(Json(0, "Kuendigungsfrist", txt).First(r => Kind(r) == "match")
        .GetProperty("match_kind").GetString() == "spelling_variant", "Umlautvariante");
    Require(Json(0, "TLS und Zertifikat", md, "--phrase").Single(r => Kind(r) == "match")
        .GetProperty("line").GetInt32() == 3, "Zeilenübergreifende Markdown-Phrase");
    string unicode = Path.Combine(root.FullName, "Unicode.txt");
    File.WriteAllText(unicode, "Ku\u0308ndigung 𐐀𐐨\n");
    var unicodeHit = Json(0, "Kündigung", unicode, "--exact").Single(r => Kind(r) == "match");
    Require(unicodeHit.GetProperty("matched_text")[0].GetString() == "Ku\u0308ndigung", "Unicode-Originaltext");
    Json(1, "𐐀", unicode, "--exact");
    Json(0, "𐐨𐐨", unicode, "--exact");
    string midnight = Path.Combine(root.FullName, "Mitternacht.txt");
    File.WriteAllText(midnight, "Zertifikat");
    var limit = new DateTime(2026, 1, 15);
    File.SetLastWriteTime(midnight, limit);
    Json(0, "Zertifikat", midnight, "--modified-since", "2026-01-15");
    File.SetLastWriteTime(midnight, limit.AddSeconds(-1));
    Json(1, "Zertifikat", midnight, "--modified-since", "2026-01-15");
    Json(1, "TLA", txt);
    Json(1, "Zertifikat TLS", txt, "--modified-since", "2099-01-01");
    Json(2, "--unknown");
    Json(2, "Zertifikat", "--interactive");
    string invalid = Path.Combine(root.FullName, "Kaputt.txt");
    File.WriteAllBytes(invalid, [0x61, 0xff]);
    var partial = Json(3, "Zertifikat", txt, invalid);
    Require(Kind(partial[1]) == "match" && partial.Any(r => Kind(r) == "issue" &&
        r.GetProperty("code").GetString() == "decode_failed"), "Teilfehler mit vorherigem Treffer");
    Json(2, "Zertifikat", invalid, "--field", "content");
    Json(2, "Zertifikat", Path.Combine(root.FullName, "Fehlt.txt"));

    if (args.Length >= 2)
    {
        string fixture = Path.GetFullPath(args[1]);
        var pdf = Json(0, "Zertifikat", fixture, "--type", "pdf", "--exact");
        Require(pdf.Any(r => Kind(r) == "match" && r.GetProperty("source").GetString() == "text" &&
            r.GetProperty("page").GetInt32() == 1), "PDF-Textebene");
        Require(pdf.Any(r => Kind(r) == "match" && r.GetProperty("source").GetString() == "ocr" &&
            r.GetProperty("page").GetInt32() == 2), "PDF-Scan trotz textlicher Kopfzeile");
        var noOcr = Json(0, "Zertifikat", fixture, "--type", "pdf", "--no-ocr");
        Require(noOcr.All(r => Kind(r) != "match" || r.GetProperty("source").GetString() != "ocr"), "OCR abschaltbar");
        Require(noOcr[^1].GetProperty("pages_excluded").GetInt32() == 1, "Reine Bildseite ausgeschlossen");
        var hyphenation = Json(0, "Kündigungsfrist", fixture, "--exact", "--no-ocr")
            .Single(r => Kind(r) == "match");
        Require(hyphenation.GetProperty("matched_text")[0].GetString()!.StartsWith("Kündigungs-"),
            "Silbentrennung mit Originaltext");
        var english = Json(0, "certificate", fixture, "--exact");
        Require(english.Any(r => Kind(r) == "match" && r.GetProperty("page").GetInt32() == 3 &&
            r.GetProperty("source").GetString() == "ocr"), "Englischer Treffer auf reiner Scan-Seite");
        string broken = Path.Combine(root.FullName, "Defekt.pdf");
        File.WriteAllText(broken, "Keine PDF-Datei.");
        Require(Json(2, "Zertifikat", broken).Any(r => Kind(r) == "issue" &&
            r.GetProperty("code").GetString() == "parse_failed"), "Defektes PDF");
    }
    string fakeViewer = Path.Combine(root.FullName, "SumatraPDF.exe");
    File.WriteAllText(fakeViewer, "");
    string selectedPath = Path.Combine(sub, "Name mit Leerzeichen.pdf");
    var selectedResult = new Result(selectedPath, "pdf", DateTime.UtcNow,
        new Unit("", Page: 3), new Hit(0, 0, ["Zertifikat"], 0), "Das Zertifikat");
    string? previousViewer = Environment.GetEnvironmentVariable("WRG_SUMATRA_PATH");
    try
    {
        Environment.SetEnvironmentVariable("WRG_SUMATRA_PATH", fakeViewer);
        var calls = new List<ProcessStartInfo>();
        Require(Picker.Open(selectedResult, start => { calls.Add(start); return true; }), "SumatraPDF-Start");
        Require(calls.Single().FileName == fakeViewer &&
            calls[0].ArgumentList.SequenceEqual(["-page", "3", selectedPath]) &&
            !calls[0].UseShellExecute, "SumatraPDF-Seitensprung mit vollständigem Pfad");
        calls.Clear();
        Require(Picker.Open(selectedResult, start => { calls.Add(start); return start.UseShellExecute; }),
            "Rückfall auf Dateizuordnung");
        Require(calls.Count == 2 && calls[1].FileName == selectedPath &&
            calls[1].UseShellExecute, "Dateizuordnung für ausgewählten Treffer");
    }
    finally { Environment.SetEnvironmentVariable("WRG_SUMATRA_PATH", previousViewer); }
    Console.WriteLine("Alle ausgeführten Smoke-Tests bestanden.");
}
finally { root.Delete(recursive: true); }

string Run(int expected, params string[] arguments)
{
    bool dll = application.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
    var start = new ProcessStartInfo(dll ? "dotnet" : application)
    {
        WorkingDirectory = root.FullName, UseShellExecute = false,
        RedirectStandardOutput = true, RedirectStandardError = true,
        StandardOutputEncoding = new UTF8Encoding(false, true)
    };
    if (dll) start.ArgumentList.Add(application);
    foreach (string arg in arguments) start.ArgumentList.Add(arg);
    using Process process = Process.Start(start)!;
    Task<string> stdout = process.StandardOutput.ReadToEndAsync();
    Task<string> stderr = process.StandardError.ReadToEndAsync();
    if (!process.WaitForExit(120000))
    {
        process.Kill(entireProcessTree: true);
        throw new Exception("Zeitlimit beim Smoke-Test.");
    }
    Require(process.ExitCode == expected,
        $"Exitcode {process.ExitCode}, erwartet {expected}: {string.Join(" ", arguments)}\n{stderr.Result}\n{stdout.Result}");
    return stdout.Result;
}

JsonElement[] Json(int expected, params string[] arguments)
{
    JsonElement[] records = Run(expected, [.. arguments, "--format", "jsonl"])
        .Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line =>
        {
            using var doc = JsonDocument.Parse(line);
            return doc.RootElement.Clone();
        }).ToArray();
    Require(records.Length >= 2 && Kind(records[0]) == "start" && Kind(records[^1]) == "summary" &&
        records.Count(r => Kind(r) == "summary") == 1 &&
        records.All(r => r.GetProperty("version").GetInt32() == 1) &&
        records[^1].GetProperty("exit_code").GetInt32() == expected, "JSONL-Vertrag");
    return records;
}

static JsonElement One(JsonElement[] records, string kind, string key, string value) =>
    records.Single(r => Kind(r) == kind && r.GetProperty(key).GetString() == value);
static string? Kind(JsonElement element) => element.GetProperty("type").GetString();
static void Require(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}
