using System.Runtime.InteropServices;
using Healer.Core.Configuration;
using Healer.Core.Engine;
using Healer.Host.Config;
using Healer.Host.Docker;
using Healer.Host.History;
using Healer.Host.HostActions;
using Healer.Host.Metrics;
using Healer.Host.Notification;
using Healer.Host.State;
using Serilog;
using Serilog.Events;

// Hand-rolled composition root — deliberately no Microsoft.Extensions.Hosting / generic host and no
// reflection-based config binding, both of which are trim-unsafe under Native AOT. Everything here
// is explicit `new` calls and source-generated JSON.

// Bootstrap logger (console only) covers the window before config is loaded — the real logger's
// file sink settings come FROM config, so it can't exist until config has loaded successfully.
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console()
    .CreateLogger();

var configPath = Environment.GetEnvironmentVariable("HEALER_CONFIG_PATH") ?? "/etc/healer/healer.json";

using var shutdown = new CancellationTokenSource();
PosixSignalRegistration.Create(PosixSignal.SIGTERM, _ => shutdown.Cancel());
PosixSignalRegistration.Create(PosixSignal.SIGINT, _ => shutdown.Cancel());

HealerConfig config;
try
{
    config = await HealerConfigLoader.LoadAsync(configPath, shutdown.Token);
}
catch (Exception ex)
{
    Log.Fatal(ex, "Could not load config from {ConfigPath}", configPath);
    return 1;
}

ConfigureLogging(config.ServerName, config.Logging);

var dockerApi = new DockerApiClient(config.DockerSocketPath);
var containerRuntime = new DockerSocketHttpClient(dockerApi, TimeProvider.System);
var hostSystemActions = new LinuxHostSystemActions(dockerApi);
var hostMetricsProvider = new HostMetricsProvider(config.Thresholds.DiskMountsToCheck);
var stateStore = new JsonFileStateStore(config.StatePath);
var historyStore = new SqliteHistoryStore(config.History.HistoryDbPath);
var composeRestartExecutor = new DockerComposeRestartExecutor();

var botToken = Environment.GetEnvironmentVariable(config.Telegram.BotTokenEnvVar);
var chatId = Environment.GetEnvironmentVariable(config.Telegram.ChatIdEnvVar);
if (string.IsNullOrEmpty(botToken) || string.IsNullOrEmpty(chatId))
{
    Log.Warning(
        "Telegram is not configured ({BotTokenEnvVar}/{ChatIdEnvVar} not set) — alerts will fail. Re-run the setup wizard to fix this.",
        config.Telegram.BotTokenEnvVar, config.Telegram.ChatIdEnvVar);
}

using var telegramHttpClient = new HttpClient();
var notifier = new TelegramNotifier(telegramHttpClient, botToken ?? "", chatId ?? "");

using var systemd = new SystemdNotifier();

var engine = new HealingEngine(
    containerRuntime,
    hostSystemActions,
    hostMetricsProvider,
    stateStore,
    notifier,
    historyStore,
    composeRestartExecutor,
    config,
    TimeProvider.System,
    onWarning: (message, ex) => Log.Warning(ex, "{Message}", message));

Log.Information(
    "Healer started. Server={ServerName} DryRun={DryRun} PollIntervalSeconds={PollIntervalSeconds} Config={ConfigPath}",
    config.ServerName, config.DryRun, config.PollIntervalSeconds, configPath);
systemd.NotifyReady();

using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(1, config.PollIntervalSeconds)));
try
{
    do
    {
        try
        {
            await engine.RunTickAsync(shutdown.Token);
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
            break;
        }
        catch (Exception ex)
        {
            // A single bad tick must never take the whole daemon down — log and keep looping.
            // (systemd's Restart=always + StartLimitIntervalSec=0 is the backstop if this loop itself dies.)
            Log.Error(ex, "Unhandled exception in RunTickAsync");
        }

        systemd.NotifyWatchdog();
    }
    while (await timer.WaitForNextTickAsync(shutdown.Token));
}
catch (OperationCanceledException)
{
    // Expected on SIGTERM/SIGINT.
}

Log.Information("Healer shutting down.");
await Log.CloseAndFlushAsync();
return 0;

void ConfigureLogging(string serverName, LoggingConfig logging)
{
    var level = Enum.TryParse<LogEventLevel>(logging.MinimumLevel, ignoreCase: true, out var parsed)
        ? parsed
        : LogEventLevel.Information;

    Directory.CreateDirectory(logging.LogDirectory);

    // Resource-aware rotation: size-capped AND count-capped, same "keep disk usage low on a
    // constrained box" philosophy as the SQLite history retention — an unbounded log is exactly
    // the kind of thing that could contribute to the disk-pressure incidents Healer exists to catch.
    Log.Logger = new LoggerConfiguration()
        .MinimumLevel.Is(level)
        .Enrich.WithProperty("Server", serverName)
        .WriteTo.Console()
        .WriteTo.File(
            Path.Combine(logging.LogDirectory, "healer-.log"),
            rollingInterval: RollingInterval.Day,
            fileSizeLimitBytes: logging.FileSizeLimitMb * 1024L * 1024,
            rollOnFileSizeLimit: true,
            retainedFileCountLimit: logging.RetainedFileCount)
        .CreateLogger();
}
