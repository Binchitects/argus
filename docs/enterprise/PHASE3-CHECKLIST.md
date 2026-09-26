# Phase 3 sign-off: chat in the app

> **Superseded.** The web is being rewritten (phases 3A–3F in the [plan](PLAN.md)); this
> checklist will be rewritten for the new web in 3F. Kept for the list of Open WebUI
> features, all of which are now in scope.

Open WebUI is removed only after you sign this off ([plan](PLAN.md), phase 3).
Try each item at `https://<LLM_DOMAIN>/chat`. Tick it, or write what is wrong
next to it.

The automated suites already cover every item marked **(tested)**: backend,
UI and browser tests (desktop and phone), against the real model. Ticking those
is still worth it: it's your judgement of how they feel, not only whether they
work.

## What the chat does

- [ ] An answer streams in token by token. Markdown, tables and code render, and
      code is highlighted. **(tested)**
- [ ] **Copy** on a code block copies it. **(tested)**
- [ ] Thinking levels come from the deployment (Deep think, Balanced, Quick,
      No thinking). The thought process shows while the model thinks, then
      folds away. *No thinking* really answers without thinking. **(tested)**
- [ ] **Stop** keeps what was written, marked *Stopped*. **Regenerate** answers
      again. **(tested)**
- [ ] Attach a text/code file and a PDF, and ask about them. A binary file and a
      scanned PDF are refused with a reason. **(tested)**
- [ ] Chats are kept: reload the page, sign out and in, open one from the list.
      **(tested)**
- [ ] Search, rename and delete chats. **(tested)**
- [ ] On a phone, the chat list opens behind **Chats** and closes with
      **Hide chats**. **(tested)**
- [ ] Tokens (in, cache hit, out) show under each answer. The chat's spend
      appears under **Usage & cost → Mine** as surface *Chat*.
- [ ] With your credit set to 0 (Admin → People), the chat answers with the
      sentence about credit (about a minute after the change). Set it back
      afterwards. **(tested by `functional-test.py`)**

## Argus (with the `argus` profile on)

- [ ] *Search our code (Argus)* is on by default. A question about your code
      shows the tool calls inline and an answer that cites files.
- [ ] Someone without access to a repository gets the notice naming the
      repository and its maintainers, and the model tells them whom to ask.
      **(tested against the test GitLab)**
- [ ] An account with no matching GitLab account gets "Argus is not available
      for this answer" with the reason, and still gets an answer.

## Open WebUI features the app's chat does not have

Open WebUI has these whether or not anyone here uses them. Mark any you need
before Open WebUI goes; each would be added in this phase.

- [ ] Several models to choose from. Today the stack serves one model, and
      Open WebUI shows one entry per thinking level.
- [ ] Editing a sent message, or branching a chat.
- [ ] Sharing or exporting a chat.
- [ ] Folders, tags, pinning or archiving chats.
- [ ] Images: image input (vision) and image generation.
- [ ] Voice: dictation and reading answers aloud.
- [ ] Web search.
- [ ] Knowledge collections: documents searched across chats. The app puts an
      attached file's text into that one chat.
- [ ] A personal system prompt, custom "models" or a prompt library.
- [ ] Rating answers.

## Sign-off

- [ ] Everything above that I need works. Remove Open WebUI and tag
      `enterprise-p3`.

Signed: ____________  Date: ____________
