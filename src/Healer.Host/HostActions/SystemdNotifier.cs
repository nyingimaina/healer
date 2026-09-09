using System.Net.Sockets;
using System.Text;

namespace Healer.Host.HostActions;

/// <summary>
/// Minimal sd_notify client: sends datagrams to the socket named by $NOTIFY_SOCKET — the same
/// mechanism systemd's own sd_notify() C function uses. No libsystemd dependency (which would be an
/// unproven Native AOT/trimming risk) — this is a few lines of raw Unix datagram socket I/O.
///
/// This is what closes the "critical the daemon is always running" gap that Restart=always alone
/// does NOT close: Restart=always only catches a process that *exits*. A process that hangs (stuck,
/// but still technically running) is invisible to it. With WatchdogSec set in the unit and this class
/// pinging WATCHDOG=1 every tick, systemd kills and restarts Healer if it ever stops responding —
/// not just if it crashes. No-ops safely when NOTIFY_SOCKET isn't set (e.g. running outside systemd).
/// </summary>
public sealed class SystemdNotifier : IDisposable
{
    private readonly Socket? _socket;
    private readonly UnixDomainSocketEndPoint? _endpoint;

    public SystemdNotifier()
    {
        var socketPath = Environment.GetEnvironmentVariable("NOTIFY_SOCKET");
        if (string.IsNullOrEmpty(socketPath))
        {
            return; // not running under systemd — every Notify* call below becomes a safe no-op
        }

        // systemd's abstract-namespace convention: a leading '@' means the abstract socket namespace,
        // represented to .NET as a path starting with a NUL byte.
        if (socketPath[0] == '@')
        {
            socketPath = "\0" + socketPath[1..];
        }

        _endpoint = new UnixDomainSocketEndPoint(socketPath);
        _socket = new Socket(AddressFamily.Unix, SocketType.Dgram, ProtocolType.Unspecified);
    }

    /// <summary>Call once, after startup has finished initializing — tells systemd the service is up (relevant when Type=notify).</summary>
    public void NotifyReady() => Send("READY=1");

    /// <summary>Call at least once per WatchdogSec/2 (e.g. every tick) to prove the process is still alive and not hung.</summary>
    public void NotifyWatchdog() => Send("WATCHDOG=1");

    public void NotifyStatus(string status) => Send($"STATUS={status}");

    private void Send(string message)
    {
        if (_socket is null || _endpoint is null)
        {
            return;
        }

        try
        {
            _socket.SendTo(Encoding.UTF8.GetBytes(message), _endpoint);
        }
        catch (SocketException)
        {
            // Best-effort — a failed watchdog ping must never crash the very daemon it's meant to keep alive.
        }
    }

    public void Dispose() => _socket?.Dispose();
}
