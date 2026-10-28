import { readdirSync, readFileSync } from 'node:fs'
import { join } from 'node:path'
import { describe, expect, it } from 'vitest'

/** Every script and stylesheet under a folder. */
function files(dir: string): string[] {
  return readdirSync(dir, { withFileTypes: true }).flatMap((e) =>
    e.isDirectory() ? files(join(dir, e.name)) : /\.(js|css)$/.test(e.name) ? [join(dir, e.name)] : [],
  )
}

describe("Monaco's workbench tokens", () => {
  it('defines each one Monaco uses that only VS Code defines, and the menu border it falls back from wrongly', () => {
    const css = readFileSync(join(process.cwd(), 'src/styles/index.css'), 'utf8')
    const used = new Set<string>()
    for (const file of files(join(process.cwd(), 'node_modules/monaco-editor/esm/vs'))) {
      for (const m of readFileSync(file, 'utf8').matchAll(/var\((--vscode-(?:cornerRadius|strokeThickness|shadow|spacing|fontWeight|fontSize)[A-Za-z0-9-]*)/g)) used.add(m[1])
    }
    expect(used.size).toBeGreaterThan(10)
    const missing = [...used, '--vscode-menu-border'].filter((name) => !css.includes(`${name}:`))
    expect(missing).toEqual([])
  })
})
