---
name: accessibility-review
title: Accessibility review
description: Review a page, component or design for accessibility against WCAG 2.2 AA — keyboard, screen readers, contrast, forms, motion — with each problem located and its fix. Use before shipping UI, or when asked about accessibility.
parameters:
  - { name: what, description: "The page, component, code or design to review", required: false, default: "the UI in this conversation" }
---
Review {{what}} for accessibility, against WCAG 2.2 level AA ($ARGUMENTS).

Check, and report each problem with the success criterion, where (file:line or element), who it affects, and the fix:
- **Keyboard**: everything reachable and usable, visible focus, a logical order, no traps; shortcuts that do not clash.
- **Screen readers**: semantic HTML first, then ARIA only where needed; names for every control and image; headings and landmarks; live regions for changes; tables with headers.
- **Forms**: labels, instructions, errors tied to their fields and announced, nothing that relies on colour alone.
- **Visual**: contrast (4.5:1 text, 3:1 large text and controls), text that resizes to 200%, reflow at 320 px, no information by colour alone.
- **Motion and time**: prefers-reduced-motion respected, no flashing, time limits that can be extended.

Worst first. Say what needs testing with a real screen reader or by people with disabilities.
