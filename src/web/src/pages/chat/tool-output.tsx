import { useQuery } from '@tanstack/react-query'
import { Braces, ExternalLink } from 'lucide-react'
import { useMemo, useState } from 'react'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { DataTable, type ColumnDef } from '@/components/ui/data-table'
import { cn } from '@/lib/utils'
import { configQuery } from './api'
import { gitlabLink, isRow, snippetParts, toHits, type CodeHit, type Row } from './argus'
import { CodeBlock } from './code-block'
import { seconds } from './format'
import { highlight } from './highlight'
import { Markdown } from './markdown'
import { agentResults, runOutput, type RunOutput } from './tool-args'

const extension = (path: string) => (path.includes('.') ? path.split('.').pop()! : path.split('/').pop()!)

function lines(hit: CodeHit) {
  if (!hit.line) return ''
  return hit.endLine && hit.endLine > hit.line ? `:${hit.line}–${hit.endLine}` : `:${hit.line}`
}

/** "group/app › src/parse.c:10–24", linking to GitLab when it is known. */
function Location({ hit, href }: { hit: CodeHit; href: string | null }) {
  const label = (
    <>
      {hit.repo && <span className="text-muted-foreground">{hit.repo} › </span>}
      <span className="break-all">{hit.path}</span>
      <span className="text-muted-foreground">{lines(hit)}</span>
    </>
  )
  return href ? (
    <a href={href} target="_blank" rel="noopener noreferrer" className="group/link inline-flex min-w-0 items-baseline gap-1 font-mono text-xs underline-offset-2 outline-none hover:underline focus-visible:ring-[3px] focus-visible:ring-ring">
      <span className="min-w-0">{label}</span>
      <ExternalLink className="size-3 shrink-0 self-center text-muted-foreground group-hover/link:text-foreground" aria-hidden="true" />
      <span className="sr-only">(opens GitLab)</span>
    </a>
  ) : (
    <span className="min-w-0 font-mono text-xs">{label}</span>
  )
}

/** One highlighted line with its number, as GitLab would show it; long ones wrap. */
function CodeLine({ code, path, line }: { code: string; path: string; line: number | null }) {
  const html = useMemo(() => highlight(code, extension(path)), [code, path])
  return (
    <pre className="hljs flex rounded-md border bg-muted/40 py-1.5 font-mono text-xs leading-relaxed">
      {line && (
        <span className="shrink-0 border-r px-2 text-right text-muted-foreground select-none" aria-hidden="true">
          {line}
        </span>
      )}
      {/* Escaped text and highlight.js spans only (see highlight.ts). */}
      <code className="min-w-0 px-3 break-words whitespace-pre-wrap" dangerouslySetInnerHTML={{ __html: html }} />
    </pre>
  )
}

function Hit({ hit, href, query }: { hit: CodeHit; href: string | null; query: string | null }) {
  return (
    <li className="grid gap-1.5 py-2.5 first:pt-0 last:pb-0">
      <div className="flex min-w-0 flex-wrap items-center gap-x-2 gap-y-1">
        {hit.name && <span className="font-mono text-sm font-medium">{hit.name}</span>}
        {hit.kind && <Badge variant="secondary">{hit.kind}</Badge>}
        {hit.score !== null && <span className="text-xs text-muted-foreground tabular-nums">{Math.round(hit.score * 100)}% match</span>}
      </div>
      <Location hit={hit} href={href} />
      {hit.code && <CodeLine code={hit.code} path={hit.path} line={hit.line} />}
      {hit.snippet && (
        <p className="rounded-md border bg-muted/40 px-3 py-1.5 font-mono text-xs leading-relaxed break-all whitespace-pre-wrap">
          {snippetParts(hit.snippet, query).map((p, i) =>
            p.match ? (
              <mark key={i} className="rounded-sm bg-warning/25 px-0.5 text-foreground">
                {p.text}
              </mark>
            ) : (
              <span key={i}>{p.text}</span>
            ),
          )}
        </p>
      )}
      {hit.doc && <p className="text-xs text-muted-foreground">{hit.doc}</p>}
    </li>
  )
}

function cell(v: unknown): string {
  if (v === null || v === undefined) return ''
  if (typeof v === 'string') return v
  if (typeof v === 'number' || typeof v === 'boolean') return String(v)
  return JSON.stringify(v)
}

/** Rows that are not code locations (which_repo, index_status, a pack's search): a table that sorts and filters. */
function RowTable({ rows }: { rows: Row[] }) {
  const columns: ColumnDef<Row>[] = [...new Set(rows.slice(0, 50).flatMap(Object.keys))].slice(0, 8).map((c) => ({
    id: c,
    header: c,
    accessorFn: (r) => (typeof r[c] === 'number' ? r[c] : cell(r[c])),
    meta: { className: 'max-w-72 font-mono text-xs break-words align-top' },
  }))
  return <DataTable columns={columns} data={rows} noun="rows" label="Rows" compact pageSize={50} />
}

/** An object that is not a file (overview, impact_of): short fields as a list; nested ones and text over a line as code blocks. */
function Fields({ value }: { value: Row }) {
  const entries = Object.entries(value).filter(([, v]) => v !== null && v !== undefined)
  const block = ([, v]: [string, unknown]) => isRow(v) || Array.isArray(v) || (typeof v === 'string' && v.includes('\n'))
  const short = entries.filter((e) => !block(e))
  return (
    <div className="grid gap-2">
      {short.length > 0 && (
        <dl className="grid grid-cols-[auto_minmax(0,1fr)] gap-x-3 gap-y-1.5 text-xs">
          {short.map(([k, v]) => (
            <div key={k} className="contents">
              <dt className="text-muted-foreground">{k}</dt>
              <dd className="font-mono break-words whitespace-pre-wrap">{cell(v)}</dd>
            </div>
          ))}
        </dl>
      )}
      {entries.filter(block).map(([k, v]) =>
        typeof v === 'string' ? <CodeBlock key={k} code={v} lang={null} label={k} /> : <CodeBlock key={k} code={JSON.stringify(v, null, 2)} lang="json" label={k} />,
      )}
    </div>
  )
}

/** A program's run: what it printed and its errors as a console would show them, then how it ended. */
function Run({ run }: { run: RunOutput }) {
  const failed = (run.exitCode ?? 0) !== 0 || run.problem !== null
  return (
    <div className="grid gap-2">
      {run.stdout ? <CodeBlock code={run.stdout} lang={null} label="Output" /> : !run.stderr && <p className="text-xs text-muted-foreground">It printed nothing.</p>}
      {run.stderr && <CodeBlock code={run.stderr} lang={null} label={failed ? 'Error' : 'Messages (stderr)'} />}
      {run.problem && <p className="text-xs text-destructive-ink">{run.problem}</p>}
      <p className="flex flex-wrap gap-x-2 text-xs text-muted-foreground tabular-nums">
        {run.exitCode !== null && <span className={cn(failed && 'text-destructive-ink')}>Exit code {run.exitCode}</span>}
        {run.seconds !== null && <span>· {seconds(run.seconds * 1000)}</span>}
      </p>
      {Object.keys(run.rest).length > 0 && <Fields value={run.rest} />}
    </div>
  )
}

/**
 * A tool's answer, shown for what it is: code locations link to GitLab with the
 * code highlighted, a file opens as code, other rows are a table. Anything
 * else, and every error, is the tool's own text. The raw answer is one click away.
 */
export function ToolOutput({ text, value, args, failed }: { text: string; value: unknown; args: Row; failed: boolean }) {
  const [raw, setRaw] = useState(false)
  const gitlab = useQuery(configQuery).data?.gitlabUrl ?? null
  const branch = typeof args.branch === 'string' ? args.branch : null
  const query = typeof args.query === 'string' ? args.query : typeof args.name === 'string' ? args.name : null
  const run = runOutput(value)
  const agents = agentResults(value)
  if (value === undefined || (failed && !run)) {
    return <Markdown text={text || '(nothing)'} />
  }
  const hits = toHits(value)
  const href = (h: CodeHit) => (gitlab && h.repo ? gitlabLink(gitlab, h.repo, h.path, branch, h.line, h.endLine) : null)
  const file = hits?.length === 1 && hits[0]!.content !== null ? hits[0]! : null
  let body
  if (raw) {
    body = <CodeBlock code={JSON.stringify(value, null, 2)} lang="json" />
  } else if (run) {
    body = <Run run={run} />
  } else if (agents) {
    body = (
      <ol className="grid gap-3" aria-label="What each sub-agent found">
        {agents.map((a, i) => (
          <li key={i} className="grid gap-1.5 rounded-lg border p-3">
            <p className="flex flex-wrap items-center gap-2 text-sm font-medium">
              {a.title}
              <span className="text-xs font-normal text-muted-foreground">
                {a.toolCalls === 0 ? 'no tool calls' : `${a.toolCalls} tool call${a.toolCalls === 1 ? '' : 's'}`}
              </span>
            </p>
            {a.error && <p className="text-xs text-destructive-ink">{a.error}</p>}
            {a.result ? <Markdown text={a.result} /> : <p className="text-xs text-muted-foreground">It brought nothing back.</p>}
          </li>
        ))}
      </ol>
    )
  } else if (file) {
    body = (
      <div className="grid gap-2">
        <Location hit={file} href={href(file)} />
        <CodeBlock code={file.content!} lang={extension(file.path)} name={file.path} />
        {file.truncated && <p className="text-xs text-muted-foreground">Argus sent the start of this file only.</p>}
      </div>
    )
  } else if (hits) {
    body = (
      <ul className="grid divide-y" aria-label="Places in the code">
        {hits.map((h, i) => (
          <Hit key={i} hit={h} href={href(h)} query={query} />
        ))}
      </ul>
    )
  } else if (Array.isArray(value) && value.length > 0 && value.every(isRow)) {
    body = <RowTable rows={value} />
  } else if (Array.isArray(value) && value.length === 0) {
    body = <p className="text-sm text-muted-foreground">Nothing found.</p>
  } else if (isRow(value)) {
    body = <Fields value={value} />
  } else {
    body = <Markdown text={text} />
  }
  return (
    <div className="grid gap-2">
      {body}
      <Button variant="ghost" size="sm" className="ml-auto h-7 gap-1 px-2 text-xs" onClick={() => setRaw(!raw)} aria-pressed={raw}>
        <Braces /> Raw answer
      </Button>
    </div>
  )
}
