# Arena Code

Our own coding agent, in the terminal (or in the browser, with `arena-code web`). A person runs it in a project folder on
their machine; it reads and changes the code there, runs commands, and works
through the Arena: the model through the gateway, and all of the chat's tools
(web search and pages, Python in the sandbox, pictures, speech, Argus's code
search, plugins, knowledge) through Arena's MCP endpoint, as that person.

It is **one file with nothing to install**: a self-contained .NET program for
Linux, macOS and Windows. It talks to the Arena only, never to the internet, so
it works on a network with no way out.

The code is `src/ArenaCode` (the base class library only, no packages), its
tests `tests/ArenaCode.Tests`.

## Getting it

**Your account → Connect your tools → Arena Code (our own agent)** offers a
download for each system the Arena has a build of. Or from a terminal, with no
browser (the downloads need no sign-in; the program does nothing without a key):

```bash
curl -fLo arena-code https://DOMAIN/api/downloads/arena-code/linux-x64
chmod +x arena-code && mkdir -p ~/.local/bin && mv arena-code ~/.local/bin/
```

The systems: `linux-x64`, `linux-arm64`, `osx-arm64` (Apple silicon),
`osx-x64` and `win-x64` (`arena-code.exe`). `/api/downloads/arena-code` lists
them with their SHA-256.

- **macOS** marks a file downloaded in a browser, and refuses to open it until
  the mark is gone: `xattr -d com.apple.quarantine arena-code`. The builds are
  signed ad hoc, not with an Apple developer ID.
- **Windows** may warn that the file is unknown (SmartScreen): **More info →
  Run anyway**, or unblock it in the file's properties. Put `arena-code.exe` in
  a folder on `PATH`.

When the page says the Arena has no builds yet, an admin adds them (below).

## Signing in

```bash
arena-code login --url https://DOMAIN
```

It asks for the Arena's address (when `--url` is not given) and the person's
API key (**Your account → API key**; typed without echo), checks the key with
the gateway (`GET https://gateway.DOMAIN/v1/models`), connects to Arena's
tools to say how many there are, and saves both in its config file, readable
by its owner only (0600). The key is never printed. `arena-code logout` forgets
the key; `arena-code models` lists the models the key may use.

| | Linux and macOS | Windows |
|---|---|---|
| settings (`config.json`, `ARENA.md`) | `~/.config/arena-code/` (or `$XDG_CONFIG_HOME`) | `%APPDATA%\arena-code\` |
| sessions | `~/.local/share/arena-code/sessions/` (or `$XDG_DATA_HOME`) | `%LOCALAPPDATA%\arena-code\sessions\` |

`ARENA_CODE_HOME` puts both under one folder (`config/` and `data/`), for a
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
arena-code login --url https://DOMAIN --ca ca.crt
```

The path is saved (`"ca"` in the file) and the CA trusted besides the system's,
for the gateway, Arena's tools and the person's own MCP servers. In order:
`--ca`, `ARENA_CA_CERT`, the file's `"ca"`, `SSL_CERT_FILE`. A certificate for
another name is never accepted, and there is no switch to turn the check off.
Trusting the CA system-wide works too, and needs nothing here.

## Working with it

```bash
cd my-project
arena-code                          # a conversation
arena-code "why does the build fail?"   # one that starts with this
arena-code -p "list the TODOs in src/"  # answer once, print the answer, exit
git diff | arena-code -p - --output json
arena-code --continue               # carry on with this folder's last session
arena-code --resume                 # choose a saved session (or --resume <id>)
arena-code web                      # the same, in your browser (see Web interface)
```

The answer streams as it is written; the model's thinking is shown dimmed
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

## Web interface

```bash
cd my-project
arena-code web                      # the same agent, in your browser
arena-code web --port 8765 --no-open
arena-code web --continue --mode auto-edit
```

`arena-code web` starts a small web server on this machine, prints its
address and opens the browser (`--no-open` only prints it). The page is
Argus Arena's chat around the agent of this folder: the sidebar with the
folder's sessions (newest first: open one to carry on with it, or start a new
one), the thread with Markdown, code blocks, the model's thinking, a card for
each tool call (the local tools and Arena's), a diff under each file edit, and
each answer's model, time and tokens with the share read from the cache. The
composer has Stop, the mode (ask, auto-edit, plan, yolo) and the context
gauge (Compact now); the header has the model picker (the gateway's models),
the thinking level and the theme (light, dark or the system's). A call that
needs permission shows Arena's approval card: **Allow**, **Always for this
session** (as `a` in the terminal) or **Deny**. `/compact` and `/clear` work
in the composer.

It is the same agent as in the terminal, not a copy: the same tools, modes,
sessions (the same files: `--resume` in the terminal opens a session started
in the browser, and the other way round), ARENA.md and MCP servers. The
terminal shows what the agent does (`● edit_file …`) while the page drives it.
A page reloaded during a turn picks the turn up from its start. One turn runs
at a time; switching sessions waits for it. **Ctrl+C** in the terminal stops
the server (and the turn, if one runs).

Only this machine can use it:

- It listens on `127.0.0.1` only, on a free port unless `--port` says which.
- The address carries a key made for this run (`?token=…`). Opening it sets a
  cookie (HttpOnly, SameSite=Strict, one per port) and takes the key out of the
  address; every request without the key is refused. A new run makes a new
  key: an old tab says so.
- The server answers only to `127.0.0.1` and `localhost` at its port (the Host
  header), so a name pointed at 127.0.0.1 (DNS rebinding) gets nothing; it
  refuses requests from other sites (Origin, Sec-Fetch-Site), takes only JSON,
  and its page cannot be framed.

The page is built into the program (below); a build without it says so when
`arena-code web` starts. Diagrams in answers are drawn by the chat's sandboxed
runner, served by the same server.

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
  "apiKey": "…written by arena-code login…",
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
`"shell"`, `"allowedPaths"`, `"arenaTools": false` (no Arena MCP), `"gateway"`,
`"mcpUrl"`, `"ca"`.

## Building it (admins)

The app's image carries the builds and serves them on Connect your tools. They
are built when the image is (`src/Llm.Api/Dockerfile`, the `arena-code`
stage), from .NET's runtime packs, which are not part of the SDK:

- **A host with internet**: `docker compose build app --build-arg ARENA_CODE=online`
  (or `tools/publish-arena-code.sh`) fetches them from nuget.org.
- **A host without**: fill `tools/offline-nuget/` once from a machine with
  internet (its [README](../tools/offline-nuget/README.md) has the commands,
  about 220 MB), carry it over with the repository, and build as usual: the
  image builds Arena Code from that folder only (`ARENA_CODE=auto`, the
  default). Without the packs, the image skips it and the page says so.
- **By hand**: `tools/publish-arena-code.sh --offline [rid ...]` writes
  `dist/arena-code/<rid>/arena-code` and `SHA256SUMS`, with `dotnet` or
  `tools/dn`. Copy the files to people any way you like.

The web interface's page is built first, from `src/web`
(`vite.arena-code.config.ts`, then the chat's diagram runner) into
`src/ArenaCode/web`, and embedded in each file whole. The script builds it
with Node.js and `src/web/node_modules` (`npm ci` there once), downloading
nothing; `--no-web` leaves it out. The image builds it in a stage of its own,
`arena-code-web`, whose first steps are `src/web`'s own image's: a host that
built the web image has its `npm ci` cached. `ARENA_CODE=none` skips it too.

`ARENA_CODE=none` leaves it out of the image (about 210 MB for the five
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
  Make a new one under **Your account → API key** and run `arena-code login`.
- **"… has no OpenAI API (404)"**: the gateway is not at `gateway.DOMAIN`. Give
  it: `arena-code login --gateway https://…`.
- **"Arena's tools are not available here"**: this Arena has no MCP endpoint
  (an older version), or a proxy in between does not pass `/mcp`. The session
  works with the local tools.
- **An edit "was not found"**: the file changed since the model read it, or the
  model got the whitespace wrong. It reads the file again and retries.
- **The window fills fast**: large tool outputs. `/cost` shows how full it is,
  `/compact` frees it, and sub-agents keep research out of the main context.
- **Colours look wrong**: `--no-color` or `NO_COLOR=1`; output to a file or a
  pipe has none.
