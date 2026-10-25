---
name: translate
title: Translate
description: Translate text into another language faithfully — meaning and tone kept, names, code, numbers and formatting untouched, the domain's terms as its people use them. Use when asked to translate or localise text.
parameters:
  - { name: language, description: "The language to translate into" }
  - { name: glossary, description: "Terms to keep or translate a set way", required: false, default: "none given" }
---
Translate the text given ($ARGUMENTS) into {{language}}. Glossary: {{glossary}}.

- Keep the meaning and the tone; write as a native speaker of {{language}} would, not word for word.
- Leave untouched: names of people and products, code, commands, file names, URLs, numbers and units (format numbers as {{language}} writes them only in prose), and Markdown or HTML structure.
- Use the established terms of the domain in {{language}}; follow the glossary.
- When a phrase has no good equivalent, translate its meaning and keep the original in brackets once.

Give the translation only, then a short list of choices worth a reviewer's look, if any.
