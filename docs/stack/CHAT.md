# Chat

The chat is part of the app, at `https://<LLM_DOMAIN>/chat`. Anyone who can
sign in can use it. Open WebUI still runs at `https://chat.<LLM_DOMAIN>` until
the app's chat is signed off ([plan](../enterprise/PLAN.md), phase 3). The two
don't share chats.

## What it does

- **Answers stream** token by token, with Markdown, tables and highlighted code.
  Each code block has **Copy**. Model output is sanitised, so HTML in an answer
  is shown as text and never runs.
- **Thinking.** Each chat has a thinking level, taken from the deployment's
  `THINKING_PRESETS` (for example Deep think, Balanced, Quick, No thinking).
  The model's reasoning shows while it thinks, then folds into
  "Thought process". *No thinking* really turns it off: it sends
  `enable_thinking: false` to the chat template, because LiteLLM drops a
  top-level `reasoning_effort` for this backend (measured).
- **Stop** keeps what was written so far, marked *Stopped*. **Regenerate**
  answers the last question again.
- **Attachments.** Text, code, Markdown, CSV, JSON and logs are attached as
  they are. PDFs are read page by page; a scan with no text layer is refused
  and the page says why. Binary files are refused. The limit is 20 MB per
  upload. A file's text is cut at 200,000 characters and marked "cut to fit".
  Only the extracted text is stored, never the file.
- **History.** Chats are listed newest first and can be searched, renamed and
  deleted. The first question becomes the title. When a long chat no longer
  fits the model's context, the oldest turns are left out of what is sent (the
  chat itself keeps them).
- **Argus.** With the `argus` profile on, **Search our code (Argus)** is on by
  default in each chat. Tool calls show inline: the tool, its arguments, and
  what it returned. When something exists that the person can't read, a notice
  names the repository and its maintainers and says what to ask for (Reporter
  access in GitLab; Argus picks the change up within 10 minutes).
- **Phones.** The list of chats folds behind **Chats**, and the thread uses the
  whole screen.

## Who sees what

- A chat belongs to one person. Nobody else can read it, **admins included**:
  every query is filtered by the signed-in person.
- Deleting a person deletes their chats and attachments.
- **Argus answers as the person asking.** The app calls Argus inside the
  network with `ARGUS_CHAT_CLIENT_TOKEN` and the person's email, and Argus
  resolves that email to a GitLab account and its access. Argus accepts that
  token only from inside the network. Without a GitLab account for the email,
  the answer carries a notice, "Argus is not available for this answer",
  followed by Argus's own reason, and the model answers without code search.

## Cost and credit

The app talks to LiteLLM with its own service key, alias `chat`, which is created
on first use and stored encrypted with `APP_DATA_KEY`. Each request names the
person (their email), so:

- spend is the person's: it appears under **Usage & cost** with surface **Chat**
- **their credit applies.** At or past it, the answer says so in a sentence
  instead of an error code.

If the key is revoked, the app creates a new one on the next answer.

## Settings

| Setting | Default | What it is |
|---|---|---|
| `THINKING_PRESETS` | from the model sample | the thinking levels offered, `level:Label,...` |
| `ARGUS_CHAT_CLIENT_TOKEN` | *(none)* | lets the chat use Argus; generate one with `openssl rand -hex 32`; Argus and the app both read it from `.env` |
| `Chat__MaxToolRounds` | `8` | tool calls per answer before the model must answer |
| `Chat__MaxUploadBytes` | `20971520` | upload size limit |
| `Chat__MaxAttachmentChars` | `200000` | text kept per attachment |
| `Chat__RequestTimeout` | `00:15:00` | longest single answer |

## When something goes wrong

| The page says | Why | What to do |
|---|---|---|
| "The model gateway is not reachable right now" or "The model could not answer: …" | LiteLLM or the engine is down or still loading | Admin → Overview shows which; loading a model takes minutes |
| "You have used all your credit. Ask an admin to raise it." | the person's credit is spent | an admin raises it under People (it takes effect within about a minute: LiteLLM caches budgets) |
| "The message did not reach the server" | the network or TLS failed before the server got it | the text is back in the box; send again |
| "This chat is already answering" | one answer at a time per chat | stop it, or wait |
| "This conversation is longer than the model can read" | the latest question and its attachments alone do not fit | start a new chat, or attach less |
| "Argus is not available for this answer: …" | Argus's reason follows | usually no GitLab account matches the person's email; see [ARGUS.md](ARGUS.md) |

## How it is tested

- **Backend (xUnit):** streaming, tool rounds, the no-access notice, stop, the
  budget sentence, a revoked key being replaced, attachments, and that people
  only ever see their own chats and files. They run against a real Postgres,
  with a fake model and a fake Argus.
- **UI (Vitest):** the page's behaviour on a fake backend, including a stream
  that is stopped, a failed answer, a message that never reached the server,
  and model HTML that must not run.
- **Browser (Playwright), desktop and phone:**
  - `e2e/chat.spec.ts` runs against the real model with `E2E_CHAT=1`.
  - The Argus case also needs the test GitLab (`scripts/test-gitlab/run.sh`)
    and a person who can't read `eal-core` (`E2E_ARGUS_USER`,
    `E2E_ARGUS_PASSWORD`).
- **`functional-test.py`** sends a chat through Traefik and checks that it is
  billed under `chat` to the person, and that zero credit is refused.
