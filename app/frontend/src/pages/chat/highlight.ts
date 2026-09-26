import hljs from 'highlight.js/lib/core'
import bash from 'highlight.js/lib/languages/bash'
import c from 'highlight.js/lib/languages/c'
import cpp from 'highlight.js/lib/languages/cpp'
import csharp from 'highlight.js/lib/languages/csharp'
import css from 'highlight.js/lib/languages/css'
import diff from 'highlight.js/lib/languages/diff'
import dockerfile from 'highlight.js/lib/languages/dockerfile'
import go from 'highlight.js/lib/languages/go'
import ini from 'highlight.js/lib/languages/ini'
import java from 'highlight.js/lib/languages/java'
import javascript from 'highlight.js/lib/languages/javascript'
import json from 'highlight.js/lib/languages/json'
import kotlin from 'highlight.js/lib/languages/kotlin'
import makefile from 'highlight.js/lib/languages/makefile'
import markdown from 'highlight.js/lib/languages/markdown'
import php from 'highlight.js/lib/languages/php'
import powershell from 'highlight.js/lib/languages/powershell'
import python from 'highlight.js/lib/languages/python'
import ruby from 'highlight.js/lib/languages/ruby'
import rust from 'highlight.js/lib/languages/rust'
import sql from 'highlight.js/lib/languages/sql'
import swift from 'highlight.js/lib/languages/swift'
import typescript from 'highlight.js/lib/languages/typescript'
import xml from 'highlight.js/lib/languages/xml'
import yaml from 'highlight.js/lib/languages/yaml'

const languages = { bash, c, cpp, csharp, css, diff, dockerfile, go, ini, java, javascript, json, kotlin, makefile, markdown, php, powershell, python, ruby, rust, sql, swift, typescript, xml, yaml }
for (const [name, lang] of Object.entries(languages)) hljs.registerLanguage(name, lang)

const aliases: Record<string, string> = {
  sh: 'bash', shell: 'bash', zsh: 'bash', console: 'bash', 'c++': 'cpp', h: 'c', hpp: 'cpp', cc: 'cpp', cs: 'csharp', js: 'javascript', jsx: 'javascript', mjs: 'javascript',
  ts: 'typescript', tsx: 'typescript', py: 'python', yml: 'yaml', html: 'xml', svg: 'xml', ps1: 'powershell', rs: 'rust', md: 'markdown', rb: 'ruby', kt: 'kotlin',
  toml: 'ini', make: 'makefile', patch: 'diff', docker: 'dockerfile',
}

/** The highlight.js name for a fence's language or a file's extension, if known. */
export function languageOf(lang: string | undefined | null): string | null {
  const l = (lang ?? '').trim().toLowerCase()
  const name = aliases[l] ?? l
  return name && hljs.getLanguage(name) ? name : null
}

const escape = (s: string) => s.replace(/[&<>"]/g, (ch) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' })[ch]!)

/** Highlighted HTML (escaped text and spans only), or the escaped text for an unknown language. */
export function highlight(code: string, lang: string | null): string {
  const name = languageOf(lang)
  return name ? hljs.highlight(code, { language: name, ignoreIllegals: true }).value : escape(code)
}

const extensions: Record<string, string> = {
  bash: 'sh', c: 'c', cpp: 'cpp', csharp: 'cs', css: 'css', diff: 'diff', dockerfile: 'Dockerfile', go: 'go', ini: 'ini', java: 'java', javascript: 'js',
  json: 'json', kotlin: 'kt', makefile: 'mk', markdown: 'md', php: 'php', powershell: 'ps1', python: 'py', ruby: 'rb', rust: 'rs', sql: 'sql', swift: 'swift',
  typescript: 'ts', xml: 'html', yaml: 'yaml',
}

export function extensionFor(lang: string | null): string {
  return extensions[languageOf(lang) ?? ''] ?? 'txt'
}
