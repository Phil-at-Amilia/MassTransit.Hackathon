using MassTransit.Hackathon.Messages;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MassTransit.Hackathon.Workers;

/// <summary>
/// Group-order publisher: asks for an item type and a count (either via CLI args when spawned
/// by the dashboard, or interactively via stdin when run standalone), then publishes that many
/// <see cref="IOrderMessage"/> messages in one shot and exits.
/// </summary>
public class GroupOrderPublisherWorker : BackgroundService
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(2);

    private readonly IPublishEndpoint _publishEndpoint;
    private readonly ILogger<GroupOrderPublisherWorker> _logger;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly WorkerOptions _options;

    public GroupOrderPublisherWorker(
        IPublishEndpoint publishEndpoint,
        ILogger<GroupOrderPublisherWorker> logger,
        IHostApplicationLifetime lifetime,
        WorkerOptions options)
    {
        _publishEndpoint = publishEndpoint;
        _logger = logger;
        _lifetime = lifetime;
        _options = options;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Give the bus a moment to finish connecting before prompting.
        await Task.Delay(InitialDelay, stoppingToken);

        // When spawned by the dashboard, item and count are pre-supplied via CLI args.
        // Fall back to interactive stdin prompts only when running standalone.
        OrderItem item;
        int count;
        if (!string.IsNullOrEmpty(_options.Item)
            && Enum.TryParse<OrderItem>(_options.Item, ignoreCase: true, out var parsedItem)
            && _options.Count > 0)
        {
            item  = parsedItem;
            count = _options.Count;
        }
        else
        {
            item  = PromptItem();
            count = PromptCount();
        }

        var placedAt = DateTime.UtcNow;

        for (int i = 0; i < count; i++)
        {
            var orderId = Guid.NewGuid().ToString("N")[..5].ToUpper();

            _logger.LogInformation(
                "[GroupOrder] Published order [{OrderId}]: {Item} ({Number}/{Count})",
                orderId, item, i + 1, count);

            await _publishEndpoint.Publish<IOrderMessage>(
                new { OrderId = orderId, Item = item, PlacedAt = placedAt },
                stoppingToken);
        }

        _logger.LogInformation(
            "[GroupOrder] All {Count} {Item} orders published.",
            count, item);

        _lifetime.StopApplication();
    }

    private static OrderItem PromptItem()
    {
        while (true)
        {
            Console.Write("Which item? [burger / fries / soda]: ");
            var input = Console.ReadLine()?.Trim();

            if (Enum.TryParse<OrderItem>(input, ignoreCase: true, out var item))
                return item;

            Console.WriteLine($"  Unknown item '{input}'. Please enter burger, fries, or soda.");
        }
    }

    private static int PromptCount()
    {
        while (true)
        {
            Console.Write("Count: ");
            var input = Console.ReadLine()?.Trim();

            if (int.TryParse(input, out var count) && count > 0)
                return count;

            Console.WriteLine($"  Invalid count '{input}'. Please enter a positive integer.");
        }
    }
}
