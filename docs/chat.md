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
  use the small model, or while it is not loaded and the engine has no free
  place for it (another model sits in the place it shares), each step uses the
  answer's own model rather than wait.
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
    restarts go once it is back. While [Code Arena](code-arena.md#chats-in-arena)
    is answering in the chat, the page says so and every message is queued:
    they go when its turn ends.
- **Reply to part of an answer.** Select words in a finished answer and a
  small toolbar comes beside them (above them; below them on a touch screen):
  - **Reply** (`Ctrl+Shift+'`, `⌘⇧'` on macOS) puts the part above the box as
    a quote, with an X to take it away (Backspace at the start of an empty box
    does too). It goes with your next message: the model reads it before your
    question, as the part of an earlier answer you mean, and the thread shows
    it above your question, two lines of it (**Show all** for the rest).
    **Go to the quoted answer** goes to the answer it came from.
  - **Ask about this** asks a short question about the part at once (or
    three one-click ones: Explain this, Say more, Give an example), leaving
    what is in the box as it is; while an answer runs, it is queued.
  - **Copy** copies the words.

  What is quoted is the answer's own Markdown for those words when it is found
  (code, links and formatting kept), else the words as shown; at most 4,000
  characters. A message needs words or files: a quote alone is not one. An
  edit keeps the question's quote (the edit can take it away), and a fork, a
  shared chat and an export carry it. Without a mouse, **Quote part of this
  answer…** under an answer opens it in a box where Shift and the arrow keys
  select a part (nothing selected quotes the whole answer). A selection over
  several messages quotes the part in the one where it starts; questions,
  thinking, tool cards and an answer still being written have no toolbar.
- **Deep research.** A tool, **Deep research** in the chat's **Tools** menu
  (there is no separate button): while it is on in a chat (it is in new chats
  unless an admin changes that), the model starts it (`deep_research`) when you
  ask for deep research or a question wants a thorough, sourced report. The
  call asks you first (**Allow**), unless an admin turned that off. Allowed,
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
  - **Who has it.** Deep research is a tool admins give to people like any
    other (Admin → Tools → **Deep research**: everyone, admins or chosen
    groups; on for everyone until an admin changes it). Only those who may use
    it see it in **Tools**. It counts in the deep research a day. Sub-agents
    never start one, and an answer that is deep research already is not
    offered it, nor are **Compare**'s two answers (deep research is one
    model's report), nor a model that calls no tools.
  - **Where nobody can allow it.** While it asks first, it is not offered
    where nobody can press **Allow**: a [scheduled task](#scheduled-tasks)'s
    run and a chat bot's thread ([integrations.md](integrations.md#chat-bots)).
    With asking turned off, they may start one too.
  - **For your agent.** [Arena MCP](mcp.md) serves `deep_research` to your
    own agent: it runs in a new chat of yours ("Deep research: …"), and the
    agent gets the report.
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
- **Skills.** `/` at the start of a message finds a skill (Workspace →
  Skills): yours, your groups', the company's or a plugin's. Chosen, it asks
  for its parameters and goes with your message; the model also uses one on
  its own when a question fits ([below](#skills-and-slash-commands)).
- **What you sent before (↑).** As in Claude's apps: with the box empty, or
  the caret on its first line, **↑** brings back your last message in this
  chat, then older ones; when this chat has none left, your messages in your
  other chats (archived ones too), newest first, each text once. A scheduled
  task's runs are not among them (an event's text rides in their questions),
  nor what a fork copied from the chat it came from: someone else's questions,
  or yours, still in that chat. **↓** goes back toward the newest, and past
  it to what you were typing, kept as it was; **Esc** goes straight back to it.
  **Enter** sends the message again, as a new one; editing it changes a copy,
  never the message it came from. Only its
  words come back, not its files. In anything of several lines, a draft or a
  message brought back, ↑ and ↓ move between its lines first, and go on from
  its first or last line. A message of one line brought back steps on with
  the next ↑ at once, even when the box wraps it; once you move the caret
  into it or edit it, ↑ and ↓ move between its rows first. The `/` menu
  keeps its own arrows while it is open, and a message brought back that
  starts with `/` does not open it. Keys with Shift, Ctrl, Alt or ⌘, a
  selection, and typing that composes (an input method) are the box's own.
  Messages over 32,000 characters (a pasted document) stay in their chat.
  Phones have no arrow keys: nothing changes there.
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
  notes, tool definitions, your instructions, the memory (what the prompt
  holds of it and what was recalled for the question), a summary of earlier
  messages, files, your messages, answers, tool calls and results, what is
  kept for the answer, and what is free. Each request is measured by kind in
  characters and scaled to its prompt tokens. Under **Biggest**, the five
  messages that took most of it (past 1,000 characters), each with its tokens
  and its start: a question or an answer is pinned or left out from there, a
  tool's result left out (below).
- **Compact.** Near the limit the chat compacts itself: the model summarizes
  the older messages, and the next answers read the summary instead (the
  newest exchanges, about a quarter of the context, stay as they are). **Compact
  now** in the gauge (or the chat's menu, or sending `/compact`) does it at
  once. Nothing is deleted: the messages stay on screen above a mark, which
  says why it compacted there ("at 80% of the context", "so your message fits",
  or "the model could not read the whole chat: compacted and asked again").
  - **When.** Your account → Answers → **Compact long chats**: the company's
    default (Settings → Chat → **Compact a chat at**, 80% unless an admin chose
    otherwise), a share of the context of your own (20% to 95%), or **Only when
    a message would not fit**. The share is of the model's whole context, as
    the gauge shows it: the gauge marks it, says whose choice it is, and links
    to where it is changed. Code Arena takes the same choice as its default.
  - **Before it fails.** Whatever the choice, a chat is compacted before a
    message would not fit: each request is measured before it is sent (its
    messages, files, pictures and tool definitions), at the characters a token
    held in the chat's last answer (3 when none says), with room kept for the
    answer (the chat's **Longest answer** when set, else an eighth of the
    context) and a margin. An answer whose tool calls outgrow the context cuts
    its earlier tool results to their start in what the model reads (the chat
    keeps them whole), then compacts everything before the question. A
    message bigger than the whole context even at its most compact is not sent:
    the answer says so, with **Compact now** and the models with a larger
    context.
  - **When the model still refuses.** A request the model refuses as longer
    than its context (llama.cpp's and LiteLLM's refusal, which say by how much)
    is compacted and asked once more; a second refusal ends the answer with
    the same actions. When the engine's cache, shared by the chats it answers
    at once, is full, the request is asked again after two seconds. A summary
    the model refuses as too long is written in halves. An answer cut at the
    end of the context says so, and the next message compacts.
  - **Cleared first.** Before compacting, the results of tools' calls older
    than the last two questions (past 600 characters) are cleared in what the
    model reads, each to a line ("Cleared to make room in the context:
    calculate's result of an earlier step (3,010 characters). Call the tool
    again if it is needed."). The calls stay, and the chat keeps the results
    whole. When that brings the request under the threshold, nothing is
    summarized; the answer says "Cleared N earlier results" above it.
  - **The summary in sections.** The model writes it under `## Goal`,
    `## Constraints and preferences`, `## Decisions`, `## Facts and findings`,
    `## State`, `## Next steps`, `## Open questions` and `## Earlier parts`
    (a line for each stretch of the chat with its message numbers, "#1–#140:
    planning the launch"), leaving out any with nothing to say. It reads each
    message with its number (#212, its place on the chat's branch from 1)
    and cites them ("chose Postgres (#212)"). After it, the next answers read what you pinned (below), word for
    word, and the chat's plan as it stands (its steps and their state), since
    the summary may have shortened them.
- **A long chat finds what it said.** A chat that has compacted keeps every
  message, and each answer then has two functions of its own (not a tool to
  choose): `chat_search` finds the earlier messages that hold some words (a
  name, a number, a few words), each with its number and the words around
  them, and `chat_read` reads messages again by their numbers, up to 20 at
  once. So a chat can go on for thousands of messages, the summary carrying
  the thread and the model reading back the exact words when it needs them.
  Messages left out by the person are not found.
- **A long chat opens at its end.** The page reads the newest 300 messages of
  the branch (and the other versions below them); **Show earlier messages**
  at the top reads 600 more each time. `GET /api/chat/conversations/{id}?last=N`
  gives that window, and `earlier` says how many of the branch it left out.
  An answer reads the chat's tree and, whole, only what it sends: the
  messages from the newest summary on, and those pinned before it.
- **Pin and leave out.** Each question has **Pin this question** and **Leave
  this question out**. A pinned question is said word for word after every
  summary (up to 8,000 characters of pins), so a budget code or an exact
  instruction is never paraphrased away. A question left out is not read by
  the model, nor are the answer and tool calls under it; it stays in the chat,
  marked "Left out: the model does not read it", and comes back with one
  click. A pinned question cannot be left out until it is unpinned.
  `PUT /api/chat/conversations/{id}/messages/{messageId}/context` with
  `{"pinned": true}` or `{"leftOut": true}` sets them.
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
  switched off, and the model is told); and when a summary fails, the oldest
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
  - **Deep research**: the model starts deep research itself when a question
    wants a thorough, sourced report, asking you first (see **Deep research**
    above).
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
  - Other answers show as a table (it sorts and filters) or a list of fields. **Raw answer** shows
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
  in a new tab, and the model, time, tokens (in, cached, out) and cost under
  each answer, for everyone. Model output is sanitised: HTML in an answer never
  runs. A table in an answer sorts by a column when its heading is clicked
  (numbers as numbers, then the other way, then back to the answer's order),
  and one of more than eight rows has a box that keeps the rows holding what
  is typed.
- **History.** Chats are grouped by date and can be searched. A
  [Code Arena](code-arena.md#chats-in-arena) session is a chat here too, with
  a terminal mark and its folder: continued here, what is added goes back to
  the session (and what the session adds comes here, within seconds while
  its page is open), with each answer's thinking, model and tokens; the files
  that went with a question there show as chips, and Code Arena's own notes
  to its model as notices. An open chat changed elsewhere (another tab or device:
  its messages, branch, title, queue or settings) is read again within
  seconds too, an answer started there is watched as it is written, and the
  list takes in new chats within half a minute. The first
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
- **Folding the list.** Each group of the chat list (Assistants, Today,
  Yesterday, Previous 7 days, a month...) folds under its heading, which then
  counts what it holds: its chats, or your assistants. The chat (or assistant)
  you have open stays in sight under a folded heading. On a wide screen
  **Collapse chat list** (left of **New chat**) folds the whole list to a
  narrow rail with **New chat** and the search, for more room; the same button
  opens it again. Both are remembered for you on that browser. A search shows
  its matches in folded groups too.
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

## Rooms

A room is a chat several people share. Make one with the **people** button
beside **New chat**, or turn one of your chats into a room (the chat's
**⋯** menu → **Make it a room**: what it said so far becomes the room's).
Add people by name and whole groups (whoever is in the group now is in the
room), each to **read and ask** or **only read**. Rooms show in their
people's chat lists with a people icon. Admins choose who may make rooms
(the `feature.rooms.create` permission, Admin → Access) and how many people
a room holds (Settings → Chat → **Most people in a room**, 50).

- **Who wrote what.** Every message shows its writer's name (never their
  email); yours are on the right. The model reads each person's message with
  their name in front, and is told who is in the room and whom it answers.
- **Whoever asks pays.** An answer is its asker's: their credit, their place
  in line, their models and tools, their safeguards and rate limits. While
  one answer runs, the next questions wait in the room's line (20 in all, 3
  of each person's) and each goes as its writer's.
- **The model answers when asked.** It answers a message that names it
  (`@Arena`) or replies to one of its answers (the reply goes to it with the
  answer it replies to); everything else is for the people. Reply to any
  message with its **Reply** button; `@Name` (a person's name, or its first
  word) tells them, and a reply tells the one replied to: both in the bell.
- **No one's own things.** Answers in a room use no one's memory, code
  (Argus), knowledge, or tools that act as the person (admins' MCP servers
  and plugins), as everyone reads them and another member's message could
  steer them.
- **Live.** Who has the room open, who is typing, and whom the model is
  answering show as they happen.
- **Roles.** The owner changes settings, adds and removes people, changes
  their roles and hands the room over; members read, ask, attach files,
  edit the canvas, and stop or decide on their own answers; viewers read.
  Owners can let members invite. Anyone but the owner can leave. When an
  owner's account is deleted the room goes to its longest-standing member;
  what someone wrote stays after they leave, as a former member's.
- **Invite links.** In the room's **People** panel, whoever may invite makes
  a link: people join as members or viewers, for 1 to 30 days, for a number
  of people or anyone. The link is shown once (copy it then); the list shows
  each link still working, how many joined, and **Stop this link**. Opening
  one, signed in, shows the room, its owner and the role it gives, and
  **Join the room**; the owner hears when someone joins.
- **Export.** **Markdown** or **JSON** in the **People** panel: the room's
  title, its people, and every message with who wrote it and when (the
  model's answers by their model). For anyone in the room who may export.
- **Read in your language.** The languages button in a room's header reads
  the room in a language of yours (Persian, German, Chinese and 17 more):
  others' messages and the model's answers are shown translated, each with
  **Show the original** under it; your own stay as you wrote them. Write in
  yours: each reader reads it in theirs. A message is translated once into
  a language, by the model for small steps (or the one an admin names under
  Settings → **Model that translates rooms**: a larger model translates days,
  names and idioms better), and kept for everyone who reads in it; one
  already in that language is left as it is. The choice is your browser's,
  for every room.
- **Calls.** **Call** in a room's header starts the room's call, and its
  people see who is in it and **Join** (viewers listen to the room, not its
  call). Each page in the call talks to every other directly (WebRTC; the
  server passes only their signals, never their sound), up to 8 at once.
  What each person says is cut at their pause (or every 12 seconds while
  they go on), written down by the speech-to-text model as theirs, and
  shown to everyone as **live captions**, in the reader's language when
  they read the room in one (translated once for all who read in it,
  **Show the original** a click away). **Mute** stops both the sound and
  the captions. A page that joins late sees the last captions; when the
  last one leaves, they go. Calls, like the room's live events, are held by
  the replica the room's pages reach. Browsers on different networks need a
  STUN or TURN server: `Rooms:Calls:IceServers` (none by default, for one
  network); `Rooms:Calls:MostInCall` (8) sets the size.

## Plans

The **Plans** tool (in the chat's Tools menu) makes and keeps plans: a title,
a goal, and steps, each with an owner, a status (to do, doing, blocked,
done), start and due dates, what done means, and the steps it waits for. Ask the chat to plan
something and say who does what by when; later say what changed, and it
changes the plan. It refuses steps that wait for one another in a circle and
dates that end before they start, and warns about a step that starts before
what it waits for is due, or one overdue and not done.

For work of several steps the chat does itself with its tools (research, a
document, an analysis), it **proposes a plan** first and stops: the plan
shows on a card under its answer, each step with what done means. **Approve
and carry it out**, **Change** a step (say what should change: the chat
proposes the plan again) or **Reject** it; each is sent as your next
message. Approved, the chat carries the plan out with its tools, marking each
step doing as it starts it and done when it is, and the card under the answer
shows each step as it stands.

Every plan opens under **Plans** (in the sidebar, and from the answer that
made it) as a **board**, a column per status, where a click moves a step on
and a new step can be added; and a **timeline**, each step from its start to
its due date. A plan made in a room is its people's to see and move along;
the chat reads it as people left it.

Code Arena's working plans are there too, with the chat its session keeps in
step with ([code-arena.md](code-arena.md#the-plan)): each step with what
done means, the files it changes and how its check went. Its agent keeps
such a plan, so the board shows it as it goes and moves nothing; to change
it, ask the agent.

## Meetings

**Meetings** (in the sidebar) makes a meeting's minutes from its recording.
Record it in the browser (the microphone hears the room, or a call played
aloud; recorded as Opus at 32 kbit/s, so three hours fit) or upload a sound
or a video, up to 60 MB. It is made in steps, each shown as it goes:

1. **Reading the recording**: the sound is made a 16 kHz mp3 in the sandbox.
2. **Listening to what was said**: the speech-to-text model writes it down
   with each part's start and end.
3. **Telling the voices apart**: the media studio's voice model groups the
   parts by voice (Speaker 1, Speaker 2, ...). Without the studio, the
   transcript is kept and the meeting says who spoke is not told.
4. **Writing the minutes**: the chat model writes the title, the summary,
   the decisions and the action items (whose each is and by when, as said),
   and names the speakers from what they called each other. A long meeting
   is read a part at a time and its notes put together.

The meeting's page plays the recording from any line of the transcript and
marks the line playing. Rename a speaker, or tell which person in Arena they
are: their action items are that person's, and on the day an item is due the
person is told once, in their notifications (an item dated in the past is
told at once, as overdue). One replica looks for due items every five
minutes. **Make a plan** makes a plan with a step per action item, its owner
and its due date. **Ask about it** opens a chat with the minutes and the whole
transcript as a file. A meeting is its owner's only; the speech-to-text model
must be one they may use, and the recording passes the safeguards like any
upload. `Meetings:MaxUploadBytes` (60 MB) and `Meetings:MostPerPerson` (500)
set the limits.

## Code review

**Code** (in the sidebar) reviews merge requests with the model and answers
questions about code. Both read the code through Argus with **your own**
GitLab access (Reporter or above on the project, and the rules of Admin →
Access), and write nothing to GitLab: no comments, no approvals.

- **A review.** Type a project's path (group/project) and press **Show**:
  its merge requests. **Review** reads the merge request (its description,
  its diff, its CI) and reviews each file on its own, the lines numbered as
  in the new file, for bugs, security holes, data loss, races, error
  handling, edge cases, performance and missing tests. Every finding is then
  put to a second pass told to refute it: those it could not refute are the
  findings; those it doubts are listed apart, with why. A summary says what
  the change does file by file, what to look at first, its risk and a
  verdict (looks good, comments, changes suggested). The bell says when it
  is ready. A branch without a merge request is reviewed against the one it
  goes into.
- **Findings** sit on a file and line, with a severity (critical, major,
  minor, nit), why it matters and a fix. Mark them **Resolved** or
  **Dismiss** them.
- **Review again** after new commits: it reads only what came after the last
  review's head.
- **Discuss in a room** makes a room with the review in it (its summary and
  findings). Add people from the room's **People** panel; they see the review
  too. The model answers there when someone writes `@Arena` or replies to it.
- **Who pays.** A review's requests are its person's, on the chat credit,
  tagged `review`. Three of a person's reviews run at once at most. Who may
  review: the `review.run` permission; who may ask about code: `code.read`
  (Admin → Access).
- **Ask about code** opens a chat with Argus on and the question in it, about
  the project shown: Argus reads the code, the merge requests, CI and any
  commit, as you may.
- **The Code review tool** (Admin → Tools) lets a chat, a coding agent
  through Arena MCP, or Code Arena start a review: "review g/app !7" runs
  the same review, waits for it (up to 15 minutes, its steps shown) and
  answers with the verdict and the findings it kept; `review_get` reads one
  again. The review is on the Code page too. Off in rooms (it reads as you).

### GitHub and Gitea

Pull requests on GitHub and Gitea are reviewed as merge requests are, by
naming the project `github:owner/repo` or `gitea:owner/repo` (on the Code
page, to the review tool, in an automatic review's rule):

- **Your own token.** Link a read-only token once in **Account → Code
  hosts**: GitHub's fine-grained token with *Contents*, *Pull requests* and
  *Metadata* read (or a classic one with `repo` read), Gitea's with
  *repository: read*. It is checked as it is linked (the account it is
  shows), kept encrypted, never shown again, and passed to Argus with your
  requests only: what you may read there is what is read. Argus only reads.
- **What is read.** The pull request, its files and diff, its commits, its
  conversation and its CI (GitHub Actions runs and jobs; Gitea's commit
  statuses). A review again reads only what came after on GitHub; Gitea's
  API cannot compare two commits, so there it reads the whole diff again.
  Comparing two branches is GitHub's and GitLab's only; `grep_at` is
  GitLab's only (Argus keeps copies of GitLab projects alone).
- **Setting it up.** GitHub.com works as it is; a GitHub Enterprise API
  (`GITHUB_API_URL`, or `off`) and your Gitea (`GITEA_URL`) are set in
  `deploy/.env` (Helm: `codeHosts`).
- **Automatic reviews** of a GitHub or Gitea project look at its open pull
  requests every few minutes as the rule's person (their token); GitLab's
  webhook is GitLab's only.

### Automatic reviews

**Code → Automatic reviews → A project** reviews a project's merge requests
as they open and get new commits, without anyone pressing Review:

- **As they open and change.** GitLab says so at once through a webhook: in
  the project's **Settings → Webhooks**, the rule's URL, its secret token
  (shown once; **New webhook secret** makes another) and **Merge request
  events**. The rule also looks at the open merge requests every few
  minutes (**Look every**, 5 to 1,440; 0: the webhook only), which catches
  what a webhook missed; **Look now** does it at once and says, for each,
  what it did. A merge request is reviewed when its newest commit is one no
  review read yet; a review again reads only what came after.
- **Which.** Only merge requests into the branches named (empty: any), and
  drafts wait until they are ready (unless you say otherwise). Only those
  changed after the rule was made: it does not review the backlog.
- **A scheduled review of a branch.** A branch, the branch it is compared
  with, and when (cron, at most hourly, in your time zone): "develop over
  main, `0 6 * * 1-5`" reviews what develop has over main each weekday
  morning, and skips the run when develop has not moved.
- **Where it goes.** Each review is in your list (marked automatic or
  scheduled); with a room chosen, each finished one is posted there by you,
  and the room's people see the review.
- **Cost and limits.** It reads with your read access and its requests are
  yours, tagged `review`, as your own reviews. A rule starts at most its
  reviews a day (20 unless changed; then it waits for tomorrow, and says
  so), two of a rule's run at once and the others wait their turn; they do
  not hold up the reviews you start. A rule stops (and says why) when you
  may no longer review or set automatic reviews. Setting them is the
  `review.policy` feature, which everyone has unless an admin denies it;
  a reviews admin sees and changes everyone's rules.

### Fix it

An agent makes the change for a review's finding, an issue or a failed
pipeline, or for what you write, and opens the merge request as you when you
say so.

- **Your token.** **Code → Fix it** links your own GitLab personal access
  token, with the `api` scope. Fix it reads the branch and opens the merge
  request with it, as you: never with the app's account or Argus's (which
  only read). The token is checked with GitLab, kept encrypted, and never
  shown again.
- **Starting one.** Press **Fix it** on a review's finding: it starts from
  the reviewed branch. Or, under **Code → Fix it**, name the project and say
  what to fix, in words, an issue's number (its title and text are read), or
  a failed pipeline's (the end of its failed jobs' logs is read, and the
  pipeline's branch is the start).
- **The work.** The agent reads the branch's files and searches them, then
  changes them, as you: your default model, your credit, your safeguards. It
  runs the tests when they can run here. That is the Python sandbox: Python
  3.13 and no network, for example `python -m unittest`. The page shows each
  step as it is taken, then the diff, what the tests said, and the agent's
  account. **Another round** asks for more on the same change. **Stop** ends
  it.
- **The merge request.** **Open the merge request as you** commits the change
  to a new branch, `fix/…`, made from the commit it read, and opens a merge
  request into the branch it started from, with the agent's account. It is
  yours in GitLab from then on.

At most two fixes of yours work at once, each for at most 30 minutes. Fix it
needs the `feature.fixit` permission. Everyone holds it unless an admin
denies it. Tests in other languages cannot run here yet: the agent says so,
and you run them in your pipeline.

### Code Studio workspaces

**Code → Code Studio** (`/code`) lists your workspaces: Code Arena's IDE in
the browser (the editor, terminals, search, Git, language servers, the
debugger and its agent), on a project of yours that the server keeps.

- **A new workspace** is a GitLab project and branch (its default branch
  when none), read with your own GitLab token, the one Fix it links. It is
  cloned once on the workspaces service; it pushes as you.
- **Open** starts it and shows the IDE at `/code/w/…`, the whole window. Its
  engine runs as a user of its own, with limits; it has no Docker socket, and
  its network reaches the app, the gateway, Argus (with your own key) and the
  internet, not the stack's databases, engines or browser. What the engine
  says as it starts is kept beside the workspace, and its end is shown when
  it does not start. Its agent uses a gateway key of its own,
  against your credit. Only you reach it.
- **Asleep.** A workspace unused for 30 minutes stops; its files stay, and
  **Open** wakes it. **Delete** takes its files and its key away.
- **Your own machine.** In a folder on your computer, `code-arena web
  --connect` (signed in with `code-arena login`) puts it in Code Studio as
  *the folder on that machine*, while it runs: **Open** shows its IDE here,
  from any browser you sign in on, through a tunnel code-arena dials with
  your API key ([code-arena.md](code-arena.md#the-ide)). Only you reach it;
  offline, Code Studio says how to connect it. Deleting it there disconnects
  it; the folder stays. With several app replicas, the one it dialled holds
  it, and the others reach it through that one (at its own address on the
  stack's network, or `Workspaces__SelfUrl`), with the tunnel's own key.
- **Where.** The workspaces service runs with the `workspaces` profile
  ([deployment.md](deployment.md)); without it, Code Studio lists no
  server workspaces. It needs the `code.studio` permission, which everyone
  holds unless an admin denies it. At most 8 workspaces each.

### The business map

**Code Studio → Business map** keeps what each system does for the business,
so Argus can explain the business, not only the code.

- **Mapping a project.** Type its path (group/project) and press **Map
  it**: a model, as you, with your default model and at your cost (tagged
  `business`), reads the project with Argus's read-only tools: its README
  and documents, its code (the things it deals in, the rules it enforces, the
  processes across files and systems), the systems around it, and its GitLab
  issues and wiki (`issue_list`, `issue_get`, `wiki_list`, `wiki_get`). It
  reports the map: the system's name, domain and summary; **what it lets
  the business do**, **processes** step by step, **rules** and why, **what
  it deals in**, **who it talks to**, and a **glossary**. Every item names
  where it was read (a file and line, a document, an issue, a merge request,
  a wiki page), and opens there in GitLab; an item with no source is left
  out. The bell says when it is ready. **Map again** reads it anew.
- **Who sees it.** A map is shown to those Argus shows its project to (a
  member at Reporter or above), checked as they open it; nobody else sees
  it, or that it exists. An admin removes one (audited as `business.*`).
- **In the chat.** With Argus on, and a map ready, Argus answers as the
  company's senior technical expert: `business_map` lists the systems you
  may read and what each does, and `business_explain` finds the
  capabilities, rules, processes and terms that answer a question, with
  their sources, which it confirms in the code, the issues and the wiki
  before it answers, in business words first. **Ask Argus** on a map starts
  that chat. In Code Studio's IDE, its agent has the same Argus.

## 3D models

The **3D models** tool (Admin → Tools; the 3D sandbox runs with
`COMPOSE_PROFILES=3d`) runs Blender 4.5 LTS headless in a sandbox of its own:
no network, a read-only system, each run as its own user, 8 GB of memory,
4 CPUs and 4 minutes at most. The model builds what you ask for in one of
two ways:

- **From parts** (`make_3d_parts`, the first choice): a list of simple
  shapes (box, cylinder, cone, sphere, torus, plane, gable roof, wedge,
  pyramid), each with a size in metres, where its bottom sits, a turn, a
  colour (a name such as brick, wood, glass, or #rrggbb), rounded edges, and
  repeats for rows of windows or fence posts. The tool writes the Blender
  script itself, so even a small model gets a house, a table or a robot
  right.
- **People, trees and organic shapes.** A person is one part (`figure`):
  a body Blender skins over a skeleton and smooths, with a face (eyes with
  irises, brows, nose, lips, ears), hands with fingers, hair (short, long,
  bun, ponytail, curly, bald) and clothes coloured where they belong (a top
  with long, short or no sleeves; trousers, shorts, a skirt or a dress;
  shoes), in a pose (standing, walking, waving, sitting, T-pose, arms up),
  any height (a child's head is larger for its height), build and body
  shape. A tree is one part too (round, pine, palm, bush). Organic and
  turned shapes: capsule, lathe (a profile turned round: a vase, a bottle,
  a column, a lamp post), extrude (any outline raised: a floor plan, a
  sign), tube (along a path: a pipe, a rail, a handle), 3D text, and
  `smooth` to round any part off.
- **A script** (`make_3d`): Blender's Python for what shapes cannot do
  (modifiers, booleans, curves, shader nodes of its own). The same `part()`,
  `light()`, `material()` and `make_texture()` helpers are there.

**Textures and shaders.** A part takes a pattern Blender draws (brick,
tiles, shingles, checker, wood, stone, concrete, plaster, grass, marble,
brushed metal, fabric, noise) at its real size, or a texture made before,
and a shader: matte, plastic, metal, mirror, glass (see-through), ceramic,
rubber, or light (it glows). **Textures as images** (`make_texture`): from a
pattern, from a description the picture model draws ("weathered oak
planks", with the person's picture permission and credit), or from a picture
in the chat; the tool bakes its colour, roughness and bumps (a normal map)
into images, with a preview on a sphere and a cube. Parts then use the
texture by its name. The texture images of patterns tile without a seam.

**Lights.** Lights are parts too: a point light (a bulb), a spot (turn aims
it), an area light (a panel) or a sun, with a colour (warm, daylight, cool,
candle or #rrggbb) and a power in watts. The pictures are taken in daylight
(a sky and the sun), in a studio (soft light from three sides) or at night
(only the model's own lights).

**What comes back.** `model.glb` (glTF: its textures, glass, glow and lights
in it; patterns are baked into images) opens in the chat's **3D viewer**:
drag or use the arrow keys to turn it, scroll, pinch or + and - to zoom; a
model with lights can be seen by its own lights alone. Two pictures, front
and three-quarter, are shown in the answer: quick flat colours for a plain
model, realistic (Cycles) when it has textures, glass or lights. Ask for a
`.blend`, `.stl` or `.obj`, or for renders of your own, and the script
writes those too. The model looks at the pictures and runs again to fix what
is wrong; it does not paste code into its answer. The chat's files are in
the run's folder (a reference picture, measurements, textures).

**To print it.** Ask to 3D-print it and `model.stl` and `model.3mf` come back
too: in millimetres, centred and resting on the plate, at its real size when
its longest side is at most 250 mm, else 150 mm long (or the size you ask
for). The 3MF keeps each part as its own object with its colour, for printers
of several colours. The answer says the printed size and whether every part
is closed (watertight); a slicer repairs small gaps.

**A video of it.** Ask for a turntable and `turntable.mp4` comes back: four
seconds of the model turning once round, played in the answer.

## The browser agent

**Browser** (in the chat's **Tools**, off in new chats) gives the model a real
web browser to do a task on websites while you watch: "find the cheapest
flight to Berlin on Friday and stop before paying", "fill in this form with
my details", "check our status page and the vendor's for an outage".

- **It browses.** It opens pages, clicks links, menus and boxes, types into
  fields, scrolls, goes back and reads, and says what it found or did with
  the page's address. Each step comes back as the page in words (its
  elements numbered) and a picture of the screen, shown under the call; a
  model that sees gets the picture too.
- **You watch it live.** While the chat's browser is open, the bar above the
  box shows its screen and where it is. **Watch** opens it larger.
- **You take over.** **Take over** gives you the browser: click on the page
  where you point, type (hidden in the box: a password or a card number is
  yours to type), press Enter, Tab or Escape, scroll. The agent's next step
  waits until you **Hand it back**, then goes on from where you left it.
- **It asks before it sends.** Anything that sends or changes something on a
  site (a form, buying, booking, posting, sending, deleting, signing up) is
  its **browser_submit**, which asks you first (**Allow**). It never types a
  password, a card number or another secret, and asks you to take over for
  that part instead.
- **Where it goes.** Only public sites on the list an admin sets (Settings →
  Tools → **Sites the browser agent may open**, else the Web tool's sites),
  never one on **Sites the browser agent never opens**, and never an address
  inside your network. The browser runs in its own container that reaches
  nothing else: every page goes through the app, which checks each site.
- **Its own browser for each chat**: cookies and signing in stay with that
  chat, apart from every other; it closes after 15 minutes unused, or with
  **Close** (the X) on the bar.
- **Who has it**: the `feature.browser` permission and the tool's own
  settings (Admin → Tools). It is off in rooms, as it acts as you. It needs
  the `browser` profile (`COMPOSE_PROFILES=browser` in `deploy/.env`).

## Apps

What the chat builds can be published as an app: a page or a React
component, at its own link, for you, some groups or everyone.

- **Built in the chat.** Ask for it ("a page where the team votes on where
  to eat, the votes kept for everyone"). Its code is a preview in the Files
  panel, running as it will run as an app.
- **What an app can do.** It runs in the preview's sandbox: no network.
  Through `window.arena` it asks the model (`await arena.ask(prompt, {
  system })`, the answer as text) and keeps its data (`arena.store.get`,
  `set`, `remove` and `keys`, JSON, everyone's, or each person's own with
  `{ mine: true }`); `arena.user.name` is who uses it. In a chat's preview
  the store lasts until the page reloads; published, it is kept.
- **As the person using it.** Each question an app asks goes to the model
  as the person using it: their default model (or the model for small
  steps, with `{ model: 'small' }`), their credit, their safeguards (the
  question's and the answer's rules); at most 300 a day for each person.
- **Publish as app** (above the preview): a name, a line about it, and who
  may open it: **Only me**, **Some groups** (yours, or any for those who may
  share with any group), or **Everyone**. It gets its link to share.
- **Workspace → Apps** lists the apps you may open, the most used first.
  An app's page runs it full size, with **Copy link** and **Remix** (a copy
  of your own, private, to change); its owner has **Change** (its name,
  words and who may open it), **Versions** (every version of its code, an
  old one put back as the newest) and **Delete** (its link and data go).
- **Who publishes**: the `feature.apps` permission (everyone, unless an
  admin denies it). Opening an app needs only to be one who may.

## Ask a panel

**Ask a panel** (beside Sketch, under the box) puts a question to several
assistants and roles at once.

- **Who sits on it.** Two to five panelists:
  - roles: the expert, the critic, the one who says no, the user, the
    accountant, the lawyer;
  - assistants you may use, each with its own instructions and model.
- **How it goes.** In each round, every panelist speaks in turn, having read
  what was said before, and answers the others by name. There are one to
  three rounds. The dialog shows each turn as it comes.
- **The verdict.** A moderator writes the panel's answer, then **who said
  what**, then where they disagree.
- **Where it ends up.** The whole discussion and its verdict go into the
  chat (or the room) as the answer to your question.

Every turn is asked as you: your credit and your safeguards.

## Sketch: napkin to app

**Sketch** (the pen button in the box) opens a whiteboard: draw what you mean,
roughly, with the mouse, a pen (it presses harder for a thicker line) or a
finger, then choose what it should become.

- **The board.** Black, red, blue or green ink, thin or thick, an eraser,
  **Undo** and **Clear**. **Draw on a picture** puts a photo (a paper sketch,
  a screenshot of a page you want changed) under the ink; while you share
  your screen, **Draw on the shared screen** puts what is on it now.
- **Make it**: **A page** (a working web page: one HTML block with its CSS
  and JavaScript, laid out as drawn, its buttons and fields working), **A
  React component**, **A diagram** (Mermaid: the boxes, arrows and labels
  drawn), **A 3D scene** (with the 3D tool, the shapes where they are
  drawn) or **A table**. The sketch goes as a picture with the question, at
  once, to the chat's model (one that sees: the picker says which).
- **It opens running.** A page or a component the answer gives opens in the
  Files panel's preview as soon as the answer is done; a diagram is drawn in
  the answer.
- **Change it by drawing.** The chat keeps its drawing while the page is
  open: open **Sketch** again, draw on it (or on a screenshot of the result)
  and ask again. **Attach it** instead puts the sketch in the box, for a
  question of your own words.

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

### Show your screen

**Share your screen** (the screen button beside Talk) lets the model see what
you see: a window, a browser tab or the whole screen, as the browser offers
when you press it.

- **Each question takes a picture of it.** While a screen is shared, every
  question you send, typed or said in Talk, takes what is on the screen at
  that moment (a JPEG, at most 1600 pixels a side, named for when it was
  taken) and sends it with the question, as an attached picture. "What is
  wrong with this chart?", "Which button saves this?", or a spoken walk
  through a dashboard, one question at a time.
- **Nothing else is recorded.** Between questions nothing is taken or sent.
  The pictures are the question's attachments: kept with it, shown in the
  thread, checked by the safeguards' file rules like any file.
- **The bar above the box** shows what is shared, a live view of it, and
  **Stop**; the browser's own **Stop sharing** stops it too.
- **A model that sees.** A chat whose model cannot see pictures gets their
  names only, and the bar says so: choose a model that sees (the picker says
  which). Phones' browsers cannot share a screen, so the button is not there.

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

What the models remember for you, in every chat and in Code Arena: about
you, about your projects, and the company's. **Workspace → Memory** lists it
all.

- **Asked for.** "Remember that I deploy with Podman": the model calls
  `remember`, and the memory is kept at once, with its kind. Its card in the
  answer says **Remembered**, with **Undo**. In a scheduled task's chat it is
  only offered: a task's question may carry an issue's or a webhook's words.
- **Offered.** When you mention something lasting (your team, your tools, how
  you like answers) without asking, the model may offer to remember it: the
  card asks **Remember this?** with **Remember**, **No thanks** and **Edit**
  (to change the words first). Nothing is kept until you say so, and nothing
  waits: the answer goes on, and you can decide later.
- **Formed on their own.** When a chat has been quiet for ten minutes, the
  model for small steps reads what it said since memory last read it, beside
  what is remembered already, and proposes what is worth keeping (at most
  five a chat; never secrets or what matters only to that chat). They wait at
  the top of **Memory** (**Remember these?**, with **Keep** and **Not now**)
  and the bell says so; answers read them once kept. With **Remember on its
  own** on, they are kept at once, and the bell says what was kept. One that
  says otherwise than a memory you kept (you moved the docs to Netlify)
  replaces it once kept; the old one stays, as replaced. A Code Arena
  session's memories about its project are kept and said. Rooms and
  scheduled tasks' chats are not read, nor what chats said before this
  arrived.
- **Past work, findable.** As a chat or a Code Arena session goes quiet
  (ten minutes), the model for small steps writes its **digest**: what it is
  about, what was asked, done and decided, the names that matter and how it
  ended, at most 150 words; as it goes on, the digest is written anew from
  the one before and what came after. With memory on, answers have
  `search_history` (your past chats and sessions, nearest by meaning to what
  the earlier work was about, else by their words; each with its id and
  digest) and `open_chat` (one of them: its digest, and with a query the
  messages that hold those words, or with from and to its messages by
  number). "As we decided last month" finds that chat. With the knowledge
  embedder, a question also comes with the past chats it bears on by
  meaning (at most two, well above the typical chat's score and near the
  best: their ids, titles and the start of their digests), so the model
  finds earlier work without being asked to look. Only your own chats
  are searched (not rooms). Code Arena asks the same
  (`POST /api/code-arena/history/search` and `/open`, with your key).
  Settings → Chat → **Digests of past chats** turns new digests off.
- **Tidied, with Undo.** Once a day (Settings → Chat → **Tidy memories**),
  each person's memories are tidied by the model for small steps: those that
  say the same (alike by meaning, or by their words; of one scope and
  project) are merged into one that keeps every detail, the one used most
  keeping its place; one replaced by a newer memory over a month ago, one
  never used in six months, or one not used for a year is let go (never a
  pinned one, never the company's); one longer than 300 characters is
  tightened. **Memory → History** lists every change with what the memories
  were, and **Undo** puts them back; **Tidy now** tidies at once. Forgetting
  all of yours forgets the history too.
- **Markdown, out and in.** **Download as Markdown** gives `memory.md`: a
  section for each whose (`## About you`, `## Project host/group/name`,
  `## The company's`), a heading for each kind (`### Preferences`,
  `### Facts`, `### Decisions`, `### How to`, `### Lessons`) and a line for
  each memory (` (pinned)` after a pinned one). **Import Markdown** reads the
  same back, or another assistant's export, or any list of `- ` lines (with
  no heading, facts about you); one said already is left as it is.
- **Kinds.** Each memory is a **preference** (how you like things), a
  **fact**, a **decision** (and why), a **how to** (how something is done
  here) or a **lesson** (what went wrong once, or what you corrected). The
  model says it when it remembers; you change it on the Memory page.
- **Whose.** Yours (every chat and project), a project's (kept by Code Arena
  in a repository's sessions, read there), or **the company's**: kept by
  those who manage the company's knowledge, read by everyone's answers, and
  changed only by them.
- **Read by relevance.** Each answer reads, in a block of its system prompt,
  the pinned memories, the company's and the newest, as many as fit in about
  400 tokens (1,500 characters); the block changes only when they do, so the
  start of the prompt stays cached. Then the memories that matter to the
  question, nearest by meaning (the knowledge embedder; by their words when
  it is away), go with the question, within about 500 tokens. Under the
  answer, **Read N memories** shows them; each memory counts how often
  answers read it. The model can also look with `recall`, which searches
  all of memory.
- **Said again.** A memory saying what one kept already says (the same
  words, or the same meaning) is that one, in the newer words, not a second.
  Memories kept while the embedder was away are given their meaning within
  ten minutes of its return.
- **Yours to change.** **Workspace → Memory**: find one by its words, by
  whose it is or its kind; pin one (every answer reads it), change its kind,
  edit or forget it, **Forget all of mine** (the company's stay), or turn
  **Use memory** off (answers then neither read nor offer memories; yours
  stay). Up to 1,000 of yours, 500 characters each; the company keeps up to
  300.
- **Nobody else sees yours**, admins included: no admin page, audit entry or
  log shows one. Sub-agents do not get the memory tool. Deleting a person
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

## Skills and slash commands

**Skills** (Workspace → Skills, `/skills`) work as Claude Code's: instructions
for a kind of work you ask often, with parameters filled in each time. A
skill has a slash name, a title, **when to use it** (a description, as the
model reads it to choose it), its **instructions** and its **parameters**.

**Skills that learn.** Arena gets better at your work as it does it:

- **Learned from your work.** When a chat or a Code Arena session that worked
  out a way of doing a task you will likely do again (several steps, not one
  answer) goes quiet, the model for small steps writes it as a skill: a slash
  name, when to use it, and the steps as instructions, general enough to
  reuse. It waits at the top of the Skills page (**Learned from your work**:
  **Keep**, or **Not now**) and the bell says so; answers use it once kept.
  With **Remember on its own** (Memory) it is kept at once.
- **Better in use.** When a skill used in a chat (chosen with `/`, or by the
  model) went wrong, or you corrected what it did, a better version is
  proposed, whole, with what was wrong (**Apply** or **Not now**). Each skill
  shows its version; **How it got better** lists every change with **Undo**.
  A skill you may not edit (the company's, Arena's own) gets better as yours:
  a skill of yours with its name, which wins over it.
- **Lessons from your marks.** An answer you marked bad, with its reason, is
  read as a lesson: what to do differently, kept as a memory.
- **Code Arena** keeps what a session worked out with `skill_save` (the
  project's, in `.arena/skills/`, or yours, here), and after a long turn it
  is reminded to.

- **In the chat**, `/` at the start of the message opens the list: type to
  find by slash name, title or description, arrows to move, **Enter** or
  **Tab** to use one, **Esc** to close it. `/compact` is there too. The
  chosen skill sits above the box as a chip, with a field for each parameter
  (required ones marked; a default shown in its field). What you write in the
  box goes with it. Enter moves from field to field, then to the box;
  Backspace in the empty box, or the chip's X, lets the skill go. Sending
  waits until every required parameter has something.
- **What the model reads**: the skill's instructions, its parameters filled
  in, then your words, unless the instructions say where they go
  (`$ARGUMENTS`). The chat shows your words and the skill you used (its chip
  and parameters; its instructions on demand), not the instructions in your
  message. A message sent while an answer runs waits with its skill, filled
  in as it was.
- **Used on its own**: a skill that says when to use it is listed to the
  model (the **Skills** tool, on in new chats), and the model loads it with
  `use_skill` when a question fits, its parameters taken from the question.
  Turn **The model uses it on its own** off to keep one for `/` only.
- **Parameters**: each `{{name}}` in the instructions is one (a new one is
  added as you write it); say what it is, whether it is required, and its
  default.
- **Yours**, **shared with your groups** (their members use it; only you
  change it), or **for everyone** (made and changed by admins; audited as
  `skill.*`). Your own slash names are unique, and so are the company's; the
  same name from two places shows both, yours first.
- **Arena's own**: the skills people use most come with Arena, for the chat
  and Code Arena alike: `/code-review`, `/security-review`, `/write-tests`,
  `/debug`, `/refactor`, `/explain-code`, `/commit-message`,
  `/mr-description`, `/release-notes`, `/document-project`, `/plan-change`,
  `/brainstorm`, `/summarize`, `/meeting-notes`, `/write-email`,
  `/analyze-data`, `/make-deck`, `/translate`, `/sql-explain`, `/api-review`
  and `/postmortem` (the repository's `skills/` folder). Nobody edits them;
  yours, a group's or the company's of the same name wins over one, and an
  admin turns one off for everyone (**Turn off for everyone** on the Skills
  page, audited as `skill.off`; Code Arena leaves it out too).
  **Settings → Chat → Arena's own skills** off removes them all.
- **Plugins bring their own** ([plugins.md](plugins.md#skills)): each skill
  folder of a plugin (its `SKILL.md`) is a slash command too, for whoever may
  use the plugin's tool, and goes when the plugin is removed: `gitlab-issues`
  brings `/triage`.
- Up to 200 skills per person, 20,000 characters of instructions and 20
  parameters each.

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
  your credit. Nobody is in the run's chat to press **Allow**, so the model is
  not offered **Deep research** while it asks first (Admin → Tools); to have
  a task research, an admin turns that off, or you ask for it in a chat. It
  is a new chat, titled with the task and the
  time, or with **One chat for every run**, the same chat carried on, so each
  run reads the ones before ("what changed since yesterday").
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
[integrations.md](integrations.md). Installed on a phone:

- **Share to Arena.** Arena is in the phone's Share sheet: a file, a page or
  words from any app open a new chat with the files attached and the words
  (a page's title, text and link, each once) in the box, to ask about. The
  app's service worker keeps what was shared (up to 50 MB together) until
  the chat takes it; what nobody took goes after a day.
- **Shortcuts.** A long press on the app's icon offers **New chat**,
  **Talk** (a voice conversation that starts at once) and **Record a
  meeting** (recording starts at once; see [Meetings](#meetings)).
- **A poor connection.** A message sent with no connection waits above the
  box (**No connection: what you send waits here**), kept in the browser
  for every tab, and is sent by itself, oldest first, when the connection
  is back; a new chat is made then. A message the server refuses then stays
  there with **Send again** and a way to remove it. Talk, Compare and a
  sketch's question need their answer at once and do not wait.

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

Safeguards keep the chat from being abused or used to harm. **Limits per
person** are under **Settings → Safeguards**: the longest message and files
per message; messages a minute (20) and a day; pictures a day (100) and deep
research answers a day (20). Past one, the message is refused with what to do
instead (HTTP 429). **Safeguards on** turns everything off when unticked.

Everything else is **rules** (Admin → Safeguards; see the admin manual): each
looks for secrets, personal data, words, a topic the model judges, prompt
injection, links or files, in questions, files, answers, tool calls and
results or API requests, for everyone or some groups and people, with some
models or all, and blocks, masks, warns, asks for a reason, logs, sends the
question to another model, or allows. The app starts with these:

- **Secrets in what people send** (on): private keys (PEM blocks), cloud and
  service tokens (AWS `AKIA…`, GitHub `ghp_`/`github_pat_`, GitLab `glpat-`,
  Slack `xox…`, `sk-` keys, Google `AIza…`, Stripe, Hugging Face, npm, JSON web
  tokens, Azure storage keys) and passwords in obvious places (`password=…`, a
  connection string's `Password=…;`, `user:password@` in an address). The
  message is not sent, or the file not attached, and the person is told what
  kind was found. Set it to **Mask** and it goes with each secret replaced by
  `[removed: GitHub token]`, in the chat as well as to the model. A
  placeholder (`$DB_PASSWORD`, `<password>`, `***`) or a name in code
  (`config.password`) is not a secret. Each one found is audited by its kind,
  never the secret. A refused secret is not a strike: it is an accident, not
  abuse.
- **Personal data to the model** (off): e-mail addresses, phone and card
  numbers (Luhn-checked), IBANs, IP addresses, social security numbers, dates
  of birth and medical record, NHS, national and Medicare numbers reach the
  model masked; the chat keeps them as written.
- **Blocked words** (off until it has words): phrases matched as whole words,
  ignoring case, or regular expressions (`re:`).
- **Harmful requests** (off): the model reads the message first and refuses
  one asking for harm (violence, self-harm, sexual content involving minors,
  mass-casualty weapons, malware, hate, fraud); learning, safety, news and
  fiction stay allowed.
- **Jailbreaks** and **Prompt injection in what tools bring** (monitor only):
  record messages that try to get round the assistant's rules, and
  instructions hidden in pages and tool results, until an admin turns them on.

**When a rule asks you.** A **Warn** rule shows what it found and asks
**Send it anyway?**; one that **asks for a reason** waits for yours, which is
recorded. Either way the message is back in the box if you keep it.

**When a message is refused**, the sentence says why. **Ask for a review**
under it sends your reason to the people who look after the safeguards; if
they allow it, you may send the same message again within a day, and you hear
either way in your bell. A refusal that counts as a strike is in the audit
log and the admins' bell; **Refusals that suspend an account** (Settings,
off by default) disables an account after that many in a day.

**The web's content is marked as data**, so the model never follows
instructions hidden in a page (prompt injection).

**On the API path too** (Check API requests too, on by default): before each
request with an API key, the gateway asks the app (its guardrail,
`config/litellm.yaml`). The rules that look at API requests read all of the
request's text; the model's checks read its last question; a warning cannot be
answered there, so it is taken as said. A refused request gets the gateway's error
with the same sentence the chat shows. The chat's limits per person (length, messages a
minute) are the chat's only: keys have their own, requests at once and the rate
limits (requests and tokens a minute), and the chat is held to none of those.

## Fair use

A model serves few people at once: its slots (**Answers at once** on its card
in Admin → Models, and its copies on other GPU servers). So that everyone gets
their turn:

- **In the chat**, each model has its own line: it runs as many answers at
  once as it serves, so someone on a small model never waits behind people on
  the big one. A person has one answer running at a time, on any model
  (Settings → Chat → Answers at once, per person).
  **Answers at once, everyone** can also limit all models together (0, the
  default: no limit beyond each model's own). Who waits sees which model is
  busy and how many answers are ahead of them ("Qwen3.8-Flash-Next is busy:
  2 answers ahead of you"), or that their own other answer goes first. A free
  place goes to whoever has had least: someone with nothing running goes
  before someone whose last answer just ended. A wait of more than ten minutes
  gives up and says which model is busy. While the GPU is lent to a picture or
  video ([admin.md](admin.md#picture-video-and-speech-models)), an answer whose
  model was unloaded for it waits and says so ("The GPU is making a video with
  Wan2.2-TI2V-5B just now: … loads again as soon as it is done"), then goes on.
- **Each conversation keeps its slot** in the engine, so the next turn reads
  only what is new from the cache (see [admin.md](admin.md#the-token-cache)).
- **API keys** (coding agents, IDEs, scripts) have four requests answered at
  once (API requests at once, per key); more wait their turn in the order they
  came and are answered as one ends, never refused at once, so an agent that
  sends several at the same time (sub-agents, tools side by side) is not slowed
  by retries. Only a request that waits past ten minutes (API request's longest
  wait) gets HTTP 429 with `Retry-After`. It holds for the OpenAI-style
  (`/v1/chat/completions`, `/v1/responses`, `/v1/completions`) and the
  Anthropic-style (`/v1/messages`) requests alike, within seconds of a change.
- **Rate limits** hold each key to so many requests and tokens a minute, when
  an admin sets them: the company's (Settings → API keys), a group's, or a
  person's own. Past one, a request gets HTTP 429 with `Retry-After` and the
  limit it met. Your key's card (Your account) shows its limits, what it used
  in the last minute and what was refused in the last day. None is set out of
  the box ([admin.md](admin.md#rate-limits-for-api-keys)).
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
- **Skills** are their owner's, unless shared with groups (members use
  them) or made for everyone by an admin; a plugin's are for whoever may use
  its tool.

## Cost and credit

The chat talks to LiteLLM with its own service key (alias `chat`). Each request
names the person, so spend is theirs (surface **Chat** under Usage & cost), and
their credit applies.

Under each answer: its tokens in, cached and out, and what it cost. The cost is
kept as each part runs, at the model's prices then (its own, else Settings →
Prices; [admin.md](admin.md#prices)), cached input at its own price: every
round, the pictures, video and speech its tools made, and its sub-agents'
rounds (kept on their delegate call, with their tokens). The gateway is given
the same prices, so it books the same. An answer from before costs were kept
shows `≈`: its cost worked out at today's prices, until an admin recalculates.
A comparison's two answers show their cost once you have voted, like their
models: the prices would tell them apart. **Usage & cost → Your prompts** lists
every answer and API request with its tokens and cost (a comparison's answers
as "Model A" and "Model B", without cost, until you vote)
([admin.md](admin.md#usage--cost)).

What a person has spent is what the gateway's request log puts to them, over
every path (chat, API keys, agents), by the same rule as the usage dashboards;
the overview, People, the export and Home all read it, this month's. The credits
are one per kind (the chat's answers, API keys' text requests, pictures, video,
speech) and the app's ([admin.md](admin.md#credits)): before each answer the
app checks what the log puts to the person this month for the chat against
their chat credit and their groups'; before each picture, video or speech the
chat makes, that kind's; the gateway asks the app before each API request,
for the kind of its model. The gateway holds no budget of its own.

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
steps** are under Settings → Model. So is **Thinking between tool calls**
(`low` as installed): how hard the model thinks in the rounds after a tool's
result, where it reads the result and calls the next tool or answers. It is
never above the chat's own level; `same` keeps the chat's. Thinking is the
wait between two tool calls: measured on Qwen3.8-Flash-Next here, a round
started 2.2 s after the tool's result and thought 3 s before it wrote. Everyone's language, voices, reading speed
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

A question can quote part of an earlier answer: `POST .../messages`,
`.../compare` and `.../queue` take `quote` beside `content`, and every message
of `GET` (and of a shared chat) carries its `quote`, as does every queued
message. The quote is kept in its own column (`chat_messages.Quote`,
`queued_messages.Quote`), never inside the content, and is at most 4,000
characters (a 400 `quote` otherwise); the message's own length limit is for
what was typed, and a quote alone is not a message (typed text or files are).
It is the person's words to the model, never the model's: secrets, blocked
words and the model's check apply to it as to the message, and personal data
in it is masked for the model. The model reads it before the question, in
this shape (a `</quote>` inside is written `<\/quote>`):

```
The person quotes this part of an earlier answer:
<quote>
…the quoted part…
</quote>

…the question…
```

The question's files follow. Compaction summaries, Auto's sorting and the
passages searched for a question read the quote too; an edit is sent with the
quote it keeps, and forks, shared chats and the data export (as `> ` lines
under "## You") carry it.

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
| "You have used all your chat credit for this month (…)" (or API, picture, video, speech) | the person's credit of that kind is spent; the other kinds go on | an admin raises it under People, or wait for the first of the month |
| "You have no picture credit: an admin sets it under People" (or chat, API, video, speech; "under Groups" for a group's) | an admin set that credit to $0 and nothing was spent | an admin sets a credit, or no limit, under People (or Groups) |
| "… has used its chat credit for this month" or "You have used your chat credit as a member of …" | a group's credit of that kind is spent | an admin raises it under Admin → Groups → the group → Policies |
| "Your API access is off" | an admin took it (People → the person → API access) | an admin gives it back; the chat is not affected |
| "This message was not sent: it holds a private key." | the secrets rule found a secret | remove it (a placeholder will do) and send again |
| "The message did not reach the server" | the network or TLS failed before the server got it | the text is back in the box; send again |
| "This chat is already answering" | one answer at a time per chat | stop it, or wait |
| "This conversation is longer than the model can read" | the question and its attachments alone do not fit | start a new chat, or attach less |
| "… cannot see images" | the chat's model has no vision | choose a model that shows "Sees images" |
| "You may not use …" | an admin took the model away from you | choose another model |
| "Auto is not available to you" | Auto needs a model for small steps that you may use | choose a model; an admin sets the small model and who may use it (Admin → Models) |
| An Auto answer says "The model for small steps is not available to you now" | the small model is not yours to use, or cannot load now | nothing: the main model answered. An admin can keep it loaded (Admin → Models) |
| "… is not loaded right now" | the chat's model is not the one the engine has loaded | choose a loaded model, or ask an admin to load it (Admin → Models) |
| "… cannot be loaded now" (in the chat, or an API call's error) | the engine holds all the models it may: those that may make room answered others all that minute, or each place is taken by, or kept for, a model that never makes room (one kept loaded, the model new chats use, the one for small steps) | choose a model that is loaded, or try again in a few minutes; an admin can raise Models loaded at once (Settings → Model) |
| "This message was not sent: the safeguards could not read it first" | the safeguards' check could not have its model: the engine stayed full for a minute | send it again in a moment; it is not counted against you |
| "… is busy: N answers ahead of you." (or "your turn is next") | the chat's model is serving as many answers as it can at once; yours is in its line (people on other models do not wait for it) | nothing: it starts on its own. A group with a higher priority goes first (Admin → Groups). An admin can change the limits (Settings → Chat) |
| "The chat is busy: N answers ahead of you." | the whole chat runs as many answers as an admin allows at once (Answers at once, everyone) | nothing: it starts on its own; an admin can raise the limit (Settings → Chat) |
| "Waiting for your other answer to end first." | you have as many answers running as you may (Answers at once, per person), in other chats or on other models | nothing: it starts when one of yours ends, or stop one |
| "… has been busy for 10 minutes: nothing was sent" | the line did not move for that long (Longest wait in line) | ask again later, or choose another model; tell an admin if it happens often |
| An API call answers **429** | the key already has as many requests running as it may | wait for one to finish, or retry; an admin sets the limit (API requests at once, per key) |

| "Waiting for your turn: N answers ahead of you" | the model is serving others; your answer is in line | nothing: it starts on its own. A group with a higher priority goes first (Admin → Groups). An admin can change the limits (Settings → Chat) |
| "The model has been busy for 10 minutes" | the line did not move for that long | ask again later; tell an admin if it happens often |
| An API call answers **429** | the key already has as many requests running as it may (`Limit type: max_parallel_requests`), or used its requests or tokens for this minute (`Limit type: requests` or `tokens`) | wait as long as `Retry-After` says, then send again; Your account → API key shows the limits and what was used. An admin sets them (Settings → API keys, a group's Policies, a person's Rate limits) |
| "Argus is not available for this answer: …" | Argus's reason follows | usually no GitLab account matches the person's email; see [ARGUS.md](argus/README.md) |
| "… is not available for this answer: … did not answer" | an MCP server is down or refused the key | Admin → Tools → the server's **Edit** → **Test** |
| No **Image generation** or **Video generation** in the Tools menu | the model is off, or its server does not run | Admin → Models: turn it on; a module left out in `docker-compose.override.yml` stays out |
| No **Python** in the Tools menu | the sandbox is not running | `docker compose ps sandbox`; `scripts/sandbox-check.py` says whether it is sound |
| No **Decide (Laya)** in the Tools menu | the laya module is off (the default), or still fetching or loading its checkpoints | `COMPOSE_PROFILES=laya` in `.env`, then `docker compose up -d laya`; Admin → Models shows the downloads |
| No **Web** in the Tools menu | it is off (the default), or no site is allowed | Admin → Tools → Web on, and Settings → Python and web → Sites the chat may open |
| No **Deep research** in the chat's **Tools** | an admin has not given deep research to you | Admin → Tools → Deep research: who may use it |
| **Deep research** is greyed out | the chat's model calls no tools, or **Compare** is on (its tooltip, or a tap, says which) | choose a model that calls tools, or turn Compare off |
| A scheduled task or a bot never starts deep research | it asks first, and nobody is there to allow it | Admin → Tools → Deep research → **Ask before each run** off, or ask for it in a chat |
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
  Also deep research as a tool (`DeepResearchTests`): on for everyone, asking
  first, with no setting saved (as on an upgrade); off, for admins and for a
  group, with a message or a queued one refused for whoever may not; a queued
  one answered plainly once it is taken away; the model starting one when
  allowed (the web and sub-agents join, its parts run, it is not offered
  again nor to the parts), declined, not asking first, and past the day's
  limit; not offered to a scheduled task's run (nor a Slack bot's answer,
  in `ChatBotTests`) while it asks first, and offered once it does not; never
  offered to Compare's two answers; and Arena MCP running one in a chat of
  the person's own, its steps as progress and its report as the result, not
  served (and saying why, on Connect your tools too) while the web asks first,
  an unknown function name then still "no tool named". The web's tests:
  the switch only for those given it, a note when it is taken away (at once,
  or on the next visit), and the reason on a tap when it is greyed out.
  Also memory: "remember I deploy with Podman" in the next chat's system
  prompt (after the fixed notes) and gone once deleted; an offer kept only
  when accepted (in the person's words), taken back, declined, and nobody
  else able to answer it; a task's chat only offering; the person's switch
  and the company's; the block's budget. And skills (`SkillTests`): a person's own, shared with their groups
  (only the owner changes it), the company's (admins only, audited), the
  checks on names, parameters and groups; one used with its parameters (the
  model reads its instructions filled in, the chat keeps the words and the
  skill apart, one missing a required parameter refused), queued with an
  answer running, used by the model on its own (`use_skill`) when it says
  when it is for; and a plugin's `/triage` (its SKILL.md) installed with it,
  for whoever may use its tool, and removed with it; prompts kept as skills
  on an upgrade (`SkillsMigrationTests`). And ↑'s history: one's own
  messages across chats, newest first, a page at a time, archived chats' too,
  a deleted chat's and pasted documents left out, and nobody else's: not a
  triggered task's run, nor a shared chat's questions copied into a fork (what
  is written in the fork after counts).
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
  - ↑ and ↓ in the box: this chat's messages newest first, then the person's
    other chats (asked for only then, each text once; a new chat's box goes
    straight to them), back down to the draft
    kept, Esc to it, a message brought back sent as a new one and edited as a
    copy, the `/` menu keeping its arrows (and not opened by a message
    brought back), lines of a draft or of a message brought back first (a
    line brought back that the box wraps stepping on at once), and keys that
    compose or carry Shift, Ctrl, Alt or ⌘ left to the box
  - the list's fork, archive, Archived view and unarchive; fork from an
    answer; the question rail; the archived notice
  - Auto in the model menu, its note under an answer (the small model's, or
    handed on with a thinking level), **Ask the big model**, no Auto without
    a small model, and the sub-agents' model under an answer
  - `/review` chosen from the `/` menu: its chip and parameter fields, not
    sent while a required one is empty, then sent as a skill with the box's
    words; Backspace letting it go; the menu found by title, closed with Esc;
    a skill without parameters; the Skills page (sections, a new skill with
    parameters shared with a group, an admin's for everyone, edit and
    delete)
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
