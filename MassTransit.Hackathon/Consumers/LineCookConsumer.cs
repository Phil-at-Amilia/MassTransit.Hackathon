using MassTransit.Hackathon.Messages;
using Microsoft.Extensions.Logging;

namespace MassTransit.Hackathon.Consumers;

/// <summary>
/// Line cook: handles <see cref="OrderItem.Burger"/> and <see cref="OrderItem.Fries"/> orders.
/// Ignores <see cref="OrderItem.Soda"/> — that's the bartender's job.
/// </summary>
public class LineCookConsumer : IConsumer<IOrderMessage>
{
    private readonly ILogger<LineCookConsumer> _logger;
    private readonly WorkerOptions _options;

    public LineCookConsumer(ILogger<LineCookConsumer> logger, WorkerOptions options)
    {
        _logger = logger;
        _options = options;
    }

    public async Task Consume(ConsumeContext<IOrderMessage> context)
    {
        if (context.Message.Item == OrderItem.Soda)
            return;

        var label = string.IsNullOrEmpty(_options.Label) ? "LineCook" : _options.Label;

        if (_options.SlowMs > 0)
        {
            _logger.LogInformation(
                "[{Label}] Starting slow cook of {Item} ({SlowMs}ms delay)...",
                label, context.Message.Item, _options.SlowMs);

            await Task.Delay(_options.SlowMs, context.CancellationToken);
        }

        _logger.LogInformation(
            "[{Label}] Cooked {Item} (ordered at {PlacedAt:O})",
            label,
            context.Message.Item,
            context.Message.PlacedAt);
    }
}
