using System.Collections.Concurrent;
using System.Diagnostics;

namespace MassTransit.Hackathon.Dashboard;

internal enum WorkerRole
{
    LineCook,
    Bartender,
    Manager,
    Publisher
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

    /// <summary>Rolling log buffer for this process (last 100 lines).</summary>
    public ConcurrentQueue<string> LogBuffer { get; } = new();
}
