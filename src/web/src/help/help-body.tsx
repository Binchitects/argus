import { useQuery } from '@tanstack/react-query'
import { BookOpen, LifeBuoy } from 'lucide-react'
import { Link, useLocation } from 'react-router'
import { ScrollRegion } from '@/components/app/scroll-region'
import { SheetDescription, SheetHeader, SheetTitle } from '@/components/ui/sheet'
import { infoQuery, meQuery, supportHref } from '@/lib/api'
import { cn } from '@/lib/utils'
import { manualHref, topicAnchor } from './pages-doc'
import { helpFor } from './route-help'
import type { HelpPart } from './topics'

/** Help text: **words** are the page's own labels, in bold. */
export function Inline({ text }: { text: string }) {
  return <>{text.split(/\*\*(.+?)\*\*/g).map((part, i) => (i % 2 ? <strong key={i} className="font-medium text-foreground">{part}</strong> : part))}</>
}

function Parts({ parts, current }: { parts: (HelpPart & { id?: string })[]; current?: string }) {
  return (
    <dl className="grid gap-3">
      {parts.map((p) => (
        <div key={p.name} aria-current={p.id !== undefined && p.id === current ? 'true' : undefined} className={cn(p.id !== undefined && p.id === current && '-mx-2 rounded-md bg-accent px-2 py-1.5')}>
          <dt className="font-medium">{p.name}</dt>
          <dd className="text-muted-foreground">
            <Inline text={p.text} />
          </dd>
        </div>
      ))}
    </dl>
  )
}

/** The help of the page on screen: what it is for, its parts, how to do the common tasks. */
export function HelpBody({ onNavigate }: { onNavigate: () => void }) {
  const { pathname, hash } = useLocation()
  const me = useQuery(meQuery).data
  const support = useQuery(infoQuery).data?.supportContact
  const supportLink = support ? supportHref(support) : null
  const { id, topic, section } = helpFor(pathname, hash, me?.isAdmin ?? false)
  const heading = 'text-xs font-semibold tracking-wider text-muted-foreground uppercase'
  return (
    <>
      <SheetHeader>
        <p className={heading}>Help</p>
        <SheetTitle className="text-lg">{topic.title}</SheetTitle>
        <SheetDescription>
          <Inline text={topic.about} />
        </SheetDescription>
      </SheetHeader>
      <ScrollRegion label={`Help: ${topic.title}`} className="grid flex-1 content-start gap-6 p-5 text-sm">
        {section && (
          <section aria-labelledby="help-group" className="rounded-lg border bg-muted/40 p-3">
            <h3 id="help-group" className={heading}>
              {topic.sectionNames?.one ?? 'This part'}: {section.name}
            </h3>
            <p className="mt-1.5">
              <Inline text={section.text} />
            </p>
          </section>
        )}
        {topic.parts.length > 0 && (
          <section aria-labelledby="help-parts" className="grid gap-3">
            <h3 id="help-parts" className={heading}>
              On this page
            </h3>
            <Parts parts={topic.parts} />
          </section>
        )}
        {topic.sections && (
          <section aria-labelledby="help-groups" className="grid gap-3">
            <h3 id="help-groups" className={heading}>
              {topic.sectionNames?.all ?? 'Its parts'}
            </h3>
            <Parts parts={Object.entries(topic.sections).map(([key, p]) => ({ ...p, id: key }))} current={section?.id} />
          </section>
        )}
        {topic.tasks.length > 0 && (
          <section aria-labelledby="help-tasks" className="grid gap-4">
            <h3 id="help-tasks" className={heading}>
              How to
            </h3>
            {topic.tasks.map((t) => (
              <div key={t.title}>
                <h4 className="font-medium">{t.title}</h4>
                <ol className="mt-1.5 grid list-decimal gap-1 pl-5 text-muted-foreground marker:text-foreground">
                  {t.steps.map((s) => (
                    <li key={s}>
                      <Inline text={s} />
                    </li>
                  ))}
                </ol>
              </div>
            ))}
          </section>
        )}
      </ScrollRegion>
      {/* The manual needs a sign-in; the sign-in page offers its support contact instead. */}
      <div className="grid gap-2 border-t p-4 text-sm">
        {me && (
          <>
            {topic.manual && (
              <Link to={manualHref(topic.manual)} onClick={onNavigate} className="inline-flex items-center gap-2 font-medium text-primary-ink underline-offset-2 hover:underline">
                <BookOpen className="size-4" aria-hidden="true" /> Read more in the manual
              </Link>
            )}
            <Link to={`/help/pages#${topicAnchor(id)}`} onClick={onNavigate} className="inline-flex items-center gap-2 text-muted-foreground underline-offset-2 hover:text-foreground hover:underline">
              <BookOpen className="size-4" aria-hidden="true" /> Every page explained, in the manual
            </Link>
          </>
        )}
        {support && (
          <p className="inline-flex items-center gap-2 text-muted-foreground">
            <LifeBuoy className="size-4" aria-hidden="true" /> Still stuck?{' '}
            {supportLink ? (
              <a href={supportLink} target="_blank" rel="noreferrer" className="underline-offset-2 hover:text-foreground hover:underline">
                Ask {support}
              </a>
            ) : (
              <>Ask {support}</>
            )}
          </p>
        )}
      </div>
    </>
  )
}
