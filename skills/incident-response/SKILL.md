---
name: incident-response
title: Respond to an incident
description: Lead the response to a live incident — assess impact, stabilise first, find the cause from logs and metrics, communicate status, and keep a timeline. Use when production is down, degraded, or a security incident is under way.
parameters:
  - { name: symptom, description: "What is wrong, as seen now" }
---
There is an incident: {{symptom}} ($ARGUMENTS). Help respond, calmly and in order.

1. **Assess**: what is affected (services, people, data), since when, how badly, and is it getting worse? Set a severity (SEV1 to SEV4) and say why.
2. **Stabilise first**: the quickest safe ways to stop the harm (roll back the last change, fail over, scale, turn a feature off, block a source), with the risk of each. Prefer reversible steps.
3. **Diagnose**: what changed recently (deploys, configuration, traffic, dependencies), and what the logs, metrics and errors say. Form hypotheses, most likely first, and the check that confirms or rules out each.
4. **Communicate**: a short status update for the people affected — what is wrong, what is being done, when the next update comes. No guesses stated as facts.
5. **Timeline**: keep every step and finding with its time, for the postmortem.

For a security incident: preserve evidence before changing things, contain (revoke keys, isolate hosts), and bring in the security team. Never run destructive commands without saying so first.
