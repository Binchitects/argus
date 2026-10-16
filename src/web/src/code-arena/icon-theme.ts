// VS Code's file icon themes (an extension's iconThemes: Seti, Material Icon Theme…): icon definitions (an image, or a
// character of the theme's font), and which one each file and folder gets — by its name, its extension (the longest
// first: "test.ts" before "ts"), its language, else the default — with a light theme's own choices over those.

export interface IconDefinition {
  iconPath?: string
  fontCharacter?: string
  fontColor?: string
  fontSize?: string
  fontId?: string
}

interface Associations {
  file?: string
  folder?: string
  folderExpanded?: string
  rootFolder?: string
  rootFolderExpanded?: string
  fileExtensions?: Record<string, string>
  fileNames?: Record<string, string>
  folderNames?: Record<string, string>
  folderNamesExpanded?: Record<string, string>
  languageIds?: Record<string, string>
}

export interface IconFont {
  id: string
  src: { path: string; format?: string }[]
  weight?: string
  style?: string
  size?: string
}

/** An icon theme's file, as VS Code reads it. */
export interface IconThemeDocument extends Associations {
  iconDefinitions?: Record<string, IconDefinition>
  fonts?: IconFont[]
  light?: Associations
  highContrast?: Associations
  hidesExplorerArrows?: boolean
}

/** What an icon is asked for: a file or a folder, its name, its language (VS Code's id), whether it is open or the root. */
export interface IconFor {
  kind: 'file' | 'folder'
  name: string
  languageId?: string
  open?: boolean
  root?: boolean
  /** The colour theme is light: the theme's light choices first. */
  light?: boolean
}

const lower = (map: Record<string, string> | undefined) => (map ? Object.fromEntries(Object.entries(map).map(([k, v]) => [k.toLowerCase(), v])) : undefined)

/** An icon theme ready to be asked: its maps by lower-case keys (names and extensions are matched without case). */
export interface IconTheme {
  doc: IconThemeDocument
  maps: { dark: Required<Pick<Associations, 'fileExtensions' | 'fileNames' | 'folderNames' | 'folderNamesExpanded' | 'languageIds'>>; light: Associations }
}

export function readIconTheme(doc: IconThemeDocument): IconTheme {
  const of = (a: Associations | undefined) => ({
    fileExtensions: lower(a?.fileExtensions) ?? {},
    fileNames: lower(a?.fileNames) ?? {},
    folderNames: lower(a?.folderNames) ?? {},
    folderNamesExpanded: lower(a?.folderNamesExpanded) ?? {},
    languageIds: a?.languageIds ?? {},
  })
  return { doc, maps: { dark: of(doc), light: doc.light ? { ...doc.light, ...of(doc.light) } : {} } }
}

/** The icon definition's id for a file or a folder, in VS Code's order; undefined when the theme has none for it. */
export function iconIdFor(theme: IconTheme, q: IconFor): string | undefined {
  const { doc } = theme
  const sets = q.light ? [theme.maps.light, theme.maps.dark] : [theme.maps.dark]
  const first = (pick: (a: Associations) => string | undefined) => {
    for (const a of sets) {
      const v = pick(a)
      if (v) return v
    }
    return undefined
  }
  const name = q.name.toLowerCase()
  const defaults = q.light && doc.light ? [doc.light, doc] : [doc]
  const fallback = (pick: (a: Associations) => string | undefined) => defaults.map(pick).find(Boolean)
  if (q.kind === 'folder') {
    // As VS Code's CSS ranks them: an open folder's own expanded icon, the theme's expanded one, then its name's, then the default.
    if (q.open) {
      const open = first((a) => a.folderNamesExpanded?.[name]) ?? (q.root ? fallback((a) => a.rootFolderExpanded) : undefined) ?? fallback((a) => a.folderExpanded)
      if (open) return open
    }
    return first((a) => a.folderNames?.[name]) ?? (q.root ? fallback((a) => a.rootFolder) : undefined) ?? fallback((a) => a.folder)
  }
  const byName = first((a) => a.fileNames?.[name])
  if (byName) return byName
  // "a.test.ts": "test.ts", then "ts".
  const dots = name.split('.')
  for (let i = 1; i < dots.length; i++) {
    const ext = dots.slice(i).join('.')
    const byExt = first((a) => a.fileExtensions?.[ext])
    if (byExt) return byExt
  }
  if (q.languageId) {
    const byLanguage = first((a) => a.languageIds?.[q.languageId!])
    if (byLanguage) return byLanguage
  }
  return fallback((a) => a.file)
}

/** VS Code's language ids by extension, for the icons a theme gives by language (the editor's own may not be loaded). */
const languageByExtension: Record<string, string> = {
  bat: 'bat', cmd: 'bat', c: 'c', h: 'c', clj: 'clojure', cljs: 'clojure', coffee: 'coffeescript', cpp: 'cpp', cc: 'cpp', cxx: 'cpp', hpp: 'cpp', hh: 'cpp', hxx: 'cpp',
  cs: 'csharp', csx: 'csharp', css: 'css', dart: 'dart', fs: 'fsharp', fsi: 'fsharp', fsx: 'fsharp', go: 'go', groovy: 'groovy', gradle: 'groovy', hbs: 'handlebars', handlebars: 'handlebars',
  html: 'html', htm: 'html', xhtml: 'html', ini: 'ini', java: 'java', js: 'javascript', mjs: 'javascript', cjs: 'javascript', jsx: 'javascriptreact', json: 'json', jsonc: 'jsonc',
  jl: 'julia', less: 'less', lua: 'lua', md: 'markdown', markdown: 'markdown', m: 'objective-c', mm: 'objective-cpp', pl: 'perl', pm: 'perl', php: 'php', ps1: 'powershell', psm1: 'powershell',
  py: 'python', pyi: 'python', r: 'r', cshtml: 'razor', rb: 'ruby', rs: 'rust', scss: 'scss', sass: 'sass', sh: 'shellscript', bash: 'shellscript', zsh: 'shellscript', sql: 'sql', swift: 'swift',
  ts: 'typescript', mts: 'typescript', cts: 'typescript', tsx: 'typescriptreact', vb: 'vb', xml: 'xml', xaml: 'xml', csproj: 'xml', props: 'xml', targets: 'xml', svg: 'xml', yml: 'yaml', yaml: 'yaml',
  toml: 'toml', vue: 'vue', svelte: 'svelte', kt: 'kotlin', kts: 'kotlin', scala: 'scala', tex: 'latex', txt: 'plaintext', log: 'log', diff: 'diff', patch: 'diff', dockerfile: 'dockerfile', proto: 'proto3',
}
const languageByName: Record<string, string> = { dockerfile: 'dockerfile', makefile: 'makefile', gnumakefile: 'makefile', '.gitignore': 'ignore', '.dockerignore': 'ignore', 'cmakelists.txt': 'cmake' }

/** A file's VS Code language id by its name: the languages extensions add first, then VS Code's own. */
export function vscodeLanguageOf(name: string, added: { id: string; extensions?: string[]; filenames?: string[] }[] = []): string | undefined {
  const n = name.toLowerCase()
  for (const l of added) if (l.filenames?.some((f) => f.toLowerCase() === n)) return l.id
  let best: { id: string; length: number } | undefined
  for (const l of added) for (const e of l.extensions ?? []) if (n.endsWith(e.toLowerCase()) && e.length > (best?.length ?? 0)) best = { id: l.id, length: e.length }
  if (best) return best.id
  if (languageByName[n]) return languageByName[n]
  const dot = n.lastIndexOf('.')
  return dot >= 0 ? languageByExtension[n.slice(dot + 1)] : undefined
}

/** A font character as the theme writes it ("\\E001", "" or the character): the character. */
export function fontCharacterOf(text: string): string {
  const escaped = /^\\([0-9a-f]{1,6})$/i.exec(text)
  return escaped ? String.fromCodePoint(parseInt(escaped[1]!, 16)) : text
}
