using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// ---------------------------------------------------------------------------
// Entry point – Generic Host wires everything together.
// Run: dotnet run  (make sure docker-compose up is running first)
// ---------------------------------------------------------------------------
await Host.CreateDefaultBuilder(args)
    .ConfigureServices(services =>
    {
        // ---------------------------------------------------------------
        // MassTransit + RabbitMQ transport
        // ---------------------------------------------------------------
        services.AddMassTransit(x =>
        {
            // Register the consumer so MassTransit creates the queue for us.
            x.AddConsumer<HelloConsumer>();

            x.UsingRabbitMq((context, cfg) =>
            {
                // Connect to the local RabbitMQ instance started via docker-compose.
                cfg.Host("localhost", "/", h =>
                {
                    h.Username("guest");
                    h.Password("guest");
                });

                // Auto-configure a receive endpoint for every registered consumer.
                cfg.ConfigureEndpoints(context);
            });
        });

        // ---------------------------------------------------------------
        // Background worker – publishes IHelloMessage every 5 seconds.
        // ---------------------------------------------------------------
        services.AddHostedService<HelloPublisherWorker>();
    })
    .RunConsoleAsync(); // Ctrl+C (or SIGTERM) triggers graceful shutdown.

// ===========================================================================
// Message contract
// ===========================================================================

/// <summary>
/// The message that travels across the bus.
/// MassTransit uses this interface to create a fanout exchange in RabbitMQ
/// named after the full type name.
/// </summary>
public interface IHelloMessage
{
    /// <summary>Human-readable payload.</summary>
    string Text { get; }

    /// <summary>UTC timestamp stamped by the publisher.</summary>
    DateTime SentAt { get; }
}

// ===========================================================================
// Consumer
// ===========================================================================

/// <summary>
/// Processes every <see cref="IHelloMessage"/> that arrives on this app's queue.
/// MassTransit resolves this class from DI, so constructor injection works.
/// </summary>
public class HelloConsumer : IConsumer<IHelloMessage>
{
    private readonly ILogger<HelloConsumer> _logger;

    public HelloConsumer(ILogger<HelloConsumer> logger) => _logger = logger;

    public Task Consume(ConsumeContext<IHelloMessage> context)
    {
        _logger.LogInformation(
            "[Consumer] Received → \"{Text}\" (sent at {SentAt:O})",
            context.Message.Text,
            context.Message.SentAt);

        return Task.CompletedTask;
    }
}

// ===========================================================================
// Background publisher
// ===========================================================================

/// <summary>
/// Hosted background service that publishes an <see cref="IHelloMessage"/>
/// every 5 seconds so the system is active immediately on startup.
/// </summary>
public class HelloPublisherWorker : BackgroundService
{
    private readonly IPublishEndpoint _publishEndpoint;
    private readonly ILogger<HelloPublisherWorker> _logger;

    public HelloPublisherWorker(
        IPublishEndpoint publishEndpoint,
        ILogger<HelloPublisherWorker> logger)
    {
        _publishEndpoint = publishEndpoint;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // Give the bus a moment to finish connecting before the first publish.
            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                // MassTransit accepts anonymous objects that satisfy the interface shape.
                await _publishEndpoint.Publish<IHelloMessage>(
                    new { Text = "Hello from MassTransit!", SentAt = DateTime.UtcNow },
                    stoppingToken);

                _logger.LogInformation("[Publisher] Published IHelloMessage at {Time:O}", DateTime.UtcNow);

                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during graceful shutdown – no action required.
            _logger.LogInformation("[Publisher] Stopping gracefully.");
        }
    }
}
