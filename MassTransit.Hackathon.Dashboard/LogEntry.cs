namespace MassTransit.Hackathon.Dashboard;

/// <summary>
/// A single structured log entry with pre-parsed fields for tabular display.
/// </summary>
internal readonly record struct LogEntry(
    string     Label,
    WorkerRole Role,
    string     Time,
    string     Message);
