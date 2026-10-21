import { transform } from 'sucrase'

/** Libraries a previewed component may import, and nothing else: the preview has no network. */
export const modules = ['react', 'react-dom', 'react-dom/client', 'react/jsx-runtime', 'lucide-react'] as const
export type ModuleName = (typeof modules)[number]

/**
 * Runs first in a previewed page: reports its errors and anything it tried to
 * fetch (which the preview's policy refuses) to the chat around it.
 */
export const reporter = `<script>(function(){var p=window.parent,s=new Set();
addEventListener('error',function(e){p.postMessage({type:'error',message:String(e.message||e)},'*')});
addEventListener('unhandledrejection',function(e){p.postMessage({type:'error',message:String(e.reason&&e.reason.message||e.reason)},'*')});
addEventListener('securitypolicyviolation',function(e){var u=e.blockedURI||e.violatedDirective;if(!s.has(u)){s.add(u);p.postMessage({type:'blocked',uri:u},'*')}});
})();</script>`

/**
 * window.arena, for the code: its calls go to the page around it (postMessage) and come back answered (ArenaReply).
 * ask(prompt, { system, model }) is the model's answer as text; store.get/set/remove/keys keep JSON for the app,
 * everyone's, or the person's own with { mine: true } last; user.name is who uses it.
 */
export function arenaBridge(user: string): string {
  return `(function(){var p=window.parent,n=0,w={};
addEventListener('message',function(e){var d=e.data;if(e.source!==p||!d||d.type!=='arena-reply'||!w[d.id])return;var c=w[d.id];delete w[d.id];if(d.ok)c[0](d.value);else c[1](new Error(d.error||'arena failed'))});
function call(m,a){return new Promise(function(ok,no){var id=++n;w[id]=[ok,no];p.postMessage({type:'arena',id:id,method:m,args:a},'*')})}
function mine(o){return !!(o&&typeof o==='object'&&o.mine===true)}
window.arena={ask:function(t,o){o=o||{};return call('ask',{prompt:String(t),system:o.system?String(o.system):undefined,model:o.model?String(o.model):undefined})},
store:{get:function(k,o){return call('store.get',{key:String(k),mine:mine(o)})},set:function(k,v,o){return call('store.set',{key:String(k),value:v,mine:mine(o)})},remove:function(k,o){return call('store.remove',{key:String(k),mine:mine(o)})},keys:function(o){return call('store.keys',{mine:mine(o)})}},
user:{name:${JSON.stringify(user).replace(/</g, '\\u003c')}}};})();`
}

/**
 * A page as the preview runs it: the reporter and window.arena first, and the libraries it would
 * fetch from a CDN (Tailwind, Mermaid) served from here instead.
 */
export function rewriteHtml(html: string, assets: { tailwind: string; mermaid: string }, user = ''): string {
  const local = html
    .replace(/<script\b([^>]*?)\bsrc=["']https?:\/\/cdn\.tailwindcss\.com[^"']*["']([^>]*)>/gi, `<script$1src="${assets.tailwind}"$2>`)
    .replace(/<script\b([^>]*?)\bsrc=["']https?:\/\/[^"']*@tailwindcss\/browser[^"']*["']([^>]*)>/gi, `<script$1src="${assets.tailwind}"$2>`)
    .replace(/<script\b([^>]*?)\bsrc=["']https?:\/\/[^"']*\/mermaid(?:@[^/"']*)?\/dist\/mermaid(?:\.min)?\.js["']([^>]*)>/gi, `<script$1src="${assets.mermaid}"$2>`)
  const first = reporter + `<script>${arenaBridge(user)}</script>`
  const head = /<head\b[^>]*>/i.exec(local)
  if (head) return local.slice(0, head.index + head[0].length) + first + local.slice(head.index + head[0].length)
  const doctype = /^\s*<!doctype[^>]*>/i.exec(local)
  if (doctype) return doctype[0] + first + local.slice(doctype[0].length)
  return first + local
}

/** The modules a component imports. */
export function importsOf(code: string): string[] {
  const found = new Set<string>()
  for (const m of code.matchAll(/(?:^|[\s;])import\s+(?:[\w*{}\s,$]+\s+from\s+)?["']([^"']+)["']/g)) found.add(m[1]!)
  for (const m of code.matchAll(/\brequire\(\s*["']([^"']+)["']\s*\)/g)) found.add(m[1]!)
  return [...found]
}

export class PreviewError extends Error {}

/**
 * A React component (JSX, TypeScript) as CommonJS the runner can evaluate, or
 * a PreviewError that says what to change.
 */
export function compileComponent(code: string): { js: string; imports: ModuleName[] } {
  const imports = importsOf(code)
  const missing = imports.filter((m) => !(modules as readonly string[]).includes(m))
  if (missing.length) {
    throw new PreviewError(
      `The preview has ${modules.filter((m) => !m.includes('/')).join(', ')} and Tailwind classes, and no network: ${missing.map((m) => `"${m}"`).join(', ')} cannot be loaded here.`,
    )
  }
  try {
    const { code: js } = transform(code, { transforms: ['jsx', 'typescript', 'imports'], production: true, jsxRuntime: 'classic' })
    return { js, imports: imports as ModuleName[] }
  } catch (e) {
    throw new PreviewError(`The component does not compile: ${(e as Error).message}`)
  }
}

/** The component a module offers: its default export, else App, else its only exported function. */
export function componentOf(exports: Record<string, unknown>): unknown {
  if (typeof exports.default === 'function' || (exports.default && typeof exports.default === 'object')) return exports.default
  if (typeof exports.App === 'function') return exports.App
  const fns = Object.values(exports).filter((v) => typeof v === 'function')
  return fns.length === 1 ? fns[0] : null
}

/**
 * Mends slips models make in Mermaid that its parser refuses, so the diagram is drawn
 * anyway: a pie's "donut" line (no such thing; "showData" belongs on the pie line), and a
 * state diagram's choice or fork written without "state" in front. Anything else is left
 * as it is (the error is shown then, with the code).
 */
export function repairMermaid(code: string): string {
  const lines = code.split('\n')
  const kind = lines.find((l) => l.trim() && !l.trim().startsWith('%%'))?.trim().split(/\s+/)[0] ?? ''
  if (kind === 'pie') {
    const show = lines.some((l) => /^\s*donut\b.*\bshowData\b/.test(l) || /^\s*showData\s*$/.test(l))
    const kept = lines.filter((l) => !/^\s*(donut\b.*|showData\s*)$/.test(l))
    const head = kept.findIndex((l) => /^\s*pie\b/.test(l))
    if (show && head >= 0 && !/\bshowData\b/.test(kept[head]!)) kept[head] = kept[head]!.replace(/\bpie\b/, 'pie showData')
    return kept.join('\n')
  }
  if (kind.startsWith('stateDiagram')) {
    return lines.map((l) => l.replace(/^(\s*)(?!state\b)([\w-]+)\s+(<<(?:choice|fork|join)>>)\s*$/, '$1state $2 $3')).join('\n')
  }
  return code
}
