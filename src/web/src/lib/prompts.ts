import { api } from './api'

export type PromptSharing = 'Personal' | 'Groups' | 'Company'

/** A prompt of the library: a slash name, a title, and a text with {{variables}} filled in when it is used. */
export interface PromptItem {
  id: string
  name: string
  title: string
  text: string
  variables: string[]
  sharing: PromptSharing
  groups: { id: string; name: string }[]
  /** mine: the person's own; group: shared with a group they are in; company: an admin's for everyone; plugin: a plugin's. */
  source: 'mine' | 'group' | 'company' | 'plugin'
  /** Who shared it (a person's name), or the plugin it came with. */
  from: string | null
  canEdit: boolean
  updatedAt: string
}

export interface PromptLibrary {
  isAdmin: boolean
  /** The groups the person may share a prompt with. */
  groups: { id: string; name: string }[]
  prompts: PromptItem[]
}

export const promptsQuery = {
  queryKey: ['prompts'],
  queryFn: ({ signal }: { signal: AbortSignal }) => api<PromptLibrary>('/api/prompts', { signal }),
}

const variable = /\{\{\s*([A-Za-z][A-Za-z0-9_-]{0,39})\s*\}\}/g

/** The {{variables}} of a text, in the order they first appear (as the server reads them). */
export function variablesOf(text: string): string[] {
  return [...new Set([...text.matchAll(variable)].map((m) => m[1]!))]
}

/** The text with each {{variable}} replaced by its value; one without a value stays as written. */
export function fillPrompt(text: string, values: Record<string, string>): string {
  return text.replace(variable, (whole, name: string) => (name in values ? values[name]!.trim() : whole))
}

/** "file_path" as a field's label: "File path". */
export function variableLabel(name: string): string {
  const words = name.replace(/[_-]+/g, ' ').trim()
  return words.charAt(0).toUpperCase() + words.slice(1)
}

/** Prompts whose slash name or title has the words typed after "/": names that start with them first. */
export function matchPrompts(prompts: PromptItem[], query: string): PromptItem[] {
  const q = query.toLowerCase()
  const score = (p: PromptItem) => (p.name.startsWith(q) ? 0 : p.name.includes(q) ? 1 : p.title.toLowerCase().includes(q) ? 2 : -1)
  return prompts
    .map((p, i) => ({ p, i, s: score(p) }))
    .filter((x) => x.s >= 0)
    .sort((a, b) => a.s - b.s || a.i - b.i)
    .map((x) => x.p)
}

/** Where a prompt comes from, in a word or two. */
export function sourceLabel(p: PromptItem): string {
  if (p.source === 'mine') return p.sharing === 'Groups' ? `Yours · ${p.groups.map((g) => g.name).join(', ')}` : 'Yours'
  if (p.source === 'group') return p.groups.map((g) => g.name).join(', ') || 'A group'
  if (p.source === 'company') return 'Company'
  return p.from ?? 'Plugin'
}

/** One line of the / menu: a prompt of the library, or one of the chat's own commands. */
export interface SlashItem {
  key: string
  name: string
  title: string
  /** Where it comes from (a prompt), or what it does (a command). */
  hint: string
  prompt?: PromptItem
  command?: 'compact'
}

/** What follows a "/" that starts the message, while it is one word; null otherwise. */
export function slashQuery(text: string): string | null {
  const m = /^\/([\w-]*)$/.exec(text)
  return m ? m[1]!.toLowerCase() : null
}

/** The / menu's lines for what was typed: the chat's commands first, then the prompts that match. */
export function slashItems(query: string, prompts: PromptItem[], canCompact: boolean): SlashItem[] {
  const commands: SlashItem[] = canCompact && 'compact'.startsWith(query) ? [{ key: 'cmd:compact', name: 'compact', title: 'Compact this chat', hint: 'Summarizes the older messages', command: 'compact' }] : []
  return [...commands, ...matchPrompts(prompts, query).map((p) => ({ key: p.id, name: p.name, title: p.title, hint: sourceLabel(p), prompt: p }))]
}
