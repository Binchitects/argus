# Chat

The chat is at `https://<LLM_DOMAIN>/chat`, for everyone who can sign in. Open
WebUI still runs at `https://chat.<LLM_DOMAIN>` until the new web is signed off
([plan](../enterprise/PLAN.md), 3F). The two don't share chats.

## What it does

- **Models.** The picker lists what the gateway serves, with what each model
  can do: context size, tools, thinking, and whether it can see images. A chat
  keeps its model. The deployment's own model is the default.
- **Thinking.** Each chat has a thinking level from the deployment's
  `THINKING_PRESETS`. While the model thinks, its reasoning shows with a timer.
  Afterwards it folds to "Thought for 4.2 s", and can be opened again.
  *No thinking* really turns it off: it sends `enable_thinking: false` to the
  chat template.
- **Instructions and parameters.** Per chat: your own instructions (sent after
  the app's), temperature, top-p, and the longest answer. Empty means the
  model's default.
- **Streaming, stop, answer again.**
  - **Stop** keeps what was written, marked *Stopped*.
  - **Answer again** writes a new answer beside the old one; both stay. It can
    also use another model or thinking level, for that answer only.
- **Branches.**
  - **Editing** a question sends the new text as a sibling of the old one:
    both questions and their answers are kept.
  - Arrows show "2 / 3" on a question or an answer with other versions, and
    switch between them.
  - What is on screen, and what the model reads, is the branch you are on.
- **Tool calls.** Each call is a card: the tool, what it was asked, whether it
  ran, how many results, and how long it took. Opened, it shows Argus's answer
  for what it is:
  - Symbols, references and search hits show as places in the code:
    repository, file and lines, linking to that line range in GitLab (on the
    branch asked about). Signatures and reference lines are highlighted. The
    words a search matched are marked.
  - A file Argus read shows as code, and goes into the Files panel.
  - Other answers show as a table or a list of fields. **Raw answer** shows
    the JSON, and errors are shown as the tool's own words.
  - When something exists that you can't read, a notice names the repository
    and its maintainers. It stays outside the fold, so it is never missed.
- **Code.**
  - Every code block shows its language or file name, with **Copy**, wrap,
    **Download** and line numbers.
  - Blocks over 40 lines fold.
  - A block the model names (```` ```ts title="src/a.ts" ````, or a first line
    `// file: src/a.ts`) opens in the Files panel.
- **Files panel.** Like Claude's: every attachment, every file Argus read (with
  **Open in GitLab**), and every file the model wrote in the branch on screen.
  It keeps the newest version of each and has a viewer.
- **Attachments.**
  - Files come by the paperclip, by pasting, or by dropping them anywhere on
    the page. Uploads show their progress.
  - Text, code, Markdown, CSV, JSON and logs go as their text. PDFs go page by
    page; a scan with no text layer is refused, with the reason.
  - Images (PNG, JPEG, GIF, WebP, told by their bytes) show as thumbnails.
    A thumbnail opens a viewer: fitted to the screen, or at actual size on a
    click. Arrows or the arrow keys move between images sent together, with
    open and download.
  - A model that can see gets the picture; one that can't gets the file name,
    and you get a note saying so.
  - SVG is never treated as an image, because it can carry script.
- **Answers.** Markdown with tables and maths (KaTeX), links that open safely
  in a new tab, and the model, time, tokens and cost under each answer. Model
  output is sanitised: HTML in an answer never runs.
- **History.** Chats are grouped by date and can be searched, renamed and
  deleted. The first question becomes the title and the browser tab's name.
- **Phones.** The chat list and the Files panel open over the thread; the
  header fits a narrow screen.

A question that never reached the server goes back into the box with its
attachments, instead of being lost.

## Who sees what

- A chat belongs to one person. Nobody else can read it, **admins included**:
  every query is filtered by the signed-in person, attachments too.
- Deleting a person deletes their chats and attachments.
- **Argus answers as the person asking.** The app calls Argus inside the
  network with `ARGUS_CHAT_CLIENT_TOKEN` and your email. Argus resolves the
  email to a GitLab account and its access. Without a GitLab account for the
  email, the answer carries "Argus is not available for this answer" with
  Argus's reason, and the model answers without code search.

## Cost and credit

The chat talks to LiteLLM with its own service key (alias `chat`). Each request
names the person, so spend is theirs (surface **Chat** under Usage & cost), and
their credit applies. The cost under each answer comes from the model's prices
at the gateway.

## Settings

The chat's limits are under Admin → Settings → Chat ([SETTINGS.md](SETTINGS.md)):

| Setting | Default | What it is |
|---|---|---|
| Tool calls per answer | 8 | how many rounds of tool use one answer may take |
| Largest attachment | 20 MB | per file (up to 100 MB) |
| Text kept per attachment | 200,000 characters | longer files are cut and marked |
| Longest single answer | 15 minutes | an answer still running after this is stopped |

The thinking levels (`THINKING_PRESETS`) are under Settings → Model.
**GitLab address for links** (Settings → Argus, applies at once) is where
browsers open GitLab from Argus's answers. Leave it empty to use the address
Argus indexes. Set it when Argus reaches GitLab by an internal name.

## How a conversation is stored

Messages form a tree: each has a parent, and the conversation remembers its
current leaf. `POST .../messages` takes a `parentId` (default: the leaf) or
`root: true`. `POST .../regenerate` takes the question and, optionally, a model
and a thinking level. `PUT .../leaf` switches branch. Chats from before
branches were each migrated to one branch.

## When something goes wrong

| The page says | Why | What to do |
|---|---|---|
| "The model gateway is not reachable right now" or "The model could not answer: …" | LiteLLM or the engine is down or still loading | Admin → Overview shows which; loading a model takes minutes |
| "You have used all your credit. Ask an admin to raise it." | the person's credit is spent | an admin raises it under People; it takes effect within about a minute |
| "The message did not reach the server" | the network or TLS failed before the server got it | the text is back in the box; send again |
| "This chat is already answering" | one answer at a time per chat | stop it, or wait |
| "This conversation is longer than the model can read" | the question and its attachments alone do not fit | start a new chat, or attach less |
| "… cannot see images" | the chat's model has no vision | choose a model that shows "Sees images" |
| "Argus is not available for this answer: …" | Argus's reason follows | usually no GitLab account matches the person's email; see [ARGUS.md](ARGUS.md) |

## How it is tested

- **Backend (xUnit):** streaming, tool rounds (Argus's rows reach the model and
  the page as one JSON list), the no-access notice, stop, the budget sentence,
  a revoked key being replaced, attachments, and ownership. Also the GitLab
  link address applying at once.
  Also branches (edits, answering again, switching, parents from another chat
  refused), a chat's instructions and parameters, retries with another model,
  images to a model that can see and one that can't, SVG never served as an
  image, and the migration of existing chats. Real Postgres, a fake model and
  a fake Argus.
- **UI (Vitest):**
  - the branch tree
  - the live-stream reducer
  - file and fence parsing
  - reading Argus's answers: each tool's rows, GitLab links on the branch
    asked about, search matches without marking the code's own brackets, and
    files Argus read in the Files panel
  - the page on a fake API: a new chat with its settings, streaming with
    thinking, tokens and cost, tool cards and the no-access notice, stop, a
    failed answer, a message given back, an edit and its version arrows,
    answering again with another thinking level, image previews and the "cannot
    see" note, settings validation, and hostile HTML and maths
  - Argus's answers linking to GitLab, **Raw answer**, a failed tool in its
    own words, and the image viewer (arrows, keys, actual size)
- **Browser (Playwright), desktop and phone, both themes, with axe, in CI
  too:** a chat with Argus's answers and two images, served by the browser
  itself, from the links to the image viewer.
- **Browser (Playwright), desktop and phone, against the real model:**
  - streaming and reload
  - thinking
  - stop, then answering again into a second version
  - an edit keeping both versions
  - an attachment read by the model and shown in the Files panel
  - a code block's copy, download and highlighting
  - the chat list's rename, search and delete
  - a chat's instructions reaching the model
  - with the test GitLab, the no-access notice
