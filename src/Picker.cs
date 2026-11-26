using System.ComponentModel;
using System.Diagnostics;

static class Picker
{
    public static bool Select(Result[] hits, CancellationToken cancellation)
    {
        int index = 0;
        Console.WriteLine("Auf/Ab wählen · Enter öffnet · Esc schliesst");
        Show();
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            if (!Console.KeyAvailable)
            {
                cancellation.WaitHandle.WaitOne(40);
                continue;
            }
            switch (Console.ReadKey(intercept: true).Key)
            {
                case ConsoleKey.Escape: return true;
                case ConsoleKey.UpArrow: index = Math.Max(0, index - 1); Show(); break;
                case ConsoleKey.DownArrow: index = Math.Min(hits.Length - 1, index + 1); Show(); break;
                case ConsoleKey.Enter: return Open(hits[index], TryStart);
            }
        }

        void Show()
        {
            Console.WriteLine();
            int first = Math.Max(0, index - 2);
            int last = Math.Min(hits.Length, index + 3);
            for (int i = first; i < last; i++)
                Console.WriteLine($"{(i == index ? ">" : " ")} {Output.Describe(hits[i], false)}");
            Console.WriteLine($"Treffer {index + 1}/{hits.Length}");
        }
    }

    internal static bool Open(Result result, Func<ProcessStartInfo, bool> startViewer)
    {
        if (result.Type == "pdf" && result.Unit.Page is int page)
        {
            string? configured = Environment.GetEnvironmentVariable("WRG_SUMATRA_PATH");
            string? sumatra = configured is null ? FindSumatra() :
                Path.IsPathFullyQualified(configured) && File.Exists(configured) &&
                string.Equals(Path.GetExtension(configured), ".exe", StringComparison.OrdinalIgnoreCase) ? configured : null;
            if (sumatra is not null)
            {
                var start = new ProcessStartInfo(sumatra) { UseShellExecute = false };
                start.ArgumentList.Add("-page");
                start.ArgumentList.Add(page.ToString(System.Globalization.CultureInfo.InvariantCulture));
                start.ArgumentList.Add(result.Path);
                if (startViewer(start)) return true;
                Console.Error.WriteLine("SumatraPDF konnte nicht gestartet werden. Dateizuordnung wird verwendet.");
            }
            else if (configured is not null)
                Console.Error.WriteLine("WRG_SUMATRA_PATH muss auf eine vorhandene ausführbare Datei zeigen. Dateizuordnung wird verwendet.");
        }
        Console.WriteLine(Output.Describe(result, true));
        return startViewer(new ProcessStartInfo(result.Path) { UseShellExecute = true, Verb = "open" });
    }

    private static bool TryStart(ProcessStartInfo start)
    {
        try
        {
            using Process? process = Process.Start(start);
            return process is not null || start.UseShellExecute;
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException or IOException)
        {
            Console.Error.WriteLine(Output.Clean(error.Message));
            return false;
        }
    }

    private static string? FindSumatra() => (Environment.GetEnvironmentVariable("PATH") ?? "")
        .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
        .Select(path => path.Trim('"')).Where(Path.IsPathFullyQualified)
        .Select(path => Path.Combine(path, "SumatraPDF.exe")).FirstOrDefault(File.Exists);
}
