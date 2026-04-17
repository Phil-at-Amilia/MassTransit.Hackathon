# 🚌 MassTransit Hackathon

> **4-hour sandbox** for learning [MassTransit](https://masstransit.io/) with RabbitMQ on .NET 8.

---

## 📋 Table of Contents

1. [Prerequisites](#prerequisites)
2. [Project Structure](#project-structure)
3. [Architecture Overview](#architecture-overview)
4. [Quick Start](#quick-start)
5. [Live Status Dashboard](#live-status-dashboard)
6. [RabbitMQ Management UI](#rabbitmq-management-ui)
7. [Key Concepts](#key-concepts)
8. [Hackathon Challenges](#hackathon-challenges)

---

## Prerequisites

| Tool | Version | Install |
|------|---------|---------|
| [.NET SDK](https://dotnet.microsoft.com/download) | 8.0+ | `winget install Microsoft.DotNet.SDK.8` / [download](https://dotnet.microsoft.com/download) |
| [Docker Desktop](https://www.docker.com/products/docker-desktop/) | any recent | [download](https://www.docker.com/products/docker-desktop/) |
| `curl` + `jq` | any | pre-installed on macOS/Linux; Windows: `winget install jqlang.jq` |

---

## Project Structure

```
MassTransit.Hackathon/
├── docker-compose.yml              # Spins up RabbitMQ (AMQP + Management UI)
├── status.sh                       # Live status dashboard (see below)
└── MassTransit.Hackathon/
    ├── MassTransit.Hackathon.csproj   # .NET 8 console app
    └── Program.cs                     # Everything in one file:
                                       #   • IHelloMessage  (message contract)
                                       #   • HelloConsumer  (processes messages)
                                       #   • HelloPublisherWorker (publishes every 5 s)
```

---

## Architecture Overview

```
┌─────────────────────────────────────────────────────────────┐
│                    .NET 8 Console App                       │
│                                                             │
│  ┌─────────────────────────┐   ┌────────────────────────┐   │
│  │  HelloPublisherWorker   │   │     HelloConsumer      │   │
│  │  (BackgroundService)    │   │  (IConsumer<IHello…>)  │   │
│  │                         │   │                        │   │
│  │  Publish every 5 s ──►──┼───┼──► Log received msg    │   │
│  └─────────────────────────┘   └────────────────────────┘   │
│              │                            ▲                 │
│              │        MassTransit Bus     │                 │
└──────────────┼────────────────────────────┼─────────────────┘
               │                            │
               ▼                            │
┌─────────────────────────────────────────────────────────────┐
│                  RabbitMQ (Docker)                          │
│                                                             │
│  Exchange: MassTransit.Hackathon:IHelloMessage (fanout)     │
│  Queue:    hello-consumer                                   │
│                                                             │
│  AMQP  → localhost:5672                                     │
│  UI    → localhost:15672  (guest / guest)                   │
└─────────────────────────────────────────────────────────────┘
``` 

---

## Quick Start

### 1 — Start RabbitMQ

```bash
docker-compose up -d
```

Wait ~5 seconds for RabbitMQ to finish booting, then verify:

```bash
docker ps          # should show "rabbitmq" container as "Up"
```

### 2 — Run the application

```bash
cd MassTransit.Hackathon
dotnet run
```

You should see output like:

```
[Publisher] Published IHelloMessage at 2024-01-15T10:00:02.000Z
[Consumer]  Received → "Hello from MassTransit!" (sent at 2024-01-15T10:00:02.000Z)
[Publisher] Published IHelloMessage at 2024-01-15T10:00:07.000Z
[Consumer]  Received → "Hello from MassTransit!" (sent at 2024-01-15T10:00:07.000Z)
```

### 3 — Stop everything

Press **Ctrl+C** to stop the app (graceful shutdown).

```bash
docker-compose down    # stop & remove the RabbitMQ container
```

---

## Live Status Dashboard

Run the included status script for a real-time ASCII overview of your RabbitMQ broker:

```bash
# Make it executable (first time only — macOS/Linux)
chmod +x status.sh

# Show live status
./status.sh

# Auto-refresh every 5 seconds
watch -n 5 ./status.sh
```

> **Windows (PowerShell):** install [Git Bash](https://gitforwindows.org/) or [WSL](https://learn.microsoft.com/en-us/windows/wsl/install) and run the script from there.

Sample output:

```
╔══════════════════════════════════════════════════════════╗
║   🐇  RabbitMQ  ×  🚌  MassTransit  —  Status Board     ║
╚══════════════════════════════════════════════════════════╝

  (\ /)    Broker: rabbit@<hostname>
  ( •.•)   Version: 3.x.x   Erlang: xx.x
  o(  ")   Uptime: X minutes

────────────────────────────── OVERVIEW ─────────────────────────────
  Connections : 1     Channels  : 1
  Exchanges   : 8     Queues    : 1
  Messages    : 0 ready  /  0 unacked

──────────────────────────── QUEUES ─────────────────────────────────
  NAME                         READY  UNACKED  CONSUMERS  STATE
  hello-consumer                   0        0          1  running

──────────────────────────── CONSUMERS ──────────────────────────────
  hello-consumer  →  1 active consumer(s)

──────────────────────────── EXCHANGES ──────────────────────────────
  MassTransit.Hackathon:IHelloMessage   fanout   D  messages-in: X
```

---

## RabbitMQ Management UI

Open **[http://localhost:15672](http://localhost:15672)** in your browser.

| Field    | Value   |
|----------|---------|
| Username | `guest` |
| Password | `guest` |

**Useful tabs:**
- **Overview** — broker-level message rates
- **Queues** — drill into message counts, consumers, and bindings
- **Exchanges** — see the fanout exchange MassTransit created for `IHelloMessage`
- **Connections** — confirm your app is connected

---

## Key Concepts

| Concept | What it does in this project |
|---------|------------------------------|
| **`IHelloMessage`** | The *message contract* — defines the data shape that travels on the bus |
| **`HelloConsumer`** | Implements `IConsumer<T>` — MassTransit calls `Consume()` for every message |
| **`HelloPublisherWorker`** | A `BackgroundService` that calls `IPublishEndpoint.Publish<T>()` every 5 s |
| **`AddMassTransit`** | Registers the bus + consumers in the .NET DI container |
| **`ConfigureEndpoints`** | Auto-creates a queue and binding for every registered consumer |
| **fanout exchange** | RabbitMQ exchange type MassTransit uses for `Publish<T>()` — all bound queues receive every message |

> 📖 Ready for more? Check out [MASSTRANSIT-102.md](MASSTRANSIT-102.md) for **retries, circuit breakers, sagas, transactional outbox, and testing harnesses**.

---

## Hackathon Challenges

Work through these in order — each one builds on the last:

1. **Add a second message type** — create `IFarewellMessage` and a `FarewellConsumer`.
2. **Add message data** — extend `IHelloMessage` with a `CorrelationId` (Guid) and log it in the consumer.
3. **Use a Saga** — track the lifecycle of a message with a `MassTransitStateMachine`.
4. **Add a Fault consumer** — create a `IConsumer<Fault<IHelloMessage>>` to handle processing errors.
5. **Request/Response** — replace fire-and-forget publish with `IRequestClient<T>` and a responding consumer.
6. **Scheduled messages** — use `context.SchedulePublish` to delay a message by 30 seconds.

