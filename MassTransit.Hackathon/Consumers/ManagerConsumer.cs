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

    public ManagerConsumer(ILogger<ManagerConsumer> logger, WorkerOptions options)
    {
        _logger = logger;
        _options = options;
    }

    public async Task Consume(ConsumeContext<IOrderMessage> context)
    {
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
