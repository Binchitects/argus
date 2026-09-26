import { AlertDialog as AD } from 'radix-ui'
import { createContext, use, useCallback, useRef, useState, type ReactNode } from 'react'
import { cn } from '@/lib/utils'
import { buttonVariants } from './button'

interface ConfirmOptions {
  title: string
  description?: ReactNode
  confirm?: string
  destructive?: boolean
}

const ConfirmContext = createContext<((o: ConfirmOptions) => Promise<boolean>) | null>(null)

/** `const confirm = useConfirm(); if (await confirm({...})) ...` — an accessible replacement for window.confirm. */
export function useConfirm() {
  const ctx = use(ConfirmContext)
  if (!ctx) throw new Error('useConfirm needs <ConfirmProvider>')
  return ctx
}

export function ConfirmProvider({ children }: { children: ReactNode }) {
  const [options, setOptions] = useState<ConfirmOptions | null>(null)
  const resolver = useRef<((ok: boolean) => void) | null>(null)
  const ask = useCallback((o: ConfirmOptions) => {
    setOptions(o)
    return new Promise<boolean>((resolve) => {
      resolver.current = resolve
    })
  }, [])
  const settle = (ok: boolean) => {
    resolver.current?.(ok)
    resolver.current = null
    setOptions(null)
  }
  return (
    <ConfirmContext value={ask}>
      {children}
      <AD.Root open={!!options} onOpenChange={(open) => !open && settle(false)}>
        <AD.Portal>
          <AD.Overlay className="fixed inset-0 z-50 bg-black/40 data-[state=open]:animate-in data-[state=open]:fade-in-0" />
          <AD.Content className="fixed top-1/2 left-1/2 z-50 grid w-[calc(100vw-2rem)] max-w-md -translate-x-1/2 -translate-y-1/2 gap-4 rounded-xl border bg-popover p-6 text-popover-foreground shadow-2xl data-[state=open]:animate-in data-[state=open]:fade-in-0 data-[state=open]:zoom-in-95">
            <AD.Title className="text-base font-semibold">{options?.title}</AD.Title>
            {options?.description ? (
              <AD.Description className="text-sm text-muted-foreground">{options.description}</AD.Description>
            ) : (
              <AD.Description className="sr-only">Confirm or cancel.</AD.Description>
            )}
            <div className="flex flex-col-reverse gap-2 sm:flex-row sm:justify-end">
              <AD.Cancel className={buttonVariants({ variant: 'outline' })}>Cancel</AD.Cancel>
              <AD.Action className={cn(buttonVariants({ variant: options?.destructive ? 'destructive' : 'default' }))} onClick={() => settle(true)}>
                {options?.confirm ?? 'Continue'}
              </AD.Action>
            </div>
          </AD.Content>
        </AD.Portal>
      </AD.Root>
    </ConfirmContext>
  )
}
