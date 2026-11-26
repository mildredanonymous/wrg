using System.Security.Cryptography;
using System.Text;

// Each slot stores its full key, so collisions evict a single older entry.
sealed class OcrCache
{
    private const int MaxEntryBytes = 256 * 1024;
    private readonly string directory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify), "wrg", "ocr-v1");
    private readonly Lazy<string?> fingerprint = new(ModelFingerprint);

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

    public string? Read(string? key)
    {
        if (key is null) return null;
        try
        {
            using var stream = new FileStream(Slot(key), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (stream.Length > MaxEntryBytes) return null;
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return reader.ReadLine() == key ? reader.ReadToEnd() : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }

    public void Write(string? key, string text)
    {
        if (key is null) return;
        string entry = key + "\n" + text;
        if (Encoding.UTF8.GetByteCount(entry) > MaxEntryBytes) return;
        string temporary = Path.Combine(directory, $"{Guid.NewGuid():N}.tmp");
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(temporary, entry);
            File.Move(temporary, Slot(key), overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    private string Slot(string key)
    {
        // 1,024 slots of at most 256 KiB.
        int slot = Convert.ToInt32(key[..3], 16) & 1023;
        return Path.Combine(directory, $"{slot:X3}.txt");
    }

    private static string? ModelFingerprint()
    {
        string bundled = Path.Combine(AppContext.BaseDirectory, "tools", "tesseract");
        string executable = Path.Combine(bundled, OperatingSystem.IsWindows() ? "tesseract.exe" : "tesseract");
        string? data = Path.Combine(bundled, "tessdata");
        if (!Directory.Exists(data)) data = Environment.GetEnvironmentVariable("TESSDATA_PREFIX");
        if (!File.Exists(executable) && !OperatingSystem.IsWindows())
        {
            executable = (Environment.GetEnvironmentVariable("PATH") ?? "")
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Where(Path.IsPathFullyQualified)
                .Select(folder => Path.Combine(folder, "tesseract"))
                .FirstOrDefault(File.Exists) ?? executable;
        }

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
