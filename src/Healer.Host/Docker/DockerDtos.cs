using System.Text.Json.Serialization;

namespace Healer.Host.Docker;

// Minimal subsets of the Docker Engine API's JSON shapes — only the fields Healer actually reads.
// Deserialized via source-generated contexts (HealerJsonContext) for Native AOT/trim safety.

public sealed class DockerContainerListItem
{
    [JsonPropertyName("Id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("Names")]
    public List<string> Names { get; set; } = [];

    /// <summary>
    /// Populated by Docker for every container; a container started by `docker compose up` carries
    /// `com.docker.compose.project` and `com.docker.compose.project.working_dir` among these — how
    /// Healer.Setup auto-discovers running compose projects without asking anyone to type a path.
    /// </summary>
    [JsonPropertyName("Labels")]
    public Dictionary<string, string> Labels { get; set; } = [];
}

public sealed class DockerContainerInspect
{
    [JsonPropertyName("Name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("RestartCount")]
    public int RestartCount { get; set; }

    [JsonPropertyName("State")]
    public DockerContainerState? State { get; set; }

    [JsonPropertyName("HostConfig")]
    public DockerHostConfig? HostConfig { get; set; }
}

public sealed class DockerContainerState
{
    [JsonPropertyName("Running")]
    public bool Running { get; set; }

    [JsonPropertyName("Health")]
    public DockerHealth? Health { get; set; }
}

public sealed class DockerHealth
{
    /// <summary>"starting", "healthy", or "unhealthy". Absent entirely when the image defines no HEALTHCHECK.</summary>
    [JsonPropertyName("Status")]
    public string? Status { get; set; }
}

public sealed class DockerHostConfig
{
    /// <summary>Memory limit in bytes; 0 means "no limit configured".</summary>
    [JsonPropertyName("Memory")]
    public long Memory { get; set; }
}

public sealed class DockerStatsResponse
{
    [JsonPropertyName("memory_stats")]
    public DockerMemoryStats? MemoryStats { get; set; }

    [JsonPropertyName("cpu_stats")]
    public DockerCpuStats? CpuStats { get; set; }

    [JsonPropertyName("precpu_stats")]
    public DockerCpuStats? PreCpuStats { get; set; }
}

public sealed class DockerMemoryStats
{
    [JsonPropertyName("usage")]
    public long Usage { get; set; }
}

public sealed class DockerCpuStats
{
    [JsonPropertyName("cpu_usage")]
    public DockerCpuUsage? CpuUsage { get; set; }

    [JsonPropertyName("system_cpu_usage")]
    public long SystemCpuUsage { get; set; }

    [JsonPropertyName("online_cpus")]
    public int OnlineCpus { get; set; }
}

public sealed class DockerCpuUsage
{
    [JsonPropertyName("total_usage")]
    public long TotalUsage { get; set; }
}
