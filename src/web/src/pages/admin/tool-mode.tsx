import { useMutation } from '@tanstack/react-query'
import { useId } from 'react'
import { Label } from '@/components/ui/label'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { toast } from '@/components/ui/toaster'
import { api, errorMessage } from '@/lib/api'

export type ToolMode = 'direct' | 'code' | 'both'

/** How a chat model is given its tools: the admins' choice (set), else the automatic one (small models direct, the rest as code). */
export interface ModelTools {
  mode: ToolMode
  set: boolean
  automatic: ToolMode
}

const labels: Record<ToolMode, string> = { direct: 'Direct', code: 'As code', both: 'Both' }

const about: Record<ToolMode, string> = {
  direct: 'Each tool called on its own: no run_code. Best for small models, which write wrong programs more often than they make a wrong call.',
  code: 'Its tools only as functions in a Python program (run_code), named in its instructions instead of every definition: fewer tokens, many calls in one step.',
  both: 'Every tool, and run_code for many calls whose results it only filters or joins: a program for many calls, a direct call for one or two.',
}

/** "Tools": how the chat and Code Arena give this model its tools, from its next answer (Code Arena's next start). */
export function ToolModePicker({ model, tools, onChanged }: { model: string; tools: ModelTools; onChanged: () => void }) {
  const id = useId()
  const save = useMutation({
    mutationFn: (mode: ToolMode | null) =>
      api<{ toolMode: ToolMode; toolModeSet: boolean }>(`/api/admin/models/${encodeURIComponent(model)}/tools`, { method: 'PUT', body: { mode } }),
    onSuccess: (r) => toast.success(`${model}: tools ${labels[r.toolMode].toLowerCase()}${r.toolModeSet ? '' : ' (automatic)'}`),
    onSettled: onChanged,
    onError: (e) => toast.error(errorMessage(e)),
  })
  return (
    <div className="grid gap-2 sm:grid-cols-[auto_minmax(0,1fr)] sm:items-center sm:gap-3">
      <Label htmlFor={id} className="text-muted-foreground">
        Tools
      </Label>
      <div className="grid gap-1">
        <Select value={tools.set ? tools.mode : 'auto'} disabled={save.isPending} onValueChange={(v: ToolMode | 'auto') => save.mutate(v === 'auto' ? null : v)}>
          <SelectTrigger id={id} size="sm" className="w-56" aria-describedby={`${id}-about`}>
            <SelectValue />
          </SelectTrigger>
          <SelectContent>
            <SelectItem value="auto">Automatic: {labels[tools.automatic].toLowerCase()}</SelectItem>
            <SelectItem value="direct">Direct</SelectItem>
            <SelectItem value="code">As code</SelectItem>
            <SelectItem value="both">Both</SelectItem>
          </SelectContent>
        </Select>
        <p id={`${id}-about`} className="text-xs text-muted-foreground">
          {about[tools.mode]}
        </p>
      </div>
    </div>
  )
}
