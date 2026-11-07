import { api, ApiError } from './api'

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
  /** The files beside its SKILL.md (an imported skill's references, templates, scripts), by their paths. */
  files?: string[]
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
  /** What they may do beyond their own: share with the company, with their groups, and turn Arena's own skills off or on. */
  mayShareCompany: boolean
  mayShareGroups: boolean
  mayTurnBuiltIn: boolean
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
  command?: 'compact' | 'context'
}

/** What follows a "/" that starts the message, while it is one word; null otherwise. */
export function slashQuery(text: string): string | null {
  const m = /^\/([\w-]*)$/.exec(text)
  return m ? m[1]!.toLowerCase() : null
}

/** The / menu's lines for what was typed: the chat's commands first, then the skills that match. */
export function slashItems(query: string, skills: SkillItem[], canCompact: boolean, canShowContext = false): SlashItem[] {
  const commands: SlashItem[] = [
    ...(canCompact && 'compact'.startsWith(query) ? [{ key: 'cmd:compact', name: 'compact', title: 'Compact this chat', hint: 'Summarizes the older messages', command: 'compact' as const }] : []),
    ...(canShowContext && 'context'.startsWith(query) ? [{ key: 'cmd:context', name: 'context', title: 'What fills the context', hint: 'Each tool, skill, file and message, in tokens', command: 'context' as const }] : []),
  ]
  return [...commands, ...matchSkills(skills, query).map((s) => ({ key: s.id, name: s.name, title: s.title, hint: sourceLabel(s), skill: s }))]
}

/** The files of skills chosen to import: a .zip or a SKILL.md as they are, a folder's files by their paths in it. */
export function importForm(files: File[], choices?: SkillImportChoice[]): FormData {
  const form = new FormData()
  for (const f of files) form.append('files', f, f.webkitRelativePath || f.name)
  if (choices) form.append('choices', JSON.stringify(choices))
  return form
}

/** One skill of an import, as the preview shows it: what it brings, and what is in the way. */
export interface SkillPreview {
  folder: string
  name: string
  title: string
  description: string
  parameters: number
  files: { path: string; bytes: number }[]
  bytes: number
  /** Its files left out (not text, too large), each with why. */
  leftOut: string[]
  /** Why it cannot come in at all (no instructions, too large, a safeguard). */
  problem: string | null
  /** Why its name is no slash name: it comes in only renamed. */
  nameProblem: string | null
  /** The person's own skill of its name (in Code Arena, the folder it is in): replace it, rename the new one, or skip it. */
  clash: { id?: string; title: string; version?: number; origin?: string | null } | null
  /** Where another skill of its name comes from (group, company, builtin, plugin; Code Arena's sources): the new one comes first. */
  shadows: string | null
  /** A free name to rename it to. */
  suggested: string
  /** The safeguards masked part of it. */
  masked: boolean
}

export interface SkillImportPreview {
  skills: SkillPreview[]
  /** What was in the upload but in no skill (or not a path inside its folder). */
  leftOut: string[]
  /** How many more skills the person may have (Arena's limit; none on this computer). */
  room?: number
  /** What the preview could not do (Code Arena not signed in: secrets looked for there only). */
  note?: string | null
}

export type SkillImportAction = 'import' | 'replace' | 'rename' | 'skip'

export interface SkillImportChoice {
  folder: string
  action: SkillImportAction
  name?: string
}

export interface SkillImportResult {
  imported: { folder: string; id: string; name: string; replaced: boolean }[]
  skipped: { folder: string; name: string; reason: string }[]
}

export const previewImport = (files: File[]) => api<SkillImportPreview>('/api/skills/import/preview', { body: importForm(files) })
export const runImport = (files: File[], choices: SkillImportChoice[]) => api<SkillImportResult>('/api/skills/import', { body: importForm(files, choices) })

/** What an import does with a skill unless the person chooses otherwise: brings it in when nothing is in the way, else leaves it. */
export function defaultAction(s: SkillPreview): SkillImportAction {
  if (s.problem) return 'skip'
  return s.clash || s.nameProblem ? 'skip' : 'import'
}

/** Skills exported as files: a .zip of their folders, or one alone as its SKILL.md (md); named as the server names it. */
export async function exportSkills(ids: string[], format: 'zip' | 'md' = 'zip'): Promise<{ blob: Blob; fileName: string }> {
  const res = await fetch(`/api/skills/export?ids=${ids.map(encodeURIComponent).join(',')}${format === 'md' ? '&format=md' : ''}`, {
    credentials: 'same-origin',
    headers: { 'X-Requested-With': 'fetch' },
  })
  if (!res.ok) {
    const said = (await res.json().catch(() => ({}))) as { status?: string; error?: string }
    throw new ApiError(res.status, said.status ?? String(res.status), said.error ?? `The export failed (HTTP ${res.status}).`, said)
  }
  const named = /filename\*?=(?:UTF-8'')?"?([^";]+)"?/i.exec(res.headers.get('Content-Disposition') ?? '')?.[1]
  return { blob: await res.blob(), fileName: named ? decodeURIComponent(named) : format === 'md' ? 'SKILL.md' : 'skills.zip' }
}

/** "12 KB": a size in a few characters. */
export function sizeLabel(bytes: number): string {
  return bytes < 1000 ? `${bytes} B` : bytes < 1_000_000 ? `${Math.round(bytes / 100) / 10} KB` : `${Math.round(bytes / 100_000) / 10} MB`
}
