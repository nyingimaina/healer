namespace Healer.Core.Configuration;

/// <summary>
/// One docker-compose project Healer periodically refreshes by restarting ALL of its services
/// together via `docker compose restart` — letting compose itself handle dependency-aware
/// shutdown/startup ordering, the same way a person would refresh a stack by hand, rather than
/// Healer restarting each container individually through the Docker API. Scheduling reuses
/// <see cref="RebootSchedule"/>'s interval/anchor/hour/timezone shape verbatim (see its own doc
/// comment for why that's interval-based rather than hardcoded to "weekly") — the wizard offers
/// the same Daily/Weekly/Every 2 weeks/Monthly presets for this as it does for host reboots.
///
/// Positional record with constructor defaults — see the comment at the top of HealerConfig.cs for
/// why (System.Text.Json's source-generated deserializer silently drops `{ get; init; } = value`
/// initializers for properties missing from JSON; positional constructor defaults don't have that bug).
/// </summary>
public sealed record ComposeProjectSchedule(
    string ProjectName = "",
    string WorkingDirectory = "",
    RebootSchedule? Schedule = null)
{
    public required string ProjectName { get; init; } = ProjectName;
    public required string WorkingDirectory { get; init; } = WorkingDirectory;
    public required RebootSchedule Schedule { get; init; } = Schedule ?? RebootSchedule.Disabled();
}

public sealed record ScheduledComposeRestartsConfig(IReadOnlyList<ComposeProjectSchedule>? Projects = null)
{
    public IReadOnlyList<ComposeProjectSchedule> Projects { get; init; } = Projects ?? [];
}
