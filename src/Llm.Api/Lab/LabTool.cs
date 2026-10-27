using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using Llm.Api.Chat;
using Llm.Api.Chat.Tools;
using Llm.Core.Chat;
using Microsoft.Extensions.Options;

namespace Llm.Api.Lab;

/// <summary>
/// A tool pack of the science lab: functions run in the sandbox by lab.py with the call in a file beside it (the model's
/// words are read as mathematics, formulas or sequences there, never as the app's code). What a result says is the
/// tool's: a claim is "proved" only when SymPy shows it, a reaction "checked" only when its atoms (and charge) count the
/// same on both sides. What the page shows beside a result (a molecule's drawing) goes with the call, not to the model.
/// A new science is a new pack: its functions in lab.py and a class here.
/// </summary>
public abstract class LabPack(SandboxClient sandbox, IOptionsMonitor<SandboxOptions> options) : IChatTool
{
    /// <summary>The lab's tools: they run in a sandbox (the proofs in Lean's), as Python does (its permission), and are called directly, never from code.</summary>
    public static readonly IReadOnlyList<string> Ids = [LabTool.ToolId, ChemistryTool.ToolId, BiologyTool.ToolId, QuantumTool.ToolId, ProofTool.ToolId];

    private static readonly string Script = Read("lab.py");

    public abstract string Id { get; }
    public abstract string Title { get; }
    public abstract string Description { get; }
    public abstract string Icon { get; }

    /// <summary>The pack's functions, as the model is offered them.</summary>
    protected abstract JsonArray Functions();

    /// <summary>What the model is told of the pack.</summary>
    protected abstract string Instructions { get; }

    public Task<string?> UnavailableAsync(CancellationToken ct) => Task.FromResult(sandbox.Unavailable());

    /// <summary>Off in new chats: the lab's page turns it on, and anyone may in a chat.</summary>
    public ToolSetting Defaults() => new() { ToolId = Id, OnByDefault = false };

    public Task<IToolRun> StartAsync(ToolContext context, CancellationToken ct) => Task.FromResult<IToolRun>(new LocalRun(Functions(), Instructions, CallAsync));

    private async Task<ToolResult> CallAsync(string name, JsonObject args, CancellationToken ct)
    {
        var call = new JsonObject { ["op"] = name, ["args"] = args.DeepClone() };
        SandboxResult run;
        try
        {
            run = await sandbox.RunAsync(Script, [new SandboxFile("lab-call.json", Encoding.UTF8.GetBytes(call.ToJsonString()))], options.CurrentValue.TimeoutSeconds, ct);
        }
        catch (SandboxException ex)
        {
            return new ToolResult(ex.Message, IsError: true);
        }
        if (run.TimedOut)
        {
            return new ToolResult($"The lab did not finish within {options.CurrentValue.TimeoutSeconds} seconds: try a simpler form, or a numeric evaluate.", IsError: true);
        }
        var line = run.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        if (line is null || JsonNode.Parse(line) is not JsonObject answer)
        {
            var said = run.Error ?? run.Stderr.Trim();
            return new ToolResult("The lab gave no answer: " + (said.Length > 0 ? said[..Math.Min(said.Length, 500)] : "nothing printed"), IsError: true);
        }
        return answer["ok"]?.GetValue<bool>() == true
            ? new ToolResult(answer["result"]!.ToJsonString(Mcp.Plain)) { Details = answer["show"] is JsonObject show ? new JsonObject { ["lab"] = show.DeepClone() } : null }
            : new ToolResult(answer["error"]?.GetValue<string>() ?? "The lab could not do that.", IsError: true);
    }

    private static string Read(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name) ?? throw new InvalidOperationException($"{name} is not embedded.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

/// <summary>The science lab's mathematics (SymPy: compute, check a claim, solve and put the solutions back) and units (Pint).</summary>
public sealed class LabTool(SandboxClient sandbox, IOptionsMonitor<SandboxOptions> options) : LabPack(sandbox, options)
{
    public const string ToolId = "lab";

    public override string Id => ToolId;
    public override string Title => "Science lab";
    public override string Description => "Exact mathematics checked by SymPy (compute, check a claim, solve) and units by Pint: results shown and proved, not guessed.";
    public override string Icon => "flask-conical";

    protected override string Instructions =>
        "Science lab: work mathematics with these tools instead of in your head. Compute with math_compute, check each step and the answer with " +
        "math_check (or math_solve, which checks its solutions), and units with units_convert. Say a result is proved only when math_check says " +
        "proved; holds numerically is evidence, not proof. Show the mathematics in LaTeX ($...$ or $$...$$) as the tool gives it.";

    protected override JsonArray Functions() =>
    [
        Schema.Function("math_compute",
            "Computes exactly with SymPy: simplify, expand, factor, diff, integrate (with lower and upper for a definite integral), solve, limit, " +
            "series, evaluate (to digits), apart, together, and for a Matrix([[...]]) det, inverse, eigenvals. Write expressions as SymPy does: " +
            "x**2 + sin(x), sqrt(2), exp(-x**2), oo for infinity. Returns the result and its LaTeX.",
            new JsonObject
            {
                ["expression"] = Schema.Text("The expression, e.g. x**2*sin(x)"),
                ["operation"] = Schema.Text("simplify (default), expand, factor, diff, integrate, solve, limit, series, evaluate, apart, together, det, inverse or eigenvals"),
                ["variable"] = Schema.Text("The variable, when there is more than one"),
                ["order"] = new JsonObject { ["type"] = "integer", ["description"] = "diff: how many times; series: up to which power" },
                ["lower"] = Schema.Text("integrate: the lower limit (e.g. 0, -oo)"),
                ["upper"] = Schema.Text("integrate: the upper limit"),
                ["at"] = Schema.Text("limit or series: the point"),
                ["digits"] = new JsonObject { ["type"] = "integer", ["description"] = "evaluate: how many digits (15)" },
            }, "expression"),
        Schema.Function("math_check",
            "Checks that left equals right: proved when SymPy shows it exactly; holds numerically when it is only true at the points tried " +
            "(not a proof); refuted with a point where it fails. Use it before saying a step, an identity or an answer is right.",
            new JsonObject
            {
                ["left"] = Schema.Text("One side, e.g. sin(x)**2 + cos(x)**2"),
                ["right"] = Schema.Text("The other side, e.g. 1"),
            }, "left", "right"),
        Schema.Function("math_solve",
            "Solves equations exactly (a system too) and puts each solution back into every equation: checked says whether it holds.",
            new JsonObject
            {
                ["equations"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["description"] = "e.g. [\"x + y = 3\", \"x - y = 1\"]" },
                ["variables"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["description"] = "Which to solve for (all of them when left out)" },
            }, "equations"),
        Schema.Function("units_convert",
            "Converts a quantity with units with Pint, and says when the units do not match (a dimension check): e.g. 9.81 m/s^2 to ft/s^2. " +
            "Without to, in base SI units.",
            new JsonObject
            {
                ["quantity"] = Schema.Text("The quantity, e.g. 3 kWh or 72 km/h"),
                ["to"] = Schema.Text("The units to convert to, e.g. MJ or m/s"),
            }, "quantity"),
    ];
}

/// <summary>The science lab's chemistry: molecules from SMILES (RDKit), formulas and molar masses, reactions balanced and checked.</summary>
public sealed class ChemistryTool(SandboxClient sandbox, IOptionsMonitor<SandboxOptions> options) : LabPack(sandbox, options)
{
    public const string ToolId = "chemistry";

    public override string Id => ToolId;
    public override string Title => "Chemistry";
    public override string Description => "Molecules from SMILES with RDKit (formula, mass, properties, drawn), molar masses, reactions balanced and checked atom by atom.";
    public override string Icon => "atom";

    protected override string Instructions =>
        "Chemistry: use these tools instead of working from memory. Balance a reaction with chem_balance (it checks each element and the " +
        "charge on both sides), molar masses with chem_formula, a molecule's formula and properties with chem_molecule from its SMILES, and " +
        "compare two molecules with chem_similarity. When you give a molecule as SMILES, check its formula with chem_molecule.";

    protected override JsonArray Functions() =>
    [
        Schema.Function("chem_molecule",
            "A molecule from its SMILES, read by RDKit: canonical SMILES, formula, molar mass, exact mass, charge, rings, stereocentres, logP, " +
            "TPSA, H-bond donors and acceptors, rotatable bonds, Lipinski's rule of five, InChI and InChIKey; drawn for the person.",
            new JsonObject { ["smiles"] = Schema.Text("The SMILES, e.g. CC(=O)Oc1ccccc1C(=O)O for aspirin") }, "smiles"),
        Schema.Function("chem_formula",
            "A formula's molar mass and each element's share, from standard atomic weights: CuSO4·5H2O, Ca(OH)2, K4[Fe(CN)6], SO4^2-.",
            new JsonObject { ["formula"] = Schema.Text("The formula, e.g. Ca(OH)2 or CuSO4·5H2O; ions as Fe^2+ or SO4^2-") }, "formula"),
        Schema.Function("chem_balance",
            "Balances a chemical equation with the smallest whole coefficients and checks each element (and the charge, for ions) on both " +
            "sides: e.g. Fe + O2 -> Fe2O3, or MnO4^- + Fe^2+ + H^+ -> Mn^2+ + Fe^3+ + H2O.",
            new JsonObject { ["equation"] = Schema.Text("Reactants -> products, species separated by ' + '; ions with their charge as Fe^2+") }, "equation"),
        Schema.Function("chem_similarity",
            "How alike two molecules are: the Tanimoto similarity of their Morgan fingerprints, and whether they are the same molecule.",
            new JsonObject
            {
                ["first"] = Schema.Text("The first molecule's SMILES"),
                ["second"] = Schema.Text("The second molecule's SMILES"),
            }, "first", "second"),
    ];
}

/// <summary>The science lab's biology: DNA, RNA and protein sequences and their alignment (Biopython).</summary>
public sealed class BiologyTool(SandboxClient sandbox, IOptionsMonitor<SandboxOptions> options) : LabPack(sandbox, options)
{
    public const string ToolId = "biology";

    public override string Id => ToolId;
    public override string Title => "Biology";
    public override string Description => "DNA, RNA and protein sequences with Biopython: GC content, masses, pI, reverse complement, transcription, translation, alignment.";
    public override string Icon => "dna";

    protected override string Instructions =>
        "Biology: work sequences with these tools instead of by hand: bio_sequence for a sequence's properties, its reverse complement, " +
        "transcription and translation, bio_align to align two. Quote the tool's results.";

    protected override JsonArray Functions() =>
    [
        Schema.Function("bio_sequence",
            "A DNA, RNA or protein sequence (plain or FASTA) with Biopython. info: length, GC content and mass (DNA, RNA), or mass, " +
            "isoelectric point, aromaticity, instability index and GRAVY (protein); reverse_complement; transcribe (DNA to RNA); translate " +
            "(DNA or RNA to protein, a codon table by NCBI number).",
            new JsonObject
            {
                ["sequence"] = Schema.Text("The sequence, e.g. ATGGCCATTGTA"),
                ["operation"] = Schema.Text("info (default), reverse_complement, transcribe or translate"),
                ["kind"] = Schema.Text("dna, rna or protein (guessed from the letters when left out)"),
                ["table"] = new JsonObject { ["type"] = "integer", ["description"] = "translate: NCBI codon table (1, the standard one)" },
                ["to_stop"] = new JsonObject { ["type"] = "boolean", ["description"] = "translate: stop at the first stop codon" },
            }, "sequence"),
        Schema.Function("bio_align",
            "Aligns two sequences (pairwise, global or local): score, identity and the alignment. Proteins with BLOSUM62.",
            new JsonObject
            {
                ["first"] = Schema.Text("The first sequence"),
                ["second"] = Schema.Text("The second sequence"),
                ["mode"] = Schema.Text("global (default) or local"),
            }, "first", "second"),
    ];
}

/// <summary>
/// The science lab's quantum computing, on simulators (the sandbox has no network): circuits run, compiled and checked with
/// Qiskit and Aer, error correction sampled with Stim and decoded with PyMatching, a few qubits' open dynamics with QuTiP,
/// variational circuits with PennyLane, and molecules' energies with PySCF and their qubit Hamiltonians with OpenFermion.
/// </summary>
public sealed class QuantumTool(SandboxClient sandbox, IOptionsMonitor<SandboxOptions> options) : LabPack(sandbox, options)
{
    public const string ToolId = "quantum";

    public override string Id => ToolId;
    public override string Title => "Quantum";
    public override string Description => "Quantum circuits run and checked (Qiskit, Aer), error correction (Stim, PyMatching), open systems (QuTiP), VQE and QAOA (PennyLane), molecules (PySCF, OpenFermion). Simulators only.";
    public override string Icon => "orbit";

    protected override string Instructions =>
        "Quantum: compute with these tools instead of in your head; they are simulators, no quantum computer is reached. Write circuits in " +
        "OpenQASM 2 (qelib1's gates; the header may be left out). Bitstrings are Qiskit's: qubit 0 is the rightmost bit. Pauli strings are read " +
        "left to right: the first letter acts on qubit 0. Before you state a circuit's state, its probabilities or that two circuits are the " +
        "same, check it with quantum_check, and say it is checked only when it says equal. Quote energies with their units (hartree for " +
        "molecules). Cirq and the others are there for a program too (run_python).";

    protected override JsonArray Functions() =>
    [
        Schema.Function("quantum_run",
            "Runs an OpenQASM circuit on Aer's simulator: the counts over shots (measuring every qubit when it measures none), the exact " +
            "probabilities and, up to 6 qubits, the statevector; depth and gate counts. With noise, Aer's depolarizing and readout errors. " +
            "With basis, the circuit compiled to those gates (and to a line of qubits with coupling: line): its depth, gates and QASM. " +
            "The circuit and a histogram are drawn for the person.",
            new JsonObject
            {
                ["qasm"] = Schema.Text("The circuit, e.g. qreg q[2]; creg c[2]; h q[0]; cx q[0],q[1]; measure q -> c;"),
                ["shots"] = new JsonObject { ["type"] = "integer", ["description"] = "How many shots (1024; at most 100000)" },
                ["noise"] = new JsonObject
                {
                    ["type"] = "object",
                    ["description"] = "Errors: depolarizing (each one-qubit gate), two_qubit (each two-qubit gate; 10 times depolarizing when left out), readout (each measurement)",
                    ["properties"] = new JsonObject
                    {
                        ["depolarizing"] = new JsonObject { ["type"] = "number" },
                        ["two_qubit"] = new JsonObject { ["type"] = "number" },
                        ["readout"] = new JsonObject { ["type"] = "number" },
                    },
                },
                ["basis"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["description"] = "Compile to these gates, e.g. [\"rz\", \"sx\", \"x\", \"cx\"]" },
                ["coupling"] = Schema.Text("line: two-qubit gates only between neighbours (with basis)"),
                ["optimization"] = new JsonObject { ["type"] = "integer", ["description"] = "The compiler's optimisation level, 0 to 3 (2)" },
                ["seed"] = new JsonObject { ["type"] = "integer", ["description"] = "The simulator's seed (7), for the same counts again" },
            }, "qasm"),
        Schema.Function("quantum_check",
            "Checks a claim about a circuit (without its final measurements): with second, that the two circuits' unitaries are equal up to " +
            "a global phase (up to 10 qubits); with state, that its state is the one claimed (fidelity); with probabilities, that its outcome " +
            "probabilities are those claimed. Says equal or different.",
            new JsonObject
            {
                ["first"] = Schema.Text("The circuit, in OpenQASM"),
                ["second"] = Schema.Text("Another circuit, to compare unitaries"),
                ["state"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["description"] = "The claimed amplitudes, |0…0⟩ first, e.g. [\"1/sqrt(2)\", \"0\", \"0\", \"1/sqrt(2)\"]" },
                ["probabilities"] = new JsonObject { ["type"] = "object", ["description"] = "The claimed probabilities by bitstring, e.g. {\"00\": 0.5, \"11\": 0.5}" },
            }, "first"),
        Schema.Function("qec_sample",
            "Samples a quantum error-correcting code with Stim under circuit-level depolarizing noise and decodes it with PyMatching: the " +
            "logical error rate per shot for each distance and physical error rate (a larger distance lowers it below the threshold). Drawn " +
            "as a chart.",
            new JsonObject
            {
                ["code"] = Schema.Text("surface (rotated, Z memory; the default), surface_x or repetition"),
                ["distances"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "integer" }, ["description"] = "Code distances, e.g. [3, 5, 7]" },
                ["errors"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "number" }, ["description"] = "Physical error rates, e.g. [0.001, 0.005, 0.01]" },
                ["rounds"] = new JsonObject { ["type"] = "integer", ["description"] = "Rounds of syndrome measurement (the distance)" },
                ["shots"] = new JsonObject { ["type"] = "integer", ["description"] = "Shots per point (20000)" },
            }),
        Schema.Function("qubit_dynamics",
            "Evolves up to 4 qubits under a Hamiltonian with QuTiP, with T1 decay and T2 dephasing (a Lindblad master equation): each " +
            "qubit's Bloch vector at the end, the purity, qubit 0's ⟨X⟩, ⟨Y⟩, ⟨Z⟩ over time (drawn). ħ = 1.",
            new JsonObject
            {
                ["hamiltonian"] = Schema.Text("Pauli strings with coefficients, e.g. 0.5*X + 1.0*Z, or ZZ + 0.3*XI + 0.3*IX"),
                ["initial"] = Schema.Text("Each qubit's start: 0, 1, +, -, r (+i) or l (-i), e.g. 0+ (0 by default)"),
                ["t1"] = new JsonObject { ["type"] = "number", ["description"] = "T1, the decay time (none when left out)" },
                ["t2"] = new JsonObject { ["type"] = "number", ["description"] = "T2, the coherence time, at most 2 T1 (none when left out)" },
                ["time"] = new JsonObject { ["type"] = "number", ["description"] = "How long (10)" },
                ["steps"] = new JsonObject { ["type"] = "integer", ["description"] = "Time points (201)" },
            }, "hamiltonian"),
        Schema.Function("quantum_variational",
            "Variational circuits with PennyLane, their gradients by automatic differentiation, optimised with Adam. vqe: a Hamiltonian's " +
            "ground energy, against the exact one. qaoa: MaxCut on a graph, its best cut against the largest one (found by trying all). " +
            "The energy by step is drawn.",
            new JsonObject
            {
                ["problem"] = Schema.Text("vqe (default) or qaoa"),
                ["hamiltonian"] = Schema.Text("vqe: Pauli strings with coefficients, e.g. -1.05*II + 0.39*ZI - 0.39*IZ - 0.01*ZZ + 0.18*XX"),
                ["edges"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "integer" } }, ["description"] = "qaoa: the graph's edges, e.g. [[0, 1], [1, 2], [2, 0]]" },
                ["layers"] = new JsonObject { ["type"] = "integer", ["description"] = "Layers of the ansatz (2)" },
                ["steps"] = new JsonObject { ["type"] = "integer", ["description"] = "Optimisation steps (120)" },
                ["step_size"] = new JsonObject { ["type"] = "number", ["description"] = "Adam's step (0.1)" },
            }),
        Schema.Function("quantum_chemistry",
            "A molecule's energies with PySCF: Hartree–Fock, CCSD and CCSD(T), and FCI (exact in the basis) when small; its qubit " +
            "Hamiltonian (Jordan–Wigner, OpenFermion) with its qubits, terms and ground energy; with vqe, VQE's energy against the exact " +
            "one (up to 8 qubits). Energies in hartree.",
            new JsonObject
            {
                ["atoms"] = Schema.Text("Each atom and its x y z in ångström, separated by ;, e.g. H 0 0 0; H 0 0 0.74"),
                ["basis"] = Schema.Text("The basis set (sto-3g)"),
                ["charge"] = new JsonObject { ["type"] = "integer", ["description"] = "The charge (0)" },
                ["spin"] = new JsonObject { ["type"] = "integer", ["description"] = "2S, unpaired electrons (0)" },
                ["vqe"] = new JsonObject { ["type"] = "boolean", ["description"] = "Also run VQE on its qubit Hamiltonian" },
            }, "atoms"),
    ];
}
