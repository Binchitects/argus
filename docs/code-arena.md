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
(`12.4k in (81% cached) · 310 out · 3 requests`). **Ctrl+C** stops the turn and
keeps what was said; at the prompt, twice leaves. A line ending in `\` goes on
to the next.

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
| `/cost` | tokens spent, and how full the model's window is |
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
prints it). **Ctrl+C** in the terminal stops it, and the turn if one runs;
the terminal shows what the agent does (`● edit_file …`) while the page drives
it. On another machine over SSH: `code-arena --port 8765 --no-open` there,
`ssh -L 8765:127.0.0.1:8765 that-machine` here, and open the address printed.

The page is laid out as VS Code is, in Argus Arena's design system:

- **activity bar** (the left edge): Explorer, Search, Agent changes (with a
  count of the files) and Chat switch the side bar; the one shown hides it.
  At its foot: the terminal panel, the theme (light, dark or the system's)
  and About.
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
  session**, **Deny**), Stop, the mode, `/compact` and `/clear`. The Chat
  activity lists this folder's sessions.
- **status bar**: the git branch, the agent's changes and the terminal on the
  left; the cursor's line and column, the file's language, the mode, the
  model and `code-arena <version>` (About: the version, the licence, the
  folder and the Source link) on the right.

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
256 KB of each one's output. Closing one sends its shell and what runs in it
SIGHUP (SIGKILL 3 s later); stopping code-arena closes them all.

A terminal is as powerful as the person's own shell, and so are the page's
file calls: the mode (ask, auto-edit, …) guards the agent, not the person.
That is why everything below is behind this run's key.

### Only this machine can use it

- It listens on `127.0.0.1` only, on a free port unless `--port` says which.
- The address carries a key made for this run (`?token=…`). Opening it sets a
  cookie (HttpOnly, SameSite=Strict, one per port) and takes the key out of the
  address; every request without the key is refused. A new run makes a new
  key: an old tab says so.
- The server answers only to `127.0.0.1` and `localhost` at its port (the Host
  header), so a name pointed at 127.0.0.1 (DNS rebinding) gets nothing; it
  refuses requests from other sites, or another port's page (Origin,
  Sec-Fetch-Site), takes only JSON, and its page cannot be framed. Every call,
  the file calls and the terminals' WebSocket included, passes these checks.
- The file calls take paths relative to the working directory only: an
  absolute path, a drive or `..` is refused, even one that would land inside.
  So is a link that leads outside, one to a file not there yet outside, and
  one inside by name only through another link. The Explorer, search and quick
  open leave such links out. Folders added with `--add-dir` are the agent's,
  not the IDE's.
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
| `GET /api/state` | `name`, `version`, `license`, `source` (the about box), the folder, project, branch, model, mode, session, busy |
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
| `GET /api/preferences` | the page's own preferences (`layout`, `theme`), as it saved them; `{}` at first |
| `POST /api/preferences` `{key: value}` | keeps these keys, merged over the others (`null` forgets one); 413 `too_large` past 16 KB |

On the terminal's WebSocket, the server sends the output kept so far, then
the output as it comes, as binary messages (the terminal's bytes), and
`{"type":"exit","code":N}` (text) when the shell ends, then closes. The page
sends keys as `{"type":"input","data":"…"}` (text) or as binary messages, and
`{"type":"resize","cols":N,"rows":N}`. The chat's calls (`/api/messages`,
`/api/turn`, `/api/approvals`, `/api/settings`, `/api/sessions`, …) are
Arena's chat's shapes.

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
outside. Commands run in the working directory, but they are the person's own
commands, with their rights: the mode is what guards them. The API key is
removed from the environment of every command and MCP server the agent starts.

### Laya looks at commands

When Arena MCP offers `decide` (the Arena runs the laya module, and the person
may use **Decide (Laya)**: [chat.md](chat.md#decide-laya)), every command is
shown to Laya before it runs, with three yes/no questions: is it destructive,
does it write outside the workspace, does it reach the network. Laya reads
the command, the working folder, and the paths outside it that the command
names (`~`, `..`, `/etc`...; found here, since Laya reads text and does not
compare paths), with its English checkpoint, in about 0.35 s on the Arena's CPU.

- **Every question about a command shows the three probabilities**:
  `Allow run_shell? (Laya: destructive 96%, outside the workspace 93%, network 3%)`,
  in the terminal and in the IDE.
- **A command Laya rates at 60% or more on any of the three asks anyway**,
  in `yolo`, and when **always** was said for its first words. **Always** said
  to a command Laya flagged covers the next flagged one with those words.
- **A run that cannot ask** (`-p`) does not run a flagged command: the model is
  told why, and finds another way.
- **It only ever adds a question.** When Laya does not answer (10 seconds at
  most), commands run as the mode says, and the session says so once. Nothing
  that would ask runs without asking.

The threshold, 0.6, is measured: of 95 commands labelled by hand, Laya asks
before 45 of the 52 risky ones (`rm -rf ~`, `git reset --hard`, `curl … |
sh`, `git push --force`, `pip install`...) and 2 of the 43 harmless ones
(`echo hi > /dev/null` and `tar -czf build.tgz dist`, 75% and 74% destructive).
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
| `grep` | file contents by regular expression, with context lines; binary and ignored files skipped |
| `run_shell` | a command in bash (sh when missing; `cmd.exe` on Windows; `"shell"` in the file for another), 120 s by default, at most 600; long output is cut in the middle |
| `git` | status, diff, log, show, blame, branch: reading only (commits go through `run_shell`) |
| `todo_write` | the to-do list the person sees (`/todo`) |
| `task` | a sub-agent (below) |

**Arena's tools** come from `https://DOMAIN/mcp` with the person's key: the
same tools as the chat, with the same rights and the same audit, as that
person. They run without asking, except those Arena marks as changing
something, which ask like a command. A tool named like a local one is offered
as `arena_<name>`. When the Arena has no MCP endpoint, the session says so once
and carries on with the local tools. Arena's own instructions to the model are
passed on.

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
in the config file when the gateway does not say (32,768 otherwise). Near 80%
of it, the older part of the conversation is summarized by the model (the
person's goals, decisions, files, commands and the state of the work) and the
recent part kept whole. `/compact` does it at once. A tool's output longer
than 40,000 characters (less for a small window) is cut in the middle.

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
`"allowedPaths"`, `"arenaTools": false` (no Arena MCP), `"gateway"`,
`"mcpUrl"`, `"ca"`.

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
  default). Without the packs, the image skips it and the page says so.
- **By hand**: `tools/publish-code-arena.sh --offline [rid ...]` writes
  `dist/code-arena/<rid>/code-arena` and `SHA256SUMS`, with `dotnet` or
  `tools/dn`. Copy the files to people any way you like.
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
- **"… has no OpenAI API (404)"**: the gateway is not at `gateway.DOMAIN`. Give
  it: `code-arena login --gateway https://…`.
- **"Arena's tools are not available here"**: this Arena has no MCP endpoint
  (an older version), or a proxy in between does not pass `/mcp`. The session
  works with the local tools.
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
