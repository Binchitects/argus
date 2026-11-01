---
name: log-analysis
title: Analyze logs
description: Read logs or error output to find what went wrong — the first failure and what followed, patterns and frequencies, correlated events — and point to the cause and the next checks. Use with logs, stack traces, or an error someone pasted.
parameters:
  - { name: logs, description: "The logs, a file of them, or where they are", required: false, default: "the logs in this conversation" }
---
Analyze {{logs}} ($ARGUMENTS).

1. **First failure**: find the earliest error or anomaly; what came after is often its consequence.
2. **Patterns**: group repeated messages, count them, note when they start and stop, and which hosts, users or requests they involve.
3. **Correlate**: line up the errors with deploys, restarts, configuration changes, traffic and resource limits around the same time.
4. **Read the errors**: what each stack trace or code means, in this code when it is here (file:line).
5. **Conclude**: the most likely cause with the evidence for it, the alternatives still open, and the next checks or fixes, in order.

Quote only the lines that matter. Say when the logs are not enough, and what to log to tell next time.
