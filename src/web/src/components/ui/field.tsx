import { createContext, use, useId, type ReactNode } from 'react'
import { cn } from '@/lib/utils'
import { Label } from './label'

interface FieldControl {
  id: string
  'aria-describedby'?: string
  'aria-invalid'?: boolean
}

const FieldContext = createContext<FieldControl | null>(null)

/** Inside a <Field>: the id and descriptions for the control. Controls spread it first, so explicit props win. */
export function useFieldControl(): FieldControl | Record<string, never> {
  return use(FieldContext) ?? {}
}

/**
 * A labelled form control with its hint and error, wired for screen readers:
 * the control (Input, Textarea, SelectTrigger) takes the id, aria-describedby
 * and aria-invalid from this field.
 */
export function Field({ label, hint, error, children, className }: { label: ReactNode; hint?: ReactNode; error?: string; children: ReactNode; className?: string }) {
  const id = useId()
  const hintId = hint && !error ? `${id}-hint` : undefined
  const errorId = error ? `${id}-error` : undefined
  const control: FieldControl = { id, 'aria-describedby': [hintId, errorId].filter(Boolean).join(' ') || undefined, 'aria-invalid': error ? true : undefined }
  return (
    <div className={cn('grid gap-2', className)}>
      <Label htmlFor={id}>{label}</Label>
      <FieldContext value={control}>{children}</FieldContext>
      {hintId && (
        <p id={hintId} className="text-xs text-muted-foreground">
          {hint}
        </p>
      )}
      {errorId && (
        <p id={errorId} className="text-xs font-medium text-destructive">
          {error}
        </p>
      )}
    </div>
  )
}
