import { Toaster as Sonner } from 'sonner'
import { useTheme } from '@/lib/theme'

export { toast } from 'sonner'

export function Toaster() {
  const { resolved } = useTheme()
  return (
    <Sonner
      theme={resolved}
      position="bottom-right"
      closeButton
      toastOptions={{
        classNames: {
          toast: 'group !rounded-lg !border !border-border !bg-popover !text-popover-foreground !shadow-lg',
          description: '!text-muted-foreground',
        },
      }}
    />
  )
}
