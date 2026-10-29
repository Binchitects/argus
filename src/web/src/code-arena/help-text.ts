import type { HelpPart, HelpTask } from '@/help/topics'
import { modKey } from './editor-state'

/**
 * Code Arena's help: what the workbench is for, what each part does, and the common tasks
 * step by step. Help in the activity bar shows it all; the ? in a panel's header shows that
 * panel's part first. **Words** in bold are the page's own labels. Keep it plain and short.
 */

/** The parts of the workbench. */
export type Part = 'activity' | 'explorer' | 'search' | 'changes' | 'problems' | 'review' | 'git' | 'sessions' | 'editor' | 'terminal' | 'chat' | 'status' | 'settings' | 'keybindings' | 'debug' | 'console' | 'extensions' | 'output'

export interface PartHelp extends HelpPart {
  /** Where the manual's Code Arena page (docs/code-arena.md) tells the rest: one of its headings. */
  manual: string
}

export const about = 'An editor around the coding agent, for this folder: its files, a terminal, and the agent\'s chat, which can read, change and run things here as the mode allows.'

export const parts: Record<Part, PartHelp> = {
  extensions: {
    name: 'Extensions',
    text: `VS Code's extensions, for what they declare (${modKey}+Shift+X): colour and file icon themes, snippets, languages (comments, brackets, folding), keybindings and setting defaults. **Install from VSIX…** takes a .vsix you downloaded; **VS Code on this computer** imports one VS Code has; **Recommended** says where each of the project's recommendations stands here. A switch turns one off; the bin uninstalls it. No extension's code runs: ESLint, GitLens, debuggers and language servers are not extensions here.`,
    manual: 'extensions',
  },
  debug: {
    name: 'Run and Debug',
    text: `Choose a run configuration (.vscode/launch.json's, or one the project's files give) and press **Start Debugging** (F5) or **Run** (Ctrl+F5: in a terminal, without the debugger). **Build** is Debug or Release. While a session runs: **Variables** of the frame chosen (double-click one to change it), **Watch** expressions, the **Call Stack** (a frame opens its line), and the **Breakpoints** (click beside a line number, or F9; right-click for a condition or a log message). The toolbar over the editor continues (F5), pauses (F6), steps over (F10), into (F11), out (Shift+F11), restarts and stops (Shift+F5).`,
    manual: 'run-and-debug',
  },
  console: {
    name: 'Debug Console',
    text: 'What the program and the debugger print while debugging; the box below evaluates an expression in the frame chosen while the program is stopped (↑ and ↓ bring back what you typed).',
    manual: 'run-and-debug',
  },
  output: {
    name: 'Output',
    text: "What each language server said, one channel each (choose it above): when it started, its messages, why it ended, and the last lines it wrote to stderr. **Restart** starts it again. The status bar's language server item opens it too (**Show Language Server Log**).",
    manual: 'language-servers',
  },
  settings: {
    name: 'Settings',
    text: `The settings Code Arena reads, as VS Code's (${modKey}+,). Search by words, or **@modified**, **@lang:python** (a language's own), **@id:editor.tabSize**. **User** is your settings.json, for every project; **Workspace** the project's .vscode/settings.json. A change is written at once, the file's comments kept; the menu beside a setting resets it.`,
    manual: 'settings-and-keyboard-shortcuts',
  },
  keybindings: {
    name: 'Keyboard Shortcuts',
    text: `Every command with its keys (${modKey}+K ${modKey}+S). Search by words, or press **Record Keys** and the keys. Double-click a key to change it; the menu adds, removes or resets one, or changes when it applies. What you change is written into your keybindings.json.`,
    manual: 'settings-and-keyboard-shortcuts',
  },
  activity: {
    name: 'Activity bar',
    text: 'The icons down the side bar\'s edge. **Explorer**, **Search**, **Source control**, **Run and Debug**, **Extensions**, **Agent changes** (with the number of files), **Problems** and **Chat** switch the side bar; pressing the one shown hides it. Drag an icon to put it elsewhere in the bar, or onto the panel\'s tabs to move its view there (drag the tab back to return it); its right-click menu has the same moves, puts the side bar on the other side (the chat goes opposite) and resets them. At its foot: **Terminal**, the theme, **Manage** (the gear: Settings, Keyboard Shortcuts, Color Theme, Extensions, the palette, and how the model gets its tools), **Help** and **About Code Arena**.',
    manual: 'the-ide',
  },
  explorer: {
    name: 'Explorer',
    text: 'The folder\'s files. Click opens one; a right-click has New file, New folder, Copy path, Rename (F2) and Delete. The files the agent changed are marked, and so are the folders they are in. **Outline**, at its foot, lists the symbols of the file shown (the one the cursor is in marked); a click goes to one.',
    manual: 'the-ide',
  },
  search: {
    name: 'Search',
    text: `Words across the folder's files (those git sees), with match case, whole word, a regular expression, and files to include or leave out. A result opens the file at the match. ${modKey}+P opens a file by a few letters of its path.`,
    manual: 'the-ide',
  },
  changes: {
    name: 'Agent changes',
    text: "The files the agent changed in this run that you have not kept or put back yet, and below them **Your changes**: those changed by hand (git's that are not the agent's). Each opens as it was and as it is now. **Accept** keeps a change, **Revert** puts the file back, **Accept all** keeps them all; a file's right-click menu adds it to the chat, or asks the agent to explain or fix the change.",
    manual: 'the-ide',
  },
  problems: {
    name: 'Problems',
    text: "The errors and warnings of the project's own check (its build, type check or linter): **Run check** runs it. Each opens the file at its line, where the editor marks it too; **Fix** asks the agent to fix it.",
    manual: 'problems',
  },
  review: {
    name: 'Review',
    text: 'A review of the changes not yet committed, or of the branch over its base, by a reviewer that reads the code and changes nothing. Its findings, worst first, open the file at their line and are marked there; **Apply fix** makes a fix that fits the file in the editor (undo takes it back), **Ask the agent** hands one over, **Dismiss** leaves it.',
    manual: 'review',
  },
  git: {
    name: 'Source control',
    text: "The folder's git: what changed since the last commit, **staged** for the next commit or not. A file opens as the last commit has it and as it is now; **+** stages it, **−** unstages it, **↺** puts it back. Write the message and **Commit** (all of it when nothing is staged); switch the branch below, and see the last commits.",
    manual: 'source-control',
  },
  sessions: {
    name: 'Sessions',
    text: 'This folder\'s conversations with the agent, newest first. Click one to carry it on; **New session** starts another. They are the same as in the terminal: code-arena chat --resume opens them too.',
    manual: 'sessions-and-the-models-window',
  },
  editor: {
    name: 'Editor',
    text: `Your open files, in tabs. A dot marks changes not saved yet, and ${modKey}+S saves. A file the agent edits reloads in its tab when nothing in it is unsaved; saving a file that changed on disk since you opened it asks first.`,
    manual: 'the-ide',
  },
  terminal: {
    name: 'Terminal',
    text: 'Shells in this folder, under the editor. Ctrl+` shows or hides them, **+** opens another, and dragging the panel\'s top edge resizes it. A terminal can do whatever your own shell can: the mode guards the agent, not you.',
    manual: 'terminals',
  },
  chat: {
    name: 'Chat with the agent',
    text: 'Ask for an answer or a change. The model and its thinking are at the top; each tool the agent uses is a card, with the diff under each edit. A file it cites (`src/app.ts:12`), the path and line numbers of a diff, and the chip under the card of a tool that read or edited a file open it in the editor at those lines. A command it runs with no time limit shows above the box with its output and **Stop**; the turn waits for it. **Stop** under the box ends a turn. The mode under the box says what runs without asking: **Ask**, **Auto-edit**, **Plan** or **Yolo**. Select words in an answer to **Reply** quoting them (Ctrl+Shift+\'), **Ask about this** or **Copy**; **Quote part of this answer…** under an answer does it by keyboard.',
    manual: 'modes',
  },
  status: {
    name: 'Status bar',
    text: 'Along the bottom: the git branch, the agent\'s changes, the terminal and the commands running with no time limit on the left; the cursor\'s line and column, the file\'s formatter, its language server (starting, ready, failed, or off until the folder is trusted: click it to restart it or see its log) and its language, the MCP servers (**Try again** for one not connected), how full the model\'s window is (**Context** sets when the session compacts), the mode, the model and the version (About: the licence and the source) on the right.',
    manual: 'the-ide',
  },
}

export const tasks: HelpTask[] = [
  {
    title: 'Ask the agent for a change',
    steps: [
      'Type what you want in the chat\'s box, on the right, and press Enter.',
      'When it asks before an edit or a command, read it, then press **Allow**, **Always for this session** or **Deny** (stopping a command it runs with no time limit asks every time: there is no **Always**).',
      'What it changed shows under **Agent changes** and in the open tabs.',
    ],
  },
  {
    title: 'Keep or undo what the agent changed',
    steps: ['Press **Agent changes** in the activity bar.', 'Click a file: it opens as it was before and as it is now.', 'Press **Accept** to keep it or **Revert** to put it back; **Accept all** keeps every change.'],
  },
  {
    title: 'Ask about code in the editor',
    steps: [
      `Choose the lines (or put the cursor on one) and press ${modKey}+L: they go with your next message.`,
      'Or right-click them: **Explain this**, **Fix this** or **Complete this** asks at once.',
      'On a line with a problem, the light bulb offers **Fix with Code Arena**.',
    ],
  },
  { title: 'Open a file fast', steps: [`Press ${modKey}+P.`, 'Type a few letters of its path.', 'Press Enter.'] },
  { title: 'Run a command yourself', steps: ['Press Ctrl+` or **Terminal** in the activity bar.', 'Type in the shell; **+** opens another one.'] },
  {
    title: 'Let the agent do more, or less, without asking',
    steps: ['Open the mode under the chat\'s box.', '**Ask** asks before edits and commands, **Auto-edit** only before commands, **Plan** changes nothing, **Yolo** asks nothing: use it only in a folder you can throw away.'],
  },
  { title: 'Go back to an earlier session', steps: ['Press **Chat** in the activity bar.', 'Click the session; **New session** starts another.'] },
  {
    title: 'Change when the conversation is compacted',
    steps: [
      'Click how full the window is, in the status bar (tokens used / the model\'s; the gauge on a phone).',
      'Choose **At a share of the window** (and set **Compact at**), **Only when a request would not fit**, or **As my Arena account says** (Your account → Answers in Arena), and what the recent part keeps, in percent of the window. It always compacts before a request would not fit.',
      'Press **Save**: the next sessions use it too.',
    ],
  },
  {
    title: 'Stop a long command the agent runs',
    steps: ['While it runs, its output shows above the chat\'s box.', 'Press **Stop** beside it: the agent is told it ended.'],
  },
  {
    title: 'Change a setting, as in VS Code',
    steps: [
      `Press ${modKey}+, (or run **Preferences: Open User Settings (JSON)**): your settings.json, for every project.`,
      'Type a setting\'s name: the editor suggests them, with what each does. `"[python]": { … }` sets them for one language.',
      'Save: it applies at once. The project\'s own are in .vscode/settings.json (**Preferences: Open Workspace Settings (JSON)**).',
    ],
  },
  {
    title: 'Change a keyboard shortcut',
    steps: [
      `Press ${modKey}+K ${modKey}+S: your keybindings.json.`,
      'Add `{ "key": "ctrl+shift+e", "command": "workbench.action.togglePanel" }`; `"-command"` takes a key away. Commands have VS Code\'s ids (the palette lists them).',
      'Save: it applies at once, in the editor too.',
    ],
  },
  {
    title: "Use a VS Code theme, icons or snippets",
    steps: [
      `Press ${modKey}+Shift+X (**Extensions**).`,
      'Press **Install from VSIX…** and choose the extension\'s .vsix (its page on Open VSX or the Marketplace has a download), or import it from **VS Code on this computer**.',
      `Choose its theme with ${modKey}+K ${modKey}+T (the editor shows each as you go down the list), or its icons with **Preferences: File Icon Theme**; its snippets and languages apply at once.`,
    ],
  },
  {
    title: 'Get completions, definitions and errors from a language server',
    steps: [
      'Install the language server for the language (typescript-language-server, pyright, gopls, rust-analyzer, clangd…): the status bar says which, and how, when it is missing.',
      'Trust the folder (**Manage Workspace Trust**): a language server runs the folder\'s code.',
      'Open a file of the language: the server starts, and F12 goes to a definition, Shift+F12 lists the references, F2 renames.',
    ],
  },
  {
    title: "Use Arena's and Argus's tools",
    steps: ['They connect by themselves once you are signed in: the plug in the status bar counts those connected.', 'Click it to see why one is not, and press **Try again**.'],
  },
]

export const keys: [keys: string, what: string][] = [
  [`${modKey}+S, ${modKey}+K S`, 'Save the file, save them all'],
  [`${modKey}+P`, 'Open a file by its name (:12 goes to a line)'],
  [`${modKey}+Shift+P or F1`, 'Run a command by its name'],
  [`${modKey}+Shift+F`, 'Search the files'],
  [`${modKey}+Shift+E`, 'The Explorer'],
  [`${modKey}+Shift+G`, 'Source control'],
  ['Ctrl+`', 'Show or hide the terminal'],
  [`${modKey}+B, ${modKey}+J`, 'Show or hide the side bar, the panel'],
  [`${modKey}+,`, 'Settings (settings.json, as VS Code\'s)'],
  [`${modKey}+K ${modKey}+S`, 'Keyboard shortcuts (keybindings.json, as VS Code\'s)'],
  [`${modKey}+K ${modKey}+T`, 'The colour theme'],
  [`${modKey}+Shift+X`, 'Extensions (VS Code\'s themes, icons, snippets, languages)'],
  [`${modKey}+L`, 'In the editor: the lines chosen go with the next message'],
  ['↑ / ↓', 'In the chat\'s box: what you sent before in this folder (the terminal\'s too); Esc goes back to what you were typing'],
]

/**
 * Each region of the workbench, by the name a screen reader gives it, and its part. ide.test.tsx
 * fails for a region that is not here: a new panel gets its part and its help.
 */
export const regions: Record<string, Part> = {
  'Activity bar': 'activity',
  Explorer: 'explorer',
  Search: 'search',
  'Search the files': 'search',
  'Agent changes': 'changes',
  Problems: 'problems',
  Review: 'review',
  'Source control': 'git',
  Sessions: 'sessions',
  Editor: 'editor',
  Terminal: 'terminal',
  Agent: 'chat',
  Chat: 'chat',
  'Status bar': 'status',
  Settings: 'settings',
  'Run and Debug': 'debug',
  'Debug Console': 'console',
  Extensions: 'extensions',
  Output: 'output',
  'Keyboard Shortcuts': 'keybindings',
  Outline: 'explorer',
  Breadcrumbs: 'editor',
}
