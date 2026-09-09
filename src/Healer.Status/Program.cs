#pragma warning disable CS0618 // Application's static facade is obsolete in favor of a newer
// instance-based API in this v2 release; still fully functional for a simple single-window app.

using System.Collections.ObjectModel;
using Healer.Core.Models;
using Healer.Host.Config;
using Healer.Host.Docker;
using Healer.Host.History;
using Healer.Host.Metrics;
using Healer.Status.Logic;
using Terminal.Gui.App;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

var configPath = Environment.GetEnvironmentVariable("HEALER_CONFIG_PATH") ?? "/etc/healer/healer.json";

Healer.Core.Configuration.HealerConfig config;
try
{
    config = await HealerConfigLoader.LoadAsync(configPath, CancellationToken.None);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Couldn't load config from {configPath}: {ex.Message}");
    return 1;
}

var dockerApi = new DockerApiClient(config.DockerSocketPath);
var containerRuntime = new DockerSocketHttpClient(dockerApi, TimeProvider.System);
var hostMetricsProvider = new HostMetricsProvider(config.Thresholds.DiskMountsToCheck);
var historyStore = new SqliteHistoryStore(config.History.HistoryDbPath);

Application.Init();
try
{
    var window = new Window { Title = "Healer Status — Ctrl+Q to quit, Ctrl+E to export full log" };

    var liveLabel = new Label { X = 0, Y = 0, Width = Dim.Fill(), Text = "Loading..." };

    var trendFrame = new FrameView { Title = "Host memory — last 24h", X = 0, Y = 1, Width = Dim.Fill(), Height = 4 };
    var trendLabel = new Label { X = 0, Y = 0, Width = Dim.Fill(), Text = "(loading)" };
    trendFrame.Add(trendLabel);

    var actionsFrame = new FrameView { Title = "Recent incidents / actions", X = 0, Y = Pos.Bottom(trendFrame), Width = Dim.Fill(), Height = Dim.Percent(50) };
    var actionsList = new ListView { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill() };
    actionsFrame.Add(actionsList);

    var logsFrame = new FrameView { Title = "Recent log lines", X = 0, Y = Pos.Bottom(actionsFrame), Width = Dim.Fill(), Height = Dim.Fill() };
    var logsView = new TextView { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(), ReadOnly = true, WordWrap = false };
    logsFrame.Add(logsView);

    window.Add(liveLabel, trendFrame, actionsFrame, logsFrame);

    string? currentLogFile = null;

    async Task RefreshAsync()
    {
        try
        {
            var host = await hostMetricsProvider.GetHostMetricsAsync(CancellationToken.None);
            var containers = await containerRuntime.ListContainersAsync(CancellationToken.None);
            var unhealthy = containers.Count(c => c.HealthStatus == ContainerHealthStatus.Unhealthy);
            liveLabel.Text = ActionHistoryFormatter.FormatLiveSummary(host, containers.Count, unhealthy);

            var trend = await historyStore.QueryTrendAsync("host.mem", DateTimeOffset.UtcNow.AddHours(-24), CancellationToken.None);
            var bucketed = TrendSparklineRenderer.Bucket(trend, 60);
            trendLabel.Text = TrendSparklineRenderer.Render(bucketed);

            var actions = await historyStore.QueryActionsAsync(0, 100, CancellationToken.None);
            actionsList.SetSource(new ObservableCollection<string>(actions.Items.Select(ActionHistoryFormatter.FormatRow)));

            var logFile = LogFileReader.FindLatestLogFile(config.Logging.LogDirectory);
            currentLogFile = logFile;
            if (logFile is not null)
            {
                var allLines = await File.ReadAllLinesAsync(logFile, CancellationToken.None);
                var tail = LogFileReader.TailLines(allLines, maxLines: 500);
                logsView.Text = string.Join('\n', tail);
                logsFrame.Title = $"Recent log lines — {Path.GetFileName(logFile)} (Ctrl+E to export the full file)";
            }
            else
            {
                logsView.Text = "(no log file found yet)";
            }
        }
        catch (Exception ex)
        {
            liveLabel.Text = $"Error refreshing: {ex.Message}";
        }
    }

    async Task ExportFullLogAsync()
    {
        if (currentLogFile is null)
        {
            logsFrame.Title = "Recent log lines — no log file to export yet";
            return;
        }

        try
        {
            var fullContent = await File.ReadAllTextAsync(currentLogFile, CancellationToken.None);
            var exportPath = LogFileReader.BuildExportFilePath(config.Logging.LogDirectory, DateTimeOffset.Now);
            await File.WriteAllTextAsync(exportPath, fullContent, CancellationToken.None);

            // Best-effort only: Terminal.Gui's clipboard needs xclip/xsel/wl-copy on plain Linux (not
            // installed on a headless EC2 box) or WSL's powershell.exe interop. The file written above
            // is the reliable path either way — it survives regardless of terminal/SSH client support,
            // and can be `cat`/`scp`'d out of the box, unlike a mouse-drag selection over this
            // full-screen TUI which can only ever grab what's currently on screen.
            var clipboardNote = "";
            try
            {
                if (Clipboard.IsSupported && Clipboard.TrySetClipboardData(fullContent))
                {
                    clipboardNote = ", also copied to clipboard";
                }
            }
            catch
            {
                // Clipboard support is inherently environment-dependent — silently fall back to the file.
            }

            logsFrame.Title = $"Recent log lines — full log exported to {exportPath}{clipboardNote}";
        }
        catch (Exception ex)
        {
            logsFrame.Title = $"Recent log lines — export failed: {ex.Message}";
        }
    }

    // The title promises Ctrl+Q closes the app and Ctrl+E exports the log — this wires both up.
    // Found missing during manual verification: nothing in Terminal.Gui binds this for a plain
    // Window instance by default (AddCommand/KeyBindings.Add are meant for View subclasses, not
    // composition on a stock instance — both are protected), so without this the title's own
    // instructions did nothing. KeyDown is a public event, so this works without subclassing Window.
    window.KeyDown += (_, key) =>
    {
        if (key == Key.Q.WithCtrl)
        {
            key.Handled = true;
            Application.RequestStop();
        }
        else if (key == Key.E.WithCtrl)
        {
            key.Handled = true;
            _ = ExportFullLogAsync();
        }
    };

    _ = RefreshAsync();
    Application.AddTimeout(TimeSpan.FromSeconds(5), () =>
    {
        _ = RefreshAsync();
        return true;
    });

    Application.Run(window, null);
}
finally
{
    Application.Shutdown();
}

return 0;
