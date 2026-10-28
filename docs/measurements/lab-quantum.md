# The science lab's quantum computing, measured

Forty problems of Qiskit HumanEval (qiskit-community/qiskit-human-eval,
Apache-2.0: Qiskit problems written by Qiskit's advocates, each a function to
write, with its canonical solution and its tests), asked through Arena's own
chat (2026-10-28, v7.4), each in a new chat:

- **plain**: the model with no tools;
- **lab**: a lab notebook in the Quantum computing field: the field's
  instructions, quantum_run, quantum_check, qec_sample, qubit_dynamics,
  quantum_variational, quantum_chemistry, SymPy's math tools, Python, and the
  lab's check (an answer no tool checked is sent back once to be checked with
  a tool).

Produced by `evals/run_lab_quantum.py` (results in
`evals/lab-quantum-results.json`) as a throwaway person, removed after. Each
answer is graded by the set's own tests, run in the sandbox's image (Python
3.13.15, Qiskit 2.6.1, Aer 0.18.2, no network), each in its own process with a
120 s limit.

Only the problems whose canonical solution passes its own tests there are
asked: 97 of the 151. The other 54 are left out: 51 need qiskit-ibm-runtime
(its fake devices), and one each needs pylatexenc, qiskit_qasm3_import and
qiskit_ibm_transpiler, none of which the sandbox has. The forty are drawn
across the difficulties with seed 7, never the first forty: 20 basic, 19
intermediate and the 1 difficult problem the draw reached.

## The numbers

| model | arm | passed | basic | intermediate | difficult | seconds each | tokens written | tool calls | problems with a call |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|
| Qwen3-4B (Q4_K_M, thinking) | plain | 12 / 40 | 8 / 20 | 4 / 19 | 0 / 1 | 136 | 169,213 | 0 | 0 |
| Qwen3-4B (Q4_K_M, thinking) | lab | 10 / 40 | 7 / 20 | 3 / 19 | 0 / 1 | 615 | 599,502 | 77 | 35 |

Seven problems passed in both arms. The lab passed three that plain missed
(13, 0 and 130) and missed five that plain passed (26, 105, 106, 107 and 139).

## What it says

- **The model writes the Qiskit it learned, which is not the Qiskit that
  runs.** Of plain's 28 misses, 18 fail on the API itself. Eight call what
  Qiskit 1.0 removed (`cnot`, `u3`, `QuantumCircuit.qasm()`,
  `Pauli.from_label`, `from qiskit import Aer`, `qiskit.providers.aer`); ten
  call a name or a signature Qiskit does not have (`to_matrix` on a circuit,
  `adjoint`, `tableau`, `MultiControlledY`, `random_circuit(n_qubits=...)`).
  The other ten are wrong answers (a depth, an operator, a circuit the tests
  do not expect). It is the 4B model's training, not the problems: every one
  of the forty has a solution that passes.
- **The lab does not fix it, as it is built.** The lab's tools run circuits
  and check states, probabilities and equivalences; nothing in it runs the
  function the answer gives, and that function is what the tests grade. The
  model called a tool in 35 of the 40 problems and still answered with APIs
  that do not run (105 with `cnot`, 65 with `cu1`, 127 with `measure(q)`, 25
  importing `transpile` from `qiskit.transpiler`, each after one call). The
  three it won are slips of the same kind (`u3`, `adjoint`, `measure` without
  its classical bit) that it got right in the lab, with one or two calls each.
- **It costs time and context.** The lab took 4.5 times as long (615 s a
  problem against 136 s) and wrote 3.5 times as much. Six of its problems ran
  to the model's 32,768-token context (32,000 to 36,000 tokens written, about
  1,800 s each) with at most one call, five of them with none: three of
  plain's passes are among them (26, 106, 107). Three answers in the lab gave
  no Python block and four gave one that does not parse.

## What follows

The lab's quantum field is for computing and checking results (states,
amplitudes, energies), and the [mathematics measurement](lab-math.md) showed that it checks
there. For an answer that is code, the step that is missing is the one the
plain arm shows would matter most: run the function given, once, in the
sandbox's Qiskit, before answering, and give the error back. Most of the
misses above fail at import or at the first call. That step is in
[plan.md](../plan.md) as a leftover, to measure with the same forty.

## Caveats

One run of each arm on one machine, the model at 4-bit on llama.cpp, forty
problems: a difference of two problems is within what another sample would
change. The set targets the Qiskit of its time with qiskit-ibm-runtime's fake
devices: the 51 problems that need them are not measured here, and installing
qiskit-ibm-runtime in the sandbox would make them usable. The bigger models
(Qwen3.8-27B) were not run in this round.
