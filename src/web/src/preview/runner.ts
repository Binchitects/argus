/**
 * The preview runner (/preview.html). nginx serves this page with a sandbox
 * policy: it runs in an origin of its own (no cookies, no storage of the app's,
 * no API) and may not reach the network at all. The chat sends it one piece of
 * code; it draws it and reports errors and blocked requests back.
 */
import tailwindUrl from '@tailwindcss/browser?url'
import mermaidUrl from 'mermaid/dist/mermaid.min.js?url'
import { isToRunner, type FromRunner, type PreviewKind } from './kind'
import { compileComponent, componentOf, PreviewError, repairMermaid, rewriteHtml, type ModuleName } from './page'

const parentWindow = window.parent
const send = (m: FromRunner) => parentWindow.postMessage(m, '*')

let done = false
window.addEventListener('message', (e) => {
  if (e.source !== parentWindow || done || !isToRunner(e.data)) return
  done = true // One render per load: the chat reloads the frame for a new one.
  void render(e.data.kind, e.data.code, e.data.theme, e.data.inline === true)
})
window.addEventListener('error', (e) => send({ type: 'error', message: String(e.message || e) }))
window.addEventListener('unhandledrejection', (e) => send({ type: 'error', message: String((e.reason as Error)?.message ?? e.reason) }))
const blocked = new Set<string>()
window.addEventListener('securitypolicyviolation', (e) => {
  const uri = e.blockedURI || e.violatedDirective
  if (!blocked.has(uri)) {
    blocked.add(uri)
    send({ type: 'blocked', uri })
  }
})
send({ type: 'ready' })

async function render(kind: PreviewKind, code: string, theme: 'light' | 'dark', inline: boolean) {
  try {
    if (kind === 'html') {
      // The page replaces this one; its own reporter (injected) keeps talking to the chat.
      document.open()
      document.write(rewriteHtml(code, { tailwind: tailwindUrl, mermaid: mermaidUrl }))
      document.close()
      send({ type: 'rendered' })
      return
    }
    document.documentElement.dataset.theme = theme
    const root = document.getElementById('root')!
    if (inline) {
      // The frame takes the content's height: the content must not take the frame's.
      document.documentElement.dataset.inline = ''
      new ResizeObserver(() => send({ type: 'size', height: Math.ceil(document.body.getBoundingClientRect().height) })).observe(document.body)
    }
    if (kind === 'svg') {
      root.className = 'center'
      // Markup, not script: an <svg>'s <script> does not run when set this way.
      root.innerHTML = code
    } else if (kind === 'mermaid') {
      await script(mermaidUrl)
      const mermaid = (window as unknown as { mermaid: MermaidApi }).mermaid
      // Dark edge labels sit on grey by default, too faint to read (WCAG AA): a darker ground.
      const themeVariables = theme === 'dark' ? { edgeLabelBackground: '#26262b' } : {}
      mermaid.initialize({ startOnLoad: false, securityLevel: 'strict', theme: theme === 'dark' ? 'dark' : 'default', themeVariables })
      // Slips models make often (a pie's "donut", a choice without "state") are mended first:
      // one is refused outright, the other drawn as a stray box.
      const { svg } = await mermaid.render('diagram', repairMermaid(code))
      root.className = 'center'
      root.innerHTML = svg
      readable(root.querySelector('svg'))
    } else {
      await component(code, root)
    }
    send({ type: 'rendered' })
  } catch (e) {
    const message = e instanceof PreviewError ? e.message : `${(e as Error)?.message ?? e}`
    showError(message)
    send({ type: 'error', message })
  }
}

type MermaidApi = { initialize(o: object): void; render(id: string, code: string): Promise<{ svg: string }> }

/** The smallest a drawing is shrunk to fit: below it, text is too small to read, and the drawing scrolls sideways instead. */
const MIN_SCALE = 0.6

/** A wide drawing keeps a readable size: shrunk to fit down to MIN_SCALE, wider than that it scrolls. */
function readable(svg: SVGSVGElement | null) {
  if (!svg) return
  const natural = svg.viewBox.baseVal?.width || svg.getBoundingClientRect().width
  const room = (svg.parentElement?.clientWidth ?? window.innerWidth) - 24
  if (natural * MIN_SCALE <= room) return
  svg.style.maxWidth = 'none'
  svg.style.width = `${Math.round(natural * MIN_SCALE)}px`
  document.documentElement.dataset.wide = ''
  // It scrolls sideways: keyboard users scroll it too.
  document.body.tabIndex = 0
}

function script(src: string) {
  return new Promise<void>((resolve, reject) => {
    const s = document.createElement('script')
    s.src = src
    s.onload = () => resolve()
    s.onerror = () => reject(new Error(`Could not load ${src}`))
    document.head.append(s)
  })
}

async function component(code: string, root: HTMLElement) {
  const { js, imports } = compileComponent(code)
  const loaders: Record<ModuleName, () => Promise<unknown>> = {
    react: () => import('react'),
    'react-dom': () => import('react-dom'),
    'react-dom/client': () => import('react-dom/client'),
    'react/jsx-runtime': () => import('react/jsx-runtime'),
    'lucide-react': () => import('lucide-react'),
  }
  const needed = new Set<ModuleName>(['react', 'react-dom/client', ...imports])
  const loaded = new Map<string, unknown>()
  await Promise.all([...needed].map(async (m) => loaded.set(m, await loaders[m]())))
  // Tailwind's browser build styles whatever classes the component uses.
  await script(tailwindUrl)
  const React = loaded.get('react') as typeof import('react')
  const module = { exports: {} as Record<string, unknown> }
  const require = (m: string) => {
    if (!loaded.has(m)) throw new PreviewError(`"${m}" cannot be loaded in the preview.`)
    return loaded.get(m)
  }
  new Function('require', 'module', 'exports', 'React', js)(require, module, module.exports, React)
  const Component = componentOf(module.exports)
  if (!Component) throw new PreviewError('Nothing to show: export the component as the default export (export default function App() { ... }).')
  const { createRoot } = loaded.get('react-dom/client') as typeof import('react-dom/client')
  createRoot(root, {
    onUncaughtError: (error) => {
      const message = (error as Error)?.message ?? String(error)
      showError(message)
      send({ type: 'error', message })
    },
  }).render(React.createElement(Component as React.ElementType))
}

function showError(message: string) {
  const box = document.createElement('pre')
  box.className = 'preview-error'
  box.textContent = message
  document.body.replaceChildren(box)
}
