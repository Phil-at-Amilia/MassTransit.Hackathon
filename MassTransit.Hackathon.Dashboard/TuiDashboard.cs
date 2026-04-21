using Spectre.Console;
using Spectre.Console.Rendering;

namespace MassTransit.Hackathon.Dashboard;

internal enum CommandType
{
    SpawnConsumer,
    SpawnPublisher,
    SpawnGroupOrder,
    GracefulStop,
    CrashKill,
    PauseToggle,
    Quit,
}

internal sealed class PendingCommand
{
    public CommandType Type  { get; }
    public bool        Slow  { get; }   // true when user pressed S/s (slow consumer)

    public PendingCommand(CommandType type, bool slow = false)
    {
        Type = type;
        Slow = slow;
    }
}

/// <summary>
/// Spectre.Console TUI dashboard.
/// Layout:
///   ┌─ Processes ──────────────────────────────────┐
///   ├─ RabbitMQ Queues ───────────────────────────┤
///   ├─ Logs (last 20) ────────────────────────────┤
///   └─ Key legend ────────────────────────────────┘
///
/// Render loop runs on the calling thread via AnsiConsole.Live.
/// Keyboard input runs on a background thread and signals commands.
/// When a command requires interactive prompts, the Live session is
/// paused, prompts are handled, then Live resumes.
/// </summary>
internal sealed class TuiDashboard
{
    private readonly ProcessManager  _pm;
    private readonly RabbitMqMonitor _rmq;

    private readonly CancellationTokenSource _sessionCts = new();
    private readonly Queue<LogEntry>          _logLines   = new();

    // Written by keyboard thread, read by render loop (volatile for visibility)
    private volatile PendingCommand? _pendingCommand;

    // Set to true while Spectre prompts are active so the keyboard thread
    // stops calling Console.ReadKey — otherwise it steals keystrokes from prompts.
    private volatile bool _promptActive;

    public TuiDashboard(ProcessManager pm, RabbitMqMonitor rmq)
    {
        _pm  = pm;
        _rmq = rmq;
    }

    public async Task RunAsync()
    {
        // Enter alternate screen buffer — like vim/htop.
        // The TUI occupies the full terminal; pressing Q restores the previous shell content.
        Console.Write("\x1b[?1049h");
        AnsiConsole.Clear();

        StartKeyboardThread();

        try
        {
            while (!_sessionCts.Token.IsCancellationRequested)
            {
                // Clear and reset cursor to top-left before each Live session.
                // This ensures the TUI always redraws from the top after a prompt
                // ran and left content on screen.
                AnsiConsole.Clear();

                await AnsiConsole.Live(BuildLayout())
                    .AutoClear(false)
                    .Overflow(VerticalOverflow.Ellipsis)
                    .StartAsync(async ctx =>
                    {
                        while (!_sessionCts.Token.IsCancellationRequested)
                        {
while (_pm.GlobalLog.TryDequeue(out var entry))
                        {
                            _logLines.Enqueue(entry);
                                if (_logLines.Count > 20)
                                    _logLines.Dequeue();
                            }

                            ctx.UpdateTarget(BuildLayout());
                            ctx.Refresh();

                            if (_pendingCommand is not null)
                                return;

                            var deadline = DateTime.UtcNow.AddMilliseconds(500);
                            while (DateTime.UtcNow < deadline
                                && _pendingCommand is null
                                && !_sessionCts.Token.IsCancellationRequested)
                            {
                                await Task.Delay(50).ConfigureAwait(false);
                            }
                        }
                    });

                var cmd = Interlocked.Exchange(ref _pendingCommand, null);
                if (cmd is not null && !_sessionCts.Token.IsCancellationRequested)
                    await HandleCommandAsync(cmd);
            }
        }
        finally
        {
            // Return to normal screen — previous terminal content is restored.
            Console.Write("\x1b[?1049l");
        }
    }

    // ── keyboard thread ──────────────────────────────────────────────────────

    private void StartKeyboardThread()
    {
        var thread = new Thread(() =>
        {
            while (!_sessionCts.Token.IsCancellationRequested)
            {
                // Yield while a prompt owns the console
                if (_promptActive)
                {
                    Thread.Sleep(50);
                    continue;
                }

                // Block only when a key is actually available, so we can
                // re-check _promptActive without burning CPU.
                if (!Console.KeyAvailable)
                {
                    Thread.Sleep(30);
                    continue;
                }

                ConsoleKeyInfo key;
                try   { key = Console.ReadKey(intercept: true); }
                catch { break; }

                PendingCommand? cmd = key.KeyChar switch
                {
                    'C' or 'c' => new PendingCommand(CommandType.SpawnConsumer, slow: false),
                    'S' or 's' => new PendingCommand(CommandType.SpawnConsumer, slow: true),
                    'P' or 'p' => new PendingCommand(CommandType.SpawnPublisher),
                    'G' or 'g' => new PendingCommand(CommandType.SpawnGroupOrder),
                    'K' or 'k' => new PendingCommand(CommandType.GracefulStop),
                    'X' or 'x' => new PendingCommand(CommandType.CrashKill),
                    'Z' or 'z' => new PendingCommand(CommandType.PauseToggle),
                    'Q' or 'q' => new PendingCommand(CommandType.Quit),
                    _ => null,
                };

                if (cmd is not null)
                    _pendingCommand = cmd;
            }
        })
        { IsBackground = true, Name = "KeyboardThread" };

        thread.Start();
    }

    // ── command handling (on main thread, outside Live) ──────────────────────

    private async Task HandleCommandAsync(PendingCommand cmd)
    {
        _promptActive = true;
        try
        {
            await HandleCommandInnerAsync(cmd);
        }
        finally
        {
            _promptActive = false;
        }
    }

    private async Task HandleCommandInnerAsync(PendingCommand cmd)
    {
        switch (cmd.Type)
        {
            case CommandType.Quit:
                _sessionCts.Cancel();
                return;

            case CommandType.SpawnConsumer:
            {
                var type = AnsiConsole.Prompt(
                    new SelectionPrompt<string>()
                        .Title("[bold]Select consumer type:[/]")
                        .AddChoices("linecook", "bartender", "manager"));

                var role = type switch
                {
                    "linecook"  => WorkerRole.LineCook,
                    "bartender" => WorkerRole.Bartender,
                    _           => WorkerRole.Manager,
                };

                int slowMs = 0;
                if (cmd.Slow)
                {
                    slowMs = AnsiConsole.Prompt(
                        new TextPrompt<int>("[bold]Processing delay (ms)?[/]")
                            .DefaultValue(2000)
                            .ValidationErrorMessage("Must be a positive number"));
                }

                _pm.SpawnConsumer(role, slowMs);
                break;
            }

            case CommandType.SpawnPublisher:
            {
                var rate = AnsiConsole.Prompt(
                    new TextPrompt<int>("[bold]Publish interval (seconds)?[/]")
                        .DefaultValue(2)
                        .ValidationErrorMessage("Must be a positive number"));

                _pm.SpawnPublisher(rate);
                break;
            }

            case CommandType.SpawnGroupOrder:
            {
                var item = AnsiConsole.Prompt(
                    new SelectionPrompt<string>()
                        .Title("[bold]Select item:[/]")
                        .AddChoices("burger", "fries", "soda"));

                var count = AnsiConsole.Prompt(
                    new TextPrompt<int>("[bold]Count?[/]")
                        .DefaultValue(5)
                        .Validate(n => n > 0
                            ? ValidationResult.Success()
                            : ValidationResult.Error("Must be a positive number")));

                _pm.SpawnGroupOrder(item, count);
                break;
            }

            case CommandType.GracefulStop:
            {
                // Only consumers and publishers support graceful stop via stdin close.
                // GroupOrder workers exit on their own after publishing. The dashboard
                // itself is never in this list.
                var stoppable = _pm.Snapshot()
                    .Where(p => p.IsRunning && p.Role != WorkerRole.GroupOrder)
                    .ToList();

                if (stoppable.Count == 0)
                {
                    AnsiConsole.MarkupLine("[yellow]No running consumers or publishers to stop.[/]");
                    await Task.Delay(1200);
                    break;
                }

                // Build a dictionary keyed by display label so we don't need to
                // parse the selected string (and avoid Spectre markup bracket conflicts).
                var choiceMap = stoppable.ToDictionary(
                    p => $"#{p.Id}  {p.Label}  ({p.Role})"
                       + (p.SlowMs > 0 ? $"  slow={p.SlowMs}ms" : ""),
                    p => p);

                var choice = AnsiConsole.Prompt(
                    new SelectionPrompt<string>()
                        .Title("[bold]Graceful stop — select process:[/]")
                        .AddChoices(choiceMap.Keys));

                _pm.GracefulStop(choiceMap[choice].Id);
                break;
            }

            case CommandType.CrashKill:
            {
                var running = _pm.Snapshot().Where(p => p.IsRunning).ToList();
                if (running.Count == 0)
                {
                    AnsiConsole.MarkupLine("[yellow]No running processes to kill.[/]");
                    await Task.Delay(1200);
                    break;
                }

                var choiceMap = running.ToDictionary(
                    p => $"#{p.Id}  {p.Label}  ({p.Role})"
                       + (p.SlowMs > 0 ? $"  slow={p.SlowMs}ms" : ""),
                    p => p);

                var choice = AnsiConsole.Prompt(
                    new SelectionPrompt<string>()
                        .Title("[bold red]CRASH KILL — select process:[/]")
                        .AddChoices(choiceMap.Keys));

                _pm.CrashKill(choiceMap[choice].Id);
                break;
            }

            case CommandType.PauseToggle:
            {
                // Only consumers can be paused — publishers have no Consume() logic.
                var consumers = _pm.Snapshot()
                    .Where(p => p.IsRunning
                             && p.Role is WorkerRole.LineCook or WorkerRole.Bartender or WorkerRole.Manager)
                    .ToList();

                if (consumers.Count == 0)
                {
                    AnsiConsole.MarkupLine("[yellow]No running consumers to pause/resume.[/]");
                    await Task.Delay(1200);
                    break;
                }

                var choiceMap = consumers.ToDictionary(
                    p => $"#{p.Id}  {p.Label}  ({p.Role})  [{(p.IsPaused ? "PAUSED" : "Running")}]"
                       + (p.SlowMs > 0 ? $"  slow={p.SlowMs}ms" : ""),
                    p => p);

                var choice = AnsiConsole.Prompt(
                    new SelectionPrompt<string>()
                        .Title("[bold]Pause/Resume — select consumer:[/]")
                        .AddChoices(choiceMap.Keys));

                _pm.PauseToggle(choiceMap[choice].Id);
                break;
            }
        }
    }

    // ── rendering ────────────────────────────────────────────────────────────

    private IRenderable BuildLayout()
    {
        var rows = new List<IRenderable>
        {
            BuildProcessTable(),
            BuildQueueTable(),
            BuildStatsPanel(),
            BuildLogPanel(),
            new Markup(
                "\n[grey][[C]][/] Consumer  [grey][[S]][/] SlowConsumer  [grey][[P]][/] Publisher  [grey][[G]][/] GroupOrder" +
                "    [grey][[K]][/] Graceful stop  [grey][[X]][/] Crash kill  [grey][[Z]][/] Pause/Resume  [grey][[Q]][/] Quit\n"),
        };

        return new Rows(rows);
    }

    private IRenderable BuildProcessTable()
    {
        var table = new Table()
            .Title("[bold]Worker Processes[/]")
            .BorderColor(Color.Grey23)
            .AddColumn(new TableColumn("[grey]#[/]").RightAligned())
            .AddColumn("[grey]Label[/]")
            .AddColumn("[grey]Role[/]")
            .AddColumn(new TableColumn("[grey]Slow[/]").RightAligned())
            .AddColumn(new TableColumn("[grey]Rate[/]").RightAligned())
            .AddColumn("[grey]Status[/]")
            .AddColumn(new TableColumn("[grey]Uptime[/]").RightAligned())
            .Expand();

        var processes = _pm.Snapshot();

        if (processes.Count == 0)
        {
            table.AddRow(new Markup("[grey italic](no processes — press C, S, P, or G to spawn one)[/]"));
            return table;
        }

        foreach (var p in processes)
        {
            var uptime  = DateTime.Now - p.StartedAt;
            var running = p.IsRunning;
            var status  = running
                ? (p.IsPaused ? "[yellow]\u23f8 Paused[/]" : "[green]\u25cf Running[/]")
                : "[red]\u2717 Exited[/]";
            var slow    = p.SlowMs   > 0 ? $"[yellow]{p.SlowMs}ms[/]" : "[grey]-[/]";
            var rate    = p.Role == WorkerRole.Publisher ? $"{p.RateSeconds}s" : "[grey]-[/]";
            var uptimeS = running
                ? $"{(int)uptime.TotalMinutes:D2}:{uptime.Seconds:D2}"
                : "[grey dim](exiting...)[/]";

            table.AddRow(
                p.Id.ToString(),
                Markup.Escape(p.Label),
                $"[{RoleColor(p.Role)}]{p.Role}[/]",
                slow,
                rate,
                status,
                uptimeS);
        }

        return table;
    }

    private IRenderable BuildQueueTable()
    {
        var connected = _rmq.IsConnected;
        var header    = connected ? "[bold]RabbitMQ Queues[/]" : "[bold]RabbitMQ Queues[/] [red](offline)[/]";

        var table = new Table()
            .Title(header)
            .BorderColor(connected ? Color.Grey23 : Color.Red)
            .AddColumn("[grey]Queue[/]")
            .AddColumn(new TableColumn("[grey]Ready[/]").RightAligned())
            .AddColumn(new TableColumn("[grey]Unacked[/]").RightAligned())
            .AddColumn(new TableColumn("[grey]Total[/]").RightAligned())
            .AddColumn(new TableColumn("[grey]Consumers[/]").RightAligned())
            .Expand();

        var queues = _rmq.Latest;

        if (queues.Length == 0)
        {
            table.AddRow(new Markup(connected
                ? "[grey italic](no queues yet)[/]"
                : "[red italic](waiting for RabbitMQ on localhost:15672)[/]"));
            return table;
        }

        foreach (var q in queues)
        {
            var readyMarkup = q.MessagesReady switch
            {
                > 50  => $"[red]{q.MessagesReady}[/]",
                > 10  => $"[yellow]{q.MessagesReady}[/]",
                > 0   => $"[white]{q.MessagesReady}[/]",
                _     => $"[grey]{q.MessagesReady}[/]",
            };
            var unackedMarkup = q.MessagesUnacknowledged > 0
                ? $"[yellow]{q.MessagesUnacknowledged}[/]"
                : $"[grey]{q.MessagesUnacknowledged}[/]";

            table.AddRow(
                Markup.Escape(q.Name),
                readyMarkup,
                unackedMarkup,
                q.TotalMessages.ToString(),
                q.Consumers.ToString());
        }

        return table;
    }

    private IRenderable BuildStatsPanel()
    {
        var pub = _pm.Published;

        var table = new Table()
            .Title("[bold]Message Flow[/]")
            .BorderColor(Color.Grey23)
            .AddColumn("[grey]Consumer[/]")
            .AddColumn(new TableColumn("[grey]Burger[/]").RightAligned())
            .AddColumn(new TableColumn("[grey]Fries[/]").RightAligned())
            .AddColumn(new TableColumn("[grey]Soda[/]").RightAligned())
            .AddColumn(new TableColumn("[grey]Total[/]").RightAligned())
            .AddColumn(new TableColumn("[grey]Queued[/]").RightAligned())
            .Expand();

        // Look up MessagesReady for a given role's queue from the RabbitMQ API data.
        string queuedMarkup(WorkerRole r)
        {
            var q = _rmq.Latest.FirstOrDefault(q => q.Name.Equals(r.ToString(), StringComparison.OrdinalIgnoreCase));
            if (q is null) return "[grey]-[/]";
            return q.MessagesReady switch
            {
                > 50 => $"[red]{q.MessagesReady}[/]",
                > 10 => $"[yellow]{q.MessagesReady}[/]",
                > 0  => $"[white]{q.MessagesReady}[/]",
                _    => $"[grey]{q.MessagesReady}[/]",
            };
        }

        table.AddRow(new Markup("[green]Published[/]"),
            new Markup(pub.Burger.ToString()), new Markup(pub.Fries.ToString()),
            new Markup(pub.Soda.ToString()), new Markup(pub.Total.ToString()),
            new Markup("[grey]-[/]"));

        table.AddEmptyRow();

        // One row per consumer role that has produced at least one ack
        var consumerRoles = new[] { WorkerRole.LineCook, WorkerRole.Bartender, WorkerRole.Manager };
        foreach (var role in consumerRoles)
        {
            if (!_pm.ConsumedByRole.TryGetValue(role, out var cons)) continue;

            var color = RoleColor(role);
            var label = role == WorkerRole.Manager
                ? $"[{color}]{role} [grey dim](observer)[/][/]"
                : $"[{color}]{role} (acked)[/]";

            // In-queue estimate = published − acked for this role's relevant items
            // Manager is excluded from in-queue since it's an independent audit queue
            string inQ(long p, long c)
            {
                if (role == WorkerRole.Manager) return "[grey dim]N/A[/]";
                var delta = p - c;
                var col   = delta > 50 ? "red" : delta > 10 ? "yellow" : "grey";
                return $"[{col}]{delta:+#;-#;0}[/]";
            }

            table.AddRow(
                new Markup(label),
                new Markup($"[grey]{cons.Burger}[/]  {inQ(pub.Burger, cons.Burger)}"),
                new Markup($"[grey]{cons.Fries}[/]  {inQ(pub.Fries, cons.Fries)}"),
                new Markup($"[grey]{cons.Soda}[/]  {inQ(pub.Soda, cons.Soda)}"),
                new Markup($"{cons.Total}"),
                new Markup(queuedMarkup(role)));
        }

        if (_pm.ConsumedByRole.IsEmpty)
            table.AddRow("[grey italic](no consumers yet)[/]", "", "", "", "", "");

        return table;
    }

    private IRenderable BuildLogPanel()
    {
        var entries = _logLines.ToArray();

        var table = new Table()
            .Title("[bold]Logs (last 20)[/]")
            .BorderColor(Color.Grey23)
            .AddColumn(new TableColumn("[grey]Source[/]").NoWrap())
            .AddColumn(new TableColumn("[grey]Time[/]").NoWrap())
            .AddColumn("[grey]Message[/]")
            .Expand();

        if (entries.Length == 0)
        {
            table.AddRow(new Markup(""), new Markup(""),
                new Markup("[grey italic](no log entries yet)[/]"));
            return table;
        }

        foreach (var e in entries)
        {
            var color = RoleColor(e.Role);
            table.AddRow(
                new Markup($"[{color} bold]{Markup.Escape(e.Label)}[/]"),
                new Markup($"[grey]{Markup.Escape(e.Time)}[/]"),
                new Markup(Markup.Escape(e.Message)));
        }

        return table;
    }

    private static string RoleColor(WorkerRole role) => role switch
    {
        WorkerRole.Publisher   => "green",
        WorkerRole.LineCook    => "cyan",
        WorkerRole.Bartender   => "dodgerblue1",
        WorkerRole.Manager     => "magenta",
        WorkerRole.GroupOrder  => "yellow",
        _ => "grey",
    };
}
