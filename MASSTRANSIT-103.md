# MassTransit 103 — Advanced Concepts

> Long-running workflows, production-safe messaging, test-driven verification, and custom middleware. This guide covers **Tier 4 & 5** achievements. Read [MASSTRANSIT-101.md](MASSTRANSIT-101.md) and [MASSTRANSIT-102.md](MASSTRANSIT-102.md) first.

---

## Table of Contents

1. [Sagas & State Machines](#1-sagas--state-machines)
2. [Transactional Outbox](#2-transactional-outbox)
3. [Testing with the Test Harness](#3-testing-with-the-test-harness)
4. [Custom Filter (Middleware)](#4-custom-filter-middleware)
5. [Putting It All Together](#5-putting-it-all-together)

---

## 1. Sagas & State Machines

> 🏆 **Achievements:** *#14 The Kitchen Manager* · *#15 Linking the Orders* · *#16 Last Call* (Tier 4 — Full Service)

### Why?

A single kitchen order is simple. But a **multi-course meal** has stages: appetizer → entrée → dessert. If the dessert takes too long, you might cancel it. **Sagas** (specifically MassTransit's *Automatonymous* state machines) let you model this kind of **long-running, multi-step workflow** as an explicit state machine.

### Core Concepts

| Concept | Kitchen Analogy |
|---------|-----------------|
| **State** | Where is this order right now? (`Submitted`, `Cooking`, `Ready`, `Cancelled`) |
| **Event** | Something that happened (`OrderSubmitted`, `ItemCooked`, `TimedOut`) |
| **Saga Instance** | One specific order being tracked (identified by `CorrelationId`) |
| **CorrelationId** | The order number — ties all events to the same order |

### Step-by-Step: Building a Kitchen Order Saga

#### 1. Define the Events (Messages)

```csharp
// Messages/IOrderSubmitted.cs
public interface IOrderSubmitted
{
    Guid CorrelationId { get; }   // 🔑 ties everything together
    OrderItem Item { get; }
    DateTime SubmittedAt { get; }
}

// Messages/IOrderCooked.cs
public interface IOrderCooked
{
    Guid CorrelationId { get; }
    DateTime CookedAt { get; }
}
```

#### 2. Define the Saga Instance (State Storage)

```csharp
// Sagas/KitchenOrderState.cs
using MassTransit;

public class KitchenOrderState : SagaStateMachineInstance
{
    public Guid CorrelationId { get; set; }  // primary key
    public string CurrentState { get; set; } = default!;
    public OrderItem Item { get; set; }
    public DateTime? SubmittedAt { get; set; }
    public DateTime? CookedAt { get; set; }
}
```

#### 3. Define the State Machine

```csharp
// Sagas/KitchenOrderStateMachine.cs
using MassTransit;

public class KitchenOrderStateMachine : MassTransitStateMachine<KitchenOrderState>
{
    // States
    public State Submitted { get; private set; } = default!;
    public State Cooking { get; private set; } = default!;
    public State Ready { get; private set; } = default!;
    public State Cancelled { get; private set; } = default!;

    // Events
    public Event<IOrderSubmitted> OrderSubmitted { get; private set; } = default!;
    public Event<IOrderCooked> OrderCooked { get; private set; } = default!;

    // Timeout schedule
    public Schedule<KitchenOrderState, OrderTimedOut> OrderTimeout { get; private set; } = default!;

    public KitchenOrderStateMachine()
    {
        // Tell MassTransit which property holds the current state
        InstanceState(x => x.CurrentState);

        // Map events to CorrelationId
        Event(() => OrderSubmitted, x => x.CorrelateById(ctx => ctx.Message.CorrelationId));
        Event(() => OrderCooked,    x => x.CorrelateById(ctx => ctx.Message.CorrelationId));

        // ⏱ Schedule a timeout (Achievement #16)
        Schedule(() => OrderTimeout, x => x.CorrelationId, s =>
        {
            s.Delay = TimeSpan.FromSeconds(30);
            s.Received = r => r.CorrelateById(ctx => ctx.Message.CorrelationId);
        });

        // 🗺 State transitions
        Initially(
            When(OrderSubmitted)
                .Then(ctx =>
                {
                    ctx.Saga.Item = ctx.Message.Item;
                    ctx.Saga.SubmittedAt = ctx.Message.SubmittedAt;
                })
                .Schedule(OrderTimeout, ctx => new OrderTimedOut
                {
                    CorrelationId = ctx.Saga.CorrelationId
                })
                .TransitionTo(Submitted)
        );

        During(Submitted,
            When(OrderCooked)
                .Then(ctx => ctx.Saga.CookedAt = ctx.Message.CookedAt)
                .Unschedule(OrderTimeout)
                .TransitionTo(Ready)
                .Finalize(),

            When(OrderTimeout!.Received)
                .TransitionTo(Cancelled)
                .Finalize()
        );

        // Clean up completed sagas
        SetCompletedWhenFinalized();
    }
}

// Timeout message
public class OrderTimedOut
{
    public Guid CorrelationId { get; set; }
}
```

#### 4. Register in Program.cs

```csharp
services.AddMassTransit(x =>
{
    // Register the saga with in-memory storage (great for hackathon!)
    x.AddSagaStateMachine<KitchenOrderStateMachine, KitchenOrderState>()
     .InMemoryRepository();

    x.UsingRabbitMq((context, cfg) =>
    {
        cfg.Host("localhost", "/", h =>
        {
            h.Username("guest");
            h.Password("guest");
        });

        // Enable the message scheduler for timeouts
        cfg.UseInMemoryScheduler();

        cfg.ConfigureEndpoints(context);
    });
});
```

### How It Flows

```
[Waiter]                  [Saga]                  [LineCook]
   │                        │                         │
   │  Publish               │                         │
   │  IOrderSubmitted ────► │ Initial → Submitted     │
   │                        │ (start 30s timer)       │
   │                        │                         │
   │                        │     IOrderCooked ◄───── │
   │                        │ Submitted → Ready       │
   │                        │ (cancel timer, done!)   │
   │                        │                         │
   ─── OR if 30s pass ──── │                         │
   │                        │ Submitted → Cancelled   │
   │                        │ (timeout fired!)        │
```

### Key Points

- Each saga instance is identified by a **`CorrelationId`** (Guid). All events for the same order share this Id.
- **In-memory repository** is fine for hackathon/dev. For production, use **Entity Framework**, **Redis**, or **MongoDB**.
- **`SetCompletedWhenFinalized()`** removes the saga instance once it reaches a `Final` state.

---

## 2. Transactional Outbox

> 🏆 **Achievement:** *#20 The Final Check (Head Chef)* (Tier 5 — The Chef's Table)

### The Problem

Imagine the manager writes an order to the database **and** publishes a message. What if the database save succeeds but RabbitMQ is down? The message is lost. What if the publish succeeds but the database crashes? The data is inconsistent.

```csharp
// ⚠️ DANGEROUS — not atomic
await _dbContext.Orders.AddAsync(order);
await _dbContext.SaveChangesAsync();           // ✅ saved
await _publishEndpoint.Publish<IOrderCooked>(  // ❌ what if this fails?
    new { order.CorrelationId, CookedAt = DateTime.UtcNow });
```

### The Solution: Transactional Outbox

The outbox pattern ensures **database writes and message publishes are atomic**:

1. Instead of publishing to RabbitMQ directly, MassTransit writes the message to an **outbox table** in the same database transaction.
2. A background process reads the outbox table and delivers messages to RabbitMQ.
3. If the database save fails → the outbox row is also rolled back → no orphan message.
4. If RabbitMQ is down → the outbox row survives → message is delivered once the broker is back.

```
┌───────────────────────────────────────────┐
│  Single Database Transaction              │
│                                           │
│  1. INSERT INTO Orders (...)              │
│  2. INSERT INTO OutboxMessages (...)      │ ← MassTransit does this
│  COMMIT                                   │
└───────────────────────────────────────────┘
        │
        ▼ (background thread)
┌──────────────────┐     ┌──────────────────┐
│  OutboxMessages   │ ──► │    RabbitMQ       │
│  table            │     │                  │
└──────────────────┘     └──────────────────┘
```

### Kitchen Example with EF Core

#### 1. Install NuGet Packages

```bash
dotnet add package MassTransit.EntityFrameworkCore
dotnet add package Microsoft.EntityFrameworkCore.Sqlite   # or your provider
```

#### 2. Create a DbContext with Outbox Support

```csharp
using MassTransit;
using Microsoft.EntityFrameworkCore;

public class KitchenDbContext : DbContext
{
    public KitchenDbContext(DbContextOptions<KitchenDbContext> options)
        : base(options) { }

    public DbSet<KitchenOrder> Orders => Set<KitchenOrder>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Add MassTransit outbox tables (InboxState, OutboxMessage, OutboxState)
        modelBuilder.AddInboxStateEntity();
        modelBuilder.AddOutboxMessageEntity();
        modelBuilder.AddOutboxStateEntity();
    }
}

public class KitchenOrder
{
    public Guid Id { get; set; }
    public string Item { get; set; } = default!;
    public DateTime CreatedAt { get; set; }
}
```

#### 3. Wire It Up in Program.cs

```csharp
services.AddDbContext<KitchenDbContext>(opt =>
    opt.UseSqlite("Data Source=kitchen.db"));

services.AddMassTransit(x =>
{
    x.AddEntityFrameworkOutbox<KitchenDbContext>(o =>
    {
        o.UseSqlite();            // match your EF provider
        o.UseBusOutbox();         // delivers messages via a background service
    });

    x.AddConsumer<LineCookConsumer>();

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
```

#### 4. Use It in a Consumer

```csharp
public class LineCookConsumer : IConsumer<IOrderSubmitted>
{
    private readonly KitchenDbContext _db;

    public LineCookConsumer(KitchenDbContext db) => _db = db;

    public async Task Consume(ConsumeContext<IOrderSubmitted> context)
    {
        // Save to DB and publish — in the SAME transaction
        _db.Orders.Add(new KitchenOrder
        {
            Id = context.Message.CorrelationId,
            Item = context.Message.Item.ToString(),
            CreatedAt = DateTime.UtcNow
        });

        // This publish is captured by the outbox, not sent to RabbitMQ yet
        await context.Publish<IOrderCooked>(new
        {
            context.Message.CorrelationId,
            CookedAt = DateTime.UtcNow
        });

        // Both the DB row and the outbox message commit together
        await _db.SaveChangesAsync();
    }
}
```

### Key Points

- The outbox guarantees **exactly-once message delivery** (combined with the inbox for deduplication).
- `UseBusOutbox()` adds a background service that polls the outbox table and delivers messages to RabbitMQ.
- For the hackathon, SQLite keeps it simple. Production systems use SQL Server, PostgreSQL, etc.

---

## 3. Testing with the Test Harness

> 🏆 **Achievement:** *#19 Test Kitchen* (Tier 5 — The Chef's Table)

### Why?

You shouldn't need Docker or RabbitMQ running to verify your consumer logic. MassTransit's **Test Harness** spins up an **in-memory bus** that behaves like the real thing — you publish messages, assert they were consumed, and check side effects.

### Setup

Add the testing packages to a test project:

```bash
dotnet add package MassTransit.Testing
dotnet add package Microsoft.Extensions.DependencyInjection
dotnet add package xunit          # or NUnit / MSTest — your choice
```

### Kitchen Example — Testing the LineCookConsumer

```csharp
using MassTransit;
using MassTransit.Testing;
using MassTransit.Hackathon.Consumers;
using MassTransit.Hackathon.Messages;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

public class LineCookConsumerTests
{
    [Fact]
    public async Task Should_consume_burger_order()
    {
        // Arrange — build a test harness with the consumer
        await using var provider = new ServiceCollection()
            .AddSingleton(new WorkerOptions()) // default options
            .AddMassTransitTestHarness(x =>
            {
                x.AddConsumer<LineCookConsumer>();
            })
            .BuildServiceProvider(true);

        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        // Act — publish a burger order
        await harness.Bus.Publish<IOrderMessage>(new
        {
            OrderId = "TEST1",
            Item = OrderItem.Burger,
            PlacedAt = DateTime.UtcNow
        });

        // Assert — the consumer received and processed the message
        Assert.True(await harness.Consumed.Any<IOrderMessage>(
            x => x.Context.Message.OrderId == "TEST1"));

        var consumerHarness = harness.GetConsumerHarness<LineCookConsumer>();
        Assert.True(await consumerHarness.Consumed.Any<IOrderMessage>(
            x => x.Context.Message.OrderId == "TEST1"));
    }

    [Fact]
    public async Task Should_ignore_soda_order()
    {
        await using var provider = new ServiceCollection()
            .AddSingleton(new WorkerOptions())
            .AddMassTransitTestHarness(x =>
            {
                x.AddConsumer<LineCookConsumer>();
            })
            .BuildServiceProvider(true);

        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        // Act — publish a soda order (not the line cook's job)
        await harness.Bus.Publish<IOrderMessage>(new
        {
            OrderId = "SODA1",
            Item = OrderItem.Soda,
            PlacedAt = DateTime.UtcNow
        });

        // Assert — message was consumed (delivered) but the cook did nothing
        Assert.True(await harness.Consumed.Any<IOrderMessage>());

        // The consumer still technically "consumed" it (returned early),
        // but you can verify no side effects occurred (no logs, no DB writes, etc.)
    }
}
```

### Testing a Saga

```csharp
[Fact]
public async Task Should_transition_to_submitted_on_order()
{
    await using var provider = new ServiceCollection()
        .AddMassTransitTestHarness(x =>
        {
            x.AddSagaStateMachine<KitchenOrderStateMachine, KitchenOrderState>()
             .InMemoryRepository();
        })
        .BuildServiceProvider(true);

    var harness = provider.GetRequiredService<ITestHarness>();
    await harness.Start();

    var orderId = NewId.NextGuid();

    // Act — submit an order
    await harness.Bus.Publish<IOrderSubmitted>(new
    {
        CorrelationId = orderId,
        Item = OrderItem.Burger,
        SubmittedAt = DateTime.UtcNow
    });

    // Assert — saga instance exists and is in the Submitted state
    var sagaHarness = harness
        .GetSagaStateMachineHarness<KitchenOrderStateMachine, KitchenOrderState>();

    Assert.True(await sagaHarness.Consumed.Any<IOrderSubmitted>());
    Assert.True(await sagaHarness.Created.Any(x =>
        x.CorrelationId == orderId));

    var instance = sagaHarness.Created
        .ContainsInState(orderId, sagaHarness.StateMachine, sagaHarness.StateMachine.Submitted);
    Assert.NotNull(instance);
}
```

### Key Points

- The test harness uses an **in-memory transport** — no RabbitMQ needed.
- `harness.Consumed.Any<T>()` waits (with a default timeout) for a message to be consumed.
- `GetConsumerHarness<T>()` lets you assert at the consumer level.
- `GetSagaStateMachineHarness<T, TInstance>()` lets you inspect saga state transitions.
- Tests run fast and are fully deterministic.

---

## 4. Custom Filter (Middleware)

> 🏆 **Achievement:** *#18 Quality Control* (Tier 5 — The Chef's Table)

### Why?

MassTransit processes messages through a **middleware pipeline** — a chain of filters that each wrap the next. Adding your own filter lets you inject cross-cutting behaviour (timing, logging, correlation, exception enrichment) without touching consumer code.

**One-liner:** A filter is a middleware step that runs before and after every `Consume()` call — your consumer doesn't know it's there.

### Kitchen Analogy

The expeditor stands at the pass between the kitchen and the dining room. Every plate goes through them — they check quality, stamp the time, and call out how long each station took. The individual cooks don't change; the expeditor is invisible to them.

### How It Works

```
Message arrives
  └─► TimingFilter<T>.Send()    ← starts Stopwatch
        └─► next.Send()         ← calls the actual consumer
        ← (consumer returns)
  ← TimingFilter<T>.Send()      ← stops Stopwatch, logs elapsed
```

### Implementing the Filter

```csharp
// Filters/TimingFilter.cs
using System.Diagnostics;
using MassTransit;
using Microsoft.Extensions.Logging;

public class TimingFilter<T> : IFilter<ConsumeContext<T>>
    where T : class
{
    private readonly ILogger<TimingFilter<T>> _logger;

    public TimingFilter(ILogger<TimingFilter<T>> logger) => _logger = logger;

    public async Task Send(ConsumeContext<T> context, IPipe<ConsumeContext<T>> next)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            await next.Send(context);
        }
        finally
        {
            sw.Stop();
            _logger.LogInformation("[Timing] {MessageType} processed in {ElapsedMs}ms",
                typeof(T).Name, sw.ElapsedMilliseconds);
        }
    }

    public void Probe(ProbeContext context) => context.CreateFilterScope("timing");
}
```

The `Send` method wraps the pipeline: it runs before and after the consumer. The `Probe` method is called by MassTransit's diagnostic pipeline graph — just return a named scope.

### Wiring It Up

Register the filter in DI, then apply it globally to all consumers using the open-generic form:

```csharp
// Program.cs — register in DI before AddMassTransit
builder.Services.AddScoped(typeof(TimingFilter<>));

// Inside AddMassTransit → UsingRabbitMq
x.UsingRabbitMq((context, cfg) =>
{
    cfg.Host("localhost", "/", h =>
    {
        h.Username("guest");
        h.Password("guest");
    });

    // 🕐 Apply TimingFilter<T> to every consumer endpoint
    cfg.UseConsumeFilter(typeof(TimingFilter<>), context);

    cfg.ConfigureEndpoints(context);
});
```

Or apply it to a **specific endpoint** only:

```csharp
cfg.ReceiveEndpoint("line-cook-consumer", e =>
{
    e.UseConsumeFilter(typeof(TimingFilter<>), context);
    e.ConfigureConsumer<LineCookConsumer>(context);
});
```

> 💡 The open-generic `typeof(TimingFilter<>)` tells MassTransit to construct `TimingFilter<IOrderMessage>` (or whatever message type) automatically at runtime.

### What You'll See

```
[Timing] IOrderMessage processed in 12ms
[Timing] IOrderMessage processed in 8ms
[Timing] IOrderMessage processed in 145ms   ← grill flare-up, needed retries
```

### Key Points

- `IFilter<ConsumeContext<T>>` requires implementing `Send()` and `Probe()`.
- Always `await next.Send(context)` — skipping it short-circuits the consumer entirely.
- Use `try/finally` to guarantee logging even when the consumer throws.
- Multiple filters compose in registration order — outermost first, innermost closest to the consumer.

---

## 5. Putting It All Together

Here's how these advanced concepts layer on top of each other in our kitchen:

```
                              ┌─────────────────────┐
                              │   Test Harness       │
                              │  (verify everything  │
                              │   without Docker)    │
                              └────────┬────────────┘
                                       │ tests
                                       ▼
┌──────────┐  IOrderSubmitted  ┌──────────────────┐  IOrderCooked   ┌──────────┐
│  Waiter   │ ───────────────► │  Kitchen Saga     │ ◄────────────── │ LineCook │
│ Publisher │                  │  (State Machine)  │                 │ Consumer │
└──────────┘                   │                   │                 └──────────┘
                               │ Submitted→Ready   │                    │
                               │ or →Cancelled     │         ┌─────────┴──────────┐
                               └──────────────────┘         │  TimingFilter<T>   │
                                                            │  (logs elapsed ms) │
                                                            └─────────┬──────────┘
                                                                      │
                                                            ┌─────────▼──────────┐
                                                            │  Transactional     │
                                                            │  Outbox (EF Core)  │
                                                            │  DB + Publish      │
                                                            │  in one TX         │
                                                            └────────────────────┘
```

### Achievement Cheat Sheet

| Achievement | Concept | Section |
|-------------|---------|---------|
| **#14** The Kitchen Manager | Saga State Machine | [§1 Sagas & State Machines](#1-sagas--state-machines) |
| **#15** Linking the Orders | Saga Event Correlation | [§1 Sagas & State Machines](#1-sagas--state-machines) |
| **#16** Last Call | Saga Timeout / Schedule | [§1 Sagas & State Machines](#1-sagas--state-machines) |
| **#18** Quality Control | Custom Filter (Timing) | [§4 Custom Filter (Middleware)](#4-custom-filter-middleware) |
| **#19** Test Kitchen | Test Harness | [§3 Testing with the Test Harness](#3-testing-with-the-test-harness) |
| **#20** The Final Check | Transactional Outbox | [§2 Transactional Outbox](#2-transactional-outbox) |
