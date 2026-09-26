import { Check, Copy, Eye, EyeOff } from 'lucide-react'
import { useState } from 'react'
import { Button } from '@/components/ui/button'
import { toast } from '@/components/ui/toaster'

/** A value shown once (a new API key): hidden by default, copyable. */
export function Secret({ label, value }: { label: string; value: string }) {
  const [shown, setShown] = useState(false)
  const [copied, setCopied] = useState(false)
  const copy = async () => {
    try {
      await navigator.clipboard.writeText(value)
      setCopied(true)
      toast.success(`${label} copied`)
      setTimeout(() => setCopied(false), 2000)
    } catch {
      toast.error('Could not copy. Select the text and copy it by hand.')
    }
  }
  return (
    <div className="grid gap-1.5">
      <p className="text-sm font-medium">{label}</p>
      <div className="flex items-center gap-1 rounded-md border bg-muted/50 p-1 pl-3">
        <code className="min-w-0 flex-1 truncate font-mono text-[0.8125rem]" aria-label={label}>
          {shown ? value : '•'.repeat(Math.min(40, value.length))}
        </code>
        <Button type="button" variant="ghost" size="icon-sm" onClick={() => setShown(!shown)} aria-label={shown ? `Hide ${label}` : `Show ${label}`}>
          {shown ? <EyeOff /> : <Eye />}
        </Button>
        <Button type="button" variant="ghost" size="icon-sm" onClick={copy} aria-label={`Copy ${label}`}>
          {copied ? <Check /> : <Copy />}
        </Button>
      </div>
    </div>
  )
}
