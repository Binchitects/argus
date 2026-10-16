import { useQuery } from '@tanstack/react-query'
import type * as Monaco from 'monaco-editor'
import { useLayoutEffect, useSyncExternalStore } from 'react'
import { toast } from '@/components/ui/toaster'
import { errorMessage } from '@/lib/api'
import type { MonacoModule } from './editor-state'
import { contributionsQuery, extensionFileText, extensionFileUrl, noContributions, type Contributions, type KeybindingContribution, type LanguageContribution, type SnippetContribution } from './extensions-api'
import { fontCharacterOf, iconIdFor, readIconTheme, vscodeLanguageOf, type IconTheme, type IconThemeDocument } from './icon-theme'
import { parse } from './jsonc'
import { toLanguageConfiguration } from './language-config'
import { setExtensionIconThemes, setExtensionThemes, type ColorTheme } from './settings-schema'
import { readSnippets, snippetSuggestions, type Snippet } from './snippets'
import { loadColorTheme, resolvePath, toMonacoTheme } from './vscode-theme'

/**
 * What the enabled VS Code extensions bring, applied to the page: their colour themes (offered with Code Arena's, and
 * given to Monaco once read), file icon themes, snippets (a completion provider per language), languages (with their
 * language-configuration.json), keybindings (for the commands Code Arena has) and configuration defaults (a settings
 * layer under the person's). Their files are read when first needed, from code-arena; nothing of theirs runs.
 */

/** A colour theme an extension brings. */
interface ThemeEntry extends ColorTheme {
  extension: string
  version: string
  path: string
  uiTheme: string
}

interface IconThemeEntry {
  id: string
  label: string
  extension: string
  version: string
  path: string
}

/** A colour theme read: Monaco's, and its own colours by VS Code's ids (the page's and the terminal's come from them). */
interface LoadedTheme {
  monaco: Monaco.editor.IStandaloneThemeData
  colors: Record<string, string>
}

/** An icon theme read: its lookups, where its files are, and its fonts' names on the page. */
export interface LoadedIconTheme {
  id: string
  theme: IconTheme
  extension: string
  version: string
  /** The folder of its file: its icons' and fonts' paths are from there. */
  dir: string
  fonts: Map<string, string>
}

const state = {
  version: 0,
  key: '',
  contributions: noContributions,
  themes: [] as ThemeEntry[],
  iconThemes: [] as IconThemeEntry[],
  /** Read themes, by extension, version and file ('failed': it could not be read). */
  loaded: new Map<string, LoadedTheme | 'failed'>(),
  loading: new Map<string, Promise<void>>(),
  icons: new Map<string, LoadedIconTheme | 'failed'>(),
  /** The icon theme the settings name, once read; and whether the colour theme is light (a theme's light icons). */
  activeIcons: null as LoadedIconTheme | null,
  activeIconId: '',
  light: false,
  monaco: null as MonacoModule | null,
  /** Monaco's themes given, by Monaco's name: the read theme each is. */
  defined: new Map<string, string>(),
  /** The languages Monaco had before any extension's. */
  ownLanguages: new Set<string>(),
  registered: new Set<string>(),
  /** Snippet providers and language configurations given to Monaco, taken back when the extensions change. */
  disposables: [] as { dispose(): void }[],
  snippets: new Map<string, Promise<Snippet[]>>(),
  /** Language configurations read, by extension, version and file. */
  configs: new Map<string, Promise<unknown>>(),
}

const listeners = new Set<() => void>()
const changed = () => {
  state.version++
  for (const l of listeners) l()
}
const subscribe = (f: () => void) => {
  listeners.add(f)
  return () => {
    listeners.delete(f)
  }
}

/** The store's version: changes as extensions change and as their themes are read. */
export function useExtensionsVersion() {
  return useSyncExternalStore(subscribe, () => state.version)
}

const fileKey = (o: { extension: string; version: string; path: string }) => `${o.extension}@${o.version}/${o.path}`
const dirOf = (path: string) => (path.includes('/') ? path.slice(0, path.lastIndexOf('/')) : '')
/** Monaco takes theme names of letters, digits and "-". */
const monacoName = (extension: string, name: string) => `ext-${`${extension}-${name}`.toLowerCase().replace(/[^a-z0-9]+/g, '-')}`
const kindOf = (uiTheme: string): 'light' | 'dark' => (uiTheme === 'vs' || uiTheme === 'hc-light' ? 'light' : 'dark')

let keybindingListener: ((rules: KeybindingContribution[]) => void) | null = null

/** The keymap takes the extensions' keys from here (for its own platform: "mac", "linux", "win" over "key"). */
export function onExtensionKeybindings(f: (rules: KeybindingContribution[]) => void) {
  keybindingListener = f
  f(keybindingsNow())
}

function keybindingsNow(): KeybindingContribution[] {
  const platform = typeof navigator === 'undefined' ? '' : navigator.platform
  const mac = /Mac|iPhone|iPad/.test(platform)
  const win = platform.startsWith('Win')
  return state.contributions.keybindings.map((r) => ({ ...r, key: (mac ? r.mac : win ? r.win : r.linux) ?? r.key }))
}

/** The names the settings read (themes, icon themes): set before anything renders with them. */
function register(c: Contributions) {
  const themes: ThemeEntry[] = []
  for (const t of c.themes) {
    const name = t.id || t.label
    if (!name || themes.some((x) => x.name === name)) continue
    themes.push({ name, kind: kindOf(t.uiTheme), monaco: monacoName(t.extension, name), extension: t.extension, version: t.version, path: t.path, uiTheme: t.uiTheme })
  }
  const iconThemes: IconThemeEntry[] = []
  for (const t of c.iconThemes) if (t.id && !iconThemes.some((x) => x.id === t.id)) iconThemes.push({ id: t.id, label: t.label || t.id, extension: t.extension, version: t.version, path: t.path })
  state.themes = themes
  state.iconThemes = iconThemes
  setExtensionThemes(themes)
  setExtensionIconThemes(iconThemes.map((t) => ({ id: t.id, label: t.label, extension: t.extension })))
}

/** What the enabled extensions bring now: the names at once, then (outside the render) Monaco and the keymap. */
function set(c: Contributions, notify: boolean) {
  const key = JSON.stringify(c)
  if (key === state.key) return
  state.key = key
  state.contributions = c
  register(c)
  if (!notify) return
  keybindingListener?.(keybindingsNow())
  applyToMonaco()
  changed()
}

/**
 * The extensions' contributions, read for the page (the settings wait for them: a theme of theirs is known as the page
 * opens). Their names are taken as soon as they come; Monaco, the keymap and the page's parts follow before it paints.
 */
export function useContributions() {
  const q = useQuery(contributionsQuery)
  const data = q.data
  if (data) set(data, false)
  useLayoutEffect(() => {
    if (!data) return
    // Taken during the render already (names only): applied now.
    state.key = ''
    set(data, true)
  }, [data])
  return q
}

// ----- colour themes -----

/** Reads an extension's colour theme (once), and gives it to Monaco when Monaco is there. */
export function ensureTheme(name: string): Promise<void> {
  const t = state.themes.find((x) => x.name === name)
  if (!t) return Promise.resolve()
  const key = fileKey(t)
  if (state.loaded.has(key)) return Promise.resolve()
  const going = state.loading.get(key)
  if (going) return going
  const read = (async () => {
    try {
      const data = await loadColorTheme(t.path, (path) => extensionFileText(t.extension, path, t.version))
      state.loaded.set(key, { monaco: toMonacoTheme(data, t.uiTheme), colors: data.colors })
    } catch (e) {
      toast.error(`The colour theme ${t.name} (${t.extension}) could not be read: ${errorMessage(e)}`)
      state.loaded.set(key, 'failed')
    }
    state.loading.delete(key)
    defineThemes()
    changed()
  })()
  state.loading.set(key, read)
  return read
}

function defineThemes() {
  const m = state.monaco
  if (!m) return
  for (const t of state.themes) {
    const key = fileKey(t)
    const data = state.loaded.get(key)
    if (!data || data === 'failed' || state.defined.get(t.monaco) === key) continue
    try {
      m.monaco.editor.defineTheme(t.monaco, data.monaco)
      state.defined.set(t.monaco, key)
    } catch (e) {
      console.warn(`Monaco did not take the theme ${t.name}`, e)
      state.loaded.set(key, 'failed')
    }
  }
}

/** Whether an extension's theme is ready in Monaco (else the editor shows Code Arena's of its kind meanwhile). */
export function themeReady(name: string): boolean {
  const t = state.themes.find((x) => x.name === name)
  return !!t && state.defined.get(t.monaco) === fileKey(t)
}

/** An extension's theme's own colours, once read (null: not one, not read yet, or it could not be). */
export function themeColors(name: string): Record<string, string> | null {
  const t = state.themes.find((x) => x.name === name)
  const data = t ? state.loaded.get(fileKey(t)) : undefined
  return data && data !== 'failed' ? data.colors : null
}

// ----- file icon themes -----

let fontStyle: HTMLStyleElement | null = null

/** The icon theme's fonts on the page (@font-face, from its files), each under a name of its own. */
function addFonts(t: IconThemeEntry, dir: string, doc: IconThemeDocument): Map<string, string> {
  const names = new Map<string, string>()
  const rules: string[] = []
  for (const f of doc.fonts ?? []) {
    if (!f || typeof f.id !== 'string' || !Array.isArray(f.src)) continue
    const family = `ca-icons-${`${t.extension}-${t.id}-${f.id}`.toLowerCase().replace(/[^a-z0-9]+/g, '-')}`
    const src = f.src
      .map((s) => {
        const path = s && typeof s.path === 'string' ? resolvePath(dir, s.path) : null
        return path ? `url("${extensionFileUrl(t.extension, path, t.version)}")${typeof s.format === 'string' && /^[\w-]+$/.test(s.format) ? ` format("${s.format}")` : ''}` : null
      })
      .filter(Boolean)
    if (src.length === 0) continue
    names.set(f.id, family)
    const weight = typeof f.weight === 'string' && /^[\w ]+$/.test(f.weight) ? f.weight : 'normal'
    const style = typeof f.style === 'string' && /^[\w ]+$/.test(f.style) ? f.style : 'normal'
    rules.push(`@font-face { font-family: "${family}"; src: ${src.join(', ')}; font-weight: ${weight}; font-style: ${style}; font-display: block; }`)
  }
  if (rules.length > 0) {
    fontStyle ??= document.head.appendChild(document.createElement('style'))
    fontStyle.dataset.codeArena = 'icon-fonts'
    fontStyle.textContent = `${fontStyle.textContent ?? ''}\n${rules.join('\n')}`
  }
  return names
}

async function loadIconTheme(t: IconThemeEntry) {
  const key = fileKey(t)
  if (state.icons.has(key)) return
  state.icons.set(key, 'failed')
  try {
    const { value } = parse(await extensionFileText(t.extension, t.path, t.version))
    if (!value || typeof value !== 'object' || Array.isArray(value)) throw new Error(`${t.path} holds no icon theme.`)
    const doc = value as IconThemeDocument
    const dir = dirOf(t.path)
    state.icons.set(key, { id: t.id, theme: readIconTheme(doc), extension: t.extension, version: t.version, dir, fonts: addFonts(t, dir, doc) })
  } catch (e) {
    toast.error(`The file icon theme ${t.label} (${t.extension}) could not be read: ${errorMessage(e)}`)
  }
  pickIcons()
  changed()
}

function pickIcons() {
  const t = state.iconThemes.find((x) => x.id === state.activeIconId)
  const loaded = t ? state.icons.get(fileKey(t)) : undefined
  state.activeIcons = loaded && loaded !== 'failed' ? loaded : null
}

/** The icon theme the settings name (read when first named), and whether the colour theme now is light. */
export function setIconTheme(id: string, light: boolean) {
  if (id === state.activeIconId && light === state.light) return
  state.activeIconId = id
  state.light = light
  const t = state.iconThemes.find((x) => x.id === id)
  pickIcons()
  if (t && !state.icons.has(fileKey(t))) void loadIconTheme(t)
  changed()
}

/** How a file's or a folder's icon shows with the icon theme now: an image, or a character of its font. */
export type ThemedIcon = { kind: 'image'; src: string } | { kind: 'font'; character: string; family: string; color?: string; size?: string }

/** The icon theme now (null: Code Arena's own icons), kept up to date. */
export function useIconTheme(): LoadedIconTheme | null {
  useSyncExternalStore(subscribe, () => state.version)
  return state.activeIcons
}

/** A file's or a folder's icon from an icon theme; null when the theme has none for it. */
export function themedIcon(t: LoadedIconTheme, q: { kind: 'file' | 'folder'; path: string; open?: boolean }): ThemedIcon | null {
  const name = q.path.slice(q.path.lastIndexOf('/') + 1)
  const id = iconIdFor(t.theme, { kind: q.kind, name, open: q.open, light: state.light, languageId: q.kind === 'file' ? vscodeLanguageOf(name, state.contributions.languages) : undefined })
  const def = id ? t.theme.doc.iconDefinitions?.[id] : undefined
  if (!def) return null
  if (typeof def.iconPath === 'string') {
    const path = resolvePath(t.dir, def.iconPath)
    return path ? { kind: 'image', src: extensionFileUrl(t.extension, path, t.version) } : null
  }
  if (typeof def.fontCharacter === 'string') {
    const family = t.fonts.get(def.fontId ?? '') ?? t.fonts.values().next().value
    if (!family) return null
    return { kind: 'font', character: fontCharacterOf(def.fontCharacter), family, color: typeof def.fontColor === 'string' ? def.fontColor : undefined, size: typeof def.fontSize === 'string' ? def.fontSize : undefined }
  }
  return null
}

// ----- Monaco: languages, their configurations, snippets, themes -----

/** Monaco, once loaded: given the extensions' languages, snippets and themes, and again each time they change. */
export function installExtensions(m: MonacoModule) {
  if (state.monaco === m) return
  state.monaco = m
  for (const l of m.monaco.languages.getLanguages()) state.ownLanguages.add(l.id)
  applyToMonaco()
  changed()
}

function applyToMonaco() {
  const m = state.monaco
  if (!m) return
  for (const d of state.disposables.splice(0)) d.dispose()
  const { languages } = m.monaco
  for (const l of state.contributions.languages) {
    // Monaco cannot take a language back: one an extension brought stays until the page reloads.
    const point = { id: l.id, extensions: l.extensions, filenames: l.filenames, filenamePatterns: l.filenamePatterns, aliases: l.aliases, mimetypes: l.mimetypes, ...(l.firstLine ? { firstLine: l.firstLine } : {}) }
    const key = JSON.stringify(point)
    if (!state.registered.has(key)) {
      state.registered.add(key)
      languages.register(point)
    }
    // A language Monaco has keeps its own configuration; a new one gets its extension's.
    if (l.configuration && !state.ownLanguages.has(l.id)) void configure(m, l)
  }
  const byLanguage = new Map<string, (SnippetContribution & { extension: string; version: string })[]>()
  for (const s of state.contributions.snippets) {
    const language = s.language || '*'
    byLanguage.set(language, [...(byLanguage.get(language) ?? []), s])
  }
  for (const [language, files] of byLanguage) {
    state.disposables.push(
      languages.registerCompletionItemProvider(language, {
        provideCompletionItems: async (model, position) => {
          const all = (await Promise.all(files.map(snippetsOf))).flat()
          return { suggestions: snippetSuggestions(languages, all, model, position) }
        },
      }),
    )
  }
  state.defined.clear()
  defineThemes()
}

async function configure(m: MonacoModule, l: LanguageContribution & { extension: string; version: string }) {
  const contributions = state.contributions
  const key = fileKey({ ...l, path: l.configuration! })
  let read = state.configs.get(key)
  if (!read) {
    read = extensionFileText(l.extension, l.configuration!, l.version).then((text) => parse(text).value)
    state.configs.set(key, read)
  }
  try {
    const value = await read
    // The extensions changed meanwhile: what replaced them applies.
    if (state.contributions !== contributions || state.monaco !== m) return
    state.disposables.push(m.monaco.languages.setLanguageConfiguration(l.id, toLanguageConfiguration(value)))
  } catch (e) {
    console.warn(`The configuration of ${l.id} (${l.extension}) could not be read`, e)
  }
}

/** A snippets file, read once (none when it cannot be). */
function snippetsOf(s: SnippetContribution & { extension: string; version: string }): Promise<Snippet[]> {
  const key = fileKey(s)
  let going = state.snippets.get(key)
  if (!going) {
    going = extensionFileText(s.extension, s.path, s.version).then(readSnippets, (e: unknown) => {
      console.warn(`The snippets ${s.path} of ${s.extension} could not be read`, e)
      return []
    })
    state.snippets.set(key, going)
  }
  return going
}
