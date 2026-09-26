import type { ComponentProps } from 'react'
import { cn } from '@/lib/utils'

export function Kbd({ className, ...props }: ComponentProps<'kbd'>) {
  return <kbd className={cn('pointer-events-none inline-flex h-5 min-w-5 items-center justify-center gap-0.5 rounded border bg-muted px-1 font-mono text-[0.6875rem] font-medium text-muted-foreground select-none', className)} {...props} />
}
