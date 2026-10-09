import { BookOpen, Search } from 'lucide-react'
import { useEffect, useLayoutEffect, useMemo, useRef, type ReactNode } from 'react'
import { Link, useLocation, useNavigate, useOutletContext, useParams, useSearchParams } from 'react-router'
import { PageHeader } from '@/components/app/page-header'
import { EmptyState } from '@/components/ui/empty-state'
import { Input } from '@/components/ui/input'
import { Select, SelectContent, SelectGroup, SelectItem, SelectLabel, SelectTrigger, SelectValue } from '@/components/ui/select'
import { canRead, manualGroups, PAGES_ID, parseDoc, resolveLink, searchManual, type ManualDoc, type SearchHit } from '@/help/manual'
import { pagesMarkdown, pagesTitle } from '@/help/pages-doc'
import { Slugger } from '@/help/slug'
import type { Me } from '@/lib/api'
import { cn } from '@/lib/utils'
import { Markdown } from './chat/markdown'

type Page = Omit<ManualDoc, 'audience'>
type Group = { title: string; pages: Page[] }

/**
 * The manual (/help): the docs people and admins need, built into the web, with contents,
 * search across every page, and anchors; links between pages stay in the app. An admin's
 * pages are listed to admins only (a tidier manual, not access control: see canRead).
 */
export function ManualPage() {
  const me = useOutletContext<Me>()
  const { '*': splat = '' } = useParams()
  // As the address names it, or as the file does (argus/README.md is argus).
  const id = splat.replace(/\/+$/, '').replace(/\.md$/, '').replace(/(^|\/)README$/, '')
  const [params, setParams] = useSearchParams()
  const q = params.get('q') ?? ''

  const groups = useMemo<Group[]>(
    () => [
      {
        title: 'This app',
        pages: [{ id: PAGES_ID, file: 'pages.md', title: pagesTitle, about: 'What each page is for, what its parts do, and the common tasks, step by step.', text: pagesMarkdown(me.isAdmin) }],
      },
      ...manualGroups.map((g) => ({ title: g.title, pages: g.docs.filter((d) => canRead(d, me.isAdmin)) })).filter((g) => g.pages.length),
    ],
    [me.isAdmin],
  )
  const pages = useMemo(() => groups.flatMap((g) => g.pages), [groups])
  const page = pages.find((p) => p.id === id) ?? null
  const navigate = useNavigate()

  return (
    <>
      <PageHeader title="Manual" description="How to use the app, and how to run it. The ? at the top of every page opens the help for that page." />
      <div className="relative mb-6 max-w-xl">
        <Search className="pointer-events-none absolute top-1/2 left-2.5 size-4 -translate-y-1/2 text-muted-foreground" aria-hidden="true" />
        <Input
          type="search"
          value={q}
          onChange={(e) => setParams(e.target.value ? { q: e.target.value } : {}, { replace: true })}
          placeholder="Search the manual"
          aria-label="Search the manual"
          className="pl-8"
        />
      </div>
      <div className="grid gap-6 lg:grid-cols-[15rem_minmax(0,1fr)] lg:gap-10">
        <nav aria-label="Contents of the manual" className="hidden lg:block">
          <div className="sticky top-20 grid max-h-[calc(100dvh-6rem)] content-start gap-5 overflow-y-auto pb-4">
            {groups.map((g) => (
              <div key={g.title}>
                <p className="px-2 pb-1.5 text-[0.6875rem] font-semibold tracking-wider text-muted-foreground uppercase">{g.title}</p>
                <ul className="grid gap-0.5">
                  {g.pages.map((p) => (
                    <li key={p.id}>
                      <Link
                        to={`/help/${p.id}`}
                        aria-current={p === page ? 'page' : undefined}
                        className={cn('block rounded-md px-2 py-1 text-sm hover:bg-accent hover:text-foreground', p === page ? 'bg-accent font-medium text-foreground' : 'text-muted-foreground')}
                      >
                        {p.title}
                      </Link>
                      {p === page && !q && <Sections page={p} />}
                    </li>
                  ))}
                </ul>
              </div>
            ))}
          </div>
        </nav>
        <div className="min-w-0">
          <div className="mb-4 lg:hidden">
            <Select value={page?.id ?? ''} onValueChange={(v) => navigate(`/help/${v}`)}>
              <SelectTrigger aria-label="Page of the manual" className="w-full">
                <SelectValue placeholder="Contents" />
              </SelectTrigger>
              <SelectContent>
                {groups.map((g) => (
                  <SelectGroup key={g.title}>
                    <SelectLabel>{g.title}</SelectLabel>
                    {g.pages.map((p) => (
                      <SelectItem key={p.id} value={p.id}>
                        {p.title}
                      </SelectItem>
                    ))}
                  </SelectGroup>
                ))}
              </SelectContent>
            </Select>
            {page && !q && <SectionPicker page={page} />}
          </div>
          {q ? (
            <Results q={q} pages={pages} />
          ) : page ? (
            <PageView page={page} isAdmin={me.isAdmin} />
          ) : id ? (
            <EmptyState icon={BookOpen} title="This page is not in the manual" action={<Link to="/help" className="text-sm font-medium text-primary-ink underline-offset-2 hover:underline">Open the manual</Link>}>
              It may be a page for admins, or one for the people who build the app.
            </EmptyState>
          ) : (
            <Start groups={groups} />
          )}
        </div>
      </div>
    </>
  )
}

/** A page's sections: its level-2 headings; the page of page help lists its pages. */
function sectionsOf(page: Page) {
  const level = page.id === PAGES_ID ? 3 : 2
  return parseDoc(page.text).headings.filter((h) => h.level === level)
}

/** A page's sections, under its name in the contents. */
function Sections({ page }: { page: Page }) {
  const headings = sectionsOf(page)
  if (!headings.length) return null
  return (
    <ul className="my-1 ml-3 grid gap-0.5 border-l pl-2" aria-label={`Sections of ${page.title}`}>
      {headings.map((h) => (
        <li key={h.id}>
          <Link to={`/help/${page.id}#${h.id}`} className="block truncate rounded px-1.5 py-0.5 text-xs text-muted-foreground hover:bg-accent hover:text-foreground" title={h.text}>
            {h.text}
          </Link>
        </li>
      ))}
    </ul>
  )
}

/** On a narrower screen, where the contents is a list of pages: the open page's sections. */
function SectionPicker({ page }: { page: Page }) {
  const navigate = useNavigate()
  const { hash } = useLocation()
  const headings = useMemo(() => sectionsOf(page), [page])
  if (!headings.length) return null
  let anchor = hash.slice(1)
  try {
    anchor = decodeURIComponent(anchor)
  } catch {
    // a malformed address: its anchor as written
  }
  return (
    <Select value={headings.some((h) => h.id === anchor) ? anchor : ''} onValueChange={(v) => navigate(`/help/${page.id}#${v}`)}>
      <SelectTrigger aria-label={`Section of ${page.title}`} className="mt-2 w-full">
        <SelectValue placeholder="Go to a section" />
      </SelectTrigger>
      <SelectContent>
        {headings.map((h) => (
          <SelectItem key={h.id} value={h.id}>
            {h.text}
          </SelectItem>
        ))}
      </SelectContent>
    </Select>
  )
}

function Start({ groups }: { groups: Group[] }) {
  return (
    <div className="grid gap-8">
      {groups.map((g) => (
        <section key={g.title} aria-label={g.title}>
          <h2 className="mb-3 text-base font-semibold">{g.title}</h2>
          <ul className="grid gap-3 sm:grid-cols-2 xl:grid-cols-3">
            {g.pages.map((p) => (
              <li key={p.id}>
                <Link to={`/help/${p.id}`} className="flex h-full flex-col gap-1 rounded-xl border bg-card p-4 shadow-xs outline-none transition-colors hover:border-primary/40 hover:bg-accent/40 focus-visible:ring-[3px] focus-visible:ring-ring">
                  <span className="font-medium">{p.title}</span>
                  <span className="text-sm text-muted-foreground">{p.about}</span>
                </Link>
              </li>
            ))}
          </ul>
        </section>
      ))}
    </div>
  )
}

/**
 * A page of the manual, drawn by the chat's Markdown. Its headings get their anchors, and its
 * links are pointed into the app: another page of the manual opens here, the web as it is, and a
 * file of the repository the manual does not hold (or a page for admins) shows as its words.
 */
function PageView({ page, isAdmin }: { page: Page; isAdmin: boolean }) {
  const ref = useRef<HTMLDivElement>(null)
  const navigate = useNavigate()
  const { hash } = useLocation()

  useLayoutEffect(() => {
    const root = ref.current
    if (!root) return
    const slugger = new Slugger()
    for (const h of root.querySelectorAll('h1, h2, h3, h4, h5, h6')) h.id = slugger.slug(h.textContent ?? '')
    for (const a of root.querySelectorAll('a[href]')) {
      const target = resolveLink(page.file, a.getAttribute('href') ?? '')
      if (target.kind === 'web') continue
      if (target.kind === 'manual' && (!target.doc || canRead(target.doc, isAdmin))) {
        a.setAttribute('href', target.to)
        a.setAttribute('data-manual', '')
        continue
      }
      const words = document.createElement('span')
      words.className = 'manual-off'
      words.title = target.kind === 'source' ? `In the source, not in the manual: ${target.path}` : 'A page for admins'
      words.append(...a.childNodes)
      a.replaceWith(words)
    }
  }, [page, isAdmin])

  useEffect(() => {
    const root = ref.current
    if (!root) return
    const onClick = (e: MouseEvent) => {
      const a = (e.target as Element | null)?.closest?.('a[data-manual]')
      if (!a || e.defaultPrevented || e.button !== 0 || e.metaKey || e.ctrlKey || e.shiftKey || e.altKey) return
      e.preventDefault()
      navigate(a.getAttribute('href') ?? '/help')
    }
    root.addEventListener('click', onClick)
    return () => root.removeEventListener('click', onClick)
  }, [navigate])

  useEffect(() => {
    if (!hash) {
      window.scrollTo(0, 0)
      return
    }
    let anchor = hash.slice(1)
    try {
      anchor = decodeURIComponent(anchor)
    } catch {
      // a malformed address: its anchor as written
    }
    document.getElementById(anchor)?.scrollIntoView({ block: 'start' })
  }, [page.id, hash])

  return (
    <article ref={ref} aria-label={page.title} className="max-w-3xl [&_:is(h1,h2,h3,h4,h5,h6)]:scroll-mt-20 [&_.manual-off]:underline [&_.manual-off]:decoration-dotted [&_.manual-off]:underline-offset-2">
      <Markdown text={page.text} />
    </article>
  )
}

function Results({ q, pages }: { q: string; pages: Page[] }) {
  const hits = useMemo(() => searchManual(pages, q), [pages, q])
  if (!hits.length)
    return (
      <EmptyState icon={Search} title="Nothing in the manual matches">
        Try fewer words, or other ones.
      </EmptyState>
    )
  return (
    <section aria-label="Search results" className="max-w-3xl">
      <p className="mb-3 text-sm text-muted-foreground" aria-live="polite">
        {hits.length === 1 ? '1 section' : `${hits.length} sections`}
      </p>
      <ol className="grid gap-2">
        {hits.map((h) => (
          <li key={`${h.doc.id}#${h.section.id}`}>
            <Link to={`/help/${h.doc.id}#${h.section.id}`} className="block rounded-lg border p-3 outline-none hover:bg-accent/50 focus-visible:ring-[3px] focus-visible:ring-ring">
              <span className="block text-xs text-muted-foreground">{h.doc.title}</span>
              <span className="block font-medium">{h.section.title}</span>
              <span className="mt-1 block text-sm text-muted-foreground">
                <Snippet hit={h} />
              </span>
            </Link>
          </li>
        ))}
      </ol>
    </section>
  )
}

function Snippet({ hit }: { hit: SearchHit }) {
  const out: ReactNode[] = []
  let at = 0
  for (const [from, to] of hit.marks) {
    out.push(hit.snippet.slice(at, from), <mark key={from} className="rounded-sm bg-primary/15 text-foreground">{hit.snippet.slice(from, to)}</mark>)
    at = to
  }
  out.push(hit.snippet.slice(at))
  return <>{out}</>
}
