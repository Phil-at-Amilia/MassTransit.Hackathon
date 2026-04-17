# MassTransit 102 — Advanced Concepts

> Level up from the basics. This guide covers **retries**, **circuit breakers**, **sagas**, **the transactional outbox**, and **testing harnesses** — all through the lens of our kitchen hackathon. Each section maps to achievements in [ACHIEVEMENTS.md](ACHIEVEMENTS.md) so you know exactly where to apply what you learn.

---

## Table of Contents

1. [Retry Policies](#1-retry-policies)
2. [Circuit Breakers](#2-circuit-breakers)
3. [Sagas & State Machines](#3-sagas--state-machines)
4. [Transactional Outbox](#4-transactional-outbox)
5. [Testing with the Test Harness](#5-testing-with-the-test-harness)
6. [Putting It All Together](#6-putting-it-all-together)

---

## 1. Retry Policies

> 🏆 **Achievements:** *#5 The Hiccup* · *#8 Take a Breather* (Tier 2 — Resilience)

### Why?

In a real kitchen, the bartender might drop a glass — you don't shut down the bar. You try again. In messaging, transient failures (database timeouts, network blips, resource locks) are normal. Retry policies let MassTransit **automatically re-attempt** a failed `Consume()` call before giving up.

### Retry Types at a Glance

| Policy | Behavior | Best for |
|--------|----------|----------|
| **Immediate** | Retry N times with no delay | Quick transient errors (optimistic lock, brief network hiccup) |
| **Interval** | Retry N times, fixed delay between each | Known recovery time (e.g. wait 2 s for a connection to reset) |
| **Incremental** | Retry N times, delay increases linearly | Gradually back off without waiting too long |
| **Exponential** | Retry N times, delay doubles each time | External service rate-limiting or unknown recovery time |

### Kitchen Example — Immediate Retry

The line cook's grill occasionally flames out. We give it 3 quick retries before sending the order to the error queue.

```csharp
// In Program.cs — configure the LineCookConsumer endpoint
x.UsingRabbitMq((context, cfg) =>
{
    cfg.Host("localhost", "/", h =>
    {
        h.Username("guest");
        h.Password("guest");
    });

    cfg.ReceiveEndpoint("line-cook-consumer", e =>
    {
        // 🔁 Retry up to 3 times immediately
        e.UseMessageRetry(r => r.Immediate(3));

        e.ConfigureConsumer<LineCookConsumer>(context);
    });

    cfg.ConfigureEndpoints(context);
});
```

To **see it in action**, add a random failure inside `LineCookConsumer.Consume()`:

```csharp
public async Task Consume(ConsumeContext<IOrderMessage> context)
{
    if (context.Message.Item == OrderItem.Soda)
        return;

    // 🔥 Simulate a grill flare-up (50% chance)
    if (Random.Shared.Next(2) == 0)
        throw new Exception("Grill flamed out! Retrying...");

    _logger.LogInformation("[LineCook] Cooked [{OrderId}] {Item}",
        context.Message.OrderId, context.Message.Item);
}
```

Watch the console — you'll see the same `OrderId` retried up to 3 times before it succeeds or lands in the `_error` queue.

### Kitchen Example — Delayed Retry

The bartender ran out of ice and needs to wait for the ice machine. Use a delayed/interval retry to pause between attempts.

```csharp
cfg.ReceiveEndpoint("bartender-consumer", e =>
{
    // ⏳ Retry 3 times, waiting 2 seconds between each attempt
    e.UseMessageRetry(r => r.Interval(3, TimeSpan.FromSeconds(2)));

    e.ConfigureConsumer<BartenderConsumer>(context);
});
```

### Key Points

- Retries happen **in-process** — the message stays in memory; RabbitMQ doesn't see intermediate failures.
- When all retries are exhausted, the message moves to the `_error` queue (Achievement **#6 Toxic Waste**).
- You can combine retry with **exception filters** to retry only specific exception types:

```csharp
e.UseMessageRetry(r => r.Immediate(3)
    .Handle<TimeoutException>()   // only retry timeouts
    .Ignore<ArgumentException>()  // never retry bad input
);
```

---

## 2. Circuit Breakers

> 🏆 **Achievement:** *#9 Tripping the Breaker* (Tier 2 — Resilience)

### Why?

If the grill is completely broken, there's no point sending every burger order into a 3-retry loop that always fails. A **circuit breaker** detects sustained failures and **short-circuits** — instantly rejecting new messages for a cooldown period. This protects downstream systems and lets the kitchen recover.

### How It Works

```
 ┌─────────┐   failures    ┌──────────┐   cooldown    ┌───────────┐
 │  Closed  │ ──────────►  │   Open   │ ──────────►   │ Half-Open │
 │ (normal) │  hit limit   │ (reject) │  timer done   │  (probe)  │
 └─────────┘               └──────────┘               └───────────┘
      ▲                                                     │
      │              success on probe message                │
      └─────────────────────────────────────────────────────┘
```

| State | What happens |
|-------|-------------|
| **Closed** | Messages flow normally. Failures are counted. |
| **Open** | All messages are **immediately** moved to `_error`. No processing attempted. |
| **Half-Open** | One message is allowed through as a test. If it succeeds → back to Closed. If it fails → back to Open. |

### Kitchen Example

The line cook's grill is toast. After 5 failures in 30 seconds, stop trying and let the grill cool down for a minute.

```csharp
cfg.ReceiveEndpoint("line-cook-consumer", e =>
{
    // 🔁 Inner retry: try 3 times per message
    e.UseMessageRetry(r => r.Immediate(3));

    // 🔌 Outer circuit breaker: if 5 messages fail within 30 s,
    //    open the circuit for 1 minute
    e.UseCircuitBreaker(cb =>
    {
        cb.TrackingPeriod = TimeSpan.FromSeconds(30);
        cb.TripThreshold = 5;
        cb.ActiveThreshold = 5;
        cb.ResetInterval = TimeSpan.FromMinutes(1);
    });

    e.ConfigureConsumer<LineCookConsumer>(context);
});
```

### Testing It (Achievement #9)

1. Hardcode `LineCookConsumer` to always throw.
2. Publish 6+ burger orders quickly.
3. The first 5 fail after retries (Closed → Open).
4. The 6th message **instantly** fails — no retries, no consumer code runs.
5. Check the RabbitMQ UI: messages piling in `line-cook-consumer_error`.
6. Wait 1 minute → the breaker moves to Half-Open → next message is probed.

### Retry + Circuit Breaker Ordering

The **order of middleware matters**. Think of it as wrapping layers:

```
Message arrives
  └─► Circuit Breaker (outer) — rejects if open
        └─► Retry (inner) — retries on failure
              └─► Consumer.Consume() — your code
```

Always configure retry **before** the circuit breaker in the pipeline (i.e. retry is the inner layer).

---

## 3. Sagas & State Machines

> 🏆 **Achievements:** *#14 The State Master* · *#15 Connecting the Dots* · *#16 The Ticking Clock* (Tier 4 — Sagas & Coordination)

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

## 4. Transactional Outbox

> 🏆 **Achievement:** *#20 The Outbox (Boss)* (Tier 5 — Observability & Production Readiness)

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

## 5. Testing with the Test Harness

> 🏆 **Achievement:** *#19 Safe in the Sandbox* (Tier 5 — Observability & Production Readiness)

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

## 6. Putting It All Together

Here's how these concepts layer on top of each other in our kitchen:

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
                               └──────────────────┘         │  Retry (3x immed)  │
                                                            │  Circuit Breaker   │
                                                            │  (5 fails → open)  │
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
| **#5** The Hiccup | Immediate Retry | [§1 Retry Policies](#1-retry-policies) |
| **#8** Take a Breather | Delayed / Interval Retry | [§1 Retry Policies](#1-retry-policies) |
| **#9** Tripping the Breaker | Circuit Breaker | [§2 Circuit Breakers](#2-circuit-breakers) |
| **#14** The State Master | Saga State Machine | [§3 Sagas & State Machines](#3-sagas--state-machines) |
| **#15** Connecting the Dots | Saga Event Correlation | [§3 Sagas & State Machines](#3-sagas--state-machines) |
| **#16** The Ticking Clock | Saga Timeout / Schedule | [§3 Sagas & State Machines](#3-sagas--state-machines) |
| **#19** Safe in the Sandbox | Test Harness | [§5 Testing with the Test Harness](#5-testing-with-the-test-harness) |
| **#20** The Outbox (Boss) | Transactional Outbox | [§4 Transactional Outbox](#4-transactional-outbox) |

---

## Further Reading

- [MassTransit Documentation](https://masstransit.io/documentation/concepts)
- [MassTransit Retry Configuration](https://masstransit.io/documentation/concepts/exceptions)
- [Automatonymous State Machines](https://masstransit.io/documentation/patterns/saga/state-machine)
- [Transactional Outbox](https://masstransit.io/documentation/patterns/transactional-outbox)
- [Testing with MassTransit](https://masstransit.io/documentation/concepts/testing)

---

> 🍔 **Happy hacking!** Start with retries (Tier 2), graduate to sagas (Tier 4), and finish with the outbox and tests (Tier 5). Refer back to [ACHIEVEMENTS.md](ACHIEVEMENTS.md) to check off your progress.
