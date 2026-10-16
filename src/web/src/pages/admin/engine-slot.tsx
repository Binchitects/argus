import { Field } from '@/components/ui/field'
import { Input } from '@/components/ui/input'
import type { EngineModel } from './engine-api'
import { FallbackField } from './engine-fields'
import type { ChatModelSlot } from './model-form'

/** The llama.cpp model's engine fields, mounted in the chat model's section: which engine runs it, and its fallback. */
export const engineSlot: ChatModelSlot = {
  initial: (saved) => ({ fallback: (saved as EngineModel | null)?.fallback ?? '' }),
  render: ({ saved, values, set, errors }) => (
    <div className="grid gap-4 sm:grid-cols-2">
      <Field label="Engine" hint="llama.cpp: a GGUF file, on any GPU backend, with layers or experts in RAM when the GPUs are short.">
        <Input value="llama.cpp" readOnly />
      </Field>
      <FallbackField self={saved?.name} value={String(values.fallback ?? '')} onChange={(v) => set({ fallback: v })} error={errors.fallback} />
    </div>
  ),
}
