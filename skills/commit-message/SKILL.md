---
name: commit-message
title: Write a commit message
description: Write a clear commit message for the changes made — a short subject in the imperative and a body saying why — in the project's convention. Use when asked for a commit message or before committing.
parameters:
  - { name: changes, description: "The changes: a diff, or what was done", required: false, default: "the changes in the working tree (git diff --staged, else git diff)" }
---
Write a commit message for {{changes}}.

- Read the changes, and the project's recent commits (git log) to follow its convention (Conventional Commits or not, language, length).
- Subject: one line, at most about 72 characters, imperative ("Add", "Fix"), saying what the change does for the reader of the history.
- Body (when the change is not trivial): why it was needed, what it changes in behaviour, anything a reviewer or a later reader should know (a migration, a breaking change, a follow-up). Wrapped at about 72 columns.
- Mention issue references when the changes or the conversation give them.

Give only the message, ready to paste. Never include secrets from the diff.
