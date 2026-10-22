---
name: triage
title: Triage a GitLab issue
description: Sort a GitLab issue — what it asks, bug or feature, how urgent, which labels — without changing it.
parameters:
  - { name: issue, description: "The issue's number, like 42" }
  - { name: project, description: "The project's path, like team/app" }
---
Triage issue #{{issue}} in the GitLab project {{project}}. Read it and its comments, then say in a few lines: what it asks for; whether it is a bug, a feature or a question; how urgent it looks, and why; which labels fit; and what is missing before someone can act on it. Do not change the issue or comment on it.
