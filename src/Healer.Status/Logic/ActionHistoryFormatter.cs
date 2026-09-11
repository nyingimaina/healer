using Healer.Core.Models;

namespace Healer.Status.Logic;

/// <summary>Formats one action/incident history row for display in the ListView browser.</summary>
public static class ActionHistoryFormatter
{
    /// <summary>Max length of the Reason cell in the compact single-line ListView row — Terminal.Gui's
    /// ListView has no per-item word-wrap, so a long reason here would either break column alignment
    /// or force horizontal scrolling. The full, untruncated reason (plus Detail) is always available
    /// via <see cref="FormatReasonDetail"/> in the word-wrapped detail pane for the selected row.</summary>
    private const int ReasonColumnMaxLength = 40;

    public static string FormatRow(ActionHistoryRecord record)
    {
        var flag = record.DryRun ? "DRY-RUN " : "";
        var reason = TruncateForDisplay(record.TriggerReason, ReasonColumnMaxLength);
        return $"{record.TimestampUtc:yyyy-MM-dd HH:mm} UTC | {record.ActionType,-28} | {record.Target,-16} | {flag}{record.Outcome} | {reason}";
    }

    /// <summary>Shortens text to fit a fixed-width table cell, replacing the tail with a single ellipsis
    /// character (not "...") so the truncation itself never eats more than one extra character of budget.</summary>
    public static string TruncateForDisplay(string text, int maxLength)
    {
        if (text.Length <= maxLength)
        {
            return text;
        }

        return text[..(maxLength - 1)] + "…";
    }

    /// <summary>The full, untruncated reason (and Detail, if present) for one history row — meant for a
    /// word-wrapped detail pane showing whichever row is currently selected in the ListView, so
    /// truncating the row itself never actually loses information the user might need.</summary>
    public static string FormatReasonDetail(ActionHistoryRecord record) =>
        record.Detail is null ? record.TriggerReason : $"{record.TriggerReason}\n{record.Detail}";

    public static string FormatLiveSummary(HostMetrics host, int containerCount, int unhealthyCount)
    {
        return $"Mem {host.MemUsedPercent:F0}%  Swap {host.SwapUsedPercent:F0}%  Load {host.LoadAvg1:F1}  |  " +
               $"{containerCount} container(s), {unhealthyCount} unhealthy";
    }
}
