using Healer.Core.Models;

namespace Healer.Status.Logic;

/// <summary>Formats one action/incident history row for display in the ListView browser.</summary>
public static class ActionHistoryFormatter
{
    public static string FormatRow(ActionHistoryRecord record)
    {
        var flag = record.DryRun ? "DRY-RUN " : "";
        return $"{record.TimestampUtc:yyyy-MM-dd HH:mm} UTC | {record.ActionType,-28} | {record.Target,-16} | {flag}{record.Outcome}";
    }

    public static string FormatLiveSummary(HostMetrics host, int containerCount, int unhealthyCount)
    {
        return $"Mem {host.MemUsedPercent:F0}%  Swap {host.SwapUsedPercent:F0}%  Load {host.LoadAvg1:F1}  |  " +
               $"{containerCount} container(s), {unhealthyCount} unhealthy";
    }
}
