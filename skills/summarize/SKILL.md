---
name: summarize
title: Summarize
description: Summarize a document, thread, page or conversation for a given reader — the gist first, then the key points, decisions and open questions. Use when asked for a summary, a TL;DR, or "what does this say".
parameters:
  - { name: length, description: "How long", required: false, default: "one short paragraph, then the key points" }
  - { name: reader, description: "Who reads it", required: false, default: "someone who has not read the original" }
---
Summarize what was given ($ARGUMENTS) in {{length}}, for {{reader}}.

- Start with the gist: what it is and the one thing to take away.
- Then the key points as bullets, in order of importance, with numbers, names and dates kept exact.
- Then decisions made and open questions, if any.
- Do not add what is not in the source; mark your own reading of it as such.
- Write in the language of the source unless asked otherwise.
