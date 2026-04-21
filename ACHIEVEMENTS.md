# 🚀 MassTransit Mini-Hackathon: Achievement Board

**Team Name / Dev Name:** _______________________

---

### Step 0: Mise en Place 🍳

Before the kitchen opens, get your station ready.

- [ ] **1. Light the Burners:** Boot the message broker — `docker-compose up -d` (from the repo root)
- [ ] **2. Open the Pass:** Start the TUI dashboard — `cd MassTransit.Hackathon.Dashboard && dotnet run`
- [ ] **3. Inspect the Pantry:** Verify the broker is alive — navigate to [http://localhost:15672](http://localhost:15672) (login: `guest` / `guest`)
- [ ] **4. Read the Recipe Book:** Quick walkthrough before we start — open and read [MASSTRANSIT-101.md](MASSTRANSIT-101.md)
- [ ] **5. Grab the Chef's Notes:** Advanced reference for Tier 2+ — skim [MASSTRANSIT-102.md](MASSTRANSIT-102.md), come back when needed

> ✅ Once all five boxes are checked, you're ready to start cooking!

---

### Tier 1: The Opening Shift (Getting Connected)
- [ ] **1. First Call to the Kitchen:** Connect to broker, publish `PingMessage`, and consume it.
- [ ] **2. The Recipe Card:** Refactor message contract to a C# `interface`.
- [ ] **3. Order Up!:** Implement Request/Response pattern and print response.
- [ ] **4. All Stations Ready:** Create 2 distinct consumers for 1 message interface. Prove both fire.

### Tier 2: Kitchen Disasters (Resilience)
- [ ] **5. A Minor Slip:** Add an Immediate(3) Retry policy. Throw exception, watch retries.
- [ ] **6. Spoiled Goods:** Hardcode a failure. Prove message lands in `_error` queue.
- [ ] **7. Rescuing the Dish:** Use RabbitMQ UI to manually move message from `_error` back to main.
- [ ] **8. Rest the Dough:** Configure Delayed Retry. Watch message wait before retrying.
- [ ] **9. The Kitchen Fire:** Implement Circuit Breaker. Fail 5 times rapidly, observe instant 6th failure.
- [ ] **10. Power Cut:** Stop consumer, publish 10 msgs, restart consumer. Prove 0 messages lost.

### Tier 3: Rush Hour (Handling the Load)
- [ ] **11. Extra Kitchen Staff:** Run 3 consumer instances. Publish 50 msgs. Observe round-robin load balancing.
- [ ] **12. The Head Chef:** Use `UseConcurrencyLimit(2)`. Prove max 2 concurrent processing.
- [ ] **13. Batch Order:** Create a Batch Consumer (wait for 10 msgs OR 5 seconds).

### Tier 4: Full Service (Sagas & Coordination)
- [ ] **14. The Kitchen Manager:** Create an Automatonymous Saga with `Initial`, `Active`, `Final` states.
- [ ] **15. Linking the Orders:** Map 2 different events to the Saga via `CorrelationId`.
- [ ] **16. Last Call:** Add a 30-second timeout that moves state to `Cancelled`.

### Tier 5: The Chef's Table (Observability & Production Readiness)
- [ ] **17. The Expeditor's Eye:** Log and trace `ConversationId` from publisher to consumer.
- [ ] **18. Quality Control:** Write a custom Filter to log execution time.
- [ ] **19. Test Kitchen:** Write 1 Unit Test using `MassTransitTestHarness`.
- [ ] **20. The Final Check (Head Chef):** Implement EF Core Transactional Outbox pattern.