# Chat

The chat is at `https://DOMAIN/chat`, for everyone who can sign in. To use
the models from your own tools (Claude Code, Qwen Code, an editor, a script),
see **Connect your tools** (`/setup`): your API key, the gateway's address and
setups to paste.

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
- **Answer now.** While the model thinks, **Answer now** beside its thinking
  stops it there and has it answer at once, without thinking (as ChatGPT's and
  Gemini's do): what it thought so far is kept ("Thought for 6 s, cut short"),
  and the rest of that answer, after tool calls too, thinks no more. For every
  answer of a chat to be quick, choose a lighter thinking level or *No
  thinking*.
- **Instructions and parameters.** Per chat: your own instructions (sent after
  the app's), temperature, top-p, and the longest answer. Empty means the
  model's default.
- **Streaming, stop, answer again.**
  - **Stop** keeps what was written, marked *Stopped*.
  - **Answer again** writes a new answer beside the old one; both stay. It can
    also use another model or thinking level, for that answer only.
  - **Shorter** and **Longer** (in the same menu) answer again at about half
    or twice the words of the answer on screen.
- **Answer length.** Your account → Answers: **Short** (the answer first, a few
  sentences or a short list, no preamble), **Normal** (the model judges) or
  **Thorough** (reasons, cases, examples). Said to the model on every
  question, in every chat.
- **Context.** The gauge beside Send shows how full the model's context is,
  from the last answer's prompt as the model counted it (and what it
  answered). Opened, it shows what fills it: the system prompt and the tools'
  notes, tool definitions, your instructions, a summary of earlier messages,
  files, your messages, answers, tool calls and results, what is kept for the
  answer, and what is free. Each request is measured by kind in characters and
  scaled to its prompt tokens.
- **Compact.** Near the limit the chat compacts itself: the model summarizes
  the older messages, and the next answers read the summary instead. **Compact
  now** in the gauge (or the chat's menu, or sending `/compact`) does it at
  once. Nothing is deleted: the messages stay on screen above a mark.
- **Tools on demand.** Every tool's definition in every request is thousands of
  tokens before the question (Argus alone is about 5,000). Past **Tool
  definitions sent whole up to**, a chat sends in full only the tools it has
  loaded, and a line for each other tool (its name, what it does, its
  functions); the model calls `load_tools` when a question needs one, and it
  stays loaded in that chat. A first question that needs no tool reads about
  900 tokens instead of 6,000 or more. Loading costs the engine one read of the
  chat, once per tool and chat.
- **Prompt cache.** The engine reads a prompt's unchanged start from its
  cache, so each request keeps its start unchanged: the date (to the day), the
  tools' notes in a fixed order, your instructions, then the assistant's files;
  the tools stay in every round (on the last allowed round calling them is
  switched off, and the model is told); and with compaction off, the oldest
  messages are left out a quarter of the room at a time, not one by one. The
  next turn of a long chat reads more than 99% of its prompt from the cache.
  The share per model is on Admin → Dashboards → LLM Overview.
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
  - **Image generation**: a picture from a description, made by the picture
    model (FLUX.2 klein). It shows in the answer, opens full size, and is a
    file of the chat.
  - **Video generation**: a clip of 1 to 5 seconds from a description, made by
    the video model (Wan2.2 TI2V 5B). It takes minutes (more while the chat
    model fills the GPU); it plays in the answer and is a file of the chat.
  - **Speech**: a text read aloud into an MP3 (a voice-over, a pronunciation),
    in English or Persian.
  - **Calculator**: exact arithmetic to 28 digits, so the model does not
    guess. Functions like sqrt and sin are good to 15 digits.
  - **Date and time**: the time in any time zone, and the days between dates.
  - **Python**: code run in the sandbox. It has data
    libraries (numpy, pandas, polars, pyarrow, duckdb, scipy, statsmodels,
    scikit-learn, xgboost, lightgbm, numba, sympy, networkx), charts
    (matplotlib, seaborn, plotly), images (pillow with HEIC, OpenCV,
    scikit-image, imageio, tesseract OCR in English, Arabic, Persian and
    Chinese, simplified and traditional), maps (geopandas, shapely, pyproj),
    office and PDF files, and LibreOffice, pandoc, ffmpeg, ImageMagick and
    graphviz; Noto fonts for most scripts, Chinese, Japanese and Korean
    included. The chat's files are in its working directory under their names
    (the original .xlsx, not its text); what it writes comes back: charts as
    pictures in the answer, other files in the Files panel and on the card, to
    open or download, and a plotly chart saved as HTML opens running. Each run
    starts afresh, has one CPU and no network, and stops at its time limit (60 s
    unless changed).
  - **Reading files**: a long attachment goes into the question only up to a
    budget (30,000 characters), with a note saying how long it really is; the
    model reads on by lines, or searches it, when it needs more. Files a tool
    made are read the same way.
  - **Web** (off until an admin turns it on): search (the stack's SearXNG)
    and reading pages, from the sites an admin allows only.
    Pages are read in parts, as text; PDFs and documents on the web too. With
    a **focus** (what the model looks for), a long page comes back as its
    passages about that, each with where it starts, instead of its first
    part. A page read lately comes from a cache for a day: reading on, or the
    same page in another answer, does not download it again.
  - **Questions for you**: when a request leaves a choice open, the model asks
    instead of guessing, as Claude does: one to four questions, each with a
    few choices (pick one, or several where it says so), and a box to write
    your own. Its answer ends there. **Send answers** sends your choices as
    your next message (one line per question), so you can also just type a
    reply, today or days later. Nothing waits on the server meanwhile.
  - **Sub-agents**: for a task whose parts do not need each other (how three
    repositories log errors, two designs side by side), the model splits it
    into two to ten parts, each done by a sub-agent of its own: a clean
    context with only its instructions, and the chat's tools (but not
    delegating again, asking you questions, or tools that ask before each
    call; on demand, they start with the tools the chat loaded). They run
    side by side, up to **Sub-agents at once** (Settings → Chat, 3) and never
    more than the engine serves at once (one more would push another's cache
    out), inside the answer's place in line; their results, in order,
    come back to the model, which puts them together. The card shows how many
    are done and what each is doing, then each part's result.
  - **MCP servers** an admin added: their tools, by name.
  - **APIs** an admin added by their OpenAPI document: each operation is a
    function (`pets__list_pets`); a call that changes something (anything but
    GET) waits for **Allow**, whatever the tool's own setting.
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
- **Long tool calls.** A call to Argus or an MCP server may run for an hour
  (**Settings → Chat → Longest tool call**); an MCP server can have its own
  limit, up to 24 hours (**Admin → Tools →** the server's **Edit** → **Longest
  call**).
  - While it runs, its card shows how long it has run, and the progress the
    server reports (MCP progress notifications): a bar when it knows the end,
    and its words.
  - The client reads the server's events as they come, answers its pings, and
    keeps the connection alive (TCP keep-alive), so a firewall does not drop
    a call that is quiet for a while. A stream that breaks is picked up where
    it broke (`Last-Event-ID`), up to five times, when the server numbers its
    events.
  - A call past its limit, or in an answer that is stopped, is cancelled at
    the server too (`notifications/cancelled`), and the model is told why.
- **Code.**
  - Every code block shows its language or file name, with **Copy**, wrap,
    **Download** and line numbers.
  - Blocks over 40 lines fold.
  - A block the model names (```` ```ts title="src/a.ts" ````, or a first line
    `// file: src/a.ts`) opens in the Files panel.
- **Diagrams.** A `mermaid` block is drawn where it is written, as a diagram:
  ask Argus for a repository's workflow, architecture or data flow and the
  answer shows it. **Code** shows its source, **Preview** opens it larger in
  the Files panel.
  - It is drawn by the preview runner (below), in a frame as tall as the
    drawing (taller than 1200 px, it scrolls), in the chat's theme.
  - One that does not parse shows its code, with Mermaid's error.
  - While the answer is still being written, the block being written is
    shown as code; it is drawn once it is done.
  - The model is told to quote labels with punctuation and to give each
    diagram an `accTitle`, which names it for screen readers.
- **Right-to-left text.** Persian, Arabic and Hebrew read right to left, as in
  ChatGPT: each paragraph, heading, list, quote and table of an answer takes
  the direction of its first words, so Persian and English sit side by side.
  Questions, the message box, the edit box, thinking and a chat's instructions
  follow what is typed. Code, code blocks and formulas stay left to right.
- **Files panel.** Like Claude's: every attachment, every file Argus read (with
  **Open in GitLab**), and every file the model wrote in the branch on screen.
  It keeps the newest version of each and has a viewer. Files made by a
  tool or a sub-agent show as soon as they exist. **Download all** (the
  archive icon) saves every file listed as one zip.
  - **Documents** (PDF, Word, PowerPoint, Excel, OpenDocument) show as their
    pages, drawn on first look in the sandbox (LibreOffice to PDF, then each
    page a picture) and kept: the first 20 pages, the rest in the download.
    **Text** beside **Pages** shows what the model read.
  - **Zoom**: pages zoom in the panel; a picture or a page opens full size
    with zoom in and out (the buttons, `+` `-` `0`, Ctrl and the wheel).
- **Previews.** Like Claude's artifacts and ChatGPT's canvas: code the model
  writes as `html`, `svg`, `mermaid`, `jsx` or `tsx` has a **Preview** button.
  It opens in the Files panel, running, with a **Code** tab beside it; the panel
  can take half the page, and **Run again** starts it afresh.
  - A page runs as written, scripts included. Tailwind's CDN script is served
    from the stack instead.
  - A React component is shown from its default export. It may import `react`,
    `react-dom` and `lucide-react`, and style itself with Tailwind classes. Any
    other import is refused with a sentence naming it.
  - A Mermaid diagram and an SVG picture are drawn in the chat's theme.
  - A page or picture Python wrote, or one attached, previews too, from the
    file itself (a plotly chart carries its 4.7 MB library, past the text the
    model reads).
  - Errors, and anything the code tried to fetch, show under the preview.
  - The model is told all of this in its system prompt, so it writes code that
    previews.
  - **The sandbox.** `/preview.html` is served with a `sandbox` policy (no
    `allow-same-origin`), so it runs in an origin of its own: it cannot read
    the app's cookies or storage, call its API, or reach any other address
    (`connect-src 'none'`), and only the chat may frame it. The frame is
    sandboxed on the chat's side too. The runner is its own build
    (`vite.preview.config.ts`); React, the icons (0.75 MB) and Mermaid (5.5 MB)
    load only when a preview needs them.
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

## Assistants

An assistant is a chat's starting point that a team can share (as custom GPTs,
Gems and Claude's projects): instructions, files, a model, a thinking level,
tools and a few conversation starters. **Workspace → Assistants** is the
gallery: every assistant you may use, with search, who made it, how far it is
shared, and how much it is used (chats started, people in the last 30 days).
The chat list shows yours, those you edit and those you chat with.

- **Starting a chat.** **Start a chat** (in the gallery or on its page) opens a
  new chat with the assistant's name and starters; a starter is a first
  question in one click. The chat starts with the assistant's model, thinking
  and tools, as far as you may use them (a model or tool you may not use is
  left to the defaults). You can change them in the chat as usual.
- **What every answer reads.** Its instructions (after the app's, before the
  chat's own) and its files (text inline up to a budget, the rest by
  `read_file`; Python opens them too). A change applies to the next answer
  of every chat with it.
- **Moving a chat.** A chat's menu → **Move to assistant**, or **Without …** to
  go on without it.
- **Who may use it.** It is yours alone until you share it (its page →
  **Sharing**): with **chosen groups** (groups you are in; an admin may choose
  any), or with **everyone** (admins only). People you choose, and members of
  groups you choose, may **edit** it (instructions, files, settings); only its
  owner shares or removes it, and admins too once it is everyone's.
- **Who sees it.** Nobody else, admins included: the gallery, its page, a chat
  with it and its files are refused to anyone it is not shared with, as if it
  did not exist. Chats with it stay each person's own; its page lists only
  yours.
- **Losing access.** Taken out of the group, a person no longer sees it. Their
  chats with it say so and answer no more with it; **Go on without it** keeps
  the chat without the assistant.
- **Removing it.** Its chats stay, without it (yours can go too). Sharing an
  assistant, and removing a shared one, are in the audit log.

Projects became assistants: each project is a private assistant with the same
instructions, files and chats.

## Shared chats

A chat's menu → **Share** makes a read-only link to it, for **everyone in the
company** or **chosen groups** (groups you are in; an admin may choose any).
It shows **the whole chat** (its branches, as it grows) or **the branch on
screen** (up to its last message, as it is now). Making the link copies it.

- **Opening it.** A link works only signed in, and only for the people it is
  for; for anyone else it does not exist. It shows the messages and their files
  (the Files panel too), with the branch arrows, and nothing to write with: no
  box, no editing, no answering again, no tool approvals.
- **Fork into my chats** copies it (or, from an answer's **Fork from here**, up
  to that answer) into a chat of your own, with copies of its files, so the
  fork stays whole whatever becomes of the original. The owner's own
  instructions for the chat are not copied.
- **Its owner** sees how many times it was opened and by how many people (their
  own visits do not count), changes who may open it or what it shows (the
  address stays), and **Revoke link** stops it at once, for everyone. Deleting
  the chat revokes it too. One link a chat.
- Sharing and revoking are in the audit log, with the chat's id, never its words.

## Sound and video

- **Voice messages**: the microphone beside the paperclip records one; the
  answer to a voice message is read aloud.
- **Sound and video files** attach like any file (MP3, WAV, OGG, M4A, WebM,
  MP4, MOV, MKV) and play in place. On upload the sandbox's ffmpeg makes what
  the models take: a sound as an MP3, a video as up to eight frames and its
  sound track.
- **What the model gets**: a model that hears (Qwen3-Omni: its projector reads
  sound) gets the sound itself; one that does not gets a transcript (Whisper,
  made once and kept). A video's frames go as pictures to a model that sees,
  with the second each was taken at.
- **Read aloud**: under every answer, the speaker reads its prose (no code, no
  links' addresses): Kokoro's voice, or a Persian voice for Persian.
- The speech models are at the gateway too: `/v1/audio/transcriptions` and
  `/v1/audio/speech` with a person's key.

## Scheduled tasks

**Scheduled tasks** (in the sidebar, `/tasks`) ask a question on a schedule, as
you: a morning digest, a weekly report on a repository with Argus.

- **The schedule** is chosen in words (every day, weekdays, once a week or a
  month, every few hours, at a time) or written as cron (five fields), in a
  time zone (your browser's by default). The card shows it in words and the
  next three runs. A task may not run more often than **Most often** (15
  minutes by default).
- **Each run** asks the question with the task's model and tools (or the ones
  a new chat would use), as any answer: in turn with everyone else's, against
  your credit. It is a new chat, titled with the task and the time, or with
  **One chat for every run**, the same chat carried on, so each run reads the
  ones before ("what changed since yesterday").
- **Delivered** under the bell in the header (a toast too, when the page is
  open), and if asked by email (to your account's address; an admin sets the
  mail server under **Settings → Email**) and to a channel: a Slack, Teams,
  Mattermost or Discord incoming webhook. The post carries `text` (and
  `content`) with the answer and a link to the chat, and `task`, `status`,
  `url`, `at` beside them. A webhook URL is a secret: it is stored encrypted
  (`APP_KEY`) and never shown again, and only hosts an admin allows
  (**Webhook hosts**) are posted to, so a task cannot reach into the network.
- **Run now** runs it at once; the card shows the last run (done, failed or
  skipped, why, what was delivered) with a link to its chat. A run is skipped
  while the one before is still answering, or when its owner is disabled.
- A task that was due while the app was down runs once when it is back. A
  removed task leaves its chats. Each person sees and changes only their own;
  admins set **Tasks per person** (10).
- **Run by events** instead of a schedule: **On GitLab events** (a merge
  request opened or updated with new commits, a pipeline failed, an issue
  opened) or **On a webhook** (any system posting JSON). The task gets an
  address, `https://DOMAIN/api/hooks/<id>`, and a secret shown once (**New
  secret** makes another): in GitLab, a project's or group's webhook with that
  secret token; elsewhere the secret in `X-Hook-Secret`. The event, in words,
  follows the task's question: a merge request with its changes, a failed
  pipeline with the end of each failed job's log, a webhook's JSON as it came.
  An event the task does not take is acknowledged and dropped.
- **Answer as a comment in GitLab**: the answer goes on the merge request,
  issue or commit. Reading the changes and logs and commenting use the **GitLab
  bot token** an admin sets (Settings → Scheduled tasks, a bot account's token
  with scope `api`, Reporter in the projects), never Argus's read-only token;
  each comment is in the audit log (`task.gitlab_comment`). Examples: review
  every merge request with Argus for context; explain each failed pipeline;
  triage new issues.

## Notifications

The bell in the header holds news for you, newest first; the page checks it
every 15 seconds and when the tab comes back, and shows what arrives as a toast:

- **Answer ready** (or failed): an answer that ended while no page of yours
  watched it (you left the chat or closed the tab). One you watch is not news.
- **A scheduled task ran**, with its answer (see above).
- **Credit**: at 80% of your credit, and when it is used up (then the admins
  hear of it too). Each once per budget: a raised budget says it again when
  reached.
- **System alerts** (admins): an alert that starts firing (Alertmanager), once
  per firing. Silenced ones are not news.
- **Model downloads** (the admin who started one): done or failed.

**Desktop notifications** (a switch at the bottom of the bell) say the same on
your desktop while the app's tab is hidden, and an answer that finishes while
you look elsewhere. The browser asks once; the switch turns them off again.
Alerts and credit are looked at every minute and every five minutes.

## Safeguards

Under **Settings → Safeguards** an admin sets what keeps the chat from being
abused or used to harm; each part can be turned off, and all of them with
**Safeguards on**:

- **Limits per person**: the longest message and files per message; messages a
  minute (20) and a day; pictures a day (100) and deep research answers a day
  (20). Past one, the message is refused with what to do instead (HTTP 429).
- **Blocked words and patterns**: phrases matched as whole words, ignoring
  case, or regular expressions (`re:`). A message with one is refused.
- **The model checks each message** (off by default): before answering, the
  chat's model reads the message as a classifier and refuses one asking for
  harm in the chosen categories (violence, self-harm, sexual content involving
  minors, mass-casualty weapons, malware, hate, fraud); learning, safety, news
  and fiction stay allowed. When the check cannot run, the message goes.
- **Mask personal data** (off by default): e-mail addresses, phone and card
  numbers (Luhn-checked) and IBANs reach the model masked; the chat keeps them.
- **The web's content is marked as data**, so the model never follows
  instructions hidden in a page (prompt injection).
- A refused message is in the audit log (`safeguard.refused`) and, by default,
  in the admins' bell; **Refusals that suspend an account** (off by default)
  disables an account after that many in a day.

They apply to the chat; API keys go straight to the gateway, where the credit
and the requests-at-once limit apply.

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
  every query is filtered by the signed-in person, attachments too. The
  exceptions are the owner's to make: a shared chat's link (read-only, for the
  company or chosen groups) and an assistant's files (for the people it is
  shared with).
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
  it. A server's key is stored encrypted under `APP_KEY` and never shown.

## Cost and credit

The chat talks to LiteLLM with its own service key (alias `chat`). Each request
names the person, so spend is theirs (surface **Chat** under Usage & cost), and
their credit applies. The cost under each answer comes from the model's prices
at the gateway, its sub-agents' calls included.

What a person has spent is what the gateway's request log puts to them, over
every path (chat, API keys, agents), by the same rule as the usage dashboards;
the overview, People, the export and Home all read it. LiteLLM's own counters
split a person in two (the chat is booked to them as an end user, their keys
as an internal user), and each limits its own path to the budget.

## Settings

The chat's limits are under Admin → Settings → Chat ([settings.md](settings.md)):

| Setting | Default | What it is |
|---|---|---|
| Tool calls per answer | 8 | how many rounds of tool use one answer may take |
| Tool definitions sent whole up to | 6,000 characters | past it, tools go on demand (below); 0: always whole |
| Tool result the model reads whole up to | 24,000 characters | a longer result goes as its start, and the whole of it becomes a file in the chat that the model reads on with `read_file`; 0: always whole |
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

A chat's link is `GET`, `PUT` and `DELETE /api/chat/conversations/{id}/share`
(`reach`: `Company` or `Groups`, with `groups`; `branch` with an optional
`messageId`). `GET /api/shared/{link}` reads it and `POST /api/shared/{link}/fork`
forks it. Assistants are under `/api/assistants` (`PUT .../sharing` for who may
use and edit one); a new chat takes `assistantId`.

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
| "Argus is not available for this answer: …" | Argus's reason follows | usually no GitLab account matches the person's email; see [ARGUS.md](argus/README.md) |
| "… is not available for this answer: … did not answer" | an MCP server is down or refused the key | Admin → Tools → the server's **Edit** → **Test** |
| No **Image generation** or **Video generation** in the Tools menu | the model is off, or its server does not run | Admin → Models: turn it on; a module left out in `docker-compose.override.yml` stays out |
| No **Python** in the Tools menu | the sandbox is not running | `docker compose ps sandbox`; `scripts/sandbox-check.py` says whether it is sound |
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
  image, and the migration of existing chats. Assistants: a private one's
  instructions and files in its chats and nobody else's, one shared with a group
  used by its members and invisible to others (gallery, page, a new chat, its
  file), a member who left the group told so and stopped, editors who change it
  but neither share nor remove it, only admins making one everyone's, and a new
  chat's model, thinking and tools. Shared chats: a link opening for a colleague
  in the group and not for someone outside it or signed out, its files, the
  owner's numbers, revoking stopping it, a fork with copies of the files and
  without the owner's instructions, and a shared branch showing only that
  branch. Real Postgres, a fake model and a fake Argus.
- **UI (Vitest):**
  - the branch tree
  - the live-stream reducer
  - file and fence parsing
  - reading Argus's answers: each tool's rows, GitLab links on the branch
    asked about, search matches without marking the code's own brackets, and
    files Argus read in the Files panel
  - what previews (a fence's own language, a file name, an `<svg>` or a page
    in bare `xml`), the page rewrite (reporter first, Tailwind and Mermaid from
    the stack), compiling a component and refusing a library it lacks
  - the page on a fake API: a new chat with its settings, streaming with
    thinking, tokens and cost, tool cards and the no-access notice, stop, a
    failed answer, a message given back, an edit and its version arrows,
    answering again with another thinking level, image previews and the "cannot
    see" note, settings validation, and hostile HTML and maths
  - Argus's answers linking to GitLab, **Raw answer**, a failed tool in its
    own words, and the image viewer (arrows, keys, actual size)
  - the list's fork, archive, Archived view and unarchive; fork from an
    answer; the question rail; the archived notice
  - the assistants gallery (use, search, yours), a new assistant, a chat started
    from a starter, an assistant read-only for its users, its owner changing its
    starters and sharing it with a group and an editor, and a chat whose
    assistant was taken away going on without it
  - a shared chat read-only with its files and forked, a link that does not
    open, the owner's numbers and revoking, and sharing from the chat's menu
    (chosen groups, the branch on screen, revoked)
- **Browser (Playwright), desktop and phone, both themes, with axe, in CI
  too:** a chat with Argus's answers and two images, served by the browser
  itself, from the links to the image viewer.
- **Browser, against the real model:** the model calls the calculator and gets
  the exact product of two nine-digit numbers, and draws a picture with the
  image tool, which opens full size.
- **Browser, the preview runner (in CI too, `e2e/preview.spec.ts`):** a page's
  script runs and its Tailwind CDN script comes from the stack; a page can read
  neither the app's cookies nor its storage, and both the app's API and another
  site are refused (and reported); a React component with state, an icon and
  Tailwind classes; a missing library named; an SVG and a Mermaid diagram; and
  the runner's policy (sandboxed, no network, framed by the chat only).
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
  - a page the model wrote running in the Files panel, with its code a tab away
  - a plotly chart Python wrote, opened running from the file it made
  - the chat list's rename, search and delete
  - a chat's instructions reaching the model
  - with the test GitLab, the no-access notice
