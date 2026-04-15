using MassTransit;
using MassTransit.Hackathon;
using MassTransit.Hackathon.Consumers;
using MassTransit.Hackathon.Workers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// Ensure stdout is UTF-8 so special characters (e.g. →) survive pipe redirection
// when this process is spawned by the Dashboard.
Console.OutputEncoding = System.Text.Encoding.UTF8;
Console.InputEncoding  = System.Text.Encoding.UTF8;

// Usage examples:
//   dotnet run                                          → all consumers + publisher
//   dotnet run -- --mode consumer --type linecook       → single LineCook consumer
//   dotnet run -- --mode consumer --type bartender --slow 3000   → slow Bartender (crash test)
//   dotnet run -- --mode publisher --rate 2             → publisher only, 2-second interval
//   dotnet run -- --mode consumer --type manager --label Manager-1

var options = WorkerOptions.Parse(args);

await Host.CreateDefaultBuilder(args)
    .ConfigureLogging(logging =>
    {
        logging.ClearProviders();
        logging.AddSimpleConsole(o =>
        {
            o.SingleLine = true;
            o.TimestampFormat = "HH:mm:ss ";
        });
        logging.SetMinimumLevel(LogLevel.Information);
    })
    .ConfigureServices(services =>
    {
        services.AddSingleton(options);

        services.AddMassTransit(x =>
        {
            bool isAll = options.Mode == "all";
            bool isConsumer = options.Mode == "consumer";

            if (isAll || (isConsumer && options.Type == "linecook"))
                x.AddConsumer<LineCookConsumer>();

            if (isAll || (isConsumer && options.Type == "bartender"))
                x.AddConsumer<BartenderConsumer>();

            if (isAll || (isConsumer && options.Type == "manager"))
                x.AddConsumer<ManagerConsumer>();

            x.UsingRabbitMq((context, cfg) =>
            {
                cfg.Host("localhost", "/", h =>
                {
                    h.Username("guest");
                    h.Password("guest");
                });

                cfg.ConfigureEndpoints(context);
            });
        });

        if (options.Mode is "all" or "publisher")
            services.AddHostedService<WaiterPublisherWorker>();

        // Always register — is a no-op when stdin is not redirected
        services.AddHostedService<StdinShutdownMonitor>();
    })
    .RunConsoleAsync();
