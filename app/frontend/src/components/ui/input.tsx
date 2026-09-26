import type { ComponentProps } from 'react'
import { cn } from '@/lib/utils'
import { useFieldControl } from './field'

export const fieldClass =
  'w-full min-w-0 rounded-md border border-input bg-card px-3 text-sm shadow-xs transition-[color,box-shadow] outline-none placeholder:text-muted-foreground focus-visible:border-primary focus-visible:ring-[3px] focus-visible:ring-ring disabled:cursor-not-allowed disabled:opacity-50 aria-invalid:border-destructive aria-invalid:ring-destructive/20'

export function Input({ className, type = 'text', ...props }: ComponentProps<'input'>) {
  return <input type={type} {...useFieldControl()} className={cn(fieldClass, 'h-9 py-1 file:border-0 file:bg-transparent file:text-sm file:font-medium', className)} {...props} />
}

export function Textarea({ className, ...props }: ComponentProps<'textarea'>) {
  return <textarea {...useFieldControl()} className={cn(fieldClass, 'min-h-20 py-2', className)} {...props} />
}
