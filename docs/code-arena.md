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
window is (`context 18.2k / 131k (14%), compacts at 80%`). **Ctrl+C** stops the turn and
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
| `/web [N\|all\|WORDS]` | your chats in Arena, newest first (the 30 newest; `all`: the 5,000 newest; `WORDS`: those of them with the words in their title or folder); `/web N` (a number in the list last shown, or a chat's id) continues one here |
| `/sync [all]` | where this session stands with its chat in Arena; `all` sends what Arena lacks of this folder's sessions |
| `/memory [add\|forget]` | what it remembers (below); `add TEXT` for this project, `add --all TEXT` for every project, `forget N` |
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

The page is laid out as VS Code is, in Argus Arena's design system:

- **activity bar** (the left edge): Explorer, Search, Source control (with a
  count of the files changed since the last commit), Agent changes (with a
  count of the files), Problems (with a count of the errors) and Chat switch
  the side bar; the one shown hides it.
  At its foot: the terminal panel, the theme (light, dark or the system's:
  kept in the person's `settings.json`, below), Help and About.
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
- **editor**: tabs with Monaco, highlighting by the file's name (or
  `files.associations`), set up by the settings (below). A dot marks unsaved
  changes and **Ctrl+S** saves (**Ctrl+K S** saves them all). A
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
  one (Ctrl+K Ctrl+N), **Accept** and **Revert** in the agent's (Ctrl+K
  Ctrl+Alt+A: the rest stays to review). The agent's changes also have
  **Revert** (puts the file back; a file the agent made is deleted) and
  **Accept** (keeps it) for the whole file, and Agent changes has **Accept
  all**. A tab's right-click menu closes it, the others, those to its
  right, the saved ones or all, copies its path and compares it.
- **search**: text across the files git sees (`.gitignore` holds), with match
  case, whole word, regular expression, and files to include or exclude; a
  result opens the file at the match. **Replace** replaces what it finds, in
  one file or in all of them (it asks first; `$1` stands for a regular
  expression's group; the files keep their line endings; a file with unsaved
  changes in the editor keeps them, and saving it then asks).
- **quick diff**: bars beside the line numbers show the lines changed since
  what git's index has (green added, blue changed, a red mark where lines were
  taken away), as you type; **Alt+F3** and **Shift+Alt+F3** go to the next and
  the previous change (`scm.diffDecorations`: `none` turns them off). Lines
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
  ones still running, with their screens (below).
- **chat**: the agent's chat on the right: the model and thinking pickers,
  Sessions and Hide at its top; the thread with a card for each tool call and
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
  any other. The Chat activity lists this folder's sessions.
- **status bar**: the git branch, the agent's changes, the terminal and the
  number of commands running with no time limit on the left (and, after the
  first key of a chord, that it waits for the second); the cursor's line and
  column, the file's indentation (**Spaces: 4**: indent with spaces or tabs,
  detect it, or convert the file), its line endings (**LF** or **CRLF**) and
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
  (`"[python]": {"editor.tabSize": 4}`, in either). Comments and trailing
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
  delay; `files.associations`), compares (`diffEditor.*`), the terminals
  (font, cursor, scrollback, the keys they let through), the colour theme
  (`workbench.colorTheme`, or `window.autoDetectColorScheme` with the
  preferred light and dark ones: **Ctrl+K Ctrl+T**) and Code Arena's code
  completion (`codeArena.inlineCompletion.enabled`, the status bar's
  **Complete**: the person's own, which a project's settings cannot turn on,
  since what is typed is sent to the model). They apply as the file is saved. Where Code Arena's default
  is not VS Code's, the description says so.
- **keybindings.json**: the person's, beside it (**Preferences: Open Keyboard
  Shortcuts**, **Ctrl+K Ctrl+S**): VS Code's rules, `{"key": "ctrl+shift+e",
  "command": "workbench.action.togglePanel", "when": "editorIsOpen"}`, over
  VS Code's own keys for the commands Code Arena has (by VS Code's command
  ids); the last rule that matches wins, `"-command"` takes a default key
  away, a key may be two chords (`ctrl+k ctrl+t`) and `when` reads VS Code's
  context keys (`editorIsOpen`, `inputFocus`, `terminalFocus`,
  `sideBarVisible`, `resourceLangId == python`…). The editor has the same
  keys, its own (Monaco's: Ctrl+D, Alt+↑, Ctrl+K Ctrl+C…) under theirs, and a
  rule may rebind one of the editor's commands too (`editor.action.…`).

**Ctrl+,** opens the **Settings editor**, as VS Code's: every setting Code
Arena reads by category, with its description and a box, a list or a tick to
set it; search by words, or **@modified**, **@lang:python** (that language's
own) and **@id:editor.tabSize**; **User** writes the person's settings.json,
**Workspace** the project's (made when it is not there yet); each change is
written at once, the file's comments kept; a setting's menu resets it or
copies its id or its JSON; settings of the file Code Arena does not use are
listed apart (they stay in it); **From VS Code** brings over VS Code's own
settings.json from this computer (what the person set already stays).
**Ctrl+K Ctrl+S** opens the **Keyboard Shortcuts editor**: every command with
its keys, when they apply and whose rule each is (Default, User, Extension),
searched by words or by pressing the keys (**Record Keys**); double-click a key
(or its menu) to press new ones (it says how many other commands have them),
and the menu removes, resets or changes when one applies, written into the
person's keybindings.json as VS Code writes it (a default taken away is a
`-command` rule). The `{}` button of either opens its JSON file.

In a terminal the keys go to the shell, but those of the commands that skip
it (the palette, quick open, the panels and views, the debugger's), and the
first key of a chord (Ctrl+K: `terminal.integrated.allowChords` false gives it
to the shell); `terminal.integrated.commandsToSkipShell` adds one
(`"-command"` gives its key back). On macOS the keys are ⌘: Ctrl+S, Ctrl+P and
the others stay the terminal's and the editor's there. A browser tab keeps
Ctrl+W, Ctrl+T, Ctrl+Tab and Ctrl+PageUp/PageDown for itself: their commands
have a second key (Ctrl+Alt+W closes the tab, Ctrl+Alt+Shift+T reopens it,
Ctrl+Alt+PageUp/PageDown move between tabs).

| keys | |
|---|---|
| Ctrl+Shift+P, F1 | run a command by name |
| Ctrl+P | open a file by name (`:12` goes to a line); Ctrl+G goes to a line |
| Ctrl+S, Ctrl+K S | save the file, save them all |
| Ctrl+Alt+W, Ctrl+K Ctrl+W, Ctrl+K U | close the tab, all the tabs, the saved ones |
| Ctrl+Alt+Shift+T | reopen the tab closed last |
| Alt+1 … Alt+9, Ctrl+Alt+PageUp/PageDown | a tab by its place, the next or the one before |
| Ctrl+Alt+- , Ctrl+Shift+- (Alt+← and Alt+→ on Windows) | back and forward through the tabs shown |
| Ctrl+B, Ctrl+J, Ctrl+Alt+B | the side bar, the panel, the chat |
| Ctrl+Shift+E, Ctrl+Shift+F, Ctrl+Shift+H, Ctrl+Shift+G, Ctrl+Shift+M | Explorer, search, replace, source control, problems |
| Ctrl+`, Ctrl+Shift+` | show or hide the terminals, a new one |
| Ctrl+, , Ctrl+K Ctrl+S, Ctrl+K Ctrl+T | settings, keyboard shortcuts, colour theme |
| Ctrl+K M, Alt+Z, Ctrl+K P | the language mode, word wrap, copy the file's path |
| Ctrl+L | in the editor: the lines chosen (or the cursor's) go with the next message |

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

### Ask about code from the editor

Lines of a file go to the agent from the editor itself:

- In the chat's box, **@** offers the folder's files, best first: ↑ and ↓
  choose, Enter or Tab puts `@path` in the box. Above the box, a dashed chip
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
warnings; the activity bar the errors.

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
| `GET /api/state` | `name`, `version`, `license`, `source` (the about box), `manual` (the help's link: the Arena's `/help/code-arena`, null without its address), the folder, project, branch, model, mode, session, busy; `turn` (the number of the answer running or run last); `queued` (the messages waiting); `context` (the window), `contextUsed` (about how much of it is in use), `compactAt` and `compactTarget` (percent); `servers` (`[{name, title, url, state, tools, status, error, nextTry}]`, `state` one of `connecting`, `connected`, `unavailable`, `failed`); `jobs` (`[{id, command, running, status, background, output}]`, `output` the end of a running one's output); `arenaChats` (sessions are kept with chats in Arena here), `account` (whose chats: the key's account, null until Arena said), `chat` (this session's chat there, null: none yet) and `synced` (how many times the web chat's news came in while nothing ran: the page reads the session again when it changes; taking it in is not `busy`) |
| `GET /api/sessions` | this folder's sessions, newest first: `[{id, title, updatedAt, messages, chat}]` (`chat`: the chat in Arena it is kept with) |
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
| `POST /api/problems/run` | runs the check (waits for the one running) and answers as above; 404 `no_check` when none is found |
| `POST /api/complete` `{path, prefix, suffix}` | code completion at the editor's cursor: `{text}`; 404 `off` when the config turns it off, 409 `busy` while an answer is written, 429 `limited` at the key's limit |
| `POST /api/messages` `{text, context}` | a message, answered as a stream of events; `context`: lines chosen in the editor, `[{path, startLine, endLine, text}]` (`text`: the editor's, when unsaved; eight at most). The stream's `{"type":"attached", id, files}` lists what went with the question |
| `POST /api/queue` `{text, context}` | a message for after the answer being written (runs at once when none is), with its lines; 202 `{queued}` (its place). `/api/messages` while an answer is written is 409 `busy` |
| `DELETE /api/queue` | drops the queued messages; 204 |
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

## What it remembers, and what came before

**Memory.** The model keeps what a later session should know with its
`remember` tool: how to build and test the project, its conventions, a
decision, or (for every project) the person's preferences. Each memory is a
line of a plain Markdown file: the project's in the data folder (by the
project's path: on this machine only, never in the repository), the person's
`memory.md` in the config folder. A session's prompt carries both as it starts.
`/memory` lists them with their files; `/memory add` and `/memory forget N`
change them, and the files can be edited by hand.

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
for that session, which goes on here. `/sync` says where the session stands
and whose chats they are: the sessions go to the chats of the account whose
key `code-arena login` was given (login says which; the IDE shows it over
**Chats in Arena**), so a key of another account (a test one) keeps them
there, not with your own chats.
`"syncChats": false` in `config.json` keeps sessions on this machine only;
`/sync all` (the IDE's **Send the other sessions here**) later sends Arena what
it lacks of this folder's sessions (those it never had, and those sent in
part), the oldest first, each keeping its place in the list here; one whose
chat was deleted there stays as it is. When Arena cannot be reached it stops,
and says how many are left for the next `/sync all`.

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
the server marks read-only are offered. `"disabled": true` keeps a server
without starting it. On Windows, `npx` and other `.cmd` scripts need
`"command": "cmd", "args": ["/c", "npx", …]`.

The rest of the file: `"model"`, `"thinking"`, `"mode"`, `"context"`,
`"shell"` (the agent's `run_shell`), `"terminalShell"` (the IDE's terminals),
`"allowedPaths"`, `"arenaTools": false` (no Arena MCP), `"argusTools": false`
(no Argus MCP), `"argusUrl"`, `"compactAt"`, `"compactTarget"`, `"gateway"`,
`"mcpUrl"`, `"ca"`, `"autoCommit"`, `"commitName"`, `"commitEmail"`, `"syncChats"`, `"window"`, `"completion"`, `"completionModel"`, `"completionTemplate"`, `"checkCommand"`, `"sandbox"`,
`"sandboxNetwork"`, `"sandboxWritable"`, `"permissions"`. `ARENA_ARGUS_URL` in the environment gives Argus's address
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

Code Arena carries on by itself where it can. A gateway that does not answer
(502, 503, 504, a lost connection: the Arena restarting, a model loading) is
tried again five times over about a minute and a half, and the session says so
each time. An answer that has not started after a minute says so (the engine
busy with others, or reading a long prompt; Ctrl+C or **Stop** ends the wait).
One that does not start within 15 minutes, or that stops for 3 minutes, is
given up and asked once more; a second such stall ends the turn with the
reason, and the next message is sent as usual. When
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
