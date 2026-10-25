---
name: meeting-notes
title: Meeting notes
description: Turn meeting notes or a transcript into minutes — summary, decisions, action items with owners and dates, open questions. Use when given notes, a transcript or a recording's text of a meeting.
parameters:
  - { name: meeting, description: "The meeting's name or topic", required: false, default: "the meeting" }
---
Write the minutes of {{meeting}} from the notes or transcript given ($ARGUMENTS).

- **Summary**: three to five sentences on what was discussed and concluded.
- **Decisions**: each as one sentence, with its reason when said.
- **Action items**: a table of what, who, by when (as said; "not set" when not), in the order they matter.
- **Open questions**: what was left undecided, and who follows up.
- **Next meeting**, if one was set.

Keep names as people said them. Do not invent owners or dates.
