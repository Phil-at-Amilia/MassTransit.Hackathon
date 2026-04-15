# MassTransit 101 — How It Really Works

> A practical explanation of what happens in the code and what you see in the RabbitMQ UI.

---

## The Big Picture

Think of **RabbitMQ** as a restaurant pass-through window. The waiter takes orders and calls them out; the kitchen staff each handle what's relevant to their role. **MassTransit** is the ticketing system — it handles all the routing, serialization, and retries so you only write business logic.

```
WaiterPublisherWorker
  │
  │  Publish<IOrderMessage>({ Item = OrderItem.Burger, PlacedAt = now })
  │
  ▼
[RabbitMQ Exchange]  MassTransit.Hackathon.Messages:IOrderMessage  (fanout)
  │
  │  fanout → all bound queues
  │
  ├──► [Queue]  bartender-consumer   →  BartenderConsumer.Consume()   (handles Soda only)
  ├──► [Queue]  line-cook-consumer   →  LineCookConsumer.Consume()    (handles Burger & Fries)
  └──► [Queue]  manager-consumer     →  ManagerConsumer.Consume()     (observes everything)
```

---

## The Three Code Pieces

### 1 — The Message Contract (`IOrderMessage`)

```csharp
public interface IOrderMessage
{
    OrderItem Item { get; }
    DateTime PlacedAt { get; }
}

public enum OrderItem { Burger, Fries, Soda }
```

This interface defines the **envelope shape** — what data travels on the wire. It has no behavior, just a schema.

**What MassTransit does with it:** it uses the fully-qualified type name as the name of a RabbitMQ **Exchange**.

> **RabbitMQ UI → Exchanges tab:** you'll see an exchange named  
> `MassTransit.Hackathon.Messages:IOrderMessage` of type **fanout**.

---

### 2 — Startup & Wiring (`Program.cs`)

```csharp
services.AddMassTransit(x =>
{
    x.AddConsumer<BartenderConsumer>();   // Handles Soda orders
    x.AddConsumer<LineCookConsumer>();    // Handles Burger & Fries orders
    x.AddConsumer<ManagerConsumer>();     // Observes all orders
    x.UsingRabbitMq((context, cfg) => {
        cfg.Host("localhost", ...);
        cfg.ConfigureEndpoints(context);  // Auto-create queues + bindings
    });
});
```

When the app **starts**, MassTransit connects to RabbitMQ and declares the topology:

1. Creates the **exchange** `MassTransit.Hackathon.Messages:IOrderMessage` (fanout)
2. Creates **queues** named `bartender-consumer`, `line-cook-consumer`, and `manager-consumer` (derived from consumer class names by convention)
3. **Binds** all three queues to the exchange

> **RabbitMQ UI — what appears on startup:**
> | Tab | What you see |
> |-----|-------------|
> | Connections | 1 connection from your app |
> | Channels | 1–2 channels (one for publishing, one for consuming) |
> | Exchanges | The new `IOrderMessage` fanout exchange |
> | Queues | `bartender-consumer`, `line-cook-consumer`, `manager-consumer` each with 1 consumer |

---

### 3 — Publishing (`WaiterPublisherWorker`)

```csharp
await _publishEndpoint.Publish<IOrderMessage>(
    new { Item = item, PlacedAt = DateTime.UtcNow });
```

`Publish<T>` tells MassTransit: *"broadcast this order to everyone who handles `IOrderMessage`"*.

Under the hood it:
1. Serializes the anonymous object into JSON (it satisfies `IOrderMessage` by shape)
2. Sends the JSON to the **`IOrderMessage` exchange** in RabbitMQ

Because the exchange is **fanout**, it immediately fans the order out to every bound queue — `bartender-consumer`, `line-cook-consumer`, and `manager-consumer` all receive a copy simultaneously. **No changes to the waiter needed** if you add more kitchen staff.

> **RabbitMQ UI → Exchanges → click `IOrderMessage`:** the "Bindings" section shows all three consumer queues bound. The message rate graph ticks with each order placed.

---

### 4 — Consuming (`BartenderConsumer`, `LineCookConsumer`, `ManagerConsumer`)

Each consumer receives every `IOrderMessage` from its own dedicated queue, but filters based on its role:

```csharp
// BartenderConsumer — only handles Soda
public Task Consume(ConsumeContext<IOrderMessage> context)
{
    if (context.Message.Item != OrderItem.Soda) return Task.CompletedTask;
    _logger.LogInformation("[Bartender] Poured {Item} (ordered at {PlacedAt:O})",
        context.Message.Item, context.Message.PlacedAt);
    return Task.CompletedTask;
}

// LineCookConsumer — handles Burger & Fries
public Task Consume(ConsumeContext<IOrderMessage> context)
{
    if (context.Message.Item == OrderItem.Soda) return Task.CompletedTask;
    _logger.LogInformation("[LineCook] Cooked {Item} (ordered at {PlacedAt:O})",
        context.Message.Item, context.Message.PlacedAt);
    return Task.CompletedTask;
}

// ManagerConsumer — observes everything
public Task Consume(ConsumeContext<IOrderMessage> context)
{
    _logger.LogInformation("[Manager] Saw order → {Item} (placed at {PlacedAt:O})",
        context.Message.Item, context.Message.PlacedAt);
    return Task.CompletedTask;
}
```

MassTransit keeps a long-lived channel **subscribed to each consumer's queue**. When a message lands:

1. RabbitMQ delivers it to the channel
2. MassTransit deserializes the JSON back into `IOrderMessage`
3. It resolves the consumer from DI and calls `Consume()`
4. If `Consume()` returns **without throwing** → MassTransit **acks** the message (tells RabbitMQ: done, remove it)
5. If it **throws** → MassTransit **nacks** it, and RabbitMQ can redeliver or dead-letter it

> **RabbitMQ UI → Queues → e.g. `bartender-consumer`:**
> | Column | Meaning |
> |--------|---------|
> | Ready | Messages sitting unprocessed (stays near 0 — consumers are fast) |
> | Unacked | Messages being processed right now (briefly 1) |
> | Consumers | 1 (your running app) |

---

## Exchange Types and Alternatives to Fanout

RabbitMQ supports four exchange types. MassTransit chooses **fanout** for `Publish<T>`, but understanding the others helps you know *why*.

### The Four RabbitMQ Exchange Types

| Exchange Type | Routing Logic | RabbitMQ UI label |
|---|---|---|
| **fanout** | Delivers to **all** bound queues, no conditions | `fanout` |
| **direct** | Delivers to queues whose binding key **exactly matches** the routing key | `direct` |
| **topic** | Delivers to queues whose binding key **pattern-matches** the routing key (`*` = one word, `#` = zero or more) | `topic` |
| **headers** | Delivers based on **message header attributes** instead of a routing key | `headers` |

### Why MassTransit Uses Fanout

When you call `Publish<IOrderMessage>()`, MassTransit's goal is **broadcast**: every service that cares about `IOrderMessage` should get a copy independently. Fanout achieves this with zero routing logic — every bound queue gets the message, period.

```
Exchange: IOrderMessage (fanout)
  ├──► queue: bartender-consumer    (BartenderConsumer — handles Soda)
  ├──► queue: line-cook-consumer    (LineCookConsumer  — handles Burger & Fries)
  └──► queue: manager-consumer      (ManagerConsumer   — observes all orders)
```

Each consumer gets its **own queue** and processes at its own pace — they are fully decoupled from each other and from the waiter.

> In the RabbitMQ UI → **Exchanges → `IOrderMessage` → Bindings**: every time you register a new `IConsumer<IOrderMessage>` and run the app, a new queue appears in that list automatically (thanks to `ConfigureEndpoints`).

### What MassTransit Actually Creates (Two Exchanges)

When you look at the Exchanges tab you'll notice MassTransit creates **two** exchanges per message type, not one:

```
MassTransit.Hackathon.Messages:IOrderMessage   (fanout)   ← publish target
bartender-consumer                             (fanout)   ← per-queue exchange
line-cook-consumer                             (fanout)   ← per-queue exchange
manager-consumer                               (fanout)   ← per-queue exchange
```

The per-queue exchange acts as an indirection layer. This allows MassTransit to send a message **directly to one specific consumer** (via `Send`) while still supporting the full publish/subscribe topology. You don't need to manage either exchange manually.

---

## Publish vs. Send — Key Distinction

| | `Publish<T>` | `Send` |
|--|--|--|
| **Routes to** | Exchange → all bound queues | Specific queue directly |
| **Coupling** | Publisher doesn't know who's listening | Publisher must know the queue name |
| **Exchange used** | The message-type fanout exchange | The per-queue exchange (or queue directly) |
| **Use when** | Broadcasting events | Commanding a specific endpoint |

```csharp
// Publish — broadcast the order to all kitchen staff
await _publishEndpoint.Publish<IOrderMessage>(new { Item = item, PlacedAt = DateTime.UtcNow });

// Send — targeted directly to one specific station
await _sendEndpoint.Send<IOrderMessage>(new { Item = item, PlacedAt = DateTime.UtcNow },
    new Uri("queue:bartender-consumer"));
```

MassTransit's `ConfigureEndpoints` auto-creates and wires queues for every registered consumer, so you never manage bindings manually when using `Publish`.

---

## What the RabbitMQ UI Tabs Actually Show

| UI Tab | What you're seeing |
|--------|--------------------|
| **Overview** | Total message rates across the whole broker |
| **Connections** | Each `dotnet run` = 1 TCP connection |
| **Channels** | Lightweight virtual connections within 1 TCP connection. MassTransit uses one per operation type |
| **Exchanges** | The fanout exchange MassTransit created for `IOrderMessage` |
| **Queues & Streams** | `bartender-consumer`, `line-cook-consumer`, `manager-consumer` — orders land here, consumers drain them |

---

## Lifecycle of One Message (End to End)

```
1. App starts
   └─ MassTransit connects → declares exchange + queues + bindings
      (bartender-consumer, line-cook-consumer, manager-consumer)

2. WaiterPublisherWorker (every N seconds)
   └─ Publish<IOrderMessage>()
      └─ MassTransit serializes to JSON
         └─ JSON sent to RabbitMQ exchange "IOrderMessage"
            └─ Exchange fans out to all three consumer queues

3. BartenderConsumer / LineCookConsumer / ManagerConsumer (in parallel)
   └─ MassTransit receives from each queue
      └─ Deserializes JSON → IOrderMessage
         └─ Calls Consume()
            └─ On success: ACK → message deleted from queue
            └─ On exception: NACK → message requeued or dead-lettered
```
