import { api, ApiError, ideUrl } from './http'

// VS Code extensions as code-arena keeps them (in its data folder): only what they declare, never their code. The page
// lists them, installs a .vsix the person picks, imports VS Code's own from this computer, and reads the files their
// contributions name (themes, icons, snippets, language configurations).

export interface ThemeContribution {
  label: string
  /** vs (light), vs-dark, hc-black or hc-light. */
  uiTheme: string
  path: string
  /** The name settings.json gives it, when it is not its label. */
  id?: string | null
}

export interface IconThemeContribution {
  id: string
  label: string
  path: string
}

export interface SnippetContribution {
  /** Its language; none: every language. */
  language?: string | null
  path: string
}

export interface LanguageContribution {
  id: string
  extensions?: string[]
  filenames?: string[]
  filenamePatterns?: string[]
  aliases?: string[]
  mimetypes?: string[]
  firstLine?: string | null
  /** Its language-configuration.json. */
  configuration?: string | null
}

export interface GrammarContribution {
  language?: string | null
  scopeName?: string | null
  path: string
}

/** What an extension brings, as the list says it (keybindings counted). */
export interface ExtensionContributes {
  themes: ThemeContribution[]
  iconThemes: IconThemeContribution[]
  snippets: SnippetContribution[]
  languages: LanguageContribution[]
  grammars: GrammarContribution[]
  keybindings: number
  configurationDefaults: Record<string, unknown>
}

/** An extension kept here. */
export interface ExtensionItem {
  id: string
  name: string
  displayName: string
  version: string
  publisher: string
  description?: string | null
  /** Its icon, one of its files. */
  icon?: string | null
  enabled: boolean
  /** vsix: sent from this page; vscode: copied from VS Code on this computer. */
  source: 'vsix' | 'vscode'
  contributes: ExtensionContributes
  /** What it has that Code Arena does not use, and why. */
  unused: string[]
}

/** Whose a contribution is: the extension and the version its files are of. */
interface Owned {
  extension: string
  version: string
}

export interface KeybindingContribution {
  command: string
  key: string
  mac?: string
  linux?: string
  win?: string
  when?: string
  args?: unknown
}

/** What the enabled extensions bring, together, each saying whose it is. */
export interface Contributions {
  themes: (ThemeContribution & Owned)[]
  iconThemes: (IconThemeContribution & Owned)[]
  snippets: (SnippetContribution & Owned)[]
  languages: (LanguageContribution & Owned)[]
  keybindings: (KeybindingContribution & Owned)[]
  configurationDefaults: ({ values: Record<string, unknown> } & Owned)[]
}

export const noContributions: Contributions = { themes: [], iconThemes: [], snippets: [], languages: [], keybindings: [], configurationDefaults: [] }

const plural = (n: number, one: string, many = `${one}s`) => `${n} ${n === 1 ? one : many}`

/** What an extension brings, in a few words: "1 colour theme: Night", "snippets (python)", "1 language (TOML)"… */
export function contributesSummary(c: ExtensionContributes | undefined): string[] {
  if (!c) return []
  const out: string[] = []
  if (c.themes.length) out.push(`${plural(c.themes.length, 'colour theme')}: ${c.themes.map((t) => t.label).join(', ')}`)
  if (c.iconThemes.length) out.push(`${plural(c.iconThemes.length, 'file icon theme')}: ${c.iconThemes.map((t) => t.label).join(', ')}`)
  if (c.snippets.length) out.push(`snippets (${[...new Set(c.snippets.map((s) => s.language || 'every language'))].join(', ')})`)
  if (c.languages.length) out.push(`${plural(c.languages.length, 'language')} (${c.languages.map((l) => l.aliases?.[0] ?? l.id).join(', ')})`)
  if (c.grammars.length) out.push(`${plural(c.grammars.length, 'grammar')} (kept, not used yet)`)
  if (c.keybindings) out.push(plural(c.keybindings, 'keybinding'))
  const defaults = Object.keys(c.configurationDefaults ?? {}).length
  if (defaults) out.push(plural(defaults, 'setting default'))
  return out
}

/** One of VS Code's own extensions on this computer (its newest copy). */
export interface LocalExtension extends Omit<ExtensionItem, 'enabled' | 'source' | 'icon' | 'contributes' | 'unused'> {
  contributes?: ExtensionContributes
  unused?: string[]
  /** VS Code, VSCodium or VS Code - Insiders. */
  from: string
  path: string
  /** It brings something Code Arena uses. */
  usable: boolean
  /** Why it cannot be imported. */
  problem?: string | null
  /** The version kept here already. */
  installed?: string | null
}

export interface Recommendation {
  id: string
  name?: string
  /** installed, built-in (Code Arena does its work), unsupported (it is code), local (VS Code has it here) or unknown. */
  status: 'installed' | 'built-in' | 'unsupported' | 'local' | 'unknown'
  note: string
}

export interface Recommended {
  /** Whether the folder has a .vscode/extensions.json. */
  file: boolean
  error?: string
  recommendations: Recommendation[]
  unwanted: string[]
}

export const extensionsQuery = {
  queryKey: ['code', 'extensions', 'list'] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<ExtensionItem[]>('/api/extensions', { signal }),
}

/** What the page applies; none when code-arena cannot say (an older one, or not answering). */
export const contributionsQuery = {
  queryKey: ['code', 'extensions', 'contributions'] as const,
  queryFn: async ({ signal }: { signal: AbortSignal }) => {
    try {
      return { ...noContributions, ...(await api<Partial<Contributions>>('/api/extensions/contributions', { signal })) }
    } catch (e) {
      if (e instanceof ApiError) return noContributions
      throw e
    }
  },
  staleTime: Infinity,
}

export const localExtensionsQuery = {
  queryKey: ['code', 'extensions', 'local'] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<LocalExtension[]>('/api/extensions/local', { signal }),
  staleTime: 30_000,
}

export const recommendedQuery = {
  queryKey: ['code', 'extensions', 'recommended'] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<Recommended>('/api/extensions/recommended', { signal }),
  staleTime: 10_000,
}

/** A .vsix the person chose, sent as its bytes. */
export const installVsix = (file: Blob) => api<ExtensionItem>('/api/extensions/install', { body: file })
export const importExtension = (id: string) => api<ExtensionItem>('/api/extensions/import', { body: { id } })
export const uninstallExtension = (id: string) => api('/api/extensions/uninstall', { body: { id } })
export const enableExtension = (id: string, enabled: boolean) => api<ExtensionItem>('/api/extensions/enable', { body: { id, enabled } })

/** The address of one of an extension's files (an icon, a font): the version makes an update's a new one. */
export const extensionFileUrl = (id: string, path: string, version?: string) =>
  ideUrl(`/api/extensions/file?id=${encodeURIComponent(id)}&path=${encodeURIComponent(path)}${version ? `&v=${encodeURIComponent(version)}` : ''}`)

/** One of an extension's files as text (a theme, snippets: JSON with comments). */
export async function extensionFileText(id: string, path: string, version?: string): Promise<string> {
  const res = await fetch(extensionFileUrl(id, path, version), { credentials: 'same-origin', headers: { 'X-Requested-With': 'fetch' } })
  const text = await res.text()
  if (!res.ok) {
    let said: { status?: string; error?: string } = {}
    try {
      said = JSON.parse(text) as typeof said
    } catch {
      // not JSON: the status says it
    }
    throw new ApiError(res.status, said.status ?? String(res.status), said.error ?? `${path} of ${id} could not be read (HTTP ${res.status}).`, said)
  }
  return text
}
