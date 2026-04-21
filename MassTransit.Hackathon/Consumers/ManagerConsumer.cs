using MassTransit.Hackathon.Messages;
using Microsoft.Extensions.Logging;

namespace MassTransit.Hackathon.Consumers;

/// <summary>
/// Manager: observes every <see cref="IOrderMessage"/> on the bus and logs it.
/// Acts as a silent auditor — does not interfere with order processing.
/// Multiple instances share the queue (competing consumers — each sees ~1/N of messages).
/// </summary>
public class ManagerConsumer : IConsumer<IOrderMessage>
{
    private readonly ILogger<ManagerConsumer> _logger;
    private readonly WorkerOptions _options;
    private readonly PauseSignal _pauseSignal;

    public ManagerConsumer(ILogger<ManagerConsumer> logger, WorkerOptions options, PauseSignal pauseSignal)
    {
        _logger      = logger;
        _options     = options;
        _pauseSignal = pauseSignal;
    }

    public async Task Consume(ConsumeContext<IOrderMessage> context)
    {
        // Block here while the process is paused; unblocks on resume or cancellation.
        await _pauseSignal.WaitIfPausedAsync(context.CancellationToken);

        var label = string.IsNullOrEmpty(_options.Label) ? "Manager" : _options.Label;

        if (_options.SlowMs > 0)
            await Task.Delay(_options.SlowMs, context.CancellationToken);

        _logger.LogInformation(
            "[{Label}] Saw order [{OrderId}] → {Item} (placed at {PlacedAt:O})",
            label,
            context.Message.OrderId,
            context.Message.Item,
            context.Message.PlacedAt);
    }
}
