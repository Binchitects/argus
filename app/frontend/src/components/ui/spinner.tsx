import { Loader2 } from 'lucide-react'
import { cn } from '@/lib/utils'

export function Spinner({ className, label }: { className?: string; label?: string }) {
  return <Loader2 className={cn('size-4 animate-spin', className)} aria-label={label} aria-hidden={label ? undefined : true} />
}
