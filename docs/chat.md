# Chat

The chat is at `https://DOMAIN/chat`, for everyone who can sign in. To use
the models from your own tools (Claude Code, Qwen Code, an editor, a script),
see **Connect your tools** (`/setup`): your API key, the gateway's address and
setups to paste, and [Code Arena](code-arena.md), our own coding agent, to
download. For a GitLab pipeline, see [ci.md](ci.md). Your agent can use your
chat tools too, with the same key: [Arena MCP](mcp.md) at `https://DOMAIN/mcp`.

## What it does

- **Models.** The picker lists the models you may use (Admin → Models), with
  what each can do: context size, tools, thinking, and whether it can see
  images. A chat keeps its model. The default is the loaded model. An engine
  model that is not loaded is listed greyed out, "Not loaded now": an admin
  loads it.
- **A model for small steps.** With **Settings → Model → Model for sub-agents
  and small steps** set, the many short steps around an answer go to that
  small, fast model instead of waiting on the big one: sub-agents, the chat's
  title, compaction summaries, the safeguards' check, and Auto's sorting (below).
  It never thinks for them. The answer itself stays with the chat's model.
  Under an answer, the sub-agents' model shows beside the answer's when it is
  another, and each sub-agent's line names its model. For someone who may not
  use the small model, or while it cannot load, each step uses the answer's
  own model.
- **Auto.** While a model for small steps is set, the picker offers **Auto**
  (an admin can make it the default: **Model new chats use** `auto`). The small
  model sorts each question in one short call: small talk, a quick lookup or
  rewrite, code, reasoning, or research. It answers small talk and quick
  lookups itself, without thinking. The rest go to the main model (the
  default one), thinking as hard as they need: code a little, research
  lightly, reasoning at the deepest level the chat offers. Questions with
  files, deep research, and an answer again with a thinking level go to the
  main model unasked; so does a question the small model cannot sort. A note
  under each answer says which model answered and why. Under the small
  model's, **Ask the big model** answers again with the main model, beside it.
  Auto is offered only to people who may use the small model; a chat on Auto
  whose person may no longer use it gets the main model, and the note says so.
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
  - **Queue.** A message sent while an answer runs waits its turn on the
    server, shown above the box as queued. It survives a reload, another tab
    and closing the page, and becomes the next question when the answer before
    it ends (however it ended), on the branch on screen. **Cancel** takes one
    out of line; **Send now** stops the answer (keeping what it has) and sends
    it at once. Up to 10 wait per chat. Each is checked as a sent message is
    (the safeguards), when it is queued. Messages still waiting when the app
    restarts go once it is back.
- **Deep research.** **Deep research** in the composer, for the next message:
  the web and sub-agents are on for that answer, and the model plans the
  research questions and gives each to a sub-agent, which searches the web
  and reads the best pages. It may send the gaps to sub-agents once more, then
  writes a report with numbered sources. It delegates twice at most: the
  delegate calls of one step count once, and one refused for its parts or
  not allowed does not count. The sub-agents read the web, not the model
  writing the report: on the small model they are faster. When **Web** asks
  before each call (Admin → Tools), sub-agents cannot use it (nobody is there
  to allow their calls), so the model researches with the web itself, each
  call waiting for your **Allow**. While it works, a line under the answer
  says which step it is on: planning the research, researching 4 parts (2 of
  4 parts done), filling gaps (1 of 2 parts done), writing the report.
- **Answer trace** (admins). The timer under an answer opens where its time
  went: the wait in line, getting ready (the chat's tools started, the chat
  read), each round of the model (tokens in, the share from the cache, tokens
  out, the first token's wait, the engine's read and write speeds), each tool
  call, and each sub-agent with its own time, tokens, speeds and tool calls.
  The slowest step is named, with why ("mostly the model: 2,900 tokens written
  at 5.6 a second"). Times, tokens and sizes only, never words; Admin → Traces
  lists the slowest answers across people.
- **Answer length.** Your account → Answers: **Short** (the answer first, a few
  sentences or a short list, no preamble), **Normal** (the model judges) or
  **Thorough** (reasons, cases, examples). Said to the model on every
  question, in every chat.
- **Memory.** "Remember that I deploy with Podman" is kept, and every later
  answer, in any chat, knows it; what the model offers to remember on its own
  waits for your yes. Your account → Memory (or the brain in a chat's header)
  lists, edits and deletes them ([below](#memory)).
- **Prompts.** `/` at the start of a message finds a prompt of the library
  (Workspace → Prompts): yours, your groups', the company's or a plugin's.
  Chosen, it asks for its blanks and sends it filled in
  ([below](#prompts-and-slash-commands)).
- **Rating answers.** Thumbs up or down under each answer. Down asks why:
  wrong, incomplete, too long, unsafe, ignored instructions or other, and a
  few words if you like. One rating per answer; rate again to change it, or
  press the thumb that is on to take it back. Admins see the ratings per model
  and per assistant (Admin → Quality), with the chat's title, the model and your
  reason, never its content, unless you tick **Share this chat with the
  admins** with a down vote: then they can read the chat down to that answer.
- **Compare (arena mode).** **Compare** beside Deep research sends your next
  question to two models: two you choose, or two at random of those you may
  use that can answer now. They answer one after the other (the engine may
  hold one model at a time), and the answers show side by side as **Model A**
  and **Model B**, which is which drawn. Vote **A is better**, **B is
  better**, **a tie** or **both are bad**, and the names show. A vote for one
  answer makes it the branch on screen, and the chat goes on from it. The
  names stay out of everything the page is sent until the vote, the chat's
  history included; a model may still name itself in its own words. Votes
  make the **Leaderboard** (in the sidebar): the company's models on its own
  questions, by Elo rating, with their wins, losses, ties and win rate.
  Admins can keep it to themselves (Settings → Chat → **Arena leaderboard for
  everyone**).
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
  tools' notes in a fixed order, what you asked it to remember, your
  instructions, then the assistant's files;
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
  - **Company knowledge**: the company's documents you may read (GitLab
    wikis and issues, Confluence, SharePoint and OneDrive, folders, websites
    an admin added under Admin → Knowledge), found by meaning; the model
    cites each passage with its title and link. A project's wiki is found
    only by its members in GitLab, a Confluence page only by whom Confluence
    lets view it. See [knowledge.md](knowledge.md).
  - **Image generation**: a picture from a description, made by the picture
    model (FLUX.2 klein). It shows in the answer, opens full size, and is a
    file of the chat.
  - **Video generation**: a clip of 1 to 5 seconds from a description, made by
    the video model (Wan2.2 TI2V 5B). It takes minutes (more while the chat
    model fills the GPU); it plays in the answer and is a file of the chat.
  - **Speech**: a text read aloud into an MP3 (a voice-over, a pronunciation),
    in the person's voice for its language ([Your voice](#your-voice)).
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
  - **Canvas**: documents and code beside the chat, which you and the model
    both edit (see **Canvas** below).
  - **Reading files**: a long attachment goes into the question only up to a
    budget (30,000 characters), with a note saying how long it really is; the
    model reads on by lines, or searches it, when it needs more. Files a tool
    made are read the same way. With the embedder, a long attachment goes in
    as its start, and the passages that match each question come with the
    question, with the file's name and lines (below).
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
    parts are done ("2 of 4 parts done") and what each is doing, then each
    part's result.
  - **Memory**: the model remembers what you ask it to across your chats, and
    offers to remember what would help later ([below](#memory)).
  - **Decide (Laya)** (when the laya module runs): typed questions about a
    text, answered with a probability for every option in a fraction of a
    second by Laya, a decision model on the CPU that never writes text
    ([below](#decide-laya)).
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
    pages, drawn in the sandbox (LibreOffice to PDF, then each page a
    picture) and kept: the first 20 on first look, and the next 20 as you
    scroll to the end (or with **Show pages 21 to 40**), to the last page.
    Each page is drawn once. **Text** beside **Pages** shows what the model
    read.
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
  question becomes the title and the browser tab's name; with a model for
  small steps, it writes a short title from the question while the answer is
  written (a title you gave meanwhile stays). Long titles are cut
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
- **Phones.** The chat list, the Files panel and the canvas open over the
  thread; the header fits a narrow screen.

A question that never reached the server goes back into the box with its
attachments, instead of being lost.

## Long files and assistants

With the embedder (the `embed` module), files are read by their passages
instead of their first part ([knowledge.md](knowledge.md#long-files-and-assistants)):

- An attachment longer than **Text of an attachment in the question** goes in
  as its first 2,000 characters and a note; with each question come the
  passages of the chat's long files that match it, best first, each with the
  file's name, its lines and its section, up to **Passages of long files per
  question** (12,000 characters).
- An assistant's files that together do not fit (three times that budget) are
  named in the system prompt, and go by their passages the same way. An
  assistant may have 200 files.
- Files are embedded in the background, an assistant's file when it is added; one
  not ready yet goes in as before for that answer.

Without the embedder, files go in as before.

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

## Canvas

Like ChatGPT's canvas and Claude's artifacts: a document (Markdown) or code (in
a language) beside the chat, which you and the model both edit. A chat has as
many as it needs (up to 50).

- **Opening it.** **Canvas** in the chat's header (with how many the chat has)
  opens the panel where the Files panel opens; one of the two shows at a time,
  and it takes half the page until narrowed. A canvas the model makes or
  changes opens by itself on a wide screen; its tool card has **Open** too.
  **New document** and **New code** start one by hand.
- **The model writes by changes.** The **Canvas** tool (on in new chats) has
  four functions. `canvas_create` writes a new canvas. `canvas_read` reads one
  as it is now (by lines for a long one), or lists them. `canvas_edit` replaces
  exact text: each `find` must be in the canvas once, the edits apply in turn,
  and all or none do. A `find` that is missing, or there more than once, is
  refused with the reason (and the lines it is on), and nothing changes; the
  model reads again and retries. `canvas_rewrite` replaces the whole text, only
  to start over. So a change to one section leaves the rest as it was. The
  model is told the chat's canvases (title, id, kind) and to read one before it
  edits it, since you may have changed it.
- **Editing.** A plain text box: code with line numbers beside it and no
  wrapping; a document with **Edit** and **Preview** (the Markdown formatted, as
  in answers). **Save** (or Ctrl+S) saves; unsaved text stays while you look at
  another canvas.
- **Versions.** Every save of yours and every change by the model is a version:
  who made it, when, and a short summary (the model's own words, or "Changed
  lines 4–9"). **Versions** lists them; choosing one shows what it changed, as
  a line diff with the version before (removed lines red with −, added green
  with +, a few unchanged lines around each change). **Restore this version**
  makes its text the newest version; nothing is lost, the others stay.
- **Both at once.** A save is made on the version you started from. If the
  model changed the canvas meanwhile, the save is refused and the editor says
  so: **Load its version** (your edits go) or **Keep mine** (yours become the
  newest version; the model's stays in Versions). A model change while you
  have nothing unsaved simply shows.
- **Asking about a part.** Select text (in the editor, or in the preview) and a
  bar shows its lines: **Ask about this** takes your question, **Make this
  shorter** sends at once. The chat gets a message that quotes the selection
  with the canvas's id and line range, and the model changes that part with
  `canvas_edit`. Unsaved text is saved first, so the model reads what you see.
  While an answer is being written, these wait until it is done.
- **Export** (the download icon) exports the saved version:
  - a document as **Markdown** (.md), **Word** (.docx) or **PDF**. The Word file
    is built by the app itself (no library): headings, paragraphs, bullet and
    numbered lists (nested), quotes, code blocks, tables, links, bold, italic and
    code inside a line; a paragraph that starts in Persian or Arabic reads right
    to left. The PDF is that Word file turned into a PDF by LibreOffice in the
    sandbox. Without the sandbox, the browser makes it: the document opens as a
    page of its own and its print dialog saves it (**Save as PDF**).
  - code as its file: the title when it is a file name (`parser.py`), else the
    title with its language's extension.
- **Who sees it.** A canvas is its chat's: only the chat's owner can open, change
  or export it, and it goes when the chat is deleted. A fork does not copy the
  canvases.

## Sound and video

- **Voice messages**: the microphone beside the paperclip records one; the
  answer to a voice message is read aloud (unless you turned that off). It is
  written down in the language you speak, when you chose one.
- **Sound and video files** attach like any file (MP3, WAV, OGG, M4A, WebM,
  MP4, MOV, MKV) and play in place. On upload the sandbox's ffmpeg makes what
  the models take: a sound as an MP3, a video as up to eight frames and its
  sound track.
- **What the model gets**: a model that hears (Qwen3-Omni: its projector reads
  sound) gets the sound itself; one that does not gets a transcript (Whisper,
  made once and kept). A video's frames go as pictures to a model that sees,
  with the second each was taken at.
- **Read aloud**: under every answer, the speaker reads its prose (no code, no
  links' addresses) in your voice for its language, at your speed.
- The speech models are at the gateway too: `/v1/audio/transcriptions` and
  `/v1/audio/speech` with a person's key. Speech that names no voice is read in
  the key's person's voice for the text's language, at their speed when it names
  none; with no model either, that voice's model. Speech to text that names no
  language is written down in the language the key's person speaks, when they
  chose one.

### Talk

**Talk** (the sound-wave button in the box) is a voice conversation in the
chat. The browser asks for the microphone once.

- **You speak, then pause.** The browser listens all along and cuts what you
  said at a pause of about a second: louder than the room for a moment starts
  it, quiet ends it. What was said is written down (Whisper, in your name, in
  the language you speak when you chose one) and sent as your question. It is
  saved in the chat like a typed one, and answered with the chat's model and
  tools, asked to answer in plain spoken sentences.
- **The answer is read aloud as it is written.** Each sentence is spoken as
  soon as it is complete, in order, while the next ones are still being
  written, each in your voice for its language: a Persian sentence in the
  Persian voice. A short sentence whose words do not say its language
  ("Claro que sí.") is read in the language of the answer so far. Code blocks
  are not read. The text appears as usual. With
  **Read answers aloud in Talk** off (Your account → Voice) the answer is only
  shown, and speaking still stops it.
- **Speak over it to stop it.** Your voice while the answer is read (or while
  it is still thinking) stops the reading and the answer, which keeps what it
  has; what you say next is the next question.
- **The bar above the box** says what Talk is doing: listening, hearing you,
  writing down what you said, thinking, speaking. **End talk** (or Esc, or the
  button again) lets go of the microphone.
- With loudspeakers the microphone may hear the answer: the browser's echo
  cancelling removes most of it, and over an answer Talk waits for louder,
  longer speech before it stops. Headphones work best.
- Thinking makes the first words wait: a chat with **No thinking** answers
  soonest.
- Talk uses the speech server's plain endpoints. Its realtime API is not used:
  it would answer with a model of its own, outside the chat's history, tools
  and credit.

### Your voice

**Your account → Voice** sets how the chat hears you and reads to you. Each
choice is yours or the company's (Admin → Settings → Speech,
[settings.md](settings.md#speech-everyones-until-they-choose)); **Use the
company's** puts them all back.

- **The language you speak**: what you say in Talk and your voice messages is
  written down in it. **Detect it** lets Whisper hear which; naming it helps
  short or accented speech. Other sound and video files are always heard in
  the language Whisper detects. The list is the languages the speech to text
  model knows.
- **Voices**: a voice for each language, from the voices the speech models
  really offer. The app asks the speech server: Kokoro reads American and
  British English, Spanish, French, Italian, Brazilian Portuguese, Japanese,
  Chinese and Hindi, each voice a woman's or a man's; Piper reads Persian. A
  model turned off (Admin → Models) offers none. Under each language is the
  id of the voice that reads it (`kokoro/pf_dora`), as Settings → Speech names
  it. **Try it** reads a sample in the voice, at your speed.
- **A voice of yours no longer offered** (its model turned off) stays yours
  and reads again when it is back. Meanwhile the company's voice reads that
  language, the row says so, and your other choices save as usual; choosing
  the company's for that language alone drops it.
- **A text is read in the voice of its language**: Persian text in your Persian
  voice when English is the one you speak. The language is told by the
  text's script (Persian, Hindi, Japanese, Chinese) and, in the Latin script,
  by its small words (English, Spanish, French, Italian, Portuguese). A small
  word English writes too ("as", "do", "per") counts for another language only
  beside one of its own, so "As far as I know." is not Portuguese. A Latin
  text whose words do not tell (a short sentence, a name) takes the language
  of what came before it in Talk, else the language you speak when it is one
  of these, else English. A language with no voice is read in the voice of the
  language you speak, then in English's.
- **Speed**: 0.5 to 2 times the voice's own pace.
- **Read answers aloud in Talk**: off, the answers in Talk and to your voice
  messages are only shown.
- **Everywhere you hear or are heard**: read aloud, Talk, voice messages, the
  Speech tool, your API key's `/v1/audio/speech` when the request names no
  voice, and its `/v1/audio/transcriptions` when the request names no
  language.
- While the speech server cannot be asked, only the voices already chosen (by
  you or the company) are listed, and they are used as chosen. So too for a
  model it has not listed yet (it lists only the models it has downloaded):
  its voices chosen are used, and the server is asked again each minute until
  it lists them.

## Memory

What you tell the chat about yourself, kept for every later answer, in every
chat, as ChatGPT and Claude do.

- **Asked for.** "Remember that I deploy with Podman": the model calls
  `remember`, and the memory is kept at once. Its card in the answer says
  **Remembered**, with **Undo**. In a scheduled task's chat it is only
  offered: a task's question may carry an issue's or a webhook's words.
- **Offered.** When you mention something lasting (your team, your tools, how
  you like answers) without asking, the model may offer to remember it: the
  card asks **Remember this?** with **Remember**, **No thanks** and **Edit**
  (to change the words first). Nothing is kept until you say so, and nothing
  waits: the answer goes on, and you can decide later.
- **Used.** Each answer gets your memories in a short block of its system
  prompt, the newest first, as many as fit in about 400 tokens (1,500
  characters). The block comes after the app's fixed notes and the tools', so
  a new memory leaves the cached start of the prompt as it was.
- **Yours to change.** Your account → **Memory**, or the brain in a chat's
  header: add one, edit or delete one, **Forget everything**, or turn **Use
  memory** off (answers then neither read nor offer memories; yours stay). Up
  to 100 memories of 300 characters each. A memory said again comes first
  again rather than twice.
- **Nobody else sees them**, admins included: no admin page, audit entry or
  log shows one. Sub-agents do not get the `remember` tool. Deleting a person
  deletes their memories.
- **For the company:** **Settings → Chat → Memory** turns it off for everyone
  (people's memories stay, to see and delete). The **Memory** tool is also
  in Admin → Tools, like any tool (who may use it, on in new chats).

## Decide (Laya)

Typed decisions about a text, by [Laya](https://github.com/NandhaKishorM/laya)
(Convai Innovations, Apache-2.0): a decision model that runs on the CPU and
never writes text. The model asks typed questions with `decide`; Laya gives a
probability for every option, all questions in one forward pass.

- **When.** Classifying, routing, triage, scoring and yes/no checks: which team
  a ticket goes to, how urgent it is, whether a message is spam or asks for a
  refund. Many texts are one call each. The model acts on an answer only when
  its probability is high (0.85 or more), and says when it is not sure.
- **Three kinds of question.**
  - `choice`: one of 2 to 20 options, each with a short description of what it
    covers. The answer is the option, with each option's probability.
  - `score`: an ordered scale of 2 to 10 levels, lowest first. The answer is
    the expected level (2.4 is between the third and fourth level), with each
    level's probability.
  - `noul`: a yes/no statement. The answer is the probability of yes.

  Each answer also has a `confidence`, low when the probability is spread out.
- **What makes answers good.** The tool's description teaches the model:
  ask what the text says, not what to do about it; put numbers and
  comparisons into words first ("the order is a week late", not two dates);
  describe every option; keep option lists short; keep the text short with
  what matters first (about 2,000 characters are read, and when Laya cuts
  the rest the answer says how many of its tokens it read); and ask every
  question about one text in one call. Wording matters: "Does the customer
  threaten to leave?" read a Persian message at 2%, "Does the customer say
  they will stop buying from us?" at 89%.
- **Languages.** English goes to the English checkpoint, whose probabilities
  are calibrated. Other scripts (Persian, Arabic, Chinese...) go to the
  multilingual one (100+ languages), whose are not: the answer says so, and
  100% there means likely, not certain. `checkpoint` names one instead.
- **How fast.** On 4 threads of an i7-13700K: four questions about an English
  incident in about 0.6 s, about a Persian complaint in about 0.2 s (the
  multilingual checkpoint is smaller). Calls take turns: with 16 already
  waiting, or after 8 seconds' wait, Laya answers that it is busy, and the
  model can try again.
- **What it is not for.** Reasoning in steps, arithmetic, pulling values out
  of a text, or writing. It is good at clear categories and yes/no checks,
  and weaker on fine scales and subtle judgements: try it on your own
  examples first.
- **Where.** In Admin → Tools while the `laya` module runs (off by default:
  [deployment.md](deployment.md#laya)); outside agents get `decide` from
  Arena MCP, and Code Arena uses it to look at commands
  ([code-arena.md](code-arena.md#laya-looks-at-commands)).

## Prompts and slash commands

**Prompts** (Workspace → Prompts, `/prompts`) keep what you ask often, with
blanks filled in each time: "Review {{file}} for {{focus}}".

- **In the chat**, `/` at the start of the message opens the list: type to
  find by slash name or title, arrows to move, **Enter** or **Tab** to use
  one, **Esc** to close it. `/compact` is there too. A chosen prompt's text
  comes into the box with a field for each `{{blank}}` (Enter moves to the
  next; on the last it sends). Sending fills them in; it waits until every
  blank has something. A prompt without blanks just comes into the box.
- **Yours**, **shared with your groups** (their members use it; only you
  change it), or **for everyone** (made and changed by admins; audited as
  `prompt.*`). Each has a slash name (lowercase letters, digits, `-` and `_`),
  a title and its text. Your own slash names are unique, and so are the
  company's; the same name from two places shows both, yours first.
- **Plugins bring their own** ([plugins.md](plugins.md#prompts)), for
  whoever may use the plugin's tool, and take them away when removed:
  `gitlab-issues` brings `/triage`.
- Up to 200 prompts per person, 20,000 characters each.

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
  An event the task does not take is acknowledged and dropped. Events that
  come while the task is answering wait their turn (up to 20; then the oldest
  goes), so a busy repository loses none. They wait in the database: a restart
  keeps them, and with several app replicas each runs once, in order.
- **Answer as a comment in GitLab**: the answer goes on the merge request,
  issue or commit. Reading the changes and logs and commenting use the **GitLab
  bot token** an admin sets (Settings → Scheduled tasks, a bot account's token
  with scope `api`, Reporter in the projects), never Argus's read-only token;
  each comment is in the audit log (`task.gitlab_comment`). Examples: review
  every merge request with Argus for context; explain each failed pipeline;
  triage new issues. A team that wants the review and the explanation as jobs
  in its own pipeline instead uses the `arena` CLI and its GitLab CI template
  ([ci.md](ci.md)).

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

**Push notifications** (Your account → Push notifications) bring the same news
to a device with no page of the app open: a phone, or a desktop where the
browser runs with no tab of the app. **Turn on for this device** (the browser asks
once); the card lists your devices, marks this one, sends a test, and removes
one. A push that arrives while a page of the app is in front is left to the
bell. A click on it opens what it is about. On an iPhone or iPad, add the app
to the home screen first, then turn them on there.

**The app installs** like any other: **Install** in the account menu (or Your
account) when the browser offers it, or the browser's own menu. It opens in its
own window; how pushes are signed and sent, and the browser extension, are in
[integrations.md](integrations.md).

**Chats from elsewhere**: a question to the bot in Slack, Mattermost or Teams,
or an email to the app, is answered as you in a chat of yours, titled with
where it came from (`Slack · …`, `Email · …`); you can carry it on here
([integrations.md](integrations.md)).

**By email and webhook.** The credit news (yours) and the alerts and used-up
credit (the admins') also go by email, once each, when email is set up
(**Settings → Email**; **Settings → Notifications → Email the credit and
alert news** turns it off). The admins' news is also posted to the **Alerts
webhook** when an admin sets one (Slack, Teams, Mattermost); its host must be
one of the webhook hosts, as a task's.

## Safeguards

Under **Settings → Safeguards** an admin sets what keeps the chat from being
abused or used to harm; each part can be turned off, and all of them with
**Safeguards on**:

- **Limits per person**: the longest message and files per message; messages a
  minute (20) and a day; pictures a day (100) and deep research answers a day
  (20). Past one, the message is refused with what to do instead (HTTP 429).
- **Secrets in messages and files** (refuse by default): private keys (PEM
  blocks), cloud and service tokens (AWS `AKIA…`, GitHub `ghp_`/`github_pat_`,
  GitLab `glpat-`, Slack `xox…`, `sk-` keys) and passwords in obvious places
  (`password=…`, a connection string's `Password=…;`, `user:password@` in an
  address). **refuse**: the message is not sent, or the file not attached, and
  the person is told what kind was found. **mask**: it goes with each secret
  replaced by `[removed: GitHub token]`, in the chat as well as to the model.
  **off**: as written. A placeholder (`$DB_PASSWORD`, `<password>`, `***`) or a
  name in code (`config.password`) is not a secret. Each one found is in the
  audit log by its kind, never the secret (`safeguard.refused`,
  `safeguard.secret_masked`). A refused secret is not a strike: it is an
  accident, not abuse.
- **Blocked words and patterns**: phrases matched as whole words, ignoring
  case, or regular expressions (`re:`). A message with one is refused.
- **The model checks each message** (off by default): before answering, the
  chat's model (the model for small steps, when one is set) reads the message
  as a classifier and refuses one asking for harm in the chosen categories
  (violence, self-harm, sexual content involving minors, mass-casualty
  weapons, malware, hate, fraud); learning, safety, news and fiction stay
  allowed. When the check cannot run, the message goes.
- **Mask personal data** (off by default): e-mail addresses, phone and card
  numbers (Luhn-checked) and IBANs reach the model masked; the chat keeps them.
- **The web's content is marked as data**, so the model never follows
  instructions hidden in a page (prompt injection).
- A refused message is in the audit log (`safeguard.refused`) and, by default,
  in the admins' bell; **Refusals that suspend an account** (off by default)
  disables an account after that many in a day.

**Per group** (Admin → Groups → a group → Policies): secrets, personal data, the
model's check and blocked words can each be set for the group's members; the
company's setting applies otherwise. A person in several groups gets the
strictest of the groups that set one (refuse over mask over off). So the
security team can paste keys while everyone else cannot, or one team has every
message checked.

**On the API path too** (Check API requests too, on by default): before each
request with an API key, the gateway asks the app (its guardrail,
`config/litellm.yaml`). Secrets are looked for in all of the request's text;
the blocked words and the model's check read its last question; personal data
is masked when the policy says so. A refused request gets the gateway's error
with the same sentence the chat shows. The chat's limits per person (length, messages a
minute) are the chat's only: keys have their own requests-at-once limit.

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
- **Credit** is one per person, over the chat and their keys together, per
  calendar month (UTC), and a group can have one too, shared by its members or
  each member's (Admin → Groups). Past any of them, the chat says which, and API
  requests are refused with the same sentence.
- **Repeated API requests** can be answered from the answer cache, at no cost
  and without the model (Settings → API keys → Answer cache; see
  [admin.md](admin.md#the-answer-cache-for-api-keys)).

## Who sees what

- A chat belongs to one person. Nobody else can read it in the app, **admins
  included**: every query is filtered by the signed-in person, attachments too.
  The exceptions: a shared chat's link (read-only, for the company or chosen
  groups) and an assistant's files (for the people it is shared with), both the
  owner's to make; a down vote with **Share this chat with the admins**, which
  lets admins read that chat, down to the rated answer, from Admin → Quality
  (each reading is in the audit log, `quality.read_shared`; rate it again
  without the box, or take the rating back, and it is closed again); and an
  admin's export of a person's data for eDiscovery (Admin → People), each one
  audited.
- Ratings and arena votes are kept with their answers: deleting the chat
  deletes its ratings. An arena vote stays on the leaderboard (without the
  chat) until its person is deleted.
- Deleting a person deletes their chats and attachments, unless they are on
  legal hold (then they cannot be deleted).
- **Chats are kept** as long as the company or the person's groups say
  (Your account → Your data says how long), then deleted with their files. A
  person on legal hold keeps everything, and a chat they delete is only hidden
  until the hold ends ([admin.md](admin.md#retention-legal-hold-and-exports)).
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
- **Company knowledge is what each person may read.** A GitLab project's wiki
  and issues are found only by its members (matched by username, as Argus
  does; admins too only when they are members); a Confluence page only by
  whom Confluence lets view it (its space and its restrictions), a SharePoint
  file only by whom it is shared with, and what they cannot tell by the groups
  an admin chose; a folder or a website only by the groups an admin chose. The
  search filters by reader before it ranks.
- **MCP servers** get the person's email only if the admin set a header for
  it. A server's key is stored encrypted under `APP_KEY` and never shown.
- **Memories are the person's own**: only their answers read them, and only
  they see, change or delete them, admins included.
- **Prompts** are their owner's, unless shared with groups (members use
  them) or made for everyone by an admin; a plugin's are for whoever may use
  its tool.

## Cost and credit

The chat talks to LiteLLM with its own service key (alias `chat`). Each request
names the person, so spend is theirs (surface **Chat** under Usage & cost), and
their credit applies. The cost under each answer comes from the model's prices
at the gateway, its sub-agents' calls included.

What a person has spent is what the gateway's request log puts to them, over
every path (chat, API keys, agents), by the same rule as the usage dashboards;
the overview, People, the export and Home all read it. LiteLLM's own counters
split a person in two (the chat is booked to them as an end user, their keys
as an internal user), and each would let the whole budget through on its path.
So the credit is one: before each answer the app adds up what the log puts to
the person this month, over every path, and checks it against their credit and
their groups'; the gateway asks the same of the app before each API request.
LiteLLM's own limits stay, as a backstop.

## Settings

The chat's limits are under Admin → Settings → Chat ([settings.md](settings.md)):

| Setting | Default | What it is |
|---|---|---|
| Tool calls per answer | 8 | how many rounds of tool use one answer may take |
| Tool definitions sent whole up to | 6,000 characters | past it, tools go on demand (below); 0: always whole |
| Tool result the model reads whole up to | 24,000 characters | a longer result goes as its start, and the whole of it becomes a file in the chat that the model reads on with `read_file`; 0: always whole |
| Largest attachment | 20 MB | per file (up to 100 MB) |
| Text kept per attachment | 200,000 characters | longer files are cut and marked |
| Passages of long files per question (Settings → Company knowledge) | 12,000 characters | with the embedder, what of the chat's long files and its assistant's files comes with each question |
| Longest single answer | 15 minutes | an answer still running after this is stopped |
| Memory | on | answers read people's memories, and the model offers new ones; off for everyone when unticked |
| Arena leaderboard for everyone | on | everyone sees the leaderboard of Compare's votes; off: admins only (Admin → Quality) |

The thinking levels (`THINKING_PRESETS`) and **Model for sub-agents and small
steps** are under Settings → Model. Everyone's language, voices, reading speed
and reading aloud in Talk are under Settings → Speech, until each person
chooses their own ([Your voice](#your-voice)).
**GitLab address for links** (Settings → Argus, applies at once) is where
browsers open GitLab from Argus's answers. Leave it empty to use the address
Argus indexes. Set it when Argus reaches GitLab by an internal name.

## How a conversation is stored

Messages form a tree: each has a parent, and the conversation remembers its
current leaf. `POST .../messages` takes a `parentId` (default: the leaf) or
`root: true`. `POST .../regenerate` takes the question and, optionally, a model
and a thinking level. A chat on Auto has `auto` as its model; each answer
on Auto keeps who answered and why (`details.route` on its first message).
`PUT .../leaf` switches branch. `POST .../fork` takes a `messageId` (default:
the leaf) and copies the path to it into a new chat. The fork must end on a
question or a finished answer, never inside a tool round. `PATCH` with
`archived` archives a chat, and `GET /conversations?archived=true` lists the
archived ones. A chat's canvases are under
`/api/chat/conversations/{id}/canvases` (list, `POST` to make one) and
`/api/chat/canvases/{id}` (read, `PUT` with `baseVersion` to save, `DELETE`),
with `/versions`, `/versions/{n}`, `POST /versions/{n}/restore` and
`/export?format=md|docx|pdf|file`; they are kept in `canvases` and
`canvas_versions`. Chats from before branches were each migrated to one branch.

Each round of an answer keeps its trace beside its tokens: the first token's
wait and the engine's read and write speeds, and on the first round the wait
in line and getting ready; the answer's last round keeps the whole answer's
time. A sub-agent's time, tokens, speeds and each tool call's time are kept with
its delegate call. `GET /api/admin/traces/{messageId}` (admins) puts an answer
together from them; `GET /api/admin/traces?from=&to=` lists the slowest.

`POST .../compare` takes the same as `.../messages` and `models` (two names, or
none for two at random). Its two answers are siblings under the question; the
chat's `arenas` lists each comparison (its question, each answer's first
message, the vote, and the names once voted), and until the vote each answer's
messages say "Model A" or "Model B" for their model. `POST
/api/chat/arena/{id}/vote` takes `a`, `b`, `tie` or `bad`, once both answers
are written, and answers with the names. `PUT` and `DELETE
.../messages/{id}/feedback` rate an answer (`up`, and with a down vote
`reason`, `comment` and `share`); each message of `GET` carries the person's
own `feedback`.

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
| "You have used all your credit for this month (…, the chat and your API keys together)" | the chat and the person's keys together reached their credit | an admin raises it under People, or wait for the first of the month |
| "… has used its credit for this month" or "You have used your credit as a member of …" | a group's credit is spent | an admin raises it under Admin → Groups → the group → Policies |
| "This message was not sent: it holds a private key." | secret scanning found a secret | remove it (a placeholder will do) and send again |
| "The message did not reach the server" | the network or TLS failed before the server got it | the text is back in the box; send again |
| "This chat is already answering" | one answer at a time per chat | stop it, or wait |
| "This conversation is longer than the model can read" | the question and its attachments alone do not fit | start a new chat, or attach less |
| "… cannot see images" | the chat's model has no vision | choose a model that shows "Sees images" |
| "You may not use …" | an admin took the model away from you | choose another model |
| "Auto is not available to you" | Auto needs a model for small steps that you may use | choose a model; an admin sets the small model and who may use it (Admin → Models) |
| An Auto answer says "The model for small steps is not available to you now" | the small model is not yours to use, or cannot load now | nothing: the main model answered. An admin can keep it loaded (Admin → Models) |
| "… is not loaded right now" | the chat's model is not the one the engine has loaded | choose a loaded model, or ask an admin to load it (Admin → Models) |
| "Waiting for your turn: N answers ahead of you" | the model is serving others; your answer is in line | nothing: it starts on its own. A group with a higher priority goes first (Admin → Groups). An admin can change the limits (Settings → Chat) |
| "The model has been busy for 10 minutes" | the line did not move for that long | ask again later; tell an admin if it happens often |
| An API call answers **429** | the key already has as many requests running as it may | wait for one to finish, or retry; an admin sets the limit (API requests at once, per key) |
| "Argus is not available for this answer: …" | Argus's reason follows | usually no GitLab account matches the person's email; see [ARGUS.md](argus/README.md) |
| "… is not available for this answer: … did not answer" | an MCP server is down or refused the key | Admin → Tools → the server's **Edit** → **Test** |
| No **Image generation** or **Video generation** in the Tools menu | the model is off, or its server does not run | Admin → Models: turn it on; a module left out in `docker-compose.override.yml` stays out |
| No **Python** in the Tools menu | the sandbox is not running | `docker compose ps sandbox`; `scripts/sandbox-check.py` says whether it is sound |
| No **Decide (Laya)** in the Tools menu | the laya module is off (the default), or still fetching or loading its checkpoints | `COMPOSE_PROFILES=laya` in `.env`, then `docker compose up -d laya`; Admin → Models shows the downloads |
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
  link address applying at once, and Talk: a recording written down in the
  person's name, a spoken question's note for its answer only, and each
  sentence read in the voice of its language. And each person's voice: the
  voices found at a fake speech server (only the app's models, with language,
  accent and gender), the choices saved and used by read aloud, Talk, voice
  messages and Try it, the company's defaults from Settings (and the warning
  under a voice there that is not offered), the voices and languages Settings
  chooses the company's from and Try it there (admins only, at the speed given
  or the company's, a voice of a model not listed yet tried as named), a text's
  language picking its voice (a short Latin sentence going by what came before
  it or the language spoken, a short English one of words Portuguese writes
  too staying English, and falling back when no voice reads it), a change
  keeping the choices it does not name, changes made at once all kept, a voice no longer offered not
  stopping other changes, new choices refused that the speech models do not
  offer, an API key's speech that names no voice read in its person's voice and
  its speech to text that names no language written down in theirs (the sound
  passed on before it has all come, and nothing held before the key says whose
  it is; a browser's preflight passed on as it came), the company's language
  refused when Whisper does not know it and heard as auto when the speech
  server does not list it, the voices chosen believed while the speech server
  is down or has not listed their model yet (asked again within a minute), and
  a person from v5.2.0 upgraded with nothing lost.
  Also branches (edits, answering again, switching, parents from another chat
  refused), archiving (and coming back when written in), forks (up to the
  chosen answer, with settings and files; never inside a tool round; the owner
  only), a deleted chat's files (kept while a fork uses them), a chat's instructions and parameters, retries with another model,
  images to a model that can see and one that can't, SVG never served as an
  image, and the migration of existing chats. The model for small steps
  (sub-agents, the title, compaction before an answer and on demand, the
  safeguards' check go to it; the answer to the chat's model; all back to the
  chat's model without it) and Auto (each kind of question routed, thinking
  by difficulty, the route kept, **Ask the big model**, deep research unasked,
  Auto as the default, and who may use the small model). Real Postgres, a fake
  model and a fake Argus.
  Also Decide (Laya), against a fake Laya: offered only while the module runs
  and a checkpoint is loaded; the model's questions sent in Laya's own shape,
  its probabilities read back to three places, Persian sent to the
  multilingual checkpoint and marked uncalibrated, a text Laya cut short
  said so with how much it read, bad questions refused
  before Laya is asked, Laya's refusal and a Laya that went away said
  plainly; served over Arena MCP as a tool that changes nothing; and both
  checkpoints fetched into the library, each into its folder. The server's
  own checks are Python tests with its model stubbed (`tests/deploy`),
  including "busy" past its queue and callers that left skipped. With
  `LAYA_URL` naming a running Laya (`-e LAYA_URL=http://127.0.0.1:18000` to
  `tools/dn test`), the `RealLaya` tests ask the real model: an English
  incident in the chat and a Persian complaint over Arena MCP, and Code
  Arena's look at 95 labelled commands and at long ones (a push after
  Persian, a script or base64); without it they are skipped.
  Also memory: "remember I deploy with Podman" in the next chat's system
  prompt (after the fixed notes) and gone once deleted; an offer kept only
  when accepted (in the person's words), taken back, declined, and nobody
  else able to answer it; a task's chat only offering; the person's switch
  and the company's; the block's budget. And the prompt library: a person's own, shared with their groups
  (only the owner changes it), the company's (admins only, audited), the
  checks on names and groups, and a plugin's `/triage` installed with it, for
  whoever may use its tool, and removed with it.
  Also ratings (kept, changed, taken back, answers and known reasons only, the
  owner only), the quality page's numbers per model and assistant, a shared chat
  read by an admin and audited, and closed again; arena mode (both models
  answer one after the other, the question alone each, no name in any event
  or in the chat until the vote, the vote once, the winner's branch shown,
  chosen models checked, which is A drawn), the Elo update, and the
  leaderboard kept to admins.
  Also assistants: a private one's
  instructions and files in its chats and nobody else's, one shared with a group
  used by its members and invisible to others (gallery, page, a new chat, its
  file), a member who left the group told so and stopped, editors who change it
  but neither share nor remove it, only admins making one everyone's, and a new
  chat's model, thinking and tools. Shared chats: a link opening for a colleague
  in the group and not for someone outside it or signed out, its files, the
  owner's numbers, revoking stopping it, a fork with copies of the files and
  without the owner's instructions, and a shared branch showing only that
  branch.
  Also the canvas: the model writes one and a change to one section leaves the
  rest untouched; a missing or repeated `find` refused with its reason and
  nothing changed (all or none); reading by lines and listing; a rewrite as
  a version; every version restored, a restore a version of its own; a save
  over a newer version refused; the owner only, and gone with the chat; the
  Word file a zip with its parts (styles, numbering, links, a table, a right to
  left paragraph); a code canvas as its file; the PDF made in the sandbox, and
  the reason when there is none.
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
  - Auto in the model menu, its note under an answer (the small model's, or
    handed on with a thinking level), **Ask the big model**, no Auto without
    a small model, and the sub-agents' model under an answer
  - `/review` chosen from the `/` menu, its blanks asked for, not sent while
    one is empty, then sent filled in; the menu found by title, closed with
    Esc; a prompt without blanks; the Prompts page (sections, a new prompt
    shared with a group, an admin's for everyone, edit and delete)
  - memory: an offer kept in the person's words and undone; the chat's Memory
    button listing and deleting; Your account → Memory (add, edit, delete,
    off)
  - thumbs up and down with a reason, words and the chat shared; Compare at
    random and with two chosen models, the two answers side by side and blind,
    the progress while they answer, the vote and the names; the leaderboard
    and Admin → Quality
  - the assistants gallery (use, search, yours), a new assistant, a chat started
    from a starter, an assistant read-only for its users, its owner changing its
    starters and sharing it with a group and an editor, and a chat whose
    assistant was taken away going on without it
  - a shared chat read-only with its files and forked, a link that does not
    open, the owner's numbers and revoking, and sharing from the chat's menu
    (chosen groups, the branch on screen, revoked)
  - Talk, with the microphone, recording and playback faked: a spoken
    question written down and sent as spoken, its first sentence read aloud
    while the answer is still being written, speaking over it stopping both,
    and End talk letting go of the microphone; the voice activity check, the
    sentence cutting (code left out, Persian marks) and the reading queue;
    with reading aloud off, the answer only shown; each sentence read with the
    answer before it, which tells a short one's language
  - Your account → Voice: the language spoken, a voice per language (the
    language spoken first, theirs or the company's) with each voice's id, the
    speed once the slider rests, reading aloud, each saved alone (the slider
    and the switch together both kept), and back to the company's; a voice no
    longer offered shown as such and put back alone; Try it with the voice
    shown and the person's speed, and again to stop; what it says when speech
    is not set up or the speech server cannot be asked
  - Settings → Speech: the language people speak chosen from those speech to
    text knows (a code it does not list shown as such); Voice for each
    language: a row per language offered,
    each voice by name with its id, chosen and saved as the setting's pairs (a
    language back to the first offered dropping its pair, the last one naming
    that voice); Try it at the speed on the page, saved or not; a voice not
    offered said in its row, one of a model not listed yet tried as named;
    Edit as text and back; typed as text while the speech server cannot be
    asked
  - the canvas: the line diff, a selection's lines and its message; the panel
    from the header, a save as a version, the versions' diff and a restore, a
    selection sent to the chat quoted, a tool card opening its canvas, the
    canvas opening by itself on a wide screen, a save over the model's newer
    version, export, and a code canvas with line numbers
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
