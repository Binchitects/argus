---
name: write-email
title: Write an email or memo
description: Draft a clear email, message or memo for a purpose and reader — the point first, what is asked, and a fitting tone. Use when asked to write, reply to or improve an email, a message or an announcement.
parameters:
  - { name: purpose, description: "What the message is for" }
  - { name: to, description: "Who it is for", required: false, default: "the reader named in the conversation" }
  - { name: tone, description: "The tone", required: false, default: "clear, friendly and brief" }
---
Write a message to {{to}} for this purpose: {{purpose}}. Tone: {{tone}}.

- A subject line that says what it is about (for an email).
- The point or the ask in the first two sentences.
- Then only the context the reader needs, in short paragraphs or bullets.
- A clear next step: what you need from them, by when.
- Close politely, in the language the reader uses.

Offer a shorter variant when the draft runs over 150 words.
