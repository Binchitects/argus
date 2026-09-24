import type { ComponentProps } from 'react'
import { cn } from '@/lib/utils'

export function Card({ className, ...props }: ComponentProps<'section'>) {
  return <section className={cn('flex min-w-0 flex-col gap-5 rounded-xl border bg-card py-5 text-card-foreground shadow-xs', className)} {...props} />
}

export function CardHeader({ className, ...props }: ComponentProps<'div'>) {
  return <div className={cn('grid auto-rows-min grid-rows-[auto_auto] items-start gap-1 px-5 has-[[data-slot=card-action]]:grid-cols-[1fr_auto]', className)} {...props} />
}

export function CardTitle({ className, children, ...props }: ComponentProps<'h2'>) {
  return (
    <h2 className={cn('text-[0.9375rem] leading-tight font-semibold', className)} {...props}>
      {children}
    </h2>
  )
}

export function CardDescription({ className, ...props }: ComponentProps<'p'>) {
  return <p className={cn('text-sm text-muted-foreground', className)} {...props} />
}

export function CardAction({ className, ...props }: ComponentProps<'div'>) {
  return <div data-slot="card-action" className={cn('col-start-2 row-span-2 row-start-1 self-start justify-self-end', className)} {...props} />
}

export function CardContent({ className, ...props }: ComponentProps<'div'>) {
  return <div className={cn('px-5', className)} {...props} />
}

export function CardFooter({ className, ...props }: ComponentProps<'div'>) {
  return <div className={cn('flex items-center gap-2 border-t px-5 pt-4', className)} {...props} />
}
