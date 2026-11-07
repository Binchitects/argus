import { describe, expect, it } from 'vitest'
import { isWorkspaceFile, normalize, relativeTo } from './code-workspace'
import { effective, layerOf, readLayer } from './settings'

describe('multi-root workspaces', () => {
  it('names a folder from the workspace file as VS Code writes it', () => {
    expect(relativeTo('/home/ada/shop', '/home/ada/shop')).toBe('.')
    expect(relativeTo('/home/ada/shop', '/home/ada/shop/api')).toBe('api')
    expect(relativeTo('/home/ada/shop', '/home/ada/lib')).toBe('../lib')
    expect(relativeTo('C:\\work\\shop', 'C:\\work\\lib')).toBe('../lib')
    expect(relativeTo('C:/work', 'D:/data')).toBe('D:/data')
  })

  it('works out . and .. in a folder named from the file', () => {
    expect(normalize('/home/ada/shop/../lib')).toBe('/home/ada/lib')
    expect(normalize('/home/ada/shop/./api/')).toBe('/home/ada/shop/api')
    expect(normalize('C:\\work\\shop\\..\\lib')).toBe('C:/work/lib')
  })

  it('knows a workspace file by its name', () => {
    expect(isWorkspaceFile('shop.code-workspace')).toBe(true)
    expect(isWorkspaceFile('/srv/Shop.CODE-WORKSPACE')).toBe(true)
    expect(isWorkspaceFile('code-workspace.json')).toBe(false)
  })

  it("puts the workspace file's settings between the person's and the folder's, a language's own over them all", () => {
    const user = readLayer('{ "editor.tabSize": 8, "editor.fontSize": 15, "[python]": { "editor.tabSize": 3 } }')
    const file = layerOf({ 'editor.tabSize': 2, 'editor.wordWrap': 'on', 'codeArena.inlineCompletion.enabled': true, '[python]': { 'editor.insertSpaces': false } }, true)
    const folder = readLayer('{ "editor.wordWrap": "off" }', true)
    const all = effective(user, folder, undefined, undefined, file)
    expect(all['editor.tabSize']).toBe(2)
    expect(all['editor.fontSize']).toBe(15)
    // The folder's own wins over the workspace file's.
    expect(all['editor.wordWrap']).toBe('off')
    const python = effective(user, folder, 'python', undefined, file)
    expect(python['editor.tabSize']).toBe(3)
    expect(python['editor.insertSpaces']).toBe(false)
    // A setting that is the person's alone is not the workspace's to set.
    expect(file.problems.some((p) => p.includes('codeArena.inlineCompletion.enabled'))).toBe(true)
  })
})
