import { Check, ChevronRight, Circle } from 'lucide-react'
import { DropdownMenu as Menu } from 'radix-ui'
import type { ComponentProps } from 'react'
import { cn } from '@/lib/utils'

export const DropdownMenu = Menu.Root
export const DropdownMenuTrigger = Menu.Trigger
export const DropdownMenuGroup = Menu.Group
export const DropdownMenuSub = Menu.Sub
export const DropdownMenuRadioGroup = Menu.RadioGroup

const content =
  'z-50 min-w-[10rem] overflow-hidden rounded-md border bg-popover p-1 text-popover-foreground shadow-lg data-[state=open]:animate-in data-[state=closed]:animate-out data-[state=closed]:fade-out-0 data-[state=open]:fade-in-0 data-[state=closed]:zoom-out-95 data-[state=open]:zoom-in-95'
const item =
  'relative flex cursor-default items-center gap-2 rounded-sm px-2 py-1.5 text-sm outline-none select-none focus:bg-accent focus:text-accent-foreground data-[disabled]:pointer-events-none data-[disabled]:opacity-50 [&_svg]:size-4 [&_svg]:shrink-0 [&_svg]:text-muted-foreground'

export function DropdownMenuContent({ className, sideOffset = 4, ...props }: ComponentProps<typeof Menu.Content>) {
  return (
    <Menu.Portal>
      <Menu.Content sideOffset={sideOffset} className={cn(content, className)} {...props} />
    </Menu.Portal>
  )
}

export function DropdownMenuItem({ className, variant, ...props }: ComponentProps<typeof Menu.Item> & { variant?: 'destructive' }) {
  return <Menu.Item className={cn(item, variant === 'destructive' && 'text-destructive-ink focus:bg-destructive/10 focus:text-destructive-ink [&_svg]:text-destructive-ink', className)} {...props} />
}

export function DropdownMenuCheckboxItem({ className, children, ...props }: ComponentProps<typeof Menu.CheckboxItem>) {
  return (
    <Menu.CheckboxItem className={cn(item, 'pl-8', className)} {...props}>
      <span className="absolute left-2 flex size-3.5 items-center justify-center">
        <Menu.ItemIndicator>
          <Check />
        </Menu.ItemIndicator>
      </span>
      {children}
    </Menu.CheckboxItem>
  )
}

export function DropdownMenuRadioItem({ className, children, ...props }: ComponentProps<typeof Menu.RadioItem>) {
  return (
    <Menu.RadioItem className={cn(item, 'pl-8', className)} {...props}>
      <span className="absolute left-2 flex size-3.5 items-center justify-center">
        <Menu.ItemIndicator>
          <Circle className="size-2 fill-current" />
        </Menu.ItemIndicator>
      </span>
      {children}
    </Menu.RadioItem>
  )
}

export function DropdownMenuLabel({ className, ...props }: ComponentProps<typeof Menu.Label>) {
  return <Menu.Label className={cn('px-2 py-1.5 text-xs font-medium text-muted-foreground', className)} {...props} />
}

export function DropdownMenuSeparator({ className, ...props }: ComponentProps<typeof Menu.Separator>) {
  return <Menu.Separator className={cn('-mx-1 my-1 h-px bg-border', className)} {...props} />
}

export function DropdownMenuShortcut({ className, ...props }: ComponentProps<'span'>) {
  return <span className={cn('ml-auto text-xs tracking-widest text-muted-foreground', className)} {...props} />
}

export function DropdownMenuSubTrigger({ className, children, ...props }: ComponentProps<typeof Menu.SubTrigger>) {
  return (
    <Menu.SubTrigger className={cn(item, 'data-[state=open]:bg-accent', className)} {...props}>
      {children}
      <ChevronRight className="ml-auto" />
    </Menu.SubTrigger>
  )
}

export function DropdownMenuSubContent({ className, ...props }: ComponentProps<typeof Menu.SubContent>) {
  return (
    <Menu.Portal>
      <Menu.SubContent className={cn(content, className)} {...props} />
    </Menu.Portal>
  )
}
