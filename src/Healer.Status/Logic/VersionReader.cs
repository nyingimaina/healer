namespace Healer.Status.Logic;

/// <summary>Reads back the VERSION file deploy/build-payload.sh writes next to the binaries — the
/// only way to tell which build is actually running on a box, since there's no auto-update
/// mechanism and a stale box otherwise looks identical to an upgraded one.</summary>
public static class VersionReader
{
    public static string Read(string path) =>
        File.Exists(path) ? File.ReadAllText(path).Trim() : "unknown";
}
