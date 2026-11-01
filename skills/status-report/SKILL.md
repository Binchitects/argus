---
name: status-report
title: Status report
description: Write a concise project or team status report — progress against goals, what was done, what is next, risks and blockers with their owners, and decisions needed — for managers or stakeholders. Use for weekly updates, steering meetings or handovers.
parameters:
  - { name: project, description: "The project or team" }
  - { name: period, description: "The period it covers", required: false, default: "this week" }
---
Write a status report for {{project}}, {{period}} ($ARGUMENTS).

- **Status** in one line: on track, at risk or off track (green, amber, red), and why.
- **Highlights**: what was achieved, as outcomes, not activities.
- **Progress**: against the goals or milestones, with dates and percentages when known.
- **Next**: the plan for the next period.
- **Risks and blockers**: each with its impact, owner, and what is being done; what help is needed.
- **Decisions needed**: from whom, by when.

One page at most. Plain words, facts first; mark estimates as estimates.
