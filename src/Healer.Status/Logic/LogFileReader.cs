namespace Healer.Status.Logic;

/// <summary>Reads the tail of Healer's own rolling Serilog file, so the log viewer needs no separate log-shipping setup — it just reads what the daemon already writes.</summary>
public static class LogFileReader
{
    public static string? FindLatestLogFile(string logDirectory)
    {
        if (!Directory.Exists(logDirectory))
        {
            return null;
        }

        return Directory.GetFiles(logDirectory, "*.log")
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    public static IReadOnlyList<string> TailLines(IReadOnlyList<string> allLines, int maxLines) =>
        allLines.Count <= maxLines ? allLines : allLines.Skip(allLines.Count - maxLines).ToList();

    /// <summary>
    /// Where "export full log" writes to. A file, not the OS clipboard, is the reliable mechanism
    /// here: healer-status is a full-screen (alternate-buffer) TUI, so a mouse-drag selection over a
    /// long scrolled TextView can only ever capture what's currently on screen, not the whole log —
    /// the exact "no way to copy all" limitation this exists to work around. Writing to a plain file
    /// lets the operator `cat`/`scp` it out over the same SSH session regardless of terminal/clipboard
    /// support (Terminal.Gui's own clipboard integration needs xclip/xsel/wl-copy, which a headless
    /// EC2 box won't have installed). Placed in the log directory itself since that's already
    /// guaranteed writable — Serilog is already writing there.
    /// </summary>
    public static string BuildExportFilePath(string logDirectory, DateTimeOffset now) =>
        Path.Combine(logDirectory, $"healer-status-export-{now:yyyyMMdd-HHmmss}.txt");
}
