import { api } from './api'

export type SkillSharing = 'Personal' | 'Groups' | 'Company'

/** A skill's parameter: its name ({{name}} in the instructions), what it is, whether it must be given, and its value when it is not. */
export interface SkillParameter {
  name: string
  description?: string | null
  required: boolean
  default?: string | null
}

/**
 * A skill, as Claude Code's: a slash name, a title, when it is for (the model uses it on its own when a question fits,
 * unless it is kept for / only), instructions with {{parameters}} and $ARGUMENTS, and its parameters.
 */
export interface SkillItem {
  id: string
  name: string
  title: string
  description: string
  instructions: string
  parameters: SkillParameter[]
  autoUse: boolean
  sharing: SkillSharing
  groups: { id: string; name: string }[]
  /** mine: the person's own; group: shared with a group they are in; company: an admin's for everyone; builtin: Arena's own; plugin: a plugin's. */
  source: 'mine' | 'group' | 'company' | 'builtin' | 'plugin'
  /** One of Arena's own an admin turned off (only admins see it, to turn it on again). */
  off?: boolean
  /** Who shared it (a person's name), or the plugin it came with. */
  from: string | null
  canEdit: boolean
  updatedAt: string
  /** Learned from the person's work and not kept yet: not used until they keep it. */
  proposed?: boolean
  /** Its version: one more with each better one applied (or undone). */
  version?: number
  /** Learned from a chat or a Code Arena session, rather than written. */
  learned?: boolean
}

/** How one of the person's skills was learned or got better: waiting for them, applied (with Undo), or undone. */
export interface SkillChangeItem {
  id: string
  skillId: string
  action: 'learned' | 'improved'
  reason: string
  proposed: boolean
  at: string
  appliedAt: string | null
  undoneAt: string | null
  sourceId: string | null
  /** The instructions before (a better version) and after. */
  before: string | null
  after: string | null
}

export const applySkillChange = (id: string) => api(`/api/skills/changes/${id}/apply`, { body: {} })
export const dismissSkillChange = (id: string) => api(`/api/skills/changes/${id}/dismiss`, { body: {} })
export const undoSkillChange = (id: string) => api(`/api/skills/changes/${id}/undo`, { body: {} })

export interface SkillLibrary {
  isAdmin: boolean
  /** How the person's skills learned and got better, waiting ones first. */
  changes?: SkillChangeItem[]
  /** The groups the person may share a skill with. */
  groups: { id: string; name: string }[]
  skills: SkillItem[]
}

/** A skill used with a message: its name and its parameters' values (the message's words are its $ARGUMENTS); its title, for the page. */
export interface SkillUse {
  name: string
  args: Record<string, string>
  title?: string
}

/** A skill as a sent message carries it: which, what it was given, and its instructions as filled in then. */
export interface SkillCall {
  name: string
  title: string
  args: Record<string, string>
  instructions: string
}

export const skillsQuery = {
  queryKey: ['skills'],
  queryFn: ({ signal }: { signal: AbortSignal }) => api<SkillLibrary>('/api/skills', { signal }),
}

export const Arguments = '$ARGUMENTS'

const variable = /\{\{\s*([A-Za-z][A-Za-z0-9_-]{0,39})\s*\}\}/g

/** The {{variables}} of instructions, in the order they first appear (as the server reads them). */
export function variablesOf(text: string): string[] {
  return [...new Set([...text.matchAll(variable)].map((m) => m[1]!))]
}

/** "file_path" as a field's label: "File path". */
export function parameterLabel(name: string): string {
  const words = name.replace(/[_-]+/g, ' ').trim()
  return words.charAt(0).toUpperCase() + words.slice(1)
}

/** Whether a parameter must be filled in to send: required, with no default. */
export function mustFill(p: SkillParameter): boolean {
  return p.required && !p.default?.trim()
}

/** The parameters a skill asks for that are left empty. */
export function missingOf(skill: SkillItem, values: Record<string, string>): string[] {
  return skill.parameters.filter((p) => mustFill(p) && !values[p.name]?.trim()).map((p) => p.name)
}

/** Skills whose slash name, title or description has the words typed after "/": names that start with them first. */
export function matchSkills(skills: SkillItem[], query: string): SkillItem[] {
  const q = query.toLowerCase()
  const score = (s: SkillItem) =>
    s.name.startsWith(q) ? 0 : s.name.includes(q) ? 1 : s.title.toLowerCase().includes(q) ? 2 : s.description.toLowerCase().includes(q) ? 3 : -1
  return skills
    .filter((s) => !s.off && !s.proposed)
    .map((s, i) => ({ s, i, score: score(s) }))
    .filter((x) => x.score >= 0)
    .sort((a, b) => a.score - b.score || a.i - b.i)
    .map((x) => x.s)
}

/** Where a skill comes from, in a word or two. */
export function sourceLabel(s: SkillItem): string {
  if (s.source === 'mine') return s.sharing === 'Groups' ? `Yours · ${s.groups.map((g) => g.name).join(', ')}` : 'Yours'
  if (s.source === 'group') return s.groups.map((g) => g.name).join(', ') || 'A group'
  if (s.source === 'company') return 'Company'
  if (s.source === 'builtin') return 'Arena'
  return s.from ?? 'Plugin'
}

/** One line of the / menu: a skill, or one of the chat's own commands. */
export interface SlashItem {
  key: string
  name: string
  title: string
  /** Where it comes from (a skill), or what it does (a command). */
  hint: string
  skill?: SkillItem
  command?: 'compact'
}

/** What follows a "/" that starts the message, while it is one word; null otherwise. */
export function slashQuery(text: string): string | null {
  const m = /^\/([\w-]*)$/.exec(text)
  return m ? m[1]!.toLowerCase() : null
}

/** The / menu's lines for what was typed: the chat's commands first, then the skills that match. */
export function slashItems(query: string, skills: SkillItem[], canCompact: boolean): SlashItem[] {
  const commands: SlashItem[] = canCompact && 'compact'.startsWith(query) ? [{ key: 'cmd:compact', name: 'compact', title: 'Compact this chat', hint: 'Summarizes the older messages', command: 'compact' }] : []
  return [...commands, ...matchSkills(skills, query).map((s) => ({ key: s.id, name: s.name, title: s.title, hint: sourceLabel(s), skill: s }))]
}
