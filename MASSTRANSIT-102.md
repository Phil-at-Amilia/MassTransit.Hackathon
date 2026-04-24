# MassTransit 102 — Resilience & Throughput

> When things go wrong — and they will — you need your kitchen to keep serving. This guide covers the **Tier 2 & 3** resilience and throughput tools: retry policies, the error queue, circuit breakers, concurrency control, and batch consumers.

---

## Table of Contents

1. [Immediate Retry](#1-immediate-retry)
2. [The Error Queue & Moving Messages](#2-the-error-queue--moving-messages)
3. [Delayed Retry](#3-delayed-retry)
4. [Circuit Breaker](#4-circuit-breaker)
5. [Concurrency Limit](#5-concurrency-limit)
6. [Batch Consumer](#6-batch-consumer)
7. [Durable Queues & Message Persistence](#7-durable-queues--message-persistence)

---

## 1. Immediate Retry

> 🏆 **Achievement:** *#5 A Minor Slip* (Tier 2 — Kitchen Disasters)

### Why?

In a real kitchen, the bartender might drop a glass — you don't shut down the bar. You try again. Transient failures (database timeouts, network blips, optimistic lock conflicts) are normal in distributed systems. **Immediate retry** lets MassTransit automatically re-attempt a failed `Consume()` call before giving up.

### How It Works

```
Message arrives
  └─► Consumer.Consume()  ❌ throws
  └─► Consumer.Consume()  ❌ throws  (retry 1)
  └─► Consumer.Consume()  ❌ throws  (retry 2)
  └─► Consumer.Consume()  ✅ succeeds (retry 3)
```

Retries happen **in-process** — RabbitMQ doesn't see them. The message stays in memory and is re-delivered to the consumer directly.

**One-liner:** `Immediate(3)` means "try up to 4 times total (1 original + 3 retries) with no delay between them."

### Kitchen Example

The line cook's grill occasionally flames out. Three quick retries before the order hits the bin.

```csharp
// Program.cs
x.UsingRabbitMq((context, cfg) =>
{
    cfg.Host("localhost", "/", h =>
    {
        h.Username("guest");
        h.Password("guest");
    });

    cfg.ReceiveEndpoint("line-cook-consumer", e =>
    {
        // 🔁 Retry up to 3 times immediately (no delay)
        e.UseMessageRetry(r => r.Immediate(3));

        e.ConfigureConsumer<LineCookConsumer>(context);
    });

    cfg.ConfigureEndpoints(context);
});
```

To **see it in action**, add a random failure to `LineCookConsumer.Consume()`:

```csharp
public async Task Consume(ConsumeContext<IOrderMessage> context)
{
    // 🔥 50% chance the grill flames out
    if (Random.Shared.Next(2) == 0)
        throw new Exception("Grill flamed out! Retrying...");

    _logger.LogInformation("[LineCook] Cooked [{OrderId}] {Item}",
        context.Message.OrderId, context.Message.Item);
}
```

Watch the console — you'll see the same `OrderId` logged in retries before it finally succeeds or exhausts all attempts.

### Filtering Exceptions

Retry only specific exception types to avoid retrying bad input:

```csharp
e.UseMessageRetry(r => r.Immediate(3)
    .Handle<TimeoutException>()    // only retry timeouts
    .Ignore<ArgumentException>()   // never retry bad input
);
```

### Key Points

- `Immediate(3)` = 3 retries. Total attempts = 4.
- Retries are **in-process** — no RabbitMQ round-trip, no message re-queuing.
- When all retries are exhausted, the message moves to the **`_error` queue** (see §2).
- Place `UseMessageRetry` **before** any other middleware on the endpoint.

---

## 2. The Error Queue & Moving Messages

> 🏆 **Achievements:** *#6 Spoiled Goods* · *#7 Rescuing the Dish* (Tier 2 — Kitchen Disasters)

### The `_error` Queue

When a message exhausts all retries (or throws with no retry policy), MassTransit **moves it to an error queue**: `<queue-name>_error`.

```
line-cook-consumer  →  all retries failed  →  line-cook-consumer_error
```

The error queue is automatically created by MassTransit. Messages here include the **original payload plus fault metadata** (exception type, message, stack trace).

**Kitchen analogy:** The spoiled ingredients go to the waste bin — clearly labeled with what went wrong and when.

### Finding It in the RabbitMQ UI

1. Open [http://localhost:15672](http://localhost:15672) → **Queues** tab.
2. Look for `line-cook-consumer_error` (or `bartender-consumer_error`, etc.).
3. Click on it → **Get messages** → set Count to 1 → click **Get Message(s)**.
4. You'll see the full JSON payload and headers including `x-first-death-reason` with the exception details.

> 💡 Messages in `_error` are **safe** — they won't be consumed again until you take action. Use this as your audit log for failures.

### Moving Messages Back (Manual Rescue)

To re-process a message from the error queue:

**Option 1 — RabbitMQ UI (one message at a time):**

1. Queue → **Get messages** → copy the raw payload.
2. Go to the **Exchanges** tab → find the target exchange (e.g. `line-cook-consumer`).
3. **Publish message** → paste the payload → click **Publish**.

**Option 2 — RabbitMQ UI "Move messages" plugin (if enabled):**

1. Go to the `_error` queue → click **Move messages**.
2. Enter the destination queue name → **Move**.

> 💡 The Shovel or Shovel Management plugin can automate this for production. For the hackathon, the manual UI approach is fine.

### Key Points

- Every consumer gets its own `_error` queue: `<consumer-name>_error`.
- Error queues are created automatically — you don't configure them.
- Messages in `_error` include fault metadata headers (`x-exception-type`, `x-exception-message`).
- Moving a message back re-queues it for a fresh delivery — retries start over.

---

## 3. Delayed Retry

> 🏆 **Achievement:** *#8 Rest the Dough* (Tier 2 — Kitchen Disasters)

### Why?

Immediate retries make sense for a brief flare-up. But if the bartender ran out of ice, retrying instantly 3 times won't help — the ice machine needs time to cycle. A **delayed retry** inserts a pause between attempts, giving the system time to recover.

### Retry Types at a Glance

| Policy | Behavior | Best for |
|--------|----------|----------|
| **Immediate** | N retries, no delay | Quick transient errors (optimistic lock, brief network hiccup) |
| **Interval** | N retries, fixed delay between each | Known recovery time (e.g. wait 2 s for a connection reset) |
| **Incremental** | N retries, delay increases by a fixed step | Gradual back-off with a predictable cap |
| **Exponential** | N retries, delay doubles each time | External service rate-limiting or unknown recovery time |

### Kitchen Example

The bartender is out of ice. Wait 2 seconds between each of 3 attempts.

```csharp
cfg.ReceiveEndpoint("bartender-consumer", e =>
{
    // ⏳ 3 retries, 2-second pause between each
    e.UseMessageRetry(r => r.Interval(3, TimeSpan.FromSeconds(2)));

    e.ConfigureConsumer<BartenderConsumer>(context);
});
```

For a gradually increasing wait (Incremental):

```csharp
// 1st retry after 1s, 2nd after 2s, 3rd after 3s
e.UseMessageRetry(r => r.Incremental(3,
    initialInterval: TimeSpan.FromSeconds(1),
    intervalIncrement: TimeSpan.FromSeconds(1)));
```

For exponential back-off (doubles each time):

```csharp
// 1st retry after 1s, 2nd after 2s, 3rd after 4s
e.UseMessageRetry(r => r.Exponential(3,
    minInterval: TimeSpan.FromSeconds(1),
    maxInterval: TimeSpan.FromMinutes(1),
    intervalDelta: TimeSpan.FromSeconds(1)));
```

### Key Points

- All retry types are **in-process** — the message stays in memory during the wait.
- Delayed retries still exhaust to the `_error` queue on final failure.
- For very long delays (minutes to hours), use **`UseScheduledRedelivery`** instead — it re-queues via the broker rather than holding in memory.

---

## 4. Circuit Breaker

> 🏆 **Achievement:** *#9 The Kitchen Fire* (Tier 2 — Kitchen Disasters)

### Why?

If the grill is completely broken, there's no point routing every burger order into a 3-retry loop that always fails. A **circuit breaker** detects sustained failure and **short-circuits** — instantly moving new messages to the error queue for a cooldown period. This protects downstream systems and lets the kitchen recover.

### How It Works

```
 ┌─────────┐   failures    ┌──────────┐   cooldown    ┌───────────┐
 │  Closed  │ ──────────►  │   Open   │ ──────────►   │ Half-Open │
 │ (normal) │  hit limit   │ (reject) │  timer done   │  (probe)  │
 └─────────┘               └──────────┘               └───────────┘
      ▲                                                     │
      │              success on probe message               │
      └─────────────────────────────────────────────────────┘
```

| State | What happens |
|-------|-------------|
| **Closed** | Messages flow normally. Failures are counted. |
| **Open** | All messages are **immediately** moved to `_error`. No consumer code runs. |
| **Half-Open** | One message is allowed through as a test. Success → back to Closed. Failure → back to Open. |

### Kitchen Example

The grill is toast. After 5% of messages fail within 30 seconds (minimum 5 messages), stop sending orders and let it cool down for 1 minute.

```csharp
cfg.ReceiveEndpoint("line-cook-consumer", e =>
{
    // 🔁 Inner: retry each message up to 3 times first
    e.UseMessageRetry(r => r.Immediate(3));

    // 🔌 Outer: if failure threshold is hit within 30 s, open the circuit for 1 minute
    e.UseCircuitBreaker(cb =>
    {
        cb.TrackingPeriod = TimeSpan.FromSeconds(30);  // sliding window
        cb.TripThreshold  = 5;   // failure rate % to trip
        cb.ActiveThreshold = 5;  // minimum messages before tripping can happen
        cb.ResetInterval  = TimeSpan.FromMinutes(1);   // how long to stay Open
    });

    e.ConfigureConsumer<LineCookConsumer>(context);
});
```

> 💡 `TripThreshold` is a **percentage** (5 = 5%). `ActiveThreshold` is the minimum number of messages that must have been processed in the tracking window before the circuit can trip — this prevents tripping on the very first failure.

### Retry + Circuit Breaker Ordering

The **order of middleware matters**. Think of wrapping layers — outermost runs first:

```
Message arrives
  └─► Circuit Breaker (outer) — short-circuits if open
        └─► Retry (inner) — retries before counting as a failure
              └─► Consumer.Consume() — your code
```

Always register retry **before** the circuit breaker in the pipeline (retry is registered first = it is the inner layer).

### Testing It (Achievement #9)

1. Hardcode `LineCookConsumer` to always `throw`.
2. Publish 6+ burger orders quickly.
3. First 5 fail after retries → circuit opens.
4. 6th message **instantly** lands in `_error` — no consumer code, no retries.
5. Check RabbitMQ UI: `line-cook-consumer_error` fills up instantly for message 6+.
6. Wait 1 minute → circuit moves to Half-Open → next message is probed.

---

## 5. Concurrency Limit

> 🏆 **Achievement:** *#12 The Head Chef* (Tier 3 — Rush Hour)

### Why?

By default, MassTransit processes messages **concurrently** — as fast as the broker delivers them. For some consumers, that's dangerous: a consumer that hits a shared database or a rate-limited API will thrash if 20 messages arrive at once. `UseConcurrencyLimit` caps how many messages a consumer processes at the same time.

**Kitchen analogy:** The head chef only has two hands. No matter how many tickets come in, they personally touch at most 2 dishes at once.

### How It Works

```
Queue: 10 messages waiting
  └─► ConcurrencyLimit(2)
        ├─► Consumer.Consume() [msg 1]  ← processing
        ├─► Consumer.Consume() [msg 2]  ← processing
        └─► msg 3..10 wait in queue     ← held back by prefetch
```

### Kitchen Example

```csharp
cfg.ReceiveEndpoint("line-cook-consumer", e =>
{
    // 👨‍🍳 Only 2 dishes on the pass at once
    e.UseConcurrencyLimit(2);

    e.ConfigureConsumer<LineCookConsumer>(context);
});
```

### Observing It (Achievement #12)

1. Add `await Task.Delay(2000)` inside `LineCookConsumer.Consume()` to simulate slow work.
2. Publish 10 messages.
3. Open the RabbitMQ UI → `line-cook-consumer` queue → watch **Unacked**.
4. It should stay at or below 2 — the rest sit in **Ready**.

> 💡 `UseConcurrencyLimit` works by controlling the **prefetch count** on the channel and using an internal semaphore. The broker delivers only as many messages as the consumer is ready to handle.

### Key Points

- `UseConcurrencyLimit(1)` = sequential processing (no parallelism). Useful for ordered or idempotent-sensitive work.
- Can be combined with multiple consumer instances: 3 instances × `UseConcurrencyLimit(2)` = 6 concurrent max.
- Must be placed in the `ReceiveEndpoint` configuration, not at the bus level.

---

## 6. Batch Consumer

> 🏆 **Achievement:** *#13 Batch Order* (Tier 3 — Rush Hour)

### Why?

Processing messages one by one is fine for most cases. But some operations are much more efficient in bulk: a single `INSERT ... VALUES (...)` for 10 rows is faster than 10 individual inserts. A **batch consumer** accumulates messages and processes them as a group.

**Kitchen analogy:** The line cook waits at the pass until there are 10 tickets, or until 5 seconds have passed — whichever comes first. Then they call "fire" on all of them at once.

### How It Works

```
Messages arrive one by one
  └─► Batch buffer
        ├─► 10 messages collected?  → fire batch immediately
        └─► 5 seconds elapsed?      → fire whatever is buffered
```

### Implementing the Batch Consumer

```csharp
// Consumers/BatchLineCookConsumer.cs
using MassTransit;

public class BatchLineCookConsumer : IConsumer<Batch<IOrderMessage>>
{
    private readonly ILogger<BatchLineCookConsumer> _logger;

    public BatchLineCookConsumer(ILogger<BatchLineCookConsumer> logger) => _logger = logger;

    public Task Consume(ConsumeContext<Batch<IOrderMessage>> context)
    {
        _logger.LogInformation("[BatchLineCook] Firing batch of {Count} orders",
            context.Message.Length);

        foreach (var msg in context.Message)
        {
            _logger.LogInformation("  → [{OrderId}] {Item}",
                msg.Message.OrderId, msg.Message.Item);
        }

        return Task.CompletedTask;
    }
}
```

### Registering the Batch Consumer

```csharp
// Program.cs
x.AddConsumer<BatchLineCookConsumer>(c =>
{
    // 🍽️ Fire when 10 messages OR 5 seconds — whichever comes first
    c.Options<BatchOptions>(o => o
        .SetMessageLimit(10)
        .SetTimeLimit(TimeSpan.FromSeconds(5)));
});
```

> 💡 `SetMessageLimit` and `SetTimeLimit` work as **OR** conditions — the batch fires as soon as either threshold is hit.

### What You'll See

Publish 25 messages quickly, then wait:

```
[BatchLineCook] Firing batch of 10 orders
  → [id-01] Burger
  → [id-02] Fries
  ...
[BatchLineCook] Firing batch of 10 orders
  ...
[BatchLineCook] Firing batch of 5 orders   ← time limit fired, only 5 left
```

### Key Points

- `context.Message` is a `Batch<T>` — iterate it with a `foreach`, access each item via `.Message`.
- Batches are **all-or-nothing**: if the batch consumer throws, all messages in the batch are faulted.
- Combine with `UseConcurrencyLimit(1)` to prevent overlapping batch processing.
- Tune `SetMessageLimit` and `SetTimeLimit` based on your latency vs. throughput trade-off.

---

---

## 7. Durable Queues & Message Persistence

### The Problem: Messages Disappearing on Server Restart

RabbitMQ supports two kinds of queues:

| Property | Value | Effect |
|----------|-------|--------|
| **Durable** | `true` | The queue definition (name, bindings, arguments) **survives a broker restart** |
| **Durable** | `false` | The queue is **wiped on restart** — all waiting messages are lost |
| **AutoDelete** | `true` | The queue is **deleted the moment the last consumer disconnects** |
| **AutoDelete** | `false` | The queue persists independently of connected consumers |

> ⚠️ **The trap:** A durable queue still loses its messages if those messages were not published as **persistent**. Both the queue and the messages must be configured correctly to survive a restart.

**Kitchen analogy:** `Durable = false` is like writing a ticket on a napkin — if the kitchen closes for the night (server reboot), the napkin is thrown away. `Durable = true` + persistent messages is a printed ticket in a fireproof order book.

---

### MassTransit Defaults

When MassTransit auto-configures endpoints via `cfg.ConfigureEndpoints(context)`, it creates queues with:

- `Durable = true` — queue definition survives restarts ✅
- `AutoDelete = false` — queue is not deleted on consumer disconnect ✅
- Messages published as **persistent** (`DeliveryMode = 2`) ✅

These are safe defaults. **The risk arises when you override them explicitly** — for example, to create lightweight queues for local development or testing:

```csharp
// ⚠️ DO NOT use this in production — queue is deleted when consumers disconnect
cfg.ReceiveEndpoint("line-cook-consumer", e =>
{
    e.AutoDelete = true;   // ← queue purged on server reboot / last consumer disconnect
    e.Durable = false;     // ← queue definition lost on broker restart
    e.ConfigureConsumer<LineCookConsumer>(context);
});
```

---

### How to Configure a Persisted Queue

Always set both properties explicitly on any production `ReceiveEndpoint` to make your intent clear and prevent accidental misconfiguration:

```csharp
cfg.ReceiveEndpoint("line-cook-consumer", e =>
{
    // ✅ Queue survives broker restart AND consumer disconnects
    e.Durable    = true;
    e.AutoDelete = false;

    e.ConfigureConsumer<LineCookConsumer>(context);
});
```

If you rely on `ConfigureEndpoints`, the defaults are already safe — but adding explicit configuration documents the intent:

```csharp
// Program.cs — explicit durability for every endpoint
x.UsingRabbitMq((context, cfg) =>
{
    cfg.Host("localhost", "/", h =>
    {
        h.Username("guest");
        h.Password("guest");
    });

    // Apply safe durability defaults to ALL auto-configured endpoints
    cfg.ConfigureEndpoints(context, new DefaultEndpointNameFormatter("", false));

    // Or configure individually:
    cfg.ReceiveEndpoint("line-cook-consumer", e =>
    {
        e.Durable    = true;   // ✅ survive broker restart
        e.AutoDelete = false;  // ✅ survive consumer disconnect

        e.ConfigureConsumer<LineCookConsumer>(context);
    });
});
```

---

### Message Persistence vs. Queue Durability

A durable queue is a necessary but not sufficient condition for surviving a restart. Messages must also be published as **persistent**.

MassTransit sets `DeliveryMode.Persistent` by default. If you ever publish directly via the RabbitMQ client or another library, make sure to set `IBasicProperties.Persistent = true` (or `DeliveryMode = 2`):

```
Queue durable = true  +  Message persistent = true  →  Messages survive restart ✅
Queue durable = true  +  Message persistent = false →  Messages lost on restart ⚠️
Queue durable = false +  Message persistent = true  →  Queue (and messages) lost ⚠️
```

> 💡 MassTransit handles message persistence automatically — you only need to worry about this if you're mixing MassTransit with direct AMQP code.

---

### Verifying Queue Durability in the RabbitMQ UI

1. Open [http://localhost:15672](http://localhost:15672) → **Queues** tab.
2. Find your queue (e.g., `line-cook-consumer`).
3. Check the **Features** column:
   - **D** = Durable ✅
   - **AD** = AutoDelete ⚠️ (will be purged on disconnect)
   - **Excl** = Exclusive (single-connection only, also lost on disconnect)
4. Click on the queue → scroll to **Overview** — the `durable` field should be `true`.

> 💡 After changing `Durable` or `AutoDelete`, you must **delete the existing queue** in the UI before restarting the app. RabbitMQ will refuse to re-declare a queue with different properties and the consumer will fail to start with a `406 PRECONDITION_FAILED` error.

---

### Key Points

- `Durable = true` + `AutoDelete = false` = queue and messages survive server reboots and consumer restarts.
- MassTransit's **default settings are safe** — only override them intentionally.
- Always verify in the RabbitMQ UI that your production queues show the **D** (durable) feature flag.
- If you change durability settings on an existing queue, delete the old queue first to avoid a `PRECONDITION_FAILED` mismatch error.
- For the `_error` queue: MassTransit also creates it as durable, so failed messages are safe across restarts.

---

## Achievement Cheat Sheet

| Achievement | Concept | Section |
|-------------|---------|---------|
| **#5** A Minor Slip | Immediate Retry | [§1 Immediate Retry](#1-immediate-retry) |
| **#6** Spoiled Goods | Error Queue | [§2 The Error Queue](#2-the-error-queue--moving-messages) |
| **#7** Rescuing the Dish | Move from `_error` | [§2 The Error Queue](#2-the-error-queue--moving-messages) |
| **#8** Rest the Dough | Delayed Retry | [§3 Delayed Retry](#3-delayed-retry) |
| **#9** The Kitchen Fire | Circuit Breaker | [§4 Circuit Breaker](#4-circuit-breaker) |
| **#12** The Head Chef | Concurrency Limit | [§5 Concurrency Limit](#5-concurrency-limit) |
| **#13** Batch Order | Batch Consumer | [§6 Batch Consumer](#6-batch-consumer) |
| — | Durable Queues | [§7 Durable Queues & Message Persistence](#7-durable-queues--message-persistence) |
