using MassTransit.Hackathon.Messages;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MassTransit.Hackathon.Workers;

/// <summary>
/// Waiter: background service that takes a random order every N seconds
/// and publishes it as an <see cref="IOrderMessage"/> on the bus.
/// The publish interval is controlled by <see cref="WorkerOptions.RateSeconds"/>.
/// </summary>
public class WaiterPublisherWorker : BackgroundService
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(2);

    private static readonly OrderItem[] Menu =
        [OrderItem.Burger, OrderItem.Fries, OrderItem.Soda];

    private readonly IPublishEndpoint _publishEndpoint;
    private readonly ILogger<WaiterPublisherWorker> _logger;
    private readonly WorkerOptions _options;

    public WaiterPublisherWorker(
        IPublishEndpoint publishEndpoint,
        ILogger<WaiterPublisherWorker> logger,
        WorkerOptions options)
    {
        _publishEndpoint = publishEndpoint;
        _logger = logger;
        _options = options;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var label = string.IsNullOrEmpty(_options.Label) ? "Waiter" : _options.Label;
        var interval = TimeSpan.FromSeconds(_options.RateSeconds);

        // Give the bus a moment to finish connecting before the first publish.
        await Task.Delay(InitialDelay, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            var item = Menu[Random.Shared.Next(Menu.Length)];

            await _publishEndpoint.Publish<IOrderMessage>(
                new { Item = item, PlacedAt = DateTime.UtcNow },
                stoppingToken);

            _logger.LogInformation(
                "[{Label}] Placed order: {Item} at {Time:O}",
                label, item, DateTime.UtcNow);

            await Task.Delay(interval, stoppingToken);
        }
    }
}

