# 🚀 MassTransit Mini-Hackathon: Achievement Board

**Team Name / Dev Name:** _______________________

---

### Step 0: Setup 🛠️

Before diving into the achievements, let's get everything up and running.

| # | What | How | ✅ |
|---|------|-----|----|
| 1 | **Start RabbitMQ** — Boot the message broker | `docker-compose up -d` (from the repo root) | ☐ |
| 2 | **Launch the Hackathon Dashboard** — Start the TUI dashboard | `cd MassTransit.Hackathon.Dashboard && dotnet run` | ☐ |
| 3 | **Open the RabbitMQ Management UI** — Verify the broker is alive | Navigate to [http://localhost:15672](http://localhost:15672) — login: `guest` / `guest` | ☐ |
| 4 | **Review the 101 page** — Quick walkthrough of concepts before we start | Open and read through [MASSTRANSIT-101.md](MASSTRANSIT-101.md) | ☐ |
| 5 | **Bookmark the 102 page** — Advanced concepts reference (retries, sagas, outbox, testing) | Skim [MASSTRANSIT-102.md](MASSTRANSIT-102.md) — come back when you hit Tier 2+ | ☐ |

> ✅ Once all five boxes are checked, you're ready to start hacking!

---

### Tier 1: The Groundwork (Getting Connected)
- [ ] **1. Hello, Is It Me:** Connect to broker, publish `PingMessage`, and consume it.
- [ ] **2. A Binding Contract:** Refactor message contract to a C# `interface`.
- [ ] **3. Two-Way Street:** Implement Request/Response pattern and print response.
- [ ] **4. The Fan-Out:** Create 2 distinct consumers for 1 message interface. Prove both fire.

### Tier 2: Resilience (Stop, Start, Pickup)
- [ ] **5. The Hiccup:** Add an Immediate(3) Retry policy. Throw exception, watch retries.
- [ ] **6. Toxic Waste:** Hardcode a failure. Prove message lands in `_error` queue.
- [ ] **7. Zombie Revival:** Use RabbitMQ UI to manually move message from `_error` back to main.
- [ ] **8. Take a Breather:** Configure Delayed Retry. Watch message wait before retrying.
- [ ] **9. Tripping the Breaker:** Implement Circuit Breaker. Fail 5 times rapidly, observe instant 6th failure.
- [ ] **10. The Outage:** Stop consumer, publish 10 msgs, restart consumer. Prove 0 messages lost.

### Tier 3: Scalability (Handling the Load)
- [ ] **11. The Clone War:** Run 3 consumer instances. Publish 50 msgs. Observe round-robin load balancing.
- [ ] **12. Traffic Cop:** Use `UseConcurrencyLimit(2)`. Prove max 2 concurrent processing.
- [ ] **13. Bulk Delivery:** Create a Batch Consumer (wait for 10 msgs OR 5 seconds).

### Tier 4: Sagas & Coordination
- [ ] **14. The State Master:** Create Automatonymous Saga with `Initial`, `Active`, `Final` states.
- [ ] **15. Connecting the Dots:** Map 2 different events to the Saga via `CorrelationId`.
- [ ] **16. The Ticking Clock:** Add a 30-second timeout that moves state to `Cancelled`.

### Tier 5: Observability & Production Readiness
- [ ] **17. X-Ray Vision:** Log and trace `ConversationId` from publisher to consumer.
- [ ] **18. The Interceptor:** Write a custom Filter to log execution time.
- [ ] **19. Safe in the Sandbox:** Write 1 Unit Test using `MassTransitTestHarness`.
- [ ] **20. The Outbox (Boss):** Implement EF Core Transactional Outbox pattern.