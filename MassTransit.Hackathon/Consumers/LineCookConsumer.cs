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
    private readonly PauseSignal _pauseSignal;

    public LineCookConsumer(ILogger<LineCookConsumer> logger, WorkerOptions options, PauseSignal pauseSignal)
    {
        _logger      = logger;
        _options     = options;
        _pauseSignal = pauseSignal;
    }

    public async Task Consume(ConsumeContext<IOrderMessage> context)
    {
        // Block here while the process is paused; unblocks on resume or cancellation.
        await _pauseSignal.WaitIfPausedAsync(context.CancellationToken);

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
            "[{Label}] Cooked [{OrderId}] {Item} (ordered at {PlacedAt:O})",
            label,
            context.Message.OrderId,
            context.Message.Item,
            context.Message.PlacedAt);
    }
}
