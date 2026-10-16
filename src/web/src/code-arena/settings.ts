import { useQuery, useQueryClient } from '@tanstack/react-query'
import { createContext, createElement, use, useCallback, useMemo, type ReactNode } from 'react'
import { ApiError } from '@/lib/api'
import { useContributions } from './extensions'
import { parse, setProperty } from './jsonc'
import { projectFileQuery, userFileQuery, writeFile, writeUserFile, type OpenedFile, type UserFile } from './ide-api'
import { check, monacoLanguageId, settingDef, settingDefs } from './settings-schema'

/** The project's own settings, as VS Code keeps them. */
export const workspaceSettingsPath = '.vscode/settings.json'

/** One settings file, read: its values by setting, its languages' own, and what in it is not taken. */
export interface Layer {
  values: Record<string, unknown>
  /** "[python]": {…}, by language id ("[a][b]" gives both the same). */
  languages: Record<string, Record<string, unknown>>
  problems: string[]
}

const empty: Layer = { values: {}, languages: {}, problems: [] }

/** Nested objects as VS Code reads them: {"editor": {"fontSize": 14}} is "editor.fontSize". */
function flatten(obj: Record<string, unknown>, prefix = '', out: Record<string, unknown> = {}) {
  for (const [k, v] of Object.entries(obj)) {
    const key = prefix ? `${prefix}.${k}` : k
    const nested = v && typeof v === 'object' && !Array.isArray(v) && !settingDef.has(key) && !k.startsWith('[') && settingDefs.some((d) => d.key.startsWith(`${key}.`))
    if (nested) flatten(v as Record<string, unknown>, key, out)
    else out[key] = v
  }
  return out
}

/** A settings file's text read (the project's when workspace): a value that is not of its setting's kind is not taken. */
export function readLayer(text: string | null | undefined, workspace = false): Layer {
  if (!text?.trim()) return empty
  const { value, errors } = parse(text)
  if (errors.length > 0) return { ...empty, problems: [`The file is not JSON: ${errors[0]!.message}`] }
  return layerOf(value, workspace)
}

/**
 * The extensions' "configurationDefaults", one over another in their order: read as a project's settings are (a setting
 * that is the person's alone is not theirs to set).
 */
export function extensionDefaults(list: { values: Record<string, unknown> }[]): Layer {
  if (list.length === 0) return empty
  const all = list.map((d) => layerOf(d.values, true))
  return {
    values: Object.assign({}, ...all.map((l) => l.values)) as Record<string, unknown>,
    languages: all.reduce<Layer['languages']>((out, l) => {
      for (const [id, v] of Object.entries(l.languages)) out[id] = { ...out[id], ...v }
      return out
    }, {}),
    problems: all.flatMap((l) => l.problems),
  }
}

/** Settings as an object holds them, read as readLayer reads a file's. */
function layerOf(value: unknown, workspace: boolean): Layer {
  if (!value || typeof value !== 'object' || Array.isArray(value)) return { ...empty, problems: ['The file holds one object: {"setting": value, …}.'] }
  const problems: string[] = []
  const take = (all: Record<string, unknown>, into: Record<string, unknown>, language?: string) => {
    for (const [key, v] of Object.entries(all)) {
      const d = settingDef.get(key)
      if (d) {
        const why = check(d, v)
        if (why) {
          problems.push(why)
          continue
        }
        if (workspace && d.scope === 'application') {
          problems.push(`"${key}" is the person's own: a project's settings cannot set it.`)
          continue
        }
        if (language && d.scope !== 'language') {
          problems.push(`"${key}" cannot be set for one language.`)
          continue
        }
      }
      into[key] = v
    }
  }
  const values: Record<string, unknown> = {}
  const languages: Record<string, Record<string, unknown>> = {}
  // VS Code's ids Monaco reads as one of its own ("[typescriptreact]" for a .tsx file, which Monaco reads as typescript)
  // go under Monaco's id; a block written with Monaco's id wins over them.
  const aliased: Record<string, Record<string, unknown>> = {}
  const flat = flatten(value as Record<string, unknown>)
  for (const [key, v] of Object.entries(flat)) {
    const ids = /^(\[[^\]]+\])+$/.test(key) ? [...key.matchAll(/\[([^\]]+)\]/g)].map((m) => m[1]!) : null
    if (!ids) continue
    if (!v || typeof v !== 'object' || Array.isArray(v)) {
      problems.push(`"${key}" holds an object of settings.`)
      continue
    }
    const own: Record<string, unknown> = {}
    take(flatten(v as Record<string, unknown>), own, ids[0])
    for (const id of ids) {
      const into = monacoLanguageId(id) === id ? languages : aliased
      into[monacoLanguageId(id)] = { ...into[monacoLanguageId(id)], ...own }
    }
  }
  for (const [id, own] of Object.entries(aliased)) languages[id] = { ...own, ...languages[id] }
  take(Object.fromEntries(Object.entries(flat).filter(([k]) => !k.startsWith('['))), values)
  return { values, languages, problems }
}

const defaults = Object.fromEntries(settingDefs.map((d) => [d.key, d.default]))

/**
 * Every setting's value: the default, the extensions' defaults, then the person's, then the project's; then, for the
 * language, the extensions', the person's and the project's (a language's own wins, as in VS Code).
 */
export function effective(user: Layer, workspace: Layer, language?: string, extension: Layer = empty): Record<string, unknown> {
  return {
    ...defaults,
    ...extension.values,
    ...user.values,
    ...workspace.values,
    ...(language ? extension.languages[language] : undefined),
    ...(language ? user.languages[language] : undefined),
    ...(language ? workspace.languages[language] : undefined),
  }
}

export type Target = 'user' | 'workspace'

export interface SettingsApi {
  /** The settings read (both files answered, or failed). */
  ready: boolean
  /** A setting's value, for a language when given. */
  get: <T = unknown>(key: string, language?: string) => T
  /** Every setting's value, for a language when given. */
  all: (language?: string) => Record<string, unknown>
  user: Layer
  workspace: Layer
  /** The enabled extensions' configuration defaults. */
  extension: Layer
  userText: string
  workspaceText: string | null
  /** Sets a setting in the person's or the project's settings.json (for a language when given; undefined: back to the default), comments kept. */
  update: (key: string, value: unknown, target?: Target, language?: string) => Promise<void>
  /** Reads both files again (one of them was saved in the editor, or changed by the agent). */
  reload: () => Promise<void>
}

const SettingsContext = createContext<SettingsApi | null>(null)

export function useSettings() {
  const ctx = use(SettingsContext)
  if (!ctx) throw new Error('useSettings needs <SettingsProvider>')
  return ctx
}

/** A setting's value where the settings are given; its default where they are not (a test of one part alone). */
export function useSetting<T = unknown>(key: string, language?: string): T {
  const ctx = use(SettingsContext)
  return (ctx ? ctx.get(key, language) : settingDef.get(key)?.default) as T
}

/**
 * The person's settings.json and the project's .vscode/settings.json, read and kept up to date for the whole page, over
 * the enabled extensions' configuration defaults.
 */
export function SettingsProvider({ children }: { children: ReactNode }) {
  const queryClient = useQueryClient()
  const userQ = useQuery(userFileQuery('settings.json'))
  const projectQ = useQuery(projectFileQuery(workspaceSettingsPath))
  const contributions = useContributions()
  const userText = userQ.data?.text ?? ''
  const workspaceText = projectQ.data?.text ?? null
  const user = useMemo(() => readLayer(userText), [userText])
  const workspace = useMemo(() => readLayer(workspaceText, true), [workspaceText])
  const defaultsOf = contributions.data?.configurationDefaults
  const extension = useMemo(() => extensionDefaults(defaultsOf ?? []), [defaultsOf])

  const reload = useCallback(async () => {
    await Promise.all([
      queryClient.invalidateQueries({ queryKey: userFileQuery('settings.json').queryKey }),
      queryClient.invalidateQueries({ queryKey: projectFileQuery(workspaceSettingsPath).queryKey }),
    ])
  }, [queryClient])

  const update = useCallback(
    async (key: string, value: unknown, target: Target = 'user', language?: string) => {
      const path = language ? [`[${language}]`, key] : [key]
      for (let attempt = 0; ; attempt++) {
        try {
          if (target === 'user') {
            const q = userFileQuery('settings.json')
            const now: UserFile = queryClient.getQueryData<UserFile>(q.queryKey) ?? (await queryClient.fetchQuery(q))
            const text = setProperty(now.text, path, value)
            const saved = await writeUserFile('settings.json', text, now.version)
            queryClient.setQueryData(q.queryKey, { ...now, text, version: saved.version })
          } else {
            const q = projectFileQuery(workspaceSettingsPath)
            const cached = queryClient.getQueryData<OpenedFile | null>(q.queryKey)
            const now: OpenedFile | null = cached === undefined ? await queryClient.fetchQuery(q) : cached
            const text = setProperty(now?.text ?? '', path, value)
            const saved = await writeFile(workspaceSettingsPath, text, now?.version ?? 'none')
            const next: OpenedFile = { path: workspaceSettingsPath, size: text.length, text, version: saved.version }
            queryClient.setQueryData(q.queryKey, next)
          }
          window.dispatchEvent(new CustomEvent('code-arena:settings-written', { detail: target }))
          return
        } catch (e) {
          // Changed meanwhile (saved in the editor, by another window): read again, set again, once.
          if (attempt === 0 && e instanceof ApiError && e.http === 409) {
            await reload()
            continue
          }
          throw e
        }
      }
    },
    [queryClient, reload],
  )

  const value = useMemo<SettingsApi>(() => {
    const cache = new Map<string, Record<string, unknown>>()
    const all = (language?: string) => {
      const k = language ?? ''
      let v = cache.get(k)
      if (!v) cache.set(k, (v = effective(user, workspace, language, extension)))
      return v
    }
    return {
      // The extensions' too: a theme of theirs is known as the page opens.
      ready: !userQ.isPending && !projectQ.isPending && !contributions.isPending,
      get: <T,>(key: string, language?: string) => all(language)[key] as T,
      all,
      user,
      workspace,
      extension,
      userText,
      workspaceText,
      update,
      reload,
    }
  }, [user, workspace, extension, userText, workspaceText, userQ.isPending, projectQ.isPending, contributions.isPending, update, reload])

  return createElement(SettingsContext, { value }, children)
}
