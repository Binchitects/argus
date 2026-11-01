---
name: performance-investigation
title: Investigate performance
description: Find why something is slow — measure first, locate the bottleneck (CPU, memory, I/O, database, network, locks), explain it, and propose fixes with their expected gain, verified by measuring again. Use when a page, query, job or API is slow or uses too much.
parameters:
  - { name: what, description: "What is slow, and how slow" }
---
Investigate why {{what}} is slow ($ARGUMENTS).

1. **Define and measure**: what "slow" is (latency at which percentile, throughput, memory), under what load, and the target. Get a baseline before changing anything.
2. **Locate**: follow the time — profiles, traces, query plans (EXPLAIN ANALYZE), logs with timings, resource metrics. Name the bottleneck with evidence, not a guess.
3. **Explain**: why it is slow (an N+1 query, a missing index, work in a loop, contention, blocking I/O, an unbounded cache, a large payload).
4. **Fix**: options with their expected gain, risk and cost; the cheapest that meets the target first.
5. **Verify**: measure again the same way, and add a guard (a benchmark, a test, an alert) so it does not come back.

Show the numbers. Do not optimise what the measurements do not point at.
