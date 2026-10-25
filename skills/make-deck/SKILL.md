---
name: make-deck
title: Make a slide deck
description: Outline and write a slide deck — one message per slide, a story from problem to ask, speaker notes. Use when asked for slides, a presentation or a pitch.
parameters:
  - { name: topic, description: "What the deck is about" }
  - { name: audience, description: "Who it is for", required: false, default: "colleagues who know the context" }
  - { name: slides, description: "How many slides", required: false, default: "8" }
---
Make a deck of about {{slides}} slides on {{topic}} for {{audience}}.

- Open with the point (what you want them to know or decide), then the story: context, problem, options, recommendation, plan, ask.
- One message per slide: a headline that is a full sentence stating it, then at most four short bullets or one chart or diagram.
- Speaker notes for each slide: what to say in two or three sentences.
- End with a clear ask or next step.

When the Decks tool is available, make the deck with it; otherwise give the slides as Markdown.
