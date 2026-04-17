# MassTransit 101 — Presentation Guide

> A walkthrough for presenting RabbitMQ, MassTransit, and the Hackathon project.

---

## What Is RabbitMQ? (TLDR for Devs)

**RabbitMQ** is an open-source **message broker** — a middleman that receives, stores, and forwards messages between applications.

- Think of it as a **post office**: your app drops off a letter (message), RabbitMQ holds it in a mailbox (queue), and the recipient picks it up when ready.
- It speaks **AMQP** (Advanced Message Queuing Protocol) — a standard wire protocol for messaging.
- It gives you **durability** (messages survive broker restarts), **decoupling** (publishers and consumers don't need to know each other), and **buffering** (handles bursts of traffic).
- It comes with a **Management UI** on port `15672` to inspect everything in real time.

**One-liner:** RabbitMQ is the pipe between your services — it makes sure messages get from A to B reliably, even if B is temporarily down.

---

## What Is MassTransit? (TLDR for Devs)

**MassTransit** is an open-source **.NET library** that sits on top of message brokers (RabbitMQ, Azure Service Bus, Amazon SQS, etc.) and removes all the plumbing.

- Without MassTransit you'd write raw AMQP code: declare exchanges, bind queues, serialize JSON, handle acks/nacks, manage retries…
- With MassTransit you write **C# interfaces** for messages and **classes** for consumers — it handles the rest.
- It auto-creates the RabbitMQ topology (exchanges, queues, bindings) from your code at startup.
- Built-in support for **retries**, **circuit breakers**, **sagas**, **outbox patterns**, and **testing harnesses**.

**One-liner:** MassTransit is the "Entity Framework of messaging" — you write business logic, it talks to the broker for you.

---

## Message, Publisher, Consumer

These are the three core building blocks you'll work with.

### The Message

A message is a **plain data contract** — an interface or class that defines what travels on the wire.

```csharp
public interface IOrderMessage
{
    OrderItem Item { get; }
    DateTime PlacedAt { get; }
}

public enum OrderItem { Burger, Fries, Soda }
```

- No behavior, just a **schema** (the shape of the envelope).
- MassTransit uses the **fully-qualified type name** as the RabbitMQ exchange name (e.g. `MassTransit.Hackathon.Messages:IOrderMessage`).

### The Publisher

The publisher is the code that **sends messages** into the broker.

```csharp
await _publishEndpoint.Publish<IOrderMessage>(
    new { Item = item, PlacedAt = DateTime.UtcNow });
```

- The publisher doesn't know (or care) who will receive the message.
- MassTransit serializes the object to **JSON** and pushes it to the appropriate RabbitMQ **exchange**.

### The Consumer

The consumer is a class that **reacts to messages** when they arrive.

```csharp
public class ManagerConsumer : IConsumer<IOrderMessage>
{
    public Task Consume(ConsumeContext<IOrderMessage> context)
    {
        _logger.LogInformation("[Manager] Saw order → {Item}", context.Message.Item);
        return Task.CompletedTask;
    }
}
```

Under the hood:
1. MassTransit keeps a long-lived channel **subscribed** to the consumer's queue.
2. When a message arrives → it **deserializes** the JSON back into `IOrderMessage`.
3. It resolves the consumer from **DI** and calls `Consume()`.
4. If `Consume()` succeeds → MassTransit **ACKs** the message (RabbitMQ removes it).
5. If it throws → MassTransit **NACKs** it (RabbitMQ can redeliver or dead-letter it).

### How They Wire Together

```
Publisher (Waiter)
  │
  │  Publish<IOrderMessage>
  ▼
[Exchange]  IOrderMessage  (fanout)
  │
  ├──► [Queue] bartender-consumer  →  BartenderConsumer.Consume()
  ├──► [Queue] line-cook-consumer  →  LineCookConsumer.Consume()
  └──► [Queue] manager-consumer    →  ManagerConsumer.Consume()
```

All of this topology is **auto-created** by MassTransit at startup via `ConfigureEndpoints`.

---

## Fanout vs. Alternatives (Publish vs. Send)

### The Four RabbitMQ Exchange Types

| Exchange Type | Routing Logic | When to use |
|---|---|---|
| **fanout** | Delivers to **all** bound queues — no conditions | Broadcast events to every subscriber |
| **direct** | Delivers to queues whose binding key **exactly matches** the routing key | Route to a specific queue by key |
| **topic** | Delivers using **pattern matching** (`*` = one word, `#` = zero or more) | Flexible routing (e.g. `orders.food.*`) |
| **headers** | Routes based on **message header attributes** instead of routing key | Complex multi-attribute routing |

### Why MassTransit Defaults to Fanout

When you call `Publish<IOrderMessage>()`, the goal is **broadcast** — every service that cares about `IOrderMessage` gets a copy. Fanout does this with zero routing logic.

```
Exchange: IOrderMessage (fanout)
  ├──► queue: bartender-consumer    (handles Soda)
  ├──► queue: line-cook-consumer    (handles Burger & Fries)
  └──► queue: manager-consumer      (observes everything)
```

Add a new consumer? Just register it — MassTransit auto-binds a new queue on startup. The publisher doesn't change.

### What MassTransit Actually Creates

You'll notice **two layers** of exchanges in the RabbitMQ UI:

```
MassTransit.Hackathon.Messages:IOrderMessage   (fanout)   ← publish target
bartender-consumer                             (fanout)   ← per-queue exchange
line-cook-consumer                             (fanout)   ← per-queue exchange
manager-consumer                               (fanout)   ← per-queue exchange
```

The per-queue exchanges are an **indirection layer** that lets MassTransit `Send` a message directly to one specific consumer while still supporting full publish/subscribe.

### Publish vs. Send

| | `Publish<T>` | `Send` |
|--|--|--|
| **Semantics** | "Something happened" (event) | "Do this" (command) |
| **Routes to** | Exchange → **all** bound queues | One **specific** queue |
| **Coupling** | Publisher doesn't know who's listening | Publisher must know the queue address |
| **Exchange used** | The message-type fanout exchange | The per-queue exchange |
| **Use when** | Broadcasting events | Commanding a specific endpoint |

```csharp
// Publish — broadcast to all kitchen staff
await _publishEndpoint.Publish<IOrderMessage>(
    new { Item = item, PlacedAt = DateTime.UtcNow });

// Send — target one specific station
var endpoint = await _sendEndpointProvider.GetSendEndpoint(
    new Uri("queue:bartender-consumer"));
await endpoint.Send<IOrderMessage>(
    new { Item = item, PlacedAt = DateTime.UtcNow });
```

**Rule of thumb:** Use `Publish` for **events** ("OrderPlaced"), use `Send` for **commands** ("CookBurger").

---

## RabbitMQ Management UI

Open [http://localhost:15672](http://localhost:15672) — login: `guest` / `guest`.

### Overview

The **landing page** of the management UI.

- **Message rates chart** — global publish, deliver, and acknowledge rates across the entire broker.
- **Queued messages chart** — total ready + unacked messages across all queues.
- **Node info** — Erlang version, memory usage, disk space, uptime.
- **Ports and contexts** — which protocols are active (AMQP 5672, HTTP 15672).

> 💡 This is your "health dashboard" — if message rates flatline or queued messages spike, something's wrong.

### Connections

Each running application = **one TCP connection** to RabbitMQ.

| Column | Meaning |
|--------|---------|
| **Name** | Client IP and port |
| **User name** | The AMQP user (e.g. `guest`) |
| **State** | `running` = healthy |
| **Channels** | Number of channels inside this connection |
| **Send / Receive rate** | Bytes per second in each direction |

> 💡 When you `dotnet run` the hackathon app, you'll see **1 connection** appear here. Stop the app → it disappears.

### Channels

Channels are **lightweight virtual connections** multiplexed inside one TCP connection.

| Column | Meaning |
|--------|---------|
| **Channel** | Identifier (e.g. `connection:1 channel:1`) |
| **Consumer count** | How many queues this channel is subscribed to |
| **Prefetch** | How many unacked messages the channel can hold at once |
| **Unacked** | Messages currently being processed |

MassTransit typically opens **one channel per consumer** plus one for publishing.

> 💡 If you have 3 consumers, expect ~4 channels (3 consuming + 1 publishing).

### Exchanges

An exchange is the **routing layer** — it receives messages and routes them to queues based on its type and bindings.

| Column | Meaning |
|--------|---------|
| **Name** | Exchange name (MassTransit uses the full type name for message exchanges) |
| **Type** | `fanout`, `direct`, `topic`, or `headers` |
| **Bindings** | Click an exchange to see which queues (or other exchanges) it routes to |
| **Message rate in** | Messages per second arriving at this exchange |

What you'll see in this hackathon:
- `MassTransit.Hackathon.Messages:IOrderMessage` — the **fanout exchange** for order messages
- `bartender-consumer`, `line-cook-consumer`, `manager-consumer` — **per-queue exchanges** created by MassTransit

> 💡 Click on the `IOrderMessage` exchange → the **Bindings** section shows all consumer queues bound to it.

### Queues and Streams

Queues are where messages **wait to be consumed**. Each consumer gets its own queue.

| Column | Meaning |
|--------|---------|
| **Name** | Queue name (MassTransit derives it from the consumer class name) |
| **Type** | `classic` or `quorum` |
| **Ready** | Messages sitting in the queue, waiting to be picked up |
| **Unacked** | Messages delivered to a consumer but not yet acknowledged |
| **Total** | Ready + Unacked |
| **Consumers** | Number of consumer instances subscribed to this queue |
| **Message rate** | Incoming and delivery rates |

What you'll see:
- `bartender-consumer` — drains Soda orders
- `line-cook-consumer` — drains Burger & Fries orders
- `manager-consumer` — observes all orders

> 💡 **Ready** stays near 0 when consumers are healthy. If it climbs, consumers are falling behind. **Unacked** briefly shows 1 while a message is being processed.

Click on a queue to see:
- **Bindings** — which exchanges feed into this queue
- **Get messages** — peek at messages without consuming them (great for debugging!)
- **Consumers** — details about connected consumer instances
