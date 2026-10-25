---
name: release-notes
title: Write release notes
description: Write release notes from the changes since the last release — highlights first, then what is new, fixed and changed, in words its readers use. Use when preparing a release, a changelog entry or an announcement.
parameters:
  - { name: version, description: "The version being released", required: false, default: "the next version" }
  - { name: readers, description: "Who reads them", required: false, default: "the people who use the product" }
---
Write release notes for {{version}}, for {{readers}}.

1. Gather the changes since the last release (tags, git log, merged merge requests, the changelog's unreleased part).
2. Group them: **Highlights** (the one to three things worth reading), **New**, **Improved**, **Fixed**, **Breaking changes** (with what to do), **Deprecations**.
3. Write each item for its reader: what they can do now or what no longer bothers them — not the internal change. One line each, two at most.
4. Leave out internal refactors and test changes unless they matter to the reader.
5. Follow the project's existing changelog format when there is one.
