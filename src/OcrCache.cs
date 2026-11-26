using System.Globalization;
using System.Security.Cryptography;
using System.Text;

// Each slot stores its full key, so collisions evict a single older entry.
sealed class OcrCache
{
    private const int MaxEntryBytes = 256 * 1024;
    private readonly string directory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify), "wrg", "ocr-v1");
    private readonly Lazy<string?> fingerprint = new(ModelFingerprint);
    private readonly Lazy<string?> renderer = new(() =>
        HashFile(FindTool(Path.Combine("poppler", "Library", "bin"), "pdftoppm")));

    public string? Key(string image)
    {
        try
        {
            if (!Path.IsPathFullyQualified(directory) || fingerprint.Value is not { } version) return null;
            using FileStream stream = File.OpenRead(image);
            return Convert.ToHexString(SHA256.HashData(stream)) + version;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }

    public static string? PdfFingerprint(string path, CancellationToken cancellation)
    {
        try
        {
            using FileStream stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashDataAsync(stream, cancellation).AsTask().GetAwaiter().GetResult());
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }

    public string? PageKey(Lazy<string?>? pdfFingerprint, int page)
    {
        try
        {
            if (!Path.IsPathFullyQualified(directory) || fingerprint.Value is not { } model ||
                renderer.Value is not { } render || pdfFingerprint?.Value is not { } pdf)
                return null;
            string source = $"pdf-page-v1:gray:2400:png:{render}:{model}:{pdf}:{page.ToString(CultureInfo.InvariantCulture)}";
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }

    public string? ReadPage(string? pageKey)
    {
        string? imageKey = ReadEntry(pageKey, pageReference: true);
        return imageKey is { Length: 128 } && imageKey.All(Uri.IsHexDigit) ? Read(imageKey) : null;
    }

    public void RememberPage(string? pageKey, string? imageKey)
    {
        if (imageKey is not null) WriteEntry(pageKey, imageKey, pageReference: true);
    }

    public string? Read(string? key) => ReadEntry(key, pageReference: false);

    public void Write(string? key, string text) => WriteEntry(key, text, pageReference: false);

    private string? ReadEntry(string? key, bool pageReference)
    {
        if (key is null) return null;
        try
        {
            using var stream = new FileStream(Slot(key, pageReference), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (stream.Length > MaxEntryBytes) return null;
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return reader.ReadLine() == key ? reader.ReadToEnd() : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }

    private void WriteEntry(string? key, string text, bool pageReference)
    {
        if (key is null) return;
        string entry = key + "\n" + text;
        if (Encoding.UTF8.GetByteCount(entry) > MaxEntryBytes) return;
        string temporary = Path.Combine(directory, $"{Guid.NewGuid():N}.tmp");
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(temporary, entry);
            File.Move(temporary, Slot(key, pageReference), overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    private string Slot(string key, bool pageReference)
    {
        int slot = Convert.ToInt32(key[..3], 16);
        // References are small pointers to the existing, separately bounded text entries.
        return Path.Combine(directory, pageReference ? $"{slot:X3}.ref" : $"{slot & 1023:X3}.txt");
    }

    private static string? HashFile(string path)
    {
        if (!File.Exists(path)) return null;
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static string FindTool(string folder, string tool)
    {
        string bundled = Path.Combine(AppContext.BaseDirectory, "tools", folder,
            tool + (OperatingSystem.IsWindows() ? ".exe" : ""));
        if (File.Exists(bundled) || OperatingSystem.IsWindows()) return bundled;
        return (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Where(Path.IsPathFullyQualified)
            .Select(path => Path.Combine(path, tool))
            .FirstOrDefault(File.Exists) ?? bundled;
    }

    private static string? ModelFingerprint()
    {
        string bundled = Path.Combine(AppContext.BaseDirectory, "tools", "tesseract");
        string executable = FindTool("tesseract", "tesseract");
        string? data = Path.Combine(bundled, "tessdata");
        if (!Directory.Exists(data)) data = Environment.GetEnvironmentVariable("TESSDATA_PREFIX");

        if (data is null) return null;
        var hashes = new byte[3 * SHA256.HashSizeInBytes];
        string[] files = [executable, Path.Combine(data, "deu.traineddata"), Path.Combine(data, "eng.traineddata")];
        for (int i = 0; i < files.Length; i++)
        {
            using FileStream stream = File.OpenRead(files[i]);
            SHA256.HashData(stream).CopyTo(hashes, i * SHA256.HashSizeInBytes);
        }
        return Convert.ToHexString(SHA256.HashData(hashes));
    }
}
