import { describe, expect, it } from 'vitest'
import { fontCharacterOf, iconIdFor, readIconTheme, vscodeLanguageOf } from './icon-theme'

// An icon theme as Seti and Material Icon Theme write theirs.
const theme = readIconTheme({
  iconDefinitions: {},
  file: '_file',
  folder: '_folder',
  folderExpanded: '_folder_open',
  rootFolder: '_root',
  fileNames: { 'package.json': '_npm', 'Dockerfile': '_docker' },
  fileExtensions: { ts: '_ts', 'test.ts': '_test', 'd.ts': '_dts', md: '_md' },
  languageIds: { typescript: '_ts_lang', python: '_py', javascriptreact: '_react' },
  folderNames: { src: '_src', node_modules: '_nm' },
  folderNamesExpanded: { src: '_src_open' },
  light: { file: '_file_light', fileExtensions: { ts: '_ts_light' }, folderNames: { src: '_src_light' } },
})

const file = (name: string, extra: { languageId?: string; light?: boolean } = {}) => iconIdFor(theme, { kind: 'file', name, ...extra })
const folder = (name: string, extra: { open?: boolean; root?: boolean; light?: boolean } = {}) => iconIdFor(theme, { kind: 'folder', name, ...extra })

describe("VS Code's file icon themes", () => {
  it("finds a file's icon by its name, then its extension (the longest first), then its language, else the default", () => {
    expect(file('package.json', { languageId: 'json' })).toBe('_npm')
    // Names and extensions are matched without case.
    expect(file('DOCKERFILE')).toBe('_docker')
    expect(file('cart.test.ts', { languageId: 'typescript' })).toBe('_test')
    expect(file('types.d.ts')).toBe('_dts')
    expect(file('app.TS', { languageId: 'typescript' })).toBe('_ts')
    expect(file('app.tsx', { languageId: 'typescriptreact' })).toBe('_file')
    expect(file('main.py', { languageId: 'python' })).toBe('_py')
    expect(file('notes.txt')).toBe('_file')
  })

  it("takes a light theme's own choices first, the dark ones after", () => {
    expect(file('app.ts', { light: true })).toBe('_ts_light')
    expect(file('README.md', { light: true })).toBe('_md')
    expect(file('notes.txt', { light: true })).toBe('_file_light')
    expect(folder('src', { light: true })).toBe('_src_light')
    expect(folder('lib', { light: true })).toBe('_folder')
  })

  it("finds a folder's icon by its name, open or closed, as VS Code ranks them", () => {
    expect(folder('src')).toBe('_src')
    expect(folder('SRC', { open: true })).toBe('_src_open')
    // An open folder without an expanded icon of its own: the theme's expanded folder.
    expect(folder('node_modules', { open: true })).toBe('_folder_open')
    expect(folder('node_modules')).toBe('_nm')
    expect(folder('lib')).toBe('_folder')
    expect(folder('lib', { root: true })).toBe('_root')
    // A theme without expanded icons keeps its closed ones open.
    const plain = readIconTheme({ folder: '_f', folderNames: { src: '_s' } })
    expect(iconIdFor(plain, { kind: 'folder', name: 'src', open: true })).toBe('_s')
    expect(iconIdFor(plain, { kind: 'folder', name: 'x', open: true })).toBe('_f')
    expect(iconIdFor(readIconTheme({}), { kind: 'file', name: 'a.ts' })).toBeUndefined()
  })

  it("knows VS Code's language ids by a file's name, the languages extensions add first", () => {
    expect(vscodeLanguageOf('app.tsx')).toBe('typescriptreact')
    expect(vscodeLanguageOf('build.sh')).toBe('shellscript')
    expect(vscodeLanguageOf('Makefile')).toBe('makefile')
    expect(vscodeLanguageOf('Cargo.toml')).toBe('toml')
    expect(vscodeLanguageOf('x.unknown')).toBeUndefined()
    const added = [{ id: 'just', filenames: ['justfile'] }, { id: 'jinja-html', extensions: ['.jinja.html', '.j2'] }]
    expect(vscodeLanguageOf('Justfile', added)).toBe('just')
    expect(vscodeLanguageOf('page.jinja.html', added)).toBe('jinja-html')
    expect(vscodeLanguageOf('page.html', added)).toBe('html')
  })

  it("reads a font character as the theme writes it", () => {
    expect(fontCharacterOf('\\E001')).toBe('\uE001')
    expect(fontCharacterOf('\\f101')).toBe('\uf101')
    expect(fontCharacterOf('A')).toBe('A')
  })
})
