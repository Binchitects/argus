import { describe, expect, it } from 'vitest'
import { isFromRunner, isToRunner, previewKindOf } from './kind'
import { compileComponent, componentOf, importsOf, PreviewError, reporter, rewriteHtml } from './page'

describe('what can be previewed', () => {
  it('goes by the fence, the file name, and for bare xml what it holds', () => {
    expect(previewKindOf('html', null, '<p>x</p>')).toBe('html')
    expect(previewKindOf('jsx', null, 'export default () => <p/>')).toBe('react')
    expect(previewKindOf('tsx', null, '')).toBe('react')
    expect(previewKindOf('mermaid', null, 'graph TD; A-->B')).toBe('mermaid')
    expect(previewKindOf(null, 'logo.svg', '<svg/>')).toBe('svg')
    expect(previewKindOf('xml', null, '<?xml version="1.0"?>\n<svg xmlns="http://www.w3.org/2000/svg"/>')).toBe('svg')
    expect(previewKindOf(null, null, '<!DOCTYPE html><html></html>')).toBe('html')
    // Plain JavaScript and Python are code, not previews.
    expect(previewKindOf('javascript', null, 'export default () => 1')).toBeNull()
    expect(previewKindOf('python', 'a.py', 'print(1)')).toBeNull()
  })

  it('accepts only a well-formed render message', () => {
    expect(isToRunner({ type: 'render', kind: 'html', code: '<p/>', theme: 'dark' })).toBe(true)
    expect(isToRunner({ type: 'render', kind: 'exe', code: '' })).toBe(false)
    expect(isToRunner({ type: 'render', kind: 'html' })).toBe(false)
  })

  it('takes a size from the runner only as a number', () => {
    expect(isFromRunner({ type: 'size', height: 320 })).toBe(true)
    expect(isFromRunner({ type: 'size', height: '320' })).toBe(false)
    expect(isFromRunner({ type: 'size', height: Infinity })).toBe(false)
  })
})

describe('a page', () => {
  const assets = { tailwind: '/preview/assets/tw.js', mermaid: '/preview/assets/mermaid.js' }

  it('reports first, and gets Tailwind and Mermaid from here instead of a CDN', () => {
    const page = rewriteHtml(
      '<!doctype html><html><head><script src="https://cdn.tailwindcss.com"></script><script src="https://cdn.jsdelivr.net/npm/mermaid@11/dist/mermaid.min.js"></script></head><body></body></html>',
      assets,
    )
    expect(page.indexOf(reporter)).toBe(page.indexOf('<head>') + '<head>'.length)
    expect(page).toContain('<script src="/preview/assets/tw.js">')
    expect(page).toContain('<script src="/preview/assets/mermaid.js">')
    expect(page).not.toContain('cdn.')
  })

  it('keeps the reporter first without a head, after a doctype', () => {
    expect(rewriteHtml('<p>hi</p>', assets).startsWith(reporter)).toBe(true)
    expect(rewriteHtml('<!DOCTYPE html><p>hi</p>', assets).startsWith('<!DOCTYPE html>' + reporter)).toBe(true)
  })
})

describe('a component', () => {
  it('compiles JSX and TypeScript to CommonJS for the runner', () => {
    const { js, imports } = compileComponent(
      "import { useState } from 'react'\nimport { Heart } from 'lucide-react'\ntype P = { n: number }\nexport default function App({ n }: P) { const [c] = useState(n); return <div className=\"p-4\"><Heart />{c}</div> }",
    )
    expect(imports).toEqual(['react', 'lucide-react'])
    expect(js).toContain('require(\'react\')')
    expect(js).toContain('React.createElement')
    expect(js).not.toContain('type P')
  })

  it('names a library the preview cannot load, and says what it has', () => {
    expect(() => compileComponent("import { LineChart } from 'recharts'\nexport default () => null")).toThrow(PreviewError)
    expect(() => compileComponent("import { LineChart } from 'recharts'\nexport default () => null")).toThrow(/"recharts" cannot be loaded here/)
  })

  it('says where a component does not compile', () => {
    expect(() => compileComponent('export default function App() { return <div> }')).toThrow(/does not compile/)
  })

  it('finds imports in every form', () => {
    expect(importsOf("import React from 'react'\nimport * as L from \"lucide-react\"\nimport 'x.css'\nconst d = require('react-dom')")).toEqual(['react', 'lucide-react', 'x.css', 'react-dom'])
  })

  it('shows the default export, else App, else the only function', () => {
    const f = () => null
    const g = () => null
    expect(componentOf({ default: f, App: g })).toBe(f)
    expect(componentOf({ App: g, helper: f })).toBe(g)
    expect(componentOf({ Only: f })).toBe(f)
    expect(componentOf({ a: f, b: g })).toBeNull()
  })
})
