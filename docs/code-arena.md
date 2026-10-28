# Code Arena

Our own coding agent, and an IDE in the browser around it. A person runs
`code-arena` in a project folder on their machine and gets a page laid out as
VS Code is: the folder's files, an editor, search, terminals and the agent's
chat (or `code-arena chat` keeps the agent in the terminal). The agent reads
and changes the code there, runs commands, and works through the Arena: the
model through the gateway, and all of the chat's tools (web search and pages,
Python in the sandbox, pictures, speech, Argus's code search, plugins,
knowledge) through Arena's MCP endpoint, as that person.

It is **one file with nothing to install**: a self-contained .NET program for
Linux, macOS and Windows. It talks to the Arena only, never to the internet, so
it works on a network with no way out.

The code is `src/CodeArena` (the base class library only, no packages), its
tests `tests/CodeArena.Tests`.

## Getting it

**Your account → Connect your tools → Code Arena (our own agent)** installs
it in one line, the build for the machine checked against the SHA-256 this
Arena lists (the downloads need no sign-in; the program does nothing without
a key):

```bash
curl -fsSL https://DOMAIN/api/downloads/code-arena/install.sh | sh     # Linux and macOS: into ~/.local/bin
```

```powershell
irm https://DOMAIN/api/downloads/code-arena/install.ps1 | iex           # Windows: %LOCALAPPDATA%\Programs\code-arena, on PATH
```

`CODE_ARENA_DIR` puts it elsewhere; on a self-signed deployment,
`curl --cacert ca.crt -fsSL …/install.sh | CODE_ARENA_CA=ca.crt sh`. Or
download the file for a system yourself:

```bash
curl -fLo code-arena https://DOMAIN/api/downloads/code-arena/linux-x64
chmod +x code-arena && mkdir -p ~/.local/bin && mv code-arena ~/.local/bin/
```

**It keeps itself current.** Arena serves the builds of its own version, so
a newer one there is the one to run: at a session's start (not for `-p`),
once a day and in the background, Code Arena asks the Arena it is signed in
to and says "Code Arena 7.0.0 is available from Arena (this is 6.9.0)"; the
IDE shows **Update** in its status bar. `code-arena update` (or that button)
downloads the build for this system, checks its size and SHA-256, and puts it
in this program's place (on Windows the running one moves aside as
`code-arena.exe.old`); the next start runs it. `"updateCheck": false` in
`config.json` stops the daily look.

The systems: `linux-x64`, `linux-arm64`, `osx-arm64` (Apple silicon),
`osx-x64` and `win-x64` (`code-arena.exe`). `/api/downloads/code-arena` lists
them with their SHA-256.

- **macOS** marks a file downloaded in a browser, and refuses to open it until
  the mark is gone: `xattr -d com.apple.quarantine code-arena`. The builds are
  signed ad hoc, not with an Apple developer ID.
- **Windows** may warn that the file is unknown (SmartScreen): **More info →
  Run anyway**, or unblock it in the file's properties. Put `code-arena.exe` in
  a folder on `PATH`.

When the page says the Arena has no builds yet, an admin adds them (below).

A release also comes as packages, one per system:
`code-arena-<version>-<rid>.tar.gz` (a `.zip` for Windows), each holding the
program, a `README.txt` (how to run it, sign in, the IDE, the terminal, the
licence), `LICENSE.md` and `LICENSING.md`, with a `SHA256SUMS` for them all.
`tools/package-code-arena.sh` builds them (below).

## Signing in

```bash
code-arena login --url https://DOMAIN
```

It asks for the Arena's address (when `--url` is not given) and the person's
API key (**Your account → API key**; typed without echo), checks the key with
the gateway (`GET https://gateway.DOMAIN/v1/models`), connects to Arena's
tools to say how many there are, and saves both in its config file, readable
by its owner only (0600). The key is never printed. `code-arena logout` forgets
the key; `code-arena models` lists the models the key may use.

Without a key at hand, press Enter at the key's question (or give
`login --user NAME`): Code Arena signs in with the user name and password as
Arena's sign-in page does, so its lockout, its directory and its second
factor hold (the code is asked for when the account has one). Arena then
makes a key of the person's own for this machine, **Code Arena on NAME**
(one of that name before it is revoked first), which **Your account → API
keys** lists, and the web session is signed out again: the key is what Code
Arena keeps. Someone who signs in with the company's account has no password
in Arena: they make a key there and paste it.

In the IDE, **Accounts** (the person at the activity bar's foot, above the
gear) says whose key it works with and on which Arena, signs in with a user
name and password the same way (Code Arena then starts again with the new
key, in the same window), signs out (the key it signed in with is revoked at
Arena and forgotten here), opens Arena's **Your account** and, for those
who may see it, **People**, and sends feedback about Arena or Code Arena
(**Send feedback…**, to Arena's Admin → Feedback, with the person's key). A dot on it says it is signed out or Arena did
not take the key.

| | Linux and macOS | Windows |
|---|---|---|
| settings (`config.json`, `ARENA.md`) | `~/.config/code-arena/` (or `$XDG_CONFIG_HOME`) | `%APPDATA%\code-arena\` |
| sessions | `~/.local/share/code-arena/sessions/` (or `$XDG_DATA_HOME`) | `%LOCALAPPDATA%\code-arena\sessions\` |
| what was sent in each folder (↑) | `~/.local/share/code-arena/history/` | `%LOCALAPPDATA%\code-arena\history\` |
| the IDE's layout and theme (`ide.json`) | `~/.local/share/code-arena/` | `%LOCALAPPDATA%\code-arena\` |

`CODE_ARENA_HOME` puts both under one folder (`config/` and `data/`), for a
portable copy on a USB stick. `ARENA_URL`, `ARENA_API_KEY`, `ARENA_GATEWAY_URL`,
`ARENA_MCP_URL` and `ARENA_MODEL` override the file for one run (CI, scripts).
The gateway is taken to be `https://gateway.DOMAIN` and Arena's tools
`https://DOMAIN/mcp`; `login --gateway URL` (or `"gateway"`, `"mcpUrl"` in the
file) when they are elsewhere.

### A company's own CA

When the Arena's certificate comes from a CA of the company's own (as
`deploy/scripts/make-cert.sh` makes, `deploy/certs/ca.crt`), give that CA's
file once:

```bash
code-arena login --url https://DOMAIN --ca ca.crt
```

The path is saved (`"ca"` in the file) and the CA trusted besides the system's,
for the gateway, Arena's tools and the person's own MCP servers. In order:
`--ca`, `ARENA_CA_CERT`, the file's `"ca"`, `SSL_CERT_FILE`. A certificate for
another name is never accepted, and there is no switch to turn the check off.
Trusting the CA system-wide works too, and needs nothing here.

## Working with it

```bash
cd my-project
code-arena                          # the IDE for this folder, in your browser (see The IDE)
code-arena chat                     # a conversation in this terminal instead
code-arena "why does the build fail?"   # one in this terminal that starts with this
code-arena -p "list the TODOs in src/"  # answer once, print the answer, exit
git diff | code-arena -p - --output json
code-arena chat --continue          # carry on with this folder's last session
code-arena chat --resume            # choose a saved session (or --resume <id>)
code-arena --plan                   # a new session that carries on the plan an earlier one left in progress
code-arena chat --web               # your chats in Arena; --web N (or its id) continues one here
code-arena --version                # the version, the licence and the source
```

`code-arena` without a prompt is the IDE (`code-arena web` is the same, and
takes no prompt); a prompt, `-p` or `chat` keeps the conversation in the
terminal. A build without the IDE's page (below) says so and runs the
conversation in the terminal.

In the terminal, the answer streams as it is written; the model's thinking is shown dimmed
before it. Each tool call is a line (`● edit_file src/app.py`), with its result
under it: a diff for an edit, the last lines of a command's output, a count for
a search. Each turn ends with its tokens and the share read from the cache
(`12.4k in (81% cached) · 310 out · 3 requests`) and how full the model's
window is (`context 18.2k / 131k (14%), compacts at 80%`). The session keeps room in the
window for the answer: the model's longest answer (at most an eighth of the window), or the
longest answer of this session and a fifth more (at most a third), so it compacts before a
long answer would be cut. An answer cut anyway at the output limit is kept and the model
carries on where it stopped (compacting first when the window was why); a tool call cut
half-way is done again in smaller parts. **Ctrl+C** stops the turn and
keeps what was said; at the prompt it clears what is typed, and on an empty
prompt twice leaves. What is typed while a turn runs is kept: a line ended with
Enter is sent as the next message when the turn ends, and a line begun waits in
the prompt. A question (`Allow edit_file?`) reads only what is typed after it is
asked; a message typed there instead of `y`, `n` or `a` is a no, and is kept as
the next message. Ctrl+C at a question stops the turn, and what was typed
meanwhile comes back in the prompt, to send, change or clear. **Ctrl+Z** at the prompt stops code-arena, as in a shell:
`fg` brings it back, with what you were typing (not on Windows). A line ending
in `\` goes on to the next.

The prompt is edited in place, as a shell's is: ← and → (with Ctrl or Alt, a
word), Home and End (Ctrl+A, Ctrl+E), Backspace and Delete, Ctrl+U and Ctrl+K
(to the start or the end of the line), Ctrl+W (the word before), Ctrl+L (clears
the screen). **↑** brings back what you sent in this folder, newest first,
in this run and the ones before (as a shell keeps its history); **↓** goes
back toward the newest and past it to what you were typing, kept; **Esc**
goes straight back to it. **Enter** sends the message brought back as a new
one; editing it changes a copy. In a message of several lines, one brought
back too, ↑ and ↓ move between its lines first. Enter in a paste, and
Alt+Enter, start a new line instead of sending. A message taller than the
terminal shows the lines round the caret while you edit it, and all of it
once sent.
The history is a file per folder in the data folder (JSON Lines, 0600, cut
back to the last 1,000 messages at 2,000), shared with the IDE's chat in the
same folder: a message sent in one is there in the other. Lines read from a
pipe, and `/exit`, are not kept. Without a terminal both ways (a pipe, or
`TERM=dumb`) the prompt reads plain lines, as before.

`-p` (one-shot) prints only the answer on stdout, the progress on stderr, and
cannot ask: edits and commands are refused unless the mode allows them
(`--mode auto-edit` or `--mode yolo`). `--output json` prints
`{"result", "is_error", "session", "model", "usage"}`.

### Commands

| | |
|---|---|
| `/help` | the commands |
| `/model [name]` | show the models, or switch |
| `/mode [mode]` | show the mode, or switch |
| `/thinking [level]` | `default`, `off`, `low`, `medium`, `high`, `xhigh` (sent as the chat sends it, through `chat_template_kwargs`); `default` is the web chat's default level when Arena says one (`DEFAULT_THINKING`), else the model's own |
| `/tools [on\|off NAME]` | the tools of the session, which ask, which are off and which are loaded when needed; `on` or `off` a group of Arena's or a server's tools in this session (by its name, its title or one of its functions) |
| `/plan` | the plan, as it stands |
| `/review [BASE]` | reviews the changes not yet committed, or this branch's over BASE (`/review main`): the model reads them with git, tries to refute each finding before it reports it, and ends with a verdict; it changes nothing. A `/review` command of the project's or your own goes first |
| `/agent [NAME TASK]` | your own coding agents (below); with a name and a task, run one on it: its output shows as it comes, and what it said goes into the conversation |
| `/compact` | summarize the conversation now |
| `/compact-at [% [%]]` | show or set when the session compacts itself and what it keeps (below); `off` for only when a request would not fit, `account` for your Arena account's, `default` for that and 25 |
| `/context` | how full the model's window is, what fills it and its biggest parts |
| `/cost` | tokens spent, and how full the model's window is |
| `/mcp [retry [name]]` | Arena's, Argus's and your MCP servers: connected or not and why; `retry` tries those not connected now |
| `/jobs [stop N]` | the commands run with no time limit, running or ended; `stop N` stops job N |
| `/clear` | a new session (the last stays saved) |
| `/resume [id]` | switch to a saved session |
| `/web [N\|all\|WORDS]` | your chats in Arena, newest first (the 30 newest; `all`: the 5,000 newest; `WORDS`: those of them with the words in their title or folder); `/web N` (a number in the list last shown, or a chat's id) continues one here |
| `/sync [all]` | where this session stands with its chat in Arena; `all` sends what Arena lacks of this folder's sessions |
| `/memory [add\|forget]` | what it remembers (below; Arena's when signed in); `add TEXT` for this project, `add --all TEXT` for every project, `forget N` |
| `/remember` | the project's long memory, `.remember/MEMORY.md` in the repository: its sections and topic files (below) |
| `/rewind [N] [code\|chat]` | the turns kept; `/rewind N` goes back to before turn N (below) |
| `/search WORDS` | earlier sessions in this folder that said these words |
| `/permissions [allow\|deny\|remove RULE]` | kept rules (below) |
| `/sandbox` | what commands in the sandbox may do |
| `/commands` | your own and the project's commands, sub-agents and skills |
| `!command` | run a command yourself (in the sandbox); its output goes with your next message |
| `/NAME [args]` | a command of your own or the project's (below) |
| `/exit` | leave (also Ctrl+D) |

## The IDE

```bash
cd my-project
code-arena                          # or: code-arena web
code-arena --port 8765 --no-open
code-arena --continue --mode auto-edit
```

`code-arena` starts a small web server on this machine, prints its address
and opens the IDE in a **desktop window of its own** (`--no-open`, or a
machine without a desktop, only prints it), through a file of the person's
own that sends it on to the address (below). The window is an installed
Chrome, Edge, Chromium or Brave in app mode: no tabs, no address bar, on a
profile of Code Arena's own (`window/` in its data folder, so it keeps its
size and place), and the browser keys VS Code uses there (Ctrl+W, Ctrl+T,
Ctrl+N) are the IDE's. `CODE_ARENA_BROWSER=/path/to/chrome` picks the
browser. Closing the window stops code-arena; a second code-arena's window
goes to the browser the first one started, so that one runs until Ctrl+C.
Without such a browser (Firefox has no app mode; a snap's browser cannot read
the data folder), with `--browser`, or with `"window": false` in
`config.json`, it opens in a tab of the default browser instead.
**Ctrl+C** in the terminal stops it, and the turn if one runs;
the terminal shows what the agent does (`● edit_file …`) while the page drives
it. On another machine over SSH: `code-arena --port 8765 --no-open` there,
`ssh -L 8765:127.0.0.1:8765 that-machine` here, and open the address printed.

**In Code Studio** (`code-arena web --connect`, signed in with `code-arena
login`): the folder shows in Arena's **Code Studio** too, as *the folder on
this machine*, while code-arena runs, and opens there in the browser, from any
computer you sign in to Arena on. code-arena dials Arena (a WebSocket, with
your API key) and Code Studio's IDE reaches this run through it: each of its
calls and WebSockets (terminals, language servers, the debugger) is answered
by this run's own web server, with this run's own key, which never leaves the
machine; nothing but the IDE's calls (`/api/…`) goes through. Only you reach
it, signed in to Arena, and only while this runs; a lost connection is dialled
again (after a second, doubling to 30). The folder keeps its place in Code
Studio between runs (its id is kept with the folder's state); deleting it
there disconnects it, and the folder stays as it is. Arena's operators run
the relay: connect folders you would open on that Arena's own workspaces.

The page is laid out as VS Code is, in Argus Arena's design system:

- **activity bar** (the side bar's edge): Explorer, Search, Source control
  (with a count of the files changed since the last commit), Agent changes
  (with a count of the files), Problems (with a count of the errors) and Chat
  switch the side bar; the one shown hides it.
  At its foot: the terminal panel, the theme (light, dark or the system's:
  kept in the person's `settings.json`, below), **Manage** (the gear:
  Settings, Keyboard Shortcuts, Color Theme, Extensions and the Command
  Palette one click away, and the model's tools: as Arena says, direct, as
  code or both, kept in config.json), Help and About.
- **moving the parts**: drag a view's icon to another place in the activity
  bar, or onto the panel's tabs to move the view into the panel (a tab
  there; drag it back onto the activity bar to return it); drag the panel's
  tabs into another order. An icon's right-click menu moves its view to the
  panel, up or down, puts the side bar on the right (the chat then on the
  left) and resets the views' places; a panel tab's moves its view back and
  puts the panel below the editor or beside it, left or right. The palette
  has them too (Toggle Primary Side Bar Position, Move Panel Left, Right or to
  Bottom, Move View to Panel, Move Panel View to Side Bar, Reset View
  Locations). Where everything is stays for the next run, with the sizes.
- **help**: Help in the activity bar opens the IDE's help over the page:
  what each part does, the common tasks step by step and the keys, with a
  link to this page of the manual in the Arena signed in to
  (`https://DOMAIN/help/code-arena`). The **?** in a panel's header
  (Explorer, Search, Agent changes, Sessions, the terminal, the chat) opens it
  at that panel. Esc closes it.
- **Open Folder…** (**Ctrl+K Ctrl+O**, the Explorer's folder button, or the
  welcome page) chooses another folder as VS Code's simple dialog does: the
  folder shown and its folders, Enter goes into one, the first row opens the
  one shown, and a path typed (from the folder shown, or whole: `~/`, `/`,
  `C:\`) goes there; the folders opened lately and, on Windows, the drives are
  listed too. **Open Recent…** (**Ctrl+R**) lists the folders opened lately.
  Code Arena then starts again on that folder in the same window, at the same
  address with the same key (its terminal says so): the session, the
  terminals and the language servers are that folder's. The last session of
  the folder left is saved, as when it stops. Not offered in Code Studio,
  whose workspace is the server's.
- **Add Folder to Workspace…** (the Explorer's button, or the command)
  chooses a folder the same way: the agent's tools may use it from then on,
  and the Explorer shows it below the folder's files, as a root of its own
  (× takes it out again). Its files open and save in the editor by their
  whole paths; Search, Source control and the language servers stay the
  folder's. The folders added are kept with the folder, and the next run has
  them.
- **Explorer**: the project's files, with New file, New folder, Refresh and
  Collapse at its top. Click opens; a right-click menu has new file, new
  folder, Copy path, Rename (F2) and Delete (Del, asks first); the arrow
  keys move through the tree. F2 and Del are the keymap's (`renameFile`,
  `deleteFile`): keybindings.json changes them or takes them away. The files the agent changed are marked, and so
  are the folders they are in. A file deleted with unsaved changes keeps its
  tab (the question says so): saving it makes the file again.
- **editor**: tabs with Monaco, highlighting by the file's name (or
  `files.associations`), set up by the settings (below). A dot marks unsaved
  changes and **Ctrl+S** saves (**Ctrl+K S** saves them all). A
  file changed on disk since it was opened is not written over unseen: saving
  asks first. Closing a tab with unsaved changes asks Save, Don't save or
  Cancel (a compare asks for each of its files no other tab holds); a file with
  unsaved changes whose last tab goes without asking (a compare accepted,
  reverted or dropped) stays open in a tab of its own; leaving the page with
  any warns. A binary file, or one over 5 MB,
  says so instead of opening. When code-arena stops answering (it stopped, or
  the SSH tunnel dropped), a line at the top says so and the editor stays, so
  what is not saved can still be copied out; it goes when it answers again.
- **the agent's edits**: a file the agent edits reloads in its tab when nothing
  in it is unsaved (undo still works; only the lines that changed are replaced,
  so the cursor and the breakpoints stay on their lines, moved down by the lines
  added above them), and after each turn every open file is checked against the
  disk. A compare whose file the agent made or deleted shows that side as it
  is now (empty, or the file). What is typed while a file reloads stays, unsaved:
  saving it then asks first.
- **compare**: Monaco's diff editor, as VS Code's compare. The agent's changes
  to a file (as it was before the agent's first change, and now: from Agent
  changes or the Explorer), a file's changes in Source control (not staged:
  the index and the file; staged: the last commit and the index; a commit of
  the history opens to the files it changed, each as its parent and it had
  it; **Open File History…** and **Compare with Revision…** take any commit or
  branch), the file shown with what is saved (**Ctrl+K D**), with the
  clipboard (**Ctrl+K C**; when the browser does not let the page read it,
  paste it in the box that opens), with another file (**Compare Active File
  With…**), or two files of the Explorer (right-click **Select for Compare**,
  then **Compare with Selected**). A side that is a file is the file itself:
  typing there changes it (it shows unsaved) and **Ctrl+S** saves it; the
  arrows beside a change put the left side's lines back. Its bar has the
  sides, the counts, the previous and the next change (**Shift+Alt+F5**,
  **Alt+F5**), inline or side by side, whitespace ignored, unchanged lines
  folded (`diffEditor.*` in the settings), the sides swapped and Open file;
  and, for the change at the cursor (or those selected): **Stage** and
  **Revert** in a change not staged (Ctrl+K Ctrl+Alt+S, Ctrl+K Ctrl+R: the
  index takes exactly that change, the file stays), **Unstage** in a staged
  one (Ctrl+K Ctrl+Alt+N; Ctrl+K Ctrl+N in a window of its own), **Accept** and **Revert** in the agent's (Ctrl+K
  Ctrl+Alt+A: the rest stays to review). The agent's changes also have
  **Revert** (puts the file back; a file the agent made is deleted) and
  **Accept** (keeps it) for the whole file, and Agent changes has **Accept
  all**. Below the agent's, **Your changes** lists the files changed by
  hand (in the editor or anywhere else: git's changes that are not the
  agent's), each opening as git's compare. A changed file's right-click menu
  (either list) opens its compare or the file, adds it to the chat, asks the
  agent to **Explain these changes** or **Fix these changes**, copies its path,
  and for the agent's accepts or reverts it. In a compare, the "now" side's
  right-click menu has **Add to chat**, **Explain this**, **Fix this** and
  **Complete this**, as the editor's does. A tab's right-click menu closes it,
  the others, those to its right, the saved ones or all, copies its path and
  compares it.
- **changes on disk**: what changes in the folder by anyone (your own editor,
  a command, git) shows at once: a file open and not edited here reloads, one
  deleted closes, the Explorer, Source Control and Changes are read again, and
  the language servers are told. Code Arena watches the folder (but `.git`'s
  insides, `node_modules` and `.code-arena`); where the system will not watch
  so many folders, the IDE reads it all again when its window comes back into
  view.
- **search**: text across the files git sees (`.gitignore` holds), with match
  case, whole word, regular expression, and files to include or exclude; a
  result opens the file at the match. **Replace** replaces what it finds, in
  one file or in all of them (it asks first; `$1` stands for a regular
  expression's group; the files keep their line endings; a file with unsaved
  changes in the editor keeps them, and saving it then asks).
- **quick diff**: bars beside the line numbers show the lines changed since
  what git's index has (green added, blue changed, a red mark where lines were
  taken away), as you type; **Alt+F3** and **Shift+Alt+F3** (or **Alt+F5** and
  **Shift+Alt+F5**, as in a compare) go to the next and the previous change,
  keys of the keymap that keybindings.json changes. `scm.diffDecorations` says
  where they are drawn: `all` (beside the line numbers, in the scroll bar and in
  the minimap), `gutter`, `overview` (the scroll bar), `minimap`, or `none`. Lines
  are compared without their ends, so a file checked out with CRLF against an
  index with LF shows no bars; changes more than about 1,400 lines apart (a
  file rewritten or reformatted) are one bar from the first to the last, so
  typing stays quick. **Ctrl+P** opens any file by a few
  letters of its path (`app:12` opens it at line 12; `:12` goes to line 12 of
  the file shown, as the line in the status bar does). **Ctrl+Shift+P** (or
  **F1**: Firefox keeps Ctrl+Shift+P for a private window) runs any command by
  name, as VS Code's palette: "Category: Title" with its key, the ones run
  lately first, a few words of the title (or of the command's id) narrowing
  them: the workbench's (save, close and reopen tabs, go back and forward,
  show or hide a part, the colour theme, the language mode, settings and
  keyboard shortcuts, help) and the editor's (Monaco's own, and Code Arena's:
  Add to chat, Explain, Fix, Complete).
- **terminals**: a panel under the editor (Ctrl+`), one tab per shell (bash 1,
  bash 2, …), + for another; drag its top edge to resize it. A path of the
  folder's in a terminal's output (a compiler's `src/app.ts:12:5`) is a link
  that opens the file there. Shown with none,
  it opens one; closing the last one hides it. A page reloaded attaches to the
  ones still running, with their screens (below). The panel's other tabs are
  the **Debug Console** and the **Output** (the language servers', below).
- **chat**: the agent's chat on the right: the model and thinking pickers,
  Sessions and Hide at its top. The pickers say what Arena's web chat says
  (read with the key from `GET /api/code-arena/config` at the start): which
  models are loaded, load when asked or are not loaded now, which see
  pictures, the deployment's thinking levels with their names, and which one
  **Default** is; Argus's answers link to the same GitLab. Without Arena every
  model counts as loaded, with Code Arena's own five levels. **Files** go with
  a message as in the web chat: the clip, a paste or a drop on the box (Word,
  Excel, PowerPoint, PDF, text, code, pictures), up to 10, as large as Arena
  takes. They are uploaded to Arena (through code-arena, which adds the key),
  which reads a document's text: the model reads it after the message (up to
  60,000 characters a file), and a model that sees gets the pictures with it
  (named otherwise; a sound or a video is named only). The question shows them,
  and the chat in Arena keeps them as its attachments, so the web shows them
  and its model reads them. Without Arena there is no clip. **Answer again**
  under an answer asks its question again as the web chat does: as it was,
  **Shorter** or **Longer** than that answer, with another model or other
  thinking (for that answer only), or **the files as they were** when the turn
  changed files (what its tools and the later turns' wrote is put back first,
  as `/rewind` does, while this run keeps that turn's checkpoint). **Edit** on a
  question asks it again with new words (its quote kept or taken away, its
  files sent again). Either goes back to before the question: the session
  goes on from there, and the chat in Arena keeps the earlier turns on a
  branch of their own, where the web's version arrows show them. Then the thread, with a card for each tool call and
  a diff under each edit, the approval card (**Allow**, **Always for this
  session**, **Deny**), Stop, the mode, `/compact` and `/clear`. A command
  the agent runs with no time limit shows above the box while it runs: the
  end of its output as it comes, how it ended, and **Stop**. **↑** in its box brings back this session's messages, then
  what this folder sent before (in the terminal too), as in Arena's chat;
  **↓** and **Esc** go back to what you were typing. A message sent while an
  answer is written is **queued** (Enter, or **Queue**): it shows above the box
  and runs once the answer ends, each in its turn: **Stop** stops the answer
  and the next queued message runs, and **Clear** drops the queue. A command with no time limit
  that ends while nothing runs starts a turn of its own, which the page shows as
  any other. A picture, a sound or a video one of Arena's tools made (kept in
  the folder under `.code-arena/media/`, the tool's result naming it) shows
  under its card: the picture opens larger at a click, the sound and the video
  play in place. A long session opens on its last 30 turns: **Show earlier**
  at the top adds 30 more each time, the turns being read staying where they
  were. The thread draws what streams in once per frame, and only the turn
  being written: a session of thousands of messages streams as smoothly as a
  short one. The Chat activity lists this folder's sessions; the list is read
  again when a session's first turn adds it, a turn of a session listed
  already just moves it to the top.
- **status bar**: the git branch, the agent's changes, the terminal and the
  number of commands running with no time limit on the left (and, after the
  first key of a chord, that it waits for the second); the cursor's line and
  column, the file's indentation (**Spaces: 4**: indent with spaces or tabs,
  detect it, or convert the file), its line endings (**LF** or **CRLF**), its
  formatter, its language server (its state: Restart and its log, below) and
  its language (another one, or **Configure '…' language based settings**),
  the MCP servers (connected of all:
  **MCP servers** lists them, why one is not connected and when it is tried
  next, with **Try again**), how full the model's window is (tokens used /
  the model's: **Context** sets when the session compacts and what it keeps,
  kept in `config.json`), the mode, the model and `code-arena <version>`
  (About: the version, the licence, the folder and the Source link) on the
  right.

The side bar, the chat and the terminal panel are resized by dragging their
edges (or with the arrow keys on the edge). Their sizes and which are shown
are kept by code-arena, in `ide.json` in its data folder, with the palette's
recent commands: every run opens as the last one was left, in any project and
whatever its port (a browser keeps a page's own storage per port, and each
run takes a new one).

### Settings and keyboard shortcuts

Code Arena reads VS Code's own files, under VS Code's names, so a person's
VS Code settings mean the same here:

- **settings.json**: the person's, in code-arena's config folder (beside
  `config.json`; **Preferences: Open User Settings (JSON)**), for every
  project; the project's `.vscode/settings.json` (**Preferences: Open
  Workspace Settings (JSON)**) over it; and, over both, a language's own
  (`"[python]": {"editor.tabSize": 4}`, in either; VS Code's ids for what
  Monaco reads as one language are that language: `[typescriptreact]` is a
  .tsx file's, as `[typescript]` is, which wins over it, and `[shellscript]`,
  `[javascriptreact]` and `[jsonc]` are `shell`, `javascript` and `json`, in
  `files.associations` too). Comments and trailing
  commas are allowed; the editor checks the file against what Code Arena
  reads, with each setting's description. A value of the wrong kind is not
  taken. The person's own settings (the terminal's keys) are not taken from a
  project's file.
- What they set: the editor (font, size, line height, ligatures, tab size and
  spaces, detecting the file's indentation, word wrap, rulers, line numbers,
  whitespace, the cursor, the minimap, sticky scroll, bracket colours and
  guides, folding, suggestions, inline completion, hovers…), saving
  (`files.autoSave` after a delay, when the focus or the window leaves;
  `files.trimTrailingWhitespace`, `files.insertFinalNewline`,
  `files.trimFinalNewlines`, steps Undo takes back, never for a save after a
  delay; `files.associations`; `files.eol`, the line ending of a new, empty
  file), compares (`diffEditor.*`), the terminals (font, size, line height,
  cursor, scrollback, applied at once to those open; the keys they let
  through), the colour theme (`workbench.colorTheme`, or
  `window.autoDetectColorScheme` with the preferred light and dark ones:
  **Ctrl+K Ctrl+Alt+T**), the file icon theme
  (`workbench.iconTheme`) and Code Arena's code completion
  (`codeArena.inlineCompletion.enabled`, the status bar's **Complete**: the
  person's own, which a project's settings cannot turn on, since what is typed
  is sent to the model). They apply as the file is saved; a file's indentation
  chosen in the status bar stays until the indentation settings themselves
  change. Where Code Arena's
  default is not VS Code's, the description says so. Under the person's
  settings are the enabled extensions' `configurationDefaults`
  ([Extensions](#extensions)).
- **keybindings.json**: the person's, beside it (**Preferences: Open Keyboard
  Shortcuts**, **Ctrl+K Ctrl+S**): VS Code's rules, `{"key": "ctrl+shift+e",
  "command": "workbench.action.togglePanel", "when": "editorIsOpen"}`, over
  VS Code's own keys for the commands Code Arena has (by VS Code's command
  ids); the last rule that matches wins, `"-command"` takes a default key
  away, a key may be two chords (`ctrl+k ctrl+t`) and `when` reads VS Code's
  context keys (`editorIsOpen`, `inputFocus`, `terminalFocus`,
  `sideBarVisible`, `resourceLangId == python`…, in the editor too). The
  editor has the same keys, its own (Monaco's: Ctrl+D, Alt+↑, Ctrl+K Ctrl+C…,
  listed with the defaults in the Keyboard Shortcuts editor and the palette)
  under theirs, and a
  rule may rebind one of the editor's commands too (`editor.action.…`,
  `cursorUp`), or take its key away (`"-editor.action.deleteLines"`). A
  `"-command"` VS Code wrote keeps the `when` of VS Code's default, which may
  read context keys Code Arena does not have (`taskCommandsRegistered`): with
  a key, it takes that key of the command away whatever its `when` here. A
  rule of a command Code Arena does not have is left out (the Keyboard
  Shortcuts editor lists it), so its key keeps doing what it did.

**Ctrl+,** opens the **Settings editor**, as VS Code's: every setting Code
Arena reads by category, with its description and a box, a list or a tick to
set it; search by words, or **@modified**, **@lang:python** (that language's
own) and **@id:editor.tabSize**; **User** writes the person's settings.json,
**Workspace** the project's (made when it is not there yet), each showing its
own file's values (the default where it sets none; "Also modified in" says
the other file sets it too); each change is written at once, the file's
comments kept (commented-out settings too), and an open tab of the file shows
it; a setting's menu resets it or copies its id or its JSON; settings of the
file Code Arena does not use are
listed apart (they stay in it); **From VS Code** brings over VS Code's own
settings.json from this computer (what the person set already stays).
**Ctrl+K Ctrl+S** opens the **Keyboard Shortcuts editor**: every command with
its keys, when they apply and whose rule each is (Default, User, Extension),
searched by words or by pressing the keys (**Record Keys**); double-click a key
(or its menu) to press new ones (it says how many other commands have them),
and the menu removes, resets or changes when one applies, written into the
person's keybindings.json as VS Code writes it (a default taken away is a
`-command` rule), which an open tab of it shows at once. The `{}` button of
either opens its JSON file.

In a terminal the keys go to the shell, but those of the commands that skip
it (the palette, quick open, the panels and views, the debugger's), and the
first key of a chord (Ctrl+K: `terminal.integrated.allowChords` false gives it
to the shell); `terminal.integrated.commandsToSkipShell` adds one
(`"-command"` gives its key back). On macOS the keys are ⌘: Ctrl+S, Ctrl+P and
the others stay the terminal's and the editor's there. A browser tab keeps
Ctrl+W, Ctrl+T, Ctrl+N, Ctrl+Tab, Ctrl+F4 and Ctrl+PageUp/PageDown for itself,
as the second key of a chord too: their commands have another key, which the
menus show (Ctrl+Alt+W closes the tab, Ctrl+K Ctrl+Alt+W all of them,
Ctrl+Alt+Shift+T reopens one, Ctrl+Alt+PageUp/PageDown move between them,
Ctrl+K Ctrl+Alt+T the colour theme, Ctrl+K Ctrl+Alt+N unstages); in Code
Arena's own window VS Code's keys work too. F5, Ctrl+F5, Shift+F5 and Ctrl+S
never reach the browser (no reload, no Save Page As), also when their command
does nothing at that moment.

| keys | |
|---|---|
| Ctrl+Shift+P, F1 | run a command by name |
| Ctrl+P | open a file by name (`:12` goes to a line); Ctrl+G goes to a line |
| Ctrl+S, Ctrl+K S | save the file, save them all |
| Ctrl+Alt+W, Ctrl+K Ctrl+Alt+W, Ctrl+K U | close the tab, all the tabs, the saved ones |
| Ctrl+Alt+Shift+T | reopen the tab closed last |
| Alt+1 … Alt+9, Ctrl+Alt+PageUp/PageDown | a tab by its place, the next or the one before |
| Ctrl+Alt+- , Ctrl+Shift+- (Alt+← and Alt+→ on Windows) | back and forward through the tabs shown |
| Ctrl+B, Ctrl+J, Ctrl+Alt+B | the side bar, the panel, the chat |
| Ctrl+Shift+E, Ctrl+Shift+F, Ctrl+Shift+H, Ctrl+Shift+G, Ctrl+Shift+M, Ctrl+Shift+X | Explorer, search, replace, source control, problems, extensions |
| Ctrl+`, Ctrl+Shift+` | show or hide the terminals, a new one |
| Ctrl+, , Ctrl+K Ctrl+S, Ctrl+K Ctrl+Alt+T | settings, keyboard shortcuts, colour theme |
| Ctrl+K M, Alt+Z, Ctrl+K P | the language mode, word wrap, copy the file's path |
| Ctrl+L | in the editor: the lines chosen (or the cursor's) go with the next message |
| Ctrl+Shift+' | in the chat: Reply, quoting the words selected in an answer (`codeArena.chat.quoteSelection`) |

Everything the page needs (Monaco and its language workers, xterm.js, the
fonts) is built into the program; nothing is fetched from anywhere else.
Monaco loads with the first file opened, xterm.js with the first terminal.

### Formatting

**Shift+Alt+F** (Ctrl+Shift+I on Linux) formats the file, **Ctrl+K Ctrl+F** the
lines chosen, **Format Document With…** picks the formatter (and **Configure
Default Formatter…** sets `editor.defaultFormatter` for the language; VS
Code's extension ids, `esbenp.prettier-vscode`, mean the same, and its
built-in formatters, `vscode.typescript-language-features`,
`vscode.json-language-features`, `vscode.css-language-features` and
`vscode.html-language-features`, mean the editor's own). The status
bar names the file's formatter. `editor.formatOnSave` formats as the file is
saved, within `codeArena.formatOnSaveTimeout` (past it, or failing, the file
is saved as it is, and a message says why); **Save without Formatting**
(Ctrl+K Ctrl+Shift+S) skips it; `editor.formatOnPaste` formats what is pasted.
VS Code's `prettier.*` settings (`printWidth`, `semi`, `singleQuote`…) go to
Prettier as flags; a project's own Prettier config wins over them. A file
with no formatter of the project's (one that is not installed does not count;
one that waits on the folder's trust does) keeps the editor's own (TypeScript,
JSON, CSS, HTML), and so do a file whose `editor.defaultFormatter` names it
and your own settings files: Format Document, Format Selection, a paste and a
save format them with it. With no `editor.defaultFormatter`, a formatter that
waits on the folder's trust is the file's formatter while none other can run.

Code Arena formats the editor's text, saved or not, with the project's own
tools, which code-arena runs:

- **Prettier:** the nearest one in `node_modules`, looking from the file's
  folder up to the folder Code Arena works in; without one, a Prettier
  installed for every project (on PATH, or in npm's global folder). It runs as
  `node` and its script.
- **Python:** `ruff format`, then `black`, from the project's `.venv` or
  `venv`, else from PATH (black first where `pyproject.toml` configures black
  and not ruff).
- **Go and Rust:** `gofmt`, and `rustfmt` with the edition of the nearest
  `Cargo.toml`.
- **C, C++, Objective-C, Java and Protobuf:** `clang-format`.
- **C# and VB:** `dotnet format whitespace`, on a copy in a temporary folder
  that holds the `.editorconfig` files from the root down (whole files only;
  the file itself is never touched).

Prettier, ruff, black and clang-format also format a selection. Formatters are
found by their files, never by running them, and what was found is kept ten
seconds per folder.

A formatter that lives in the folder (a project's `node_modules` or `.venv`,
or a config formatter whose program is there) runs only once you **trust the
folder**, as VS Code asks: **Format Document** (or **Format Document With…**)
asks the first time (**Manage Workspace Trust** asks again; a folder you kept
restricted is not asked about again); a save never asks: it saves as typed,
and says so once. The choice is kept on this computer, never in the project. Until then the global one runs instead, and a global
Prettier runs without the project's config (a Prettier config can be
JavaScript or load plugins). Until the folder is trusted, rustfmt runs outside
it (rustup would otherwise use the toolchain the folder's
`rust-toolchain.toml` names), with the nearest `rustfmt.toml`; `dotnet format`
always runs on its copy, so the folder's `global.json` cannot choose the SDK.
A file's name reaches each tool as one argument (`--stdin-filepath=…`), so a
file named like a flag stays a name. Where the system has a sandbox
(bubblewrap, sandbox-exec), each formatter runs in it: it can read the folder
but not write it, has a `/tmp` of its own and no network, cannot see what
holds secrets, and never gets your API key. Without a sandbox (Windows, or
`"sandbox": "off"`), formatters run as you, as Problems does. Four run at once
at most, ten seconds each (thirty for dotnet format); one that runs longer is
stopped, with anything it started.

`"formatters"` in `config.json` adds your own, or replaces a built-in when it
uses the built-in's name:

```json
"formatters": {
  "stylua": {"command": ["stylua", "--indent-width", "${tabSize}", "--stdin-filepath", "${file}", "-"], "languages": ["lua"]},
  "biome": {"command": ["biome", "format", "--stdin-file-path=${relativeFile}"], "languages": ["typescript", "*.jsonc"], "timeout": 5}
}
```

The text goes to the command's input and the formatted text comes from its
output. `languages` are Monaco's language ids or file patterns (`*.lua`); an
entry named after a built-in (`prettier`, `ruff`, `black`, `gofmt`, `rustfmt`,
`clang-format`, `dotnet-format`) takes that built-in's languages when it lists
none. `${file}`, `${relativeFile}`, `${fileDirname}`, `${workspaceFolder}`,
`${tabSize}` and `${insertSpaces}` are filled in; with `"range": true` it
formats a selection too (`${rangeStart}` and `${rangeEnd}`, offsets, and
`${startLine}` and `${endLine}`); `"timeout"` is in seconds, 1 to 600. Only
`config.json` names commands: a project cannot add one. Your own formatters
come first, so the first one that can run is the default.

### Run and Debug

**Run and Debug** in the activity bar (**Ctrl+Shift+D**) is VS Code's: a run
configuration to start, **Start Debugging** (**F5**) and **Run** (Run Without
Debugging, **Ctrl+F5**: the program in a terminal of its own, without the
debugger), and **Build**, Debug or Release. While a session runs, it shows the
frame chosen's **Variables** (each opens to its members; double-click one to
change it), **Watch** expressions (evaluated at each stop, kept for the
folder), the **Call Stack** (a frame opens its file at its line, and is the one
the variables and watches are of) and the **Breakpoints** (each on or off,
removed, all removed; the exceptions to stop at, as the adapter offers them).
A toolbar over the editor continues (**F5**) or pauses (**F6**), steps over
(**F10**), into (**F11**) and out (**Shift+F11**), restarts
(**Ctrl+Shift+F5**) and stops (**Shift+F5**). The panel's **Debug Console**
(**Ctrl+Shift+Y**) shows what the program and the debugger print, and its box
evaluates an expression in the frame chosen while the program is stopped (what
it gave stays in turn with the output, and **↑** goes back through what was
typed).

In the editor, a click beside a line number (or **F9**) sets or clears a
breakpoint; the right-click menu adds a **Conditional Breakpoint…** (it breaks
when an expression is true) or a **Logpoint…** (it prints a message, `{x}`
replaced by `x`'s value, and goes on), **Run to Cursor** (a breakpoint there
for this run only, then continue: it goes at the next stop or when the session
ends, and one of the person's turned off on that line comes back as it was)
and **Add to Watch**. While stopped, the
variables' values show at the ends of the lines above the one stopped at that
name them (`debug.inlineValues`). A breakpoint no
adapter has bound yet is hollow, one turned off grey; breakpoints follow their
lines as the file is edited, and are kept for the folder on this computer
(with the watches and the exceptions chosen). The line a session stopped at is
yellow (the frame chosen, green), and the pointer over an expression shows its
value. As the program stops, the file opens at its line and Run and Debug
shows (`debug.openDebug`); a program that ends by itself lets its debugger go (its
output stays in the Debug Console until the next start); the Debug Console
opens as the first session starts (`debug.internalConsoleOptions`, or the
configuration's own `internalConsoleOptions`), except for a program that runs
in a terminal of its own, which stays in front; files with unsaved changes are saved first
(`debug.saveBeforeStart`); `debug.toolBarLocation` hides the toolbar.

**Configurations.** `.vscode/launch.json` gives them, as VS Code reads it: a
`windows`, `linux` or `osx` part merged over each, `envFile` read under `env`,
`preLaunchTask` (run first and waited for; one that fails stops the start) and
`postDebugTask`, and `inputs`. The project's files give more: each .NET
program (its build first, then the program MSBuild says it built, Debug or
Release), npm's `start` and `dev` scripts, the Python file open, Go's package,
Cargo's programs, CMake's executables and the programs a Makefile built. The
**gear** opens launch.json (made when it is not there); **Add Configuration…**
adds one the project gives, or a type's template. **Run** turns each type into
a plain program: `coreclr` (a `.dll` through `dotnet`), `node` and `pwa-node`,
`python` and `debugpy`, `go` (`go run`, or the program with `"mode":
"exec"`), `cppdbg`, `lldb`, `lldb-dap` and `gdb` (the program; CodeLLDB's
`cargo` as `cargo run`); attach configurations need their debugger. Variables
work as in VS Code: `${workspaceFolder}`, `${file}`, `${relativeFile}`,
`${fileDirname}`, `${lineNumber}`, `${selectedText}`, `${env:NAME}` (never
`ARENA_API_KEY`), `${config:key}`, `${input:id}` (promptString and pickString
are asked first), `${codeArena.buildConfiguration}` and the others; another
variable, or another `${command:…}`, is refused naming it (write a shell's
own variables as `$NAME`). An attach configuration's `${command:pickProcess}`
asks which of the person's own processes to attach to (by its name, id or
command line; on Linux, `ptrace_scope` may need the debugger to be the
process's parent or root: `sudo sysctl kernel.yama.ptrace_scope=0` allows it
until the next boot). **Compounds** (launch.json's `"compounds"`) are chosen
as a configuration is: one starts its `preLaunchTask` first and waits for it
(done, or ready for a background task), then all its configurations together;
with `"stopAll": true`, stopping one of its sessions stops the others.

**Tasks.** `.vscode/tasks.json` (version 2.0.0) as VS Code reads it: `shell`
and `process` tasks (and `npm` and `typescript` ones) with `options`, `group`
(`build`, `test`, `isDefault`), `dependsOn` (at once, or in order with
`"dependsOrder": "sequence"`), a system's part, `isBackground` (ready when its
matcher's `background.endsPattern` shows) and `problemMatcher` (`$msCompile`,
`$tsc`, `$tsc-watch`, `$gcc`, `$eslint-compact`, `$go`, or a pattern of one
line: what they find joins Problems). The project's files give more: `dotnet:
build` and `test` (with `-c Debug` or `-c Release`), `npm: <script>`, `cargo:
build` and `test`, `go: build` and `test`, `make` targets, `cmake: configure`,
`build` and `test`, `python: pytest`. **Tasks: Run Task**, **Run Build Task**
(**Ctrl+Shift+B**), **Run Test Task** and **Terminate Task** run and stop
them, each in a terminal of its own named after it. **Debug or Release** is
the project's own choice (**Build** in Run and Debug, or **Select the Build
Configuration**): the Problems check, the detected tasks and configurations
build and run it.

**Trust.** tasks.json's tasks, launch.json's configurations and every debug
session run only in a folder you trust; until then, starting one lists the
commands it would run and asks first. Detected tasks and configurations show
their command and need no trust to run without the debugger.

A program an adapter asks to run in a terminal (debugpy's and js-debug's
`"console": "integratedTerminal"`) runs in a terminal of its own, shown in the
panel.

**Debuggers.** The debugger is the page's own: it speaks the Debug Adapter
Protocol to a debug adapter through code-arena, as VS Code does. code-arena
starts the adapter, passes messages both ways and keeps what a page that comes
back needs (a reloaded page attaches again). Nothing is downloaded: a missing
adapter says how to get it.

| Adapter | Languages | launch.json types | How to get it |
|---|---|---|---|
| gdb 14 or later | C, C++, Rust | `cppdbg`, `gdb` | the system's packages (`apt install gdb`) |
| lldb-dap (or lldb-vscode) | C, C++, Rust | `cppdbg` with `"MIMode": "lldb"` (the default on macOS), `lldb`, `lldb-dap` | LLVM, Xcode |
| netcoredbg | .NET | `coreclr` | Samsung's releases on GitHub |
| debugpy | Python | `python`, `debugpy` | `pip install debugpy` |
| Delve | Go | `go` | `go install github.com/go-delve/delve/cmd/dlv@latest` |
| js-debug | Node.js | `node`, `pwa-node` | the `js-debug-dap` release of vscode-js-debug |

Each adapter is looked for in `config.json`'s `"debugAdapters"`, then the data
folder (`adapters/ID/current`), then the folders on `PATH` (full paths only),
then where its usual installs put it:

```json
"debugAdapters": {
  "gdb": {"command": ["/opt/gdb/bin/gdb", "-i", "dap"]},
  "dlv": {"command": ["dlv", "dap", "--listen=127.0.0.1:${port}"]},
  "netcoredbg": {"port": 4711}
}
```

`command`'s program is a full path or a name on PATH; `${port}` is where it
takes a port to listen on (code-arena picks a free one); `port` names an
adapter already listening on 127.0.0.1, as a configuration's `"debugServer":
N` does. `cppvsdbg` (Microsoft's debugger, licensed for its own products
only), browser types and `java` are refused, each saying why. A session runs
in the working directory, without `ARENA_API_KEY` and without a sandbox (as
the terminals do); eight run at once at most; stopping one sends `terminate`,
then `disconnect`, and kills what is left 3 s later (an attach session is
disconnected, the program let go). A file outside the folder is read only when
the session's adapter named it, and never code-arena's own config or data.

The browser keeps a few function keys for itself in a tab (F11 makes the
window full screen in some): **Step Into** is also in the toolbar.

### Extensions

**Extensions** in the activity bar (**Ctrl+Shift+X**) takes VS Code's
extensions for what they declare in their `package.json`, never for their
code: Code Arena has no extension host, and no extension's code runs. The
view lists:

- **Installed**: each with what it brings; its switch turns it off (its
  contributions stop applying), the bin uninstalls it, and **Not used here**
  says what it has that Code Arena does not use, and why.
- **Recommended**: the project's `.vscode/extensions.json`
  (`recommendations`), each with where it stands here: **Installed**; **Built
  in** (Code Arena does its work itself: `esbenp.prettier-vscode`, ruff and
  black are [formatting](#formatting), `ms-vscode.cpptools`,
  `ms-python.debugpy`, `golang.go` and the others are [Run and
  Debug](#run-and-debug)'s adapters); **Not supported** (it is its code:
  ESLint, GitLens, Copilot, Pylance, remote development, with what to do
  instead, such as ESLint as the project's check in Problems); **On this
  computer** (VS Code has it here: **Import**); **Not here** (install its
  .vsix).
- **VS Code on this computer**: the extensions VS Code installed
  (`~/.vscode/extensions`), VSCodium's (`~/.vscode-oss/extensions`) and
  Insiders' (`~/.vscode-insiders/extensions`; `%USERPROFILE%\.vscode\extensions`
  on Windows), the newest copy of each, and whether it has anything Code Arena
  uses. **Import** copies one (VS Code's own copy is left as it is).
- **Install from VSIX…** (also **Extensions: Install from VSIX…** in the
  palette): a .vsix you downloaded (an extension's page on Open VSX or the
  Marketplace has one). Code Arena itself downloads nothing.

What it uses of an extension:

| contribution | in Code Arena |
|---|---|
| `themes` (colour themes, JSON) | offered by **Color Theme** (Ctrl+K Ctrl+Alt+T) and `workbench.colorTheme` (by its `id`, else its `label`); going down the list shows each theme at once, and Escape goes back. Its `include` is read. Its `tokenColors` colour the editor by scope: the editor's own highlighting names its tokens as TextMate's broadest scopes (comment, string, keyword…), and the common narrower ones (`constant.numeric`, `entity.name.type`, `entity.name.tag`, `storage.type`…) are given its names too. Its `colors` are the editor's, the terminal's (`terminal.foreground`, `terminal.ansi*`), and the page's for about twenty of the workbench's (`editor.background`, `sideBar.background`, `button.background`, `list.hoverBackground`, `focusBorder`, `panel.border`…); its `uiTheme` makes the page light or dark |
| `iconThemes` | offered by **Preferences: File Icon Theme** and `workbench.iconTheme`. A file's icon is by its name, then its extension (the longest first: `test.ts` before `ts`), then its language, else the default; a folder's by its name, open or closed; a light theme's own choices first. Images, and characters of the theme's fonts |
| `snippets` | suggestions (Ctrl+Space, and as you type) in the files of their language, in VS Code's snippet syntax (`$1`, `${2:name}`, `${3\|a,b\|}`, `$0`) |
| `languages` | a language for files by their extensions and names (the status bar's language, `files.associations`); for a language the editor does not have, its `language-configuration.json`: comments (Ctrl+/), brackets, auto-closing and surrounding pairs, folding markers, the word pattern, indentation and Enter rules |
| `grammars` | kept, not used yet: the editor's own highlighting is used (TextMate grammars come later) |
| `keybindings` | for the commands Code Arena has, by VS Code's ids, between its defaults and your keybindings.json (**Extension** in the Keyboard Shortcuts editor); keys for the extension's own commands are not (they need its code) |
| `configurationDefaults` | a layer under your settings: Code Arena's defaults, then the extensions', then your settings.json, then the project's; a language's own (`"[toml]": {…}`) over them all, as in VS Code |

Everything else is left out and said so: its code (`main`, `browser`), its
commands, menus and views, its own settings (`configuration`), debuggers,
language servers, `jsonValidation`, product icon themes and `.tmTheme`
colour themes. So ESLint, GitLens, debuggers and language features need
Code Arena's own way: ESLint as the project's check (Problems), Source
control and its compares, Run and Debug's adapters, Prettier and the others
as formatters, and the language servers themselves (below) for completions,
definitions and errors. An extension with nothing Code Arena uses is refused. A
project's own `.vscode/*.code-snippets` and your own snippets files are not
read yet: only an extension's.

Extensions are kept in code-arena's data folder, in
`extensions/<publisher.name>-<version>/` with only the files their
contributions name (the themes and what they include, the icons and fonts,
the snippets, the language configurations, the grammars), and listed in
`extensions/installed.json` with whether each is on. A new version replaces
the one before (on or off as it was). A .vsix is taken up to 50 MB, a file
in it up to 5 MB, 50 MB and 10,000 files kept per extension; a .vsix with a
name that leads out of it (`..`, `/`, `C:`) or a link in it is refused whole,
and a path in its `package.json` that leads out of the extension is left out.
An extension's files are served to the page only when its contributions name
them, never as a page of its own (an SVG runs no script). Monaco cannot take a
language back: one an extension added stays until the page reloads.
### Language servers

The editor speaks the Language Server Protocol, as VS Code does: open a file
and the language server for its language starts, and the editor gets its
completions (with their documentation and auto-imports), hovers, signature
help, **Go to Definition** (**F12**; into another file of the folder, it opens
there), declaration, type definition and implementation, **Go to
References** (**Shift+F12**), the other uses of a name highlighted, the
symbols (**Ctrl+Shift+O**, the outline), **Rename Symbol** (**F2**, in every
file it changes: the files not open yet open behind the one shown, unsaved),
code actions (the light bulb: quick fixes, organize imports, refactorings) and
formatting. Its errors and warnings are marked in the files and listed under
**Problems** (below the check's), with the server's name. While a server runs,
the editor's own services for its languages (TypeScript's and JavaScript's,
and JSON's, CSS's and HTML's when their servers are on) are turned off, so
nothing is answered twice; they come back when it stops. The project's own
formatter (Prettier, ruff, gofmt…: above) still formats its files; a language
server formats the languages that have none.

| Server | Languages | How to get it |
|---|---|---|
| typescript-language-server | TypeScript, JavaScript | `npm install -g typescript-language-server typescript` |
| Pyright, basedpyright, Python LSP Server (pylsp) | Python | `npm install -g pyright`, `pip install basedpyright`, `pip install python-lsp-server` |
| gopls | Go | `go install golang.org/x/tools/gopls@latest` |
| rust-analyzer | Rust | `rustup component add rust-analyzer` |
| clangd | C, C++ | the system's packages (`apt install clangd`), LLVM |
| csharp-ls | C# | `dotnet tool install --global csharp-ls` |
| vscode-json-, vscode-css- and vscode-html-language-server | JSON, CSS (SCSS, Less), HTML | `npm install -g vscode-langservers-extracted` |
| yaml-language-server | YAML | `npm install -g yaml-language-server` |
| bash-language-server | shell scripts | `npm install -g bash-language-server` |

A server is looked for in `config.json`'s `"languageServers"`, then in the
folders on `PATH` (full paths only: `.` in PATH would find the project's own
program), then where its usual installs put it (`~/go/bin`, `~/.cargo/bin`,
`~/.dotnet/tools`, `clangd-18` and the like); Python's first in the project's
`.venv` or `venv` (or `$VIRTUAL_ENV`). For a language, the first one found
that the settings leave on starts: `codeArena.languageServers.enabled` turns
them all off, `codeArena.languageServers.ID.enabled` one of them (the JSON,
CSS and HTML servers are off by default: the editor's own services are the
same libraries; turned on, they replace them, and while the JSON server runs
your own settings.json and keybindings.json lose the editor's checks and
suggestions). TypeScript's
uses the project's own TypeScript (`node_modules/typescript`) when it has one.
`config.json` adds your own server, or changes a built-in one (by its id):

```json
"languageServers": {
  "lua": {"command": ["lua-language-server"], "languages": ["lua"], "settings": {"Lua": {"diagnostics": {"globals": ["vim"]}}}},
  "pyright": {"command": ["/opt/node/bin/pyright-langserver", "--stdio"]},
  "clangd": {"command": ["clangd", "--background-index", "--compile-commands-dir=build"], "env": {"CLANGD_FLAGS": null}},
  "gopls": {"disabled": true}
}
```

`command` is the program (a full path or a name on PATH) and its arguments;
`languages` are Monaco's language ids (a built-in's own when not given);
`initializationOptions` go to the server's `initialize`; `settings` answer
the server's `workspace/configuration` (over your settings.json, where VS
Code's settings for the server work too: `"python.analysis.typeCheckingMode"`,
`"gopls": {…}`, `"rust-analyzer.cargo.features"`); `env` adds to its
environment (`null` takes a variable away); `"disabled": true` turns a
built-in off. Your own come before the built-in ones for their languages.

**Trust.** A language server runs the folder's code (a tsserver plugin, a
build script, `go list`, a Cargo build script), so none starts until you
trust the folder: until then the status bar says so, quietly (no prompt for
each file); **Manage Workspace Trust** trusts it, and the servers start.
Taking the trust back stops them.

**Nothing is fetched.** Each runs in the folder, without `ARENA_API_KEY`, in
the agent's sandbox where the system has one (it can write the folder and the
build tools' caches, cannot see what holds secrets) and without the network.
The catalog's servers are also told not to fetch, for a system without a
sandbox: no type packages for TypeScript, `GOPROXY=off` for gopls,
`CARGO_NET_OFFLINE=true` for rust-analyzer, no schema store for YAML, and no
schemas from the web for JSON. Nothing is installed: a missing server says how
to get it.

**The status bar** names the file's server and its state: starting, ready
(and what it is doing: indexing…), failed, off until the folder is trusted,
in use by another page, not installed. Clicking it offers **Restart Language
Server** and **Show Language Server Log** (both in the palette too). The
panel's **Output** tab (**Toggle Output**) has a channel per server: when it
started, its messages, why it ended, and the last 200 lines it wrote to
stderr. A server that ends by itself starts again (three times in three
minutes at most).

Six servers run at once at most, one of each; a server serves one page: a
second page of the folder that opens a file of its language takes it over
(the first says so, and **Restart** takes it back). A page that closes or
reloads stops its servers (shutdown and exit, killed 2 s later); the page
reloaded starts them again. Definitions in a file outside the folder (a
library's, the standard library's) are not opened: the editor opens the
folder's files only.

### Ask about code from the editor

Lines of a file go to the agent from the editor itself:

- In the chat's box, **@** offers the folder's files, best first: ↑ and ↓
  choose, Enter or Tab puts `@path` in the box. A whole path (`@/`, `@~/`,
  `@C:/`) offers what is there instead, outside the folder: a folder chosen
  goes into it, a file chosen goes with the message. A file or folder outside
  named whole goes with the message too when it is there, and the agent's
  tools may use it from then on in this run (it is the person's to give); a
  relative path outside stays as typed. Above the box, a dashed chip
  offers the lines the editor shows (those chosen, or the cursor's): a click
  sends them with the message.
- **Add to chat** (Ctrl+L, or the editor's right-click menu): the lines chosen,
  or the cursor's line, wait above the chat's box as a chip (× leaves them out)
  and go with the next message, as they are when it goes; the box takes the
  focus. A message queued while an answer is written takes them as they are
  when it is queued. A file with unsaved
  changes sends the editor's text, marked unsaved. The Explorer's right-click
  **Add to chat** names the whole file in the box (`@path`).
- **Explain this**, **Fix this** and **Complete this** (the right-click menu,
  and F1): asked at once about those lines (queued while an answer is
  written). Fix says the problems the editor marks there; Complete asks for
  what is missing (a function's body, a TODO) to be written with `edit_file`.
- On a line with a problem marked, the light bulb offers **Fix with Code
  Arena**.

The model gets them numbered, after the message, as `<selection path lines>`
(with `unsaved="true"` for the editor's text); at most eight a message.

### Reply to part of an answer

Words selected in a finished answer of the chat get the web chat's toolbar
beside them ([Chat](chat.md#what-it-does)): **Reply** puts them above the box
as a quote (× or Backspace at the start of an empty box takes it away), and
the next message goes with it; **Ask about this** asks about them at once
(queued while an answer is written), leaving what is in the box and the
editor's lines above it as they are; **Copy** copies them. The command **Chat:
Reply quoting the selection** (`codeArena.chat.quoteSelection`, Ctrl+Shift+' by
default, changed in `keybindings.json`) does what Reply does; F1 lists it only
while words are selected in an answer. **Quote part of this answer…** under an
answer opens it in a box to select from by keyboard. The thread shows a
question's quote above it, with a way to the answer it came from. The model
reads the quote before the message, and the editor's lines after it.

### Source control

**Source control** in the activity bar (Ctrl+Shift+G, or the branch in the
status bar) is the folder's git as you work it, beside the agent's own turn
commits: what changed since the last commit, **Staged** (in the next commit)
and **Changes** (not yet). A file opens as a diff tab, as the last commit has
it and as it is now; **+** stages it, **−** unstages it, **↺** puts it back as
the index has it (a file git does not know is deleted; it asks first). Write
the message and **Commit** (Ctrl+Enter): what is staged, or all of it when
nothing is. Your own git name and email commit. Below: the branch, to switch
to another, how far it is ahead of and behind its upstream, and the last
commits. A repository bigger than the folder shows the folder's files only.

### Problems

**Problems** in the activity bar runs the project's own check and lists the
errors and warnings it printed, by file: **Run check** runs it (in the folder,
as your terminals run, with no sandbox; five minutes at most). Each problem
opens its file at its line, where the editor marks it too (as the editor's
own problems, with **Fix with Code Arena** in the light bulb), and **Fix**
beside it asks the agent to fix it. The status bar counts the errors and
warnings; the activity bar the errors. Below the check's, **Language
servers** lists what the language servers running found (in the files open,
and in others some servers check too), each with the server's name.

The check is `"checkCommand"` in `config.json`, else the first found by the
project's files: `dotnet build` (a solution or project), `tsc --noEmit`
(tsconfig.json), `npm run lint` (a `lint` script), `cargo check`, `go vet`,
`ruff check`. Its output is read in the common forms: `path(line,col): error
CODE: message` (MSBuild, tsc) and `path:line:col: error: message` (gcc, clang,
go, cargo, ruff, eslint's unix format); only the folder's files count. A line
with no word for how bad it is counts as an error (go vet, Go's compiler),
but a linter's rule code (ruff's `F401`) or ESLint's `[Warning/…]` as a
warning. The worst 2,000 are kept; a check that leaves something running in
the background is not waited for.

Arena's **security review** of the folder's repository joins them, read with
your key as the view opens (and again with **Read the security review
again**): the latest review of origin's project at the folder's branch (or
of its default branch, when the branch has none of its own) that you may
read. Its open findings are problems, critical and high ones errors, medium
warnings, low notes, each with its CWE (or the scanner's rule); the doubted
ones are left out. **Fix** beside one gives the agent what Fix it would ask:
the weakness, where outside data comes in, its way to the sink, the
references, and a test that shows it first. The line above them says which
review it is, with a link to it in Arena.


### Review

**Review** in the activity bar has a reviewer look at **the changes not yet
committed**, or at **this branch over** a base you name (main, origin/main, a
commit), as `/review` does in the terminal: a fresh agent with the reading
tools only (files, search, git, and Arena's and Argus's tools that change
nothing) reads the changes and the code around them, tries to refute each
finding before it keeps it, and changes nothing. Its steps show as it goes;
**Stop the review** ends it (20 minutes at most).

Then the verdict (looks good, comments, changes needed), a summary, and the
findings by file, worst first (critical, major, minor, nit). Each opens its
file at its line, where the editor marks it (as **Problems** list it while it
is open: critical and major as errors, minor as warnings). Unfolded, it says
why it matters and the fix. **Apply fix** makes the fix in the editor when
the reviewer gave the exact text to replace and it is in the file once
(undo takes it back; saving is yours); **Ask the agent** hands the finding
to the chat with its lines; **Dismiss** leaves it. A finding on a file that
is not in the folder is left out. The last review stays until the next.
### Code completion

As you type in the editor, Code Arena asks the model for what comes next at
the cursor (a pause of 300 ms first) and shows it as grey text: **Tab** takes
it, **Esc** or typing on leaves it. It sends the code before the cursor and
after it (6,000 and 2,000 characters at most) with the file's path, as the
model's fill-in-the-middle prompt, in a request of its own: never part of the
conversation. Its tokens count to the session's, and to your API credit like
the agent's. It waits while the agent works (the key's requests at once are
the agent's then), and after the key's limit for a minute it rests half a
minute. **Complete** in the status bar turns it off or on (kept for every
run); `"completion": false` in `config.json` turns it off for good.

It uses the session's model, or `"completionModel"`; a small coder model kept
loaded answers fastest. `"completionTemplate"` names the model family's
fill-in-the-middle tokens: `qwen` (the default), `deepseek`, `codellama` or
`starcoder`.

### Files in answers

In the IDE the agent cites code as `path:line` or `path:start-end`, and a file
of the folder it cites opens in the editor at those lines (selected), with a
click or Enter: in backticks, in its text (`src/app.ts`, `src/app.ts:12`,
`src/app.ts#L12-L30`, `Program.cs(44,13)`), or as a relative link. A path that
is no file of the folder stays text, and a link to it goes nowhere. Under a
tool's card, `read_file`, `edit_file` and `write_file` have a chip that opens
their file (where it read from, or the lines changed: `edit_file` says them,
"Edited src/app.ts (lines 12-14)."); an edit's diff opens it from its path,
and at any line from that line's number. The paths in a tool's output
(`grep`'s `path:line:` rows, `glob`'s list) open the same way. A code block
named for a file of the folder (```` ```ts:src/app.ts ````) opens it from its
header.

It is the same agent as in the terminal, not a copy: the same tools, modes,
sessions (the same files: `code-arena chat --resume` opens a session started
in the browser, and the other way round), ARENA.md and MCP servers. A page
reloaded during a turn picks the turn up from its start. One turn runs at a
time; switching sessions waits for it.

### Terminals

Each terminal is a shell on a real pseudo-terminal: openpty and posix_spawn
on Linux; on macOS the shell starts through `code-arena --pty-helper`, which
makes a session, takes the terminal as its controlling one and becomes the
shell; ConPTY on Windows (10 1809 or later). Job control, Ctrl+C, colours and
full-screen programs work, and a resize reaches the program.

The shell is `"terminalShell"` in `config.json` (a path, or a name found on
`PATH`), else `$SHELL` (a login shell on macOS, as Terminal.app starts it),
else bash or sh; on Windows PowerShell 7, Windows PowerShell, else `cmd.exe`.
It starts in the working directory with code-arena's environment, without
`ARENA_API_KEY`, with `TERM=xterm-256color`, `COLORTERM=truecolor` and
`TERM_PROGRAM=code-arena`, every signal at its default and none blocked. Ten
run at once at most. A page that comes back, or a second tab, sees the last
256 KB of each one's output. Closing one sends what runs in it SIGHUP, and
SIGKILL 3 s later to what is left, whether the shell ended or not: the shell,
the job in the foreground and, on Linux, every job of the shell's session,
those in the background and those started with `nohup` too (on macOS, the
shell's and the foreground job's process groups). A program that leaves the
session (`setsid`, a daemon, tmux) keeps running. Stopping code-arena closes
them all.

A terminal is as powerful as the person's own shell, and so are the page's
file calls: the mode (ask, auto-edit, …) guards the agent, not the person.
That is why everything below is behind this run's key.

### Only this machine can use it

- It listens on `127.0.0.1` only, on a free port unless `--port` says which.
- The address carries a key made for this run (`?token=…`). Opening it sets a
  cookie (HttpOnly, SameSite=Strict, one per port) and takes the key out of the
  address, with a page that goes on to `/` itself: the browser sends a
  SameSite=Strict cookie on that, not on a redirect it follows from another
  site, as the launcher file below is. Every request without the key is
  refused. A new run makes a new key: an old tab says so.
- The key never goes on a command line, which every user of the machine can
  read (`ps`, `/proc`): the window or the browser is opened with a file,
  `open/code-arena-PORT.html` in code-arena's data folder (0600, in a folder
  of the person's alone, 0700), which sends it on to the address, as Jupyter
  does. The file goes when the run ends. A browser in a sandbox (a snap, as
  Ubuntu's Firefox is, or a Flatpak) cannot read it: when no browser has the
  page, cookie and all, 20 s later, code-arena says so, and the address it
  printed opens the IDE there.
- The server answers only to `127.0.0.1` and `localhost` at its port (the Host
  header), so a name pointed at 127.0.0.1 (DNS rebinding) gets nothing; it
  refuses requests from other sites, or another port's page (Origin,
  Sec-Fetch-Site), takes only JSON (a .vsix only as its bytes, which no form
  of another site's can send), and its page cannot be framed. Every call,
  the file calls and the terminals' WebSocket included, passes these checks.
- The page never holds the API key. The few routes of Arena's chat it uses
  (the person's files: `POST /api/chat/attachments`, a file's `content`, its
  `pages`) it calls on code-arena, which passes them on to Arena with the key,
  after the checks above; a form (a file) is taken there alone. Arena takes
  the key on those routes and no others of its chat, and a file is the
  person's own or not found.
- The file calls take paths relative to the working directory only: an
  absolute path, a drive or `..` is refused, even one that would land inside.
  So is a link that leads outside, one to a file not there yet outside, and
  one inside by name only: where a link leads is found as the system finds
  it, one part at a time, a `..` in its target applied after the link before
  it (with `d → /`, `x → d/../etc/passwd` is `/etc/passwd`). The Explorer,
  search and quick open leave such links out. A name is taken exactly as it
  is, spaces and (outside Windows) backslashes included. A named pipe, a
  socket or a device is not opened (it would keep the call waiting). Folders
  added with `--add-dir` are the agent's, not the IDE's.
- A cookie does not tell ports apart: another web server on 127.0.0.1 that
  the person opens in the same browser is sent it too. Open no local page you
  do not trust while the IDE runs.

The page is built into the program (below); a build without it says so when
`code-arena web` starts. Diagrams in answers are drawn by the chat's sandboxed
runner, served by the same server.

### The page's calls

For whoever changes the page (`src/web/src/code-arena/`). Every call is under
`/api/`; a refusal is `{"status": code, "error": message}` with its HTTP
status (403 `outside` for a path outside the folder).

| call | what it does |
|---|---|
| `GET /api/state` | `name`, `version`, `license`, `source` (the about box), `manual` (the help's link: the Arena's `/help/code-arena`, null without its address), the folder, project, branch, model, mode, session, busy; `turn` (the number of the answer running or run last); `queued` (the messages waiting); `context` (the window), `contextUsed` (about how much of it is in use), `compactAt` and `compactTarget` (percent; `compactAt` 0: only when a request would not fit), `compactAtSource` (`flag`, `config`, `account`, `arena` or `default`) and `compactAtAccount` (the Arena account's threshold, null when Arena did not say); `servers` (`[{name, title, url, state, tools, status, error, nextTry}]`, `state` one of `connecting`, `connected`, `unavailable`, `failed`); `jobs` (`[{id, command, running, status, background, output}]`, `output` the end of a running one's output); `arenaChats` (sessions are kept with chats in Arena here), `account` (whose chats: the key's account, null until Arena said), `chat` (this session's chat there, null: none yet) and `synced` (how many times the web chat's news came in while nothing ran: the page reads the session again when it changes; taking it in is not `busy`); `sync` (`{chat, account, waiting, trouble, behind}`: the chat, whose chats, how many messages Arena lacks, what stops them, whether the web chat has news not taken in; null without the sync) and `notices` (`[{id, text, at}]`: the last 20 said while nothing ran, the page shows each once) |
| `GET /api/folders?path=` | for Open Folder: a folder's folders by name (hidden ones last, no files, at most 2,000) as `{path, parent, folders, why, current, home, separator, drives, recent, added, files}` (`why`: what is not listed; `recent`: the folders opened lately, the one open now first; `added`: the folders added to this workspace; `files`: its files' names, with `files=1`, for @ in the chat); with no path, the open folder's parent; 404 `not_found` when it is not a folder |
| `POST /api/folders/add` `{path}` | Add Folder to Workspace: the tools may use it, kept with this folder: `{folder, added}` (`added`: the folders added, whole); 409 `inside` for the folder open now or one in it |
| `POST /api/folders/remove` `{path}` | takes an added folder out again: `{added}`; 404 `not_added` |
| `POST /api/folders/open` `{path}` | opens that folder (`~` is the home folder; relative to the one open now): answers `{folder}` with its full path, then code-arena starts again on it at the same address with the same key; 404 `not_found` when it is not a folder, 409 `not_here` in Code Studio |
| `GET /api/sessions` | this folder's sessions, newest first: `[{id, title, updatedAt, messages, chat, unsent, waiting}]` (`chat`: the chat in Arena it is kept with; `unsent`: Arena lacks `waiting` of its messages) |
| `GET /api/sessions/web` | the person's chats in Arena (the 5,000 newest): `[{id, title, origin, place, updatedAt, messages, session}]` (`session`: this folder's session kept with it); 409 `off` without the sync, 503 `arena` when Arena cannot say |
| `POST /api/sessions/web` `{id}` | continues that chat here (in the session kept with it, else a new one with its messages); the session; 404 `gone` when Arena has no such chat of the person's, 409 `busy` while an answer is written, 503 `arena` when Arena cannot say |
| `POST /api/sessions/send-all` | sends Arena what it lacks of this folder's sessions: `{sent, failed}` (`failed`: why, per session) |
| `POST /api/servers/retry` `{name}` | tries that MCP server again now (every one not connected without `name`); the state |
| `POST /api/jobs/stop` `{id}` | stops a command run with no time limit; 404 for no such job |
| `POST /api/replace` `{q, case, word, regex, include, exclude, replacement, paths}` | replaces what that search finds (in `paths` only, when given): `{files: [{path, replaced, version}], count, truncated}` |
| `GET /api/git/status` | `{repository, branch, ahead, behind, files: [{path, staged, changed, from}]}` (`staged`, `changed`: git's letters M, A, D, R, `?` untracked; null: none) |
| `POST /api/git/stage`, `/api/git/unstage`, `/api/git/discard` `{paths}` | the status after; 409 `git` with what git said |
| `POST /api/git/commit` `{message, all}` | commits what is staged (all of it first, with `all`): `{hash, status}`; 409 `nothing` when nothing is staged |
| `GET /api/git/log`, `GET /api/git/branches`, `POST /api/git/switch` `{branch}` | the last 30 commits `[{hash, subject, author, at}]` (`limit` for another number, 1 to 1,000); the branches `[{name, current}]`; switches |
| `GET /api/git/show?path=FILE&rev=REV` | the file as `rev` has it: `HEAD` (the default), `:` (the index) or a commit-ish (a hash, a branch, a tag, `main~2`): `{path, rev, commit, exists, size, text, binary, tooLarge}` (`commit`: the full hash, null for the index; `exists` false when the file is not there, at HEAD before the first commit, and in the index while the file is in conflict; `text` null with `binary` (a NUL byte, or not UTF-8) or `tooLarge` (over 5 MB)); 400 for a revision that starts with `-` or has a space or `:` in it, and for a file name with a line break; 404 `no_revision` for a revision git does not know |
| `GET /api/git/log?path=FILE&limit=N` | with `path`: the commits that changed the file, newest first, followed across renames: `[{hash, subject, author, at, status, pathAt, from}]` (`status`: git's letter A, M, D, R, C, null for a merge; `pathAt`: its name in that commit; `from`: its name before a rename; either null when outside the folder); 400 `folder` for a folder |
| `GET /api/git/commit?hash=REV` | `{hash, parents, subject, author, at, files: [{path, status, from}], truncated}` (full hashes; the folder's files changed against the first parent, all `A` in a root commit; 2,000 at most); 404 `no_revision` |
| `POST /api/git/stage-text` `{path, text}` | stages exactly `text` as the file's (the file in the folder left as it is; the index's mode kept, 100644 for a new file; the file's byte-order mark kept; a file in conflict is settled); the status after; 409 `not_text` for a link or a submodule, 400 `folder` |
| `GET /api/problems` | the check's last run: `{command, running, ran, exitCode, said, problems: [{path, line, column, severity, code, message, source}]}` (`ran` null before the first; `command` null when none is found; `source`: `check`, or `task:` and the label for what a task's problem matcher found, listed after the check's) |
| `GET /api/tasks` | `{tasks: [{label, source, group, isDefault, command, detail, dependsOn, background, terminal, running}], errors: [{file, line, message}], trusted, buildConfiguration}`: `.vscode/tasks.json`'s (`source` `workspace`), then the detected ones of other labels (`detected`); `group` `build`, `test` or null; `command` as written (null: it only waits for others); `terminal` its terminal while kept, `running` while its program runs |
| `POST /api/tasks/run` `{label}` or `{group}`, with `{context, inputs, cols, rows, wait}` | runs it (a group's default, else its only task) after what it waits for, and answers once its own terminal is open (with `wait`, once it ended or is ready: a compound's preLaunchTask): `{task, terminal, terminals}` (`terminal` null for a task that only waits for others; `terminals` every one the run opened, in order); 404 `not_found`; 409 `pick` `{tasks}` (several in the group, none the default), `untrusted` `{commands}`, `needs_input` `{inputs}`, `running` `{terminal}`, `task_failed` `{task, exitCode, terminal}` (what it waited for failed), `too_many`; 400 `bad_config` `{file, line}` (`circle` too for tasks that wait for each other), `no_program` |
| `POST /api/tasks/stop` `{terminal}` | ends a task's program (SIGHUP, SIGKILL 3 s later); its terminal once it ended (5 s at most); 404 for no such task's terminal |
| `GET /api/run/configs` | `{configurations: [{name, type, request, source, program, command, preLaunchTask, supported, reason, hidden, configuration}], compounds: [{name, configurations, preLaunchTask, stopAll}], errors: [{file, line, message}], trusted, buildConfiguration}`: `.vscode/launch.json`'s (`source` `launch`), then the detected ones of other names (`detected`, with `configuration`: what Add Configuration writes); `command` what a run runs, as written; `supported` false with `reason` when it runs only with its debugger |
| `POST /api/run/start` `{name, noDebug, context: {file, lineNumber, selectedText, settings}, inputs: {id: value}, cols, rows}` | with `noDebug`: runs it without debugging: its `preLaunchTask` first (waited for, or until ready), its program in a terminal of kind `run` (used again by its next run), its `postDebugTask` once it ended: `{terminal, configuration, command}` (`configuration` with everything filled in); 404 `not_found`; 409 `untrusted` `{commands}`, `needs_input` `{inputs: [{id, type, description, default, options: [{label, value}], password}]}`, `running` `{terminal}` (null while its preLaunchTask runs), `task_failed` `{task, exitCode, terminal}`, `too_many`; 400 `bad_config` `{file, line}` (a variable not known, `${command:…}`, a type that runs only with its debugger, a missing envFile), `no_program`, `compound` (the page starts each of a compound's configurations); 403 `outside` for a `context.file` outside the folder |
| `POST /api/run/stop` `{terminal}` | ends a run's program; its terminal once it ended (5 s at most); 404 for no such run's terminal |
| `POST /api/debug/start` `{configuration}` | starts the debug adapter for a launch configuration whose variables are already filled in (`debugServer: N` reaches one already listening on 127.0.0.1:N): `{id, name, adapter, adapterId, configuration, notes, root, rootReal, pathStyle, internalConsoleOptions}`. `adapter` is the adapter's id (`gdb`, `lldb-dap`, `netcoredbg`, `debugpy`, `dlv`, `js-debug`); `adapterId` is the type to send as initialize's `adapterID`; `configuration` is the configuration in the adapter's words, for the page's `launch` or `attach` as its `request` says (cppdbg's `miDebuggerServerAddress` becomes an `attach`); `notes` say what was changed or left out; `rootReal` is `root` with links resolved; `pathStyle` is `posix` or `win`; `internalConsoleOptions` is the configuration's own (left out of what the adapter is told). 409 `untrusted` until the folder is trusted, 409 `no_adapter` `{adapter, install}` when that adapter is not here (`install`: how to get it), 409 `adapter_failed` when it does not start (with the last lines it wrote), 409 `too_many` past eight, 400 `unsupported` for a type no adapter here takes (why, in `error`), 400 `bad_config` (also for a `debugServer` that is no port) |
| `GET /api/debug/sessions` | `[{id, name, adapter, type, state}]` (`state`: `starting`, `running`, `stopped` or `ended`); an ended one stays listed until it is stopped (the 8 newest) |
| `GET /api/debug/socket?id=ID` | the session's WebSocket: DAP messages as JSON text, one per message, both ways (below); 404 for no such session |
| `POST /api/debug/stop` `{id}` | a launch: `terminate` when the adapter has it, then `disconnect` with `terminateDebuggee`; an attach: `disconnect` alone, the program let go. Whatever is left 3 s later is killed with every process it started, and the session is forgotten; 204, 404 for no such session |
| `GET /api/debug/source?id=ID&path=P` | one of the session's source files. In the folder (an absolute or a relative path), as `/api/file` reads it, with `readOnly: false`. Outside the folder only when the session's adapter named that path (a stack frame, a breakpoint, its loaded sources), and never Code Arena's own config or data files, without `version`, with `readOnly: true`. Anything else is 403 `outside` |
| `GET /api/debug/adapters` | `[{id, name, types, found, path, version, reason, install}]`: the six adapters as found here (`types`: the launch.json types each serves; `path`: the program, or `127.0.0.1:N`; `reason`: why it is not found or will not do; `install`: how to get it, when it is missing) |
| `GET /api/lsp` | `[{id, name, languages, found, path, version, reason, install, settings}]`: the language servers, `config.json`'s own first, then the built-in ones (`languages`: Monaco's ids; `path`: the program found; `version` as it says it, for a built-in one found on PATH; `reason`: why it is not found or is off; `install`: how to get it, when it is missing; `settings`: what the page answers its `workspace/configuration` with over settings.json, Code Arena's own defaults with `config.json`'s over them) |
| `GET /api/lsp/socket?server=ID` | the server's WebSocket, the server started as it opens (in place of the one of that id another page had, which is told) and stopped as it closes: LSP messages as JSON text, one per message, both ways (below). Refused before it opens: 403 `not_trusted` until the folder is trusted, 404 `missing` for no such server or one not found here (`install`), 409 `too_many` past six running, 409 `failed` when it does not start |
| `GET /api/lsp/log?server=ID` | `{server, running, exitCode, lines, refused}`: the last 200 lines the server running (else the last that ran) wrote to stderr, and why its last start was refused (`refused`, cleared by one that starts: a WebSocket refused tells the page nothing); 404 `missing` for no such server |
| `POST /api/problems/run` | runs the check (waits for the one running) and answers as above; 404 `no_check` when none is found |
| `POST /api/complete` `{path, prefix, suffix}` | code completion at the editor's cursor: `{text}`; 404 `off` when the config turns it off, 409 `busy` while an answer is written, 429 `limited` at the key's limit |
| `POST /api/messages` `{text, context, quote}` | a message, answered as a stream of events; `context`: lines chosen in the editor, `[{path, startLine, endLine, text}]` (`text`: the editor's, when unsaved; eight at most); `quote`: the part of an earlier answer it replies to (4,000 characters at most, else 400 `invalid`), which the model reads before the message, as Arena's own model does ([Chat](chat.md#how-a-conversation-is-stored)). The stream's `{"type":"question", id, text, quote}` starts it, and `{"type":"attached", id, files}` lists what went with the question. The session as the page reads it gives each question's `quote`, `content` (what was typed) and `files` apart |
| `POST /api/queue` `{text, context, quote}` | a message for after the answer being written (runs at once when none is), with its lines and its quote; 202 `{queued}` (its place). `/api/messages` while an answer is written is 409 `busy` |
| `DELETE /api/queue` | drops the queued messages; 204 |
| `GET /api/files?path=DIR` | a folder's entries, folders first: `{path, entries: [{name, path, kind, size, link}]}` |
| `GET /api/files/all` | every file, for quick open: `{files, truncated}` (50,000 at most) |
| `GET /api/file?path=FILE` | `{path, size, version, text}`; `text` is null with `binary` or `tooLarge` (over 5 MB) |
| `GET /api/media?path=FILE` | a picture, sound or video of the allowed folders as it is (PNG, JPEG, GIF, WebP, BMP, AVIF, MP3, WAV, Ogg, FLAC, M4A, AAC, MP4, WebM, MOV; up to 256 MB), for an `img`, `audio` or `video` element: the media type set, `nosniff` and a sandboxing CSP; 415 `not_media` for anything else (SVG and HTML included), 413 `too_large` |
| `POST /api/file` `{path, text, version}` | saves; 409 `changed` when the file changed since `version`; `{path, version, size}` |
| `POST /api/files/new` `{path, kind}` | a new file, or a folder with `kind: "dir"`; 409 `exists` |
| `POST /api/files/rename` `{from, to}` | moves a file or folder; `{from, to}` |
| `POST /api/files/delete` `{path}` | a file, or a folder with what is in it (a link, not what it points at) |
| `GET /api/search?q=…&regex=1&case=1&word=1&include=…&exclude=…` | `{files: [{path, matches: [{line, column, length, preview, start}]}], count, truncated}` (2,000 matches, 20 s at most) |
| `GET /api/changes` | the agent's changed files: `[{path, created, deleted, added, removed}]` |
| `GET /api/changes/diff?path=FILE` | `{path, original, modified, version}` (`original` null: the agent made it) |
| `POST /api/changes/accept` `{path, before}` | keeps the change (all of them without `path`); with `before` (a text), part of it: that text becomes the file's before the agent (equal to the file now, or empty for a file gone since: all of it); the list left |
| `POST /api/changes/revert` `{path}` | puts the file back; the list left |
| `GET /api/terminals` | `[{id, kind, title, pid, cols, rows, exitCode}]` (`kind`: `shell`, `task`, `run` or `debuggee`; `title`: the shell's name, a task's label or a configuration's name) |
| `POST /api/terminals` `{cols, rows}` | opens the person's shell; 409 `too_many` past sixteen running. With `{program, args, cwd, env, title}` (a debug adapter's `runInTerminal`, passed on by the page): that program in a terminal of kind `debuggee`, in the folder or one inside it (403 `outside` else), without `ARENA_API_KEY` |
| `POST /api/terminals/close` `{id}` | ends its shell and forgets it |
| `GET /api/terminals/socket?id=ID` | the terminal's WebSocket |
| `GET /api/history` | what this folder sent, newest first, each text once (500 at most): `[{text}]`, without the terminal's own commands (`/help`, `/model`, …; `/compact` and `/clear` stay) |
| `GET /api/preferences` | the page's own preferences (`layout`, `paletteRecent`), as it saved them; `{}` at first |
| `POST /api/preferences` `{key: value}` | keeps these keys, merged over the others (`null` forgets one); 413 `too_large` past 16 KB |
| `GET /api/formatters?path=FILE&language=ID` | the file's formatters, in order: `{language, formatters: [{id, name, version, source, available, reason, hint, range, path}], default}`. `language` is Monaco's id, from the file's name or as sent. `source` is `project` (in the folder: runs only once it is trusted), `global` or `config`. A formatter that cannot run has `reason` `trust`, `missing` or `node` (Prettier without node on PATH), and `hint` (what to install, or "trust this folder"). `path` is the program, or Prettier's script, that would run. `default` is the first available one (null: none). A global formatter is listed only while the folder's own with the same `id` cannot run. 400 for a folder, 403 `outside` |
| `POST /api/format` `{path, text, formatter, range: {start, end}, options: {tabSize, insertSpaces, prettier}, language}` | the text through the formatter `formatter` (an `id` from the list; the default without it): `{text, formatter, source}` (`formatter` the `id` that ran, `source` where it came from), always the whole text, even for a range (`start` and `end` are UTF-16 offsets). Errors: 422 `format_failed` `{error, line, column}` (the first 20 lines the tool said, colours removed; `line` and `column` when it named them); 422 `no_range` for a whole-file formatter; 504 `timeout` (10 s, 30 s for dotnet format, or a config entry's `timeout`); 413 `too_large` past 5 MB; 403 `not_trusted` for the folder's own formatter before the folder is trusted; 404 `no_formatter` (none for the language, not installed, or no such `id`); 400 for a bad range or a missing `text`. `options.prettier` takes VS Code's `prettier.*` settings (`printWidth`, `tabWidth`, `useTabs`, `semi`, `singleQuote`, `jsxSingleQuote`, `trailingComma`, `bracketSpacing`, `bracketSameLine`, `arrowParens`, `endOfLine`, `proseWrap`, `quoteProps`, `singleAttributePerLine`, `htmlWhitespaceSensitivity`, `embeddedLanguageFormatting`, `vueIndentScriptAndStyle`) as flags; a project's config file wins over them |
| `GET /api/user-files?name=settings.json` (or `keybindings.json`) | the person's own file in the config folder: `{name, text, version, exists}` (`text` empty, `version` `none` before the first save); 400 for any other name |
| `POST /api/user-files` `{name, text, version}` | saves it as sent (comments kept; readable by the person alone); 409 `changed` when it changed since `version`, 413 `too_large` past 1 MB |
| `GET /api/user-files/vscode?name=…` | VS Code's own copy on this computer (Code, then Insiders, then VSCodium): `{name, text, from}`; 404 when there is none |
| `GET /api/processes` | the person's own processes, newest first, Code Arena itself left out: `[{pid, name, command}]` (`command` null where the system does not say; 2,000 at most), for `${command:pickProcess}` |
| `GET /api/extensions` | the extensions kept: `[{id, name, displayName, version, publisher, description, icon, enabled, source, contributes: {themes: [{label, uiTheme, path, id}], iconThemes: [{id, label, path}], snippets: [{language, path}], languages: [{id, extensions, filenames, filenamePatterns, aliases, mimetypes, firstLine, configuration}], grammars: [{language, scopeName, path}], keybindings, configurationDefaults}, unused}]` (`id`: `publisher.name`, lower case; `source`: `vsix` or `vscode`; `keybindings`: how many; `unused`: what it has that is not used, and why) |
| `GET /api/extensions/contributions` | what the enabled ones bring together, for the page to apply: `{themes, iconThemes, snippets, languages, keybindings: [{command, key, mac, linux, win, when, args}], configurationDefaults: [{values}]}`, each entry with its `extension` and `version` |
| `GET /api/extensions/file?id=ID&path=P` | one of its files that its contributions name (a theme, an icon, a font, snippets…), with its type by its extension, `X-Content-Type-Options: nosniff` and `Content-Security-Policy: sandbox`; 404 for any other |
| `POST /api/extensions/install` (the .vsix's bytes, `Content-Type: application/octet-stream` or `application/zip`) | keeps it (in place of another version of it): the extension as listed; 400 `invalid` for no zip or no `extension/package.json`, 413 `too_large` past the limits, 422 `unsafe` for a name that leads out or a link, 422 `nothing_usable` (with `unused`) for one that is only code |
| `POST /api/extensions/import` `{id}` | copies VS Code's newest copy of it on this computer, as an install; 404 when VS Code has none |
| `POST /api/extensions/uninstall` `{id}` | takes it and its files away; 204; 404 for none |
| `POST /api/extensions/enable` `{id, enabled}` | turns it on or off: the extension as listed |
| `GET /api/extensions/local` | VS Code's extensions on this computer, the newest copy of each: `[{id, name, displayName, version, publisher, description, contributes, unused, from, path, usable, problem, installed}]` (`from`: VS Code, VSCodium or VS Code - Insiders; `problem`: why it cannot be imported; `installed`: the version kept here) |
| `GET /api/extensions/recommended` | `.vscode/extensions.json` (comments allowed): `{file, error, recommendations: [{id, name, status, note}], unwanted}` (`status`: `installed`, `built-in`, `unsupported`, `local` or `unknown`; `note`: what it means here) |
| `GET /api/workspace/state` | what this computer keeps for this folder (in the data folder, never in the project): `trust` (`trusted`, `restricted`, missing: not asked), `buildConfiguration`, and the page's own keys (breakpoints, watch, the run configuration chosen) |
| `POST /api/workspace/state` `{key: value}` | merges these keys (`null` forgets one); 400 for `trust`, or a `buildConfiguration` other than `Debug` or `Release`; 413 past 1 MB |
| `POST /api/workspace/trust` `{trusted}` | trusts the folder (`true`), keeps it restricted (`false`) or forgets the choice (missing); the state |

On a debug session's WebSocket the server first sends
`{"type":"event","event":"codeArena.snapshot","seq":0,"body":{id, name, adapter, type, state, error, configuration, capabilities, stopped, threads, exitCode}}`
(`capabilities`: the adapter's answer to `initialize`; `stopped`: the last
`stopped` event's body while the program is stopped; `threads`: the threads
still alive), then the last 256 KB of `output` events, then each message from
the adapter: a response goes only to the page that sent the request, with that
page's own `seq` as its `request_seq`, and an event to every page.
`{"type":"event","event":"codeArena.state","seq":0,"body":{state, error}}`
comes each time the state changes (`error`: why the adapter ended, with the
last lines it wrote). When an adapter ends without `exited` and `terminated`,
the server sends them for it (`exitCode` -1), answers each request still
waiting with `success: false`, and closes the socket. The page drives the
protocol: `initialize`, then `launch` or `attach` with the start's
`configuration` without waiting for its answer (gdb answers it after
`configurationDone`), then, on `initialized`, its breakpoints and
`configurationDone`. A `cancel`'s and a `progressStart`'s `requestId` are the
page's own numbers. A request from the adapter (`runInTerminal`,
`startDebugging`) goes to every page, and the first answer reaches the
adapter; the page runs `runInTerminal`'s program in a terminal of its own and
answers with its process, and answers any other with `success: false`. A page
sends 1 MB per message at most, an adapter 32 MB; a page that falls 10,000
messages or 64 MB behind is let go (its socket closes; it reattaches to get
the snapshot).

On a language server's WebSocket, each message is relayed as it is but for
its file URIs: the page names a file of the folder `file:///` and its path in
the folder (as its editor's models are named), the server `file://` and its
full path (the folder's real path, its links resolved, is the folder too); a
file outside the folder is `outside://` and its full path for the page. Every
string in a message that is a whole file URI is rewritten both ways, and each
key that is one (a WorkspaceEdit's `changes`), but not a document's or an
edit's text (`text`, `newText`, `insertText`, `value`), and a path of the
page's that would leave the folder (`..`) is passed on as it is. `initialize`'s
`processId` (null in a sandbox), `rootUri`, `rootPath`, `workspaceFolders` and
`initializationOptions` are the server's to set, whatever the page sends. A
server that ends (or is stopped) sends the page, last,
`{"jsonrpc":"2.0","method":"codeArena/ended","params":{reason, exitCode,
takenOver, stopped}}` (`takenOver`: another page took it; `stopped`: Code
Arena stopped it: the trust was taken back, or it is stopping), then the socket
closes. Closing the socket sends the server `shutdown` and `exit`, and kills
what is left 2 s later with every process it started. A page sends 16 MB per
message at most, a server 64 MB; a server waits while 1,000 of its messages
wait for the page.

On the terminal's WebSocket, the server sends the output kept so far, then
the output as it comes, as binary messages (the terminal's bytes), and
`{"type":"exit","code":N}` (text) when the shell ends, then closes. The page
sends keys as `{"type":"input","data":"…"}` (text) or as binary messages, and
`{"type":"resize","cols":N,"rows":N}`. The chat's calls (`/api/messages`,
`/api/turn`, `/api/approvals`, `/api/settings`, `/api/sessions`, …) are
Arena's chat's shapes; `/api/settings` also takes `compactAt` (0: only when a
request would not fit; null: the Arena account's again) and `compactTarget`
(400 `invalid` with the bounds), and a turn's stream adds
`{"type":"job", job, command, running, status}`, `{"type":"job_output", job,
text}` (a few times a second, for as long as the command runs; a page that
comes back during the turn is sent the last 64 KB of each) and
`{"type":"job_end", job, status, exitCode, stopped}` for its commands with no
time limit.

## Modes

| mode | reads | file edits | commands, and MCP tools that change things |
|---|---|---|---|
| `ask` (default) | run | ask | ask |
| `auto-edit` | run | run | ask |
| `plan` | run | refused | refused |
| `yolo` | run | run | run |

A question takes **y** (yes), **n** (no: the model is told, and asked to find
another way) or **a** (always, for this session: every file edit; a command by
its first two words, `npm test …`; an MCP tool by name). `plan` refuses every
tool that changes something when the model calls it (the model is offered the
same tools in every mode, so the start of its requests stays cached): the
agent investigates, then proposes its plan, which you review
([below](#reviewing-a-proposed-plan)).
`yolo` warns when it starts: use it in a folder you can throw away. Set the
mode with `--mode`, `/mode`, or `"mode"` in the config file.

The tools stay inside the working directory, and those added with `--add-dir`
(or `"allowedPaths"` in the file); a link inside that leads outside counts as
outside, where it leads found as the system finds it (a `..` in a link's
target is applied after the link before it, not by name), and a loop of links
or more than 40 on the way counts as outside too. Commands run in the working
directory, but they are the person's own commands, with their rights: the
mode is what guards them. The API key is removed from the environment of every
command and MCP server the agent starts.

### Laya looks at commands

When Arena MCP offers `decide` (the Arena runs the laya module, and the person
may use **Decide (Laya)**: [chat.md](chat.md#decide-laya)), every command is
shown to Laya before it runs, with three yes/no questions: is it destructive,
does it write outside the workspace, does it reach the network. Laya reads
the command on one line (its line breaks written `\n`, so none can pose as
the lines after it), then the working folder and the paths outside it that
the command names (`~`, `..`, `/etc`...; found here, since Laya reads text and
does not compare paths), with its English checkpoint, in about 0.35 s on the
Arena's CPU. Only while the English checkpoint is not loaded does the
multilingual one read the commands (its probabilities are not calibrated).

Laya's English checkpoint reads 512 tokens, the question included: about 460
are left for the command. Code Arena counts a command's tokens from above
(shell is about 2.5 characters a token, Persian and base64 about 1), and
reads a longer command in parts, cut after its line breaks, `&&`, `||`, `;`
and `|`, each part with the folder and the paths after it; each question
takes its highest probability over the parts, so a `git push --force` after
a long heredoc is still seen. Laya says when it cut a part short anyway (it
counts its own tokens); that part is read again in smaller ones. A command
that would take more than 8 parts (about 6,000 characters of shell, fewer of
Persian or base64) is not read at all, and asks as a flagged one does. A
long command takes Laya a second or more for each part.

- **Every question about a command shows the three probabilities**:
  `Allow run_shell? (Laya: destructive 96%, outside the workspace 93%, network 3%)`,
  in the terminal and in the IDE.
- **A command Laya rates at 60% or more on any of the three asks anyway**,
  in `yolo`, and when **always** was said for its first words; so does one
  Laya could not read all of. **Always** said to a command Laya flagged covers
  the next flagged one with those words; said to one it could not read all
  of, it covers the next one it could not read, never one it flags.
- **A run that cannot ask** (`-p`) does not run a flagged command: the model is
  told why, and finds another way (for a long one: write the text with the
  file tools, and run shorter commands).
- **It only ever adds a question.** When Laya does not answer at all (10
  seconds at most for each part; an error, "busy", or a connection that
  drops), commands run as the mode says, and the session says so once, in the
  terminal and on the IDE's page. When it answered some parts of a command
  and not the rest, the command asks, with what Laya found in the parts it
  read. Nothing that would ask runs without asking.

The threshold, 0.6, is measured: of 95 commands labelled by hand, Laya asks
before 45 of the 52 risky ones (`rm -rf ~`, `git reset --hard`, `curl … |
sh`, `git push --force`, `pip install`...) and 2 of the 43 harmless ones
(`echo hi > /dev/null` and `tar -czf build.tgz dist`, 75% and 74% destructive).
That is with the command first: with the folder and the paths before it, Laya
asks before only 41 of the risky ones, and 3 harmless ones.
It misses `git checkout -- .`, `git branch -D`, `crontab -r`, `docker system prune`,
`cp config.json ~/.config/shop/`, `go get` and a `curl -X POST` that uploads a
file. At 0.7 it asks before only 37 of the risky ones, with the same 2 harmless
ones. The commands are in `tests/CodeArena.Tests/RealLayaTests.cs`, which asks
a real Laya when `LAYA_URL` names one. It is a second look, not a sandbox:
`yolo` is still for a folder you can throw away.

## Tools

On the person's machine:

| tool | what it does |
|---|---|
| `read_file` | a text file with line numbers, 2,000 lines at a time (`offset`, `limit`) |
| `write_file` | creates or replaces a file; Windows line endings are kept |
| `edit_file` | replaces an exact piece of a file; it must match once (or `replace_all`), whitespace included; an empty `old_string` makes a new file. Windows line endings are kept |
| `list_dir`, `glob` | folders and file names (`**/*.cs`, `*.{ts,tsx}`); `.gitignore` is respected |
| `grep` | file contents by regular expression, with context lines; binary and ignored files skipped, and those it cannot read, named pipes and links that lead outside |
| `run_shell` | a command in bash (sh when missing; on Windows Git's bash when Git for Windows is installed, else PowerShell 7, else `cmd.exe`; `"shell"` in the file for another), 120 s by default, at most 600; long output is cut in the middle. With `no_time_limit`, it runs in the background until it ends (below) |
| `command_output` | the state and the latest output of a command run with no time limit; `wait` waits until it ends |
| `stop_command` | stops a command run with no time limit; it always asks the person first, `yolo` too |
| `git` | status, diff, log, show, blame, branch: reading only (commits go through `run_shell`), inside the working directory: `diff --no-index` is refused, and so is a file outside it named anywhere in the arguments (`blame --contents FILE`, `-S FILE`), by name or through a link that leads out. It never asks, so the repository's own settings run nothing in it: no fsmonitor, hook, clean or smudge filter, merge driver (`--remerge-diff`), text conversion (so not `status -v`), external diff or signature checker, no submodule looked into (their changes are not shown), and no network (a partial clone does not fetch). Nor do they send it elsewhere: a work tree other than the folder that holds `.git` (`core.worktree`), or a `.git` that leads to another repository's folder, is refused (a worktree of another checkout and a submodule, which git made so, are read), and a `blame.ignoreRevsFile` outside is not read. The file list `grep` and `glob` (and the IDE's search and quick open) take from git is read the same way |
| `screenshot` | a picture of a web page (`url`, at `width` × `height`, 1280 × 800 by default: the app you build, on its dev server) through a headless Chrome, Edge, Chromium or Brave (`CODE_ARENA_BROWSER` names one); without `url`, the whole screen (grim, gnome-screenshot, spectacle, ImageMagick or scrot on Linux; screencapture on macOS; PowerShell on Windows). The picture goes to the model after the result (one that sees pictures; another is told it cannot). It asks first, as a command does |
| `ask_agent` | hands a task to one of the person's own coding agents (below), which runs it in the working directory; what it prints comes back. Offered when one is installed; it asks first, as a command does |
| `plan` | the working plan the person sees (`/plan`), kept in Arena with the session's chat ([below](#the-plan)) |
| `task` | a sub-agent (below) |
| `run_steps` | 2 to 4 steps of the plan that wait for nothing not done, at once ([below](#steps-at-once)) |
| `run_code` | a Python program that calls the session's tools as functions ([below](#code-mode)) |

The files are read as they are while other programs hold them open (on
Windows a build, a server's log or an editor would otherwise make the read
fail), in UTF-8 or, by their byte-order mark, UTF-16 and UTF-32 (Windows
PowerShell's `>` writes UTF-16): such a file is read, searched and edited as
text, and written back in its own encoding. `write_file` keeps a file's
Windows line endings, as `edit_file` does. On Windows a path as Git's bash
writes it (`/c/Users/me/shop`) is taken as `C:/Users/me/shop`.

**Your own subscriptions.** Claude Code (`claude`, a Claude Pro or Max
subscription), Codex CLI (`codex`, ChatGPT Plus or Pro) and Gemini CLI
(`gemini`) work beside Code Arena when installed and signed in on the machine
(each once, in a terminal, the provider's own way: `claude` then `/login`,
`codex login`, `gemini`). Code Arena never reads, keeps or relays those
sign-ins: it runs the provider's program on a task, non-interactively, in the
working directory, and reads what it prints, so the provider's terms and
limits apply as when you run it. `/agent` lists them; `/agent claude fix the
failing test` runs one; the model may hand one a task too (`ask_agent`), when
you ask for it or for a second opinion. `"agents"` in the config sets their
command lines (`{"claude": ["claude", "-p", "--permission-mode",
"acceptEdits"]}`), adds another, or turns one off (`[]`). Providers' APIs by
key (OpenAI, Anthropic, Gemini, OpenRouter...) come through Arena instead:
an admin adds them under Admin → Models → Other servers and providers, and
their models are in Code Arena's model list with the rest.

**Arena's tools** come from `https://DOMAIN/mcp` with the person's key: the
same tools as the chat, with the same rights and the same audit, as that
person. They run without asking, except those Arena marks as changing
something, which ask like a command. A tool named like a local one is offered
as `arena_<name>`. Arena's own instructions to the model are passed on.
Arena's tools make pictures, video and speech too: what a tool returns (a
picture or sound inline, a file it links to, fetched with `resources/read`;
up to 256 MB) is saved in the working directory under `.code-arena/media/`,
named after the tool and the time (`generate_image-20261016-101500.png`), and
the model is given its path to tell the person, who opens it in the IDE. That
folder has a `.gitignore` of its own, so the files are never committed.

**Every tool call has a deadline**: 10 minutes without an answer, unless the
tool says it takes longer (`"arena/timeoutSeconds"` or `"timeoutSeconds"` in
its `_meta`). Each progress report the server sends (MCP
`notifications/progress`, asked for with a progress token on every call)
starts the time again and is told to the person (every 20 seconds at most).
Past it the call is given up: the server is told (`notifications/cancelled`)
and the model hears that it timed out. Every connection has TCP keep-alive
(a probe after 30 seconds of quiet, then every 10), so a server that went away
without a word (a laptop asleep, a VPN that changed) is found out in about a
minute and a half rather than never.

**Argus's tools** come from `https://argus.DOMAIN/mcp` with the same key
(`"argusUrl"` in the config file for another address; none for an Arena
reached by an IP address or as `localhost`). Arena serves the person's Argus
tools too: those it serves are offered once, as Arena's, and Argus's own copy
is used while Arena is not connected. Argus's other tools join beside them.

Both are on by default once signed in, and both connect **in the
background**: a session starts when the gateway has listed the models, and
waits a few seconds at most for its servers (`-p` waits up to 25 seconds, as
it cannot wait later). A server that has not answered by then joins when it
does, with its tools and its instructions, and the terminal says so. One that
is down or refuses is said once, then tried again after 5 seconds and less
often after that, up to every 5 minutes; a call that finds it gone (the
connection dropped, or its proxy's 502, 503 or 504) has it connect again at
once. `/mcp` (or **MCP servers** in the IDE's status bar) shows each one,
connected or not and why; `/mcp retry` (**Try again**) tries now. When the
Arena has no MCP endpoint at all, or no Argus beside it (`argus.DOMAIN` does
not resolve, or has no MCP endpoint), the session says so once, quietly, and
carries on. A server that fails in any other way is said and tried again
the same way; nothing waits for it for ever (a command waits for Arena's
first answer, for Laya, 20 seconds at most). An address that is not an http
or https one (`"mcpUrl"`, `"argusUrl"`, `"gateway"` or their variables: a
port out of range, a space, `ftp://`) stops the session as it starts, saying
where to fix it; an MCP server of your own at such an address is skipped and
said. `"arenaTools": false` and `"argusTools": false` in
`config.json` turn them off. `code-arena login` tries both and says what it
found.

**Which of them, and when they load.** As in the web chat, a session has its
own choice of Arena's tools: the IDE's **Tools** menu beside the thinking
picker (the web chat's own menu: Arena's tools by the name its chat gives them,
each MCP server of yours as one) or `/tools off NAME` and `/tools on NAME` in
the terminal. A tool off is left out of the requests, and a call to it is
refused; the local tools are always there. The choice is kept with the session
(a new one has every tool on). Arena's tools are many (about 35 functions, some
9,000 tokens), so they come **on demand**, as in the web chat: past 6,000
characters of definitions (`"toolChars"` in `config.json`; 0 sends them whole
always) only those loaded go whole in each request. The others are listed in
Code Arena's note, a line per tool, and the model loads what a task needs with
`load_tools` (a function called straight from the list is loaded then). What a
session loads stays loaded, kept with it, so the start of the requests stays
what the engine has cached; loading one more reads the prompt again once.

### Commands with no time limit, and in the background

A command that takes longer than `run_shell`'s limit (a full build, a long
test suite, an install, a migration) runs with `no_time_limit`: as a job of
its own, with a watcher that shows its output as it comes (in the terminal
under its job number, `│1 …`; in the IDE above the chat's box), however long
it takes. In the terminal its lines never break into a question waiting for the
person's answer or into the middle of the model's line: they wait, and follow.
The model gets a job number at once and goes on with other work, or ends its
answer: **the turn does not wait for the job**, so the prompt is free and the
person can type their next message meanwhile. When the job ends, the model is
told its exit code and the end of its output (the last 8,000 characters; the
last 256 KB are kept for `command_output`) and **carries on from there in a
turn of its own** (`job 1 ended: the model carries on`), at the prompt or in the
IDE, once no other turn runs. `command_output` reads a job's latest output, and
with `wait` waits for it to end, ten minutes at most. Several run at once. A time
limit given with it does not apply. When a turn fails while one runs (the
gateway or the model's server down), the command is not stopped.

A server or a watcher that never ends by itself (`npm run dev`, `dotnet watch`,
a database) runs with `background`: the model gets its first output after a few
seconds (where it listens, or why it failed), and it runs until stopped. Its end
starts no turn, and stopping a turn does not stop it.

Only the person stops a command with no time limit: **Ctrl+C** in the terminal
(or **Stop** in the IDE) stops the turn and the commands that turn started with
no time limit; those in the background run on. `/jobs` lists the jobs, running
or ended, and `/jobs stop N` stops one (the model is told how it ended). **Stop**
next to a command in the IDE stops that one (a command running that no turn on
the page shows, after a reload, is listed from the session's state with its
**Stop**), and the model's `stop_command` asks the person first in every mode,
every time: it offers no **always**. Ordinary commands keep their limit (120 s
by default, at most 600).

Running a command with no time limit, or in the background, asks where
commands ask: in `ask` and `auto-edit` the question says "with no time limit"
or "in the background", and **always** said to `npm test` does not cover
`npm test` with no time limit or in the background (nor the other way round),
so the person agrees to each kind of wait. `yolo` runs it without
asking, as it runs every command: it is watched, shown, and stopped with
Ctrl+C or Stop, so nothing runs out of the person's sight. Laya still looks at
the command first (above). `-p` cannot ask, so outside `yolo` it is refused
with a reason the model passes on. Sub-agents cannot start one.

**Sub-agents** (`task`): the model hands a self-contained piece of research to
a fresh conversation with only the reading tools (local and Arena's), which
returns one report. Several run at once. Their tokens count in the turn.

Calls that do not wait on each other run at once (the model asks for them
together); questions are asked one at a time first.

## Instructions: ARENA.md

Every session reads, into the model's instructions:

1. `ARENA.md` in the config folder: the person's own, for every project;
2. `ARENA.md` at the repository's root (the folder holding `.git`), or in the
   working directory when it is not in a repository;
3. `ARENA.md` in the working directory, when it is below the root.

What to put there: how to build and test, conventions, what not to touch.
Each file is read up to 20,000 characters. The model also gets the system, the
shell, the working directory, the git branch, the date and the mode.

## The plan

For work of several steps the agent keeps a plan with the `plan` tool: a
title, a goal, and steps, each with what done means (its check: a command
whose success shows it, as `dotnet test`, or what to look at), the files and
the symbols (functions, classes, types) it changes, the steps it waits for,
notes, and a status (to do, doing, blocked, done; one doing at a time). It makes the plan before it starts and sends only
what changes as it goes: a step by its number, a new one without, one taken
out with `remove`, all of it again with `clear`. A step that waits for one
not there, steps that wait for one another in a circle, or two doing at once
are refused, and the plan stays as it was.

**Nothing is done that was not checked.** A step's check that is a command
(its first word a program here, or a script in the project) is run by the
agent with `run_shell`, exactly as written, with the same permissions as any
command; Code Arena records how it went on the step (passed or failed, and
the end of its output). The step is done only once that check passed since
it began: marking it done otherwise is refused, and a failed check is fixed,
or the step changed with the reason in its notes. A check in words ("the
page shows the cart") is the agent's to look at. A **checkpoint** is kept
before each step as it begins: `/plan undo N` (the IDE's **Undo step N**) puts
the files written since back as they were before step N began (and before
every step begun after it) and makes those steps to do again; the agent is
told. What commands changed is not undone.

**A plan outlives its session.** A new session in a folder where an earlier
one left its plan in progress (a step not done) says so when it starts:
`/plan resume` (the IDE's Plan view: **Carry it on here**) brings that plan
into the new session with its progress and notes, and the agent is told
where it stands and to see what that session changed before going on.
`code-arena --plan` starts a session that does so at once; with `-p` it runs
unattended, so the system's scheduler can carry a long task on, a turn at a
time, for example nightly: `code-arena --plan -p "Carry on with the plan"`
(a session that finds no plan in progress says so and goes on). Code
Arena's plans in progress are first on the web's **Plans** page.

### Steps at once

Steps that wait for nothing not done can run together: the agent calls
`run_steps` with 2 to 4 of them. A step that changes no file (research) goes
to a read-only sub-agent, whose report becomes the step's. A step that
changes files goes to a worker in a **git worktree of its own**, made from
the project as it is now (what is changed and not committed included) in the
system's temporary folder: it edits there freely and runs no command but its
step's check (anything else is declined); its check runs there once more on
what it left, and once it passed the change is merged into the project (`git
apply --3way`), each file kept for `/rewind`. A step whose check failed, or
whose change does not merge, is blocked with why in its notes, and its
worktree is kept for you to look at (`git worktree list`). Steps that change
files run at once only in `auto-edit` or `yolo` mode, where edits run
without asking.

The terminal shows the plan each time it changes, and `/plan` shows it as it
stands. It is kept with the session (`--continue` and `/resume` bring it
back) and said again after every summary, so it is in the model's window
however long the work goes on. While the session is kept in step with a chat
in Arena, the plan goes there too, once the messages that changed it have:
it shows under **Plans** in the web, with the session's project, as the
agent keeps it ([chat.md](chat.md#plans)).

### Reviewing a proposed plan

In plan mode the agent makes its plan with the `plan` tool as a proposal.
It is **grounded in the code** first: when Argus is connected and the project
is a repository it indexes, each step's symbols are looked up with
`change_impact` (at most 12 a plan), and the step shows what they reach: the
lines that use each name in every repository, how many repositories depend
on this one, and the tests that name it. Before you see the plan, a **second
reading** looks at it with fresh eyes: a
sub-agent in a context of its own (the same model, the reading tools), given
what you asked and the plan, says what is missing, what is risky, which step
has no check or one that would not show it is done, and what is out of
order. The agent folds in what holds and says what it left out; the reading
happens once a plan, and you see its notes with the plan (`"planCritique":
false` in the config file turns it off). When the turn ends the terminal
shows the plan, and the IDE's Plan view offers the same choices. Each is said
to the agent as your next message:

| terminal | IDE | what happens |
|---|---|---|
| `/plan approve` | **Approve** | the mode becomes `auto-edit` and the agent carries the plan out, step by step: each step doing as it starts, its check run when it is done, done only once the check passed |
| `/plan approve ask` | **Approve, asking before each change** | the same, in `ask` mode |
| `/plan change N what` | **Change step N** | the agent changes that step as you say and proposes the plan again |
| `/plan another why` | **Another way** | the agent proposes a different plan |
| `/plan reject why` | **Reject** | the plan goes, and the agent is told not to carry it out |

Saying anything else keeps the conversation going in plan mode. On the
Plans board in the web, a proposed plan says it waits for its review in
Code Arena.

## Code mode

Rather than calling tools one by one, each call and its whole result passing
through the model's window, the agent can write a short Python program that
calls them as functions (`run_code`): `import arena`, then
`arena.tools.search("…")` lists the functions that match,
`help(arena.local.read_file)` says one's parameters, and
`arena.local.grep(...)`, `arena.argus.find_symbol(...)`,
`arena.<server>.<tool>(...)` call the session's own tools, Arena's, Argus's
and your MCP servers'. The program loops, filters and joins, and only what it
prints comes back. Each call goes through a socket to Code Arena and is
checked as a direct call would be (the mode, your rules, asking where it
asks); at most 100 a program. The program runs in the working directory, in
the session's sandbox, with `python3` (`"python"` in the config file names
another); `run_code` asks first as a command does, and plan mode refuses it,
since a program could write without a tool. The `arena` module is the one the
web chat's sandbox has ([chat.md](chat.md#what-it-does)).

How the model is given its tools follows the model in use, as Arena says for
it (Admin → Models → **Tools**, in Arena's chat config): **direct** leaves
`run_code` out; **code** sends the servers' tools (Arena's, Argus's, your MCP
servers') only as functions for a program, named in Code Arena's note under
"Tools as code", while the files, search, git and shell tools stay as they
are; **both** (also without a word from Arena) sends all. `"toolMode":
"direct" | "code" | "both"` in the config file has the last word; the IDE's
gear (Manage) sets it, or takes it away to follow Arena again.

## Sessions and the model's window

Each session is a JSON Lines file in the sessions folder (0600): every message
as it happens, the model, the plan and the tokens. `--continue`,
`--resume` and `/resume` read it back. A compaction appends the new history
(the summary and the part kept), never rewrites the file. Each file is
scanned once while Code Arena runs and then only what was appended to it (by
its size and time), so listing sessions and the IDE's session list stay fast
with long sessions; reading one back starts at its last compaction.

The model's window comes from the gateway (`/v1/model/info`), or `"context"`
in the config file when the gateway does not say (32,768 otherwise). When the
next request would pass the **threshold** (your Arena account's choice, as the
web chat's: Your account → Answers → **Compact long chats**; 80% of the window
when Arena does not say), or would not fit at all, the older
part of the conversation is summarized by the model (the person's goals,
decisions, files, commands and the state of the work) and the recent part
kept whole, within the **target**, 25% of the window by default. `/compact`
does it at once. The summary is written in sections (`## Goal`,
`## Constraints and preferences`, `## Decisions`, `## Files`, `## Commands`,
`## Errors met`, `## State`, `## Next steps`), and the plan as it stands
is said after it.

What fills the window is managed before anything is summarized:

- **A big result is kept aside.** A tool's result past 16,000 characters (less
  for a small window) is kept in a file beside the session's
  (`sessions/ID.results/r1.txt`, 0600) and the model reads its start and its
  end, with a line saying how much is not shown and how to read it:
  `read_result {"id":"r1","from":81,"lines":200}` reads on, and
  `{"id":"r1","find":"text"}` gives the lines that hold some text. A
  sub-agent's results, with no session file of their own, are cut in the
  middle as before (past 40,000 characters).
- **Earlier results are cleared before compacting.** When the next request
  would pass the threshold, the results older than the person's last two
  messages (past 1,000 characters) are kept aside the same way and replaced
  by a line ("Cleared to make room in the window: read_file's result (10,950
  characters) is kept as r3. read_result {"id":"r3"} reads it again."). When
  that is enough, nothing is summarized: "Cleared N earlier tool results
  (~T tokens) to make room".
- **A long session finds what it said.** Every message stays in the
  session's file, compactions aside: `session_search` finds the ones that
  hold some words (with their numbers, #N, their place in the session from
  1), and `session_read` reads them again word for word, up to 20 at once. The
  summary is asked with the numbers of the messages it covers (and the
  person's first words in each), cites them, and ends with `## Earlier parts`
  (a line for each stretch of the session); after it, the model is told the
  messages are kept and how to read them. `search_sessions` searches the
  other sessions on this machine; `search_history` asks Arena for the
  person's past chats and sessions everywhere (the web chat's too), by what
  they were about (each one's digest), and `open_chat` reads one.
- **`/context` shows what fills it**: the instructions, the memory (the
  prompt's and what was recalled for the questions), the tools, Code Arena's
  notes, the summary, your messages, the answers and the tools' results, each
  as a bar with its tokens, then the five biggest messages. The IDE's
  **Context** (the window's use in the status bar) lists the same, from
  `GET /api/window`, measured while no turn runs.

A long session never gets stuck on the window:

- **The summary is asked as the conversation goes on**: the same system
  prompt, tools and older messages the model has read (so the engine reads
  them from its cache), then a request to summarize them, within what the
  window holds (the oldest messages left out past it, an earlier summary
  kept).
- **A summary that cannot be made is not fatal**: the older messages are left
  out instead, with a note of what they held (the summary before them, the
  person's messages, the files changed, the commands run, the last answer),
  long tool results and file contents the model wrote in older steps are cut
  to their start, and the turn goes on. A summary is tried again only after 2
  compactions (then 4, then 8) while it keeps failing; `/compact` always tries
  it, and leaves the conversation as it was when it fails.
- **One long turn compacts itself**: when nothing before the turn is left to
  summarize, the turn's earlier steps are (the person's message and the
  latest steps kept whole). A compaction that would change nothing writes
  nothing and does not reset the IDE's page.
- **A request that would not fit is compacted first**, whatever the
  threshold (and with it off): "Compacting: the next request (~N tokens) would
  not fit the window (M) with room for the answer."
- **A request the window refuses** (the engine's or LiteLLM's "exceeds the
  context size", `ContextWindowExceededError`, "maximum context length") is
  compacted at once and asked again, once. If it still does not fit, the turn
  ends with that, and the message is taken out of the conversation so it does
  not pile up. llama.cpp's "Context size has been exceeded." says the cache
  its requests share was full (other requests filled it): the request is
  asked again after two seconds, without compacting, and compacted only if
  the engine refuses it again. Code Arena and the web chat read these refusals
  with the same code (`ContextOverflow`, in Llm.Core, linked into Code Arena).
- **The size is measured, not guessed**: the characters a token holds are
  learned from the gateway's counts (and from the size a refusal gives); a
  resumed session starts from its last count. The answer's room (the model's
  longest answer as the gateway says, 4,096 tokens otherwise, at most an
  eighth of the window) stays free below the window, whatever the threshold.
- **The start of every request stays the same**, so the engine does not read
  the whole conversation again after a change: the same tools in every mode,
  MCP servers' tools in the order they came (a new server's at the end), and
  what changes during a session (the mode, the date, the branch, whether
  Arena's tools are connected, the servers' instructions) in a short note of
  Code Arena's added to the conversation where it changed, never in the
  system prompt. The notes are not saved in the session nor shown on the page.

Set them with `/compact-at 70` (the threshold), `/compact-at 70 30` (and the
target) or `/compact-at off` (only when a request would not fit), in the IDE's
**Context** (the window's use in the status bar: at a share, only when a
request would not fit, or as the Arena account says), or `"compactAt"` and
`"compactTarget"` in `config.json`: `/compact-at` and the IDE keep them there
for the next sessions. `/compact-at account` (the IDE's **As my Arena account
says**) takes the threshold out of `config.json` again, so it follows the
account, and `/compact-at default` does that and puts the target back to 25.
`--compact-at 70 --compact-to 30` sets them for one run. Which wins: the flag,
then `config.json`, then the Arena account (read at the start from
`GET /api/code-arena/preferences` with the API key: the person's own choice,
else Arena's default), then 80%. `/compact-at`, `/context` and the IDE say where
the threshold comes from ("from your Arena account", "Arena's default", "set in
config.json", "set with --compact-at"). A share is written `70`, `70%` or `0.7`
(in `config.json` too: `70`, `"70%"`, `0.7`, or `0` and `"off"`; anything else
stops the session with where it is). The threshold is from 20% to 95%, or off,
and the target from 5% to 10 points under the threshold (85% when off; a lower
threshold brings the target down with it), so a compaction does not start the
next one. The use of the window shows after each turn in the terminal
(`/context` at any time) and in the IDE's status bar and chat gauge, which marks
the threshold and says whose it is.

## What it remembers, and what came before

**Memory.** Signed in to Arena, Code Arena's memory is Arena's, the web
chat's too (**Workspace → Memory** in Arena lists and edits it). The model
keeps what a later session should know with its `remember` tool, with its
kind (how to build and test the project, a convention, a decision and why, a
lesson from something that went wrong, or for every project the person's
preferences): a project's memory is kept by the project's key, its
repository as its `origin` remote names it (`gitlab.corp/payments/api`, the
same on every machine and clone; a folder with no remote has a key of its
own on this machine), the person's for every project. A session starts with
Arena's block (the pinned memories, the company's and the newest, about 400
tokens); each message then brings the memories that matter to it, in Code
Arena's note before the message (never synced into the web chat), each said
once in a session. `recall` searches all of memory when the model needs
more. When a session has been quiet for ten minutes, Arena reads what it said
(the session is kept in step with a chat there) and keeps what is worth
remembering about the project (a build command that worked, a convention, a
lesson); what it learned about the person is proposed in Arena's **Memory**
first, as in the web chat. `/memory` lists the person's, the project's and the company's,
numbered; `/memory add [--all] TEXT` and `/memory forget N` change them.

Without Arena (not signed in, or it did not answer as the session started),
memory is the files on this machine: the project's in the data folder (by
the project's path, never in the repository), the person's `memory.md` in
the config folder, each memory a line the person may edit; the session's
prompt carries both, and the block Arena gave last time. What is kept
meanwhile goes to Arena when Code Arena next reaches it (once: the files are
kept beside as `.imported`).

**The project's long memory: `.remember/`.** Beside Arena's memories (short
facts, found by relevance), a project keeps a long memory of its own in the
repository: `.remember/MEMORY.md`, what the project is, how it builds, tests
and runs, its conventions, the decisions and why, where the work stands and
what is next, and the lessons; with topic files beside it
(`.remember/architecture.md`, `.remember/db/migrations.md`) for the details.
Every session reads `MEMORY.md` at its start (up to 24,000 characters; past
that, its start and a note to read the rest and move details into topic
files), and the topic files are listed with their first lines for the
model to read when they matter. The model keeps it current as it works with
`remember_project` (a section of `MEMORY.md` or of a topic file, added to,
or replaced when it is out of date; the file and the section made when they
are not there yet), without asking (it writes only inside `.remember/`, and
a checkpoint keeps what it was for `/rewind`). With none yet, the session is
told how to start it. It lives at the repository's root, so a session in a
subfolder shares it; commit it to share it with the team (or add it to
`.gitignore`). `/remember` shows its sections and topic files; the IDE's
**Memory** view opens it. `"projectMemory": false` in `config.json` turns
it off.

**It learns skills as it works.** `skill_save` keeps a way of doing a task
the session worked out (how to release here, how to add an endpoint, how to
debug this kind of failure) as a skill, to do it the same way next time: for
the project, `.arena/skills/NAME/SKILL.md` in the repository (a better
version replaces it; commit it to share it), asking first as any edit does
in the ask mode; or for the person (`"scope": "person"`), one of their
skills in Arena, used in every project and the web chat, waiting on Arena's
Skills page unless they keep what is learned on their own (one of theirs of
that name gets a better version instead). Sessions read skills as they
start: one saved is used from the next. After a turn of 8 steps or more,
Code Arena's note reminds the model to keep what it worked out (a skill, or
the project's memory). Arena also learns skills from the sessions it keeps
in step with its chats, as it does from the web chat.

**The IDE's Memory view** (the brain in the activity bar) lists what Arena
remembers for this project, the person and the company, finds by words,
keeps one (for this project, or for every one) and forgets one of the
person's.

**Earlier sessions.** The model's `search_sessions` tool finds what was said
in this folder's earlier sessions (every folder's when asked): messages that
hold every word searched for. `/search WORDS` does the same for the person.

**Instructions of other agents.** In each folder read for instructions, the
first of `ARENA.md`, `AGENTS.md`, `CLAUDE.md` and `QWEN.md` counts, so a
project written for another agent is read as it is.

**@files.** `@src/parser.cs` in a message sends the file with it, its lines
numbered as `read_file` numbers them (a folder sends its list; `@"a b.txt"`
for a name with spaces); `@src/parser.cs:40-60` (or `#L40-L60`) sends those
lines with three around them. Up to 60,000 characters a file and 150,000 a
message, cut in the middle beyond. Anything that is not a file the tools may
read (an email address, `@Override`) stays as typed. What goes with a message
comes after what was typed: the IDE shows the question as typed, with a chip
for each file or lines that went with it (a click opens them).

**Checkpoints.** Each turn leaves a checkpoint while Code Arena runs (the last
50): how long the conversation was, and what each file the turn's tools wrote
held before. `/rewind` lists them; `/rewind N` goes back to before turn N: the
files put back (a file a turn made is removed) and the conversation cut there;
`/rewind N code` puts back only the files, `/rewind N chat` only cuts the
conversation. What commands changed is not put back (git has it). With the
turn's commits on, the files put back are committed as Code Arena's
("Rewind: back to before ..."). The chat in Arena keeps what was said.

## Your own commands, sub-agents and skills

Markdown files, the project's under `.arena/` at its root and the person's in
the config folder (the project's win over the person's of the same name);
those written for Claude Code (`.claude/`) and Qwen Code (`.qwen/`) are read
too. `/commands` lists what was found.

- **Commands**: `commands/NAME.md` is `/NAME` (`commands/git/commit.md` is
  `/git:commit`). The file is the prompt sent: `$ARGUMENTS` is what was typed
  after the command, `$1`, `$2` its words; a prompt with neither gets what was
  typed after it. Front matter (`---` lines) may give a `description` and an
  `argument-hint`. In the IDE's chat too.
- **Sub-agents**: `agents/NAME.md`, its front matter's `name`, `description`,
  `tools` (which of the reading tools it may use) and `model`; the file is its
  instructions. The model hands it a task by name (`task` with `agent`). Like
  every sub-agent it reads only: it cannot edit files or run commands.
- **Skills**: `skills/NAME/SKILL.md` (agentskills.io's format: `name` and
  `description` in its front matter). The prompt lists each skill's name and
  what it is for; the model loads one with its `skill` tool when the task needs
  it, and the files beside it one by one.
- **Skills from Arena's plugins**: the skills the plugins you may use bring
  (Admin → Plugins), kept in the config folder's `plugin-skills/` and written
  anew at each start when Arena answers (kept as they were when it does not).
  The project's and your own skills of the same name win over them.
- **Arena's own skills**: the skills people use most come built into Code
  Arena, the chat's own too (`code-review`, `write-tests`, `debug`,
  `refactor`, `plan-change`, `commit-message`, `mr-description` and the
  rest; see [chat.md](chat.md#skills-and-slash-commands)): written to the
  config folder's `builtin-skills/` at each start and read last, so the
  project's, your own and the plugins' of the same name win. One an Arena
  admin turned off is left out. A loaded skill names its parameters
  (`‹what›`) and says what each is, for the model to take from the request.

## Kept rules and the sandbox

**Rules.** `/permissions allow RULE` lets calls run without asking, `deny RULE`
refuses them whatever the mode; both are kept in `config.json` (`"permissions":
{"allow": [...], "deny": [...]}`). A rule is a tool's name, with a pattern in
brackets matched against its command or path (`*` is anything):
`run_shell(npm test*)`, `edit_file`, `read_file(.env*)`, `mcp__tickets__*`. A
command is matched whole and part by part, so `run_shell(rm *)` refuses
`make && rm -rf build` too. A project's `.arena/settings.json` may deny (the
same shape) but never allow: a repository someone cloned cannot let itself run
commands unasked. A command Laya flags still asks.

**Sandbox.** The agent's commands (`run_shell`, those with no time limit, and
`!command`) run in a sandbox: bubblewrap on Linux, `sandbox-exec` on macOS.
They read everything but what holds secrets (`~/.ssh`, `~/.gnupg`, cloud and
Kubernetes credentials, `.netrc`, `.npmrc`, browsers' profiles, Code Arena's
own config and sessions), which they cannot see; they write only in the
working directory, the folders allowed besides it, the build tools' caches
(`~/.cache`, `~/.npm`, `~/.nuget`, ...) and a `/tmp` of their own; Docker's and
Podman's sockets are out of reach. `"sandbox"` in `config.json`: `"auto"` (as
installed: a sandbox when the system has one, else commands run as you and the
session says why), `"on"` (refuse to start without one), `"off"`.
`"sandboxNetwork": false` takes the network away from commands;
`"sandboxWritable": [...]` adds folders they may write. Windows has no sandbox
Code Arena can use. The IDE's terminals are the person's own: no sandbox.
`/sandbox` says what applies.

## Chats in Arena

Each session is a chat in Arena too, kept in step both ways with your API key:
in the chat list it has a terminal mark, and its page says it is a Code Arena
session and which folder it runs in. It can be continued on the web, where the
chat's own tools answer (the files and commands stay on the machine Code Arena
runs on). What was added there comes into the session within seconds while the
session waits (the terminal's prompt says so above what is being typed; the
IDE's thread shows it), and otherwise at its next turn here, before the
question, so the model hears it (a turn waits a few seconds at most for Arena
to say: when it is slow, the web's news comes in at the turn after; while the
web chat is answering, its messages come when the answer ends). The terminal
and the IDE ask Arena every 5 seconds with the chat's stamp (its last message):
a chat that did not change is not read again. What a session adds
shows on the web page of its chat within seconds too, and new chats come into
the web's list within half a minute.

`/web` lists your 30 newest chats in Arena, `/web all` the 5,000 newest (read
200 at a time) and `/web WORDS` those of them with the words in their title or
folder; `/web N` (a number in the list last shown, or `code-arena chat --web
N`, or a chat's id) continues one here: in this folder's session already kept
with it, with what was added since, else in a new session with its messages.
An id Arena has no chat of yours for is said so, and the session stays as it
is. What this session adds goes back to it. In the IDE, the
side bar's **Chats in Arena** lists them (with a box to find one), below this
folder's sessions; a session kept with a chat has a cloud mark.

Messages are sent in the background as they are added, in order, and again
until Arena has them: a session goes on while Arena is out of reach, and what
it missed is sent when Arena answers (at the latest when the session is next
opened). Each message carries its place in the session, so one sent twice
(its answer lost) is never kept twice. A chat deleted in Arena ends the sync
for that session, which goes on here.

The session keeps Arena's id of each message, and both sides go by those ids:
the session reads what follows the last message both have, and adds after it.
So what the web changes is followed here: a question edited there, an answer
written again, or another branch shown makes the session's conversation the
web's from where the two part (said in the thread: "The web chat's
conversation changed"), and what the session sends next goes on after it.
`/rewind` here works the other way: the turns gone back from stay on the web as
a branch, and what the session adds goes on a branch of its own there, shown
from then on. When the web moved to another branch while the session's
messages were still on their way, those go on a branch of their own too; when
the web only added messages meanwhile, they come before the session's in
both places (a tool's result always stays straight after its call). A long
chat comes in a page of 200 messages at a time.

A compaction here is sent too: Arena keeps its summary where the web's own
compaction keeps one (on the last message it covers), so the web's next answer
reads it instead of those messages. The other way, a chat compacted on the
web is read here as its summary and what follows it. Every message on the
branch counts, on both sides and in the lists: an answer the model gave with
nothing in it is not kept here (the thread says the model said nothing), and
one that failed on the web with nothing in it is read here as "(no answer)".

While a turn runs here, the chat is the session's: Arena holds it for the turn
(renewed every 30 seconds, and let go once Arena has the turn's messages, or
after 90 seconds without a word, when Code Arena stopped). Meanwhile the web
page says **Code Arena is answering in this chat**, and what the person sends
there waits in the chat's queue, then goes (and is answered on the web) when
the turn ends. A turn that starts while the web chat is answering waits for
that answer first (two minutes at most; Stop goes on without it). `/sync` says where the session stands
and whose chats they are: the sessions go to the chats of the account whose
key `code-arena login` was given (login says which; the IDE shows it over
**Chats in Arena**), so a key of another account (a test one) keeps them
there, not with your own chats.
`"syncChats": false` in `config.json` keeps sessions on this machine only;
`/sync all` (the IDE's **Send the other sessions here**) later sends Arena what
it lacks of this folder's sessions (those it never had, and those sent in
part), the oldest first, each keeping its place in the list here; one whose
chat was deleted there stays as it is. When Arena cannot be reached it stops,
and says how many are left for the next `/sync all`. The terminal's chat and
the IDE also do this by themselves, in the background, ten seconds after they
start: what Arena lacks of every folder's sessions goes then (the session open
now is left to its own sync), and one notice says how many went. The IDE marks
a session Arena lacks part of as **not sent**.

Nothing is lost on the way. The web shows each answer with its thinking (kept
in the session, never sent back to the model), its model, tokens and time; a
tool's result with its tool; an answer stopped here as stopped; Code Arena's
own note to the model (the connection dropped) as a notice, not as your words;
and what went with a question (`@files`, lines chosen in the IDE's editor) as
what you typed plus the files as chips. A question that quotes part of an
answer (Reply, in either chat) carries its quote both ways as a field of its
own: the web shows it above the question, and the session keeps it before the
question, as its model reads it. Continued on the web, the chat answers
with the model Code Arena answered with last. The other way, what the web
attached comes with its messages: a text file's text as the web's model reads
it, a picture as a picture to a model that sees (fetched with your key; named
otherwise), a sound's or a video's transcript; the web's tool results come
marked as data where the web marks them for its own model (web pages, the
company's documents). An archived chat continued from Code Arena (`/web N`,
`--web`, the IDE's **Chats in Arena**) comes back to the web's list once; what
the session adds later leaves it as you left it.

The IDE's chat shows where the sync stands in its header (in step, how many
messages are still to send, the web's news waiting, or what stops it: its
words on hover), and what was said while nothing ran (Arena out of reach, the
web's messages taken in) comes as a notice there, not only in the terminal that
started it.

## Its commits

In a git repository, each turn that ends commits the files it changed, with
Code Arena as the commit's **author** and the person (their own git settings)
as the **committer**, so `git log --format='%an | %cn %s'` tells what the
harness wrote from what the person did. The commit's first line is the
request's first line; its body quotes the request and names the model and the
session (`Code-Arena-Model:`, `Code-Arena-Session:`). A `git commit` the model
runs itself has Code Arena as its author too.

Only what the turn changed goes in. A file that already held the person's own
uncommitted changes when the turn began is left for them (the turn says
which), and so is whatever else they had changed or staged. A turn that is
stopped or fails leaves its changes uncommitted, and the next turn that ends
commits them. Nothing is committed during a merge, rebase, cherry-pick or
revert. The repository's hooks run as for any commit; one that refuses, or one
that waits more than two minutes, leaves the changes uncommitted, and the turn
says why; after two seconds the turn says it is committing, and Ctrl+C or
**Stop** skips the commit (git is told to stop, so it removes its lock). A turn
whose commands with no time limit still run leaves its changes uncommitted
until they end: the turn that carries on then commits them.

`"autoCommit": false` in `config.json` turns the turn's commits off;
`"commitName"` and `"commitEmail"` change the author (Code Arena,
`code-arena@` the Arena's host, as installed).

## The person's own MCP servers

Under `"mcpServers"` in `config.json`, in the shape Claude Code and others use:

```json
{
  "url": "https://llm.example.com",
  "apiKey": "…written by code-arena login…",
  "mcpServers": {
    "files": { "command": "npx", "args": ["-y", "@modelcontextprotocol/server-filesystem", "/srv/docs"] },
    "tickets": { "url": "https://tickets.example.com/mcp", "headers": { "Authorization": "Bearer ${TICKETS_TOKEN}" }, "trust": true }
  }
}
```

A `command` runs as a child process speaking MCP on stdin and stdout (`args`,
`env`, `cwd`); a `url` is streamable HTTP (`headers`). `${NAME}` in `env` and
`headers` is taken from the environment, so tokens stay out of the file. Their
tools are named `mcp__<server>__<tool>` and ask before each call unless the
server has `"trust": true` (or the mode is `yolo`); in `plan` mode only those
the server marks read-only run (the others are refused when called). `"disabled": true` keeps a server
without starting it. On Windows, `npx` and other `.cmd` scripts need
`"command": "cmd", "args": ["/c", "npx", …]`.

The rest of the file: `"model"`, `"thinking"`, `"mode"`, `"context"`,
`"shell"` (the agent's `run_shell`), `"terminalShell"` (the IDE's terminals),
`"allowedPaths"`, `"arenaTools": false` (no Arena MCP), `"argusTools": false`
(no Argus MCP), `"toolChars"` (tools on demand, above), `"argusUrl"`, `"compactAt"`, `"compactTarget"`, `"gateway"`,
`"mcpUrl"`, `"ca"`, `"autoCommit"`, `"planCritique"`, `"python"`, `"toolMode"`, `"commitName"`, `"commitEmail"`, `"syncChats"`, `"window"`, `"completion"`, `"completionModel"`, `"completionTemplate"`, `"checkCommand"`, `"sandbox"`,
`"sandboxNetwork"`, `"sandboxWritable"`, `"permissions"`, `"formatters"`, `"debugAdapters"` and
`"languageServers"` (the IDE's: above). `ARENA_ARGUS_URL` in the environment gives Argus's address
for one run.

## Building it (admins)

The app's image carries the builds and serves them on Connect your tools. They
are built when the image is (`src/Llm.Api/Dockerfile`, the `code-arena`
stage), from .NET's runtime packs, which are not part of the SDK:

- **The deploy**: `tools/fetch-code-arena-packs.sh` first (it asks the SDK
  which runtime it publishes with and fetches the missing packs from
  nuget.org into `tools/offline-nuget/`, about 220 MB each .NET patch;
  `--prune` drops older ones), then `docker compose build app`. When the
  packs there are not at the SDK's version, the image publishes with
  nuget.org's instead, and skips Code Arena only when neither is to be had.
  `deploy/scripts/make-installer.sh` packs Code Arena's packages of the
  release, making them first when `dist/` has none.
- **A host with internet**: `docker compose build app --build-arg CODE_ARENA=online`
  (or `tools/publish-code-arena.sh`) fetches them from nuget.org.
- **A host without**: fill `tools/offline-nuget/` once from a machine with
  internet (its [README](../tools/offline-nuget/README.md) has the commands,
  about 220 MB), carry it over with the repository, and build as usual: the
  image builds Code Arena from that folder only (`CODE_ARENA=auto`, the
  default). The packs must be at the runtime version the SDK publishes with,
  the SDK image's own runtime (`dotnet msbuild src/CodeArena/CodeArena.csproj
  -getProperty:BundledNETCoreAppPackageVersion` prints it; a newer `sdk:10.0`
  pulled since moves it).
  Without them, the image tries nuget.org's; with neither, it skips Code
  Arena, the build log lists the files to fetch at that version, and the page
  says how to add it.
- **By hand**: `tools/publish-code-arena.sh --offline [rid ...]` writes
  `dist/code-arena/<rid>/code-arena` and `SHA256SUMS`, with `dotnet` or
  `tools/dn`. It checks the packs first: when one is missing for a system
  asked for, it lists them at the version to fetch, builds nothing and exits
  with 3. Copy the files to people any way you like.
- **Packages for a release**: `tools/package-code-arena.sh [rid ...]` runs the
  publish script (offline, from `tools/offline-nuget`; `--online` fetches the
  packs instead), then packs each system as
  `dist/code-arena-<version>-<rid>.tar.gz` (`.zip` for `win-x64`): a folder
  with the program, `README.txt`, `LICENSE.md` and `LICENSING.md`. It writes
  `dist/SHA256SUMS` for every package of the version (`VERSION`'s) in `dist/`,
  which git ignores. The packages carry no owner, and the last commit's time
  on every file. The macOS builds are signed ad hoc by the SDK.

The web interface's page is built first, from `src/web`
(`vite.code-arena.config.ts`, then the chat's diagram runner) into
`src/CodeArena/web`, and embedded in each file whole. The script builds it
with Node.js and `src/web/node_modules` (`npm ci` there once), downloading
nothing; `--no-web` leaves it out. The image builds it in a stage of its own,
`code-arena-web`, whose first steps are `src/web`'s own image's: a host that
built the web image has its `npm ci` cached. `CODE_ARENA=none` skips it too.

`CODE_ARENA=none` leaves it out of the image (about 210 MB for the five
systems). Trimming is off: it would need a package the offline folder does not
hold.

## When something is wrong

Code Arena carries on by itself where it can. A gateway that does not answer
(502, 503, 504, a lost connection: the Arena restarting, a model loading) is
tried again five times over about a minute and a half, and the session says so
each time. An answer that has not started after a minute says so (the engine
busy with others, or reading a long prompt; Ctrl+C or **Stop** ends the wait).
One that does not start within 15 minutes, or that stops for 3 minutes, is
given up and asked once more (the gateway's own timeout, 408, counts as such a
stall); a second such stall ends the turn with the reason, and the next
message is sent as usual. Once the engine's reading speed is measured (the
time to the first word of answers whose prompt it had not cached), a request
with much it has not cached says first about how long the answer may take to
start, and waits half as long again as that before giving up (an hour at
most). The gateway waits 16 minutes (`timeout` and `stream_timeout` in
`config/litellm.yaml`), so Code Arena, not the gateway, gives up on a long
prompt being read within its first 15 minutes. Its window is the model's at
the gateway: what one of the engine's slots holds (Admin → Models, "65,536 a
conversation"), so it compacts before a slot overflows. When
the connection drops after part of an answer arrived (a stream that ends
without its end counts too), that part is kept and the model is asked to carry
on from where it stopped, up to three times a turn.


- **"The certificate of … is not trusted"**: the Arena's certificate comes
  from a CA the machine does not know. Give the CA's file with `--ca` (above).
- **"The name in … does not resolve" / "did not answer in time"**: check the
  address, VPN and DNS. Behind a proxy, set `HTTPS_PROXY` (and `NO_PROXY` for
  the Arena when it is inside the network); on Windows the system's proxy is
  used too.
- **"The gateway refused your API key (401)"**: the key was revoked or mistyped.
  Make a new one under **Your account → API key** and run `code-arena login`.
- **"Your API key reached its limit of … a minute"**: an admin gave your key a
  rate limit (requests or tokens a minute). Wait a minute and ask again; **Your
  account → API key** shows the limits and what you used. Code Arena does not
  ask again by itself: the minute is not over in a few seconds, and the
  gateway counts each try. For tokens a minute, the gateway counts a request's
  prompt and the answer it asks for before it runs, so a request bigger than
  the limit is refused every minute: `/compact` makes the conversation smaller,
  or ask an admin to raise the limit. **"… requests at once"**: more answers at
  once than your key may have (sub-agents count). Code Arena tries again (as
  above) before it says so.
- **"… has no OpenAI API (404)"**: the gateway is not at `gateway.DOMAIN`. Give
  it: `code-arena login --gateway https://…`.
- **"Arena's tools are not available here"**: this Arena has no MCP endpoint
  (an older version), or a proxy in between does not pass `/mcp`. The session
  works with the local tools.
- **"Arena's tools are still connecting"** or **"Argus's tools did not
  connect"**: the server is slow or down. The session carries on without its
  tools and tries again; `/mcp` says why and `/mcp retry` tries now. A refusal
  gives the server's reason: for Argus, **"refused the credentials (401): …"**
  names the key (make a new one and `code-arena login`) or says GitLab cannot
  tell what you may read just now (Argus older than 5.3 refuses then; it
  connects now and only its code tools wait for GitLab).
- **An edit "was not found"**: the file changed since the model read it, or the
  model got the whitespace wrong. It reads the file again and retries.
- **The window fills fast**: large tool outputs. `/cost` shows how full it is,
  `/compact` frees it, and sub-agents keep research out of the main context.
- **Colours look wrong**: `--no-color` or `NO_COLOR=1`; output to a file or a
  pipe has none.
- **A terminal does not open**: the shell named by `"terminalShell"` is not
  there (give its full path), or, on Windows, the system is older than
  Windows 10 1809 (no ConPTY).
- **The IDE's address says the key is wrong**: it is an old run's. Each run
  prints a new address; open that one.
- **The browser says it cannot read `code-arena-PORT.html`**: it runs in a
  sandbox (a snap, as Ubuntu's Firefox and Chromium are, or a Flatpak), which
  cannot read code-arena's data folder. Open the address code-arena printed.
