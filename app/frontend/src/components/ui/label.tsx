import { Label as LabelPrimitive } from 'radix-ui'
import type { ComponentProps } from 'react'
import { cn } from '@/lib/utils'

export function Label({ className, ...props }: ComponentProps<typeof LabelPrimitive.Root>) {
  return <LabelPrimitive.Root className={cn('flex items-center gap-2 text-sm font-medium leading-none select-none peer-disabled:opacity-50', className)} {...props} />
}
