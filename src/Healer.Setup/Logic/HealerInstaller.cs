using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Healer.Core.Configuration;

namespace Healer.Setup.Logic;

/// <summary>
/// The wizard's "Apply" step: writes config + secrets, installs the systemd unit, and starts the
/// service. This IS the installer — there's no separate install script to run before or after it.
/// Uses plain reflection-based JsonSerializer (this tool isn't Native AOT, unlike Healer.Host, so
/// that's fine) with the same camelCase + string-enum shape Healer.Host's source-gen context uses,
/// so the two produce/consume an identical file format.
/// </summary>
public static class HealerInstaller
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    public const string DefaultConfigPath = "/etc/healer/healer.json";
    public const string DefaultEnvPath = "/etc/healer/healer.env";
    public const string SystemdUnitDestination = "/etc/systemd/system/healer.service";

    public static async Task WriteConfigAsync(HealerConfig config, string path, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, config, JsonOptions, ct);
    }

    public static async Task WriteEnvFileAsync(TelegramConfig telegram, string botToken, string chatId, string path, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var content = $"{telegram.BotTokenEnvVar}={botToken}\n{telegram.ChatIdEnvVar}={chatId}\n";
        await File.WriteAllTextAsync(path, content, ct);

        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    public static async Task InstallAndStartServiceAsync(string unitSourcePath, CancellationToken ct)
    {
        File.Copy(unitSourcePath, SystemdUnitDestination, overwrite: true);
        await RunAsync("systemctl", "daemon-reload", ct);
        await RunAsync("systemctl", "enable --now healer", ct);
    }

    public static async Task<bool> IsServiceActiveAsync(CancellationToken ct)
    {
        try
        {
            var (exitCode, _) = await RunCapturedAsync("systemctl", "is-active healer", ct);
            return exitCode == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static async Task RunAsync(string fileName, string arguments, CancellationToken ct)
    {
        var (exitCode, stderr) = await RunCapturedAsync(fileName, arguments, ct);
        if (exitCode != 0)
        {
            throw new InvalidOperationException($"{fileName} {arguments} exited with code {exitCode}: {stderr}");
        }
    }

    private static async Task<(int ExitCode, string StdErr)> RunCapturedAsync(string fileName, string arguments, CancellationToken ct)
    {
        using var process = Process.Start(new ProcessStartInfo(fileName, arguments)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        }) ?? throw new InvalidOperationException($"Failed to start process: {fileName} {arguments}");

        var stderr = await process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        return (process.ExitCode, stderr);
    }
}
