import { engineLabel, serverPerModel, type EngineInstance, type Snapshot } from './engine-api'
import type { LibraryFile, ModelProfile } from './model-profile'

/** The kinds of engine a chat model runs on, in the Engine field's order. */
export type ChatEngineKind = 'llamacpp' | 'vllm' | 'sglang'

const kinds: ChatEngineKind[] = ['llamacpp', 'vllm', 'sglang']

/** The feature (deploy/features) that runs this stack's engine of each kind. */
const featureOf: Record<ChatEngineKind, string> = { llamacpp: 'chat-models', vllm: 'vllm', sglang: 'sglang' }

/** One choice of the Engine field: an engine instance, or a kind this stack has none of, and why it cannot be chosen. */
export interface EngineChoice {
  /** The instance's name (llama.cpp's is llamacpp); for a kind with none, the kind. */
  value: string
  kind: ChatEngineKind
  /** "vLLM", or "vLLM · name" when its kind has several. */
  label: string
  /** Why it cannot be chosen, said short on the option; null: it can. */
  why: string | null
  /** Not running now, though it can be chosen: a model added there waits for it. */
  down: boolean
  /** Why it cannot be chosen (or is not running) in full, with how to turn it on, said under the field. */
  detail: string | null
}

/** The engine a saved chat model runs on: its kind, and its instance by name. */
export function homeOf(m: { engineKind?: string; engine?: string | null }, instances: EngineInstance[]): { name: string; kind: ChatEngineKind } {
  const kind: ChatEngineKind = m.engineKind === 'vllm' || m.engineKind === 'sglang' ? m.engineKind : 'llamacpp'
  if (kind === 'llamacpp') return { name: instances.find((i) => i.kind === kind)?.name ?? kind, kind }
  return { name: m.engine || (instances.find((i) => i.kind === kind)?.name ?? kind), kind }
}

/** A chat model's engine for its card: "llama.cpp", "vLLM", or "vLLM · name" when its kind has several (or it names one not listed). */
export function engineName(m: { engineKind?: string; engine?: string | null }, instances: EngineInstance[]): string {
  const home = homeOf(m, instances)
  const of = instances.filter((i) => i.kind === home.kind)
  return serverPerModel(home.kind) && (of.length > 1 || (!!m.engine && !of.some((i) => i.name === home.name))) ? `${engineLabel(home.kind)} · ${home.name}` : engineLabel(home.kind)
}

/**
 * Every engine a chat model may run on, in kind order: each instance of llama.cpp, vLLM and SGLang this stack lists (and
 * the model's own). One that does not run here is there all the same, with why: its feature off, or (for a move, which the
 * API refuses then) not running; a kind this stack has none of, too.
 */
export function engineChoices(instances: EngineInstance[], { llama, current, moving }: { llama: boolean; current: { name: string; kind: ChatEngineKind } | null; moving: boolean }): EngineChoice[] {
  const out: EngineChoice[] = []
  for (const kind of kinds) {
    const label = engineLabel(kind)
    let of = instances.filter((i) => i.kind === kind)
    // A list without llama.cpp's own: it runs where chat models can be added.
    if (kind === 'llamacpp' && of.length === 0 && llama) of = [{ name: kind, kind, managed: true, problem: null }]
    if (current?.kind === kind && !of.some((i) => i.name === current.name)) of = [...of, { name: current.name, kind, managed: true, problem: null }]
    if (of.length === 0) {
      out.push({
        value: kind, kind, label, why: 'not on this stack', down: false,
        detail: kind === 'llamacpp' ? 'llama.cpp does not run on this stack (on Kubernetes, llamacpp.enabled).' : `This stack has no ${label} engine (Engine:Vllm names them; on Kubernetes, ${kind}.enabled).`,
      })
      continue
    }
    for (const i of of) {
      const name = of.length > 1 ? `${label} · ${i.name}` : label
      const own = i.name === current?.name
      const off = !own && i.on === false
      // A move to an engine that is not running is refused; a model added there waits for it.
      const down = !own && !off && kind !== 'llamacpp' && !!i.problem
      out.push({
        value: i.name, kind, label: name,
        why: off ? 'off here' : down && moving ? 'not running' : null,
        down,
        // As a feature that is off says it elsewhere: from the checkout, or with the offline installer on a host with no network.
        detail: off
          ? `${name} is off here: the installer turns it on with ./arenactl repair --with ${featureOf[kind]} from the checkout, or sh argus-arena-VERSION-offline.run repair --with ${featureOf[kind]} with the offline installer${kind === 'vllm' ? " (its image, arena-vllm, is vLLM's own with its GGUF plugin: the checkout builds it on VLLM_IMAGE, the offline installer carries it)" : ''}.`
          : down
            ? `${i.problem} ${moving ? 'A model moves there once it runs.' : 'A model added there waits for it.'}`
            : null,
      })
    }
  }
  return out
}

/** A name's words, for matching one form of a model to the other: "Qwen3-8B-Q4_K_M.gguf" and "Qwen/Qwen3-8B" are both qwen3, 8b. */
function words(text: string): string[] {
  return text
    .toLowerCase()
    .split(/[^a-z0-9]+/)
    .filter((w) => w && !/^(gguf|fp8|fp4|nvfp4|int4|int8|awq|gptq|w4a16|w8a8)$/.test(w))
}

/** A GGUF file's name without its split part and quantisation: Qwen3-8B-Q4_K_M-00001-of-00002.gguf -> Qwen3-8B. */
const ggufBase = (path: string) =>
  (path.split('/').pop() ?? '')
    .replace(/\.gguf$/i, '')
    .replace(/-\d{5}-of-\d{5}$/i, '')
    .replace(/[-_.](?:ud-)?(?:i?q\d(?:_[a-z0-9]+)*|f16|bf16|f32|fp16|mxfp4(?:_moe)?)$/i, '')

/** What a GGUF file may be called: its name, its folder's (less -GGUF), and the name in its header. */
function ggufNames(path: string, profile: ModelProfile | undefined): string[][] {
  const folder = path.split('/').slice(-2, -1)[0]?.replace(/[-_.]gguf$/i, '') ?? ''
  const own = profile?.name ? words(profile.name) : []
  const size = profile?.sizeLabel ? words(profile.sizeLabel) : []
  return [words(ggufBase(path)), words(folder), own, size.every((w) => own.includes(w)) ? [] : [...own, ...size]].filter((w) => w.length > 0)
}

/** The repository's name (not its owner) of a snapshot: hf/Qwen/Qwen3-8B@abc -> Qwen3-8B. */
const repoName = (s: { path: string; repo?: string }) => (s.repo ?? s.path.replace(/^hf\//, '').replace(/@.*$/, '')).split('/').pop() ?? ''

/** How alike two names are: 3 the same words, 2 the words of one within the other's (two at least), 0 not alike. */
function likeness(names: string[][], other: string[]): number {
  const b = new Set(other)
  let best = 0
  for (const n of names) {
    const a = new Set(n)
    const aInB = [...a].every((w) => b.has(w))
    const bInA = [...b].every((w) => a.has(w))
    if (aInB && bInA) best = 3
    else if ((aInB && a.size >= 2) || (bInA && b.size >= 2)) best = Math.max(best, 2)
  }
  return best
}

/** Whether a snapshot's architecture (Qwen3ForCausalLM) is the GGUF file's (qwen3). */
const sameArchitecture = (gguf: string | null | undefined, architectures: string[] | undefined) => {
  const a = (gguf ?? '').toLowerCase().replace(/[^a-z0-9]/g, '')
  return !!a && (architectures ?? []).some((x) => x.toLowerCase().replace(/[^a-z0-9]/g, '').startsWith(a))
}

/** The same model in the other form, and how it was matched. */
export interface FormMatch {
  path: string
  how: 'name' | 'name and architecture'
}

/** The library's snapshot of a GGUF file's model, by its names (and its architecture first); a whole one first; null: none. */
export function matchSnapshot(path: string, profile: ModelProfile | undefined, snapshots: Snapshot[]): FormMatch | null {
  const names = ggufNames(path, profile)
  const ranked = snapshots
    .map((s) => ({ s, alike: likeness(names, words(repoName(s))), arch: sameArchitecture(profile?.architecture, s.architectures) }))
    .filter((x) => x.alike >= 2)
    .sort((x, y) => y.alike + Number(y.arch) + Number(y.s.complete) / 2 - (x.alike + Number(x.arch) + Number(x.s.complete) / 2))
  const best = ranked[0]
  return best ? { path: best.s.path, how: best.arch ? 'name and architecture' : 'name' } : null
}

/** The library's GGUF file (a language model) of a snapshot's model, by its names (and its architecture first); null: none. */
export function matchGguf(path: string, snapshot: Snapshot | undefined, library: LibraryFile[]): FormMatch | null {
  const name = words(repoName(snapshot ?? { path }))
  const ranked = library
    .filter((f) => f.profile.kind === 'language')
    .map((f) => ({ f, alike: likeness(ggufNames(f.path, f.profile), name), arch: sameArchitecture(f.profile.architecture, snapshot?.architectures) }))
    .filter((x) => x.alike >= 2)
    .sort((x, y) => y.alike + Number(y.arch) - (x.alike + Number(x.arch)))
  const best = ranked[0]
  return best ? { path: best.f.path, how: best.arch ? 'name and architecture' : 'name' } : null
}
