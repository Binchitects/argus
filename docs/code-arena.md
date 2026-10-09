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

**Your account → Connect your tools → Code Arena (our own agent)** offers a
download for each system the Arena has a build of. Or from a terminal, with no
browser (the downloads need no sign-in; the program does nothing without a key):

```bash
curl -fLo code-arena https://DOMAIN/api/downloads/code-arena/linux-x64
chmod +x code-arena && mkdir -p ~/.local/bin && mv code-arena ~/.local/bin/
```

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
window is (`context 18.2k / 131k (14%), compacts at 80%`). **Ctrl+C** stops the turn and
keeps what was said; at the prompt it clears what is typed, and on an empty
prompt twice leaves. **Ctrl+Z** at the prompt stops code-arena, as in a shell:
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
| `/thinking [level]` | `default`, `off`, `low`, `medium`, `high`, `xhigh` (sent as the chat sends it, through `chat_template_kwargs`) |
| `/tools` | the tools of the session, and which ask |
| `/todo` | the to-do list |
| `/compact` | summarize the conversation now |
| `/compact-at [% [%]]` | show or set when the session compacts itself and what it keeps (below); `default` for 80 and 25 |
| `/context` | how full the model's window is |
| `/cost` | tokens spent, and how full the model's window is |
| `/mcp [retry [name]]` | Arena's, Argus's and your MCP servers: connected or not and why; `retry` tries those not connected now |
| `/jobs [stop N]` | the commands run with no time limit, running or ended; `stop N` stops job N |
| `/clear` | a new session (the last stays saved) |
| `/resume [id]` | switch to a saved session |
| `/exit` | leave (also Ctrl+D) |

## The IDE

```bash
cd my-project
code-arena                          # or: code-arena web
code-arena --port 8765 --no-open
code-arena --continue --mode auto-edit
```

`code-arena` starts a small web server on this machine, prints its address
and opens the browser (`--no-open`, or a machine without a desktop, only
prints it), through a file of the person's own that sends it on to the
address (below). **Ctrl+C** in the terminal stops it, and the turn if one runs;
the terminal shows what the agent does (`● edit_file …`) while the page drives
it. On another machine over SSH: `code-arena --port 8765 --no-open` there,
`ssh -L 8765:127.0.0.1:8765 that-machine` here, and open the address printed.

The page is laid out as VS Code is, in Argus Arena's design system:

- **activity bar** (the left edge): Explorer, Search, Agent changes (with a
  count of the files) and Chat switch the side bar; the one shown hides it.
  At its foot: the terminal panel, the theme (light, dark or the system's),
  Help and About.
- **help**: Help in the activity bar opens the IDE's help over the page:
  what each part does, the common tasks step by step and the keys, with a
  link to this page of the manual in the Arena signed in to
  (`https://DOMAIN/help/code-arena`). The **?** in a panel's header
  (Explorer, Search, Agent changes, Sessions, the terminal, the chat) opens it
  at that panel. Esc closes it.
- **Explorer**: the project's files, with New file, New folder, Refresh and
  Collapse at its top. Click opens; a right-click menu has new file, new
  folder, Copy path, Rename (F2) and Delete (Del, asks first); the arrow
  keys move through the tree. The files the agent changed are marked, and so
  are the folders they are in. A file deleted with unsaved changes keeps its
  tab (the question says so): saving it makes the file again.
- **editor**: tabs with Monaco, highlighting by the file's name, the theme
  following the page's. A dot marks unsaved changes and **Ctrl+S** saves. A
  file changed on disk since it was opened is not written over unseen: saving
  asks first. Closing a tab with unsaved changes asks Save, Don't save or
  Cancel; leaving the page with any warns. A binary file, or one over 5 MB,
  says so instead of opening. When code-arena stops answering (it stopped, or
  the SSH tunnel dropped), a line at the top says so and the editor stays, so
  what is not saved can still be copied out; it goes when it answers again.
- **the agent's edits**: a file the agent edits reloads in its tab when nothing
  in it is unsaved (undo still works), and after each turn every open file is
  checked against the disk. What is typed while a file reloads stays, unsaved:
  saving it then asks first.
- **diff**: Monaco's diff editor for each file the agent changed in this run,
  as it was before the agent's first change and now, opened from Agent
  changes or the Explorer. Its bar has the counts, Open file, **Revert** (puts
  the file back; a file the agent made is deleted) and **Accept** (keeps it);
  Agent changes also has **Accept all**.
- **search**: text across the files git sees (`.gitignore` holds), with match
  case, whole word, regular expression, and files to include or exclude; a
  result opens the file at the match. **Ctrl+P** opens any file by a few
  letters of its path.
- **terminals**: a panel under the editor (Ctrl+`), one tab per shell (bash 1,
  bash 2, …), + for another; drag its top edge to resize it. Shown with none,
  it opens one; closing the last one hides it. A page reloaded attaches to the
  ones still running, with their screens (below).
- **chat**: the agent's chat on the right: the model and thinking pickers,
  Sessions and Hide at its top; the thread with a card for each tool call and
  a diff under each edit, the approval card (**Allow**, **Always for this
  session**, **Deny**), Stop, the mode, `/compact` and `/clear`. A command
  the agent runs with no time limit shows above the box while it runs: the
  end of its output as it comes, how it ended, and **Stop**. **↑** in its box brings back this session's messages, then
  what this folder sent before (in the terminal too), as in Arena's chat;
  **↓** and **Esc** go back to what you were typing. The Chat activity lists
  this folder's sessions.
- **status bar**: the git branch, the agent's changes, the terminal and the
  number of commands running with no time limit on the left; the cursor's
  line and column, the file's language, the MCP servers (connected of all:
  **MCP servers** lists them, why one is not connected and when it is tried
  next, with **Try again**), how full the model's window is (tokens used /
  the model's: **Context** sets when the session compacts and what it keeps,
  kept in `config.json`), the mode, the model and `code-arena <version>`
  (About: the version, the licence, the folder and the Source link) on the
  right.

The side bar, the chat and the terminal panel are resized by dragging their
edges (or with the arrow keys on the edge). Their sizes, which are shown and
the theme are kept by code-arena, in `ide.json` in its data folder: every run
opens as the last one was left, in any project and whatever its port (a
browser keeps a page's own storage per port, and each run takes a new one).
The keys work wherever the focus is, the terminal too. On macOS they are ⌘:
Ctrl+S, Ctrl+P and the others stay the terminal's and the editor's there (a
shell's history, emacs, nano).

| keys | |
|---|---|
| Ctrl+S (⌘S) | save the file |
| Ctrl+P (⌘P) | open a file by name |
| Ctrl+Shift+F (⌘⇧F) | search |
| Ctrl+Shift+E (⌘⇧E) | the Explorer |
| Ctrl+` | show or hide the terminals |

Everything the page needs (Monaco and its language workers, xterm.js, the
fonts) is built into the program; nothing is fetched from anywhere else.
Monaco loads with the first file opened, xterm.js with the first terminal.

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
  read (`ps`, `/proc`): the browser is opened with a file,
  `open/code-arena-PORT.html` in code-arena's data folder (0600, in a folder
  of the person's alone, 0700), which sends it on to the address, as Jupyter
  does. The file goes when the run ends. A browser in a sandbox (a snap, as
  Ubuntu's Firefox is, or a Flatpak) cannot read it: when no browser has the
  page, cookie and all, 20 s later, code-arena says so, and the address it
  printed opens the IDE there.
- The server answers only to `127.0.0.1` and `localhost` at its port (the Host
  header), so a name pointed at 127.0.0.1 (DNS rebinding) gets nothing; it
  refuses requests from other sites, or another port's page (Origin,
  Sec-Fetch-Site), takes only JSON, and its page cannot be framed. Every call,
  the file calls and the terminals' WebSocket included, passes these checks.
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
| `GET /api/state` | `name`, `version`, `license`, `source` (the about box), `manual` (the help's link: the Arena's `/help/code-arena`, null without its address), the folder, project, branch, model, mode, session, busy; `context` (the window), `contextUsed` (about how much of it is in use), `compactAt` and `compactTarget` (percent); `servers` (`[{name, title, url, state, tools, status, error, nextTry}]`, `state` one of `connecting`, `connected`, `unavailable`, `failed`); `jobs` (`[{id, command, running, status}]`) |
| `POST /api/servers/retry` `{name}` | tries that MCP server again now (every one not connected without `name`); the state |
| `POST /api/jobs/stop` `{id}` | stops a command run with no time limit; 404 for no such job |
| `GET /api/files?path=DIR` | a folder's entries, folders first: `{path, entries: [{name, path, kind, size, link}]}` |
| `GET /api/files/all` | every file, for quick open: `{files, truncated}` (50,000 at most) |
| `GET /api/file?path=FILE` | `{path, size, version, text}`; `text` is null with `binary` or `tooLarge` (over 5 MB) |
| `POST /api/file` `{path, text, version}` | saves; 409 `changed` when the file changed since `version`; `{path, version, size}` |
| `POST /api/files/new` `{path, kind}` | a new file, or a folder with `kind: "dir"`; 409 `exists` |
| `POST /api/files/rename` `{from, to}` | moves a file or folder; `{from, to}` |
| `POST /api/files/delete` `{path}` | a file, or a folder with what is in it (a link, not what it points at) |
| `GET /api/search?q=…&regex=1&case=1&word=1&include=…&exclude=…` | `{files: [{path, matches: [{line, column, length, preview, start}]}], count, truncated}` (2,000 matches, 20 s at most) |
| `GET /api/changes` | the agent's changed files: `[{path, created, deleted, added, removed}]` |
| `GET /api/changes/diff?path=FILE` | `{path, original, modified, version}` (`original` null: the agent made it) |
| `POST /api/changes/accept` `{path}` | keeps the change (all of them without `path`); the list left |
| `POST /api/changes/revert` `{path}` | puts the file back; the list left |
| `GET /api/terminals` | `[{id, title, pid, cols, rows, exitCode}]` |
| `POST /api/terminals` `{cols, rows}` | opens one; 409 `too_many` past ten |
| `POST /api/terminals/close` `{id}` | ends its shell and forgets it |
| `GET /api/terminals/socket?id=ID` | the terminal's WebSocket |
| `GET /api/history` | what this folder sent, newest first, each text once (500 at most): `[{text}]`, without the terminal's own commands (`/help`, `/model`, …; `/compact` and `/clear` stay) |
| `GET /api/preferences` | the page's own preferences (`layout`, `theme`), as it saved them; `{}` at first |
| `POST /api/preferences` `{key: value}` | keeps these keys, merged over the others (`null` forgets one); 413 `too_large` past 16 KB |

On the terminal's WebSocket, the server sends the output kept so far, then
the output as it comes, as binary messages (the terminal's bytes), and
`{"type":"exit","code":N}` (text) when the shell ends, then closes. The page
sends keys as `{"type":"input","data":"…"}` (text) or as binary messages, and
`{"type":"resize","cols":N,"rows":N}`. The chat's calls (`/api/messages`,
`/api/turn`, `/api/approvals`, `/api/settings`, `/api/sessions`, …) are
Arena's chat's shapes; `/api/settings` also takes `compactAt` and
`compactTarget` (400 `invalid` with the bounds), and a turn's stream adds
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
its first two words, `npm test …`; an MCP tool by name). `plan` offers the
model only the tools that change nothing, and asks it for a plan to approve.
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
| `write_file` | creates or replaces a file |
| `edit_file` | replaces an exact piece of a file; it must match once (or `replace_all`), whitespace included; an empty `old_string` makes a new file. Windows line endings are kept |
| `list_dir`, `glob` | folders and file names (`**/*.cs`, `*.{ts,tsx}`); `.gitignore` is respected |
| `grep` | file contents by regular expression, with context lines; binary and ignored files skipped, and those it cannot read, named pipes and links that lead outside |
| `run_shell` | a command in bash (sh when missing; `cmd.exe` on Windows; `"shell"` in the file for another), 120 s by default, at most 600; long output is cut in the middle. With `no_time_limit`, it runs in the background until it ends (below) |
| `command_output` | the state and the latest output of a command run with no time limit; `wait` waits until it ends |
| `stop_command` | stops a command run with no time limit; it always asks the person first, `yolo` too |
| `git` | status, diff, log, show, blame, branch: reading only (commits go through `run_shell`), inside the working directory: `diff --no-index` is refused, and so is a file outside it named anywhere in the arguments (`blame --contents FILE`, `-S FILE`), by name or through a link that leads out. It never asks, so the repository's own settings run nothing in it: no fsmonitor, hook, clean or smudge filter, merge driver (`--remerge-diff`), text conversion (so not `status -v`), external diff or signature checker, no submodule looked into (their changes are not shown), and no network (a partial clone does not fetch). Nor do they send it elsewhere: a work tree other than the folder that holds `.git` (`core.worktree`), or a `.git` that leads to another repository's folder, is refused (a worktree of another checkout and a submodule, which git made so, are read), and a `blame.ignoreRevsFile` outside is not read. The file list `grep` and `glob` (and the IDE's search and quick open) take from git is read the same way |
| `todo_write` | the to-do list the person sees (`/todo`) |
| `task` | a sub-agent (below) |

**Arena's tools** come from `https://DOMAIN/mcp` with the person's key: the
same tools as the chat, with the same rights and the same audit, as that
person. They run without asking, except those Arena marks as changing
something, which ask like a command. A tool named like a local one is offered
as `arena_<name>`. Arena's own instructions to the model are passed on.

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

### Commands with no time limit

A command that takes longer than `run_shell`'s limit (a full build, a long
test suite, an install, a migration) runs with `no_time_limit`: in the
background, with a watcher that shows its output as it comes (in the terminal
under its job number, `│1 …`; in the IDE above the chat's box), however long
it takes. In the terminal its lines never break into a question waiting for the
person's answer or into the middle of the model's line: they wait, and follow. The model gets a job number at once and can go on with other work,
or wait with `command_output`; the turn does not end while one it started
runs, and when one ends the model is told, as a `command_output` result, its
exit code and the end of its output (the last 8,000 characters; the last
256 KB are kept for `command_output`). Several run at once. A time limit given
with it does not apply. When the turn fails while one runs (the gateway or
the model's server down), the command is not stopped: the turn says so and
keeps watching it until it ends, and the model is told how it ended with the
person's next message.

Only the person stops one: **Ctrl+C** in the terminal stops the turn and the
commands it started. While the turn waits for them, Ctrl+C stops the turn
and them. Where the terminal reads plain lines (a pipe, `TERM=dumb`), it still
reads what the person types meanwhile: `/jobs` lists them and `/jobs stop N`
stops one (the model is told how it ended and the turn goes on), and anything
else is taken as the next message when the turn ends (not if they stop it).
At an interactive prompt, whose line editor reads the keys itself, what to say
next is typed once the turn ends, and `/jobs` then lists the commands. **Stop** in the
IDE stops the turn or one command (a command running that no turn on the page shows, after
a reload, is listed from the session's state with its **Stop**), and the
model's `stop_command` asks the person first in every mode, every time: it
offers no **always**. Ordinary commands keep their limit (120 s by default, at
most 600).

Running a command with no time limit asks where commands ask: in `ask` and
`auto-edit` the question says "with no time limit", and **always** said to
`npm test` does not cover `npm test` with no time limit (nor the other way
round), so the person agrees to each kind of wait. `yolo` runs it without
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

## Sessions and the model's window

Each session is a JSON Lines file in the sessions folder (0600): every message
as it happens, the model, the to-do list and the tokens. `--continue`,
`--resume` and `/resume` read it back.

The model's window comes from the gateway (`/v1/model/info`), or `"context"`
in the config file when the gateway does not say (32,768 otherwise). When the
next request would pass the **threshold**, 80% of it by default, the older
part of the conversation is summarized by the model (the person's goals,
decisions, files, commands and the state of the work) and the recent part
kept whole, within the **target**, 25% of the window by default. `/compact`
does it at once. A tool's output longer than 40,000 characters (less for a
small window) is cut in the middle.

Set them with `/compact-at 70` (the threshold) or `/compact-at 70 30` (and the
target), in the IDE's **Context** (the window's use in the status bar), or
`"compactAt"` and `"compactTarget"` in `config.json`: `/compact-at` and the
IDE keep them there for the next sessions, and `/compact-at default` goes back
to 80 and 25. `--compact-at 70 --compact-to 30` sets them for one run. A share
is written `70`, `70%` or `0.7` (in `config.json` too: `70`, `"70%"` or `0.7`;
anything else stops the session with where it is). The threshold is from 20% to 95%, and the
target from 5% to 10 points under the threshold (a lower threshold brings the
target down with it), so a compaction does not start the next one. The use of
the window shows after each turn in the terminal (`/context` at any time) and
in the IDE's status bar.

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
says why.

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
the server marks read-only are offered. `"disabled": true` keeps a server
without starting it. On Windows, `npx` and other `.cmd` scripts need
`"command": "cmd", "args": ["/c", "npx", …]`.

The rest of the file: `"model"`, `"thinking"`, `"mode"`, `"context"`,
`"shell"` (the agent's `run_shell`), `"terminalShell"` (the IDE's terminals),
`"allowedPaths"`, `"arenaTools": false` (no Arena MCP), `"argusTools": false`
(no Argus MCP), `"argusUrl"`, `"compactAt"`, `"compactTarget"`, `"gateway"`,
`"mcpUrl"`, `"ca"`, `"autoCommit"`, `"commitName"`, `"commitEmail"`. `ARENA_ARGUS_URL` in the environment gives Argus's address
for one run.

## Building it (admins)

The app's image carries the builds and serves them on Connect your tools. They
are built when the image is (`src/Llm.Api/Dockerfile`, the `code-arena`
stage), from .NET's runtime packs, which are not part of the SDK:

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
  Without them, the image skips Code Arena, the build log lists the files to
  fetch at that version, and the page says how to add it.
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
  once than your key may have (sub-agents count). Code Arena tries again twice,
  a few seconds apart, before it says so.
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
