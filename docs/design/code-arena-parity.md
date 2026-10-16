# Code Arena chat compared with the web chat: what is missing, and the commits to close it

This is a design only. I edited no repository files and changed nothing live. The only live calls were two read-only ones made with the key on this host, which belongs to the test member account: `initialize` and `tools/list` on `https://llm.localhost/mcp` (both returned 200), and `GET gateway /v1/model/info` (200).

## 1. Approach

**Keep the agent loop on the person's machine.** Modes, Laya, the sandbox, checkpoints, the turn commits, jobs with no time limit, hundreds of tool steps per turn and offline `-p` all live in `src/CodeArena`.

I considered and rejected running Code Arena's turns in Arena's chat engine with the local tools tunnelled to it. It would bring every web feature at once, but it has three costs:
- The chat engine caps tool calls per answer at 8 (docs/chat.md:1042).
- Every local tool call would make a round trip through Arena.
- The terminal and `-p` would depend on the chat engine.

Web chat features reach Code Arena by three routes:

1. **Shared components.** `chat.tsx` and `answer.tsx` already import `Markdown`, `ToolCard`, `Thinking`, `ModelPicker`, `ThinkingPicker`, `ContextGauge`, `useRecall`, `QuestionTurn` and `CompactedMark` from `@/pages/chat/*`. More of them can be imported the same way.
2. **An Arena relay in code-arena's local server.**
   - code-arena forwards a fixed list of Arena routes, adding the person's key. Web components that call `/api/chat/...` then work unchanged, and the key never reaches the page.
   - The list: `/api/chat/attachments` (upload, content, pages), `/speech`, `/transcribe`, `/search`, message feedback, `/api/account/voice`, memory, prompts, share, summary and a config mirror.
   - Today Arena accepts only the sign-in cookie (`src/Llm.Api/IdentityWiring.cs:71`). It would also need to accept the key on those routes, through a second auth scheme built on `McpPeople.FromHeaderAsync`, the same check `/api/code-arena` uses (`src/Llm.Api/Chat/CodeArenaSync.cs:46-66`).
   - This does not give the key new reach: it can already read every chat of the person through `/api/code-arena/chats`.
3. **Work in Code Arena itself.** Images sent to vision models, sessions as trees, `ask_user`, "Answer now", MCP progress and tools on demand.

**Prerequisite.** The sync must first be keyed on message ids. Today it counts messages (`SyncAppend(int After, …)`, `CodeArenaSync.cs:30`). Ratings, share links and branches all need the Arena id of each message. That rework is already its own v5.8 item.

## 2. Gap table

Status: HAS, PARTIAL, MISSING, or BROKEN (offered but does not work). Paths are relative to /home/ghadyani/llm-service. CA means Code Arena.

| # | Capability | Web chat (where) | Code Arena now (evidence) | How to bring it |
|---|---|---|---|---|
| 1 | Model picker showing what each model can do | `header.tsx:32`; config `ChatEndpoints.cs:97-125` | PARTIAL. Same picker (`chat.tsx:608`), but `Web.cs:520` hard-codes `vision=false` and `:523` `loaded=true` for every model. `Gateway.cs:117-126` ignores `supports_vision`, although the gateway reports it (Qwen3-Omni-30B-A3B: `supports_vision` True). | CA: read `supports_vision` and `supports_audio_input`; take `loaded` and `onRequest` from the relay's config mirror. |
| 2 | Thinking levels | `header.tsx:102`, `THINKING_PRESETS` | HAS, but with CA's own levels (`Web.cs:528`), not the deployment's presets and default. | Use presets and `defaultThinking` from Arena's config. |
| 3 | Auto, and a small model for small steps | `header.tsx:50`, chat.md:17-39 | MISSING (`auto=null`, `Web.cs:527`) | CA: send compaction summaries and `task` sub-agents to Arena's small model. Leave coding turns unrouted. |
| 4 | Answer now | `turns.tsx` `onHurry`, `POST …/hurry` | MISSING | CA: `/api/hurry` stops the thinking and the rest of the turn answers without it. Reuse `Thinking`'s `onHurry`. |
| 5 | Per-chat instructions, temperature, top-p, longest answer | `header.tsx:127` `ChatSettingsPopover` | MISSING (ARENA.md only) | Reuse the popover; keep the values in the session file. |
| 6 | Answer length (account setting) | chat.md:108 | MISSING | Read it through the relay; one line in the prompt. |
| 7 | Attachments: clip, paste, drop; PDF, Office, text; upload progress | `composer.tsx:300-305`, `uploads.ts`, `ChatEndpoints.cs:84` | MISSING. `maxUploadBytes=0` (`Web.cs:533`), and the local API takes JSON only (`Web.cs:203`). | Relay a multipart upload, allowed on that path only. Arena extracts the text (CA has no libraries to do it). Reuse `useUploads` and the attachment chips. |
| 8 | Vision (pictures sent to the model) | `Vision` flag, image parts | MISSING: no image content anywhere in `src/CodeArena` | CA: `image_url` parts for vision models; a pasted screenshot counts. |
| 9 | Sound and video files sent to the model | chat.md:567-574 | MISSING | Upload through the relay; Arena makes the MP3, frames and transcript. |
| 10 | Voice message | `media.tsx:29` `VoiceButton`, `/api/chat/transcribe` | MISSING. The page sends `Permissions-Policy microphone=()` (`Web.cs:281`). | Allow `microphone=(self)`, relay transcribe, reuse `VoiceButton`. |
| 11 | Talk | `talk.tsx:17,38`, `use-talk.ts` | MISSING | Reuse `useTalk` over the relayed speech and transcribe routes. |
| 12 | Slash menu: prompt library with blanks, `/compact` | `slash.tsx:10,65` | PARTIAL. Only `/compact`, `/clear` and `/new` work, in the page (`chat.tsx:471,505`). `/NAME` commands expand on the server (`Runtime.cs:590`) but have no menu. The terminal's built-in commands (`Repl.cs:486-700`: `/rewind`, `/memory`, `/permissions`, `/search`, `/mcp`, `/jobs`, `/todo`, `/cost`, `/sandbox`, `!cmd`) cannot be reached from the IDE. | Reuse `SlashMenu` and `PromptFields`. Fill the menu from a new `GET /api/commands` (built-ins, user commands, skills) plus Arena prompts through the relay. Run built-ins through `POST /api/command`. |
| 13 | ↑ brings back what was sent | `recall.ts` | HAS (the folder's history, shared with the terminal) | — |
| 14 | Queue: cancel one message, send one now | `composer.tsx:212-224` | PARTIAL: only Clear all (`DELETE /api/queue`) | Add per-message cancel and send-now routes. |
| 15 | Tools menu per chat | `tools-picker.tsx:11` | MISSING: every Arena tool is always on (`/tools` only lists them) | Reuse `ToolsPicker`; keep the chosen set in the session. |
| 16 | Tools on demand | chat.md:171-178 | MISSING. Live: every request carries 35 Arena tool definitions, 34,778 characters (about 8.7k tokens), plus 7,102 characters of Arena's instructions. | CA: `load_tools`, designed as the web chat does it. |
| 17 | Compare (two models, vote) | `arena.tsx` | MISSING | Later, as a coding variant in two worktrees. |
| 18 | Deep research | research tool | PARTIAL. Reached through MCP: live `tools/list` marks `deep_research` destructive, so it asks first. It shows no progress. | Fixed by row 25. |
| 19 | Markdown, sortable tables, KaTeX, right-to-left text, Mermaid drawn, code copy and download | `markdown.tsx`, `code-block.tsx` | HAS (Mermaid's preview runner is served at `Web.cs:155-158`) | — |
| 20 | Running previews (html, svg, jsx, tsx) | `code-block.tsx:93` `onPreview` | MISSING: `Markdown` is rendered without `onPreview` (`answer.tsx:237`) | CA: open the preview as an editor tab; the runner is already served. |
| 21 | Copy answer; model, time, tokens, cache share, cost | `turns.tsx:285+` | HAS (`answer.tsx:267+`) | — |
| 22 | Thumbs up and down | `feedback.tsx:28` | MISSING | Reuse `FeedbackButtons` once the sync gives Arena message ids. |
| 23 | Read aloud | `media.tsx:81` | MISSING | Reuse `ReadAloud` over the relayed `/api/chat/speech`. |
| 24 | Answer again, Shorter, Longer, another model or thinking level | `turns.tsx:288-336` | MISSING | CA: run the question again as a sibling branch (row 26), with the files put back to that turn's checkpoint first. |
| 25 | Progress of long tool calls | `parts.tsx` progress, chat.md:289-302 | MISSING. CA never sends `_meta.progressToken` (no "progress" anywhere in `src/CodeArena`), and Arena reports progress only when asked (`McpEndpoints.cs:376-381`). | CA: send the token and map the reports to the existing `calls` event. |
| 26 | Branches: edit a question, versions "2 / 3" | `turns.tsx:56-64,125-191`, `tree.ts` | MISSING. `QuestionTurn siblings=[t.question]`, `onSwitch` does nothing, no `onEdit` (`chat.tsx:676`). A session is a linear JSONL file. | CA: make the session a tree (parent per message, a current leaf). Reuse `Branches` and `ChatTree`. Editing offers to put the files back. |
| 27 | Fork from here, fork chat | `turns.tsx:337`, `header.tsx:323` | MISSING | A new session made from the start of this one, up to the chosen answer. |
| 28 | Pictures, video and speech from tools, shown in the answer | `media.tsx:12`, `image-viewer.tsx:17` | BROKEN. `Mcp.cs:132` replaces image and audio with "[image: …, not shown]". The `resource_link` Arena sends (`McpTools.cs:273`) survives only as text. The model never sees the picture. | Keep the links in the tool result's details. Serve them through the relay, or through MCP `resources/read`, which Arena already accepts with the key. Reuse `MediaPlayer` and `ImageViewer`. |
| 29 | Questions for you (`ask_user`) | `questions.tsx:13` | MISSING: MCP leaves `ask` out (`McpTools.cs:39`) | A local `ask_user` tool; reuse `QuestionCard`. The terminal asks in place. |
| 30 | Sub-agents with their progress | `parts.tsx` `agents` | PARTIAL: `task` (read-only) shows as a plain card | Emit sub-agent events; reuse the AgentWork view. |
| 31 | Memory: in every answer, Remember/Undo card, brain button | `memory.tsx:27,88` | PARTIAL, and split in two. CA keeps file memories (`Memory.cs`). Arena's `remember` arrives renamed `arena_remember` (`Runtime.cs:743`), but CA never reads Arena's memories into its prompt. | One memory: "every project" memories are Arena's, read through the relay; project memories stay local. Reuse `MemoryCard` and `MemoryButton`. |
| 32 | Canvas | `canvas-panel.tsx` | BROKEN tool. `canvas_*` is offered through MCP (live `tools/list`), but MCP calls run in a chat that is never saved (`McpTools.cs:322-323`). Reading the code, `canvas_create` should then fail the foreign key at `CanvasTables.cs:19` (not called live). | Add `canvas` to MCP's chat-only list. In Code Arena the editor is the canvas. |
| 33 | Files panel: attachments, documents as pages, zip download | `files-panel.tsx:33` | PARTIAL. Explorer and Agent changes cover the workspace; attachments and generated media have no place. | A "Session files" list; reuse the `FilesPanel` viewer for them. |
| 34 | Argus hits linked to GitLab | `tool-output.tsx:153` | MISSING (`gitlabUrl=null`, `Web.cs:532`) | Take it from Arena's config. |
| 35 | Context gauge, Compact now | `context-gauge.tsx` | HAS. CA can also set when it compacts; the web chat cannot yet (a separate v5.8 item). | — |
| 36 | Answer-ready and desktop notifications | `chat-page.tsx:365` `tellDesktop` | MISSING | Reuse `tellDesktop` when the window is hidden. |
| 37 | Stop | both | HAS. Neither has a Continue for an answer cut at the length limit. | — |
| 38 | Search chats by content, date, model | `chat-search.tsx:60` | PARTIAL: Arena chats are filtered by title or folder only; `/search` works only in the terminal | A search box over local sessions (`search_sessions`) plus the relayed `/api/chat/search`. |
| 39 | Rename, archive, delete | `header.tsx:296-375` | MISSING (the only session routes are new, resume, web and send-all) | Add the routes and menu items. |
| 40 | Share link | `share-dialog.tsx:25` | MISSING (possible from the web on the synced chat) | Through the relay, after the id-keyed sync. |
| 41 | Export as md, html, pdf, json, or a summary | `export.ts:30-96` | MISSING | Reuse `export.ts` in the page; the summary through the relay. |
| 42 | Jump-to-question rail | `question-rail.tsx:15` | MISSING | Reuse. |
| 43 | Assistants and projects | `assistant-view.tsx` | Filled in another way: ARENA.md, commands, skills, sub-agents | Later and low value: "use assistant X in this session" through the relay. |
| 44 | Pins and folders | neither has them (the list folds by date only) | — | Not a parity item. |
| 45 | Safeguards, credit, fair use | gateway guardrail on API requests (chat.md:905-912) | HAS (inherited through the gateway) | — |

Rows with nothing for Code Arena to do: `web_search` is missing on the live stack for both surfaces, because it is the same tool (`Web.cs:366`, `CanSearch`). Scheduled tasks, bots and email stay web-only.

## 3. "And more": what a coding chat should add

1. **A summary card at the end of each turn.** The files changed with +/− counts, Open diff, Accept, Revert, and "Rewind to before this turn" (code, chat or both). This puts `/rewind` and the checkpoints in the IDE.
2. **The edit's diff on the approval card** in ask mode, before Allow. Plus "Always for this file".
3. **A live to-do panel** from `todo_write`, above the input box. In plan mode, **Approve plan**: switch to auto-edit and carry on.
4. **Code blocks that act on the workspace.** "Apply to file" (opens the compare view first), "Insert at cursor", "Run in terminal" for shell blocks.
5. **More @-mentions.** `@problems`, `@diff` (uncommitted or staged), `@terminal` (its selection or last output), `@symbol` (once LSP lands), `@url` (fetched with `fetch_page`), `@session` (another session).
6. **Send to chat from other parts of the IDE.** A terminal's selection, a failed task's output, a debugger's stack, a commit from history.
7. **A commit message drafted** from the staged changes, in the Source control box. A **Review changes** action on Source control and on commits.
8. **Previews in the IDE.** HTML and React as editor tabs; a screenshot of a preview goes to a vision model.
9. **Session totals.** Cost and tokens for the whole session, and an optional cost cap per session.
10. **Parallel sessions in git worktrees.** Compare (row 17) runs as two models in two worktrees, and the person picks one diff.

## 4. Commits, ordered by value (each one shippable and tested)

1. **Code Arena reports the models as Arena does.**
   - Gateway: `supports_vision` and `supports_audio_input` in `ModelInfo`.
   - `ChatConfig` (`Web.cs:512-536`) fills `loaded`, `onRequest`, `presets`, `defaultThinking`, `gitlabUrl`, `imageTypes` and `maxUploadBytes` from a key-authed `GET /api/code-arena/config`.
   - Fixes rows 1, 2 and 34. Tests: `CodeArena.Tests` (`WebTests`, `FakeGateway`) and `Llm.Tests`.
2. **Arena tools on demand, a per-session tools menu, and two fixes to Arena's MCP.**
   - Adds `load_tools` and `ToolsPicker`.
   - Adds `canvas` to `ChatOnly` (`McpTools.cs:39`).
   - Sends `_meta.progressToken` and shows the reported progress.
   - About 8.7k fewer tokens on every request; deep research and video show how far they are.
   - Fixes rows 15, 16, 18, 25 and 32.
3. **The Arena relay.**
   - An auth scheme on Arena that takes the person's key on the allowlisted routes, with tests that one person cannot read another's attachment.
   - A forwarder in code-arena that keeps its own token and origin checks, and allows multipart on the upload path only.
   - The foundation for commits 4, 5, 8, 9 and 10.
4. **Pictures, video and speech from tools appear in answers, and vision models see the pictures.**
   - Fixes `Mcp.cs:132` (no longer dropped); reuses `MediaPlayer` and `ImageViewer`.
   - Fixes row 28. This bears directly on the open "media models" ask.
5. **Attachments and vision in the IDE's input box.** Clip, paste and drop; Arena extracts the text; images go as `image_url` parts; sound and video through Arena. Fixes rows 7, 8 and 9.
6. **Sessions become trees** (needs the id-keyed sync).
   - Edit a question, Answer again, Shorter, Longer, another model or thinking level, the version arrows, Fork from here; files put back to the checkpoint when asked.
   - Then ratings through the relay.
   - Fixes rows 22, 24, 26 and 27.
7. **The slash menu, with every command in the IDE.** `GET /api/commands`, `POST /api/command`, and prompts with blanks; `/rewind`, `/memory`, `/permissions` and `/search` get IDE views. Fixes row 12.
8. **The agent asks and shows its work.** `ask_user` with `QuestionCard`, Answer now, live sub-agents, the to-do panel, Approve plan. Fixes rows 4, 29 and 30, and "more" item 3.
9. **Voice.** Microphone policy, voice message, read aloud, Talk. Fixes rows 10, 11 and 23.
10. **Managing sessions.** Content search, rename, archive, delete, export, the jump rail, share, desktop notification when a turn ends. Fixes rows 36 and 38–42.
11. **One memory and per-session settings.** Arena memories in the prompt with `MemoryCard`; per-session instructions and parameters; Answer length; the small model for compaction and sub-agents. Fixes rows 3, 5, 6 and 31.
12. **Code blocks act on the workspace.** Previews as editor tabs, Apply to file, Insert at cursor, Run in terminal. Fixes row 20 and "more" items 4 and 8.
13. **Review-first agent turns.** The summary card at the end of each turn, the diff on the approval card, more @-mentions, send to chat from the terminal, Problems, debugger and commits, a drafted commit message, Review changes. "More" items 1, 2, 5, 6 and 7.
14. **Compare in two worktrees, and parallel sessions.** Row 17 and "more" item 10.

The probe scripts I ran are in /tmp/claude-1000/-home-ghadyani-llm-service/1e6fba3b-9691-480c-910d-b8a6e9b10bb9/scratchpad:
- mcp_list.py
- model_info.py