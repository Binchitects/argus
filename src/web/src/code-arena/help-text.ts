import type { HelpPart, HelpTask } from '@/help/topics'
import { modKey } from './editor-state'

/**
 * Code Arena's help: what the workbench is for, what each part does, and the common tasks
 * step by step. Help in the activity bar shows it all; the ? in a panel's header shows that
 * panel's part first. **Words** in bold are the page's own labels. Keep it plain and short.
 */

/** The parts of the workbench. */
export type Part = 'activity' | 'explorer' | 'search' | 'changes' | 'sessions' | 'editor' | 'terminal' | 'chat' | 'status'

export interface PartHelp extends HelpPart {
  /** Where the manual's Code Arena page (docs/code-arena.md) tells the rest: one of its headings. */
  manual: string
}

export const about = 'An editor around the coding agent, for this folder: its files, a terminal, and the agent\'s chat, which can read, change and run things here as the mode allows.'

export const parts: Record<Part, PartHelp> = {
  activity: {
    name: 'Activity bar',
    text: 'The icons down the left edge. **Explorer**, **Search**, **Agent changes** (with the number of files) and **Chat** switch the side bar; pressing the one shown hides it. At its foot: **Terminal**, the theme, **Help** and **About Code Arena**.',
    manual: 'the-ide',
  },
  explorer: {
    name: 'Explorer',
    text: 'The folder\'s files. Click opens one; a right-click has New file, New folder, Copy path, Rename (F2) and Delete. The files the agent changed are marked, and so are the folders they are in.',
    manual: 'the-ide',
  },
  search: {
    name: 'Search',
    text: `Words across the folder's files (those git sees), with match case, whole word, a regular expression, and files to include or leave out. A result opens the file at the match. ${modKey}+P opens a file by a few letters of its path.`,
    manual: 'the-ide',
  },
  changes: {
    name: 'Agent changes',
    text: 'The files the agent changed in this run that you have not kept or put back yet. Each opens as it was before the agent and as it is now. **Accept** keeps a change, **Revert** puts the file back, **Accept all** keeps them all.',
    manual: 'the-ide',
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
    text: 'Ask for an answer or a change. The model and its thinking are at the top; each tool the agent uses is a card, with the diff under each edit. **Stop** ends a turn. The mode under the box says what runs without asking: **Ask**, **Auto-edit**, **Plan** or **Yolo**.',
    manual: 'modes',
  },
  status: {
    name: 'Status bar',
    text: 'Along the bottom: the git branch, the agent\'s changes and the terminal on the left; the cursor\'s line and column, the file\'s language, the mode, the model and the version (About: the licence and the source) on the right.',
    manual: 'the-ide',
  },
}

export const tasks: HelpTask[] = [
  {
    title: 'Ask the agent for a change',
    steps: [
      'Type what you want in the chat\'s box, on the right, and press Enter.',
      'When it asks before an edit or a command, read it, then press **Allow**, **Always for this session** or **Deny**.',
      'What it changed shows under **Agent changes** and in the open tabs.',
    ],
  },
  {
    title: 'Keep or undo what the agent changed',
    steps: ['Press **Agent changes** in the activity bar.', 'Click a file: it opens as it was before and as it is now.', 'Press **Accept** to keep it or **Revert** to put it back; **Accept all** keeps every change.'],
  },
  { title: 'Open a file fast', steps: [`Press ${modKey}+P.`, 'Type a few letters of its path.', 'Press Enter.'] },
  { title: 'Run a command yourself', steps: ['Press Ctrl+` or **Terminal** in the activity bar.', 'Type in the shell; **+** opens another one.'] },
  {
    title: 'Let the agent do more, or less, without asking',
    steps: ['Open the mode under the chat\'s box.', '**Ask** asks before edits and commands, **Auto-edit** only before commands, **Plan** changes nothing, **Yolo** asks nothing: use it only in a folder you can throw away.'],
  },
  { title: 'Go back to an earlier session', steps: ['Press **Chat** in the activity bar.', 'Click the session; **New session** starts another.'] },
]

export const keys: [keys: string, what: string][] = [
  [`${modKey}+S`, 'Save the file'],
  [`${modKey}+P`, 'Open a file by its name'],
  [`${modKey}+Shift+F`, 'Search the files'],
  [`${modKey}+Shift+E`, 'The Explorer'],
  ['Ctrl+`', 'Show or hide the terminal'],
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
  Sessions: 'sessions',
  Editor: 'editor',
  Terminal: 'terminal',
  Agent: 'chat',
  Chat: 'chat',
  'Status bar': 'status',
}
