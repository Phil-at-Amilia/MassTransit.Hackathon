using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace MassTransit.Hackathon.Dashboard;

/// <summary>
/// Spawns, monitors, and terminates worker OS processes.
/// </summary>
internal sealed class ProcessManager
{
    private readonly string _workerExe;
    private readonly string _workerArgPrefix;  // "" for .exe, "\"path.dll\" " for dotnet
    private readonly List<ManagedProcess> _processes = new();
    private readonly object _lock = new();
    private int _nextId = 0;

    // Strips the .NET log-level prefix and long category name from SimpleConsole output.
    // e.g. "info: MassTransit.Hackathon.Workers.WaiterPublisherWorker[0] " → ""
    private static readonly Regex LogCategory =
        new(@"\s*(info|warn|fail|crit|dbug|trce): \S+\[\d+\] ",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Extracts the leading HH:mm:ss timestamp injected by SimpleConsole.
    private static readonly Regex LeadingTime =
        new(@"^(\d{2}:\d{2}:\d{2})\s+", RegexOptions.Compiled);

    // Collapses verbose ISO-8601 datetimes to just the time component.
    // e.g. "2026-04-08T17:57:00.9481375Z" → "17:57:00"
    private static readonly Regex IsoDateTime =
        new(@"\d{4}-\d{2}-\d{2}T(\d{2}:\d{2}:\d{2})\.\d+Z?", RegexOptions.Compiled);

    /// <summary>Global rolling log (last 500 lines) merged from all child processes.</summary>
    public ConcurrentQueue<LogEntry> GlobalLog { get; } = new();

    /// <summary>Count of each item type seen in publisher log lines.</summary>
    public MessageStats Published { get; } = new();

    /// <summary>
    /// Count of each item type acknowledged by each consumer role.
    /// Each role has its own queue in RabbitMQ, so counts are independent.
    /// </summary>
    public ConcurrentDictionary<WorkerRole, MessageStats> ConsumedByRole { get; } = new();

    public ProcessManager(string workerExe, string argPrefix = "")
    {
        _workerExe      = workerExe;
        _workerArgPrefix = argPrefix;
    }

    /// <summary>Returns a point-in-time snapshot of all tracked processes.</summary>
    public IReadOnlyList<ManagedProcess> Snapshot()
    {
        lock (_lock) return _processes.ToList();
    }

    public ManagedProcess SpawnConsumer(WorkerRole role, int slowMs)
    {
        var type = role switch
        {
            WorkerRole.LineCook  => "linecook",
            WorkerRole.Bartender => "bartender",
            WorkerRole.Manager   => "manager",
            _ => throw new ArgumentOutOfRangeException(nameof(role))
        };

        var id    = Interlocked.Increment(ref _nextId);
        var label = $"{char.ToUpper(type[0])}{type[1..]}-{id}";
        var args  = $"--mode consumer --type {type} --label {label}"
                  + (slowMs > 0 ? $" --slow {slowMs}" : "");

        return Spawn(id, role, label, args, slowMs, 0);
    }

    public ManagedProcess SpawnPublisher(int rateSeconds)
    {
        var id    = Interlocked.Increment(ref _nextId);
        var label = $"Pub-{id}";
        var args  = $"--mode publisher --label {label} --rate {rateSeconds}";
        return Spawn(id, WorkerRole.Publisher, label, args, 0, rateSeconds);
    }

    public ManagedProcess SpawnGroupOrder(string item, int count)
    {
        var id    = Interlocked.Increment(ref _nextId);
        var label = $"GroupOrder-{id}";
        var args  = $"--mode grouporder --label {label} --item {item} --count {count}";
        return Spawn(id, WorkerRole.GroupOrder, label, args, 0, 0);
    }

    private ManagedProcess Spawn(int id, WorkerRole role, string label, string args, int slowMs, int rateSeconds)
    {
        var psi = new ProcessStartInfo(_workerExe, _workerArgPrefix + args)
        {
            UseShellExecute         = false,
            RedirectStandardOutput  = true,
            RedirectStandardError   = true,
            RedirectStandardInput   = true,   // stdin close = graceful shutdown signal
            CreateNoWindow          = true,
            StandardOutputEncoding  = System.Text.Encoding.UTF8,
            StandardErrorEncoding   = System.Text.Encoding.UTF8,
        };

        var proc    = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var managed = new ManagedProcess
        {
            Id            = id,
            Role          = role,
            Label         = label,
            SlowMs        = slowMs,
            RateSeconds   = rateSeconds,
            StartedAt     = DateTime.Now,
            SystemProcess = proc,
        };

        proc.OutputDataReceived += (_, e) => AppendLog(managed, e.Data, isError: false);
        proc.ErrorDataReceived  += (_, e) => AppendLog(managed, e.Data, isError: true);

        proc.Exited += (_, _) =>
        {
            var code = proc.HasExited ? proc.ExitCode : -1;
            AppendEvent(label, role, $"── EXITED (code {code}) ──");
            _ = Task.Run(async () =>
            {
                await Task.Delay(5_000);
                lock (_lock) _processes.Remove(managed);
            });
        };

        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        lock (_lock) _processes.Add(managed);
        AppendEvent(label, role, $"── STARTED (PID {proc.Id}) ──");

        return managed;
    }

    /// <summary>
    /// Graceful stop: closes the child's stdin pipe.
    /// The worker's <c>StdinShutdownMonitor</c> detects this and calls
    /// <c>IHostApplicationLifetime.StopApplication()</c>, allowing in-flight
    /// messages to complete before the process exits.
    /// Falls back to a hard kill after 5 seconds.
    /// </summary>
    public void GracefulStop(int id)
    {
        var target = Find(id);
        if (target is null || !target.IsRunning) return;

        try
        {
            target.SystemProcess.StandardInput.Close();
            AppendEvent(target.Label, target.Role, "── GRACEFUL STOP requested ──");
        }
        catch { /* process may have already exited */ }

        _ = Task.Run(async () =>
        {
            await Task.Delay(5_000);
            if (target.IsRunning)
            {
                AppendEvent(target.Label, target.Role, "── Graceful timeout, force-killing ──");
                ForceKill(target);
            }
        });
    }

    /// <summary>
    /// Crash kill: terminates the process immediately.
    /// Any in-flight (unacknowledged) messages will be requeued by RabbitMQ
    /// after the consumer timeout elapses — this is the key crash-resilience test.
    /// </summary>
    public void CrashKill(int id)
    {
        var target = Find(id);
        if (target is null || !target.IsRunning) return;
        AppendEvent(target.Label, target.Role, "── CRASH KILL ──");
        ForceKill(target);
    }

    /// <summary>
    /// Toggles pause/resume on a consumer process by writing the corresponding
    /// command line to its stdin pipe.  The worker's <c>StdinShutdownMonitor</c>
    /// reads these lines and calls <c>PauseSignal.Pause()</c> / <c>Resume()</c>,
    /// which blocks/unblocks each consumer's <c>Consume()</c> before it starts
    /// processing the next message.
    /// </summary>
    public void PauseToggle(int id)
    {
        var target = Find(id);
        if (target is null || !target.IsRunning) return;

        var command = target.IsPaused ? "resume" : "pause";
        target.IsPaused = !target.IsPaused;

        try
        {
            target.SystemProcess.StandardInput.WriteLine(command);
            target.SystemProcess.StandardInput.Flush();
            AppendEvent(target.Label, target.Role,
                target.IsPaused ? "── PAUSED ──" : "── RESUMED ──");
        }
        catch { /* process may have already exited */ }
    }

    /// <summary>Hard-kills every tracked process (called on dashboard exit).</summary>
    public void KillAll()
    {
        List<ManagedProcess> snapshot;
        lock (_lock) snapshot = _processes.ToList();

        foreach (var p in snapshot)
        {
            try { if (p.IsRunning) ForceKill(p); }
            catch { /* ignore */ }
        }
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private ManagedProcess? Find(int id)
    {
        lock (_lock) return _processes.FirstOrDefault(p => p.Id == id);
    }

    private static void ForceKill(ManagedProcess mp)
    {
        try { mp.SystemProcess.Kill(entireProcessTree: true); }
        catch { /* already gone */ }
    }

    private void AppendLog(ManagedProcess mp, string? data, bool isError)
    {
        if (data is null) return;

        // Strip .NET log-level prefix + long category name
        var cleaned = LogCategory.Replace(data, " ").Trim();

        // Extract leading HH:mm:ss timestamp (produced by SimpleConsole format)
        var timeMatch = LeadingTime.Match(cleaned);
        var time = timeMatch.Success
            ? timeMatch.Groups[1].Value
            : DateTime.Now.ToString("HH:mm:ss");
        if (timeMatch.Success)
            cleaned = cleaned[timeMatch.Length..].Trim();

        // Strip leading [Label] — stored separately, no need to repeat in message
        cleaned = Regex.Replace(cleaned, $@"^\[{Regex.Escape(mp.Label)}\]\s*", "");

        // Collapse verbose ISO datetimes → HH:mm:ss
        cleaned = IsoDateTime.Replace(cleaned, "$1");

        if (isError) cleaned = $"[ERR] {cleaned}";

        // Track message type stats for the stats panel
        if (cleaned.Contains("Placed order:", StringComparison.OrdinalIgnoreCase))
            Published.Record(cleaned);
        else if (cleaned.Contains("Cooked ",    StringComparison.OrdinalIgnoreCase) ||
                 cleaned.Contains("Poured ",    StringComparison.OrdinalIgnoreCase) ||
                 cleaned.Contains("Saw order",  StringComparison.OrdinalIgnoreCase))
        {
            ConsumedByRole.GetOrAdd(mp.Role, _ => new MessageStats()).Record(cleaned);
            mp.Stats.Record(cleaned);
        }

        var entry = new LogEntry(mp.Label, mp.Role, time, cleaned);
        Enqueue(mp.LogBuffer, entry.Message, max: 100);
        EnqueueEntry(GlobalLog, entry);
    }

    private void AppendEvent(string label, WorkerRole role, string message)
    {
        var entry = new LogEntry(label, role, DateTime.Now.ToString("HH:mm:ss"), message);
        EnqueueEntry(GlobalLog, entry);
    }

    private static void EnqueueEntry(ConcurrentQueue<LogEntry> queue, LogEntry entry, int max = 500)
    {
        queue.Enqueue(entry);
        while (queue.Count > max)
            queue.TryDequeue(out _);
    }

    private static void Enqueue(ConcurrentQueue<string> queue, string line, int max = 100)
    {
        queue.Enqueue(line);
        while (queue.Count > max)
            queue.TryDequeue(out _);
    }
}
