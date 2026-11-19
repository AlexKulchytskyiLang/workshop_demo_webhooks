# Load results

Measured on: 24 logical cores, Microsoft Windows NT 10.0.26300.0, .NET 10.0.3, Docker Desktop, everything on one machine. Last update 2026-11-13 11:09 UTC. These are laptop numbers for the shape of the system, not a capacity claim.

<!-- section:latency -->
## Latency against the offered rate (ASR-03)

2 delivery workers, one receiver that answers at once, the production values of the re-send timers. Measured by the receiver: from the moment the event was committed in the platform to the moment it was in the receiver's inbox. The register's target is p95 of 5 s at 50 events per second.

| offered events per second | events | p50 | p95 | p99 | whole run | p95 within 5 s |
|---|---|---|---|---|---|---|
| 6 | 90 | 157 ms | 565 ms | 1142 ms | 15.7 s | yes |
| 10 | 135 | 170 ms | 261 ms | 396 ms | 14.4 s | yes |
| 15 | 180 | 171 ms | 296 ms | 491 ms | 13.6 s | yes |
| 51 | 300 | 785 ms | 25513 ms | 26094 ms | 30.8 s | no |

Where the time went at the highest rate (p50 / p95, from the platform's own records):

Slowest waits for the relay (seconds after the first event of the step: wait): 3.7 s: 12386 ms, row 561; 3.7 s: 12386 ms, row 562; 3.7 s: 12369 ms, row 563; 3.8 s: 12341 ms, row 564; 3.8 s: 12337 ms, row 565; 3.7 s: 12320 ms, row 560; 3.8 s: 12277 ms, row 566; 3.8 s: 12277 ms, row 567

| stage | p50 | p95 |
|---|---|---|
| committed, waiting for the relay | 209 ms | 12216 ms |
| published to confirmed fanned out (broker and fan-out) | 65 ms | 13265 ms |
| commit to delivery row (the two above) | 234 ms | 22559 ms |
| delivery row to first attempt (queue, workers, slots) | 150 ms | 13465 ms |
| one attempt, as the sender saw it (request to answer) | 9 ms | 27 ms |

Processor time used by each host during that step: Simulator 0.6 s, Api 0.5 s, Receiver.Api 1.4 s, Delivery 5.9 s. The hosts are mostly waiting, not computing: the wait is in the stages that go through the message broker, and one attempt itself takes milliseconds.

<!-- section:scale -->
## Throughput with 1, 2 and 3 delivery workers (ASR-05)

The receiver answers each request after 1 second, so the number of attempts a worker can have in flight (16) is the limit. 13 endpoints of one tenant, 20 invoices (60 events), so 780 deliveries each time. The growth is less than linear because the broker emulator's receive rate does not grow with more consumers (SPIKE-0); a real Service Bus namespace does not have that ceiling.

| workers | deliveries | time | deliveries per second | compared with one worker |
|---|---|---|---|---|
| 1 | 780 | 68.7 s | 11.3 | ×1.0 |
| 2 | 780 | 31.8 s | 24.5 | ×2.2 |
| 3 | 780 | 21.5 s | 36.3 | ×3.2 |

