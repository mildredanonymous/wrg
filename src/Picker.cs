using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

static class Picker
{
    public static bool Select(Result[] hits, CancellationToken cancellation)
    {
        int index = 0;
        bool allOpened = true;
        string? status = null;
        bool alternateScreen = TryEnableTerminal(out IntPtr handle, out uint originalMode);
        try
        {
            if (alternateScreen) Console.Write("\x1b[?1049h\x1b[?25l");
            int width = -1, height = -1;
            bool changed = true;
            while (true)
            {
                cancellation.ThrowIfCancellationRequested();
                int currentWidth = Console.WindowWidth;
                int currentHeight = Console.WindowHeight;
                if (changed || currentWidth != width || currentHeight != height)
                {
                    width = currentWidth;
                    height = currentHeight;
                    string view = Render(hits, index, width, height, status);
                    if (alternateScreen) Console.Write("\x1b[H\x1b[J" + view);
                    else
                    {
                        Console.Clear();
                        Console.Write(view);
                    }
                    changed = false;
                }
                if (!Console.KeyAvailable)
                {
                    cancellation.WaitHandle.WaitOne(40);
                    continue;
                }

                ConsoleKey key = Console.ReadKey(intercept: true).Key;
                if (key == ConsoleKey.Escape) return allOpened;
                if (key == ConsoleKey.Enter)
                {
                    var opened = Open(hits[index], StartViewer);
                    status = opened.Message;
                    allOpened &= opened.Success;
                    changed = true;
                    continue;
                }
                int next = key switch
                {
                    ConsoleKey.UpArrow => Math.Max(0, index - 1),
                    ConsoleKey.DownArrow => Math.Min(hits.Length - 1, index + 1),
                    _ => index
                };
                changed = index != next;
                if (changed) status = null;
                index = next;
            }
        }
        finally
        {
            if (alternateScreen) Console.Write("\x1b[?25h\x1b[?1049l");
            if (handle != IntPtr.Zero) SetConsoleMode(handle, originalMode);
        }
    }

    internal static string Render(Result[] hits, int index, int width, int height, string? status = null)
    {
        int columns = Math.Max(1, width - 1);
        int rows = Math.Max(1, height);
        var lines = new List<string> { $"Treffer {index + 1}/{hits.Length}    ↑/↓ wählen · Enter öffnen · Esc schliessen" };
        if (status is not null) lines.Add(status);
        int count = Math.Min(hits.Length, Math.Clamp(rows - 6 - lines.Count, 1, 5));
        int first = Math.Clamp(index - count / 2, 0, hits.Length - count);
        for (int i = first; i < first + count; i++)
            lines.Add($"{(i == index ? ">" : " ")} {Output.DescribeLocation(hits[i], false)} · {Path.GetFileName(hits[i].Path)}");

        Result selected = hits[index];
        lines.Add("");
        lines.Add(Path.GetRelativePath(Environment.CurrentDirectory, selected.Path));
        lines.Add(Output.DescribeLocation(selected));
        string preview = Output.Clean(selected.Snippet);
        while (preview.Length > 0 && lines.Count < rows)
        {
            int length = Fit(preview, columns);
            if (length == 0) break;
            lines.Add(preview[..length]);
            preview = preview[length..].TrimStart();
        }
        return string.Join(Environment.NewLine, lines.Take(rows).Select(line =>
        {
            string clean = Output.Clean(line);
            int length = Fit(clean, columns);
            return length == clean.Length ? clean : clean[..Fit(clean, Math.Max(1, columns - 1))] + (columns > 1 ? "…" : "");
        }));
    }

    private static int Fit(string text, int columns)
    {
        int length = 0;
        foreach (Rune rune in text.EnumerateRunes())
        {
            // Reserve two columns for characters that may render wide.
            int cells = rune.Value >= 0x1100 && rune.Value != 0x2191 && rune.Value != 0x2193 ? 2 : 1;
            if (columns < cells) return length;
            columns -= cells;
            length += rune.Utf16SequenceLength;
        }
        return length;
    }

    private static bool TryEnableTerminal(out IntPtr handle, out uint originalMode)
    {
        handle = IntPtr.Zero;
        originalMode = 0;
        if (!OperatingSystem.IsWindows()) return Environment.GetEnvironmentVariable("TERM") != "dumb";

        IntPtr output = GetStdHandle(-11);
        if (!GetConsoleMode(output, out originalMode) || !SetConsoleMode(output, originalMode | 0x0004))
            return false;
        handle = output;
        return true;
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetStdHandle(int standardHandle);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetConsoleMode(IntPtr handle, out uint mode);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetConsoleMode(IntPtr handle, uint mode);

    internal static (bool Success, string Message) Open(Result result, Func<ProcessStartInfo, bool> startViewer)
    {
        if (!File.Exists(result.Path))
            return (false, $"Datei fehlt oder ist nicht zugänglich · {result.Path}");

        string location = Output.DescribeLocation(result, details: false);
        string fallback = "";
        if (result.Type == "pdf" && result.Unit.Page is int page)
        {
            string? configured = Environment.GetEnvironmentVariable("WRG_SUMATRA_PATH");
            string? sumatra = configured is null ? FindSumatra() :
                Path.IsPathFullyQualified(configured) && File.Exists(configured) &&
                string.Equals(Path.GetExtension(configured), ".exe", StringComparison.OrdinalIgnoreCase) ? configured : null;
            if (sumatra is not null)
            {
                var start = new ProcessStartInfo(sumatra) { UseShellExecute = false };
                start.ArgumentList.Add("-new-window");
                start.ArgumentList.Add("-page");
                start.ArgumentList.Add(page.ToString(System.Globalization.CultureInfo.InvariantCulture));
                start.ArgumentList.Add(result.Path);
                string? error = TryStart(start);
                if (error is null) return (true, $"An SumatraPDF übergeben · {location}");
                fallback = $" · SumatraPDF: {error}";
            }
            else
                fallback = configured is null ? " · SumatraPDF fehlt" : " · WRG_SUMATRA_PATH ungültig";
        }

        string? shellError = TryStart(new ProcessStartInfo(result.Path) { UseShellExecute = true, Verb = "open" });
        return shellError is null
            ? (true, $"An Standardprogramm übergeben · Ziel: {location}{fallback}")
            : (false, $"Öffnen fehlgeschlagen · {shellError}{fallback}");

        string? TryStart(ProcessStartInfo start)
        {
            try { return startViewer(start) ? null : "Programm konnte nicht gestartet werden."; }
            catch (Exception error) when (error is Win32Exception or InvalidOperationException or IOException)
            {
                return error.Message;
            }
        }
    }

    private static bool StartViewer(ProcessStartInfo start)
    {
        using Process? process = Process.Start(start);
        return process is not null || start.UseShellExecute;
    }

    private static string? FindSumatra()
    {
        var candidates = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(path => path.Trim('"')).Where(Path.IsPathFullyQualified)
            .Select(path => Path.Combine(path, "SumatraPDF.exe"));

        if (OperatingSystem.IsWindows())
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string[] folders = [local, Path.Combine(local, "Programs"),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)];
            candidates = candidates.Concat(folders.Where(Path.IsPathFullyQualified)
                .Select(folder => Path.Combine(folder, "SumatraPDF", "SumatraPDF.exe")));
        }

        return candidates.FirstOrDefault(File.Exists);
    }
}
