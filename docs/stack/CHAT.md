# Chat

The chat is at `https://<LLM_DOMAIN>/chat`, for everyone who can sign in. Open
WebUI still runs at `https://chat.<LLM_DOMAIN>` until the new web is signed off
([plan](../enterprise/PLAN.md), 3F). The two don't share chats.

## What it does

- **Models.** The picker lists the models you may use (Admin → Models), with
  what each can do: context size, tools, thinking, and whether it can see
  images. A chat keeps its model. The default is the loaded model. An engine
  model that is not loaded is listed greyed out, "Not loaded now": an admin
  loads it.
- **Thinking.** Each chat has a thinking level from the deployment's
  `THINKING_PRESETS`. A new chat starts at the deployment's default,
  `MODEL_REASONING_EFFORT` (medium unless changed). While the model thinks, its reasoning shows with a timer.
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
- **Tools.** **Tools** beside the paperclip lists the tools you may use and
  which of them this chat has on; a new chat starts with those an admin put
  on in new chats. The model calls a tool when a question needs it.
  - **Argus**: the code you can read in GitLab (below).
  - **Image generation**: a picture from a description, made by the image
    model at the gateway (the `image` profile). It shows in the answer, opens
    full size, and is a file of the chat.
  - **Calculator**: exact arithmetic to 28 digits, so the model does not
    guess. Functions like sqrt and sin are good to 15 digits.
  - **Date and time**: the time in any time zone, and the days between dates.
  - **Python**: code run in the sandbox (the `sandbox` profile) with numpy,
    pandas, matplotlib, scipy, sympy and openpyxl. The chat's files are in its
    working directory under their names (the original .xlsx, not its text);
    what it writes comes back: charts as pictures in the answer, other files
    in the Files panel and on the card, to open or download. Each run starts
    afresh, has no network, and stops at its time limit (60 s unless changed).
  - **Reading files**: a long attachment goes into the question only up to a
    budget (30,000 characters), with a note saying how long it really is; the
    model reads on by lines, or searches it, when it needs more. Files a tool
    made are read the same way.
  - **Web** (off until an admin turns it on): search (the `websearch`
    profile's SearXNG) and reading pages, from the sites an admin allows only.
    Pages are read in parts, as text; PDFs and documents on the web too.
  - **MCP servers** an admin added: their tools, by name.
  - A tool set to **ask before each call** waits with **Allow** and **Don't
    allow**. A call you do not allow is not run, and the model is told so.
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
  - Word, Excel and PowerPoint files (.docx, .xlsx, .pptx), their OpenDocument
    cousins (.odt, .ods, .odp) and RTF go as their text, read on the server
    from the file itself (nothing in it is run): headings, lists and tables
    (as rows of cells) from a document, each sheet as CSV from a workbook,
    each slide in order from a deck. Old binary Office files (.doc, .xls,
    .ppt) are refused with how to save them in the newer format. The file
    itself is kept beside its text.
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
- **History.** Chats are grouped by date and can be searched. The first
  question becomes the title and the browser tab's name. Long titles are cut
  with an ellipsis; the list never scrolls sideways. From the list, or the
  **⋯** menu in a chat's header, a chat can be:
  - **renamed**;
  - **forked**: a new chat with the branch on screen, its files and the chat's
    settings. It notes where it came from; the original is untouched.
  - **archived**: it leaves the list for **Archived chats** at the bottom, and
    comes back by itself when you write in it (or with **Unarchive**);
  - **deleted**, with its files, except files a fork still uses.
- **Fork from an answer.** **Fork from here** under an answer starts a new
  chat that ends with that answer, to try another direction without losing
  this one.
- **Jump to a question.** On wider screens a rail at the thread's right edge
  has a mark for each question. Hover one to read the question and click it to
  go there. The question being read is marked.
- **Width.** Pages and the chat grow with the screen. **Width** in the account
  menu (or Your account → Appearance) picks Comfortable, Wide (the default) or
  Full width, remembered per browser.
- **Phones.** The chat list and the Files panel open over the thread; the
  header fits a narrow screen.

A question that never reached the server goes back into the box with its
attachments, instead of being lost.

## Fair use

The model serves few people at once (llama.cpp's `LLAMACPP_PARALLEL` slots).
So that everyone gets their turn:

- **In the chat**, a person has one answer running at a time (Settings → Chat →
  Answers at once, per person), and the chat as many as the engine serves at
  once (Answers at once, everyone; 0 means the engine's slots). Others wait in
  line and see how many answers are ahead of them. A free place goes to
  whoever has had least: someone with nothing running goes before someone
  whose last answer just ended. A wait of more than ten minutes gives up and
  says the model is busy.
- **API keys** (Qwen Code, IDEs, scripts) have at most two requests at once
  (API requests at once, per key); a third at the same time gets HTTP 429 and
  can retry. It applies to every key, within seconds of a change.

## Who sees what

- A chat belongs to one person. Nobody else can read it, **admins included**:
  every query is filtered by the signed-in person, attachments too.
- Deleting a person deletes their chats and attachments.
- **Argus answers as the person asking.** The app calls Argus inside the
  network with `ARGUS_CHAT_CLIENT_TOKEN` and your email. Argus resolves the
  email to a GitLab account and its access. Without a GitLab account for the
  email, the answer carries "Argus is not available for this answer" with
  Argus's reason, and the model answers without code search.
- **Tools are for whom an admin says** (Admin → Tools): everyone, admins, or
  members of chosen groups (Admin → Groups: app groups, or groups from the
  company directory). A chat can only turn on tools its owner may use, and the
  server checks it on every answer: a tool an admin takes away leaves every
  chat at once.
- **Models are for whom an admin says** too (Admin → Models), with the same
  rules, in the chat and on the person's API keys.
- **Python runs are sealed off.** No network at all; each run is its own
  unprivileged user in its own directory, with CPU, memory, file size and
  process limits; nothing it starts outlives it; its directory is wiped. It
  sees only the files of the chat it runs for. `scripts/sandbox-check.py`
  tests all of it against the running sandbox.
- **The web is only what an admin allows**: named sites (or `*` for any public
  one), and never an address inside the network, whatever a name or a
  redirect points to: every connection is checked as it is made. Pages are
  data to the model, not instructions.
- **MCP servers** get the person's email only if the admin set a header for
  it. A server's key is stored encrypted under `APP_DATA_KEY` and never shown.

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
and a thinking level. `PUT .../leaf` switches branch. `POST .../fork` takes a
`messageId` (default: the leaf) and copies the path to it into a new chat. The
fork must end on a question or a finished answer, never inside a tool round.
`PATCH` with `archived` archives a chat, and `GET /conversations?archived=true`
lists the archived ones. Chats from before branches were each migrated to one
branch.

## When something goes wrong

| The page says | Why | What to do |
|---|---|---|
| "The model gateway is not reachable right now" or "The model could not answer: …" | LiteLLM or the engine is down or still loading | Admin → Overview shows which; loading a model takes minutes |
| "You have used all your credit. Ask an admin to raise it." | the person's credit is spent | an admin raises it under People; it takes effect within about a minute |
| "The message did not reach the server" | the network or TLS failed before the server got it | the text is back in the box; send again |
| "This chat is already answering" | one answer at a time per chat | stop it, or wait |
| "This conversation is longer than the model can read" | the question and its attachments alone do not fit | start a new chat, or attach less |
| "… cannot see images" | the chat's model has no vision | choose a model that shows "Sees images" |
| "You may not use …" | an admin took the model away from you | choose another model |
| "… is not loaded right now" | the chat's model is not the one the engine has loaded | choose a loaded model, or ask an admin to load it (Admin → Models) |
| "Waiting for your turn: N answers ahead of you" | the model is serving others; your answer is in line | nothing: it starts on its own. An admin can change the limits (Settings → Chat) |
| "The model has been busy for 10 minutes" | the line did not move for that long | ask again later; tell an admin if it happens often |
| An API call answers **429** | the key already has as many requests running as it may | wait for one to finish, or retry; an admin sets the limit (API requests at once, per key) |
| "Argus is not available for this answer: …" | Argus's reason follows | usually no GitLab account matches the person's email; see [ARGUS.md](ARGUS.md) |
| "… is not available for this answer: … did not answer" | an MCP server is down or refused the key | Admin → Tools → the server's **Edit** → **Test** |
| No **Image generation** in the Tools menu | no image model at the gateway | turn on the `image` profile (Settings → Deployment) and apply it |
| No **Python** in the Tools menu | the sandbox is not running | turn on the `sandbox` profile and apply it; `scripts/sandbox-check.py` says whether it is sound |
| No **Web** in the Tools menu | it is off (the default), or no site is allowed | Admin → Tools → Web on, and Settings → Python and web → Sites the chat may open |
| "… is not one of the sites the chat may open" | the page's site is not allowed | allow it in Settings → Python and web, or `*` for any public site |

## How it is tested

- **Backend (xUnit):** streaming, tool rounds (Argus's rows reach the model and
  the page as one JSON list), each built-in tool, a chat's own tools and who may
  use them (off, groups, off in new chats), asking first (declined and allowed,
  and nobody else can answer), pictures kept as the person's files and deleted
  with the chat, an MCP server tested, added, called with its key and the
  person's email, and removed, the calculator's parser (exact to 28 digits,
  code refused), the no-access notice, stop, the budget sentence,
  a revoked key being replaced, attachments, and ownership. Also the GitLab
  link address applying at once.
  Also branches (edits, answering again, switching, parents from another chat
  refused), archiving (and coming back when written in), forks (up to the
  chosen answer, with settings and files; never inside a tool round; the owner
  only), a deleted chat's files (kept while a fork uses them), a chat's instructions and parameters, retries with another model,
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
  - the list's fork, archive, Archived view and unarchive; fork from an
    answer; the question rail; the archived notice
- **Browser (Playwright), desktop and phone, both themes, with axe, in CI
  too:** a chat with Argus's answers and two images, served by the browser
  itself, from the links to the image viewer.
- **Browser, against the real model:** the model calls the calculator and gets
  the exact product of two nine-digit numbers, and draws a picture with the
  image tool, which opens full size.
- **Browser, with or without a model (in CI too):** the Tools menu turns a
  tool off for a chat; the Tools and Groups pages pass axe in both themes; a
  very long title leaves no sideways scrolling; a chat forked, archived, found under Archived chats,
  brought back and deleted; the question rail and a fork from an answer.
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
