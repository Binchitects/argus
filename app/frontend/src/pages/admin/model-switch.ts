/** KEY=value lines of a sample's model block; the host's own model directory is kept. */
export function blockChanges(block: string): { key: string; value: string }[] {
  return block
    .split('\n')
    .map((l) => /^([A-Z][A-Z0-9_]*)=(.*)$/.exec(l.trim()))
    .filter((m): m is RegExpExecArray => !!m && m[1] !== 'LLAMACPP_MODEL_DIR')
    .map((m) => ({ key: m[1]!, value: m[2]!.replace(/^"(.*)"$/, '$1') }))
}
