---
name: document-project
title: Document a project
description: Write or improve a project's documentation — a README or guide that says what it is, how to install, configure and use it, and how to contribute. Use when asked for a README, docs, a guide or onboarding notes.
parameters:
  - { name: what, description: "What to document: the project, a module, a feature", required: false, default: "this project" }
  - { name: readers, description: "Who reads it", required: false, default: "a new user and a new contributor" }
---
Document {{what}} for {{readers}}.

1. Read the code, the configuration, the build and the existing docs first; document what is true, and say where you were unsure.
2. A README covers, in this order: what it is and why (two sentences), a quick start that works when copied, installation and requirements, configuration (each setting, its default, an example), usage with examples, how to run the tests, how to contribute, the licence.
3. Use short sections, code blocks for commands, and tables for settings. Link instead of repeating.
4. Keep commands and versions exact; never put real secrets in examples — show how to make them instead.
