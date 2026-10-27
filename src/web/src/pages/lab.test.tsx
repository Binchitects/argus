import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'
import { fakeApi, member, renderApp } from '@/test/utils'
import type { Conversation, Message } from './chat/types'

const fields = {
  available: true,
  why: null,
  fields: [
    { id: 'mathematics', title: 'Mathematics', about: 'Each step checked by SymPy.', tools: ['lab', 'python'] },
    { id: 'physics', title: 'Physics', about: 'Quantities with units.', tools: ['lab', 'python'] },
  ],
}

const message = (over: Pick<Message, 'id' | 'role'> & Partial<Omit<Message, 'id' | 'role'>>): Message => ({
  parentId: null, content: '', reasoning: null, toolName: null, toolCallId: null, toolCalls: null, attachments: [], status: 'complete', error: null, model: null,
  promptTokens: null, cachedTokens: null, completionTokens: null, thinkingMs: null, durationMs: null, createdAt: '', noAccess: false, ...over,
})

const notebook = (messages: Message[] = []): Conversation => ({
  id: 'n1', title: 'Gaussian integral', thinking: null, tools: ['lab', 'python'], useArgus: false, model: null, systemPrompt: null, temperature: null, topP: null, maxTokens: null,
  currentLeafId: messages.at(-1)?.id ?? null, archivedAt: null, forkedFrom: null, createdAt: '', updatedAt: '', messages, labField: 'mathematics',
})

const call = (id: string, name: string, args: object) => ({ id, type: 'function' as const, function: { name, arguments: JSON.stringify(args) } })

/** A worked notebook: a problem, a computation, a proved check, a refuted one and a solve with one solution that fails. */
const worked = [
  message({ id: 'u1', role: 'user', content: 'Integrate exp(-x^2) over the real line.' }),
  message({
    id: 'a1', parentId: 'u1', role: 'assistant', content: 'First the integral.',
    toolCalls: [call('t1', 'math_compute', { expression: 'exp(-x**2)', operation: 'integrate', lower: '-oo', upper: 'oo' }), call('t2', 'math_check', { left: 'sqrt(pi)**2', right: 'pi' })],
  }),
  message({ id: 'r1', parentId: 'a1', role: 'tool', toolName: 'math_compute', toolCallId: 't1', content: JSON.stringify({ result: 'sqrt(pi)', latex: '\\sqrt{\\pi}', operation: 'integrate', numeric: '1.77245385090552' }) }),
  message({ id: 'r2', parentId: 'r1', role: 'tool', toolName: 'math_check', toolCallId: 't2', content: JSON.stringify({ verdict: 'proved', detail: 'SymPy shows left − right simplifies to 0.', left: '\\pi', right: '\\pi' }) }),
  message({
    id: 'a2', parentId: 'r2', role: 'assistant', content: 'Now two more.',
    toolCalls: [call('t3', 'math_check', { left: 'x', right: 'x + 1' }), call('t4', 'math_solve', { equations: ['x**2 = 4'] })],
  }),
  message({ id: 'r3', parentId: 'a2', role: 'tool', toolName: 'math_check', toolCallId: 't3', content: JSON.stringify({ verdict: 'refuted', detail: 'Left − right is -1, not 0.', left: 'x', right: 'x + 1' }) }),
  message({
    id: 'r4', parentId: 'r3', role: 'tool', toolName: 'math_solve', toolCallId: 't4',
    content: JSON.stringify({ solutions: [{ solution: { x: '2' }, latex: 'x = 2', checked: true }, { solution: { x: '3' }, latex: 'x = 3', checked: false }], count: 2 }),
  }),
  message({ id: 'a3', parentId: 'r4', role: 'assistant', content: 'Result: $\\sqrt{\\pi}$, proved.' }),
]

describe('science lab', () => {
  it('starts a notebook of the chosen science and sends the problem once it opens', async () => {
    let sent = 0
    const calls = fakeApi(member, {
      'GET /api/lab/fields': () => ({ json: fields }),
      'GET /api/lab/notebooks': () => ({ json: [] }),
      'POST /api/chat/conversations': () => ({ status: 201, json: notebook() }),
      'GET /api/chat/conversations/n1': () => ({ json: notebook(sent > 0 ? worked.slice(0, 1) : []) }),
      'POST /api/chat/conversations/n1/messages': () => {
        sent++
        return { events: [{ type: 'question', id: 'u1', parentId: null }, { type: 'content', text: 'Working' }, { type: 'done', id: 'a1' }] }
      },
    })
    renderApp('/lab')
    expect(await screen.findByText('No notebooks yet')).toBeInTheDocument()
    await userEvent.click(screen.getByRole('radio', { name: /Physics/ }))
    await userEvent.type(screen.getByRole('textbox', { name: 'The problem' }), 'A ball falls 20 m.')
    await userEvent.click(screen.getByRole('button', { name: 'Work it' }))
    await waitFor(() => expect(calls.find((c) => c.method === 'POST' && c.path === '/api/chat/conversations')?.body).toEqual({ lab: 'physics' }))
    await waitFor(() => expect(calls.find((c) => c.path === '/api/chat/conversations/n1/messages')?.body).toEqual({ content: 'A ball falls 20 m.', attachments: [] }))
    expect(await screen.findByRole('region', { name: 'The problem' })).toBeInTheDocument()
    await waitFor(() => expect(screen.getByRole('list', { name: 'Notebook' })).toHaveAttribute('aria-busy', 'false'))
    expect(sent).toBe(1)
  })

  it('lists the notebooks with what was checked in each', async () => {
    fakeApi(member, {
      'GET /api/lab/fields': () => ({ json: fields }),
      'GET /api/lab/notebooks': () => ({ json: [{ id: 'n1', title: 'Gaussian integral', field: 'mathematics', updatedAt: '2026-10-27T10:00:00Z', checks: { proved: 2, refuted: 1, numeric: 0 } }] }),
    })
    renderApp('/lab')
    const row = within(await screen.findByRole('list', { name: 'Notebooks' })).getByRole('link', { name: /Gaussian integral/ })
    expect(row).toHaveAttribute('href', '/lab/n1')
    expect(within(row).getByText('Mathematics')).toBeInTheDocument()
    expect(within(row).getByText(/2 proved/)).toBeInTheDocument()
    expect(within(row).getByText(/1 refuted/)).toBeInTheDocument()
    expect(within(row).queryByText(/only numerically/)).not.toBeInTheDocument()
  })

  it('says why there is no lab and offers no problem to work', async () => {
    fakeApi(member, {
      'GET /api/lab/fields': () => ({ json: { available: false, why: 'The lab needs the sandbox running.', fields: fields.fields } }),
      'GET /api/lab/notebooks': () => ({ json: [] }),
    })
    renderApp('/lab')
    expect(await screen.findByText('The lab needs the sandbox running.')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Work it' })).not.toBeInTheDocument()
  })

  it('shows each step of a notebook as a cell with its verdict, the checks on top, and asks more', async () => {
    const calls = fakeApi(member, {
      'GET /api/lab/fields': () => ({ json: fields }),
      'GET /api/lab/notebooks': () => ({ json: [] }),
      'GET /api/chat/conversations/n1': () => ({ json: notebook(worked) }),
      'POST /api/chat/conversations/n1/messages': () => ({ events: [{ type: 'question', id: 'u2', parentId: 'a3' }, { type: 'done', id: 'a4' }] }),
    })
    renderApp('/lab/n1')
    const cells = await screen.findByRole('list', { name: 'Notebook' })
    expect(within(cells).getByRole('region', { name: 'The problem' })).toHaveTextContent('Integrate exp(-x^2)')
    expect(within(cells).getByRole('region', { name: 'Compute: integrate' })).toHaveTextContent('≈ 1.77245385090552')
    const checks = within(cells).getAllByRole('region', { name: 'Check' })
    expect(checks.map((c) => within(c).getByText(/^(Proved|Refuted)$/).textContent)).toEqual(['Proved', 'Refuted'])
    expect(checks[1]).toHaveTextContent('Left − right is -1, not 0.')
    // One of its two solutions does not hold: the solve is not proved.
    const solve = within(cells).getByRole('region', { name: 'Solve' })
    expect(within(solve).getByText('Refuted')).toBeInTheDocument()
    expect(solve).toHaveTextContent('✓')
    expect(solve).toHaveTextContent('✗')
    // On top: one check proved, two refuted (the check and the solve).
    expect(screen.getByText(/1 proved/)).toBeInTheDocument()
    expect(screen.getByText(/2 refuted/)).toBeInTheDocument()
    expect(screen.getByRole('link', { name: /Open as a chat/ })).toHaveAttribute('href', '/chat/n1')

    await userEvent.type(screen.getByRole('textbox', { name: 'Ask more' }), 'Why does the check fail?')
    await userEvent.click(screen.getByRole('button', { name: 'Ask' }))
    await waitFor(() => expect(calls.find((c) => c.method === 'POST')?.body).toEqual({ content: 'Why does the check fail?', attachments: [] }))
  })

  it('shows a molecule drawn, a balanced reaction checked atom by atom, and a translated sequence', async () => {
    const steps = [
      message({ id: 'u1', role: 'user', content: 'Aspirin, rust and a gene.' }),
      message({
        id: 'a1', parentId: 'u1', role: 'assistant', content: '',
        toolCalls: [call('t1', 'chem_molecule', { smiles: 'CC(=O)Oc1ccccc1C(=O)O' }), call('t2', 'chem_balance', { equation: 'Fe + O2 -> Fe2O3' }), call('t3', 'bio_sequence', { sequence: 'ATGGCC', operation: 'translate' })],
      }),
      message({
        id: 'r1', parentId: 'a1', role: 'tool', toolName: 'chem_molecule', toolCallId: 't1',
        content: JSON.stringify({ smiles: 'CC(=O)Oc1ccccc1C(=O)O', formula: 'C9H8O4', molar_mass: '180.159 g/mol', logP: 1.31, tpsa: 63.6, h_donors: 1, h_acceptors: 3, rotatable_bonds: 2, rings: 1, lipinski: 'passes the rule of five', inchikey: 'BSYNRYMUTXBXSQ-UHFFFAOYSA-N' }),
        details: { lab: { figures: [{ svg: '<svg xmlns="http://www.w3.org/2000/svg"></svg>', label: 'C9H8O4, drawn' }] } },
      }),
      message({
        id: 'r2', parentId: 'r1', role: 'tool', toolName: 'chem_balance', toolCallId: 't2',
        content: JSON.stringify({ balanced: '4 Fe + 3 O2 -> 2 Fe2O3', atoms: [{ element: 'Fe', left: 4, right: 4 }, { element: 'O', left: 6, right: 6 }], checked: true, detail: 'Each element counted on both sides: equal.' }),
      }),
      message({ id: 'r3', parentId: 'r2', role: 'tool', toolName: 'bio_sequence', toolCallId: 't3', content: JSON.stringify({ kind: 'dna', length: 6, operation: 'translate', result: 'MA', table: 1 }) }),
    ]
    fakeApi(member, {
      'GET /api/lab/fields': () => ({ json: fields }),
      'GET /api/lab/notebooks': () => ({ json: [] }),
      'GET /api/chat/conversations/n1': () => ({ json: { ...notebook(steps), labField: 'chemistry' } }),
    })
    renderApp('/lab/n1')
    const molecule = await screen.findByRole('region', { name: 'Molecule' })
    expect(within(molecule).getByRole('img', { name: 'C9H8O4, drawn' })).toHaveAttribute('src', expect.stringMatching(/^data:image\/svg\+xml;/))
    expect(molecule).toHaveTextContent('180.159 g/mol')
    expect(molecule).toHaveTextContent('passes the rule of five')
    const reaction = screen.getByRole('region', { name: 'Reaction' })
    expect(within(reaction).getByText('Balanced, checked')).toBeInTheDocument()
    expect(reaction).toHaveTextContent('4 Fe + 3 O2 → 2 Fe2O3')
    expect(within(reaction).getAllByRole('row')).toHaveLength(3)
    expect(screen.getByRole('region', { name: 'Sequence: translate' })).toHaveTextContent('MA')
    // The balanced reaction counts as checked.
    expect(screen.getByText(/1 proved/)).toBeInTheDocument()
  })

  it('shows a circuit drawn with its histogram, a claimed state checked, and a code sampled', async () => {
    const bell = 'qreg q[2]; creg c[2]; h q[0]; cx q[0],q[1]; measure q -> c;'
    const steps = [
      message({ id: 'u1', role: 'user', content: 'A Bell pair, and the surface code.' }),
      message({
        id: 'a1', parentId: 'u1', role: 'assistant', content: '',
        toolCalls: [call('t1', 'quantum_run', { qasm: bell }), call('t2', 'quantum_check', { first: bell, state: ['1/sqrt(2)', '0', '0', '1/sqrt(2)'] }), call('t3', 'qec_sample', { distances: [3, 5], errors: [0.001] })],
      }),
      message({
        id: 'r1', parentId: 'a1', role: 'tool', toolName: 'quantum_run', toolCallId: 't1',
        content: JSON.stringify({ qubits: 2, depth: 3, shots: 1024, noise: 'none (ideal)', statevector: '0.7071|00⟩ + 0.7071|11⟩', counts: { '00': 530, '11': 494 } }),
        details: { lab: { diagram: 'q_0: ┤ H ├──■──', figures: [{ svg: '<svg xmlns="http://www.w3.org/2000/svg"></svg>', label: '1024 shots' }] } },
      }),
      message({
        id: 'r2', parentId: 'r1', role: 'tool', toolName: 'quantum_check', toolCallId: 't2',
        content: JSON.stringify({ checked: true, verdict: 'equal', fidelity: 1, state: '0.7071|00⟩ + 0.7071|11⟩', detail: "The circuit's state is the one claimed (up to a global phase)." }),
      }),
      message({
        id: 'r3', parentId: 'r2', role: 'tool', toolName: 'qec_sample', toolCallId: 't3',
        content: JSON.stringify({ code: 'surface', results: [{ distance: 3, physical: 0.001, logical: 0.0004, plus_minus: 0.0001, shots: 20000 }, { distance: 5, physical: 0.001, logical: 0.00005, plus_minus: 0.00005, shots: 20000 }], detail: 'p = 0.001: a larger distance lowers the logical error rate' }),
      }),
    ]
    fakeApi(member, {
      'GET /api/lab/fields': () => ({ json: fields }),
      'GET /api/lab/notebooks': () => ({ json: [] }),
      'GET /api/chat/conversations/n1': () => ({ json: { ...notebook(steps), labField: 'quantum' } }),
    })
    renderApp('/lab/n1')
    const circuit = await screen.findByRole('region', { name: 'Circuit' })
    expect(within(circuit).getByLabelText('The circuit, drawn')).toHaveTextContent('q_0: ┤ H ├──■──')
    expect(within(circuit).getByRole('img', { name: '1024 shots' })).toHaveAttribute('src', expect.stringMatching(/^data:image\/svg\+xml;/))
    expect(circuit).toHaveTextContent('0.7071|00⟩ + 0.7071|11⟩')
    expect(within(circuit).getAllByRole('row')).toHaveLength(2)
    expect(within(screen.getByRole('region', { name: 'Quantum check' })).getByText('Checked: equal')).toBeInTheDocument()
    const code = screen.getByRole('region', { name: 'Error correction: surface code' })
    expect(within(code).getAllByRole('row')).toHaveLength(3)
    expect(code).toHaveTextContent('a larger distance lowers')
    expect(screen.getByText(/1 proved/)).toBeInTheDocument()
  })

  it("shows a proof Lean checked, one it rejected with its messages, and one that leans on sorry", async () => {
    const source = (body: string) => `import Mathlib\n\ntheorem t (a b : ℝ) : a + b = b + a := by ${body}`
    const steps = [
      message({ id: 'u1', role: 'user', content: 'Prove that addition commutes.' }),
      message({
        id: 'a1', parentId: 'u1', role: 'assistant', content: '',
        toolCalls: [call('t1', 'lean_check', { source: source('ring') }), call('t2', 'lean_check', { source: source('simp') }), call('t3', 'lean_check', { source: source('sorry') })],
      }),
      message({ id: 'r1', parentId: 'a1', role: 'tool', toolName: 'lean_check', toolCallId: 't1', content: JSON.stringify({ verdict: 'checked', detail: 'Lean accepts the file: every theorem in it is proved.', messages: [] }) }),
      message({
        id: 'r2', parentId: 'r1', role: 'tool', toolName: 'lean_check', toolCallId: 't2',
        content: JSON.stringify({ verdict: 'rejected', detail: 'Lean rejects it: 1 error.', messages: [{ line: 3, column: 52, severity: 'error', text: 'simp made no progress' }] }),
      }),
      message({ id: 'r3', parentId: 'r2', role: 'tool', toolName: 'lean_check', toolCallId: 't3', content: JSON.stringify({ verdict: 'incomplete', detail: 'Lean accepts the file, but a proof uses sorry: not proved.', messages: [] }) }),
    ]
    fakeApi(member, {
      'GET /api/lab/fields': () => ({ json: fields }),
      'GET /api/lab/notebooks': () => ({ json: [] }),
      'GET /api/chat/conversations/n1': () => ({ json: notebook(steps) }),
    })
    renderApp('/lab/n1')
    await screen.findByRole('list', { name: 'Notebook' })
    const proofs = screen.getAllByRole('region', { name: 'Proof' })
    expect(proofs.map((p) => within(p).getByText(/Lean|sorry/, { selector: 'span' }).textContent)).toEqual(['Checked by Lean', 'Rejected by Lean', 'Uses sorry: not proved'])
    expect(proofs[1]).toHaveTextContent('line 3, column 52: error: simp made no progress')
    expect(proofs[0]).toHaveTextContent('theorem t (a b : ℝ)')
    // Sorry proves nothing: one proved, one refuted.
    expect(screen.getByText(/1 proved/)).toBeInTheDocument()
    expect(screen.getByText(/1 refuted/)).toBeInTheDocument()
  })

  it('says when the lab asked for a check of an answer no tool had checked', async () => {
    const steps = [
      message({ id: 'u1', role: 'user', content: 'What is 2 + 2?' }),
      message({ id: 'a1', parentId: 'u1', role: 'assistant', content: 'It is 4.' }),
      message({ id: 'a2', parentId: 'a1', role: 'assistant', content: '', toolCalls: [call('t1', 'math_check', { left: '2 + 2', right: '4' })] }),
      message({ id: 'r1', parentId: 'a2', role: 'tool', toolName: 'math_check', toolCallId: 't1', content: JSON.stringify({ verdict: 'proved', detail: 'SymPy shows left − right simplifies to 0.' }) }),
      message({ id: 'a3', parentId: 'r1', role: 'assistant', content: 'Checked: 4.' }),
    ]
    fakeApi(member, {
      'GET /api/lab/fields': () => ({ json: fields }),
      'GET /api/lab/notebooks': () => ({ json: [] }),
      'GET /api/chat/conversations/n1': () => ({ json: notebook(steps) }),
    })
    renderApp('/lab/n1')
    expect(await screen.findByText('No tool had checked that answer: the lab asked for a check.')).toBeInTheDocument()
    expect(screen.getAllByText(/the lab asked for a check/)).toHaveLength(1)
  })
})
