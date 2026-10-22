import { FoldVertical, SquareSlash, X } from 'lucide-react'
import { useEffect, useId, useRef, type KeyboardEvent, type RefObject } from 'react'
import { Link } from 'react-router'
import { Kbd } from '@/components/ui/kbd'
import { fieldClass } from '@/components/ui/input'
import { mustFill, parameterLabel, type SkillItem, type SlashItem } from '@/lib/skills'
import { cn } from '@/lib/utils'

/** The / menu: the skills (and commands) that match, above the message box. Arrows move, Enter or Tab use one, Esc closes it. */
export function SlashMenu({ id, items, active, onPick, onActive }: { id: string; items: SlashItem[]; active: number; onPick: (item: SlashItem) => void; onActive: (i: number) => void }) {
  const list = useRef<HTMLUListElement>(null)
  useEffect(() => {
    list.current?.querySelector<HTMLElement>('[aria-selected="true"]')?.scrollIntoView?.({ block: 'nearest' })
  }, [active])
  return (
    <div className="absolute inset-x-0 bottom-full z-20 mb-2 overflow-hidden rounded-xl border bg-popover text-popover-foreground shadow-lg animate-enter">
      {/* oxlint-disable-next-line jsx-a11y/prefer-tag-over-role, jsx-a11y/no-noninteractive-element-to-interactive-role -- the message box's popup list (a combobox's listbox); a <select> cannot be one */}
      <ul ref={list} id={id} role="listbox" aria-label="Skills" className="max-h-72 overflow-y-auto p-1">
        {items.map((item, i) => (
          <li
            key={item.key}
            id={`${id}-${i}`}
            // oxlint-disable-next-line jsx-a11y/prefer-tag-over-role, jsx-a11y/no-noninteractive-element-to-interactive-role -- an option of the listbox above
            role="option"
            aria-selected={i === active}
            // Chosen before the box loses focus.
            onMouseDown={(e) => {
              e.preventDefault()
              onPick(item)
            }}
            onMouseEnter={() => onActive(i)}
            className={cn('flex cursor-pointer items-center gap-2.5 rounded-lg px-2.5 py-2 text-sm', i === active && 'bg-accent')}
          >
            {item.command ? <FoldVertical className="size-4 shrink-0 text-muted-foreground" aria-hidden="true" /> : <SquareSlash className="size-4 shrink-0 text-primary-ink" aria-hidden="true" />}
            <span className="shrink-0 font-mono text-[0.8125rem] font-medium">/{item.name}</span>
            <span dir="auto" className="min-w-0 truncate">
              {item.title}
            </span>
            <span className="ml-auto shrink-0 truncate text-xs text-muted-foreground">{item.hint}</span>
          </li>
        ))}
      </ul>
      <p className="flex flex-wrap items-center gap-x-3 gap-y-1 border-t px-3 py-1.5 text-xs text-muted-foreground">
        <span>
          <Kbd>↑</Kbd> <Kbd>↓</Kbd> to choose
        </span>
        <span>
          <Kbd>Enter</Kbd> to use
        </span>
        <span>
          <Kbd>Esc</Kbd> to close
        </span>
        <Link to="/skills" className="ml-auto underline-offset-2 hover:underline" onMouseDown={(e) => e.preventDefault()}>
          Manage skills
        </Link>
      </p>
    </div>
  )
}

/**
 * The skill chosen for the message, above the message box: its name, what it is for, and a field for each of its
 * parameters (a default shown as the field's placeholder). Enter goes to the next field, and from the last to the
 * message box (what is written there goes with the skill); Shift+Enter adds a line.
 */
export function SkillFields({
  skill,
  values,
  missing,
  onChange,
  onClear,
  onLast,
  refs,
}: {
  skill: SkillItem
  values: Record<string, string>
  /** The parameters left empty when sending was tried. */
  missing: string[]
  onChange: (name: string, value: string) => void
  onClear: () => void
  onLast: () => void
  refs: RefObject<Record<string, HTMLTextAreaElement | null>>
}) {
  const id = useId()
  const onKey = (e: KeyboardEvent<HTMLTextAreaElement>, i: number) => {
    if (e.key !== 'Enter' || e.shiftKey || e.nativeEvent.isComposing) return
    e.preventDefault()
    const next = skill.parameters[i + 1]
    if (next) refs.current[next.name]?.focus()
    else onLast()
  }
  return (
    <fieldset className="grid gap-2 border-b px-3 pt-3 pb-3">
      <legend className="sr-only">Use /{skill.name}</legend>
      <div className="flex min-w-0 items-center gap-2 text-sm">
        <span className="inline-flex shrink-0 items-center gap-1.5 rounded-md bg-primary/10 px-2 py-0.5 text-primary-ink">
          <SquareSlash className="size-3.5" aria-hidden="true" />
          <span className="font-mono text-[0.8125rem] font-medium">{skill.name}</span>
        </span>
        <span dir="auto" className="min-w-0 truncate text-muted-foreground" title={skill.description || undefined}>
          {skill.title}
        </span>
        <button
          type="button"
          onClick={onClear}
          className="ml-auto flex size-6 shrink-0 items-center justify-center rounded-md text-muted-foreground outline-none hover:bg-accent hover:text-foreground focus-visible:ring-[3px] focus-visible:ring-ring"
          aria-label={`Stop using /${skill.name}`}
        >
          <X className="size-3.5" />
        </button>
      </div>
      {skill.parameters.length > 0 && (
        <div className="grid gap-2 sm:grid-cols-2">
          {skill.parameters.map((p, i) => {
            const empty = missing.includes(p.name)
            const hint = [p.description, mustFill(p) ? null : p.default ? `default: ${p.default}` : 'optional'].filter(Boolean).join(' · ')
            return (
              <div key={p.name} className="grid min-w-0 content-start gap-1">
                <label htmlFor={`${id}-${i}`} className="text-xs font-medium text-muted-foreground">
                  {parameterLabel(p.name)}
                  {mustFill(p) && (
                    <span aria-hidden="true" className="text-destructive">
                      {' '}
                      *
                    </span>
                  )}
                </label>
                <textarea
                  ref={(el) => {
                    refs.current[p.name] = el
                  }}
                  id={`${id}-${i}`}
                  dir="auto"
                  rows={1}
                  value={values[p.name] ?? ''}
                  placeholder={p.default ?? undefined}
                  aria-required={mustFill(p) || undefined}
                  onChange={(e) => onChange(p.name, e.target.value)}
                  onKeyDown={(e) => onKey(e, i)}
                  aria-invalid={empty || undefined}
                  aria-describedby={empty ? `${id}-${i}-empty` : hint ? `${id}-${i}-hint` : undefined}
                  className={cn(fieldClass, 'max-h-32 min-h-9 resize-y py-1.5')}
                />
                {empty ? (
                  <p id={`${id}-${i}-empty`} className="text-xs text-destructive">
                    Fill this in to send.
                  </p>
                ) : (
                  hint && (
                    <p id={`${id}-${i}-hint`} className="truncate text-xs text-muted-foreground">
                      {hint}
                    </p>
                  )
                )}
              </div>
            )
          })}
        </div>
      )}
    </fieldset>
  )
}
