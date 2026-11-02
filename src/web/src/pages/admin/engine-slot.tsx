import type { EngineModel } from './engine-api'
import { FallbackField } from './engine-fields'
import type { ChatModelSlot } from './model-form'

/** The llama.cpp model's engine fields, mounted in the chat model's section: its fallback (its engine is the dialog's Engine field). */
export const engineSlot: ChatModelSlot = {
  initial: (saved) => ({ fallback: (saved as EngineModel | null)?.fallback ?? '' }),
  render: ({ saved, values, set, errors }) => (
    <div className="grid gap-4 sm:grid-cols-2">
      <FallbackField self={saved?.name} value={String(values.fallback ?? '')} onChange={(v) => set({ fallback: v })} error={errors.fallback} />
    </div>
  ),
}
