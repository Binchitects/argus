import { cva, type VariantProps } from 'class-variance-authority'
import { AlertTriangle, CheckCircle2, Info, XCircle } from 'lucide-react'
import type { ComponentProps, ReactNode } from 'react'
import { cn } from '@/lib/utils'

const alertVariants = cva('relative grid grid-cols-[auto_1fr] gap-x-3 gap-y-0.5 rounded-lg border px-4 py-3 text-sm [&>svg]:mt-0.5 [&>svg]:size-4', {
  variants: {
    variant: {
      info: 'border-primary/25 bg-primary/5 [&>svg]:text-primary',
      success: 'border-success/30 bg-success/5 [&>svg]:text-success',
      warning: 'border-warning/40 bg-warning/8 [&>svg]:text-warning',
      destructive: 'border-destructive/30 bg-destructive/5 [&>svg]:text-destructive',
    },
  },
  defaultVariants: { variant: 'info' },
})

const icons = { info: Info, success: CheckCircle2, warning: AlertTriangle, destructive: XCircle }

/** A callout: state always has an icon and words, never colour alone. */
export function Alert({
  className,
  variant = 'info',
  title,
  children,
  action,
  ...props
}: Omit<ComponentProps<'div'>, 'title'> & VariantProps<typeof alertVariants> & { title?: ReactNode; action?: ReactNode }) {
  const Icon = icons[variant ?? 'info']
  return (
    <div role={variant === 'destructive' ? 'alert' : 'status'} className={cn(alertVariants({ variant }), className)} {...props}>
      <Icon aria-hidden="true" />
      <div className="col-start-2 min-w-0">
        {title && <p className="font-medium">{title}</p>}
        {children && <div className={cn('text-muted-foreground [&_p]:leading-relaxed', !title && 'text-foreground')}>{children}</div>}
      </div>
      {action && <div className="col-start-2 mt-2">{action}</div>}
    </div>
  )
}
