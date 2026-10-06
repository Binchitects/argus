import { BookOpen, CircleHelp } from 'lucide-react'
import { useCallback, useState, type ReactNode } from 'react'
import { ScrollRegion } from '@/components/app/scroll-region'
import { Kbd } from '@/components/ui/kbd'
import { Sheet, SheetContent, SheetDescription, SheetHeader, SheetTitle } from '@/components/ui/sheet'
import { Tooltip } from '@/components/ui/tooltip'
import { Inline } from '@/help/inline'
import { cn } from '@/lib/utils'
import { OpenHelp, useOpenHelp } from './help-context'
import { about, keys, parts, tasks, type Part } from './help-text'

/** Around the workbench: the help, which Help in the activity bar and the ? in each panel's header open. */
export function HelpProvider({ manual, children }: { manual: string | null; children: ReactNode }) {
  // Open, and the part it was asked from (a panel's ?), if any.
  const [help, setHelp] = useState<{ open: boolean; part?: Part }>({ open: false })
  const open = useCallback((part?: Part) => setHelp({ open: true, part }), [])
  return (
    <OpenHelp.Provider value={open}>
      {children}
      <HelpSheet open={help.open} part={help.part} manual={manual} onOpenChange={(o) => setHelp((h) => ({ ...h, open: o }))} />
    </OpenHelp.Provider>
  )
}

/** The ? in a panel's header: the help, at this panel's part. Nothing outside the workbench. */
export function PartHelp({ part, className }: { part: Part; className?: string }) {
  const open = useOpenHelp()
  if (!open) return null
  const { name } = parts[part]
  return (
    <Tooltip content={`Help: ${name}`}>
      <button
        type="button"
        aria-label={`Help: ${name}`}
        onClick={() => open(part)}
        className={cn('grid size-6 shrink-0 place-items-center rounded-sm text-muted-foreground outline-none hover:bg-accent hover:text-foreground focus-visible:ring-2 focus-visible:ring-ring [&_svg]:size-3.5', className)}
      >
        <CircleHelp aria-hidden="true" />
      </button>
    </Tooltip>
  )
}

const heading = 'text-xs font-semibold tracking-wider text-muted-foreground uppercase'

/** A link to the manual's Code Arena page, at one of its headings. */
function ManualLink({ manual, anchor, children }: { manual: string; anchor: string; children: ReactNode }) {
  return (
    <a href={`${manual}#${anchor}`} target="_blank" rel="noreferrer" className="inline-flex items-center gap-2 font-medium text-primary-ink underline-offset-2 hover:underline">
      <BookOpen className="size-4" aria-hidden="true" /> {children}
    </a>
  )
}

/**
 * Code Arena's help, over the workbench (Esc or a click beside it closes it): what it is for,
 * every part, the common tasks and the keys, with the part asked about first.
 */
function HelpSheet({ open, part, manual, onOpenChange }: { open: boolean; part?: Part; manual: string | null; onOpenChange: (open: boolean) => void }) {
  const shown = part ? parts[part] : null
  // The manual of the Arena code-arena signed in to (its web's /help/code-arena); none without its address.
  const book = manual && /^https?:\/\//.test(manual) ? manual : null
  return (
    <Sheet open={open} onOpenChange={onOpenChange}>
      <SheetContent className="gap-0 text-foreground sm:max-w-[24rem]">
        <SheetHeader>
          <p className={heading}>Help</p>
          <SheetTitle className="text-lg font-semibold">Code Arena</SheetTitle>
          <SheetDescription>{about}</SheetDescription>
        </SheetHeader>
        <ScrollRegion label="Help: Code Arena" className="grid flex-1 content-start gap-6 p-5 text-sm">
          {shown && (
            <section aria-labelledby="help-part" className="grid gap-2 rounded-lg border bg-muted/40 p-3">
              <h3 id="help-part" className={heading}>
                This part: {shown.name}
              </h3>
              <p>
                <Inline text={shown.text} />
              </p>
              {book && (
                <ManualLink manual={book} anchor={shown.manual}>
                  More on this in the manual
                </ManualLink>
              )}
            </section>
          )}
          <section aria-labelledby="help-parts" className="grid gap-3">
            <h3 id="help-parts" className={heading}>
              Its parts
            </h3>
            <dl className="grid gap-3">
              {(Object.entries(parts) as [Part, (typeof parts)[Part]][]).map(([id, p]) => (
                <div key={id} aria-current={id === part ? 'true' : undefined} className={cn(id === part && '-mx-2 rounded-md bg-accent px-2 py-1.5')}>
                  <dt className="font-medium">{p.name}</dt>
                  <dd className="text-muted-foreground">
                    <Inline text={p.text} />
                  </dd>
                </div>
              ))}
            </dl>
          </section>
          <section aria-labelledby="help-tasks" className="grid gap-4">
            <h3 id="help-tasks" className={heading}>
              How to
            </h3>
            {tasks.map((t) => (
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
          <section aria-labelledby="help-keys" className="grid gap-3">
            <h3 id="help-keys" className={heading}>
              Keys
            </h3>
            <dl className="grid grid-cols-[auto_1fr] items-center gap-x-4 gap-y-2">
              {keys.map(([k, what]) => (
                <div key={k} className="contents">
                  <dt className="justify-self-end">
                    <Kbd>{k}</Kbd>
                  </dt>
                  <dd className="text-muted-foreground">{what}</dd>
                </div>
              ))}
            </dl>
          </section>
        </ScrollRegion>
        <div className="grid gap-2 border-t p-4 text-sm">
          {book ? (
            <ManualLink manual={book} anchor="the-ide">
              Code Arena in the manual
            </ManualLink>
          ) : (
            <p className="text-muted-foreground">The whole story is in your Argus Arena&apos;s manual: Manual, then Code Arena.</p>
          )}
        </div>
      </SheetContent>
    </Sheet>
  )
}
