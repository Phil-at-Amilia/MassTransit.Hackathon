using MassTransit.Hackathon.Messages;
using Microsoft.Extensions.Logging;

namespace MassTransit.Hackathon.Consumers;

/// <summary>
/// Bartender: handles <see cref="OrderItem.Soda"/> orders exclusively.
/// Ignores food items — that's the line cook's domain.
/// </summary>
public class BartenderConsumer : IConsumer<IOrderMessage>
{
    private readonly ILogger<BartenderConsumer> _logger;
    private readonly WorkerOptions _options;

    public BartenderConsumer(ILogger<BartenderConsumer> logger, WorkerOptions options)
    {
        _logger = logger;
        _options = options;
    }

    public async Task Consume(ConsumeContext<IOrderMessage> context)
    {
        if (context.Message.Item != OrderItem.Soda)
            return;

        var label = string.IsNullOrEmpty(_options.Label) ? "Bartender" : _options.Label;

        if (_options.SlowMs > 0)
        {
            _logger.LogInformation(
                "[{Label}] Starting slow pour of {Item} ({SlowMs}ms delay)...",
                label, context.Message.Item, _options.SlowMs);

            await Task.Delay(_options.SlowMs, context.CancellationToken);
        }

        _logger.LogInformation(
            "[{Label}] Poured [{OrderId}] {Item} (ordered at {PlacedAt:O})",
            label,
            context.Message.OrderId,
            context.Message.Item,
            context.Message.PlacedAt);
    }
}
