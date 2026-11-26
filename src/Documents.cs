using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

sealed record DocumentIssue(string Code, string Message, int? Page = null);

static partial class Documents
{
    public const long FileLimit = 256L * 1024 * 1024;
    private const long TextLimit = 32L * 1024 * 1024;
    private static readonly Regex Heading = new(@"^ {0,3}#{1,6}[ \t]+(.+?)\s*#*\s*$");
    private static readonly XNamespace Word = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    public static IEnumerable<Unit> Read(string path, string type, Options options,
        Action<DocumentIssue> issue, Action excludePage, Action pageFinished, Action<int> pageStarted, CancellationToken cancellation) => type switch
    {
        "docx" => Docx(path, options.Field, cancellation),
        "pdf" => Pdf(path, options, issue, excludePage, pageFinished, pageStarted, cancellation),
        _ when options.Field is not ("all" or "content") => [],
        _ => Text(path, type == "md", cancellation)
    };

    private static IEnumerable<Unit> Text(string path, bool markdown, CancellationToken cancellation)
    {
        using var file = File.OpenRead(path);
        if (file.Length > TextLimit) throw new InvalidDataException("Textdatei überschreitet 32 MiB.");
        var bytes = new byte[checked((int)file.Length)];
        file.ReadExactly(bytes);
        ReadOnlySpan<byte> data = bytes;
        Encoding encoding = new UTF8Encoding(false, true);
        int bom = 0;
        if (data.StartsWith(new byte[] { 0xef, 0xbb, 0xbf })) bom = 3;
        else if (data.StartsWith(new byte[] { 0xff, 0xfe }))
        {
            encoding = new UnicodeEncoding(false, false, true);
            bom = 2;
        }
        else if (data.StartsWith(new byte[] { 0xfe, 0xff }))
        {
            encoding = new UnicodeEncoding(true, false, true);
            bom = 2;
        }
        return Lines(encoding.GetString(data[bom..]), markdown, cancellation);
    }

    private static IEnumerable<Unit> Lines(string text, bool markdown, CancellationToken cancellation)
    {
        using var reader = new StringReader(text);
        int number = 0, first = 0;
        string? section = null;
        var paragraph = new StringBuilder();
        char fence = '\0';
        int fenceLength = 0;
        while (reader.ReadLine() is { } line)
        {
            cancellation.ThrowIfCancellationRequested();
            number++;
            if (!markdown)
            {
                yield return new Unit(line, Line: number);
                continue;
            }
            string trimmed = line.TrimStart(' ');
            int markerLength = trimmed.Length == 0 ? 0 : trimmed.TakeWhile(c => c == trimmed[0]).Count();
            bool marker = line.Length - trimmed.Length <= 3 && markerLength >= 3 && trimmed[0] is '`' or '~';
            if (fence != '\0')
            {
                if (marker && trimmed[0] == fence && markerLength >= fenceLength &&
                    string.IsNullOrWhiteSpace(trimmed[markerLength..]))
                    fence = '\0';
                else
                    yield return new Unit(line, Line: number, Section: section);
                continue;
            }
            Match heading = Heading.Match(line);
            bool setext = paragraph.Length > 0 && first == number - 1 &&
                Regex.IsMatch(line, @"^ {0,3}(=+|-+)\s*$");
            if (setext)
            {
                section = paragraph.ToString().Trim();
                yield return new Unit(paragraph.ToString(), Line: first, Section: section);
                paragraph.Clear();
                continue;
            }
            if (heading.Success || marker || string.IsNullOrWhiteSpace(line))
            {
                if (paragraph.Length > 0) yield return new Unit(paragraph.ToString(), Line: first, Section: section);
                paragraph.Clear();
                if (heading.Success)
                {
                    section = heading.Groups[1].Value.Trim();
                    yield return new Unit(line, Line: number, Section: section);
                }
                if (marker)
                {
                    fence = trimmed[0];
                    fenceLength = markerLength;
                }
                continue;
            }
            if (paragraph.Length == 0) first = number;
            else paragraph.Append('\n');
            paragraph.Append(line);
        }
        if (paragraph.Length > 0) yield return new Unit(paragraph.ToString(), Line: first, Section: section);
    }

    private static IEnumerable<Unit> Docx(string path, string scope, CancellationToken cancellation)
    {
        using var archive = ZipFile.OpenRead(path);
        if (archive.Entries.Count > 10000 || archive.Entries.Sum(entry => (decimal)entry.Length) > 128 * 1024 * 1024)
            throw new InvalidDataException("Entpacktes DOCX überschreitet das Grössen- oder Eintragslimit.");
        if (scope != "content")
        {
            XDocument? core = ReadXml(archive.GetEntry("docProps/core.xml"));
            XNamespace dc = "http://purl.org/dc/elements/1.1/";
            foreach (var (field, name) in new[] { ("title", dc + "title"), ("author", dc + "creator") })
                if ((scope == "all" || scope == field) &&
                    core?.Root?.Element(name)?.Value is { Length: > 0 } value)
                    yield return new Unit(value, field, "metadata");
        }
        if (scope is not ("all" or "content")) yield break;

        XElement body = ReadXml(archive.GetEntry("word/document.xml"))?.Root?.Element(Word + "body")
            ?? throw new InvalidDataException("DOCX-Haupttext fehlt.");
        int paragraph = 0;
        string? section = null;
        foreach (XElement element in body.Descendants())
        {
            cancellation.ThrowIfCancellationRequested();
            if (element.Name == Word + "tc")
            {
                XElement[] cell = element.Descendants(Word + "p")
                    .Where(p => p.Ancestors(Word + "tc").FirstOrDefault() == element).ToArray();
                if (cell.Length == 0) continue;
                string text = string.Join('\n', cell.Select(ParagraphText));
                yield return new Unit(text, Paragraph: paragraph + 1, Section: section);
                continue;
            }
            if (element.Name != Word + "p") continue;
            paragraph++;
            if (element.Ancestors(Word + "tc").Any()) continue;
            string value = ParagraphText(element);
            XElement? style = element.Element(Word + "pPr");
            string? styleName = (string?)style?.Element(Word + "pStyle")?.Attribute(Word + "val");
            if (style?.Element(Word + "outlineLvl") is not null ||
                styleName is not null && Regex.IsMatch(styleName, @"^(Heading|berschrift|Überschrift)[1-9]$", RegexOptions.IgnoreCase))
                section = value;
            yield return new Unit(value, Paragraph: paragraph, Section: section);
        }
    }

    private static string ParagraphText(XElement paragraph) => string.Concat(paragraph.Descendants()
        .Where(node => node.Ancestors(Word + "p").FirstOrDefault() == paragraph)
        .Select(node => node.Name == Word + "t" ? node.Value :
            node.Name == Word + "tab" ? "\t" : node.Name == Word + "br" ? "\n" : ""));

    private static XDocument? ReadXml(ZipArchiveEntry? entry)
    {
        if (entry is null) return null;
        if (entry.Length > TextLimit) throw new InvalidDataException("DOCX-XML überschreitet 32 MiB.");
        using Stream stream = entry.Open();
        using XmlReader reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = TextLimit
        });
        return XDocument.Load(reader);
    }
}
