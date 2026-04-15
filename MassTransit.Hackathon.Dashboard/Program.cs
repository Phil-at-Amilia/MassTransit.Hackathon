using MassTransit.Hackathon.Dashboard;
using Spectre.Console;

// Resolve the worker binary — exits early with a clear message if not built yet
(string workerExe, string argPrefix) workerInfo;
try
{
    workerInfo = WorkerExecutable.Resolve();
}
catch (FileNotFoundException ex)
{
    AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(ex.Message)}");
    return 1;
}

var pm  = new ProcessManager(workerInfo.workerExe, workerInfo.argPrefix);
var rmq = new RabbitMqMonitor();

using var cts = new CancellationTokenSource();

// RabbitMQ polling runs in the background
var rmqTask = rmq.StartPollingAsync(cts.Token);

// Graceful app-level shutdown on Ctrl+C (when running standalone, not via Dashboard)
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

var dashboard = new TuiDashboard(pm, rmq);

try
{
    await dashboard.RunAsync();
}
finally
{
    cts.Cancel();
    AnsiConsole.MarkupLine("\n[yellow]Shutting down all worker processes...[/]");
    pm.KillAll();
    await Task.Delay(500); // give KillAll a moment to flush
    AnsiConsole.MarkupLine("[grey]Done.[/]");
}

return 0;
