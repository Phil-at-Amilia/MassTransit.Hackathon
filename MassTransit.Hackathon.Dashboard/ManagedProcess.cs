using System.Collections.Concurrent;
using System.Diagnostics;

namespace MassTransit.Hackathon.Dashboard;

internal enum WorkerRole
{
    LineCook,
    Bartender,
    Manager,
    Publisher,
    GroupOrder,
}

internal sealed class ManagedProcess
{
    public int Id { get; init; }
    public WorkerRole Role { get; init; }
    public string Label { get; init; } = "";
    public int SlowMs { get; init; }
    public int RateSeconds { get; init; }
    public DateTime StartedAt { get; init; }
    public Process SystemProcess { get; init; } = null!;

    public bool IsRunning => !SystemProcess.HasExited;

    /// <summary>True while a "pause" command has been sent and "resume" has not yet been sent.</summary>
    public bool IsPaused { get; set; }

    /// <summary>Rolling log buffer for this process (last 100 lines).</summary>
    public ConcurrentQueue<string> LogBuffer { get; } = new();

    /// <summary>Per-instance message stats (acked counts for this process only).</summary>
    public MessageStats Stats { get; } = new();
}
